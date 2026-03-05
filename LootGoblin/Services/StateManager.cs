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
                GameHelpers.StopAutoMove();
                autoMoveActive = false;
            }
            CheckForPortalAfterChest();
            return;
        }
        
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

        // Check if in combat
        bool inCombat = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        
        if (inCombat)
        {
            // In combat - stop automove and clear target so we're not chained to chest
            if (autoMoveActive)
            {
                GameHelpers.StopAutoMove();
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

        // Not in combat - approach and interact with chest
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

        // Continually try to interact every ~2 seconds (only when NOT in combat)
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
                _plugin.AddDebugLog($"[Portal] Found portal at {dist:F1}y distance");
                if (dist <= 30f)
                    return portalObj;
                else
                    _plugin.AddDebugLog($"[Portal] Portal too far ({dist:F1}y > 30y)");
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

        // First time in dungeon after loading finishes
        if (!dungeonEntryProcessed && inDuty)
        {
            dungeonEntryProcessed = true;
            dungeonFloor = 1;
            excludedDoorEntityId = null;
            doorStuckStart = DateTime.MinValue;
            _plugin.AddDebugLog($"[Dungeon] Entered dungeon - floor {dungeonFloor}");
        }

        // Check if we got ejected (no longer in duty, grace period for loading)
        if (!inDuty && (DateTime.Now - stateStartTime).TotalSeconds > 5)
        {
            _plugin.AddDebugLog($"[Dungeon] No longer bound by duty - ejected after floor {dungeonFloor}");
            CommandHelper.SendCommand("/bmrai off");
            dungeonEntryProcessed = false;
            TransitionTo(BotState.Completed, $"Dungeon complete (reached floor {dungeonFloor})");
            return;
        }

        // Check if in combat → DungeonCombat
        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            TransitionTo(BotState.DungeonCombat, $"Combat on floor {dungeonFloor}!");
            return;
        }

        // Check for card game addon → skip with "Open Chest"
        if (TrySkipCardGame())
            return;

        // Scan for loot objects first (coffers, chests, sacks)
        var lootObjects = FindDungeonObjects(lootOnly: true);
        if (lootObjects.Count > 0)
        {
            TransitionTo(BotState.DungeonLooting, $"Looting on floor {dungeonFloor}...");
            return;
        }

        // Scan for progression objects (doors, spheres, any interactable EventObj)
        var progressionObjects = FindDungeonObjects(lootOnly: false);
        if (progressionObjects.Count > 0)
        {
            TransitionTo(BotState.DungeonProgressing, $"Progressing on floor {dungeonFloor}...");
            return;
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

        // Combat ended - go back to InDungeon to scan for loot/progression
        _plugin.AddDebugLog($"[Dungeon] Combat ended on floor {dungeonFloor}");
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
            if (autoMoveActive) { GameHelpers.StopAutoMove(); autoMoveActive = false; }
            TransitionTo(BotState.DungeonCombat, $"Combat during looting on floor {dungeonFloor}!");
            return;
        }

        // Check card game
        if (TrySkipCardGame())
            return;

        // Find loot objects
        var lootObjects = FindDungeonObjects(lootOnly: true);
        if (lootObjects.Count == 0)
        {
            // No more loot - go back to InDungeon to find progression
            if (autoMoveActive) { GameHelpers.StopAutoMove(); autoMoveActive = false; }
            _plugin.AddDebugLog($"[Dungeon] All loot collected on floor {dungeonFloor}");
            TransitionTo(BotState.InDungeon, $"Loot done - looking for progression on floor {dungeonFloor}...");
            return;
        }

        var target = lootObjects[0]; // Nearest loot object
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var dist = Vector3.Distance(player.Position, target.Position);
        var targetName = target.Name.ToString();

        if (dist > 3f)
        {
            // Approach with lockon+automove
            Plugin.TargetManager.Target = target;
            if (!autoMoveActive)
            {
                _plugin.AddDebugLog($"[Dungeon] Approaching loot '{targetName}' at {dist:F1}y");
                GameHelpers.LockOnAndAutoMove();
                autoMoveActive = true;
            }
            StateDetail = $"Approaching '{targetName}' ({dist:F1}y)...";
        }
        else
        {
            if (autoMoveActive) { GameHelpers.StopAutoMove(); autoMoveActive = false; }

            // Interact every ~2 seconds
            if ((DateTime.Now - lastInteractionTime).TotalSeconds >= 2.0)
            {
                lastInteractionTime = DateTime.Now;
                Plugin.TargetManager.Target = target;
                GameHelpers.InteractWithObject(target);
                _plugin.AddDebugLog($"[Dungeon] Interacting with loot '{targetName}'");
                StateDetail = $"Opening '{targetName}'...";
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
            if (autoMoveActive) { GameHelpers.StopAutoMove(); autoMoveActive = false; }
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
            if (autoMoveActive) { GameHelpers.StopAutoMove(); autoMoveActive = false; }
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
            if (autoMoveActive) { GameHelpers.StopAutoMove(); autoMoveActive = false; }
            TransitionTo(BotState.DungeonLooting, $"More loot found on floor {dungeonFloor}...");
            return;
        }

        // Find progression objects (any interactable EventObj that isn't loot)
        var progressionObjects = FindDungeonObjects(lootOnly: false);
        if (progressionObjects.Count == 0)
        {
            // Nothing to interact with - go back to scanning
            if (autoMoveActive) { GameHelpers.StopAutoMove(); autoMoveActive = false; }
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

        if (dist > 3f)
        {
            Plugin.TargetManager.Target = target;
            if (!autoMoveActive)
            {
                _plugin.AddDebugLog($"[Dungeon] Approaching progression '{targetName}' at {dist:F1}y");
                GameHelpers.LockOnAndAutoMove();
                autoMoveActive = true;
            }
            StateDetail = $"Approaching '{targetName}' ({dist:F1}y)...";
        }
        else
        {
            if (autoMoveActive) { GameHelpers.StopAutoMove(); autoMoveActive = false; }

            // Interact every ~2 seconds
            if ((DateTime.Now - lastInteractionTime).TotalSeconds >= 2.0)
            {
                lastInteractionTime = DateTime.Now;
                Plugin.TargetManager.Target = target;
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
                    _plugin.AddDebugLog("[Portal] Clicked Yes on portal dialog");
                    if (autoMoveActive)
                    {
                        GameHelpers.StopAutoMove();
                        autoMoveActive = false;
                    }
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

    // ─── Dungeon Helpers ─────────────────────────────────────────────────────

    private bool IsDungeonState() =>
        State == BotState.InDungeon || State == BotState.DungeonCombat ||
        State == BotState.DungeonLooting || State == BotState.DungeonProgressing;

    private List<IGameObject> FindDungeonObjects(bool lootOnly)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return new List<IGameObject>();

        var lootNames = new[] { "treasure", "coffer", "chest", "sack" };

        return Plugin.ObjectTable
            .Where(obj =>
            {
                if (obj == null) return false;
                var name = obj.Name.ToString();
                if (string.IsNullOrEmpty(name)) return false;
                if (Vector3.Distance(player.Position, obj.Position) > 30f) return false;

                // Only EventObj type (interactive dungeon objects)
                if (obj.ObjectKind != ObjectKind.EventObj) return false;

                // Skip the teleportation portal (handled separately)
                if (name == "Teleportation Portal") return false;

                var lower = name.ToLowerInvariant();
                bool isLoot = lootNames.Any(l => lower.Contains(l));

                if (lootOnly)
                    return isLoot;

                // For progression: return non-loot EventObj
                // Exclude any door we gave up on (stuck)
                if (isLoot) return false;
                if (excludedDoorEntityId.HasValue && obj.EntityId == excludedDoorEntityId.Value)
                    return false;

                return true;
            })
            .OrderBy(obj => Vector3.Distance(player.Position, obj.Position))
            .ToList();
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
        // For now, use generic approaches:

        // 1. Try clicking Yes on any standard dialog (catches many popups)
        // (Already called at top of each tick method)

        // 2. Try Numpad0 as generic controller-mode bypass every 5 seconds
        //    This simulates "confirm" and can bypass many UI popups
        if ((DateTime.Now - lastInteractionTime).TotalSeconds >= 5.0)
        {
            // Check if we're in a state where a popup might be blocking
            // If not in combat and no objects found for a while, try bypass
            var loot = FindDungeonObjects(lootOnly: true);
            var progression = FindDungeonObjects(lootOnly: false);

            if (loot.Count == 0 && progression.Count == 0)
            {
                // Nothing interactable visible - might be a card game popup
                // Try generic confirm via /sendkey numpad0
                // Note: Only do this if we've been stuck for a bit
                var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
                if (elapsed > 5)
                {
                    _plugin.AddDebugLog("[Dungeon] No objects visible - trying generic confirm (possible card game)");
                    CommandHelper.SendCommand("/echo [LootGoblin] Attempting card game skip...");
                    // TODO: Add proper addon detection once addon name is known
                    // For now, clicking Yes handles most dialogs
                }
            }
        }

        return false; // Not blocking - continue normal tick
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
