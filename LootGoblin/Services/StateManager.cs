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
    private DateTime lastDungeonLogTime = DateTime.MinValue; // Throttle object logging
    private uint lastTerritoryId; // Track territory changes for floor transitions
    private DateTime forwardMovementStart = DateTime.MinValue; // When we started moving forward after territory change
    private uint lastGlobalTerritoryId; // Track territory changes globally for map refresh
    private DateTime chestDisappearedTime = DateTime.MinValue; // Track when chest first disappeared for grace period
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

    private DateTime mountAttemptStart = DateTime.MinValue; // Track mount retry timing
    private int mountAttempts = 0; // Track mount retry count
    private DateTime lastDungeonInteractionTime = DateTime.MinValue; // Prevent interaction spam on dungeon objects
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
        bool wasInCombat = lastCombatEndTime != DateTime.MinValue;
        
        if (currentlyInCombat && !wasInCombat)
        {
            // Combat just started
            OnCombatStart();
        }
        else if (!currentlyInCombat && wasInCombat)
        {
            // Combat just ended
            OnCombatEnd();
        }

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
            _plugin.AddDebugLog($"[InDungeon] Combat detected on floor {dungeonFloor} - resetting attempted coffers ({attemptedCoffers.Count})");
            attemptedCoffers.Clear();
            // Don't clear failed spheres - they should remain failed until chests are cleared
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
                // Skip spheres that have failed to trigger combat/despawn
                if (failedSpheres.Contains(sphere.EntityId))
                {
                    _plugin.AddDebugLog($"[Dungeon] Skipping failed Arcane Sphere after territory change (EntityId: {sphere.EntityId})");
                }
                else
                {
                    _plugin.AddDebugLog($"[Dungeon] Arcane Sphere detected - processing as progression object");
                    dungeonStartNavigating = false;
                    doorTransitionNavigating = false;
                    ProcessLootTarget(sphere); // Process sphere directly
                    return;
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

        // Check for Arcane Spheres first (progression objects)
        var progressionSphere = FindArcaneSphere();
        if (progressionSphere != null)
        {
            // Skip spheres that have failed to trigger combat/despawn
            if (failedSpheres.Contains(progressionSphere.EntityId))
            {
                _plugin.AddDebugLog($"[Dungeon] Skipping failed Arcane Sphere (EntityId: {progressionSphere.EntityId})");
            }
            else
            {
                _plugin.AddDebugLog($"[Dungeon] Arcane Sphere found - processing as progression object");
                ProcessLootTarget(progressionSphere);
                return;
            }
        }

        // Scan for chest/coffer/sack objects (loot)
        _plugin.AddDebugLog($"[InDungeon] Scanning for chest objects on floor {dungeonFloor}...");
        var chestObjects = Plugin.ObjectTable
            .Where(obj =>
            {
                if (obj == null || obj.ObjectKind != ObjectKind.EventObj) return false;
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
            _plugin.AddDebugLog($"[InDungeon] Found {chestObjects.Count} chest object(s), transitioning to DungeonLooting");
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
                    ProcessLootTarget(sweepObjects[0]);
                }
                else
                {
                    // All objects swept - move to progression
                    currentObjective = DungeonObjective.ProcessingSpheres;
                    _plugin.AddDebugLog("[Objective] Room sweep complete - moving to progression");
                }
                break;
                
            case DungeonObjective.ProcessingSpheres:
                // Look for progression: Sluice Gate, Arcane Sphere, doors (High/Low)
                var progressionObjects = GetProgressionObjects();
                if (progressionObjects.Count > 0 && canCheckFailedObjects)
                {
                    ProcessLootTarget(progressionObjects[0]);
                }
                else
                {
                    // No progression objects - head to exit
                    currentObjective = DungeonObjective.HeadingToExit;
                    _plugin.AddDebugLog("[Objective] No progression objects found - heading to exit");
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
            if (obj.ObjectKind != ObjectKind.EventObj) continue;
            
            var objName = obj.Name.ToString();
            if (string.IsNullOrEmpty(objName)) continue;
            
            // Check for chest/coffer/sack names (using same logic as original)
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
            StateDetail = "Loading...";
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
        // Only process objectives every 3 seconds to prevent spam
        if ((DateTime.Now - lastDungeonInteractionTime).TotalSeconds >= 3.0)
        {
            ProcessDungeonObjectives();
        }
    }

    private void ProcessLootTarget(IGameObject target)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var dist = Vector3.Distance(player.Position, target.Position);
        var targetName = target.Name.ToString();
        var targetId = target.EntityId;

        _plugin.AddDebugLog($"[DungeonLooting] Processing target '{targetName}' at {dist:F1}y (EntityId: {targetId})");

        // Validate targetability (quick check)
        if (!IsObjectTargetable(target))
        {
            _plugin.AddDebugLog($"[DungeonLooting] '{targetName}' not targetable - skipping");
            return; // Will try next object on next tick
        }

        // Always set target
        Plugin.TargetManager.Target = target;
        
        // Track which chest we're working on (preserved during combat)
        if (currentCofferId != targetId)
        {
            currentCofferId = targetId;
            _plugin.AddDebugLog($"[DungeonLooting] Now working on chest '{targetName}' (EntityId: {targetId})");
        }
        
        // Start navigation timer if not already started for this coffer
        if (cofferNavigationStart == DateTime.MinValue)
        {
            cofferNavigationStart = DateTime.Now;
            _plugin.AddDebugLog($"[DungeonLooting] Starting navigation to '{targetName}' (EntityId: {targetId})");
        }

        // Check timeout (15s per object - marks attempted and moves to next)
        var navigationTime = (DateTime.Now - cofferNavigationStart).TotalSeconds;
        if (navigationTime > 15.0)
        {
            _plugin.AddDebugLog($"[DungeonLooting] Timeout on '{targetName}' after {navigationTime:F0}s - marking attempted, moving to next");
            attemptedCoffers.Add(targetId);
            cofferNavigationStart = DateTime.MinValue;
            currentCofferId = 0;
            if (autoMoveActive) { CommandHelper.SendCommand("/automove off"); autoMoveActive = false; }
            if (_plugin.NavigationService.State == NavigationState.Flying) _plugin.NavigationService.StopNavigation();
            return;
        }
        
        // NAVIGATION LOGIC: Use vnavmesh for distant objects, stop at 6y for interaction
        if (dist > 6f)
        {
            // Stop any existing automove before starting vnavmesh navigation
            if (autoMoveActive) { CommandHelper.SendCommand("/automove off"); autoMoveActive = false; }
            
            // Use vnavmesh to navigate to distant objects
            if (_plugin.NavigationService.State != NavigationState.Flying)
            {
                _plugin.AddDebugLog($"[DungeonLooting] Navigating to '{targetName}' at {dist:F1}y via vnavmesh");
                _plugin.NavigationService.MoveToPosition(target.Position);
            }
            StateDetail = $"Navigating to '{targetName}' ({dist:F1}y, {navigationTime:F0}s)...";
        }
        else
        {
            // Within 6y - stop ALL navigation and interact
            if (autoMoveActive)
            {
                CommandHelper.SendCommand("/automove off");
                autoMoveActive = false;
            }
            if (_plugin.NavigationService.State == NavigationState.Flying)
            {
                _plugin.NavigationService.StopNavigation();
                _plugin.AddDebugLog($"[DungeonLooting] Stopped navigation at {dist:F1}y - attempting interaction with '{targetName}'");
            }

            // Interact every 2 seconds
            if ((DateTime.Now - lastDungeonInteractionTime).TotalSeconds >= 2.0)
            {
                lastDungeonInteractionTime = DateTime.Now;
                
                // Route to targeting method
                switch (_plugin.Configuration.SelectedTargetingMethod)
                {
                    case TargetingMethod.Method2_IsTargetable:
                        InteractMethod2_IsTargetable(target, targetName, targetId);
                        break;
                    case TargetingMethod.Method3_ChatValidation:
                        InteractMethod3_ChatValidation(target, targetName, targetId);
                        break;
                    case TargetingMethod.Method1_Current:
                    default:
                        InteractMethod1_Current(target, targetName, targetId);
                        break;
                }
            }
            
            StateDetail = $"Interacting with '{targetName}'...";
        }
    }

    // ─── Targeting Method 1: Current (Chat + TargetManager + interact) ───
    private void InteractMethod1_Current(IGameObject target, string targetName, uint targetId)
    {
        string targetCommand = GetTargetCommand(targetName);
        
        SendChatCommand(targetCommand);
        Plugin.TargetManager.Target = target;
        _plugin.AddDebugLog($"[Method1] Targeted '{targetName}' - firing interact");
        
        TriggerControllerModeInteract();
        
        PostInteractionTracking(targetName, targetId);
    }

    // ─── Targeting Method 2: IsTargetable Filter (AutoDuty pattern) ───
    private void InteractMethod2_IsTargetable(IGameObject target, string targetName, uint targetId)
    {
        if (!target.IsTargetable)
        {
            _plugin.AddDebugLog($"[Method2] '{targetName}' NOT targetable (ghost/despawned) - marking attempted");
            attemptedCoffers.Add(targetId);
            cofferNavigationStart = DateTime.MinValue;
            currentCofferId = 0;
            return;
        }
        
        string targetCommand = GetTargetCommand(targetName);
        SendChatCommand(targetCommand);
        Plugin.TargetManager.Target = target;
        _plugin.AddDebugLog($"[Method2] Targeted '{targetName}' (IsTargetable=true) - firing interact");
        
        TriggerControllerModeInteract();
        
        PostInteractionTracking(targetName, targetId);
    }

    // ─── Targeting Method 3: Chat Command Validation ───
    private void InteractMethod3_ChatValidation(IGameObject target, string targetName, uint targetId)
    {
        // Clear target, send /target command, check if game acquired it
        Plugin.TargetManager.Target = null;
        string targetCommand = GetTargetCommand(targetName);
        SendChatCommand(targetCommand);
        
        var currentTarget = Plugin.TargetManager.Target;
        if (currentTarget == null)
        {
            _plugin.AddDebugLog($"[Method3] /target failed for '{targetName}' - marking attempted");
            attemptedCoffers.Add(targetId);
            cofferNavigationStart = DateTime.MinValue;
            currentCofferId = 0;
            return;
        }
        
        _plugin.AddDebugLog($"[Method3] Target validated: '{currentTarget.Name}' - firing interact");
        
        TriggerControllerModeInteract();
        
        PostInteractionTracking(targetName, targetId);
    }

    // ─── Shared Helpers for Targeting Methods ───
    private string GetTargetCommand(string targetName)
    {
        return targetName.ToLowerInvariant() switch
        {
            var name when name.Contains("coffer") => "/target coffer",
            var name when name.Contains("sack") => "/target sack",
            var name when name.Contains("chest") => "/target chest",
            var name when name.Contains("arcane") => "/target arcane",
            var name when name.Contains("sluice") => "/target sluice",
            _ => $"/target {targetName}"
        };
    }

    private void PostInteractionTracking(string targetName, uint targetId)
    {
        // Mark object as attempted after 3s delay (gives time for despawn/reaction)
        // The sweep will move to next object on the next tick
        Task.Run(async () =>
        {
            await Task.Delay(3000);
            if (!attemptedCoffers.Contains(targetId))
            {
                attemptedCoffers.Add(targetId);
                _plugin.AddDebugLog($"[Interaction] '{targetName}' marked attempted after delay (EntityId: {targetId})");
            }
        });
        
        // Reset navigation timer for this object
        cofferNavigationStart = DateTime.MinValue;
        currentCofferId = 0;
        
        // Specific tracking for progression objects
        var lower = targetName.ToLowerInvariant();
        if (lower.Contains("arcane sphere"))
        {
            processedSpheres.Add(targetId);
            if (!sphereInteractionTimes.ContainsKey(targetId))
            {
                sphereInteractionTimes[targetId] = DateTime.Now;
                _plugin.AddDebugLog($"[Interaction] First Arcane Sphere interaction (EntityId: {targetId})");
            }
        }
        else if (lower.Contains("sluice") || lower.Contains("gate") || lower.Contains("door"))
        {
            processedSpheres.Add(targetId);
            _plugin.AddDebugLog($"[Interaction] Progression object '{targetName}' interaction sent (EntityId: {targetId})");
        }
        
        _plugin.AddDebugLog($"[Interaction] Interaction sent for '{targetName}' - waiting for despawn or combat");
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
        
        // INTERACTION RANGE: 2y
        if (dist > 2f)
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
            // Within interaction range - stop movement and interact
            if (autoMoveActive)
            {
                if (dist < 10f)
                    CommandHelper.SendCommand("/automove off");
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
                    
                    // Reset objective tracking for new dungeon
                    currentObjective = DungeonObjective.ClearingChests;
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
    /// Returns ALL targetable EventObj objects in the room, excluding progression/exit objects.
    /// This is the "ritual sweep" - walk to each object and try to interact.
    /// Objects that can't be interacted with will timeout and be marked attempted.
    /// </summary>
    private List<IGameObject> GetRoomSweepObjects()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return new List<IGameObject>();

        // Names to EXCLUDE from sweep (progression + exit - handled in later phase)
        var excludePartial = new[] { "sluice", "arcane sphere", "teleportation portal" };
        var excludeExact = new[] { "exit" };

        var allEventObjs = Plugin.ObjectTable
            .Where(obj => obj != null && obj.ObjectKind == ObjectKind.EventObj)
            .ToList();

        _plugin.AddDebugLog($"[Sweep] Scanning {allEventObjs.Count} EventObj objects in room...");
        foreach (var obj in allEventObjs.Take(10))
        {
            var d = Vector3.Distance(player.Position, obj.Position);
            _plugin.AddDebugLog($"[Sweep]   '{obj.Name}' at {d:F1}y (ID:{obj.EntityId}, Targetable:{obj.IsTargetable})");
        }
        if (allEventObjs.Count > 10)
            _plugin.AddDebugLog($"[Sweep]   ... and {allEventObjs.Count - 10} more");

        var candidates = allEventObjs
            .Where(obj =>
            {
                var name = obj.Name.ToString();
                if (string.IsNullOrEmpty(name)) return false;
                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist > 50f) return false;

                var lower = name.ToLowerInvariant();

                // Skip progression/exit objects (saved for ProcessingSpheres phase)
                if (excludePartial.Any(p => lower.Contains(p))) return false;
                if (excludeExact.Any(e => lower == e)) return false;

                // Skip already attempted objects
                if (attemptedCoffers.Contains(obj.EntityId)) return false;

                return true;
            })
            .Where(obj => obj.IsTargetable) // Only targetable objects (filters ghosts)
            .OrderBy(obj => Vector3.Distance(player.Position, obj.Position))
            .ToList();

        _plugin.AddDebugLog($"[Sweep] Found {candidates.Count} sweepable objects (excludes progression/exit/attempted)");
        foreach (var obj in candidates)
        {
            var d = Vector3.Distance(player.Position, obj.Position);
            _plugin.AddDebugLog($"[Sweep]   → '{obj.Name}' at {d:F1}y (ID:{obj.EntityId})");
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

        var progressionPartial = new[] { "sluice", "arcane sphere" };

        return Plugin.ObjectTable
            .Where(obj =>
            {
                if (obj == null || obj.ObjectKind != ObjectKind.EventObj) return false;
                var name = obj.Name.ToString();
                if (string.IsNullOrEmpty(name)) return false;
                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist > 50f) return false;

                var lower = name.ToLowerInvariant();
                if (!progressionPartial.Any(p => lower.Contains(p))) return false;

                // Skip already attempted
                if (attemptedCoffers.Contains(obj.EntityId)) return false;

                return true;
            })
            .Where(obj => obj.IsTargetable)
            .OrderBy(obj => Vector3.Distance(player.Position, obj.Position))
            .ToList();
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
        var doorNames = new[] { "door", "gate", "sphere" }; // Partial matching for doors (Sluice Gate, etc)

        // Log all EventObj objects for debugging
        var allEventObjs = Plugin.ObjectTable
            .Where(obj => obj != null && obj.ObjectKind == ObjectKind.EventObj)
            .ToList();
        
        _plugin.AddDebugLog($"[Dungeon] Found {allEventObjs.Count} EventObj objects total");
        foreach (var obj in allEventObjs.Take(10)) // Limit to first 10 to avoid spam
        {
            var dist = Vector3.Distance(player.Position, obj.Position);
            _plugin.AddDebugLog($"[Dungeon]   EventObj: '{obj.Name}' at {dist:F1}y (EntityId: {obj.EntityId})");
        }
        if (allEventObjs.Count > 10)
        {
            _plugin.AddDebugLog($"[Dungeon]   ... and {allEventObjs.Count - 10} more EventObj objects");
        }

        // First pass: find all UNOPENED loot objects within 50y for door priority check
        var allLoot = Plugin.ObjectTable
            .Where(obj =>
            {
                if (obj == null || obj.ObjectKind != ObjectKind.EventObj) return false;
                var name = obj.Name.ToString();
                if (string.IsNullOrEmpty(name)) return false;
                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist > 50f) return false; // Check within 50y for door priority
                
                var lower = name.ToLowerInvariant();
                bool isLoot = lower.Contains(sphereName) || lootNames.Any(l => lower.Contains(l));
                if (!isLoot) return false;
                
                // Skip loot we've already attempted (opened/empty chests)
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
                var name = obj.Name.ToString();
                if (string.IsNullOrEmpty(name)) return false;
                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist > 50f) return false; // 50y interaction range

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
            .Where(obj => 
            {
                // Method 2: Use direct IsTargetable property (AutoDuty pattern - lightweight)
                if (_plugin.Configuration.SelectedTargetingMethod == TargetingMethod.Method2_IsTargetable)
                    return obj.IsTargetable;
                // Method 1 & 3: Use TargetManager-based check
                return IsObjectTargetable(obj);
            })
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

    /// <summary>
    /// Controller mode interaction sequence - exact copy of TriggerSelectIconStringClick from GameHelpers.cs.
    /// Uses numpad2 to switch to controller mode, then numpad0 twice to interact.
    /// </summary>
    private static async void TriggerControllerModeInteract()
    {
        try
        {
            // Controller mode sequence: numpad2, then numpad0 twice
            // Exact copy from GameHelpers.TriggerSelectIconStringClick
            GameHelpers.KeyPress(VirtualKey.NUMPAD2);
            
            await System.Threading.Tasks.Task.Delay(200);
            
            GameHelpers.KeyPress(VirtualKey.NUMPAD0);
            
            await System.Threading.Tasks.Task.Delay(200);
            
            GameHelpers.KeyPress(VirtualKey.NUMPAD0);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[DungeonLooting] Controller mode interact failed: {ex.Message}");
        }
    }

    private static unsafe void SendChatCommand(string command)
    {
        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule == null) return;
            
            var bytes = Encoding.UTF8.GetBytes(command);
            var utf8String = Utf8String.FromSequence(bytes);
            uiModule->ProcessChatBoxEntry(utf8String, nint.Zero);
        }
        catch (Exception ex)
        {
            // Log error if needed, but don't crash
        }
    }
}
