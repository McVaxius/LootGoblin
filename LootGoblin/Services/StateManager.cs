using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Text;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using LootGoblin.Models;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace LootGoblin.Services;

public enum DungeonObjective
{
    ClearingChests,    // Level 1: Always start here
    ProcessingSpheres, // Level 2: After chests cleared OR if no chests exist
    HeadingToExit      // Level 3: After all objectives done
}

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
    
    // Map opening validation variables
    private int initialMapCount;
    private bool mapCountChecked = false;
    private bool mapOpeningRetried = false;
    private const double TickIntervalSeconds = 0.5;

    // Dungeon state tracking (Phase 8)
    private int dungeonFloor;
    private bool dungeonEntryProcessed; // True once we've confirmed we're inside the dungeon
    private uint? excludedDoorEntityId; // Door we gave up on (stuck), try others
    private DateTime doorStuckStart = DateTime.MinValue; // When we started trying current door
    private Vector3? lastDoorOpenedPosition = null; // Position of door that just opened (walk through it)
    private DateTime doorWalkThroughStart = DateTime.MinValue; // When we started walking through opened door
    private DateTime lastDungeonLogTime = DateTime.MinValue; // Throttle object logging
    private uint lastTerritoryId; // Track territory changes for floor transitions
    private DateTime forwardMovementStart = DateTime.MinValue; // When we started moving forward after territory change
    private uint lastGlobalTerritoryId; // Track territory changes globally for map refresh
    private DateTime chestDisappearedTime = DateTime.MinValue; // Track when chest first disappeared for grace period
    private DateTime chestApproachStart = DateTime.MinValue; // When we started approaching chest (stuck detection)
    private float chestApproachLastDist = 0f; // Distance when we started approaching (stuck detection)
    private HashSet<uint> attemptedCoffers = new HashSet<uint>(); // Track which coffers we've tried to interact with
    private DateTime cofferNavigationStart = DateTime.MinValue; // When we started navigating to current coffer
    private uint currentCofferId = 0; // Track which chest we're currently working on (preserved during combat)
    private uint lastBMRTerritoryId = 0; // Track territory for BMR activation
    private Dictionary<uint, DateTime> sphereInteractionTimes = new Dictionary<uint, DateTime>(); // Track sphere interactions to prevent spam
    private HashSet<uint> failedSpheres = new HashSet<uint>(); // Track spheres that didn't trigger combat/despawn

    // Dungeon objective tracking
    private DungeonObjective currentObjective = DungeonObjective.ClearingChests;
    private HashSet<uint> processedChests = new HashSet<uint>();
    private HashSet<uint> processedSpheres = new HashSet<uint>();
    private HashSet<uint> failedObjects = new HashSet<uint>(); // Unified failed object tracking
    private DateTime lastCombatEndTime = DateTime.MinValue;
    private const float OBJECTIVE_SEARCH_RADIUS = 80f;
    private const int COMBAT_FREE_WAIT_SECONDS = 5;
    private DateTime dungeonLoadWaitStart = DateTime.MinValue; // Wait for objects to become targetable on entry

    private DateTime mountAttemptStart = DateTime.MinValue; // Track mount retry timing
    private int mountAttempts = 0; // Track mount retry count
    private DateTime lastDungeonInteractionTime = DateTime.MinValue; // Prevent interaction spam on dungeon objects
    private int dungeonInteractionAttemptCount = 0; // Cycle between interaction methods
    private DateTime _lastSweepLogTime = DateTime.MinValue; // Throttle sweep log spam
    private Vector3 dungeonNavLastPos = Vector3.Zero; // Stuck detection: last position during dungeon nav
    private DateTime dungeonNavLastCheckTime = DateTime.MinValue; // Stuck detection: last check time
    private float dungeonNavLastDist = float.MaxValue; // Stuck detection: last distance to target
    private bool previouslyInCombat = false; // Proper combat edge detection
    private bool dungeonStartNavigating; // True while navigating to dungeon start position
    private bool doorTransitionNavigating; // True while navigating through a door transition point
    private bool dungeonStartChecked; // True once we've evaluated dungeon start on first entry
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

        // Enable BMR on territory changes (never turn off)
        if (lastBMRTerritoryId != 0 && lastBMRTerritoryId != currentTerritory)
        {
            CommandHelper.SendCommand("/bmrai on");
            _plugin.AddDebugLog($"[BMR] Enabled on territory change: {lastBMRTerritoryId} -> {currentTerritory}");
        }
        lastBMRTerritoryId = currentTerritory;

        // Universal combat pathfinding stop - only stop when actually in combat
        if (Plugin.Condition[ConditionFlag.InCombat] && autoMoveActive)
        {
            _plugin.NavigationService.StopNavigation();
            autoMoveActive = false;
        }
        
        // Combat start/end tracking for objective system
        bool currentlyInCombat = Plugin.Condition[ConditionFlag.InCombat];
        
        if (currentlyInCombat && !previouslyInCombat)
        {
            // Combat just started
            OnCombatStart();
        }
        else if (!currentlyInCombat && previouslyInCombat)
        {
            // Combat just ended
            OnCombatEnd();
        }
        previouslyInCombat = currentlyInCombat;

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

        // Check if already in dungeon and start objective system
        bool inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                      Plugin.Condition[ConditionFlag.BoundByDuty56];
        
        if (inDuty)
        {
            _plugin.AddDebugLog("[Start] Already in dungeon - starting objective system");
            RetryCount = 0;
            CurrentLocation = null;
            SelectedMapItemId = 0;
            _plugin.YesAlreadyIPC.Pause();
            _plugin.AddDebugLog($"[Start] YesAlready paused: {_plugin.YesAlreadyIPC.IsPaused}");
            
            // Initialize dungeon state
            dungeonEntryProcessed = false;
            dungeonFloor = 1; // Assume floor 1 if starting mid-dungeon
            
            // Reset objective tracking
            currentObjective = DungeonObjective.ClearingChests;
            dungeonLoadWaitStart = DateTime.MinValue;
            processedChests.Clear();
            processedSpheres.Clear();
            failedObjects.Clear();
            sphereInteractionTimes.Clear();
            _plugin.AddDebugLog("[Start] Dungeon objectives reset for mid-dungeon start");
            
            // Skip entry logic - go directly to looting
            dungeonEntryProcessed = true; // Skip initial entry logic
            TransitionTo(BotState.DungeonLooting, "Starting in dungeon - looking for chests within 80y...");
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
        
        // Initialize map count validation variables
        initialMapCount = _plugin.InventoryService.GetMapCount(SelectedMapItemId);
        mapCountChecked = false;
        mapOpeningRetried = false;
        _plugin.AddDebugLog($"[SelectingMap] Initial map count: {initialMapCount}");
        
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
        // Check if map opening failed by validating map count decreased
        if (!mapCountChecked)
        {
            var currentCount = _plugin.InventoryService.GetMapCount(SelectedMapItemId);
            _plugin.AddDebugLog($"[DetectingLocation] Map count check: {currentCount} (was {initialMapCount})");
            
            if (currentCount >= initialMapCount)
            {
                // Map count didn't decrease - opening failed, retry once
                if (!mapOpeningRetried)
                {
                    _plugin.AddDebugLog($"[DetectingLocation] Map count didn't decrease - opening failed, retrying...");
                    mapOpeningRetried = true;
                    mapCountChecked = true; // Don't check again on retry
                    TransitionTo(BotState.OpeningMap, "Retrying map opening...");
                    return;
                }
                else
                {
                    // Already retried once - handle as error
                    _plugin.AddDebugLog($"[DetectingLocation] Map opening failed after retry - treating as error");
                    HandleError("Map opening failed - map count didn't decrease");
                    return;
                }
            }
            else
            {
                _plugin.AddDebugLog($"[DetectingLocation] Map count decreased - opening successful");
                mapCountChecked = true;
            }
        }

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
            // Successfully mounted - reset counters and proceed
            mountAttemptStart = DateTime.MinValue;
            mountAttempts = 0;
            
            var partySize = Plugin.PartyList.Length;
            if (partySize > 0 && _plugin.Configuration.WaitForParty)
                TransitionTo(BotState.WaitingForParty, "Waiting for party to mount...");
            else
                TransitionTo(BotState.Flying, "Mounted! Flying to location...");
            return;
        }

        // Try mounting up to 3 times with 2s delays
        if (mountAttemptStart == DateTime.MinValue)
        {
            mountAttemptStart = DateTime.Now;
            mountAttempts = 0;
        }

        var mountElapsed = (DateTime.Now - mountAttemptStart).TotalSeconds;

        if (mountAttempts < 3)
        {
            if (mountElapsed >= mountAttempts * 2.0) // 0s, 2s, 4s
            {
                mountAttempts++;
                _plugin.AddDebugLog($"[Mounting] Attempt {mountAttempts}/3 to mount");
                nav.MountUp();
            }
            StateDetail = $"Mounting (attempt {mountAttempts}/3)...";
            return;
        }
        else
        {
            _plugin.AddDebugLog($"[Mounting] Failed to mount after 3 attempts - resetting bot");
            mountAttemptStart = DateTime.MinValue;
            mountAttempts = 0;
            TransitionTo(BotState.Idle, "Mount failed - please restart");
            return;
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
            
            // Wait 2 seconds for chest to spawn before looking for it
            System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ => {
                TransitionTo(BotState.OpeningChest, "Looking for treasure coffer to interact...");
            });
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
            // In combat - stop movement and clear target so we're not chained to chest
            if (autoMoveActive)
            {
                GameHelpers.StopAutoMove();
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
                chestApproachStart = DateTime.MinValue;
                _plugin.AddDebugLog($"[OpeningChest] Combat detected - stopped automove, clearing target");
            }
            // Clear target so player can fight freely
            if (Plugin.TargetManager.Target?.Name.ToString() == chestName)
            {
                Plugin.TargetManager.Target = null;
            }
            StateDetail = $"In combat - waiting ({dist:F1}y from '{chestName}')...";
            return;
        }

        // Not in combat - approach and interact with chest using lockon+automove
        Plugin.TargetManager.Target = chest;
        
        if (dist > range)
        {
            if (!autoMoveActive)
            {
                _plugin.AddDebugLog($"[OpeningChest] Coffer '{chestName}' at {dist:F1}y - lockon+automove");
                GameHelpers.LockOnAndAutoMove();
                autoMoveActive = true;
                chestApproachStart = now;
                chestApproachLastDist = dist;
            }
            else
            {
                // Stuck detection: if we've been approaching for 5s+ and haven't closed 2y, use vnavmesh
                var approachElapsed = (now - chestApproachStart).TotalSeconds;
                if (approachElapsed >= 5.0 && dist >= chestApproachLastDist - 2f)
                {
                    _plugin.AddDebugLog($"[OpeningChest] Stuck at {dist:F1}y for {approachElapsed:F0}s (was {chestApproachLastDist:F1}y) - switching to vnavmesh");
                    GameHelpers.StopAutoMove();
                    autoMoveActive = false;
                    _plugin.NavigationService.MoveToPosition(chest.Position);
                    autoMoveActive = true;
                    chestApproachStart = now;
                    chestApproachLastDist = dist;
                }
            }
            StateDetail = $"Approaching '{chestName}' ({dist:F1}y away)...";
        }
        else
        {
            // In range - stop automove
            if (autoMoveActive)
            {
                GameHelpers.StopAutoMove();
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
            }
            chestApproachStart = DateTime.MinValue;
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

    private void OnCombatStart()
    {
        lastCombatEndTime = DateTime.MinValue; // Reset combat end time
        
        // Clear all failed objects when combat starts
        if (failedObjects.Count > 0)
        {
            _plugin.AddDebugLog($"[Combat] Clearing {failedObjects.Count} failed object(s) - combat started");
            failedObjects.Clear();
        }
        
        _plugin.AddDebugLog("[Combat] Combat started - objective system reset");
    }
    
    private void OnCombatEnd()
    {
        lastCombatEndTime = DateTime.Now;
        
        // ALWAYS reset to chest priority after combat
        currentObjective = DungeonObjective.ClearingChests;
        dungeonLoadWaitStart = DateTime.MinValue;
        
        _plugin.AddDebugLog("[Combat] Combat ended - will clear processed objects in 5s");
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
            lastDoorOpenedPosition = null;
            doorWalkThroughStart = DateTime.MinValue;
            forwardMovementStart = DateTime.MinValue; // Reset forward movement timer
        }
        lastTerritoryId = currentTerritory;

        // First time in dungeon
        if (!dungeonEntryProcessed)
        {
            dungeonFloor = 1;
            dungeonEntryProcessed = true;
            dungeonStartChecked = false;
            dungeonStartNavigating = false;
            doorTransitionNavigating = false;
            attemptedCoffers.Clear();
            failedSpheres.Clear(); // Clear failed spheres on new dungeon entry
            sphereInteractionTimes.Clear(); // Reset sphere interaction tracking
            _plugin.AddDebugLog($"[InDungeon] First entry confirmed - floor {dungeonFloor}");
            _plugin.AddDebugLog($"[InDungeon] Territory: {currentTerritory}, BoundByDuty: {inDuty}");
        }

        // Check ejection
        if (!inDuty && (DateTime.Now - stateStartTime).TotalSeconds > 5)
        {
            _plugin.AddDebugLog($"[Dungeon] No longer bound by duty - ejected after floor {dungeonFloor}");
            dungeonEntryProcessed = false;
            TransitionTo(BotState.Completed, $"Dungeon complete (reached floor {dungeonFloor})");
            return;
        }

        // Check for combat
        bool inCombat = Plugin.Condition[ConditionFlag.InCombat];
        if (inCombat)
        {
            _plugin.AddDebugLog($"[InDungeon] Combat detected on floor {dungeonFloor} - preserving {attemptedCoffers.Count} attempted coffers");
            // DO NOT clear attemptedCoffers - preserve sweep progress across combat
            cofferNavigationStart = DateTime.MinValue;
            dungeonStartNavigating = false;
            doorTransitionNavigating = false;
            if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
            TransitionTo(BotState.DungeonCombat, $"Combat detected on floor {dungeonFloor}...");
            return;
        }

        // Check for card game addon → skip with "Open Chest"
        if (TrySkipCardGame())
            return;

        // After territory change or first entry: navigate to known dungeon start/door transition point
        if (territoryChanged || (dungeonFloor == 1 && !dungeonStartChecked))
        {
            var sphere = FindArcaneSphere();
            if (sphere != null)
            {
                // Arcane Sphere found - transition to DungeonLooting which does sweep (chests first, THEN progression)
                if (!failedSpheres.Contains(sphere.EntityId))
                {
                    _plugin.AddDebugLog($"[Dungeon] Arcane Sphere detected on entry - transitioning to DungeonLooting (sweep chests first)");
                    dungeonStartNavigating = false;
                    doorTransitionNavigating = false;
                    dungeonStartChecked = true;
                    TransitionTo(BotState.DungeonLooting, $"Looting floor {dungeonFloor} (sweep then progression)...");
                    return;
                }
                else
                {
                    _plugin.AddDebugLog($"[Dungeon] Skipping failed Arcane Sphere after territory change (EntityId: {sphere.EntityId})");
                }
            }

            dungeonStartChecked = true; // Mark as evaluated so we don't re-trigger every tick

            // Check if we have known location data for this territory
            if (dungeonFloor == 1 && DungeonLocationData.HasDungeonData(currentTerritory))
            {
                // First floor: navigate to dungeon start position
                var startPoint = DungeonLocationData.GetDungeonStart(currentTerritory);
                if (startPoint != null)
                {
                    _plugin.AddDebugLog($"[Dungeon] Known dungeon start for territory {currentTerritory}: '{startPoint.Label}' - navigating via vnavmesh");
                    dungeonStartNavigating = true;
                    doorTransitionNavigating = false;
                }
            }
            else if (territoryChanged && DungeonLocationData.HasDungeonData(currentTerritory))
            {
                // Floor transition: check if we're near a known door transition point
                var player = Plugin.ObjectTable.LocalPlayer;
                if (player != null)
                {
                    var doorPoint = DungeonLocationData.FindNearestDoorTransition(currentTerritory, player.Position, 10f);
                    if (doorPoint != null)
                    {
                        _plugin.AddDebugLog($"[Dungeon] Near door transition '{doorPoint.Label}' - will navigate after ready");
                        doorTransitionNavigating = true;
                        dungeonStartNavigating = false;
                    }
                    else
                    {
                        _plugin.AddDebugLog($"[Dungeon] No door transition within 10y - using forward movement fallback");
                        forwardMovementStart = DateTime.Now;
                    }
                }
            }
            else if (territoryChanged)
            {
                // No known data - fallback to forward movement
                _plugin.AddDebugLog($"[Dungeon] No location data for territory {currentTerritory} - moving forward for 10s");
                forwardMovementStart = DateTime.Now;
            }
        }

        // Handle dungeon start navigation
        if (dungeonStartNavigating)
        {
            if (!IsCharacterReady())
            {
                StateDetail = $"Waiting for character ready (dungeon start)...";
                return;
            }

            var startPoint = DungeonLocationData.GetDungeonStart(currentTerritory);
            if (startPoint != null)
            {
                var player = Plugin.ObjectTable.LocalPlayer;
                if (player != null)
                {
                    var dist = Vector3.Distance(player.Position, startPoint.Position);
                    if (dist > 3f)
                    {
                        if (!autoMoveActive)
                        {
                            _plugin.AddDebugLog($"[Dungeon] Navigating to dungeon start '{startPoint.Label}' at {dist:F1}y");
                            _plugin.NavigationService.MoveToPosition(startPoint.Position);
                            autoMoveActive = true;
                        }
                        StateDetail = $"Navigating to dungeon start ({dist:F1}y)...";
                        return;
                    }
                    else
                    {
                        _plugin.AddDebugLog($"[Dungeon] Reached dungeon start '{startPoint.Label}'");
                        if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
                        dungeonStartNavigating = false;
                    }
                }
            }
            else
            {
                dungeonStartNavigating = false;
            }
        }

        // Handle door transition navigation
        if (doorTransitionNavigating)
        {
            if (!IsCharacterReady())
            {
                StateDetail = $"Waiting for character ready (door transition)...";
                return;
            }

            var player = Plugin.ObjectTable.LocalPlayer;
            if (player != null)
            {
                var doorPoint = DungeonLocationData.FindNearestDoorTransition(currentTerritory, player.Position, 15f);
                if (doorPoint != null)
                {
                    var dist = Vector3.Distance(player.Position, doorPoint.Position);
                    if (dist > 3f)
                    {
                        if (!autoMoveActive)
                        {
                            _plugin.AddDebugLog($"[Dungeon] Navigating to door transition '{doorPoint.Label}' at {dist:F1}y");
                            _plugin.NavigationService.MoveToPosition(doorPoint.Position);
                            autoMoveActive = true;
                        }
                        StateDetail = $"Navigating through door transition ({dist:F1}y)...";
                        return;
                    }
                    else
                    {
                        _plugin.AddDebugLog($"[Dungeon] Reached door transition '{doorPoint.Label}'");
                        if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
                        doorTransitionNavigating = false;
                    }
                }
                else
                {
                    _plugin.AddDebugLog($"[Dungeon] Door transition point no longer in range - done");
                    if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
                    doorTransitionNavigating = false;
                }
            }
        }

        // Fallback: forward movement for dungeons without location data
        if (forwardMovementStart != DateTime.MinValue)
        {
            var forwardElapsed = (DateTime.Now - forwardMovementStart).TotalSeconds;
            if (forwardElapsed < 10.0)
            {
                if ((int)forwardElapsed % 1 == 0)
                {
                    CommandHelper.SendCommand("/automove on");
                }
                StateDetail = $"Moving forward to trigger area shift... ({forwardElapsed:F0}/10s)";
                return;
            }
            else
            {
                CommandHelper.SendCommand("/automove off");
                forwardMovementStart = DateTime.MinValue;
                _plugin.AddDebugLog($"[Dungeon] Forward movement complete");
            }
        }

        // Check for Arcane Spheres - if found, transition to DungeonLooting (sweep chests first)
        var progressionSphere = FindArcaneSphere();
        if (progressionSphere != null && !failedSpheres.Contains(progressionSphere.EntityId))
        {
            _plugin.AddDebugLog($"[Dungeon] Arcane Sphere found - transitioning to DungeonLooting (sweep chests first)");
            TransitionTo(BotState.DungeonLooting, $"Looting floor {dungeonFloor} (sweep then progression)...");
            return;
        }

        // Scan for chest/coffer/sack objects (loot) - includes ObjectKind.Treasure (PandorasBox pattern)
        _plugin.AddDebugLog($"[InDungeon] Scanning for chest objects on floor {dungeonFloor}...");
        var chestObjects = Plugin.ObjectTable
            .Where(obj =>
            {
                if (obj == null) return false;
                
                // Treasure objects (coffers/sacks) - PandorasBox pattern
                if (obj.ObjectKind == ObjectKind.Treasure)
                {
                    return obj.IsTargetable && !attemptedCoffers.Contains(obj.EntityId);
                }
                
                // Also check EventObj for named chests
                if (obj.ObjectKind != ObjectKind.EventObj) return false;
                var name = obj.Name.ToString();
                if (string.IsNullOrEmpty(name)) return false;
                var lower = name.ToLowerInvariant();
                bool isChest = new[] { "treasure", "coffer", "chest", "sack" }.Any(l => lower.Contains(l));
                bool isSphere = lower.Contains("arcane sphere");
                return isChest && !isSphere && !attemptedCoffers.Contains(obj.EntityId);
            })
            .ToList();
            
        if (chestObjects.Count > 0)
        {
            _plugin.AddDebugLog($"[InDungeon] Found {chestObjects.Count} chest object(s) (Treasure+EventObj), transitioning to DungeonLooting");
            TransitionTo(BotState.DungeonLooting, $"Found {chestObjects.Count} chest object(s) on floor {dungeonFloor}...");
            return;
        }
        else
        {
            _plugin.AddDebugLog($"[InDungeon] No chest objects found");
            
            // Edge case: if we're at the flag location but no chests exist, retry digging
            // This handles interrupted /gaction dig from combat aggro
            if (CurrentLocation != null && CurrentLocation.TerritoryId == currentTerritory)
            {
                var player = Plugin.ObjectTable.LocalPlayer;
                if (player != null)
                {
                    var flagPosition = new Vector3(CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z);
                    var distToFlag = Vector3.Distance(player.Position, flagPosition);
                    if (distToFlag < 30f) // Within reasonable range of flag location
                    {
                        _plugin.AddDebugLog($"[InDungeon] At flag location ({distToFlag:F1}y) but no chests - digging was likely interrupted");
                        
                        bool currentlyInCombat = Plugin.Condition[ConditionFlag.InCombat];
                        if (currentlyInCombat)
                        {
                            _plugin.AddDebugLog($"[InDungeon] In combat at flag location - will finish combat then retry digging");
                            // Stay in combat state, combat end logic will handle retry
                            return;
                        }
                        else
                        {
                            _plugin.AddDebugLog($"[InDungeon] Not in combat at flag location - retrying dig now");
                            // Retry digging immediately
                            CommandHelper.SendCommand("/gaction dig");
                            _plugin.AddDebugLog($"[InDungeon] Dig command sent, waiting for chests to spawn...");
                            return;
                        }
                    }
                }
            }
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
            dungeonEntryProcessed = false;
            TransitionTo(BotState.Completed, $"Dungeon complete (wiped on floor {dungeonFloor})");
            return;
        }

        // During combat - let BMR handle targeting
        bool inCombat = Plugin.Condition[ConditionFlag.InCombat];

        if (inCombat)
        {
            StateDetail = $"In combat on floor {dungeonFloor} - BMR handling...";
            return;
        }

        // Combat ended - post-combat cleanup and return to preserved chest
        _plugin.AddDebugLog($"[DungeonCombat] Combat ended on floor {dungeonFloor}");
        
        // Clear failed spheres - combat success means spheres should now be targetable
        if (failedSpheres.Count > 0)
        {
            _plugin.AddDebugLog($"[DungeonCombat] Clearing {failedSpheres.Count} failed Arcane Sphere(s) - combat ended successfully");
            failedSpheres.Clear();
            sphereInteractionTimes.Clear();
        }

        // Wait for enemies to despawn (2s grace period)
        var combatEndElapsed = (DateTime.Now - stateStartTime).TotalSeconds;
        if (combatEndElapsed < 2.0)
        {
            StateDetail = $"Combat ended - waiting for despawn... ({combatEndElapsed:F1}/2.0s)";
            return;
        }

        // Check if we were working on a specific chest before combat
        if (currentCofferId != 0)
        {
            var preservedChest = Plugin.ObjectTable.FirstOrDefault(obj => obj != null && obj.EntityId == currentCofferId);
            if (preservedChest != null && IsObjectTargetable(preservedChest))
            {
                _plugin.AddDebugLog($"[DungeonCombat] Returning to preserved chest '{preservedChest.Name}' (EntityId: {currentCofferId})");
                TransitionTo(BotState.DungeonLooting, $"Returning to chest after combat on floor {dungeonFloor}...");
                return;
            }
            else
            {
                _plugin.AddDebugLog($"[DungeonCombat] Preserved chest {currentCofferId} no longer available - clearing");
                currentCofferId = 0;
            }
        }
        
        // Check for any loot objects
        var lootObjects = FindDungeonObjects(lootOnly: true);
        if (lootObjects.Count > 0)
        {
            _plugin.AddDebugLog($"[DungeonCombat] Combat ended - {lootObjects.Count} loot object(s) found");
            TransitionTo(BotState.DungeonLooting, $"Looting after combat on floor {dungeonFloor}...");
            return;
        }
        
        // No loot found - continue dungeon progression
        _plugin.AddDebugLog($"[DungeonCombat] No loot after combat - continuing dungeon");
        TransitionTo(BotState.InDungeon, $"No loot after combat - continuing dungeon on floor {dungeonFloor}...");
        
        // Edge case: if we're at flag location but no chests exist after combat, retry digging
        // This handles interrupted /gaction dig from combat aggro
        if (CurrentLocation != null && CurrentLocation.TerritoryId == Plugin.ClientState.TerritoryType)
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player != null)
            {
                var flagPosition = new Vector3(CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z);
                    var distToFlag = Vector3.Distance(player.Position, flagPosition);
                if (distToFlag < 30f) // Within reasonable range of flag location
                {
                    _plugin.AddDebugLog($"[Dungeon] Combat ended at flag location ({distToFlag:F1}y) with no chests - retrying dig");
                    CommandHelper.SendCommand("/gaction dig");
                    _plugin.AddDebugLog($"[Dungeon] Dig command sent after combat, waiting for chests to spawn...");
                    return;
                }
            }
        }
        
        TransitionTo(BotState.InDungeon, $"Combat ended - scanning floor {dungeonFloor}...");
    }

    private void ProcessDungeonObjectives()
    {
        // Wait 5 seconds after combat before checking failed objects
        var combatFreeTime = (DateTime.Now - lastCombatEndTime).TotalSeconds;
        bool canCheckFailedObjects = combatFreeTime >= COMBAT_FREE_WAIT_SECONDS;
        
        // Clear all processed objects once 5 seconds after combat ends
        if (combatFreeTime >= COMBAT_FREE_WAIT_SECONDS && (processedChests.Count > 0 || processedSpheres.Count > 0))
        {
            processedChests.Clear();
            processedSpheres.Clear();
            // Reset combat end time so this doesn't run again
            lastCombatEndTime = DateTime.MinValue;
            _plugin.AddDebugLog("[Objective] Cleared all processed objects 5s after combat ended");
        }
        
        switch (currentObjective)
        {
            case DungeonObjective.ClearingChests:
                // ROOM SWEEP: Try ALL EventObj objects (coffers, sacks, any interactable)
                // Excludes progression objects (sluice gate, arcane sphere) and exit objects
                var sweepObjects = GetRoomSweepObjects();
                if (sweepObjects.Count > 0)
                {
                    dungeonLoadWaitStart = DateTime.MinValue; // Reset wait timer - we have objects
                    ProcessLootTarget(sweepObjects[0]);
                }
                else
                {
                    // No sweepable objects found. Before waiting, check if progression objects are already targetable.
                    // This avoids waiting 30s for unnamed scenery that will never become targetable.
                    var earlyProgression = GetProgressionObjects();
                    if (earlyProgression.Count > 0)
                    {
                        _plugin.AddDebugLog($"[Objective] No sweep objects but {earlyProgression.Count} progression object(s) already targetable - skipping to progression");
                        dungeonLoadWaitStart = DateTime.MinValue;
                        currentObjective = DungeonObjective.ProcessingSpheres;
                        break;
                    }
                    
                    // Check if there are untargetable objects nearby (still loading/activating)
                    int untargetableCount = CountNearbyUntargetableObjects();
                    if (untargetableCount > 0)
                    {
                        // Objects exist but aren't targetable yet - WAIT
                        if (dungeonLoadWaitStart == DateTime.MinValue)
                        {
                            dungeonLoadWaitStart = DateTime.Now;
                            _plugin.AddDebugLog($"[Objective] Found {untargetableCount} untargetable objects - waiting for them to load...");
                        }
                        var waitTime = (DateTime.Now - dungeonLoadWaitStart).TotalSeconds;
                        if (waitTime > 30.0)
                        {
                            _plugin.AddDebugLog($"[Objective] Waited {waitTime:F0}s for objects to load - giving up, moving to progression");
                            dungeonLoadWaitStart = DateTime.MinValue;
                            currentObjective = DungeonObjective.ProcessingSpheres;
                        }
                        else
                        {
                            StateDetail = $"Waiting for dungeon objects to activate ({waitTime:F0}/30s)...";
                        }
                    }
                    else
                    {
                        // Truly no objects - move to progression
                        dungeonLoadWaitStart = DateTime.MinValue;
                        currentObjective = DungeonObjective.ProcessingSpheres;
                        _plugin.AddDebugLog("[Objective] Room sweep complete - moving to progression");
                    }
                }
                break;
                
            case DungeonObjective.ProcessingSpheres:
                // Before targeting progression, double-check for loot that may have spawned late
                var lateLoot = FindDungeonObjects(lootOnly: true);
                if (lateLoot.Count > 0)
                {
                    _plugin.AddDebugLog($"[Objective] Found {lateLoot.Count} loot object(s) while in ProcessingSpheres - going back to ClearingChests");
                    currentObjective = DungeonObjective.ClearingChests;
                    dungeonLoadWaitStart = DateTime.MinValue;
                    break; // Re-enter switch on next tick as ClearingChests
                }
                
                // Look for progression: Sluice Gate, Arcane Sphere, doors (High/Low)
                var progressionObjects = GetProgressionObjects();
                if (progressionObjects.Count > 0 && canCheckFailedObjects)
                {
                    dungeonLoadWaitStart = DateTime.MinValue;
                    ProcessLootTarget(progressionObjects[0]);
                }
                else
                {
                    // Check if we already used an Arcane Sphere on this floor
                    // If so, transition to DungeonProgressing which handles door finding
                    bool sphereWasUsed = attemptedCoffers.Any(id =>
                    {
                        var obj = Plugin.ObjectTable.FirstOrDefault(o => o != null && o.EntityId == id);
                        if (obj == null)
                        {
                            // Object gone from table - check processedSpheres
                            return processedSpheres.Contains(id);
                        }
                        var name = obj.Name.ToString().ToLowerInvariant();
                        return name.Contains("arcane sphere") || name.Contains("sluice");
                    }) || processedSpheres.Count > 0;
                    
                    if (sphereWasUsed)
                    {
                        _plugin.AddDebugLog("[Objective] Sphere/progression already used - transitioning to DungeonProgressing for door handling");
                        dungeonLoadWaitStart = DateTime.MinValue;
                        if (autoMoveActive) { GameHelpers.StopAutoMove(); autoMoveActive = false; }
                        TransitionTo(BotState.DungeonProgressing, $"Finding doors on floor {dungeonFloor}...");
                        return;
                    }
                    
                    // Check if progression objects exist but aren't targetable
                    int untargetableProgression = CountNearbyUntargetableProgressionObjects();
                    if (untargetableProgression > 0)
                    {
                        if (dungeonLoadWaitStart == DateTime.MinValue)
                        {
                            dungeonLoadWaitStart = DateTime.Now;
                            _plugin.AddDebugLog($"[Objective] Found {untargetableProgression} untargetable progression objects - waiting...");
                        }
                        var waitTime = (DateTime.Now - dungeonLoadWaitStart).TotalSeconds;
                        if (waitTime > 30.0)
                        {
                            _plugin.AddDebugLog($"[Objective] Waited {waitTime:F0}s for progression objects - transitioning to DungeonProgressing");
                            dungeonLoadWaitStart = DateTime.MinValue;
                            TransitionTo(BotState.DungeonProgressing, $"Finding doors on floor {dungeonFloor}...");
                            return;
                        }
                        else
                        {
                            StateDetail = $"Waiting for progression objects to activate ({waitTime:F0}/30s)...";
                        }
                    }
                    else
                    {
                        // No progression objects at all - try DungeonProgressing (has broader search)
                        dungeonLoadWaitStart = DateTime.MinValue;
                        _plugin.AddDebugLog("[Objective] No progression objects found - transitioning to DungeonProgressing");
                        TransitionTo(BotState.DungeonProgressing, $"Finding doors on floor {dungeonFloor}...");
                        return;
                    }
                }
                break;
                
            case DungeonObjective.HeadingToExit:
                // Use existing exit logic in TickCompleted()
                TransitionTo(BotState.Completed, "All objectives cleared - heading to exit");
                break;
        }
    }
    
    private List<IGameObject> FindChestsInRadius(float radius)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return new List<IGameObject>();
        
        var chests = new List<IGameObject>();
        var playerPos = player.Position;
        var currentChestIds = new HashSet<uint>();
        
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null || obj.EntityId == 0) continue;
            
            // PandorasBox pattern: coffers are ObjectKind.Treasure
            if (obj.ObjectKind == ObjectKind.Treasure)
            {
                if (!obj.IsTargetable) continue;
                currentChestIds.Add(obj.EntityId);
                var dist = Vector3.Distance(playerPos, obj.Position);
                if (dist <= radius && !processedChests.Contains(obj.EntityId))
                {
                    chests.Add(obj);
                }
                continue;
            }
            
            // Also check EventObj for named chests
            if (obj.ObjectKind != ObjectKind.EventObj) continue;
            
            var objName = obj.Name.ToString();
            if (string.IsNullOrEmpty(objName)) continue;
            
            var lower = objName.ToLowerInvariant();
            bool isChest = new[] { "treasure", "coffer", "chest", "sack" }.Any(l => lower.Contains(l));
            bool isSphere = lower.Contains("arcane sphere");
            
            if (isChest && !isSphere)
            {
                currentChestIds.Add(obj.EntityId);
                var dist = Vector3.Distance(playerPos, obj.Position);
                if (dist <= radius && !processedChests.Contains(obj.EntityId))
                {
                    chests.Add(obj);
                }
            }
        }
        
        // Note: processedChests are only added when chests actually despawn
        // This prevents re-targeting chests that are already opened/being processed
        
        return chests;
    }
    
    private List<IGameObject> FindSpheresInRadius(float radius)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return new List<IGameObject>();
        
        var spheres = new List<IGameObject>();
        var playerPos = player.Position;
        
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null || obj.EntityId == 0) continue;
            
            var objName = obj.Name.TextValue;
            if (string.IsNullOrEmpty(objName)) continue;
            
            // Check for sphere/door names
            bool isSphere = objName.Contains("Arcane Sphere");
            bool isDoor = objName.Contains("Door");
            
            if ((isSphere || isDoor) && !failedObjects.Contains(obj.EntityId))
            {
                var dist = Vector3.Distance(playerPos, obj.Position);
                if (dist <= radius && !processedSpheres.Contains(obj.EntityId))
                {
                    spheres.Add(obj);
                }
            }
        }
        
        return spheres;
    }
    
    private void ProcessChests(List<IGameObject> chests)
    {
        // Use existing chest processing logic - just pick the nearest one
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;
        
        var nearestChest = chests.OrderBy(c => Vector3.Distance(player.Position, c.Position)).FirstOrDefault();
        if (nearestChest != null)
        {
            ProcessLootTarget(nearestChest);
        }
    }
    
    private void ProcessSpheres(List<IGameObject> spheres)
    {
        // Use existing sphere processing logic
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;
        
        var nearestSphere = spheres.OrderBy(s => Vector3.Distance(player.Position, s.Position)).FirstOrDefault();
        if (nearestSphere != null)
        {
            ProcessLootTarget(nearestSphere);
        }
    }

    private void TickDungeonLooting()
    {
        GameHelpers.ClickYesIfVisible();

        bool loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                       Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (loading)
        {
            // Loading screen during looting = floor transition (roulette/door triggered it)
            dungeonFloor++;
            excludedDoorEntityId = null;
            doorStuckStart = DateTime.MinValue;
            lastDoorOpenedPosition = null;
            doorWalkThroughStart = DateTime.MinValue;
            currentObjective = DungeonObjective.ClearingChests;
            dungeonLoadWaitStart = DateTime.MinValue;
            if (autoMoveActive) { GameHelpers.StopAutoMove(); autoMoveActive = false; }
            _plugin.AddDebugLog($"[DungeonLooting] Loading screen detected - advancing to floor {dungeonFloor}");
            TransitionTo(BotState.InDungeon, $"Entering floor {dungeonFloor}...");
            return;
        }

        // Check ejection
        bool inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                      Plugin.Condition[ConditionFlag.BoundByDuty56];
        if (!inDuty)
        {
            _plugin.AddDebugLog($"[Dungeon] Ejected during looting on floor {dungeonFloor}");
            dungeonEntryProcessed = false;
            TransitionTo(BotState.Completed, $"Dungeon complete (floor {dungeonFloor})");
            return;
        }

        // Do not interact during combat - transition to DungeonCombat
        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            if (autoMoveActive) { CommandHelper.SendCommand("/automove off"); autoMoveActive = false; }
            if (currentCofferId != 0)
            {
                _plugin.AddDebugLog($"[DungeonLooting] Combat started - preserving chest {currentCofferId}, transitioning to DungeonCombat");
            }
            else
            {
                _plugin.AddDebugLog($"[DungeonLooting] Combat started, transitioning to DungeonCombat");
            }
            TransitionTo(BotState.DungeonCombat, $"Combat on floor {dungeonFloor}...");
            return;
        }

        // Check card game
        if (TrySkipCardGame())
            return;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        // NEW OBJECTIVE SYSTEM: Sequential processing with priority hierarchy
        // Called every tick - ProcessLootTarget has its own 2s interaction cooldown
        ProcessDungeonObjectives();
    }

    private void ProcessLootTarget(IGameObject target)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var dist = Vector3.Distance(player.Position, target.Position);
        var targetName = target.Name.ToString();
        var targetId = target.EntityId;

        // Track which object we're working on (preserved during combat)
        if (currentCofferId != targetId)
        {
            currentCofferId = targetId;
            cofferNavigationStart = DateTime.Now;
            dungeonInteractionAttemptCount = 0;
            dungeonNavLastPos = player.Position;
            dungeonNavLastCheckTime = DateTime.Now;
            dungeonNavLastDist = dist;
            _plugin.AddDebugLog($"[DungeonLooting] Now targeting '{targetName}' Kind={target.ObjectKind} at {dist:F1}y (EntityId: {targetId})");
        }

        // Check if object became untargetable (interaction succeeded - it despawned/opened)
        if (!target.IsTargetable)
        {
            _plugin.AddDebugLog($"[DungeonLooting] '{targetName}' is no longer targetable - interaction succeeded!");
            attemptedCoffers.Add(targetId);
            cofferNavigationStart = DateTime.MinValue;
            currentCofferId = 0;
            if (autoMoveActive) { GameHelpers.StopAutoMove(); autoMoveActive = false; }
            if (_plugin.NavigationService.State == NavigationState.Flying) _plugin.NavigationService.StopNavigation();
            return;
        }

        // Check timeout (60s per object - marks attempted and moves to next)
        var navigationTime = (DateTime.Now - cofferNavigationStart).TotalSeconds;
        if (navigationTime > 60.0)
        {
            _plugin.AddDebugLog($"[DungeonLooting] Timeout on '{targetName}' after {navigationTime:F0}s - marking attempted, moving to next");
            attemptedCoffers.Add(targetId);
            cofferNavigationStart = DateTime.MinValue;
            currentCofferId = 0;
            if (autoMoveActive) { GameHelpers.StopAutoMove(); autoMoveActive = false; }
            if (_plugin.NavigationService.State == NavigationState.Flying) _plugin.NavigationService.StopNavigation();
            return;
        }
        
        // NAVIGATION + INTERACTION using proven OpeningChest pattern:
        // >6y: vnavmesh navigation
        // 3-6y: lockon+automove approach (vnavmesh can't path closer)
        // <3y: stop movement, interact
        if (dist > 6f)
        {
            // Stop any existing automove before starting vnavmesh navigation
            if (autoMoveActive) { GameHelpers.StopAutoMove(); autoMoveActive = false; }
            
            // Use vnavmesh to navigate to distant objects
            if (_plugin.NavigationService.State != NavigationState.Flying)
            {
                _plugin.AddDebugLog($"[DungeonLooting] Navigating to '{targetName}' at {dist:F1}y via vnavmesh");
                _plugin.NavigationService.MoveToPosition(target.Position);
                dungeonNavLastPos = player.Position;
                dungeonNavLastCheckTime = DateTime.Now;
                dungeonNavLastDist = dist;
            }
            
            // Stuck detection: every 10s check if bot moved <3y, if so re-issue nav command
            // (proven pattern from TickFlying stuck detection)
            var sinceNavCheck = (DateTime.Now - dungeonNavLastCheckTime).TotalSeconds;
            if (sinceNavCheck >= 10.0)
            {
                var movedDistance = Vector3.Distance(player.Position, dungeonNavLastPos);
                if (movedDistance < 3.0f)
                {
                    // Stuck! Re-issue nav command
                    _plugin.AddDebugLog($"[DungeonLooting] Stuck detected (moved {movedDistance:F1}y in 10s, dist={dist:F1}y) - re-pathfinding to '{targetName}'");
                    _plugin.NavigationService.StopNavigation();
                    _plugin.NavigationService.MoveToPosition(target.Position);
                }
                else
                {
                    _plugin.AddDebugLog($"[DungeonLooting] Nav progress: moved {movedDistance:F1}y in 10s, dist={dist:F1}y to '{targetName}' ({navigationTime:F0}s elapsed)");
                }
                dungeonNavLastPos = player.Position;
                dungeonNavLastCheckTime = DateTime.Now;
                dungeonNavLastDist = dist;
            }
            
            StateDetail = $"Navigating to '{targetName}' ({dist:F1}y, {navigationTime:F0}s)...";
        }
        else if (dist > 3f)
        {
            // 3-6y: Stop vnavmesh, use lockon+automove to close the gap (proven pattern)
            if (_plugin.NavigationService.State == NavigationState.Flying)
            {
                _plugin.NavigationService.StopNavigation();
                _plugin.AddDebugLog($"[DungeonLooting] Stopped vnavmesh at {dist:F1}y - using lockon+automove to approach '{targetName}'");
            }
            
            // Lockon+automove approach (same as OpeningChest proven pattern)
            Plugin.TargetManager.Target = target;
            if (!autoMoveActive)
            {
                GameHelpers.LockOnAndAutoMove();
                autoMoveActive = true;
            }
            
            // ALSO attempt interaction while approaching (proven TickOpeningChest pattern)
            // Many objects can be interacted from 4-6y range
            if ((DateTime.Now - lastDungeonInteractionTime).TotalSeconds >= 2.0)
            {
                lastDungeonInteractionTime = DateTime.Now;
                dungeonInteractionAttemptCount++;
                Plugin.TargetManager.Target = target;
                
                // Cycle interaction methods for redundancy
                if (dungeonInteractionAttemptCount % 2 == 1)
                {
                    // Odd attempts: TargetSystem.InteractWithObject (PandorasBox pattern)
                    _plugin.AddDebugLog($"[DungeonLooting] Interact attempt #{dungeonInteractionAttemptCount} (TargetSystem) with '{targetName}' at {dist:F1}y");
                    GameHelpers.InteractWithObject(target);
                }
                else
                {
                    // Even attempts: /interact command (game native)
                    _plugin.AddDebugLog($"[DungeonLooting] Interact attempt #{dungeonInteractionAttemptCount} (/interact) with '{targetName}' at {dist:F1}y");
                    CommandHelper.SendCommand("/interact");
                }
            }
            StateDetail = $"Approaching+interacting '{targetName}' ({dist:F1}y, attempt #{dungeonInteractionAttemptCount})...";
        }
        else
        {
            // Within 3y - stop ALL movement and interact
            if (autoMoveActive)
            {
                GameHelpers.StopAutoMove();
                autoMoveActive = false;
            }
            if (_plugin.NavigationService.State == NavigationState.Flying)
            {
                _plugin.NavigationService.StopNavigation();
            }

            // Interact every 2 seconds (continuous retry until despawn or timeout)
            if ((DateTime.Now - lastDungeonInteractionTime).TotalSeconds >= 2.0)
            {
                lastDungeonInteractionTime = DateTime.Now;
                dungeonInteractionAttemptCount++;
                Plugin.TargetManager.Target = target;
                
                // Cycle interaction methods for redundancy
                if (dungeonInteractionAttemptCount % 2 == 1)
                {
                    // Odd attempts: TargetSystem.InteractWithObject (PandorasBox pattern)
                    _plugin.AddDebugLog($"[DungeonLooting] Interact attempt #{dungeonInteractionAttemptCount} (TargetSystem) with '{targetName}' Kind={target.ObjectKind} at {dist:F1}y");
                    GameHelpers.InteractWithObject(target);
                }
                else
                {
                    // Even attempts: /interact command (game native)
                    _plugin.AddDebugLog($"[DungeonLooting] Interact attempt #{dungeonInteractionAttemptCount} (/interact) with '{targetName}' at {dist:F1}y");
                    CommandHelper.SendCommand("/interact");
                }
                
                // Track progression objects for state management (but do NOT mark attempted)
                var lower = targetName.ToLowerInvariant();
                if (lower.Contains("arcane sphere"))
                {
                    processedSpheres.Add(targetId);
                }
                else if (lower.Contains("sluice") || lower.Contains("gate") || lower.Contains("door"))
                {
                    processedSpheres.Add(targetId);
                }
            }
            
            StateDetail = $"Interacting with '{targetName}' ({dist:F1}y, attempt #{dungeonInteractionAttemptCount})...";
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
            lastDoorOpenedPosition = null;
            doorWalkThroughStart = DateTime.MinValue;
            if (autoMoveActive)
            {
                CommandHelper.SendCommand("/automove off");
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
            }
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
            dungeonEntryProcessed = false;
            TransitionTo(BotState.Completed, $"Dungeon complete (floor {dungeonFloor})");
            return;
        }

        // Do not interact during combat - wait for combat to end
        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            if (autoMoveActive) { CommandHelper.SendCommand("/automove off"); autoMoveActive = false; }
            StateDetail = $"In combat on floor {dungeonFloor} - waiting...";
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
            currentObjective = DungeonObjective.ClearingChests; // Reset so sweep finds the new loot
            dungeonLoadWaitStart = DateTime.MinValue;
            _plugin.AddDebugLog($"[DungeonProgressing] Found {lootObjects.Count} loot object(s) - resetting to ClearingChests sweep");
            TransitionTo(BotState.DungeonLooting, $"More loot found on floor {dungeonFloor}...");
            return;
        }

        // Walk-through phase: after a door opens, navigate to its transition point
        if (lastDoorOpenedPosition.HasValue)
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return;

            if (doorWalkThroughStart == DateTime.MinValue)
                doorWalkThroughStart = DateTime.Now;

            var walkElapsed = (DateTime.Now - doorWalkThroughStart).TotalSeconds;

            // Timeout: if we haven't triggered BetweenAreas in 15s, give up and rescan
            if (walkElapsed > 15.0)
            {
                _plugin.AddDebugLog($"[Dungeon] Door walk-through timeout after {walkElapsed:F0}s - rescanning");
                if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
                lastDoorOpenedPosition = null;
                doorWalkThroughStart = DateTime.MinValue;
                TransitionTo(BotState.InDungeon, $"Rescanning floor {dungeonFloor}...");
                return;
            }

            var distToDoor = Vector3.Distance(player.Position, lastDoorOpenedPosition.Value);
            if (distToDoor > 2f)
            {
                if (!autoMoveActive)
                {
                    _plugin.AddDebugLog($"[Dungeon] Walking through opened door at {distToDoor:F1}y");
                    _plugin.NavigationService.MoveToPosition(lastDoorOpenedPosition.Value);
                    autoMoveActive = true;
                }
                StateDetail = $"Walking through opened door ({distToDoor:F1}y)...";
            }
            else
            {
                // At the door position - use automove forward to push through the transition zone
                if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
                if (!autoMoveActive)
                {
                    CommandHelper.SendCommand("/automove on");
                    autoMoveActive = true;
                }
                StateDetail = $"Pushing through door transition ({walkElapsed:F0}s)...";
            }
            return;
        }

        // Find progression objects (any interactable EventObj that isn't loot)
        var progressionObjects = FindDungeonObjects(lootOnly: false);
        if (progressionObjects.Count == 0)
        {
            // No progression objects found - check if a door was recently opened
            // (attemptedCoffers will have filtered it out of FindDungeonObjects)
            if (doorStuckStart != DateTime.MinValue)
            {
                // We were tracking a door that's now gone → it opened!
                // Find nearest door transition point from DungeonLocationData
                var player = Plugin.ObjectTable.LocalPlayer;
                if (player != null)
                {
                    var currentTerritory = Plugin.ClientState.TerritoryType;
                    var doorTransition = DungeonLocationData.FindNearestDoorTransition(currentTerritory, player.Position, 50f);
                    if (doorTransition != null)
                    {
                        _plugin.AddDebugLog($"[Dungeon] Door opened! Walking to transition '{doorTransition.Label}' at {Vector3.Distance(player.Position, doorTransition.Position):F1}y");
                        lastDoorOpenedPosition = doorTransition.Position;
                    }
                    else
                    {
                        _plugin.AddDebugLog($"[Dungeon] Door opened but no known transition point - using automove forward");
                        // Fallback: move forward from current position for 10s
                        lastDoorOpenedPosition = player.Position + new Vector3(0, 0, -10f); // Forward approximation
                    }
                    doorStuckStart = DateTime.MinValue;
                    doorWalkThroughStart = DateTime.MinValue; // Will be set next tick
                    return;
                }
            }

            // Nothing found and no recent door - wait then rescan
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

        // Reset walk-through state when we have a valid target
        lastDoorOpenedPosition = null;
        doorWalkThroughStart = DateTime.MinValue;

        var target = progressionObjects[0]; // Nearest progression object
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) return;

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

            // Use ProcessLootTarget for multi-method interaction cycling
            // (InteractWithObject + /interact alternating, 3-phase approach, stuck detection)
            ProcessLootTarget(target);
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
                    lastDoorOpenedPosition = null;
                    doorWalkThroughStart = DateTime.MinValue;
                    
                    // Reset objective tracking for new dungeon
                    currentObjective = DungeonObjective.ClearingChests;
                    dungeonLoadWaitStart = DateTime.MinValue;
                    processedChests.Clear();
                    processedSpheres.Clear();
                    failedObjects.Clear();
                    sphereInteractionTimes.Clear();
                    _plugin.AddDebugLog("[Objective] New dungeon entry - all objectives reset");
                    
                    TransitionTo(BotState.InDungeon, "Entering dungeon instance...");
                    return;
                }

                var portal = FindNearestPortal();

                if (portal != null)
                {
                    // Double-check portal is still targetable before attempting to move
                    if (!IsObjectTargetable(portal))
                    {
                        _plugin.AddDebugLog($"[Portal] Portal became untargetable - waiting...");
                        return;
                    }
                    
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

    // ─── Room Sweep Methods (brute-force object interaction) ─────────────────

    /// <summary>
    /// Returns ALL targetable loot objects in the room: Treasure (coffers/sacks) + non-progression EventObj.
    /// PandorasBox pattern: coffers are ObjectKind.Treasure, NOT EventObj.
    /// Objects that can't be interacted with will timeout and be marked attempted.
    /// </summary>
    private List<IGameObject> GetRoomSweepObjects()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return new List<IGameObject>();

        // Names to EXCLUDE from sweep (progression + exit - handled in later phase)
        var excludePartial = new[] { "sluice", "arcane sphere", "teleportation portal" };
        var excludeExact = new[] { "exit" };

        // Scan BOTH Treasure (coffers/sacks) AND EventObj (non-progression interactables)
        var allSweepable = Plugin.ObjectTable
            .Where(obj => obj != null && 
                   (obj.ObjectKind == ObjectKind.Treasure || obj.ObjectKind == ObjectKind.EventObj))
            .ToList();

        // Throttle verbose sweep logging to once per 10 seconds
        var sweepLogNow = DateTime.Now;
        bool shouldLogSweepDetails = (sweepLogNow - _lastSweepLogTime).TotalSeconds >= 10.0;
        if (shouldLogSweepDetails)
        {
            _lastSweepLogTime = sweepLogNow;
            _plugin.AddDebugLog($"[Sweep] Scanning {allSweepable.Count} Treasure+EventObj objects in room...");
            foreach (var obj in allSweepable.Take(10))
            {
                var d = Vector3.Distance(player.Position, obj.Position);
                _plugin.AddDebugLog($"[Sweep]   '{obj.Name}' Kind={obj.ObjectKind} at {d:F1}y (ID:{obj.EntityId}, Targetable:{obj.IsTargetable})");
            }
            if (allSweepable.Count > 10)
                _plugin.AddDebugLog($"[Sweep]   ... and {allSweepable.Count - 10} more");
        }

        var candidates = allSweepable
            .Where(obj =>
            {
                // Treasure objects (coffers/sacks) are ALWAYS included in sweep
                if (obj.ObjectKind == ObjectKind.Treasure)
                {
                    var dist = Vector3.Distance(player.Position, obj.Position);
                    if (dist > 50f) return false;
                    if (attemptedCoffers.Contains(obj.EntityId)) return false;
                    return true;
                }

                // EventObj: filter out progression/exit/empty names
                var name = obj.Name.ToString();
                if (string.IsNullOrEmpty(name)) return false;
                var edist = Vector3.Distance(player.Position, obj.Position);
                if (edist > 50f) return false;

                var lower = name.ToLowerInvariant();
                if (excludePartial.Any(p => lower.Contains(p))) return false;
                if (excludeExact.Any(e => lower == e)) return false;
                if (attemptedCoffers.Contains(obj.EntityId)) return false;

                return true;
            })
            .Where(obj => obj.IsTargetable) // Only targetable objects (filters ghosts/opened)
            .OrderBy(obj => Vector3.Distance(player.Position, obj.Position))
            .ToList();

        _plugin.AddDebugLog($"[Sweep] Found {candidates.Count} sweepable objects (excludes progression/exit/attempted)");
        foreach (var obj in candidates)
        {
            var d = Vector3.Distance(player.Position, obj.Position);
            _plugin.AddDebugLog($"[Sweep]   → '{obj.Name}' Kind={obj.ObjectKind} at {d:F1}y (ID:{obj.EntityId})");
        }

        return candidates;
    }

    /// <summary>
    /// Returns progression objects: Sluice Gate, Arcane Sphere, doors (High/Low).
    /// Called after room sweep is complete.
    /// </summary>
    private List<IGameObject> GetProgressionObjects()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return new List<IGameObject>();

        var progressionPartial = new[] { "sluice", "arcane sphere", "door", "gate", "high", "low", "exit" };
        // Exclude loot-sounding names that might false-match (e.g. "Sluice Gate" is progression, not "Treasure Coffer")
        var excludePartial = new[] { "treasure", "coffer", "chest", "sack", "teleportation portal" };

        return Plugin.ObjectTable
            .Where(obj =>
            {
                if (obj == null || obj.ObjectKind != ObjectKind.EventObj) return false;
                var name = obj.Name.ToString();
                if (string.IsNullOrEmpty(name)) return false;
                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist > 50f) return false;

                var lower = name.ToLowerInvariant();
                if (excludePartial.Any(p => lower.Contains(p))) return false;
                if (!progressionPartial.Any(p => lower.Contains(p))) return false;

                // Skip already attempted
                if (attemptedCoffers.Contains(obj.EntityId)) return false;

                return true;
            })
            .Where(obj => obj.IsTargetable)
            .OrderBy(obj =>
            {
                // Priority: Arcane Sphere first, then Sluice Gate, then doors by distance
                var name = obj.Name.ToString().ToLowerInvariant();
                if (name.Contains("arcane sphere")) return 0;
                if (name.Contains("sluice")) return 1;
                return 2 + (int)Vector3.Distance(player.Position, obj.Position);
            })
            .ToList();
    }

    /// <summary>
    /// Counts nearby Treasure/EventObj objects that exist but are NOT targetable.
    /// Used to detect objects still loading on dungeon entry.
    /// </summary>
    private int CountNearbyUntargetableObjects()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return 0;

        return Plugin.ObjectTable.Count(obj =>
            obj != null &&
            (obj.ObjectKind == ObjectKind.Treasure || obj.ObjectKind == ObjectKind.EventObj) &&
            !obj.IsTargetable &&
            Vector3.Distance(player.Position, obj.Position) <= 50f);
    }

    /// <summary>
    /// Counts nearby progression objects (Arcane Sphere, Sluice Gate) that exist but are NOT targetable.
    /// </summary>
    private int CountNearbyUntargetableProgressionObjects()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return 0;

        var progressionPartial = new[] { "sluice", "arcane sphere" };

        return Plugin.ObjectTable.Count(obj =>
        {
            if (obj == null || obj.ObjectKind != ObjectKind.EventObj) return false;
            if (obj.IsTargetable) return false; // Already targetable = handled by GetProgressionObjects
            if (attemptedCoffers.Contains(obj.EntityId)) return false; // Already used/attempted
            if (processedSpheres.Contains(obj.EntityId)) return false; // Already processed
            var name = obj.Name.ToString();
            if (string.IsNullOrEmpty(name)) return false;
            var dist = Vector3.Distance(player.Position, obj.Position);
            if (dist > 50f) return false;
            var lower = name.ToLowerInvariant();
            return progressionPartial.Any(p => lower.Contains(p));
        });
    }

    // ─── Dungeon Helpers ─────────────────────────────────────────────────────

    private bool IsDungeonState() =>
        State == BotState.InDungeon || State == BotState.DungeonCombat ||
        State == BotState.DungeonLooting || State == BotState.DungeonProgressing;

    private bool IsCharacterReady()
    {
        // Character is ready when: not in cutscene, not casting, not loading
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return false;
        if (player.IsCasting) return false;
        if (Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]) return false;
        if (Plugin.Condition[ConditionFlag.Occupied33]) return false;
        if (Plugin.Condition[ConditionFlag.BetweenAreas]) return false;
        if (Plugin.Condition[ConditionFlag.BetweenAreas51]) return false;
        return true;
    }

    private bool IsObjectTargetable(IGameObject obj)
    {
        // Verify object can actually be targeted (not a ghost object)
        // Quick check without blocking delays
        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            _plugin.AddDebugLog($"[ObjectCheck] Skipping targeting check for '{obj.Name}' during combat");
            return true;
        }
        
        try
        {
            var previousTarget = Plugin.TargetManager.Target;
            Plugin.TargetManager.Target = obj;
            var canTarget = Plugin.TargetManager.Target?.EntityId == obj.EntityId;
            Plugin.TargetManager.Target = previousTarget;
            
            if (canTarget)
            {
                _plugin.AddDebugLog($"[ObjectCheck] '{obj.Name}' targetable");
                return true;
            }
        }
        catch (Exception ex)
        {
            _plugin.AddDebugLog($"[ObjectCheck] Exception: {ex.Message}");
        }
        
        _plugin.AddDebugLog($"[ObjectCheck] '{obj.Name}' not targetable - skipping");
        return false;
    }

    private IGameObject? FindArcaneSphere()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return null;

        return Plugin.ObjectTable
            .FirstOrDefault(obj =>
                obj != null &&
                obj.ObjectKind == ObjectKind.EventObj &&
                obj.Name.ToString().Contains("Arcane Sphere", StringComparison.OrdinalIgnoreCase));
    }

    private List<IGameObject> FindDungeonObjects(bool lootOnly)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return new List<IGameObject>();

        _plugin.AddDebugLog($"[Dungeon] FindDungeonObjects(lootOnly={lootOnly}) - scanning all objects...");
        
        // Priority: Arcane Sphere, then loot (treasure/coffer/chest/sack), then progression (doors/gates)
        var lootNames = new[] { "treasure", "coffer", "chest", "sack" };
        var sphereName = "arcane sphere";
        var doorNames = new[] { "door", "gate" }; // Partial matching for doors (Sluice Gate, etc)

        // Log all Treasure + EventObj objects for debugging
        var allDungeonObjs = Plugin.ObjectTable
            .Where(obj => obj != null && (obj.ObjectKind == ObjectKind.Treasure || obj.ObjectKind == ObjectKind.EventObj))
            .ToList();
        
        _plugin.AddDebugLog($"[Dungeon] Found {allDungeonObjs.Count} Treasure+EventObj objects total");
        foreach (var obj in allDungeonObjs.Take(10))
        {
            var dist = Vector3.Distance(player.Position, obj.Position);
            _plugin.AddDebugLog($"[Dungeon]   {obj.ObjectKind}: '{obj.Name}' at {dist:F1}y (EntityId: {obj.EntityId}, Targetable: {obj.IsTargetable})");
        }
        if (allDungeonObjs.Count > 10)
        {
            _plugin.AddDebugLog($"[Dungeon]   ... and {allDungeonObjs.Count - 10} more objects");
        }

        // First pass: find all UNOPENED loot objects within 50y for door priority check
        // Includes ObjectKind.Treasure (PandorasBox pattern) + EventObj named chests
        var allLoot = Plugin.ObjectTable
            .Where(obj =>
            {
                if (obj == null) return false;
                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist > 50f) return false;
                
                // Treasure objects are always loot
                if (obj.ObjectKind == ObjectKind.Treasure)
                    return obj.IsTargetable && !attemptedCoffers.Contains(obj.EntityId);
                
                if (obj.ObjectKind != ObjectKind.EventObj) return false;
                var name = obj.Name.ToString();
                if (string.IsNullOrEmpty(name)) return false;
                
                var lower = name.ToLowerInvariant();
                // Only actual loot names count - Arcane Sphere is progression, NOT loot
                bool isLoot = lootNames.Any(l => lower.Contains(l));
                if (!isLoot) return false;
                
                if (!obj.IsTargetable) return false; // Must be targetable (opened coffers have IsTargetable=false)
                return !attemptedCoffers.Contains(obj.EntityId);
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
                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist > 50f) return false;

                // Include Treasure objects (coffers/sacks) when looking for loot
                if (obj.ObjectKind == ObjectKind.Treasure)
                    return lootOnly && obj.IsTargetable && !attemptedCoffers.Contains(obj.EntityId);

                // EventObj type (interactive dungeon objects)
                if (obj.ObjectKind != ObjectKind.EventObj) return false;
                var name = obj.Name.ToString();
                
                // Skip the teleportation portal (handled separately)
                if (name == "Teleportation Portal") return false;
                
                // Exclude any door we gave up on (stuck)
                if (excludedDoorEntityId.HasValue && obj.EntityId == excludedDoorEntityId.Value)
                    return false;

                // Handle unnamed EventObj: potential doors in some dungeons (e.g. territory 794)
                // Only include unnamed objects for progression (not loot), within 30y, and targetable
                if (string.IsNullOrEmpty(name))
                {
                    if (lootOnly) return false; // Unnamed objects are never loot
                    if (dist > 30f) return false; // Tighter radius for unnamed objects
                    if (attemptedCoffers.Contains(obj.EntityId)) return false;
                    if (hasNearbyLoot) return false; // Don't pick doors while loot exists
                    return true; // Unnamed targetable EventObj = likely a door
                }

                var lower = name.ToLowerInvariant();
                bool isSphere = lower.Contains(sphereName);
                bool isLoot = lootNames.Any(l => lower.Contains(l));
                bool isDoor = doorNames.Any(d => lower.Contains(d));

                if (lootOnly)
                    return isLoot && obj.IsTargetable; // Only actual targetable loot (opened coffers have IsTargetable=false)

                // For progression: return doors/gates, but NOT if loot exists within 50y
                if (isSphere || isLoot) return false;
                
                // Don't return doors if there's loot within 50y (other rooms)
                if (isDoor && hasNearbyLoot)
                {
                    _plugin.AddDebugLog($"[Dungeon] Skipping door '{name}' - loot within 50y");
                    return false;
                }

                // Filter out objects we've already attempted
                if (attemptedCoffers.Contains(obj.EntityId)) return false;
                if (!obj.IsTargetable) return false;
                return true;
            })
            .Where(obj => IsObjectTargetable(obj))
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
