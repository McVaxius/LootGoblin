using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using LootGoblin.Models;

namespace LootGoblin.Services;

public class StateManager : IDisposable
{
    private readonly Plugin _plugin;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    public BotState State { get; private set; } = BotState.Idle;
    public string StateDetail { get; private set; } = "";
    public bool IsPaused { get; private set; }
    public int RetryCount { get; private set; }
    public uint SelectedMapItemId { get; private set; }
    public MapLocation? CurrentLocation { get; private set; }

    private DateTime stateStartTime = DateTime.Now;
    private DateTime lastTickTime = DateTime.MinValue;
    private DateTime lastMapScanTime = DateTime.MinValue;
    private bool stateActionIssued;
    private Vector3 lastStuckCheckPos; // Position at last stuck check
    private DateTime lastStuckCheckTime = DateTime.MinValue; // Time of last stuck check
    private DateTime portalRetryStart = DateTime.MinValue; // Portal interaction retry timer
    private DateTime dismountAttemptStart = DateTime.MinValue; // When dismount first attempted at flag X,Z
    private bool descentInProgress; // Prevents overlapping async Ctrl+Space descent calls
    private DateTime lastInteractionTime = DateTime.MinValue; // Throttle chest/portal interaction attempts
    private bool autoMoveActive; // Track if automove is currently on
    private const double TickIntervalSeconds = 0.5;
    private readonly MountService _mountService;

    private static readonly Dictionary<BotState, double> StateTimeouts = new()
    {
        { BotState.OpeningMap,        30  },
        { BotState.DetectingLocation, 30  },
        { BotState.Teleporting,       90  },
        { BotState.Mounting,          30  },
        { BotState.WaitingForParty,   120 },
        { BotState.Flying,            300 },
        { BotState.OpeningChest,      120 },
    };

    public StateManager(Plugin plugin, IFramework framework, IPluginLog log)
    {
        _plugin = plugin;
        _framework = framework;
        _log = log;
        _mountService = new MountService(plugin);
        _framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_plugin.Configuration.Enabled) return;
        if (IsPaused) return;
        if (State == BotState.Idle || State == BotState.Error) return;

        var now = DateTime.Now;
        if ((now - lastTickTime).TotalSeconds < TickIntervalSeconds) return;
        lastTickTime = now;

        if (!Plugin.ClientState.IsLoggedIn)
        {
            TransitionTo(BotState.Error, "Lost connection - not logged in.");
            return;
        }

