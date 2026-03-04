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
    private bool chestInteracted; // Separate flag: true after we've called InteractWithObject on chest
    private DateTime combatEndTime; // Track when combat actually ended
    private const double TickIntervalSeconds = 0.5;
    private readonly MountService _mountService;

    private static readonly Dictionary<BotState, double> StateTimeouts = new()
    {
        { BotState.OpeningMap,        15  },
        { BotState.DetectingLocation, 30  },
        { BotState.Teleporting,       90  },
        { BotState.Mounting,          30  },
        { BotState.WaitingForParty,   120 },
        { BotState.Flying,            300 },
        { BotState.OpeningChest,      60  },
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
        if (State != BotState.Idle)
        {
            _plugin.AddDebugLog("Cannot start: bot is not idle.");
            return;
        }

        if (!Plugin.ClientState.IsLoggedIn)
        {
            _plugin.AddDebugLog("Cannot start: not logged in.");
            return;
        }

        RetryCount = 0;
        CurrentLocation = null;
        SelectedMapItemId = 0;
        TransitionTo(BotState.SelectingMap, "Starting map run...");
    }

    public void Stop()
    {
        _plugin.NavigationService.StopNavigation();
        IsPaused = false;
        TransitionTo(BotState.Idle, "Stopped by user.");
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
            return;
        }

        if (nav.State == NavigationState.Error)
        {
            HandleError($"Navigation error: {nav.StateDetail}");
            return;
        }

        // Re-navigate every 30 seconds in case we get stuck, but only if we're far from target
        var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
        var currentPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var targetPos = new Vector3(CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z);
        var distanceFromTarget = Vector3.Distance(currentPos, targetPos);
        
        // Only re-navigate if we're more than 5 yalms from target AND at least 10 seconds have passed
        if ((int)elapsed % 30 == 0 && (int)elapsed > 0 && distanceFromTarget > 5.0f)
        {
            nav.FlyToPosition(targetPos);
            _plugin.AddDebugLog($"Re-navigating to target (elapsed: {elapsed:F0}s, distance: {distanceFromTarget:F1}y)");
        }

        // Check if we're close enough to X,Z coordinates (within 5 yalms)
        var xzDist = Math.Sqrt(Math.Pow(currentPos.X - targetPos.X, 2) + Math.Pow(currentPos.Z - targetPos.Z, 2));
        
        if (nav.State == NavigationState.Arrived || nav.State == NavigationState.Idle || xzDist < 5.0f)
        {
            // Force landing if we're flying
            if (_plugin.NavigationService.IsMounted())
            {
                _plugin.AddDebugLog("Close to target - attempting to land...");
                
                System.Threading.Tasks.Task.Run(async () => {
                    // Use MountService to force land
                    _mountService.ForceLand();
                    
                    // Wait a bit more to ensure landing is complete
                    await System.Threading.Tasks.Task.Delay(2000);
                    
                    // If we're no longer mounted, proceed with map content
                    if (!_plugin.NavigationService.IsMounted())
                    {
                        _plugin.AddDebugLog("Successfully landed - proceeding with map content");
                        
                        // Use /gaction dig to trigger the map content
                        CommandHelper.SendCommand("/gaction dig");
                        _plugin.AddDebugLog("Using /gaction dig to trigger map content...");
                        
                        TransitionTo(BotState.OpeningChest, "Looking for treasure coffer to interact...");
                    }
                    else
                    {
                        _plugin.AddDebugLog("Failed to land - forcing dismount");
                        // Force dismount if landing failed
                        _mountService.Dismount();
                        await System.Threading.Tasks.Task.Delay(1500);
                        
                        CommandHelper.SendCommand("/gaction dig");
                        _plugin.AddDebugLog("Using /gaction dig to trigger map content...");
                        
                        TransitionTo(BotState.OpeningChest, "Looking for treasure coffer to interact...");
                    }
                });
                return;
            }
            
            // Use /gaction dig to trigger the map content
            CommandHelper.SendCommand("/gaction dig");
            _plugin.AddDebugLog("Using /gaction dig to trigger map content...");
            
            TransitionTo(BotState.OpeningChest, "Looking for treasure coffer to interact...");
        }
    }

    private void TickOpeningChest()
    {
        var chest = _plugin.ChestDetectionService.FindNearestCoffer();

        if (chest == null)
        {
            var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
            StateDetail = $"Searching for treasure coffer... ({elapsed:F0}s)";
            if ((int)elapsed % 5 == 0 && (int)elapsed > 0)
                _plugin.AddDebugLog($"[OpeningChest] No coffer found yet ({elapsed:F0}s)");
            
            // Check if combat started without chest (e.g., from /gaction dig)
            if (_plugin.NavigationService.IsInCombat())
            {
                _plugin.AddDebugLog("[OpeningChest] Combat detected without chest - transitioning to InCombat...");
                TransitionTo(BotState.InCombat, "Combat started without chest!");
                return;
            }
            return;
        }

        var dist = _plugin.ChestDetectionService.NearestCofferDistance;
        var range = _plugin.Configuration.ChestInteractionRange;
        var chestName = chest.Name.TextValue;

        // Step 1: Navigate to chest if too far
        if (dist > range)
        {
            if (!stateActionIssued)
            {
                _plugin.AddDebugLog($"[OpeningChest] Coffer '{chestName}' found at {dist:F1}y - moving closer (range: {range})");
                _plugin.NavigationService.MoveToPosition(chest.Position);
                stateActionIssued = true;
            }
            StateDetail = $"Moving to '{chestName}' ({dist:F1}y away)...";
            return;
        }

        // Step 2: In range - stop nav and interact
        _plugin.NavigationService.StopNavigation();

        if (!chestInteracted)
        {
            _plugin.AddDebugLog($"[OpeningChest] In range of '{chestName}' ({dist:F1}y) - attempting interaction...");
            var interacted = GameHelpers.InteractWithObject(chest);
            _plugin.AddDebugLog($"[OpeningChest] InteractWithObject returned: {interacted}");
            
            if (interacted)
            {
                chestInteracted = true;
                _plugin.AddDebugLog($"[OpeningChest] Successfully interacted with '{chestName}' - waiting for combat...");
                StateDetail = $"Interacted with '{chestName}' - waiting for combat...";
            }
            else
            {
                _plugin.AddDebugLog($"[OpeningChest] InteractWithObject failed for '{chestName}' - will retry next tick");
                StateDetail = $"Interaction failed - retrying...";
            }
        }
        else
        {
            // Already interacted, waiting for combat to start
            var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
            StateDetail = $"Waiting for combat after chest interaction... ({elapsed:F0}s)";
            
            // Check if combat started after chest interaction
            if (_plugin.NavigationService.IsInCombat())
            {
                _plugin.AddDebugLog("[OpeningChest] Combat detected after chest interaction - transitioning to InCombat...");
                TransitionTo(BotState.InCombat, "Combat started from chest interaction!");
                return;
            }
        }
    }

    private void CheckForPortalAfterChest()
    {
        // Look for portal (EventObj with "Teleportation Portal" in name)
        var portal = FindNearestPortal();
        if (portal != null)
        {
            _plugin.AddDebugLog($"Found portal '{portal.Name.TextValue}' - interacting to enter dungeon...");
            
            // First interact with the portal to get the popup
            var interacted = GameHelpers.InteractWithObject(portal);
            if (interacted)
            {
                _plugin.AddDebugLog("Interacted with portal - waiting for 'Journey through the portal?' popup");
                
                // Wait for popup then click yes
                System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ => {
                    CommandHelper.SendCommand("/click yes");
                    _plugin.AddDebugLog("Clicked yes to 'Journey through the portal?'");
                });
            }
            else
            {
                _plugin.AddDebugLog("Failed to interact with portal");
            }
            
            TransitionTo(BotState.InDungeon, "Entering dungeon instance...");
        }
        else
        {
            _plugin.AddDebugLog("No portal found - map complete!");
            TransitionTo(BotState.Completed, "Map completed - no portal");
        }
    }
    
    private IGameObject? FindNearestPortal()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return null;
        
        IGameObject? nearest = null;
        var nearestDist = float.MaxValue;
        
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj) continue;
            if (obj.Name.TextValue.Contains("Teleportation Portal", StringComparison.OrdinalIgnoreCase))
            {
                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist < nearestDist && dist < 10f) // Within 10y
                {
                    nearest = obj;
                    nearestDist = dist;
                }
            }
        }
        
        return nearest;
    }

    private void TickInCombat()
    {
        // Check if combat is active
        if (_plugin.NavigationService.IsInCombat())
        {
            // Combat is active - enable BMR AI
            if (!stateActionIssued)
            {
                CommandHelper.SendCommand("/bmrai on");
                _plugin.AddDebugLog("[InCombat] Combat active - enabled BMR AI");
                stateActionIssued = true;
                combatEndTime = DateTime.MinValue; // Reset end tracker
            }
            StateDetail = "In combat - BMR AI active...";
            return;
        }
        
        // Combat not active - track when it ended
        if (combatEndTime == DateTime.MinValue)
        {
            combatEndTime = DateTime.Now;
            _plugin.AddDebugLog("[InCombat] Combat ended - starting 6s cooldown...");
        }
        
        var sinceEnd = (DateTime.Now - combatEndTime).TotalSeconds;
        if (sinceEnd < 6)
        {
            StateDetail = $"Combat ended - waiting before 2nd chest interaction... ({sinceEnd:F0}/6s)";
            return;
        }
        
        // Time to interact with chest again (post-combat)
        _plugin.AddDebugLog("[InCombat] 6s cooldown done - attempting 2nd chest interaction...");
        var chest = _plugin.ChestDetectionService.FindNearestCoffer();
        
        if (chest != null)
        {
            var dist = _plugin.ChestDetectionService.NearestCofferDistance;
            _plugin.AddDebugLog($"[InCombat] Chest '{chest.Name.TextValue}' at {dist:F1}y");
            
            if (dist <= _plugin.Configuration.ChestInteractionRange)
            {
                var interacted = GameHelpers.InteractWithObject(chest);
                _plugin.AddDebugLog($"[InCombat] 2nd InteractWithObject returned: {interacted}");
                
                if (interacted)
                {
                    _plugin.AddDebugLog($"[InCombat] 2nd interaction successful - checking for portal...");
                }
                else
                {
                    _plugin.AddDebugLog("[InCombat] 2nd interaction failed - checking for portal anyway...");
                }
            }
            else
            {
                _plugin.AddDebugLog($"[InCombat] Chest too far ({dist:F1}y) - checking for portal...");
            }
        }
        else
        {
            _plugin.AddDebugLog("[InCombat] No chest found post-combat - checking for portal...");
        }
        
        CheckForPortalAfterChest();
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
        chestInteracted = false;
        combatEndTime = DateTime.MinValue;

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
