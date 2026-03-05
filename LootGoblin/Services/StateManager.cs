using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
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

    // Dungeon state tracking (Phase 8)
    private int dungeonFloor;
    private bool dungeonEntryProcessed; // True once we've confirmed we're inside the dungeon
    private uint? excludedDoorEntityId; // Door we gave up on (stuck), try others
    private DateTime doorStuckStart = DateTime.MinValue; // When we started trying current door
    private DateTime lastDungeonLogTime = DateTime.MinValue; // Throttle object logging
    private uint lastTerritoryId; // Track territory changes for floor transitions
    private DateTime forwardMovementStart = DateTime.MinValue; // When we started moving forward after territory change
    private uint lastGlobalTerritoryId; // Track territory changes globally for map refresh
    private DateTime chestDisappearedTime = DateTime.MinValue; // Track when chest first disappeared for grace period
    private HashSet<uint> attemptedCoffers = new HashSet<uint>(); // Track which coffers we've tried to interact with
    private DateTime cofferNavigationStart = DateTime.MinValue; // When we started navigating to current coffer
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
        { BotState.DungeonCombat,      300 },
        { BotState.DungeonLooting,     120 },
        { BotState.DungeonProgressing, 120 },
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
            // Lost connection is the only legitimate reason to stop the bot
            _plugin.NavigationService.StopNavigation();
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
        // Check for territory change and refresh maps to fix inventory index issues
        var currentTerritory = Plugin.ClientState.TerritoryType;
        if (lastGlobalTerritoryId != 0 && lastGlobalTerritoryId != currentTerritory)
        {
            _plugin.AddDebugLog($"[Territory] Territory changed: {lastGlobalTerritoryId} -> {currentTerritory} - refreshing maps");
            _plugin.InventoryService.ScanForMaps();
        }
        lastGlobalTerritoryId = currentTerritory;

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
            case BotState.DungeonCombat:    TickDungeonCombat();    break;
            case BotState.DungeonLooting:   TickDungeonLooting();   break;
            case BotState.DungeonProgressing: TickDungeonProgressing(); break;
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
        if (IsDungeonState())
        {
            CommandHelper.SendCommand("/bmrai off");
            _plugin.AddDebugLog("[Stop] Disabled BMR AI (was in dungeon)");
        }
        IsPaused = false;
        RetryCount = 0;
        portalRetryStart = DateTime.MinValue;
        dungeonEntryProcessed = false;
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
        
        // If we're not mounted, we've already dismounted - proceed with dig regardless of nav state
        if (!_plugin.NavigationService.IsMounted() && dismountAttemptStart != DateTime.MinValue)
        {
            _plugin.AddDebugLog("Successfully dismounted - proceeding with map content");
            
            CommandHelper.SendCommand("/bmrai on");
            _plugin.AddDebugLog("Enabled BMR AI after dismount");
            
            CommandHelper.SendCommand("/gaction dig");
            _plugin.AddDebugLog("Using /gaction dig to trigger map content...");
            
            TransitionTo(BotState.OpeningChest, "Looking for treasure coffer to interact...");
            return;
        }
        
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
        }
    }

    private void TickOpeningChest()
    {
        // Click Yes on any dialog (Open the treasure coffer? etc)
        GameHelpers.ClickYesIfVisible();
        
        // Check for portal EVERY tick - portals can appear during/after combat
        var portal = FindNearestPortal();
        if (portal != null)
        {
            _plugin.AddDebugLog("[OpeningChest] Portal detected - transitioning to portal interaction...");
            if (autoMoveActive)
            {
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
            }
            CheckForPortalAfterChest();
            return;
        }
        
        // No portal yet - keep working on chest
        var chest = _plugin.ChestDetectionService.FindNearestCoffer();

        if (chest == null)
        {
            // Start grace period timer if not already started
            if (chestDisappearedTime == DateTime.MinValue)
            {
                chestDisappearedTime = DateTime.Now;
                _plugin.AddDebugLog("[OpeningChest] Chest disappeared - starting 5s grace period");
            }
            
            var gracePeriod = (DateTime.Now - chestDisappearedTime).TotalSeconds;
            
            // Wait 5 seconds before declaring run complete (prevents FATE interference)
            if (gracePeriod < 5.0)
            {
                StateDetail = $"Waiting for chest to reappear... ({gracePeriod:F1}/5.0s)";
                
                // Fallback: try targeting via command if ObjectTable fails
                if ((int)gracePeriod % 2 == 0 && (int)gracePeriod > 0)
                {
                    CommandHelper.SendCommand("/target \"Treasure Coffer\"");
                }
                return;
            }
            
            // Grace period elapsed - chest is truly gone, check for portal
            _plugin.AddDebugLog("[OpeningChest] No chest found after 5s grace period - checking for portal");
            chestDisappearedTime = DateTime.MinValue;
            CheckForPortalAfterChest();
            return;
        }
        
        // Chest exists - reset grace period timer
        chestDisappearedTime = DateTime.MinValue;

        var dist = _plugin.ChestDetectionService.NearestCofferDistance;
        var range = _plugin.Configuration.ChestInteractionRange;
        var chestName = chest.Name.TextValue;
        var now = DateTime.Now;

        // Check if in combat
        bool inCombat = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        
        if (inCombat)
        {
            // In combat - stop navigation and clear target so we're not chained to chest
            if (autoMoveActive)
            {
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
            }
            // Clear target so player can fight freely
            if (Plugin.TargetManager.Target?.Name.ToString() == chestName)
            {
                Plugin.TargetManager.Target = null;
            }
            StateDetail = $"In combat - waiting for combat to end...";
            return;
        }

        // Not in combat - approach and interact with chest using lockon+automove
        Plugin.TargetManager.Target = chest;
        
        if (dist > range)
        {
            // Use lockon+automove for short-range chest approach
            if (!autoMoveActive)
            {
                _plugin.AddDebugLog($"[OpeningChest] Coffer '{chestName}' at {dist:F1}y - lockon+automove");
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

        // Continually try to interact every ~1 second (only when NOT in combat)
        if ((now - lastInteractionTime).TotalSeconds >= 1.0)
        {
            lastInteractionTime = now;
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
                _plugin.AddDebugLog($"[Portal] Found portal at {dist:F1}y distance");
                
                if (dist > 30f)
                {
                    _plugin.AddDebugLog($"[Portal] Portal too far ({dist:F1}y > 30y)");
                    return null;
                }
                
                // Verify portal is targetable (not a ghost object)
                if (!IsObjectTargetable(portalObj))
                {
                    _plugin.AddDebugLog($"[Portal] Portal is NOT targetable (ghost object) - ignoring");
                    return null;
                }
                
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
        GameHelpers.ClickYesIfVisible();

        // Grace period: don't check for portal immediately after entering InDungeon state
        // This prevents rapid toggling between InDungeon and Completed states
        var timeSinceStateStart = (DateTime.Now - stateStartTime).TotalSeconds;
        if (timeSinceStateStart < 3.0)
        {
            StateDetail = "Confirming dungeon entry...";
            return;
        }

        // Check if portal exists in ObjectTable - if so, we're still outside, not inside dungeon
        var portal = FindNearestPortal();
        if (portal != null)
        {
            _plugin.AddDebugLog("[InDungeon] Teleportation Portal detected - still outside, transitioning back to Completed");
            portalRetryStart = DateTime.Now;
            TransitionTo(BotState.Completed, "Portal found - searching for portal...");
            return;
        }

        bool inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                      Plugin.Condition[ConditionFlag.BoundByDuty56];
        bool loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                       Plugin.Condition[ConditionFlag.BetweenAreas51];

        // Wait for loading screens (entering dungeon or transitioning between rooms)
        if (loading)
        {
            StateDetail = "Loading dungeon room...";
            return;
        }

        // Track territory changes for floor transitions
        var currentTerritory = Plugin.ClientState.TerritoryType;
        bool territoryChanged = lastTerritoryId != 0 && lastTerritoryId != currentTerritory;
        if (territoryChanged)
        {
            _plugin.AddDebugLog($"[Dungeon] Territory changed: {lastTerritoryId} -> {currentTerritory}");
            dungeonFloor++;
            excludedDoorEntityId = null;
            doorStuckStart = DateTime.MinValue;
            forwardMovementStart = DateTime.MinValue; // Reset forward movement timer
        }
        lastTerritoryId = currentTerritory;

        // First time in dungeon
        if (!dungeonEntryProcessed)
        {
            dungeonFloor = 1;
            dungeonEntryProcessed = true;
            _plugin.AddDebugLog($"[InDungeon] First entry confirmed - floor {dungeonFloor}");
            _plugin.AddDebugLog($"[InDungeon] Territory: {currentTerritory}, BoundByDuty: {inDuty}");
        }

        // Check ejection
        if (!inDuty && (DateTime.Now - stateStartTime).TotalSeconds > 5)
        {
            _plugin.AddDebugLog($"[Dungeon] No longer bound by duty - ejected after floor {dungeonFloor}");
            CommandHelper.SendCommand("/bmrai off");
            dungeonEntryProcessed = false;
            TransitionTo(BotState.Completed, $"Dungeon complete (reached floor {dungeonFloor})");
            return;
        }

        // Check for combat
        bool inCombat = Plugin.Condition[ConditionFlag.InCombat];
        if (inCombat)
        {
            _plugin.AddDebugLog($"[InDungeon] Combat detected on floor {dungeonFloor}");
            TransitionTo(BotState.DungeonCombat, $"Combat detected on floor {dungeonFloor}...");
            return;
        }

        // Check for card game addon → skip with "Open Chest"
        if (TrySkipCardGame())
            return;

        // After territory change: check for Arcane Sphere or start forward movement
        if (territoryChanged)
        {
            var sphere = FindArcaneSphere();
            if (sphere != null)
            {
                _plugin.AddDebugLog($"[Dungeon] Arcane Sphere detected after territory change - targeting");
                TransitionTo(BotState.DungeonLooting, $"Arcane Sphere on floor {dungeonFloor}...");
                return;
            }
            else
            {
                // No sphere - start moving forward for 10 seconds to trigger area shift
                _plugin.AddDebugLog($"[Dungeon] No Arcane Sphere after territory change - moving forward for 10s");
                forwardMovementStart = DateTime.Now;
            }
        }

        // Handle forward movement after territory change (no sphere)
        if (forwardMovementStart != DateTime.MinValue)
        {
            var forwardElapsed = (DateTime.Now - forwardMovementStart).TotalSeconds;
            if (forwardElapsed < 10.0)
            {
                // Move forward using keyboard
                if ((int)forwardElapsed % 1 == 0) // Every second
                {
                    CommandHelper.SendCommand("/automove on");
                }
                StateDetail = $"Moving forward to trigger area shift... ({forwardElapsed:F0}/10s)";
                return;
            }
            else
            {
                // Stop forward movement after 10 seconds
                CommandHelper.SendCommand("/automove off");
                forwardMovementStart = DateTime.MinValue;
                _plugin.AddDebugLog($"[Dungeon] Forward movement complete");
            }
        }

        // Scan for loot objects first (priority)
        _plugin.AddDebugLog($"[InDungeon] Scanning for loot objects on floor {dungeonFloor}...");
        var lootObjects = FindDungeonObjects(lootOnly: true);
        if (lootObjects.Count > 0)
        {
            _plugin.AddDebugLog($"[InDungeon] Found {lootObjects.Count} loot object(s), transitioning to DungeonLooting");
            TransitionTo(BotState.DungeonLooting, $"Found {lootObjects.Count} loot object(s) on floor {dungeonFloor}...");
            return;
        }
        else
        {
            _plugin.AddDebugLog($"[InDungeon] No loot objects found");
        }

        // Scan for progression objects
        _plugin.AddDebugLog($"[InDungeon] Scanning for progression objects on floor {dungeonFloor}...");
        var progressionObjects = FindDungeonObjects(lootOnly: false);
        if (progressionObjects.Count > 0)
        {
            _plugin.AddDebugLog($"[InDungeon] Found {progressionObjects.Count} progression object(s), transitioning to DungeonProgressing");
            TransitionTo(BotState.DungeonProgressing, $"Found {progressionObjects.Count} progression object(s) on floor {dungeonFloor}...");
            return;
        }
        else
        {
            _plugin.AddDebugLog($"[InDungeon] No progression objects found");
        }

        // Nothing found - waiting for objects to spawn
        var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
        StateDetail = $"Floor {dungeonFloor} - scanning for objects... ({elapsed:F0}s)";

        // Periodically log all visible objects for datamining
        if ((DateTime.Now - lastDungeonLogTime).TotalSeconds >= 15)
        {
            lastDungeonLogTime = DateTime.Now;
            LogDungeonObjects();
        }
    }

    private void TickDungeonCombat()
    {
        GameHelpers.ClickYesIfVisible();

        bool loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                       Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (loading)
        {
            StateDetail = "Loading...";
            return;
        }

        // Check ejection
        bool inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                      Plugin.Condition[ConditionFlag.BoundByDuty56];
        if (!inDuty)
        {
            _plugin.AddDebugLog($"[Dungeon] Ejected during combat on floor {dungeonFloor}");
            CommandHelper.SendCommand("/bmrai off");
            dungeonEntryProcessed = false;
            TransitionTo(BotState.Completed, $"Dungeon complete (wiped on floor {dungeonFloor})");
            return;
        }

        // Don't interfere with targeting during combat - let BMR handle it
        bool inCombat = Plugin.Condition[ConditionFlag.InCombat];

        if (inCombat)
        {
            StateDetail = $"In combat on floor {dungeonFloor} - BMR handling...";
            return;
        }

        // Combat ended - check for loot first before going back to InDungeon
        _plugin.AddDebugLog($"[Dungeon] Combat ended on floor {dungeonFloor}");
        
        var lootObjects = FindDungeonObjects(lootOnly: true);
        if (lootObjects.Count > 0)
        {
            _plugin.AddDebugLog($"[Dungeon] Combat ended - {lootObjects.Count} loot object(s) found");
            TransitionTo(BotState.DungeonLooting, $"Looting after combat on floor {dungeonFloor}...");
            return;
        }
        
        TransitionTo(BotState.InDungeon, $"Combat ended - scanning floor {dungeonFloor}...");
    }

    private void TickDungeonLooting()
    {
        GameHelpers.ClickYesIfVisible();

        bool loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                       Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (loading)
        {
            StateDetail = "Loading...";
            return;
        }

        // Check ejection
        bool inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                      Plugin.Condition[ConditionFlag.BoundByDuty56];
        if (!inDuty)
        {
            _plugin.AddDebugLog($"[Dungeon] Ejected during looting on floor {dungeonFloor}");
            CommandHelper.SendCommand("/bmrai off");
            dungeonEntryProcessed = false;
            TransitionTo(BotState.Completed, $"Dungeon complete (floor {dungeonFloor})");
            return;
        }

        // If combat starts during looting, switch to combat
        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
            TransitionTo(BotState.DungeonCombat, $"Combat during looting on floor {dungeonFloor}!");
            return;
        }

        // Check card game
        if (TrySkipCardGame())
            return;

        // Find loot objects, excluding ones we've already attempted
        var lootObjects = FindDungeonObjects(lootOnly: true)
            .Where(obj => !attemptedCoffers.Contains(obj.EntityId))
            .ToList();
        
        if (lootObjects.Count == 0)
        {
            // No more loot to attempt - go back to InDungeon to find progression
            if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
            _plugin.AddDebugLog($"[DungeonLooting] All loot attempted on floor {dungeonFloor} (attempted {attemptedCoffers.Count} coffer(s))");
            attemptedCoffers.Clear(); // Reset for next floor
            cofferNavigationStart = DateTime.MinValue;
            TransitionTo(BotState.InDungeon, $"Loot done - looking for progression on floor {dungeonFloor}...");
            return;
        }

        var target = lootObjects[0]; // Nearest unattempted loot object
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var dist = Vector3.Distance(player.Position, target.Position);
        var targetName = target.Name.ToString();
        var targetId = target.EntityId;

        // Always set target
        Plugin.TargetManager.Target = target;
        
        // Start navigation timer if not already started for this coffer
        if (cofferNavigationStart == DateTime.MinValue)
        {
            cofferNavigationStart = DateTime.Now;
            _plugin.AddDebugLog($"[DungeonLooting] Starting navigation to '{targetName}' (EntityId: {targetId})");
        }

        // Check if we've been trying to reach this coffer for too long (30s timeout)
        var navigationTime = (DateTime.Now - cofferNavigationStart).TotalSeconds;
        if (navigationTime > 30.0)
        {
            _plugin.AddDebugLog($"[DungeonLooting] Timeout reaching '{targetName}' after {navigationTime:F0}s - marking as attempted");
            attemptedCoffers.Add(targetId);
            cofferNavigationStart = DateTime.MinValue;
            if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
            return; // Try next coffer
        }
        
        if (dist > 3f)
        {
            // Use vnavmesh for all coffers to ensure we actually reach them
            if (!autoMoveActive)
            {
                _plugin.AddDebugLog($"[DungeonLooting] Navigating to '{targetName}' at {dist:F1}y - vnavmesh");
                _plugin.NavigationService.MoveToPosition(target.Position);
                autoMoveActive = true;
            }
            
            // After navigating for 2s, check if we're close enough to stop
            if (navigationTime > 2.0 && dist < 5f)
            {
                _plugin.AddDebugLog($"[DungeonLooting] Close enough to '{targetName}' ({dist:F1}y) - stopping navigation");
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
            }
            
            StateDetail = $"Navigating to '{targetName}' ({dist:F1}y, {navigationTime:F0}s)...";
        }
        else
        {
            // In range - stop movement and interact
            if (autoMoveActive)
            {
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
                _plugin.AddDebugLog($"[DungeonLooting] Reached '{targetName}' - attempting interaction");
            }

            // Interact every ~1 second
            if ((DateTime.Now - lastInteractionTime).TotalSeconds >= 1.0)
            {
                lastInteractionTime = DateTime.Now;
                var interacted = GameHelpers.InteractWithObject(target);
                _plugin.AddDebugLog($"[DungeonLooting] Interacting with '{targetName}' - returned: {interacted}");
                
                // After interaction attempt, mark as attempted and move to next
                // The interaction might trigger combat or loot, either way we've tried this one
                if (navigationTime > 3.0) // Give it at least 3s of interaction attempts
                {
                    _plugin.AddDebugLog($"[DungeonLooting] Marking '{targetName}' as attempted after {navigationTime:F0}s");
                    attemptedCoffers.Add(targetId);
                    cofferNavigationStart = DateTime.MinValue;
                }
                
                StateDetail = $"Interacting with '{targetName}'...";
            }
        }
    }

    private void TickDungeonProgressing()
    {
        GameHelpers.ClickYesIfVisible();

        bool loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                       Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (loading)
        {
            // Loading screen = we're moving to next room!
            dungeonFloor++;
            excludedDoorEntityId = null;
            doorStuckStart = DateTime.MinValue;
            if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
            _plugin.AddDebugLog($"[Dungeon] Loading next room - advancing to floor {dungeonFloor}");
            TransitionTo(BotState.InDungeon, $"Entering floor {dungeonFloor}...");
            return;
        }

        // Check ejection
        bool inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                      Plugin.Condition[ConditionFlag.BoundByDuty56];
        if (!inDuty)
        {
            _plugin.AddDebugLog($"[Dungeon] Ejected during progression (wrong door?) on floor {dungeonFloor}");
            CommandHelper.SendCommand("/bmrai off");
            dungeonEntryProcessed = false;
            TransitionTo(BotState.Completed, $"Dungeon complete (floor {dungeonFloor})");
            return;
        }

        // If combat starts, switch to combat
        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
            TransitionTo(BotState.DungeonCombat, $"Combat during progression on floor {dungeonFloor}!");
            return;
        }

        // Check card game
        if (TrySkipCardGame())
            return;

        // Check if new loot appeared (bonus spawns)
        var lootObjects = FindDungeonObjects(lootOnly: true);
        if (lootObjects.Count > 0)
        {
            if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
            TransitionTo(BotState.DungeonLooting, $"More loot found on floor {dungeonFloor}...");
            return;
        }

        // Find progression objects (any interactable EventObj that isn't loot)
        var progressionObjects = FindDungeonObjects(lootOnly: false);
        if (progressionObjects.Count == 0)
        {
            // Nothing to interact with - go back to scanning
            if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
            var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
            if (elapsed > 30)
            {
                _plugin.AddDebugLog($"[Dungeon] No progression objects found for 30s on floor {dungeonFloor} - rescanning");
                TransitionTo(BotState.InDungeon, $"Rescanning floor {dungeonFloor}...");
            }
            else
            {
                StateDetail = $"Looking for door/progression on floor {dungeonFloor}... ({elapsed:F0}s)";
            }
            return;
        }

        var target = progressionObjects[0]; // Nearest progression object
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var dist = Vector3.Distance(player.Position, target.Position);
        var targetName = target.Name.ToString();

        // Track stuck time on current door
        if (doorStuckStart == DateTime.MinValue)
        {
            doorStuckStart = DateTime.Now;
            _plugin.AddDebugLog($"[Dungeon] Trying progression object '{targetName}' (EntityId: {target.EntityId})");
        }

        var stuckSeconds = (DateTime.Now - doorStuckStart).TotalSeconds;

        // If stuck for 60s on same door, exclude it and try another
        if (stuckSeconds > 60 && progressionObjects.Count > 1)
        {
            excludedDoorEntityId = target.EntityId;
            doorStuckStart = DateTime.MinValue;
            _plugin.AddDebugLog($"[Dungeon] Stuck at '{targetName}' for 60s - trying other door");
            return; // Next tick will pick a different object
        }

        Plugin.TargetManager.Target = target;
        
        if (dist > 3f)
        {
            // Distance-based navigation: <10y lockon+automove, >10y vnavmesh
            if (dist < 10f)
            {
                // Close range - use lockon+automove
                if (!autoMoveActive)
                {
                    _plugin.AddDebugLog($"[Dungeon] Approaching progression '{targetName}' at {dist:F1}y - lockon+automove");
                    GameHelpers.LockOnAndAutoMove();
                    autoMoveActive = true;
                }
            }
            else
            {
                // Long range - use vnavmesh
                if (!autoMoveActive)
                {
                    _plugin.AddDebugLog($"[Dungeon] Approaching progression '{targetName}' at {dist:F1}y - vnavmesh");
                    _plugin.NavigationService.MoveToPosition(target.Position);
                    autoMoveActive = true;
                }
            }
            StateDetail = $"Approaching '{targetName}' ({dist:F1}y)...";
        }
        else
        {
            // In range - stop movement and interact
            if (autoMoveActive)
            {
                if (dist < 10f)
                    GameHelpers.StopAutoMove();
                else
                    _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
            }

            // Interact every ~1 second
            if ((DateTime.Now - lastInteractionTime).TotalSeconds >= 1.0)
            {
                lastInteractionTime = DateTime.Now;
                GameHelpers.InteractWithObject(target);
                _plugin.AddDebugLog($"[Dungeon] Interacting with '{targetName}' ({stuckSeconds:F0}s)");
                StateDetail = $"Interacting with '{targetName}' ({stuckSeconds:F0}s)...";
            }
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
                    _plugin.AddDebugLog("[Portal] Clicked Yes on portal dialog - waiting for loading screen...");
                    if (autoMoveActive)
                    {
                        _plugin.NavigationService.StopNavigation();
                        autoMoveActive = false;
                    }
                    // Don't transition to InDungeon yet - wait for BoundByDuty flag
                    // Portal interaction will trigger loading screen, then BoundByDuty will be set
                    StateDetail = "Portal accepted - waiting for dungeon entry...";
                    return;
                }
                
                // Check if we're now in a duty (portal was accepted and loading finished)
                bool inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                              Plugin.Condition[ConditionFlag.BoundByDuty56];
                bool loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                               Plugin.Condition[ConditionFlag.BetweenAreas51];
                
                // Only transition to InDungeon if:
                // 1. We're bound by duty AND
                // 2. Portal no longer exists in ObjectTable (we've actually entered) OR we're loading
                var portalCheck = FindNearestPortal();
                if (inDuty && (portalCheck == null || loading))
                {
                    _plugin.AddDebugLog("[Portal] BoundByDuty detected and portal gone/loading - entering dungeon!");
                    portalRetryStart = DateTime.MinValue;
                    dungeonEntryProcessed = false;
                    dungeonFloor = 0;
                    excludedDoorEntityId = null;
                    doorStuckStart = DateTime.MinValue;
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
                            _plugin.AddDebugLog($"[Portal] Portal at {portalDist:F1}y - lockon+automove");
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
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
            }
            portalRetryStart = DateTime.MinValue;
        }
        
        _plugin.AddDebugLog("[Completed] Map run complete.");
        
        // CRITICAL: Do NOT start next map if still in a dungeon
        bool stillInDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                           Plugin.Condition[ConditionFlag.BoundByDuty56];
        if (stillInDuty)
        {
            _plugin.AddDebugLog("[Completed] ERROR: Still in dungeon (BoundByDuty) - cannot start next map!");
            TransitionTo(BotState.Error, "Still in dungeon - cannot start next map. Manual intervention required.");
            return;
        }
        
        KrangleService.ClearCache();

        if (_plugin.Configuration.AutoStartNextMap)
        {
            _plugin.AddDebugLog("[Completed] AutoStartNextMap enabled - scanning for maps");
            var maps = _plugin.InventoryService.ScanForMaps();
            _plugin.AddDebugLog($"[Completed] Found {maps.Count} map(s) in inventory");
            
            if (maps.Count > 0)
            {
                RetryCount = 0;
                CurrentLocation = null;
                TransitionTo(BotState.SelectingMap, "Auto-starting next map...");
                return;
            }

            _plugin.AddDebugLog("[Completed] No more maps in inventory.");
        }

        RetryCount = 0;
        TransitionTo(BotState.Idle, "Run complete.");
    }

    // ─── Dungeon Helpers ─────────────────────────────────────────────────────

    private bool IsDungeonState() =>
        State == BotState.InDungeon || State == BotState.DungeonCombat ||
        State == BotState.DungeonLooting || State == BotState.DungeonProgressing;

    private bool IsObjectTargetable(IGameObject obj)
    {
        // Verify object can actually be targeted (not a ghost object)
        try
        {
            var previousTarget = Plugin.TargetManager.Target;
            Plugin.TargetManager.Target = obj;
            var canTarget = Plugin.TargetManager.Target?.EntityId == obj.EntityId;
            Plugin.TargetManager.Target = previousTarget; // Restore previous target
            
            if (!canTarget)
            {
                _plugin.AddDebugLog($"[ObjectCheck] '{obj.Name}' at {obj.Position} is NOT targetable (ghost object)");
            }
            
            return canTarget;
        }
        catch
        {
            return false;
        }
    }

    private IGameObject? FindArcaneSphere()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return null;

        return Plugin.ObjectTable
            .FirstOrDefault(obj =>
                obj != null &&
                obj.ObjectKind == ObjectKind.EventObj &&
                obj.Name.ToString().Contains("Arcane Sphere", StringComparison.OrdinalIgnoreCase) &&
                Vector3.Distance(player.Position, obj.Position) <= 30f);
    }

    private List<IGameObject> FindDungeonObjects(bool lootOnly)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return new List<IGameObject>();

        // Priority: Arcane Sphere, then loot (treasure/coffer/chest/sack), then progression (doors/gates)
        var lootNames = new[] { "treasure", "coffer", "chest", "sack" };
        var sphereName = "arcane sphere";
        var doorNames = new[] { "door", "gate", "sphere" }; // Partial matching for doors (Sluice Gate, etc)

        // First pass: find all loot objects within 50y for door priority check
        var allLoot = Plugin.ObjectTable
            .Where(obj =>
            {
                if (obj == null || obj.ObjectKind != ObjectKind.EventObj) return false;
                var name = obj.Name.ToString();
                if (string.IsNullOrEmpty(name)) return false;
                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist > 50f) return false; // Check within 50y for door priority
                
                var lower = name.ToLowerInvariant();
                return lower.Contains(sphereName) || lootNames.Any(l => lower.Contains(l));
            })
            .ToList();

        bool hasNearbyLoot = allLoot.Count > 0;
        
        if (hasNearbyLoot)
        {
            _plugin.AddDebugLog($"[Dungeon] Found {allLoot.Count} loot object(s) within 50y - doors will be skipped");
            foreach (var loot in allLoot)
            {
                var lootDist = Vector3.Distance(player.Position, loot.Position);
                _plugin.AddDebugLog($"[Dungeon]   - '{loot.Name}' at {lootDist:F1}y");
            }
        }

        var candidates = Plugin.ObjectTable
            .Where(obj =>
            {
                if (obj == null) return false;
                var name = obj.Name.ToString();
                if (string.IsNullOrEmpty(name)) return false;
                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist > 30f) return false; // 30y for interaction range

                // Only EventObj type (interactive dungeon objects)
                if (obj.ObjectKind != ObjectKind.EventObj) return false;

                // Skip the teleportation portal (handled separately)
                if (name == "Teleportation Portal") return false;

                var lower = name.ToLowerInvariant();
                bool isSphere = lower.Contains(sphereName);
                bool isLoot = lootNames.Any(l => lower.Contains(l));
                bool isDoor = doorNames.Any(d => lower.Contains(d));

                if (lootOnly)
                    return isSphere || isLoot; // Sphere counts as loot priority

                // For progression: return doors/gates, but NOT if loot exists within 50y
                if (isSphere || isLoot) return false;
                
                // Don't return doors if there's loot within 50y (other rooms)
                if (isDoor && hasNearbyLoot)
                {
                    _plugin.AddDebugLog($"[Dungeon] Skipping door '{name}' - loot within 50y");
                    return false;
                }
                
                // Exclude any door we gave up on (stuck)
                if (excludedDoorEntityId.HasValue && obj.EntityId == excludedDoorEntityId.Value)
                    return false;

                return true;
            })
            .Where(obj => IsObjectTargetable(obj)) // Filter out ghost objects
            .OrderBy(obj =>
            {
                // Priority order: Arcane Sphere first, then by distance
                var name = obj.Name.ToString().ToLowerInvariant();
                if (name.Contains("arcane sphere")) return 0; // Highest priority
                return (int)Vector3.Distance(player.Position, obj.Position);
            })
            .ToList();
        
        // Log final results
        _plugin.AddDebugLog($"[Dungeon] FindDungeonObjects(lootOnly={lootOnly}) found {candidates.Count} object(s)");
        foreach (var obj in candidates)
        {
            var objDist = Vector3.Distance(player.Position, obj.Position);
            _plugin.AddDebugLog($"[Dungeon]   - '{obj.Name}' at {objDist:F1}y (EntityId: {obj.EntityId})");
        }
        
        return candidates;
    }

    private void LogDungeonObjects()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var objects = Plugin.ObjectTable
            .Where(obj => obj != null &&
                   !string.IsNullOrEmpty(obj.Name.ToString()) &&
                   Vector3.Distance(player.Position, obj.Position) <= 50f &&
                   obj.ObjectKind != ObjectKind.Player &&
                   obj.ObjectKind != ObjectKind.Companion)
            .OrderBy(obj => Vector3.Distance(player.Position, obj.Position))
            .ToList();

        _plugin.AddDebugLog($"[Dungeon] === Object Scan (floor {dungeonFloor}, {objects.Count} objects) ===");
        foreach (var obj in objects.Take(15))
        {
            var dist = Vector3.Distance(player.Position, obj.Position);
            _plugin.AddDebugLog($"[Dungeon]   {obj.ObjectKind}: '{obj.Name}' at {dist:F1}y (EntityId: {obj.EntityId})");
        }
    }

    private bool TrySkipCardGame()
    {
        // Try to detect and skip the card game by clicking "Open Chest"
        // The addon name is currently unknown - will be discovered via testing
        // For now, ClickYesIfVisible() at the top of each tick method handles most dialogs
        // This function is a placeholder for future addon-specific detection
        
        // Don't spam - card game detection will be implemented when addon name is known
        return false; // Not blocking - continue normal tick
    }

    // ─── Error Handling ───────────────────────────────────────────────────────

    private void HandleError(string message)
    {
        RetryCount++;
        _plugin.AddDebugLog($"[Error #{RetryCount}] {message}");
        
        // Always retry from SelectingMap - never stop the bot
        // Errors are counted for informational purposes only
        _plugin.NavigationService.StopNavigation();
        TransitionTo(BotState.SelectingMap, $"Error #{RetryCount}: {message}");
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

        // Stop navigation if it was active
        if (autoMoveActive)
        {
            _plugin.NavigationService.StopNavigation();
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