        CheckStateTimeout();
        Tick();
    }

    private void CheckStateTimeout()
    {
        if (!StateTimeouts.TryGetValue(State, out var timeout)) return;
        var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
        if (elapsed > timeout)
        {
            _plugin.AddDebugLog($"[TIMEOUT] State {State} timed out after {elapsed:F0}s (limit: {timeout}s)");
            HandleError($"Timeout in state {State} after {timeout}s.");
        }
    }

    private void Tick()
    {
        switch (State)
        {
            case BotState.SelectingMap:     TickSelectingMap();     break;
            case BotState.OpeningMap:       TickOpeningMap();       break;
            case BotState.DetectingLocation: TickDetectingLocation(); break;
            case BotState.Teleporting:      TickTeleporting();      break;
            case BotState.Mounting:         TickMounting();         break;
            case BotState.WaitingForParty:  TickWaitingForParty();  break;
            case BotState.Flying:           TickFlying();           break;
            case BotState.OpeningChest:     TickOpeningChest();     break;
            case BotState.InCombat:         TickInCombat();         break;
            case BotState.InDungeon:        TickInDungeon();        break;
            case BotState.Completed:        TickCompleted();        break;
        }
    }

    public void Start()
    {
        if (State != BotState.Idle && State != BotState.Error)
        {
            _plugin.AddDebugLog("Bot already running.");
            return;
        }

        RetryCount = 0;
        CurrentLocation = null;
        SelectedMapItemId = 0;
        _plugin.YesAlreadyIPC.Pause();
        _plugin.AddDebugLog($"[Start] YesAlready paused: {_plugin.YesAlreadyIPC.IsPaused}");
        TransitionTo(BotState.SelectingMap, "Starting map run...");
    }

    public void Stop()
    {
        _plugin.NavigationService.StopNavigation();
        IsPaused = false;
        RetryCount = 0;
        portalRetryStart = DateTime.MinValue;
        TransitionTo(BotState.Idle, "Stopped by user.");
    }

    public void ResetAll()
    {
        _plugin.NavigationService.StopNavigation();
        IsPaused = false;
        RetryCount = 0;
        CurrentLocation = null;
        SelectedMapItemId = 0;
        portalRetryStart = DateTime.MinValue;
        KrangleService.ClearCache();
        TransitionTo(BotState.Idle, "Full reset by user.");
        _plugin.AddDebugLog("All plugin states reset.");
    }

    public void Pause()
    {
        if (State == BotState.Idle || State == BotState.Error) return;
        IsPaused = true;
        _plugin.NavigationService.StopNavigation();
        _plugin.AddDebugLog("Bot paused.");
    }

    public void Resume()
    {
        if (!IsPaused) return;
        IsPaused = false;
        stateActionIssued = false;
        _plugin.AddDebugLog("Bot resumed.");
    }

    // ─── State Ticks ─────────────────────────────────────────────────────────

    private void TickSelectingMap()
    {
        // Only scan every 3 seconds to reduce log spam
        if ((DateTime.Now - lastMapScanTime).TotalSeconds < 3)
        {
            return;
        }
        lastMapScanTime = DateTime.Now;

        var maps = _plugin.InventoryService.ScanForMaps();
        _plugin.AddDebugLog($"[TICK] Scanning inventory... Found {maps.Count} different map types");
        
        if (maps.Count == 0)
        {
            HandleError("No maps found in inventory.");
            return;
        }

        var enabled = _plugin.Configuration.EnabledMapTypes;

        // Filter to only enabled map types; if none configured, allow all
        var candidates = enabled.Count > 0
            ? maps.Where(kvp => enabled.Contains(kvp.Key) && kvp.Value > 0).Select(kvp => kvp.Key).ToList()
            : maps.Where(kvp => kvp.Value > 0).Select(kvp => kvp.Key).ToList();

        if (candidates.Count == 0)
        {
            HandleError("No enabled maps in inventory. Check map selection in UI.");
            return;
        }

        // Don't sort - use inventory order to match menu order
        SelectedMapItemId = candidates[0];
        var mapName = TreasureMapData.KnownMaps.TryGetValue(SelectedMapItemId, out var info) ? info.Name : $"ID {SelectedMapItemId}";
        _plugin.AddDebugLog($"Selected: {mapName} (ID {SelectedMapItemId}).");
        TransitionTo(BotState.OpeningMap, $"Opening {mapName}...");
    }

    private void TickOpeningMap()
    {
        if (!stateActionIssued)
        {
            if (!GameHelpers.IsPlayerAvailable())
            {
                StateDetail = "Waiting for player to be available...";
                return;
            }

            // Use GameHelpers.UseItem - now properly uses InventoryManager.UseItem API
            var result = GameHelpers.UseItem(SelectedMapItemId, _plugin.InventoryService);
            if (result)
            {
                _plugin.AddDebugLog($"Map decipher triggered for ID {SelectedMapItemId}.");
                stateActionIssued = true;
            }
            else
            {
                _plugin.AddDebugLog($"UseItem({SelectedMapItemId}) failed, retrying...");
            }
            return;
        }

        // Safety net: click Yes on any decipher confirmation dialog that might be stuck
        GameHelpers.ClickYesIfVisible();

        // After /item command, wait for the decipher dialog + flag to set
        // Transition to detection after a short delay to allow the game to process
        if ((DateTime.Now - stateStartTime).TotalSeconds > 4)
            TransitionTo(BotState.DetectingLocation, "Map opened, reading location...");
    }

    private void TickDetectingLocation()
    {
        // Try to read the map flag from AgentMap (set when map is deciphered)
        var location = _plugin.GlobeTrotterIPC.TryGetMapLocation();
        if (location != null)
        {
            // Find nearest aetheryte to navigate from
            var aetheryteId = _plugin.NavigationService.FindNearestAetheryte(location.TerritoryId);
            location.NearestAetheryteId = aetheryteId;

            SetLocation(location);

            if (Plugin.ClientState.TerritoryType == location.TerritoryId)
            {
                // Already in the right zone - skip teleport
                TransitionTo(BotState.Mounting, "Already in zone! Mounting up...");
            }
            else
            {
                TransitionTo(BotState.Teleporting, $"Teleporting to {location.ZoneName}...");
            }
            return;
        }

        // Not found yet - keep polling (timeout handled by StateTimeouts)
        var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
        StateDetail = $"Waiting for map flag... ({elapsed:F0}s / {StateTimeouts[BotState.DetectingLocation]}s)";
    }

    private void TickTeleporting()
    {
        var nav = _plugin.NavigationService;

        if (!stateActionIssued)
        {
            if (CurrentLocation == null || CurrentLocation.NearestAetheryteId == 0)
            {
                HandleError("No aetheryte ID for teleport.");
                return;
            }
            nav.TeleportToAetheryte(CurrentLocation.NearestAetheryteId);
            stateActionIssued = true;
            return;
        }

        // Check if teleport finished (no longer between areas and in correct territory)
        if (!nav.IsTeleporting())
        {
            var currentTerritory = Plugin.ClientState.TerritoryType;
            if (CurrentLocation != null && currentTerritory == CurrentLocation.TerritoryId)
            {
                TransitionTo(BotState.Mounting, "Arrived! Mounting up...");
            }
            else if ((DateTime.Now - stateStartTime).TotalSeconds > 10)
            {
                HandleError($"Wrong territory after teleport: {currentTerritory} (expected {CurrentLocation?.TerritoryId}).");
            }
        }
    }

    private void TickMounting()
    {
        var nav = _plugin.NavigationService;

        if (nav.IsMounted())
        {
            var partySize = Plugin.PartyList.Length;
            if (partySize > 0 && _plugin.Configuration.WaitForParty)
                TransitionTo(BotState.WaitingForParty, "Waiting for party to mount...");
            else
                TransitionTo(BotState.Flying, "Mounted! Flying to location...");
            return;
        }

        if (!stateActionIssued)
        {
            nav.MountUp();
            stateActionIssued = true;
        }
    }

    private void TickWaitingForParty()
    {
        _plugin.PartyService.UpdatePartyStatus();

        if (_plugin.PartyService.AllMembersMounted)
        {
            TransitionTo(BotState.Flying, "All party members mounted! Flying...");
            return;
        }

        var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
        var timeout = _plugin.Configuration.PartyWaitTimeout;
        var remaining = timeout - (int)elapsed;

        if ((int)elapsed % 10 == 0 && (int)elapsed > 0)
        {
            var mounted = _plugin.PartyService.PartyMembers.FindAll(m => m.IsMounted).Count;
            var total = _plugin.PartyService.PartyMembers.Count;
            StateDetail = $"Waiting for party ({mounted}/{total} mounted) - {remaining}s left...";
        }
    }

    private void TickFlying()
    {
        if (CurrentLocation == null)
        {
            HandleError("No location data for navigation.");
            return;
        }

        var nav = _plugin.NavigationService;

        if (!stateActionIssued)
        {
            var target = new Vector3(CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z);
            nav.FlyToPosition(target);
            stateActionIssued = true;
            lastStuckCheckPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            lastStuckCheckTime = DateTime.Now;
            return;
        }

        if (nav.State == NavigationState.Error)
        {
            HandleError($"Navigation error: {nav.StateDetail}");
            return;
        }

        var currentPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var targetPos = new Vector3(CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z);
        var distanceFromTarget = Vector3.Distance(currentPos, targetPos);
        
        // Stuck detection: only re-pathfind if stuck (10+ seconds without moving 5+ yalms)
        var sinceStuckCheck = (DateTime.Now - lastStuckCheckTime).TotalSeconds;
        if (sinceStuckCheck >= 10.0 && distanceFromTarget > 5.0f)
        {
            var movedDistance = Vector3.Distance(currentPos, lastStuckCheckPos);
            if (movedDistance < 5.0f)
            {
                // Stuck! Re-pathfind
                nav.FlyToPosition(targetPos);
                _plugin.AddDebugLog($"[Flying] Stuck detected (moved {movedDistance:F1}y in 10s) - re-pathfinding (distance: {distanceFromTarget:F1}y)");
            }
            lastStuckCheckPos = currentPos;
            lastStuckCheckTime = DateTime.Now;
        }

        // Check if we're close enough to X,Z coordinates (within 5 yalms)
        var xzDist = Math.Sqrt(Math.Pow(currentPos.X - targetPos.X, 2) + Math.Pow(currentPos.Z - targetPos.Z, 2));
        
        if (nav.State == NavigationState.Arrived || nav.State == NavigationState.Idle || xzDist < 5.0f)
        {
            // We've arrived at the flag X,Z — now we need to dismount
            if (_plugin.NavigationService.IsMounted())
            {
                // Record when we first started trying to dismount at this location
                if (dismountAttemptStart == DateTime.MinValue)
                {
                    dismountAttemptStart = DateTime.Now;
                    _plugin.AddDebugLog("Close to target - attempting to land/dismount...");
                }

                var dismountElapsed = (DateTime.Now - dismountAttemptStart).TotalSeconds;

                // Normal dismount: try /mount toggle (ForceLand) for up to 60 seconds
                if (dismountElapsed < 60.0)
                {
                    // Attempt dismount every 2 seconds (ForceLand spams /mount internally)
                    if (!descentInProgress && (int)dismountElapsed % 2 == 0)
                    {
                        _mountService.Dismount();
                    }
                    StateDetail = $"Landing/dismounting... ({dismountElapsed:F0}s)";
                    return;
                }

                // 60+ seconds and still mounted = likely underwater, need Ctrl+Space descent
                if (!descentInProgress)
                {
                    descentInProgress = true;
                    _plugin.AddDebugLog($"[Flying] Dismount failed after {dismountElapsed:F0}s - attempting Ctrl+Space descent (underwater?)");
                    
                    System.Threading.Tasks.Task.Run(async () => {
                        // Ctrl+Space hold for 1 second, release, wait - SND pattern
                        await GameHelpers.PerformDescentAsync();
                        
                        // Wait a moment after releasing
                        await System.Threading.Tasks.Task.Delay(1000);
                        
                        // Check if we dismounted
                        if (!_plugin.NavigationService.IsMounted())
                        {
                            _plugin.AddDebugLog("[Flying] Ctrl+Space descent succeeded - dismounted!");
                        }
                        else
                        {
                            _plugin.AddDebugLog("[Flying] Still mounted after descent attempt - will retry next tick");
                        }
                        
                        descentInProgress = false;
                    });
                }
                
                StateDetail = $"Underwater descent in progress... ({dismountElapsed:F0}s)";
                return;
            }
            
            // Successfully dismounted — proceed with map content
            _plugin.AddDebugLog("Successfully dismounted - proceeding with map content");
            
            CommandHelper.SendCommand("/bmrai on");
            _plugin.AddDebugLog("Enabled BMR AI after dismount");
            
            CommandHelper.SendCommand("/gaction dig");
            _plugin.AddDebugLog("Using /gaction dig to trigger map content...");
            
            TransitionTo(BotState.OpeningChest, "Looking for treasure coffer to interact...");
        }
    }

    private void TickOpeningChest()
    {
        // First check if portal is targetable - if so, we're done with chests
        var portal = FindNearestPortal();
        if (portal != null)
        {
            _plugin.AddDebugLog("[OpeningChest] Portal detected - transitioning to portal interaction...");
            if (autoMoveActive)
            {
                GameHelpers.StopAutoMove();
                autoMoveActive = false;
            }
            CheckForPortalAfterChest();
            return;
        }
        
        // Click Yes on any dialog (Open the treasure coffer? etc)
        GameHelpers.ClickYesIfVisible();
        
        // No portal yet - keep working on chest
        var chest = _plugin.ChestDetectionService.FindNearestCoffer();

        if (chest == null)
        {
            var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
            StateDetail = $"Searching for treasure coffer... ({elapsed:F0}s)";
            if ((int)elapsed % 5 == 0 && (int)elapsed > 0)
                _plugin.AddDebugLog($"[OpeningChest] No coffer found yet ({elapsed:F0}s)");
            
            // Fallback: try targeting via command if ObjectTable fails
            if ((int)elapsed % 10 == 0 && (int)elapsed > 0)
            {
                _plugin.AddDebugLog("[OpeningChest] Trying /target \"Treasure Coffer\" as fallback...");
                CommandHelper.SendCommand("/target \"Treasure Coffer\"");
            }
            return;
        }

        var dist = _plugin.ChestDetectionService.NearestCofferDistance;
        var range = _plugin.Configuration.ChestInteractionRange;
        var chestName = chest.Name.TextValue;
        var now = DateTime.Now;

        // Approach chest if too far (regardless of combat state)
        if (dist > range)
        {
            Plugin.TargetManager.Target = chest;
            if (!autoMoveActive)
            {
                _plugin.AddDebugLog($"[OpeningChest] Coffer '{chestName}' at {dist:F1}y - lockon+automove approach");
                GameHelpers.LockOnAndAutoMove();
                autoMoveActive = true;
            }
            StateDetail = $"Approaching '{chestName}' ({dist:F1}y away)...";
        }
        else
        {
            // In range - stop automove
            if (autoMoveActive)
            {
                GameHelpers.StopAutoMove();
                autoMoveActive = false;
            }
        }

        // Continually try to interact every ~2 seconds (regardless of combat state)
        // Game will handle if chest is already opened or if we're in combat
        if ((now - lastInteractionTime).TotalSeconds >= 2.0)
        {
            lastInteractionTime = now;
            Plugin.TargetManager.Target = chest;
            var interacted = GameHelpers.InteractWithObject(chest);
            _plugin.AddDebugLog($"[OpeningChest] Interaction attempt with '{chestName}' - returned: {interacted}");
            StateDetail = $"Interacting with '{chestName}' - waiting for portal...";
        }
    }

    private void CheckForPortalAfterChest()
    {
        // Transition to a portal-searching state that retries every 2s for 10s
        portalRetryStart = DateTime.Now;
        TransitionTo(BotState.Completed, "Searching for portal...");
    }
    
    private IGameObject? FindNearestPortal()
    {
        // FrenRider-style: simple exact name match
        var portalObj = Plugin.ObjectTable.FirstOrDefault(obj => 
            obj != null && obj.Name.ToString() == "Teleportation Portal");
        
        if (portalObj != null)
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player != null)
            {
                var dist = Vector3.Distance(player.Position, portalObj.Position);
                if (dist <= 30f)
                    return portalObj;
            }
        }
        
        return null;
    }

    private void TickInCombat()
    {
        // InCombat state removed - OpeningChest now loops until portal appears
        // This should never be reached, but just in case, transition back to OpeningChest
        _plugin.AddDebugLog("[InCombat] Unexpected InCombat state - transitioning to OpeningChest...");
        TransitionTo(BotState.OpeningChest, "Resuming chest interaction loop...");
    }

    private void TickInDungeon()
    {
        // In dungeon - wait until we leave the instance
        // Bot will resume when we're back in the overworld
        StateDetail = "In dungeon instance - waiting to exit...";
        
        // Check if we're no longer in a dungeon (territory changed)
        if (CurrentLocation != null && Plugin.ClientState.TerritoryType != CurrentLocation.TerritoryId)
        {
            _plugin.AddDebugLog("Left dungeon instance - resuming normal operation");
            TransitionTo(BotState.Completed, "Dungeon completed - back in overworld");
        }
    }

    private void TickCompleted()
    {
        // If portalRetryStart is set, we're searching for a portal before finishing
        if (portalRetryStart != DateTime.MinValue)
        {
            var sinceStart = (DateTime.Now - portalRetryStart).TotalSeconds;
            var now = DateTime.Now;
            
            // Try to find and interact with portal for up to 15 seconds
            if (sinceStart <= 15.0)
            {
                // Click Yes on any visible dialog (portal confirmation from previous tick)
                if (GameHelpers.ClickYesIfVisible())
                {
                    _plugin.AddDebugLog("[Portal] Clicked Yes on portal dialog");
                    if (autoMoveActive)
                    {
                        GameHelpers.StopAutoMove();
                        autoMoveActive = false;
                    }
                    portalRetryStart = DateTime.MinValue;
                    TransitionTo(BotState.InDungeon, "Entering dungeon instance...");
                    return;
                }

                var portal = FindNearestPortal();

                if (portal != null)
                {
                    // Target the portal
                    Plugin.TargetManager.Target = portal;
                    
                    // Use lockon+automove to approach portal
                    var player = Plugin.ObjectTable.LocalPlayer;
                    if (player != null)
                    {
                        var portalDist = Vector3.Distance(player.Position, portal.Position);
                        if (portalDist > 3f && !autoMoveActive)
                        {
                            _plugin.AddDebugLog($"[Portal] Lockon+automove to portal at {portalDist:F1}y");
                            GameHelpers.LockOnAndAutoMove();
                            autoMoveActive = true;
                        }
                        else if (portalDist <= 3f && autoMoveActive)
                        {
                            GameHelpers.StopAutoMove();
                            autoMoveActive = false;
                        }
                    }

                    // /gaction jump for 0.5s bursts to get Y-axis range for underwater portals
                    if ((int)(sinceStart * 2) % 2 == 0 && (int)sinceStart > 0)
                    {
                        CommandHelper.SendCommand("/gaction jump");
                    }

                    // Continually interact every ~1 second
                    if ((now - lastInteractionTime).TotalSeconds >= 1.0)
                    {
                        lastInteractionTime = now;
                        _plugin.AddDebugLog($"[Portal] Interacting with '{portal.Name.TextValue}'...");
                        GameHelpers.InteractWithObject(portal);
                    }
                }
                else
                {
                    // No portal object found - try /target as fallback
                    if ((int)sinceStart % 4 == 0 && (int)sinceStart > 0)
                    {
                        CommandHelper.SendCommand("/target \"Teleportation Portal\"");
                        _plugin.AddDebugLog("[Portal] Trying /target fallback...");
                    }
                }
                
                StateDetail = $"Searching for portal... ({sinceStart:F0}/15s)";
                return;
            }
            
            // Time elapsed, no portal found - map is complete (no dungeon)
            _plugin.AddDebugLog("[Portal] No portal found after 15s - map complete (no dungeon)");
            if (autoMoveActive)
            {
                GameHelpers.StopAutoMove();
                autoMoveActive = false;
            }
            portalRetryStart = DateTime.MinValue;
        }
        
        _plugin.AddDebugLog("Map run complete.");
        KrangleService.ClearCache();

        if (_plugin.Configuration.AutoStartNextMap)
        {
            var maps = _plugin.InventoryService.ScanForMaps();
            if (maps.Count > 0)
            {
                RetryCount = 0;
                CurrentLocation = null;
                TransitionTo(BotState.SelectingMap, "Auto-starting next map...");
                return;
            }

            _plugin.AddDebugLog("No more maps in inventory.");
        }

        RetryCount = 0;
        TransitionTo(BotState.Idle, "Run complete.");
    }

    // ─── Error Handling ───────────────────────────────────────────────────────

    private void HandleError(string message)
    {
        _plugin.AddDebugLog($"[Error] {message}");

        var maxRetries = _plugin.Configuration.MaxRetries;
        if (maxRetries > 0 && RetryCount < maxRetries)
        {
            RetryCount++;
            _plugin.AddDebugLog($"Retrying ({RetryCount}/{maxRetries})...");
            _plugin.NavigationService.StopNavigation();
            TransitionTo(BotState.SelectingMap, $"Retry {RetryCount}/{maxRetries}: {message}");
        }
        else
        {
            _plugin.NavigationService.StopNavigation();
            TransitionTo(BotState.Error, message);
        }
    }

    // ─── Transition ───────────────────────────────────────────────────────────

    private void TransitionTo(BotState newState, string detail)
    {
        var prev = State;
        State = newState;
        StateDetail = detail;
        stateStartTime = DateTime.Now;
        stateActionIssued = false;
        dismountAttemptStart = DateTime.MinValue;
        descentInProgress = false;

        // Stop automove if it was active
        if (autoMoveActive)
        {
            GameHelpers.StopAutoMove();
            autoMoveActive = false;
        }

        // Unpause YesAlready when bot reaches terminal states
        if (newState == BotState.Idle || newState == BotState.Error)
        {
            _plugin.YesAlreadyIPC.Unpause();
            _plugin.AddDebugLog($"[TransitionTo] YesAlready unpaused: {!_plugin.YesAlreadyIPC.IsPaused}");
        }

        if (_plugin.Configuration.EnableStateLogging)
            _plugin.AddDebugLog($"[State] {prev} → {newState} | {detail}");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    public void SetLocation(MapLocation location)
    {
        CurrentLocation = location;
        _plugin.AddDebugLog($"Location set: {location.ZoneName} ({location.X:F1}, {location.Y:F1}, {location.Z:F1})");
    }
}
