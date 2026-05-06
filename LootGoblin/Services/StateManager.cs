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
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace LootGoblin.Services;

public enum DungeonObjective
{
    ClearingChests,    // Level 1: Always start here
    ProcessingSpheres, // Level 2: After chests cleared OR if no chests exist
    HeadingToExit      // Level 3: After all objectives done
}

public enum OverworldLandingMode
{
    MountToggle,
    UnderwaterBounce,
}

internal enum SaddlebagRetrievalStep
{
    Idle,
    Opening,
    WaitingForAddon,
    WaitingStable,
    Moving,
    Confirming,
}

public class StateManager : IDisposable
{
    private readonly struct OverworldLandingPartyWaitResult
    {
        public bool CanProceed { get; init; }
        public int NearbyOthers { get; init; }
        public int RequiredOthers { get; init; }
        public int TotalOthers { get; init; }
        public int SameZoneFarCount { get; init; }
        public int OutOfZoneCount { get; init; }
    }

    private const uint ThiefMapItemId = 19770;
    private readonly Plugin _plugin;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;

    public BotState State { get; private set; } = BotState.Idle;
    public string StateDetail { get; private set; } = "";
    public string WarningMessage { get; private set; } = "";
    public bool IsPaused { get; private set; }
    public int RetryCount { get; private set; }
    public uint SelectedMapItemId { get; private set; }
    public MapLocation? CurrentLocation { get; private set; }

    private DateTime stateStartTime = DateTime.Now;
    private DateTime lastTickTime = DateTime.MinValue;
    private DateTime lastMapScanTime = DateTime.MinValue;
    private int mapScanCounter = 0; // Counter for reducing log spam
    private bool stateActionIssued;
    private Vector3 lastStuckCheckPos; // Position at last stuck check
    private DateTime lastStuckCheckTime = DateTime.MinValue; // Time of last stuck check
    private DateTime portalRetryStart = DateTime.MinValue; // Portal interaction retry timer
    private bool portalMapFlagCleared; // Clear old map/vnav flag once before portal interaction
    private Vector3? portalApproachPosition; // Exact portal XYZ captured for this portal window
    private DateTime lastPortalMountCommandTime = DateTime.MinValue; // Throttle portal mount attempts before fly pathing
    private DateTime portalLandingStartedAt = DateTime.MinValue; // Tracks portal <=3y landing/dismount handoff
    private DateTime lastPortalDismountCommandTime = DateTime.MinValue; // Throttle portal dismount attempts
    private uint openingChestCofferMountRecoveryEntityId; // Coffer being recovered with mounted approach
    private bool openingChestCofferMountRecoveryActive; // True while overworld chest recovery owns movement
    private bool openingChestCofferMountRecoveryRangeReached; // True after coffer recovery enters interaction handoff
    private DateTime lastOpeningChestCofferMountCommandTime = DateTime.MinValue; // Throttle coffer mount attempts
    private DateTime lastOpeningChestCofferDismountCommandTime = DateTime.MinValue; // Throttle coffer dismount attempts
    private DateTime portalApproachStartedAt = DateTime.MinValue; // Tracks progress for portal FlyToPosition
    private float portalApproachStartDistance = float.MaxValue; // Distance when current portal approach started
    private DateTime lastPortalRepathTime = DateTime.MinValue; // Rate-limit portal stop + repath recovery
    private bool portalRegularVnavPathLogged; // One-shot log for portal vnav-only approach path
    private DateTime lastPortalTimeoutHoldLogTime = DateTime.MinValue; // Throttle timeout hold logs while portal/duty still active
    private DateTime dismountAttemptStart = DateTime.MinValue; // When dismount first attempted at flag X,Z
    private bool descentInProgress = false; // Whether Ctrl+Space descent is currently running
    private DateTime descentStartTime = DateTime.MinValue; // When Ctrl+Space descent started
    private float descentStartY = 0f; // Y position when descent started
    private bool descentMode = false; // Whether we're in descent+dismount mode (Ctrl+Space first)
    private DateTime lastInteractionTime = DateTime.MinValue; // Throttle chest/portal interaction attempts
    private bool autoMoveActive; // Track if automove is currently on
    private bool pendingDungeonMapFlagClear; // Clear the overworld flag once dungeon entry has settled
    private bool treasureHighLowHandledThisOpen; // Prevent callback spam while puzzle addon stays open
    
    // Map opening validation variables
    private int initialMapCount;
    private bool mapCountChecked = false;
    private bool mapOpeningRetried = false;
    private const double TickIntervalSeconds = 0.5;
    private const double DungeonInteractionIntervalSeconds = 1.0;
    private const float MapDigXZRange = 5.0f;
    private const double SameZoneAetheryteTeleportSkipXZRange = 50.0;
    private const float PortalInteractionRange = 3.0f;
    private const float OpeningChestCofferMountRecoveryDistance = 3.0f;
    private const float OpeningChestCofferMountRecoveryYDelta = 0.5f;
    private const float CapturedLocationMatchXZRange = 10.0f;
    private const float UnderwaterBounceTriggerXZRange = 10.0f;
    private const float UnderwaterFlagApproachArrivalXZRange = 5.0f;
    private const float PortalRunawayDistanceIncrease = 2.0f;
    private static readonly TimeSpan UnderwaterBounceDescentInterval = TimeSpan.FromSeconds(1.25);
    private static readonly TimeSpan UnderwaterFlagApproachReissueInterval = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan PortalMountCommandInterval = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan PortalDismountCommandInterval = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan OpeningChestCofferMountCommandInterval = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan OpeningChestCofferDismountCommandInterval = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan KeyItemMapRecoveryTimeout = TimeSpan.FromSeconds(30.0);
    private static readonly TimeSpan KeyItemMapOpenRetryInterval = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan PortalRunawayCheckDelay = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan PortalRepathInterval = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan PortalSearchTimeout = TimeSpan.FromSeconds(15.0);
    private static readonly TimeSpan PortalActiveApproachTimeout = TimeSpan.FromSeconds(60.0);
    private static readonly TimeSpan TreasureHighLowSecondCallbackDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DoorTransitionReadyStabilization = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan SaddlebagAddonStableDelay = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan SaddlebagStepTimeout = TimeSpan.FromSeconds(20);
    private SaddlebagRetrievalStep saddlebagRetrievalStep = SaddlebagRetrievalStep.Idle;
    private DateTime saddlebagStepStartedAt = DateTime.MinValue;
    private DateTime saddlebagNextActionAt = DateTime.MinValue;
    private DateTime saddlebagAddonVisibleSince = DateTime.MinValue;
    private uint saddlebagTargetItemId;
    private int saddlebagInitialInventoryCount;
    private int saddlebagInitialSaddlebagCount;
    private SaddlebagMovePlan? saddlebagMovePlan;
    private static readonly TimeSpan StartMapRefreshSaddlebagTimeout = TimeSpan.FromSeconds(6.0);
    private bool startMapRefreshPending;
    private bool startMapRefreshOpenedSaddlebag;
    private DateTime startMapRefreshStartedAt = DateTime.MinValue;

    // Dungeon state tracking (Phase 8)
    private int dungeonFloor;
    private bool dungeonEntryProcessed; // True once we've confirmed we're inside the dungeon
    private uint? excludedDoorEntityId; // Door we gave up on (stuck), try others
    private DateTime doorStuckStart = DateTime.MinValue; // When we started trying current door
    private Vector3? lastDoorOpenedPosition = null; // Position of door that just opened (walk through it)
    private DateTime doorWalkThroughStart = DateTime.MinValue; // When we started walking through opened door
    private DateTime doorWalkThroughReadySince = DateTime.MinValue; // When character first looked ready after a door cutscene
    private DateTime lastDungeonLogTime = DateTime.MinValue; // Throttle object logging
    private uint lastTerritoryId; // Track territory changes for floor transitions
    private DateTime forwardMovementStart = DateTime.MinValue; // When we started moving forward after territory change
    private uint lastGlobalTerritoryId; // Track territory changes globally for map refresh
    private DateTime chestDisappearedTime = DateTime.MinValue; // Track when chest first disappeared for grace period
    private DateTime chestApproachStart = DateTime.MinValue; // When we started approaching chest (stuck detection)
    private float chestApproachLastDist = 0f; // Distance when we started approaching (stuck detection)
    private bool openingChestCombatInterrupted; // Combat started while recovering an overworld chest
    private bool openingChestRecoveryDigIssued; // One-shot dig retry after combat interruption
    private bool openingChestReturningToFlag; // One-shot path back to the flag after combat displacement
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
    private DateTime doorTransitionReadySince = DateTime.MinValue; // Ready settle timer after territory/door cutscenes
    private bool dungeonStartChecked; // True once we've evaluated dungeon start on first entry
    private bool doorWalkThroughBlockedLogged; // One-shot log while a fake-out cutscene still owns the player
    private bool doorTransitionReadyWaitLogged; // One-shot log while waiting for transition readiness
    private readonly MountService _mountService;
    private DateTime lastDiscardTime = DateTime.MinValue; // Auto-discard timer
    private DateTime lastDiscardDeferredLogTime = DateTime.MinValue;
    private string lastDiscardDeferredReason = string.Empty;
    private DateTime lastCompanionCheckTime = DateTime.MinValue; // Companion summoning timer
    private DateTime companionStanceDeferred = DateTime.MinValue; // Deferred stance set after summon
    private bool adsDutyHandoffActive; // True while ADS owns the dungeon phase for the current map
    private DateTime adsDutyHandoffStarted = DateTime.MinValue;
    private DateTime adsDutyEntryConfirmedAt = DateTime.MinValue;
    private DateTime adsDutyReadySince = DateTime.MinValue;
    private DateTime lastMapDutyOutsideDungeonLog = DateTime.MinValue;
    private bool adsOwnershipObserved;
    private DateTime adsInsideSentAt = DateTime.MinValue;
    private bool adsInsideRetrySent;
    private bool adsLeaveIssued;
    private bool adsUnreadableStatusLogged;
    private bool mountedRotationSuppressed;
    private OverworldLandingMode currentLandingMode = OverworldLandingMode.MountToggle;
    private string lastLandingPartyWaitSignature = string.Empty;
    private bool landingCommandsRanThisMap;
    private bool dutyEntryCommandsRanThisMap;
    private bool finishCommandsRanThisRun;
    private bool digIssuedThisMap;
    private DateTime digIssuedAt = DateTime.MinValue;
    private bool chestConfirmedThisMap;
    private bool portalConfirmedThisMap;
    private bool dungeonConfirmedThisMap;
    private DateTime keyItemMapRecoveryStartedAt = DateTime.MinValue;
    private DateTime keyItemMapNextOpenAttemptAt = DateTime.MinValue;
    private int keyItemMapOpenAttemptCount;
    private uint activeKeyItemMapItemId;
    private int activeKeyItemMapSlot = -1;
    private DateTime lastKeyItemCompletionGuardLogAt = DateTime.MinValue;
    private DateTime lastVnavPathFailureTime = DateTime.MinValue;
    private string lastVnavPathFailureText = string.Empty;
    private bool flyFlagFallbackUsedThisFlight;
    private DateTime lastUnderwaterBounceDescentStart = DateTime.MinValue;
    private bool underwaterBounceHoldLogged;
    private bool underwaterBounceSuppressedVnavLogged;
    private bool underwaterBounceReenterLogged;
    private bool underwaterFlagApproachIssued;
    private bool underwaterFlagApproachLogged;
    private DateTime lastUnderwaterFlagApproachTime = DateTime.MinValue;

    // Cycling mode state
    private List<(uint Id, string Name, uint TerritoryId)> cycleAetheryteQueue = new();
    private int cycleAetheryteIndex;
    private uint cycleCurrentAetheryteId;
    private bool cycleTeleportIssued;
    private DateTime cycleTeleportTime;
    private bool cycleLandingIssued;
    private DateTime cycleLandingTime;
    private Vector3 cycleLastPosition;
    private bool cyclePositionChanged;
    private DateTime cyclePositionChangeTime;
    private List<MapLocationEntry> cycleMapLocationQueue = new();
    private int cycleMapLocationIndex;
    private bool cycleManualControl;
    public bool CycleManualControl => cycleManualControl;

    // Alexandrite farming state
    private int alexandriteRunsRemaining;
    private int alexandriteRunsCompleted;
    private int alexandriteStep; // Sub-state machine step
    private DateTime alexandriteStepTime;
    private bool alexandriteActionIssued;
    public int AlexandriteRunsRemaining => alexandriteRunsRemaining;
    public int AlexandriteRunsCompleted => alexandriteRunsCompleted;

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
        { BotState.CyclingAetherytes,   60  },
        { BotState.CyclingMapLocations, 300 },
        { BotState.AlexandriteFarming,  300 },
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
        // Update delayed callbacks for SelectIconString
        GameHelpers.UpdateDelayedCallbacks();
        
        // Auto-discard runs when bot is enabled (any state)
        if (_plugin.Configuration.Enabled && _plugin.Configuration.EnableAutoDiscard && Plugin.ClientState.IsLoggedIn)
        {
            var now = DateTime.Now;
            if ((now - lastDiscardTime).TotalSeconds >= 30.0)
            {
                if (GameHelpers.CanAutoDiscardNow(out var discardReason))
                {
                    CommandHelper.SendCommand("/ays discard");
                    lastDiscardTime = now;
                    lastDiscardDeferredReason = string.Empty;
                }
                else if (discardReason != lastDiscardDeferredReason ||
                         (now - lastDiscardDeferredLogTime).TotalSeconds >= 5.0)
                {
                    lastDiscardDeferredReason = discardReason;
                    lastDiscardDeferredLogTime = now;
                    _plugin.AddDebugLog($"[AutoDiscard] Deferred: {discardReason}.");
                }
            }
        }

        // Companion chocobo summoning (every 15s when bot is enabled)
        if (_plugin.Configuration.Enabled && _plugin.Configuration.SummonChocobo && Plugin.ClientState.IsLoggedIn)
        {
            var now = DateTime.Now;

            // Deferred stance set after summoning
            if (companionStanceDeferred != DateTime.MinValue && now >= companionStanceDeferred)
            {
                companionStanceDeferred = DateTime.MinValue;
                var stanceCmd = _plugin.Configuration.CompanionStance switch
                {
                    "Defender Stance" => "/cac \"Defender Stance\"",
                    "Attacker Stance" => "/cac \"Attacker Stance\"",
                    "Healer Stance" => "/cac \"Healer Stance\"",
                    "Follow" => "/cac \"Follow\"",
                    _ => "/cac \"Free Stance\"",
                };
                CommandHelper.SendCommand(stanceCmd);
                _plugin.AddDebugLog($"[Companion] Set stance: {stanceCmd}");
            }

            if ((now - lastCompanionCheckTime).TotalSeconds >= 15.0)
            {
                lastCompanionCheckTime = now;
                var inCombat = Plugin.Condition[ConditionFlag.InCombat];
                var mounted = Plugin.Condition[ConditionFlag.Mounted];
                var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty];
                if (!inCombat && !mounted && !inDuty && !GameHelpers.IsInSanctuary())
                {
                    var buddyTime = GameHelpers.GetBuddyTimeRemaining();
                    if (buddyTime < 900f)
                    {
                        var greensCount = GameHelpers.GetInventoryItemCount(GameHelpers.GysahlGreensItemId);
                        if (greensCount > 0)
                        {
                            var result = GameHelpers.UseGysahlGreens();
                            if (result)
                            {
                                _plugin.AddDebugLog($"[Companion] Summoning chocobo (timer={buddyTime:F0}s, greens={greensCount})");
                                companionStanceDeferred = now.AddSeconds(3);
                                lastCompanionCheckTime = now.AddSeconds(20); // Don't recheck for 20s
                            }
                        }
                    }
                }
            }
        }

        var allowCycling = State is BotState.CyclingAetherytes or BotState.CyclingMapLocations or BotState.AlexandriteFarming;
        if (!_plugin.Configuration.Enabled && !allowCycling) return;
        if (IsPaused) return;
        if (State == BotState.Idle || State == BotState.Error) return;

        var now2 = DateTime.Now;
        if ((now2 - lastTickTime).TotalSeconds < TickIntervalSeconds) return;
        lastTickTime = now2;

        if (!Plugin.ClientState.IsLoggedIn)
        {
            // Lost connection is the only legitimate reason to stop the bot
            _plugin.NavigationService.StopNavigation();
            TransitionTo(BotState.Error, "Lost connection - not logged in.");
            return;
        }

        if (_plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver && _plugin.IsAdsAvailable)
            _plugin.AdsStatusService.Refresh();

        CheckStateTimeout();
        Tick();
    }

    private void CheckStateTimeout()
    {
        if (!StateTimeouts.TryGetValue(State, out var timeout)) return;

        if (State == BotState.DetectingLocation && _plugin.InventoryService.HasTreasureMapKeyItem())
        {
            stateStartTime = DateTime.Now;
            return;
        }
        
        // Don't timeout during combat - reset the timer so it starts fresh after combat ends
        bool inCombat = Plugin.Condition[ConditionFlag.InCombat];
        if (inCombat)
        {
            stateStartTime = DateTime.Now;
            return;
        }
        
        var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
        if (elapsed > timeout)
        {
            _plugin.AddDebugLog($"[TIMEOUT] State {State} timed out after {elapsed:F0}s (limit: {timeout}s)");
            HandleError($"Timeout in state {State} after {timeout}s.");
        }
    }

    private void Tick()
    {
        // Guard: never access game memory during zone transitions (loading screens)
        bool loading = Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (loading)
        {
            ResetPortalApproachTrackingForAreaChange();
            stateStartTime = DateTime.Now; // Don't timeout while loading
            return;
        }

        UpdateMountedRotationLifecycle();
        TryClearPendingDungeonMapFlag();

        // Check for territory change and refresh maps to fix inventory index issues
        var currentTerritory = Plugin.ClientState.TerritoryType;
        if (lastGlobalTerritoryId != 0 && lastGlobalTerritoryId != currentTerritory)
        {
            ResetPortalApproachTrackingForAreaChange();
            _plugin.AddDebugLog($"[Territory] Territory changed: {lastGlobalTerritoryId} -> {currentTerritory} - refreshing maps");
            _plugin.InventoryService.ScanForMaps();
        }
        lastGlobalTerritoryId = currentTerritory;

        // Enable BMR on territory changes (never turn off)
        if (!adsDutyHandoffActive && !mountedRotationSuppressed && lastBMRTerritoryId != 0 && lastBMRTerritoryId != currentTerritory)
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
            case BotState.CyclingAetherytes: TickCyclingAetherytes(); break;
            case BotState.CyclingMapLocations: TickCyclingMapLocations(); break;
            case BotState.AlexandriteFarming: TickAlexandriteFarming(); break;
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

        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
        {
            SetWarning("Cannot start LootGoblin from Main Menu. Log in first.");
            _plugin.AddDebugLog("[Start] Ignored start request because client is not logged in.");
            return;
        }

        ClearWarning();
        ResetRunCommandTriggers();
        ResetSaddlebagRetrieval();
        ResetStartMapRefresh();
        _plugin.TreasureMapLocationService.ClearCapturedLocation();

        // Check for AutoDuty when starting LootGoblin
        _plugin.RefreshDependencyStatus(logStatus: true);
        _plugin.AddDebugLog("[Start] Checking for AutoDuty before starting...");
        _plugin.AutoDutyDetectionService.ForceCheck();
        
        var isAutoDutyDetected = _plugin.AutoDutyDetectionService.IsAutoDutyDetected();
        _plugin.AddDebugLog($"[Start] AutoDuty detected: {isAutoDutyDetected}");
        
        if (isAutoDutyDetected)
        {
            _plugin.AddDebugLog("[Start] AutoDuty detected - showing warning window");
            _plugin.AutoDutyDetectionService.ForceShowWarning();
        }
        else
        {
            _plugin.AddDebugLog("[Start] AutoDuty not detected - proceeding with start");
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
            currentLandingMode = OverworldLandingMode.MountToggle;
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
        currentLandingMode = OverworldLandingMode.MountToggle;
        ResetKeyItemMapRecoveryState(clearActiveKey: true);
        _plugin.YesAlreadyIPC.Pause();
        _plugin.AddDebugLog($"[Start] YesAlready paused: {_plugin.YesAlreadyIPC.IsPaused}");

        if (TryRecoverActiveKeyItemMap("[Start]", transitionToDetectingOnActive: true))
            return;

        if (_plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver && !_plugin.IsAdsAvailable)
        {
            _plugin.ShowAdsMissingToast();
        }

        BeginStartMapRefresh();
        TransitionTo(BotState.SelectingMap, "Starting map run - refreshing saddlebag maps...");
    }

    public void Stop()
    {
        _plugin.NavigationService.StopNavigation(clearFlag: true);
        ResetVnavFlyFlagFallbackState();
        if (IsDungeonState())
        {
            CommandHelper.SendCommand("/bmrai off");
            _plugin.AddDebugLog("[Stop] Disabled BMR AI (was in dungeon)");
        }
        IsPaused = false;
        RetryCount = 0;
        EndPortalRetryWindow();
        dungeonEntryProcessed = false;
        ResetAdsHandoffTracking(resetStatus: true);
        _plugin.RetainerMapRetrievalService.Reset();
        ResetSaddlebagRetrieval();
        ResetStartMapRefresh();
        currentLandingMode = OverworldLandingMode.MountToggle;
        ResetRunCommandTriggers();
        ResetKeyItemMapRecoveryState(clearActiveKey: true);
        RestoreMountedRotationLifecycle("bot stop");
        ClearWarning();
        TransitionTo(BotState.Idle, "Stopped by user.");
    }

    public void ResetAll()
    {
        _plugin.NavigationService.StopNavigation(clearFlag: true);
        ResetVnavFlyFlagFallbackState();
        IsPaused = false;
        RetryCount = 0;
        CurrentLocation = null;
        SelectedMapItemId = 0;
        EndPortalRetryWindow();
        ResetAdsHandoffTracking(resetStatus: true);
        _plugin.RetainerMapRetrievalService.Reset();
        ResetSaddlebagRetrieval();
        ResetStartMapRefresh();
        currentLandingMode = OverworldLandingMode.MountToggle;
        ResetRunCommandTriggers();
        RestoreMountedRotationLifecycle("full reset");
        KrangleService.ClearCache();
        ClearWarning();
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

    private bool TryRecoverActiveKeyItemMap(string source, bool transitionToDetectingOnActive)
    {
        if (!_plugin.InventoryService.TryFindTreasureMapKeyItem(out var keyItem))
        {
            ResetKeyItemMapRecoveryState(clearActiveKey: true);
            if (WarningMessage.Contains("key item", StringComparison.OrdinalIgnoreCase))
                ClearWarning();
            return false;
        }

        UpdateActiveKeyItemMap(keyItem, source);
        mapCountChecked = true;
        mapOpeningRetried = false;
        initialMapCount = 0;

        var existingFlag = _plugin.MapFlagService.TryGetMapLocation();
        if (existingFlag != null)
        {
            ResetKeyItemMapRecoveryState();
            SetWarning(keyItem.KnownMapItemId == 0
                ? $"Active treasure map key item '{keyItem.DisplayName}' found. Map type is unknown, but LootGoblin will use the existing map flag and will not open another map."
                : $"Active deciphered map key item '{keyItem.DisplayName}' found. LootGoblin will finish it before opening another map.");

            if (transitionToDetectingOnActive && State != BotState.DetectingLocation)
            {
                RetryCount = 0;
                CurrentLocation = null;
                TransitionTo(BotState.DetectingLocation, $"Recovered active map flag from {keyItem.DisplayName}...");
                return true;
            }

            return false;
        }

        if (_plugin.TreasureMapLocationService.TryGetLatestLocationForKeyItem(keyItem.ItemId, out _))
        {
            ResetKeyItemMapRecoveryState();
            SetWarning(keyItem.KnownMapItemId == 0
                ? $"Active treasure map key item '{keyItem.DisplayName}' found. LootGoblin recovered its location locally and will not open another map."
                : $"Active deciphered map key item '{keyItem.DisplayName}' found. LootGoblin recovered its location locally and will finish it before opening another map.");

            if (transitionToDetectingOnActive && State != BotState.DetectingLocation)
            {
                RetryCount = 0;
                CurrentLocation = null;
                TransitionTo(BotState.DetectingLocation, $"Recovered active map location from {keyItem.DisplayName}...");
                return true;
            }

            return false;
        }

        var now = DateTime.Now;
        if (keyItemMapRecoveryStartedAt == DateTime.MinValue)
        {
            keyItemMapRecoveryStartedAt = now;
            _plugin.AddDebugLog($"{source} Active key-item map found ({keyItem.DisplayName}) but AgentMap has no flag.");
        }

        var elapsed = now - keyItemMapRecoveryStartedAt;
        if (transitionToDetectingOnActive && State != BotState.DetectingLocation)
        {
            TransitionTo(BotState.DetectingLocation, $"Recovering active map flag for {keyItem.DisplayName}...");
            return true;
        }

        if (elapsed >= KeyItemMapRecoveryTimeout)
        {
            var failed =
                $"Treasure map key item '{keyItem.DisplayName}' is active, but no local map flag appeared after LootGoblin tried to open it. Open the key item manually or stop before opening another.";
            SetWarning(failed);
            TransitionTo(BotState.Error, failed);
            return true;
        }

        TryOpenActiveKeyItemMap(keyItem);
        StateDetail = keyItemMapOpenAttemptCount > 0
            ? $"Opening active key-item map '{keyItem.DisplayName}'... ({elapsed.TotalSeconds:F0}/{KeyItemMapRecoveryTimeout.TotalSeconds:F0}s)"
            : $"Waiting to open active key-item map '{keyItem.DisplayName}'...";
        stateStartTime = DateTime.Now;
        return true;
    }

    private void TryOpenActiveKeyItemMap(TreasureMapKeyItem keyItem)
    {
        var now = DateTime.Now;
        if (now < keyItemMapNextOpenAttemptAt)
            return;

        keyItemMapNextOpenAttemptAt = now.Add(KeyItemMapOpenRetryInterval);
        keyItemMapOpenAttemptCount++;

        if (!GameHelpers.IsPlayerAvailable())
        {
            _plugin.AddDebugLog($"[KeyItemMap] Player unavailable; deferring active key-item open for {keyItem.DisplayName}.");
            return;
        }

        if (GameHelpers.UseEventItem(keyItem.ItemId, keyItem.DisplayName))
        {
            _plugin.AddDebugLog($"[KeyItemMap] Opened active treasure-map key item {keyItem.DisplayName} (attempt {keyItemMapOpenAttemptCount}).");
        }
        else
        {
            _plugin.AddDebugLog($"[KeyItemMap] Failed to open active treasure-map key item {keyItem.DisplayName} (attempt {keyItemMapOpenAttemptCount}); retrying.");
        }

        if (keyItemMapOpenAttemptCount == 1)
        {
            SetWarning($"Active treasure map key item '{keyItem.DisplayName}' found. LootGoblin is opening it to recover the map flag before opening another map.");
            stateStartTime = DateTime.Now;
        }
    }

    private void UpdateActiveKeyItemMap(TreasureMapKeyItem keyItem, string source)
    {
        var changed = activeKeyItemMapItemId != keyItem.ItemId || activeKeyItemMapSlot != keyItem.Slot;
        if (changed)
        {
            ResetKeyItemMapRecoveryState();
            digIssuedThisMap = false;
            digIssuedAt = DateTime.MinValue;
            chestConfirmedThisMap = false;
            portalConfirmedThisMap = false;
            dungeonConfirmedThisMap = false;
            activeKeyItemMapItemId = keyItem.ItemId;
            activeKeyItemMapSlot = keyItem.Slot;
            _plugin.AddDebugLog($"{source} Active treasure map key item: {keyItem.DisplayName} (item {keyItem.ItemId}, slot {keyItem.Slot}).");
        }

        if (keyItem.KnownMapItemId != 0)
        {
            var landingMode = ResolveLandingMode(keyItem.KnownMapItemId);
            if (SelectedMapItemId != keyItem.KnownMapItemId || currentLandingMode != landingMode)
            {
                SelectedMapItemId = keyItem.KnownMapItemId;
                currentLandingMode = landingMode;
                if (currentLandingMode != OverworldLandingMode.UnderwaterBounce)
                    ResetUnderwaterLandingState();
                _plugin.AddDebugLog($"{source} Matched key item to known map ID {SelectedMapItemId}; landing mode {currentLandingMode}.");
            }
        }
        else if (SelectedMapItemId != 0 || currentLandingMode != OverworldLandingMode.MountToggle)
        {
            SelectedMapItemId = 0;
            currentLandingMode = OverworldLandingMode.MountToggle;
            ResetUnderwaterLandingState();
            _plugin.AddDebugLog($"{source} Active key-item map type is unknown; landing mode MountToggle.");
        }
    }

    private List<uint> GetEnabledMapCandidates(
        Dictionary<uint, MapSourceCount> mapSources,
        bool includeInventory,
        bool includeSaddlebags)
    {
        var enabled = _plugin.Configuration.EnabledMapTypes;

        return mapSources
            .Where(kvp =>
            {
                if (enabled.Count > 0 && !enabled.Contains(kvp.Key))
                    return false;

                var count = 0;
                if (includeInventory)
                    count += kvp.Value.Inventory;
                if (includeSaddlebags)
                    count += kvp.Value.SaddlebagTotal;

                return count > 0;
            })
            .Select(kvp => kvp.Key)
            .ToList();
    }

    private void TryRetrieveSaddlebagMap(uint mapItemId)
    {
        if (saddlebagRetrievalStep != SaddlebagRetrievalStep.Idle)
            return;

        var mapName = TreasureMapData.KnownMaps.TryGetValue(mapItemId, out var info)
            ? info.Name
            : $"ID {mapItemId}";

        saddlebagTargetItemId = mapItemId;
        saddlebagMovePlan = null;
        saddlebagInitialInventoryCount = _plugin.InventoryService.GetMapCount(mapItemId);
        saddlebagInitialSaddlebagCount = _plugin.InventoryService.GetSaddlebagMapCount(mapItemId);
        saddlebagAddonVisibleSince = DateTime.MinValue;
        _plugin.AddDebugLog(
            $"[Saddlebag] Starting UI retrieval for {mapName}: inventory={saddlebagInitialInventoryCount}, saddlebag={saddlebagInitialSaddlebagCount}");
        EnterSaddlebagStep(SaddlebagRetrievalStep.Opening, $"Opening saddlebag for {mapName}...");
    }

    private void TickSaddlebagMapRetrieval()
    {
        if (saddlebagRetrievalStep == SaddlebagRetrievalStep.Idle)
            return;

        if (DateTime.Now < saddlebagNextActionAt)
            return;

        var mapName = TreasureMapData.KnownMaps.TryGetValue(saddlebagTargetItemId, out var info)
            ? info.Name
            : $"ID {saddlebagTargetItemId}";

        if (DateTime.Now - saddlebagStepStartedAt > SaddlebagStepTimeout)
        {
            FailSaddlebagRetrieval($"{saddlebagRetrievalStep} timed out for {mapName}.");
            return;
        }

        switch (saddlebagRetrievalStep)
        {
            case SaddlebagRetrievalStep.Opening:
                TickOpeningSaddlebag(mapName);
                break;
            case SaddlebagRetrievalStep.WaitingForAddon:
                TickWaitingForSaddlebagAddon(mapName);
                break;
            case SaddlebagRetrievalStep.WaitingStable:
                TickWaitingForStableSaddlebag(mapName);
                break;
            case SaddlebagRetrievalStep.Moving:
                TickMovingSaddlebagMap(mapName);
                break;
            case SaddlebagRetrievalStep.Confirming:
                TickConfirmingSaddlebagMove(mapName);
                break;
        }
    }

    private void TickOpeningSaddlebag(string mapName)
    {
        if (!CanRunSaddlebagAction(out var reason))
        {
            StateDetail = $"Waiting to open saddlebag: {reason}";
            saddlebagNextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (GameHelpers.IsAddonVisible("InventoryBuddy"))
        {
            saddlebagAddonVisibleSince = DateTime.Now;
            _plugin.AddDebugLog("[Saddlebag] InventoryBuddy already visible.");
            EnterSaddlebagStep(SaddlebagRetrievalStep.WaitingStable, $"Waiting for saddlebag UI to stabilize for {mapName}...");
            return;
        }

        CommandHelper.SendCommand("/saddlebag");
        _plugin.AddDebugLog("[Saddlebag] Open requested with /saddlebag.");
        EnterSaddlebagStep(SaddlebagRetrievalStep.WaitingForAddon, $"Waiting for saddlebag UI for {mapName}...");
        saddlebagNextActionAt = DateTime.Now.AddSeconds(1);
    }

    private void TickWaitingForSaddlebagAddon(string mapName)
    {
        if (!CanRunSaddlebagAction(out var reason))
        {
            StateDetail = $"Waiting for saddlebag UI: {reason}";
            saddlebagNextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (GameHelpers.IsAddonVisible("InventoryBuddy"))
        {
            saddlebagAddonVisibleSince = DateTime.Now;
            _plugin.AddDebugLog("[Saddlebag] InventoryBuddy visible.");
            EnterSaddlebagStep(SaddlebagRetrievalStep.WaitingStable, $"Waiting for saddlebag UI to stabilize for {mapName}...");
            return;
        }

        StateDetail = $"Waiting for saddlebag UI for {mapName}...";
        saddlebagNextActionAt = DateTime.Now.AddMilliseconds(500);
    }

    private void TickWaitingForStableSaddlebag(string mapName)
    {
        if (!CanRunSaddlebagAction(out var reason))
        {
            StateDetail = $"Waiting for stable saddlebag UI: {reason}";
            saddlebagNextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (!GameHelpers.IsAddonVisible("InventoryBuddy"))
        {
            _plugin.AddDebugLog("[Saddlebag] InventoryBuddy hidden while waiting; reopening.");
            EnterSaddlebagStep(SaddlebagRetrievalStep.Opening, $"Reopening saddlebag for {mapName}...");
            saddlebagNextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (DateTime.Now - saddlebagAddonVisibleSince < SaddlebagAddonStableDelay)
        {
            StateDetail = $"Waiting for saddlebag UI to stabilize for {mapName}...";
            saddlebagNextActionAt = DateTime.Now.AddMilliseconds(250);
            return;
        }

        _plugin.InventoryService.ScanForMapSources();

        if (!_plugin.InventoryService.TryPlanSaddlebagMapMove(saddlebagTargetItemId, out var plan, out var detail))
        {
            if (detail.Contains("No empty inventory slot", StringComparison.OrdinalIgnoreCase))
            {
                FailSaddlebagRetrieval(detail);
                return;
            }

            StateDetail = $"Waiting for loaded saddlebag source for {mapName}...";
            _plugin.AddDebugLog($"[Saddlebag] Waiting for source/destination: {detail}");
            saddlebagNextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        saddlebagMovePlan = plan;
        _plugin.AddDebugLog($"[Saddlebag] Source/destination chosen for {mapName}: {detail}");
        EnterSaddlebagStep(SaddlebagRetrievalStep.Moving, $"Moving {mapName} from saddlebag...");
        saddlebagNextActionAt = DateTime.Now.AddMilliseconds(500);
    }

    private void TickMovingSaddlebagMap(string mapName)
    {
        if (saddlebagMovePlan == null)
        {
            FailSaddlebagRetrieval("No saddlebag move plan.");
            return;
        }

        if (!CanRunSaddlebagAction(out var reason))
        {
            StateDetail = $"Waiting to move saddlebag map: {reason}";
            saddlebagNextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (!GameHelpers.IsAddonVisible("InventoryBuddy"))
        {
            FailSaddlebagRetrieval("InventoryBuddy hidden before saddlebag move.");
            return;
        }

        if (!_plugin.InventoryService.TryMovePlannedSaddlebagMap(saddlebagMovePlan, out var detail))
        {
            FailSaddlebagRetrieval(detail);
            return;
        }

        _plugin.AddDebugLog($"[Saddlebag] Move issued for {mapName}: {detail}");
        EnterSaddlebagStep(SaddlebagRetrievalStep.Confirming, $"Confirming {mapName} moved from saddlebag...");
        saddlebagNextActionAt = DateTime.Now.AddSeconds(1);
    }

    private void TickConfirmingSaddlebagMove(string mapName)
    {
        var inventoryCount = _plugin.InventoryService.GetMapCount(saddlebagTargetItemId);
        var saddlebagCount = _plugin.InventoryService.GetSaddlebagMapCount(saddlebagTargetItemId);

        if (inventoryCount > saddlebagInitialInventoryCount && saddlebagCount < saddlebagInitialSaddlebagCount)
        {
            _plugin.AddDebugLog(
                $"[Saddlebag] Count confirmed for {mapName}: inventory {saddlebagInitialInventoryCount}->{inventoryCount}, saddlebag {saddlebagInitialSaddlebagCount}->{saddlebagCount}");
            if (GameHelpers.IsAddonVisible("InventoryBuddy"))
                GameHelpers.CloseCurrentAddon();

            StateDetail = $"Retrieved {mapName} from saddlebag. Rechecking inventory...";
            lastMapScanTime = DateTime.MinValue;
            ResetSaddlebagRetrieval();
            return;
        }

        StateDetail = $"Waiting for {mapName} saddlebag move confirmation...";
        saddlebagNextActionAt = DateTime.Now.AddMilliseconds(500);
    }

    private bool CanRunSaddlebagAction(out string reason)
    {
        if (!Plugin.ClientState.IsLoggedIn)
        {
            reason = "not logged in";
            return false;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            reason = "player unavailable";
            return false;
        }

        if (player.IsCasting || Plugin.Condition[ConditionFlag.Casting])
        {
            reason = "casting";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "loading";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.Occupied] ||
            Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Plugin.Condition[ConditionFlag.Occupied33] ||
            Plugin.Condition[ConditionFlag.Occupied39] ||
            Plugin.Condition[ConditionFlag.WatchingCutscene])
        {
            reason = "occupied";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            reason = "in combat";
            return false;
        }

        reason = "ready";
        return true;
    }

    private void EnterSaddlebagStep(SaddlebagRetrievalStep nextStep, string detail)
    {
        saddlebagRetrievalStep = nextStep;
        saddlebagStepStartedAt = DateTime.Now;
        saddlebagNextActionAt = DateTime.Now;
        StateDetail = detail;
        _plugin.AddDebugLog($"[Saddlebag] {detail}");
    }

    private void FailSaddlebagRetrieval(string detail)
    {
        var mapName = TreasureMapData.KnownMaps.TryGetValue(saddlebagTargetItemId, out var info)
            ? info.Name
            : $"ID {saddlebagTargetItemId}";

        _plugin.AddDebugLog($"[Saddlebag] ERROR: {detail}");
        ResetSaddlebagRetrieval();
        HandleError($"Could not retrieve {mapName} from saddlebag: {detail}");
    }

    private void ResetSaddlebagRetrieval()
    {
        saddlebagRetrievalStep = SaddlebagRetrievalStep.Idle;
        saddlebagStepStartedAt = DateTime.MinValue;
        saddlebagNextActionAt = DateTime.MinValue;
        saddlebagAddonVisibleSince = DateTime.MinValue;
        saddlebagTargetItemId = 0;
        saddlebagInitialInventoryCount = 0;
        saddlebagInitialSaddlebagCount = 0;
        saddlebagMovePlan = null;
    }

    private void BeginStartMapRefresh()
    {
        startMapRefreshPending = true;
        startMapRefreshOpenedSaddlebag = false;
        startMapRefreshStartedAt = DateTime.Now;
        _plugin.AddDebugLog("[MapRefresh][Start] Queued saddlebag refresh before selecting a map.");
    }

    private void ResetStartMapRefresh()
    {
        startMapRefreshPending = false;
        startMapRefreshOpenedSaddlebag = false;
        startMapRefreshStartedAt = DateTime.MinValue;
    }

    private bool TickStartMapRefresh()
    {
        if (!startMapRefreshPending)
            return false;

        if (!GameHelpers.IsPlayerAvailable())
        {
            StateDetail = "Waiting to refresh saddlebags: player unavailable...";
            return true;
        }

        if (GameHelpers.IsAddonVisible("InventoryBuddy"))
        {
            CompleteStartMapRefresh(startMapRefreshOpenedSaddlebag, "Saddlebag visible; refreshed loaded map sources.");
            return true;
        }

        if (!startMapRefreshOpenedSaddlebag)
        {
            if (CommandHelper.TrySendCommand("/saddlebag"))
            {
                startMapRefreshOpenedSaddlebag = true;
                startMapRefreshStartedAt = DateTime.Now;
                _plugin.AddDebugLog("[MapRefresh][Start] Opened saddlebag for start refresh.");
            }

            StateDetail = "Opening saddlebag to refresh map sources...";
            return true;
        }

        if (DateTime.Now - startMapRefreshStartedAt < StartMapRefreshSaddlebagTimeout)
        {
            StateDetail = "Waiting for saddlebag map refresh...";
            return true;
        }

        CompleteStartMapRefresh(closeSaddlebagAfterScan: false, "Saddlebag did not open before timeout; refreshed currently loaded map sources.");
        return true;
    }

    private void CompleteStartMapRefresh(bool closeSaddlebagAfterScan, string detail)
    {
        _plugin.InventoryService.ScanForMapSources(includeSaddlebags: true);

        if (closeSaddlebagAfterScan && GameHelpers.IsAddonVisible("InventoryBuddy"))
        {
            GameHelpers.CloseCurrentAddon();
            _plugin.AddDebugLog("[MapRefresh][Start] Closed saddlebag after start refresh.");
        }

        ResetStartMapRefresh();
        StateDetail = "Saddlebag map refresh complete.";
        _plugin.AddDebugLog($"[MapRefresh][Start] {detail}");
    }

    private bool TryRetrieveRetainerMap(IReadOnlyCollection<uint> enabledMapIds, string emptyInventoryError)
    {
        if (!_plugin.Configuration.EnableRetainerMapRetrieval)
        {
            _plugin.AddDebugLog($"[RetainerMap] Retrieval disabled. {emptyInventoryError}");
            HandleError(emptyInventoryError);
            return true;
        }

        _plugin.AddDebugLog($"[RetainerMap] Checking XA Database for enabled map IDs: {FormatMapIds(enabledMapIds)}");
        var result = _plugin.RetainerMapRetrievalService.StartOrTick(enabledMapIds);
        switch (result)
        {
            case RetainerMapRetrievalResult.Running:
                _plugin.AddDebugLog($"[RetainerMap] Retrieval running: {_plugin.RetainerMapRetrievalService.StatusText}");
                StateDetail = _plugin.RetainerMapRetrievalService.StatusText;
                lastMapScanTime = DateTime.MinValue;
                return true;

            case RetainerMapRetrievalResult.Retrieved:
                _plugin.AddDebugLog("[RetainerMap] Retrieval complete. Rechecking inventory.");
                StateDetail = "Retainer map retrieved. Rechecking inventory...";
                lastMapScanTime = DateTime.MinValue;
                return true;

            case RetainerMapRetrievalResult.Error:
                _plugin.AddDebugLog($"[RetainerMap] Retrieval error: {_plugin.RetainerMapRetrievalService.LastError}");
                StopOnRetainerRetrievalError($"Could not retrieve retainer map: {_plugin.RetainerMapRetrievalService.LastError}");
                return true;

            case RetainerMapRetrievalResult.NotAvailable:
                _plugin.AddDebugLog($"[RetainerMap] No enabled retainer map available. {emptyInventoryError}");
                HandleError(emptyInventoryError);
                return true;
        }

        return false;
    }

    private static string FormatMapIds(IReadOnlyCollection<uint> mapIds)
        => mapIds.Count == 0 ? "all configured maps" : string.Join(", ", mapIds);

    private void ResetRunCommandTriggers()
    {
        ResetPerMapCommandTriggers();
        finishCommandsRanThisRun = false;
    }

    private void ResetPerMapCommandTriggers()
    {
        ResetVnavFlyFlagFallbackState();
        landingCommandsRanThisMap = false;
        dutyEntryCommandsRanThisMap = false;
        digIssuedThisMap = false;
        digIssuedAt = DateTime.MinValue;
        chestConfirmedThisMap = false;
        portalConfirmedThisMap = false;
        dungeonConfirmedThisMap = false;
        ResetKeyItemMapRecoveryState(clearActiveKey: true);
        ResetUnderwaterLandingState();
        _plugin.TreasureMapLocationService.ClearCapturedLocation();
    }

    private void ResetUnderwaterLandingState()
    {
        if (descentInProgress)
        {
            GameHelpers.KeyRelease(VirtualKey.CONTROL);
            GameHelpers.KeyRelease(VirtualKey.SPACE);
        }

        dismountAttemptStart = DateTime.MinValue;
        descentInProgress = false;
        descentMode = false;
        descentStartTime = DateTime.MinValue;
        descentStartY = 0f;
        lastUnderwaterBounceDescentStart = DateTime.MinValue;
        underwaterBounceHoldLogged = false;
        underwaterBounceSuppressedVnavLogged = false;
        underwaterBounceReenterLogged = false;
        underwaterFlagApproachIssued = false;
        underwaterFlagApproachLogged = false;
        lastUnderwaterFlagApproachTime = DateTime.MinValue;
        underwaterTargetPosition = Vector3.Zero;
        wasDiving = false;
        nonThiefDivingIgnoredLogged = false;
    }

    public void NotifyVnavPathFailure(string text)
    {
        if (State != BotState.Flying)
            return;

        lastVnavPathFailureTime = DateTime.Now;
        lastVnavPathFailureText = text;
        _plugin.AddDebugLog($"[Flying] Observed vnav path failure: {text}");
    }

    private void ResetVnavFlyFlagFallbackState()
    {
        lastVnavPathFailureTime = DateTime.MinValue;
        lastVnavPathFailureText = string.Empty;
        flyFlagFallbackUsedThisFlight = false;
    }

    private void ResetKeyItemMapRecoveryState(bool clearActiveKey = false)
    {
        keyItemMapRecoveryStartedAt = DateTime.MinValue;
        keyItemMapNextOpenAttemptAt = DateTime.MinValue;
        keyItemMapOpenAttemptCount = 0;
        if (!clearActiveKey)
            return;

        activeKeyItemMapItemId = 0;
        activeKeyItemMapSlot = -1;
        lastKeyItemCompletionGuardLogAt = DateTime.MinValue;
    }

    private bool TryFallbackToFlyFlagAfterVnavFailure(DateTime now)
    {
        if (flyFlagFallbackUsedThisFlight || lastVnavPathFailureTime == DateTime.MinValue)
            return false;

        if (CurrentLocation == null || !stateActionIssued)
            return false;

        var loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                      Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (loading)
            return false;

        flyFlagFallbackUsedThisFlight = true;
        _plugin.AddDebugLog("[Flying] vnav flyto failed - falling back to /vnav flyflag");
        if (!string.IsNullOrWhiteSpace(lastVnavPathFailureText))
            _plugin.AddDebugLog($"[Flying] vnav failure text: {lastVnavPathFailureText}");

        _plugin.NavigationService.FlyToFlag();
        StateDetail = "Flying to map flag after vnav flyto failure...";
        lastStuckCheckPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        lastStuckCheckTime = now;
        return true;
    }

    private void RunLandingCommandsOnce(string reason)
    {
        if (landingCommandsRanThisMap)
            return;

        landingCommandsRanThisMap = true;
        RunConfiguredCommands(_plugin.Configuration.LandingOrDutyCommandTriggers, reason);
    }

    private void RunDutyEntryCommandsOnce(string reason)
    {
        if (dutyEntryCommandsRanThisMap)
            return;

        dutyEntryCommandsRanThisMap = true;
        RunConfiguredCommands(_plugin.Configuration.LandingOrDutyCommandTriggers, reason);
    }

    private void RunFinishCommandsOnce(string reason)
    {
        if (finishCommandsRanThisRun)
            return;

        finishCommandsRanThisRun = true;
        RunConfiguredCommands(_plugin.Configuration.FinishCommandTriggers, reason);
    }

    private void RunConfiguredCommands(List<string>? commands, string reason)
    {
        if (commands == null || commands.Count == 0)
            return;

        var sent = 0;
        foreach (var command in commands)
        {
            var trimmed = command?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (CommandHelper.TrySendCommand(trimmed))
                sent++;
        }

        if (sent > 0)
            _plugin.AddDebugLog($"[CommandTrigger] Sent {sent} command(s) for {reason}.");
    }

    private void TryDigWhileDiving(string reason)
    {
        if (!Plugin.Condition[ConditionFlag.Diving])
            return;

        if (!CanUseUnderwaterNavigation())
            return;

        if ((DateTime.Now - lastDigTime).TotalSeconds < 3.0)
            return;

        RunLandingCommandsOnce(reason);
        CommandHelper.SendCommand("/gaction dig");
        lastDigTime = DateTime.Now;
        digIssuedThisMap = true;
        digIssuedAt = lastDigTime;
        _plugin.AddDebugLog($"{reason}: issued /gaction dig while diving.");
    }

    private bool TryHandleMapLandingAndDig(
        string reason,
        string landingBasis,
        Vector3 currentPos,
        Vector3 landingTarget,
        double xzDist)
    {
        if (currentLandingMode != OverworldLandingMode.MountToggle)
            return false;

        var now = DateTime.Now;
        if (_plugin.NavigationService.IsMounted())
        {
            var waitForPartyDismount = _plugin.Configuration.PartyWaitBeforeDismount;
            _plugin.AddDebugLog(
                $"[Dismount] PartyWaitBeforeDismount={waitForPartyDismount}, LandingMode={currentLandingMode}, BypassForDive=False");

            if (waitForPartyDismount)
            {
                var partyWait = EvaluateOverworldLandingPartyWait(10.0);
                if (!partyWait.CanProceed)
                {
                    StateDetail = BuildOverworldLandingPartyWaitDetail(partyWait, 10.0);
                    return true;
                }
            }

            if (dismountAttemptStart == DateTime.MinValue)
            {
                dismountAttemptStart = now;
                stateActionIssued = true;
                _plugin.NavigationService.StopNavigation();
                _plugin.AddDebugLog(
                    $"{reason}: landing phase ready via {landingBasis}; landingXZ={xzDist:F1}y; " +
                    $"current={FormatVectorCompact(currentPos)}; landingTarget={FormatVectorCompact(landingTarget)}");
            }

            var dismountElapsed = (now - dismountAttemptStart).TotalSeconds;
            _mountService.TryLandingToggle();
            StateDetail = $"Landing by /mount toggle... ({dismountElapsed:F0}s)";
            return true;
        }

        if (dismountAttemptStart == DateTime.MinValue)
        {
            dismountAttemptStart = now;
            stateActionIssued = true;
            _plugin.AddDebugLog(
                $"{reason}: already unmounted at map location via {landingBasis}; landingXZ={xzDist:F1}y; " +
                $"current={FormatVectorCompact(currentPos)}; landingTarget={FormatVectorCompact(landingTarget)}");
        }

        if (digIssuedThisMap)
        {
            var elapsed = digIssuedAt == DateTime.MinValue ? 0 : (now - digIssuedAt).TotalSeconds;
            StateDetail = $"Waiting for treasure coffer after dig... ({elapsed:F1}s)";
            return true;
        }

        RecordMapLandingPosition();
        RunLandingCommandsOnce(reason);
        CommandHelper.SendCommand("/gaction dig");
        lastDigTime = now;
        digIssuedThisMap = true;
        digIssuedAt = now;
        _plugin.AddDebugLog($"{reason}: issued /gaction dig after landing.");

        System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ => {
            try
            {
                TransitionTo(BotState.OpeningChest, "Looking for treasure coffer to interact...");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"[StateManager] ContinueWith exception in TransitionTo (dig handoff): {ex.Message}");
            }
        }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);

        return true;
    }

    private void RecordMapLandingPosition()
    {
        if (CurrentLocation == null)
            return;

        var realPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        if (realPos == Vector3.Zero)
            return;

        var mapItemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var mapName = SelectedMapItemId > 0
            ? (mapItemSheet?.GetRow(SelectedMapItemId).Name.ToString() ?? $"Map {SelectedMapItemId}")
            : "Unknown Map";

        _plugin.MapLocationDatabase.RecordLocation(
            CurrentLocation.TerritoryId,
            CurrentLocation.ZoneName,
            mapName,
            CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z,
            realPos.X, realPos.Y, realPos.Z);
    }

    private bool ShouldAssumeThiefMapFromDiving()
    {
        return Plugin.Condition[ConditionFlag.Diving]
            && IsThiefMap(SelectedMapItemId)
            && (State == BotState.Flying || State == BotState.OpeningChest);
    }

    private bool CanUseUnderwaterNavigation()
    {
        return IsThiefMap(SelectedMapItemId);
    }

    private bool IsDivingForCurrentMap()
    {
        var isDiving = Plugin.Condition[ConditionFlag.Diving];
        if (!isDiving)
        {
            nonThiefDivingIgnoredLogged = false;
            return false;
        }

        if (CanUseUnderwaterNavigation())
            return true;

        if (currentLandingMode == OverworldLandingMode.UnderwaterBounce
            || descentInProgress
            || descentMode
            || underwaterTargetPosition != Vector3.Zero
            || wasDiving)
        {
            currentLandingMode = OverworldLandingMode.MountToggle;
            ResetUnderwaterLandingState();
        }

        if (!nonThiefDivingIgnoredLogged)
        {
            nonThiefDivingIgnoredLogged = true;
            _plugin.AddDebugLog($"[Underwater] Ignoring Diving for non-thief map ID {SelectedMapItemId}; underwater navigation is Thief's Map only.");
        }

        return false;
    }

    private bool IsUnderwaterBounceTriggerFlow(bool includeNearTarget = true)
    {
        if (State != BotState.Flying && State != BotState.OpeningChest)
            return false;

        if (!CanUseUnderwaterNavigation())
        {
            if (currentLandingMode == OverworldLandingMode.UnderwaterBounce)
            {
                currentLandingMode = OverworldLandingMode.MountToggle;
                ResetUnderwaterLandingState();
                _plugin.AddDebugLog($"[Landing] Cleared underwater bounce for non-thief map ID {SelectedMapItemId}; using MountToggle.");
            }
            return false;
        }

        if (currentLandingMode != OverworldLandingMode.UnderwaterBounce)
        {
            if (!ShouldAssumeThiefMapFromDiving())
                return false;

            currentLandingMode = OverworldLandingMode.UnderwaterBounce;
            _plugin.AddDebugLog("[Underwater] Diving detected for thief map - enabling thief-map trigger flow");
        }

        if (Plugin.Condition[ConditionFlag.Diving]
            || descentInProgress
            || descentMode
            || underwaterTargetPosition != Vector3.Zero
            || dismountAttemptStart != DateTime.MinValue)
        {
            return true;
        }

        if (!includeNearTarget || CurrentLocation == null)
            return false;

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        if (playerPos == Vector3.Zero)
            return false;

        var targets = ResolveOverworldNavigationTargets();
        return targets.LandingTarget != Vector3.Zero
            && CalculateXZDistance(playerPos, targets.LandingTarget) <= UnderwaterBounceTriggerXZRange;
    }

    private Vector3 ResolveUnderwaterTargetPosition(MapLocationEntry? currentEntry, int destinationIndex, out string basis)
    {
        basis = "standard navigation";

        if (CurrentLocation == null)
            return Vector3.Zero;

        if (!CanUseUnderwaterNavigation())
        {
            basis = "underwater disabled for non-thief map";
            return Vector3.Zero;
        }

        if (currentEntry != null && destinationIndex > 0)
        {
            var specialNav = _plugin.SpecialNavigationDatabase.FindEntry(destinationIndex);
            if (specialNav != null)
            {
                basis = "special navigation";
                return new Vector3(specialNav.MainX, specialNav.MainY, specialNav.MainZ);
            }
        }

        if (currentLandingMode == OverworldLandingMode.UnderwaterBounce)
        {
            var targets = ResolveOverworldNavigationTargets();
            if (targets.LandingTarget != Vector3.Zero)
            {
                basis = targets.Basis;
                return targets.LandingTarget;
            }
        }

        return new Vector3(CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z);
    }

    private Vector3 ResolveUnderwaterTargetPosition(out string destinationText, out string zoneName, out string basis)
    {
        destinationText = "Unknown";
        zoneName = "Unknown";
        basis = "standard navigation";

        if (CurrentLocation == null)
            return Vector3.Zero;

        var currentEntry = _plugin.MapLocationDatabase.FindEntry(CurrentLocation.TerritoryId, CurrentLocation.X, CurrentLocation.Z);
        var destinationIndex = currentEntry?.Index > 0 ? currentEntry.Index : -1;
        destinationText = destinationIndex > 0 ? $"Destination #{destinationIndex}" : "Unknown";
        zoneName = currentEntry?.ZoneName ?? CurrentLocation.ZoneName ?? "Unknown";

        return ResolveUnderwaterTargetPosition(currentEntry, destinationIndex, out basis);
    }

    private Vector3 ResolveUnderwaterFlagApproachTarget(Vector3 currentPos, out string basis, out string destinationText, out string zoneName)
    {
        var targets = ResolveOverworldNavigationTargets();
        basis = targets.Basis;
        destinationText = targets.DestinationText;
        zoneName = targets.ZoneName;

        if (targets.LandingTarget == Vector3.Zero || currentPos == Vector3.Zero)
            return Vector3.Zero;

        return new Vector3(targets.LandingTarget.X, currentPos.Y, targets.LandingTarget.Z);
    }

    private bool ShouldReissueUnderwaterFlagApproach(DateTime now)
    {
        if (!underwaterFlagApproachIssued)
            return true;

        var navState = _plugin.NavigationService.State;
        return (navState == NavigationState.Idle || navState == NavigationState.Error)
            && now - lastUnderwaterFlagApproachTime >= UnderwaterFlagApproachReissueInterval;
    }

    private void SuppressUnderwaterBounceVnav()
    {
        if (_plugin.NavigationService.State == NavigationState.Idle)
            return;

        _plugin.NavigationService.StopNavigation();
        autoMoveActive = false;

        if (!underwaterBounceSuppressedVnavLogged)
        {
            underwaterBounceSuppressedVnavLogged = true;
            _plugin.AddDebugLog("[Underwater] Suppressed upward vnav during thief-map trigger");
        }
    }

    private void EnsureUnderwaterBounceDescent(DateTime now, Vector3 currentPos)
    {
        descentMode = true;

        if (dismountAttemptStart == DateTime.MinValue)
        {
            dismountAttemptStart = now;
            descentStartTime = now;
            descentStartY = currentPos.Y;
        }

        if (!underwaterBounceHoldLogged)
        {
            underwaterBounceHoldLogged = true;
            _plugin.AddDebugLog("[Underwater] Holding descent for thief-map trigger");
        }

        if (descentInProgress || now - lastUnderwaterBounceDescentStart < UnderwaterBounceDescentInterval)
            return;

        lastUnderwaterBounceDescentStart = now;
        descentInProgress = true;
        System.Threading.Tasks.Task.Run(async () =>
        {
            await GameHelpers.PerformDescentAsync();
            descentInProgress = false;
        });
    }

    private bool TryHandleUnderwaterBounceTriggerFlow(bool isDiving, bool includeNearTarget = true)
    {
        if (!IsUnderwaterBounceTriggerFlow(includeNearTarget))
            return false;

        if (TryHandleConfirmedDutyEntry("[Underwater]"))
            return true;

        var portal = FindNearestPortal();
        if (portal != null)
        {
            _plugin.AddDebugLog("[Underwater] Portal detected during thief-map trigger - switching to portal interaction.");
            CheckForPortalAfterChest();
            return true;
        }

        var chest = _plugin.ChestDetectionService.FindNearestCoffer();
        if (chest != null)
        {
            if (State == BotState.OpeningChest)
                return false;

            _plugin.AddDebugLog("[Underwater] Coffer detected during thief-map trigger - transitioning to chest flow.");
            TransitionTo(BotState.OpeningChest, "Looking for treasure coffer to interact...");
            return true;
        }

        var now = DateTime.Now;
        var currentPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;

        if (isDiving)
        {
            if (!wasDiving)
            {
                _plugin.AddDebugLog("[Underwater] Diving state detected - swimming to flag X/Z before thief-map trigger descent");
                wasDiving = true;
            }

            if (underwaterTargetPosition == Vector3.Zero)
            {
                underwaterTargetPosition = ResolveUnderwaterFlagApproachTarget(
                    currentPos,
                    out var basis,
                    out var destinationText,
                    out var zoneName);

                if (underwaterTargetPosition != Vector3.Zero && !underwaterFlagApproachLogged)
                {
                    underwaterFlagApproachLogged = true;
                    _plugin.AddDebugLog(
                        $"[Underwater] Approaching thief-map flag X/Z at dive altitude via {basis} for {destinationText} - {zoneName}; " +
                        $"target={FormatVectorCompact(underwaterTargetPosition)}");
                }
            }

            if (underwaterTargetPosition == Vector3.Zero)
            {
                StateDetail = "Waiting for underwater thief-map flag target...";
                return true;
            }

            var approachXZ = CalculateXZDistance(currentPos, underwaterTargetPosition);
            if (approachXZ > UnderwaterFlagApproachArrivalXZRange)
            {
                if (descentInProgress)
                {
                    GameHelpers.KeyRelease(VirtualKey.CONTROL);
                    GameHelpers.KeyRelease(VirtualKey.SPACE);
                    descentInProgress = false;
                    lastUnderwaterBounceDescentStart = DateTime.MinValue;
                    _plugin.AddDebugLog("[Underwater] Paused thief-map descent until flag X/Z arrival.");
                }

                if (ShouldReissueUnderwaterFlagApproach(now))
                {
                    _plugin.NavigationService.FlyToPosition(underwaterTargetPosition);
                    underwaterFlagApproachIssued = true;
                    lastUnderwaterFlagApproachTime = now;
                    _plugin.AddDebugLog(
                        $"[Underwater] Flying horizontally to thief-map flag X/Z at current dive Y; " +
                        $"xzDistance={approachXZ:F1}y; target={FormatVectorCompact(underwaterTargetPosition)}");
                }

                StateDetail = $"Swimming to underwater flag X/Z... ({approachXZ:F1}y)";
                return true;
            }

            SuppressUnderwaterBounceVnav();
            TryDigWhileDiving("[Underwater] thief-map trigger");
        }
        else if (wasDiving)
        {
            wasDiving = false;
            if (!underwaterBounceReenterLogged)
            {
                underwaterBounceReenterLogged = true;
                _plugin.AddDebugLog("[Underwater] Re-entering descent after leaving Diving before trigger");
            }
        }

        if (!isDiving)
            SuppressUnderwaterBounceVnav();

        EnsureUnderwaterBounceDescent(now, currentPos);
        StateDetail = isDiving
            ? "Holding underwater thief-map trigger..."
            : "Descending for underwater thief-map trigger...";
        return true;
    }

    // ─── State Ticks ─────────────────────────────────────────────────────────

    private void StartPortalRetryWindow()
    {
        portalRetryStart = DateTime.Now;
        ResetPortalWindowState();
    }

    private void EndPortalRetryWindow()
    {
        portalRetryStart = DateTime.MinValue;
        ResetPortalWindowState();
    }

    private void ResetPortalWindowState()
    {
        portalMapFlagCleared = false;
        portalApproachPosition = null;
        lastPortalMountCommandTime = DateTime.MinValue;
        portalLandingStartedAt = DateTime.MinValue;
        lastPortalDismountCommandTime = DateTime.MinValue;
        portalApproachStartedAt = DateTime.MinValue;
        portalApproachStartDistance = float.MaxValue;
        lastPortalRepathTime = DateTime.MinValue;
        portalRegularVnavPathLogged = false;
        lastPortalTimeoutHoldLogTime = DateTime.MinValue;
    }

    private void ResetPortalApproachTrackingForAreaChange()
    {
        portalApproachStartedAt = DateTime.MinValue;
        portalApproachStartDistance = float.MaxValue;
        lastPortalRepathTime = DateTime.MinValue;
    }

    private Vector3 CapturePortalApproachPosition(IGameObject portal)
    {
        if (portalApproachPosition.HasValue)
            return portalApproachPosition.Value;

        portalApproachPosition = portal.Position;
        portalApproachStartedAt = DateTime.MinValue;
        portalApproachStartDistance = float.MaxValue;
        lastPortalRepathTime = DateTime.MinValue;
        _plugin.AddDebugLog($"[Portal] Captured targetable portal XYZ {FormatVectorCompact(portalApproachPosition.Value)}");
        StopPortalConflictingMovement();
        return portalApproachPosition.Value;
    }

    private void StopPortalConflictingMovement()
    {
        var hadMovement = autoMoveActive
            || descentInProgress
            || descentMode
            || underwaterTargetPosition != Vector3.Zero
            || _plugin.NavigationService.State != NavigationState.Idle;

        CommandHelper.SendCommand("/automove off");
        autoMoveActive = false;

        if (descentInProgress)
        {
            GameHelpers.KeyRelease(VirtualKey.CONTROL);
            GameHelpers.KeyRelease(VirtualKey.SPACE);
        }

        descentMode = false;
        descentInProgress = false;
        underwaterTargetPosition = Vector3.Zero;
        if (_plugin.NavigationService.State != NavigationState.Idle)
            _plugin.NavigationService.StopNavigation();

        _plugin.AddDebugLog(hadMovement
            ? "[Portal] Stopped previous navigation before portal XYZ approach."
            : "[Portal] Ensured automove is off before portal XYZ approach.");
    }

    private void ResetOpeningChestCofferMountRecovery(string? reason = null, bool stopNavigation = false)
    {
        var wasActive = openingChestCofferMountRecoveryActive;

        if (stopNavigation && wasActive)
        {
            if (autoMoveActive)
            {
                GameHelpers.StopAutoMove();
                autoMoveActive = false;
            }

            if (_plugin.NavigationService.State != NavigationState.Idle)
            {
                _plugin.NavigationService.StopNavigation();
                if (!string.IsNullOrWhiteSpace(reason))
                    _plugin.AddDebugLog($"[OpeningChest] Stopped coffer mount recovery {reason}.");
            }
        }

        openingChestCofferMountRecoveryEntityId = 0;
        openingChestCofferMountRecoveryActive = false;
        openingChestCofferMountRecoveryRangeReached = false;
        lastOpeningChestCofferMountCommandTime = DateTime.MinValue;
        chestApproachStart = DateTime.MinValue;
    }

    private bool EnsureOpeningChestCofferRecoveryMounted(string chestName, float distance, float yDelta, DateTime now)
    {
        var nav = _plugin.NavigationService;
        if (nav.IsMounted() || nav.IsFlying())
            return true;

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            StateDetail = $"Waiting for combat to end before coffer recovery ({distance:F1}y, Y {Math.Abs(yDelta):F1}y)...";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.Mounting71])
        {
            StateDetail = $"Mounting before coffer recovery ({distance:F1}y, Y {Math.Abs(yDelta):F1}y)...";
            return false;
        }

        if (now - lastOpeningChestCofferMountCommandTime >= OpeningChestCofferMountCommandInterval)
        {
            lastOpeningChestCofferMountCommandTime = now;
            nav.MountUp();
            _plugin.AddDebugLog(
                $"[OpeningChest] Mounting before displaced coffer recovery to '{chestName}' ({distance:F1}y, Y {Math.Abs(yDelta):F1}y).");
        }

        StateDetail = $"Mounting before coffer recovery ({distance:F1}y, Y {Math.Abs(yDelta):F1}y)...";
        return false;
    }

    private bool FlyToOpeningChestCoffer(IGameObject chest, Vector3 playerPosition, float distance, float yDelta, float range, string chestName, DateTime now)
    {
        var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                     Plugin.Condition[ConditionFlag.BoundByDuty56];
        if (inDuty)
            return false;

        var displaced = distance > OpeningChestCofferMountRecoveryDistance ||
                        Math.Abs(yDelta) > OpeningChestCofferMountRecoveryYDelta;
        if (!displaced && !openingChestCofferMountRecoveryActive)
            return false;

        if (openingChestCofferMountRecoveryEntityId != 0 &&
            openingChestCofferMountRecoveryEntityId != chest.EntityId)
        {
            ResetOpeningChestCofferMountRecovery();
        }

        if (distance <= range)
        {
            if (!openingChestCofferMountRecoveryActive && displaced)
            {
                openingChestCofferMountRecoveryActive = true;
                openingChestCofferMountRecoveryRangeReached = true;
                openingChestCofferMountRecoveryEntityId = chest.EntityId;
            }

            if (openingChestCofferMountRecoveryActive && !openingChestCofferMountRecoveryRangeReached)
            {
                _plugin.AddDebugLog(
                    $"[OpeningChest] Coffer mount recovery reached interaction range for '{chestName}' ({distance:F1}y, Y {Math.Abs(yDelta):F1}y).");
                openingChestCofferMountRecoveryRangeReached = true;
            }

            if (autoMoveActive)
            {
                GameHelpers.StopAutoMove();
                autoMoveActive = false;
            }

            if (_plugin.NavigationService.State != NavigationState.Idle)
            {
                _plugin.NavigationService.StopNavigation();
                _plugin.AddDebugLog($"[OpeningChest] Within {range:F1}y - stopped vnav before coffer interaction handoff.");
            }

            var isMountedOrFlying = _plugin.NavigationService.IsMounted()
                || _plugin.NavigationService.IsFlying()
                || Plugin.Condition[ConditionFlag.Mounting71];
            if (isMountedOrFlying)
            {
                if (!Plugin.Condition[ConditionFlag.Mounting71] &&
                    now - lastOpeningChestCofferDismountCommandTime >= OpeningChestCofferDismountCommandInterval)
                {
                    lastOpeningChestCofferDismountCommandTime = now;
                    _mountService.Dismount();
                }

                StateDetail = $"Landing at '{chestName}' ({distance:F1}y, Y {Math.Abs(yDelta):F1}y)...";
                return true;
            }

            ResetOpeningChestCofferMountRecovery();
            Plugin.TargetManager.Target = chest;

            if (!IsCharacterReady())
            {
                StateDetail = $"Waiting to interact with '{chestName}' ({DescribeCharacterReadyBlockers()})...";
                return true;
            }

            if ((now - lastInteractionTime).TotalSeconds >= 1.0)
            {
                lastInteractionTime = now;
                var interacted = GameHelpers.InteractWithObject(chest);
                _plugin.AddDebugLog($"[OpeningChest] Interaction attempt with '{chestName}' after coffer recovery - returned: {interacted}");
            }

            StateDetail = $"Interacting with '{chestName}' after recovery ({distance:F1}y)...";
            return true;
        }

        if (!openingChestCofferMountRecoveryActive)
        {
            openingChestCofferMountRecoveryActive = true;
            openingChestCofferMountRecoveryRangeReached = false;
            openingChestCofferMountRecoveryEntityId = chest.EntityId;
            _plugin.AddDebugLog(
                $"[OpeningChest] Displaced coffer '{chestName}' detected at {FormatVectorCompact(chest.Position)}; " +
                $"player={FormatVectorCompact(playerPosition)}; dist={distance:F1}y; Y={Math.Abs(yDelta):F1}y - using mounted recovery.");

            if (autoMoveActive)
            {
                GameHelpers.StopAutoMove();
                autoMoveActive = false;
            }

            if (_plugin.NavigationService.State != NavigationState.Idle)
            {
                _plugin.NavigationService.StopNavigation();
                _plugin.AddDebugLog("[OpeningChest] Stopped previous coffer navigation before mounted recovery.");
            }
        }

        Plugin.TargetManager.Target = chest;

        if (!EnsureOpeningChestCofferRecoveryMounted(chestName, distance, yDelta, now))
            return true;

        _plugin.NavigationService.FlyToPosition(chest.Position);
        autoMoveActive = true;
        StateDetail = $"Flying to displaced coffer '{chestName}' ({distance:F1}y, Y {Math.Abs(yDelta):F1}y)...";
        return true;
    }

    private bool EnsurePortalFlyApproachMounted()
    {
        var nav = _plugin.NavigationService;
        if (nav.IsMounted() || nav.IsFlying())
            return true;

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            StateDetail = "Waiting for combat to end before portal fly approach...";
            return false;
        }

        var now = DateTime.Now;
        if (Plugin.Condition[ConditionFlag.Mounting71])
        {
            StateDetail = "Mounting before portal fly approach...";
            return false;
        }

        if (now - lastPortalMountCommandTime >= PortalMountCommandInterval)
        {
            lastPortalMountCommandTime = now;
            nav.MountUp();
            _plugin.AddDebugLog("[Portal] Mounting before fly approach to captured portal XYZ.");
        }

        StateDetail = "Mounting before portal fly approach...";
        return false;
    }

    private bool FlyToPortalApproachPosition(Vector3 position, float distance, bool force = false)
    {
        if (!EnsurePortalFlyApproachMounted())
            return false;

        var now = DateTime.Now;
        if (portalApproachStartedAt != DateTime.MinValue
                 && now - portalApproachStartedAt >= PortalRunawayCheckDelay
                 && distance >= portalApproachStartDistance + PortalRunawayDistanceIncrease
                 && now - lastPortalRepathTime >= PortalRepathInterval)
        {
            _plugin.AddDebugLog(
                $"[Portal] Approach distance increased from {portalApproachStartDistance:F1}y to {distance:F1}y after {(now - portalApproachStartedAt).TotalSeconds:F1}s - stopping vnav and retrying portal fly path.");
            _plugin.NavigationService.StopNavigation();
            autoMoveActive = false;
            lastPortalRepathTime = now;
            portalApproachStartedAt = DateTime.MinValue;
            force = true;
        }

        if (!force && portalApproachStartedAt != DateTime.MinValue)
        {
            var portalVnavInactive = _plugin.NavigationService.State != NavigationState.Flying
                || !_plugin.VNavIPC.IsNavigating;
            if (!portalVnavInactive)
                return true;

            _plugin.AddDebugLog("[Portal] Portal vnav approach became inactive before range - reissuing captured XYZ fly path.");
            if (_plugin.NavigationService.State != NavigationState.Idle)
                _plugin.NavigationService.StopNavigation();
            portalApproachStartedAt = DateTime.MinValue;
            portalApproachStartDistance = float.MaxValue;
            force = true;
        }

        StopPortalMovementBeforeVnav();
        portalApproachStartedAt = now;
        portalApproachStartDistance = distance;
        lastPortalRepathTime = now;
        _plugin.NavigationService.FlyToPosition(position);
        autoMoveActive = true;
        _plugin.AddDebugLog($"[Portal] Flying to captured portal XYZ {FormatVectorCompact(position)} ({distance:F1}y).");
        return true;
    }

    private void StopPortalMovementBeforeVnav()
    {
        var stoppedMovement = autoMoveActive
            || descentInProgress
            || descentMode
            || underwaterTargetPosition != Vector3.Zero;

        CommandHelper.SendCommand("/automove off");
        autoMoveActive = false;

        if (descentInProgress || descentMode)
        {
            GameHelpers.KeyRelease(VirtualKey.CONTROL);
            GameHelpers.KeyRelease(VirtualKey.SPACE);
            descentMode = false;
            descentInProgress = false;
            underwaterTargetPosition = Vector3.Zero;
            stoppedMovement = true;
        }

        underwaterTargetPosition = Vector3.Zero;

        _plugin.AddDebugLog(stoppedMovement
            ? "[Portal] Stopped automove/descent before portal vnav path."
            : "[Portal] Ensured automove is off before portal vnav path.");
    }

    private void HandlePortalInInteractionRange(IGameObject portal, Vector3 approachPosition, float portalDist, DateTime now)
    {
        CommandHelper.SendCommand("/automove off");
        autoMoveActive = false;
        portalApproachStartedAt = DateTime.MinValue;
        portalApproachStartDistance = float.MaxValue;

        if (_plugin.NavigationService.State != NavigationState.Idle)
        {
            _plugin.NavigationService.StopNavigation();
            _plugin.AddDebugLog($"[Portal] Within {PortalInteractionRange:F1}y - stopped vnav before portal interaction handoff.");
        }

        var isMountedOrFlying = _plugin.NavigationService.IsMounted()
            || _plugin.NavigationService.IsFlying()
            || Plugin.Condition[ConditionFlag.Mounting71];

        if (isMountedOrFlying)
        {
            if (portalLandingStartedAt == DateTime.MinValue)
            {
                portalLandingStartedAt = now;
                _plugin.AddDebugLog($"[Portal] Within {PortalInteractionRange:F1}y - landing/dismounting before interaction.");
            }

            if (!Plugin.Condition[ConditionFlag.Mounting71] && now - lastPortalDismountCommandTime >= PortalDismountCommandInterval)
            {
                lastPortalDismountCommandTime = now;
                _mountService.Dismount();
            }

            StateDetail = $"Landing at portal ({portalDist:F1}y)...";
            return;
        }

        if (portalLandingStartedAt != DateTime.MinValue)
        {
            _plugin.AddDebugLog($"[Portal] Dismounted at portal after {(now - portalLandingStartedAt).TotalSeconds:F1}s.");
            portalLandingStartedAt = DateTime.MinValue;
        }

        if (!IsCharacterReady())
        {
            StateDetail = $"Waiting to interact with portal ({DescribeCharacterReadyBlockers()})...";
            return;
        }

        if ((now - lastInteractionTime).TotalSeconds >= 1.0)
        {
            EnsurePortalMapFlagCleared();
            Plugin.TargetManager.Target = portal;
            lastInteractionTime = now;
            _plugin.AddDebugLog($"[Portal] Interacting with '{portal.Name.TextValue}' at XYZ {FormatVectorCompact(approachPosition)}...");
            GameHelpers.InteractWithObject(portal);
        }

        StateDetail = $"Interacting with portal ({portalDist:F1}y)...";
    }

    private void EnsurePortalMapFlagCleared()
    {
        if (portalMapFlagCleared)
            return;

        var cleared = GameHelpers.ClearMapFlag(_plugin.MapFlagService.TryReadFlag);
        portalMapFlagCleared = true;
        _plugin.AddDebugLog($"[Portal] Cleared map flag before first portal interaction (verified={cleared}).");
    }

    private void TickSelectingMap()
    {
        if (TickStartMapRefresh())
            return;

        if (saddlebagRetrievalStep != SaddlebagRetrievalStep.Idle)
        {
            TickSaddlebagMapRetrieval();
            return;
        }

        if (TryRecoverActiveKeyItemMap("[SelectingMap]", transitionToDetectingOnActive: true))
            return;

        // Only scan every 3 seconds to reduce log spam
        if ((DateTime.Now - lastMapScanTime).TotalSeconds < 3)
        {
            return;
        }
        lastMapScanTime = DateTime.Now;

        var mapSources = _plugin.InventoryService.ScanForMapSources();
        var maps = mapSources
            .Where(kvp => kvp.Value.Inventory > 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Inventory);
        mapScanCounter++;
        
        // Only log every 5 scans to reduce spam
        if (mapScanCounter % 5 == 1)
        {
            _plugin.AddDebugLog($"[TICK] Scanning inventory... Found {maps.Count} different map types (scan #{mapScanCounter})");
        }
        
        if (maps.Count == 0)
        {
            var saddlebagCandidates = GetEnabledMapCandidates(mapSources, includeInventory: false, includeSaddlebags: true);
            if (saddlebagCandidates.Count > 0)
            {
                _plugin.AddDebugLog($"[SelectingMap] No inventory maps. Retrieving enabled saddlebag map ID {saddlebagCandidates[0]}.");
                TryRetrieveSaddlebagMap(saddlebagCandidates[0]);
                return;
            }

            var enabledForRetainers = _plugin.Configuration.EnabledMapTypes.Count > 0
                ? _plugin.Configuration.EnabledMapTypes.ToList()
                : TreasureMapData.KnownMaps.Keys.ToList();
            _plugin.AddDebugLog($"[SelectingMap] No inventory/saddlebag maps. Checking retainer maps via XADB for {FormatMapIds(enabledForRetainers)}.");
            if (TryRetrieveRetainerMap(enabledForRetainers, "No maps found in inventory, saddlebags, or retainers."))
                return;

            return;
        }

        ClearWarning();

        var enabled = _plugin.Configuration.EnabledMapTypes;

        // Filter to only enabled map types; if none configured, allow all
        var candidates = enabled.Count > 0
            ? maps.Where(kvp => enabled.Contains(kvp.Key) && kvp.Value > 0).Select(kvp => kvp.Key).ToList()
            : maps.Where(kvp => kvp.Value > 0).Select(kvp => kvp.Key).ToList();

        if (candidates.Count == 0)
        {
            _plugin.AddDebugLog(
                $"[SelectingMap] No enabled local maps. Enabled={FormatMapIds(enabled.Count > 0 ? enabled.ToList() : TreasureMapData.KnownMaps.Keys.ToList())}; local map types={maps.Count}.");
            var saddlebagCandidates = GetEnabledMapCandidates(mapSources, includeInventory: false, includeSaddlebags: true);
            if (saddlebagCandidates.Count > 0)
            {
                _plugin.AddDebugLog($"[SelectingMap] Retrieving enabled saddlebag map ID {saddlebagCandidates[0]}.");
                TryRetrieveSaddlebagMap(saddlebagCandidates[0]);
                return;
            }

            _plugin.AddDebugLog("[SelectingMap] No enabled saddlebag maps. Checking enabled retainer maps via XADB.");
            if (TryRetrieveRetainerMap(enabled.Count > 0 ? enabled.ToList() : TreasureMapData.KnownMaps.Keys.ToList(),
                    "No enabled maps in inventory, saddlebags, or retainers. Check map selection in UI."))
                return;

            return;
        }

        // Don't sort - use inventory order to match menu order
        SelectedMapItemId = candidates[0];
        var mapName = TreasureMapData.KnownMaps.TryGetValue(SelectedMapItemId, out var info) ? info.Name : $"ID {SelectedMapItemId}";
        currentLandingMode = ResolveLandingMode(SelectedMapItemId);
        _plugin.AddDebugLog($"Selected: {mapName} (ID {SelectedMapItemId}).");
        _plugin.AddDebugLog($"[Landing] SelectedMapItemId={SelectedMapItemId}, Using {currentLandingMode} for this run.");
        
        // Initialize map count validation variables
        initialMapCount = _plugin.InventoryService.GetMapCount(SelectedMapItemId);
        mapCountChecked = false;
        mapOpeningRetried = false;
        ResetPerMapCommandTriggers();
        _plugin.AddDebugLog($"[SelectingMap] Initial map count: {initialMapCount}");
        
        // Clear any existing flag to prevent conflicts with new map run
        // Skip during zone transitions to prevent AgentHUD.UpdateNaviMap crashes
        bool loading = Plugin.Condition[ConditionFlag.BetweenAreas] || 
                       Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (!loading)
        {
            var cleared = GameHelpers.ClearMapFlag(_plugin.MapFlagService.TryReadFlag);
            _plugin.AddDebugLog($"[SelectingMap] Cleared existing map flag (verified={cleared})");
        }
        else
        {
            _plugin.AddDebugLog($"[SelectingMap] Skipping flag clear during zone transition");
        }
        
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
        // Fire more frequently to handle confirmation dialogs better
        if (GameHelpers.ClickYesIfVisible())
        {
            _plugin.AddDebugLog("[OpeningMap] Clicked Yes on decipher confirmation dialog");
        }

        // After /item command, wait for the decipher dialog + flag to set
        // Transition to detection after a short delay to allow the game to process
        if ((DateTime.Now - stateStartTime).TotalSeconds > 4)
            TransitionTo(BotState.DetectingLocation, "Map opened, reading location...");
    }

    private MapLocation? ResolveDetectedMapLocation(MapLocation? agentMapLocation, MapLocation? capturedLocation)
    {
        var isKnownThiefMap = IsThiefMap(SelectedMapItemId);
        var isKnownNonThiefMap = SelectedMapItemId != 0 && !isKnownThiefMap;

        if (isKnownNonThiefMap && capturedLocation != null)
        {
            if (agentMapLocation == null)
            {
                _plugin.AddDebugLog("[DetectingLocation] Location source=TreasureSpot; AgentMap has no flag.");
                return capturedLocation;
            }

            if (IsSameDetectedLocation(agentMapLocation, capturedLocation))
            {
                _plugin.AddDebugLog(
                    $"[DetectingLocation] Location source=TreasureSpot; AgentMap Y {agentMapLocation.Y:F1} is player-height fallback, " +
                    $"captured Y {capturedLocation.Y:F1} wins for non-thief map.");
                return capturedLocation;
            }

            _plugin.AddDebugLog(
                $"[DetectingLocation] TreasureSpot capture did not match AgentMap flag within {CapturedLocationMatchXZRange:F0}y XZ; " +
                $"using AgentMap flag. agent=T{agentMapLocation.TerritoryId} ({agentMapLocation.X:F1},{agentMapLocation.Z:F1}) " +
                $"capture=T{capturedLocation.TerritoryId} ({capturedLocation.X:F1},{capturedLocation.Z:F1})");
        }

        if (agentMapLocation != null)
        {
            _plugin.AddDebugLog(isKnownThiefMap
                ? "[DetectingLocation] Location source=AgentMap; preserving thief-map underwater path precedence."
                : $"[DetectingLocation] Location source=AgentMapPlayerY; Y {agentMapLocation.Y:F1} is current-player height fallback.");
            return agentMapLocation;
        }

        if (capturedLocation != null)
        {
            _plugin.AddDebugLog(isKnownThiefMap
                ? "[DetectingLocation] Location source=TreasureSpot fallback; AgentMap missing for thief map."
                : "[DetectingLocation] Location source=TreasureSpot; AgentMap missing.");
            return capturedLocation;
        }

        return null;
    }

    private static bool IsSameDetectedLocation(MapLocation agentMapLocation, MapLocation capturedLocation)
    {
        if (agentMapLocation.TerritoryId != capturedLocation.TerritoryId)
            return false;

        var agentPos = new Vector3(agentMapLocation.X, 0f, agentMapLocation.Z);
        var capturedPos = new Vector3(capturedLocation.X, 0f, capturedLocation.Z);
        return CalculateXZDistance(agentPos, capturedPos) <= CapturedLocationMatchXZRange;
    }

    private void TickDetectingLocation()
    {
        if (TryRecoverActiveKeyItemMap("[DetectingLocation]", transitionToDetectingOnActive: false))
            return;

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

        // AgentMap provides flag X/Z but not reliable destination height. TreasureSpot capture has true XYZ.
        var agentMapLocation = _plugin.MapFlagService.TryGetMapLocation();
        MapLocation? capturedLocation = null;
        if (_plugin.TreasureMapLocationService.TryGetLatestLocation(SelectedMapItemId, activeKeyItemMapItemId, out var latestCapturedLocation))
            capturedLocation = latestCapturedLocation;

        var location = ResolveDetectedMapLocation(agentMapLocation, capturedLocation);

        if (location != null)
        {
            // Find nearest aetheryte to navigate from (pass flag position for closest-to-target selection)
            var flagPos = new Vector3(location.X, location.Y, location.Z);
            var aetheryteId = _plugin.NavigationService.FindNearestAetheryte(location.TerritoryId, flagPos, out var bestAethDist, out var usedXyz);
            location.NearestAetheryteId = aetheryteId;

            // Populate aetheryte name for passive recording
            if (aetheryteId > 0)
            {
                try
                {
                    var aetheryteSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
                    if (aetheryteSheet != null)
                    {
                        var aetheryte = aetheryteSheet.GetRow(aetheryteId);
                        location.NearestAetheryteName = aetheryte.PlaceName.ValueNullable?.Name.ToString() ?? $"ID {aetheryteId}";
                    }
                }
                catch { }
            }

            SetLocation(location);

            if (Plugin.ClientState.TerritoryType == location.TerritoryId)
            {
                // Already in zone - compare player distance vs aetheryte distance to decide if teleporting is worth it
                var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
                double playerDist;
                if (usedXyz)
                {
                    // We have community RealXYZ - use full 3D distance for player too
                    var dbEntry = _plugin.MapLocationDatabase?.FindEntry(location.TerritoryId, location.X, location.Z);
                    if (dbEntry != null && dbEntry.HasRealXYZ)
                    {
                        var dx = playerPos.X - dbEntry.RealX;
                        var dy = playerPos.Y - dbEntry.RealY;
                        var dz = playerPos.Z - dbEntry.RealZ;
                        playerDist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    }
                    else
                    {
                        var dx = playerPos.X - location.X;
                        var dz = playerPos.Z - location.Z;
                        playerDist = Math.Sqrt(dx * dx + dz * dz);
                    }
                }
                else
                {
                    // No community RealXYZ - use XZ only for player distance
                    var dx = playerPos.X - location.X;
                    var dz = playerPos.Z - location.Z;
                    playerDist = Math.Sqrt(dx * dx + dz * dz);
                }

                _plugin.AddDebugLog($"[DetectingLocation] Already in zone: player {(usedXyz ? "XYZ" : "XZ")} dist={playerDist:F0}y, best aetheryte dist={bestAethDist:F0}y");
                _plugin.AddDebugLog($"[DetectingLocation] Player pos: ({playerPos.X:F1}, {playerPos.Y:F1}, {playerPos.Z:F1}), Aetheryte ID: {aetheryteId}");

                var playerXZDistToFlag = CalculateXZDistance(playerPos, flagPos);
                if (playerXZDistToFlag <= MapDigXZRange)
                {
                    _plugin.AddDebugLog($"[DetectingLocation] Already within dig range ({playerXZDistToFlag:F1}y XZ) - landing/digging without teleport or mount setup.");
                    TransitionTo(BotState.Flying, "Already at map location - landing and digging...");
                    return;
                }

                var playerXZDistToAetheryte = aetheryteId != 0
                    ? _plugin.NavigationService.GetPlayerXZDistanceToAetheryte(aetheryteId)
                    : null;

                if (playerXZDistToAetheryte is { } aetherytePlayerDistance
                    && aetherytePlayerDistance <= SameZoneAetheryteTeleportSkipXZRange)
                {
                    _plugin.AddDebugLog(
                        $"[DetectingLocation] Player is already within {SameZoneAetheryteTeleportSkipXZRange:F0}y XZ of selected aetheryte {aetheryteId} " +
                        $"({aetherytePlayerDistance:F1}y) - skipping same-zone teleport.");
                    TransitionTo(BotState.Mounting, "Already near selected aetheryte - mounting up...");
                    return;
                }

                if (aetheryteId != 0 && bestAethDist < playerDist && bestAethDist != double.MaxValue)
                {
                    // Aetheryte is closer than player - teleport
                    _plugin.AddDebugLog($"[DetectingLocation] Aetheryte is closer ({bestAethDist:F0}y < {playerDist:F0}y) - teleporting to aetheryte {aetheryteId}");
                    TransitionTo(BotState.Teleporting, $"In zone but aetheryte closer ({bestAethDist:F0}y vs {playerDist:F0}y) - teleporting...");
                }
                else
                {
                    if (aetheryteId == 0)
                        _plugin.AddDebugLog($"[DetectingLocation] No valid aetheryte found ({aetheryteId}) - mounting up");
                    else if (bestAethDist == double.MaxValue)
                        _plugin.AddDebugLog($"[DetectingLocation] Aetheryte has no position data (dist=∞) - mounting up, no teleport possible");
                    else
                        _plugin.AddDebugLog($"[DetectingLocation] Player is closer ({playerDist:F0}y <= {bestAethDist:F0}y) - mounting up, no teleport needed");
                    TransitionTo(BotState.Mounting, "Already in zone & closer than aetheryte! Mounting up...");
                }
            }
            else
            {
                TransitionTo(BotState.Teleporting, $"Teleporting to {location.ZoneName}...");
            }
            return;
        }

        // Not found yet - keep polling (timeout handled by StateTimeouts)
        var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
        StateDetail = $"Waiting for map location... ({elapsed:F0}s / {StateTimeouts[BotState.DetectingLocation]}s)";
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

        // Minimum wait after teleport command to allow animation to start and complete
        // Without this, same-zone teleports transition instantly because BetweenAreas hasn't been set yet
        var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
        if (elapsed < 5.0)
        {
            StateDetail = $"Teleporting... ({elapsed:F0}s)";
            return;
        }

        // Check if teleport finished (no longer between areas and in correct territory)
        if (!nav.IsTeleporting())
        {
            var currentTerritory = Plugin.ClientState.TerritoryType;
            if (CurrentLocation != null && currentTerritory == CurrentLocation.TerritoryId)
            {
                _plugin.AddDebugLog($"[Teleporting] Arrived after {elapsed:F1}s");

                // Passively record aetheryte position for future nearest-aetheryte lookups
                // Record when within 20y of estimated aetheryte location (XZ only)
                if (CurrentLocation?.NearestAetheryteId > 0)
                {
                    var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
                    if (playerPos != Vector3.Zero)
                    {
                        // Get estimated position from Level sheet or MapMarker
                        var estimatedPos = _plugin.NavigationService.GetEstimatedAetherytePosition(CurrentLocation.NearestAetheryteId);
                        if (estimatedPos != Vector3.Zero)
                        {
                            // Check XZ distance only (ignore Y)
                            var dx = playerPos.X - estimatedPos.X;
                            var dz = playerPos.Z - estimatedPos.Z;
                            var xzDist = Math.Sqrt(dx * dx + dz * dz);
                            
                            _plugin.AddDebugLog($"[Aetheryte] {CurrentLocation.NearestAetheryteName} - Player pos: ({playerPos.X:F1}, {playerPos.Z:F1}), Est pos: ({estimatedPos.X:F1}, {estimatedPos.Z:F1}), XZ dist: {xzDist:F1}y");
                            
                            if (xzDist <= 20.0f)
                            {
                                _plugin.AddDebugLog($"[Aetheryte] RECORDING {CurrentLocation.NearestAetheryteId} - within 20y!");
                                _plugin.AetherytePositionDatabase.RecordPosition(
                                    CurrentLocation.NearestAetheryteId,
                                    CurrentLocation.NearestAetheryteName,
                                    playerPos.X, playerPos.Y, playerPos.Z);
                            }
                        }
                        else
                        {
                            _plugin.AddDebugLog($"[Aetheryte] No estimated position for {CurrentLocation.NearestAetheryteName} (ID {CurrentLocation.NearestAetheryteId})");
                        }
                    }
                }

                TransitionTo(BotState.Mounting, "Arrived! Mounting up...");
            }
            else if (elapsed > 15)
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
            var waitForParty = _plugin.Configuration.WaitForParty;
            _plugin.AddDebugLog($"[Mounting] PartySize={partySize}, WaitForParty={waitForParty}");
            
            if (partySize > 0 && waitForParty)
                TransitionTo(BotState.WaitingForParty, "Waiting for party to mount...");
            else
                TransitionTo(BotState.Flying, "Mounted! Flying to location...");
            return;
        }

        // Grace period: wait 3s after entering Mounting state before first attempt
        // This gives time for post-teleport animations and loading to complete
        var sinceStateStart = (DateTime.Now - stateStartTime).TotalSeconds;
        if (sinceStateStart < 3.0)
        {
            StateDetail = $"Preparing to mount ({3 - (int)sinceStateStart}s)...";
            return;
        }

        // Try mounting up to 5 times with 3s delays
        if (mountAttemptStart == DateTime.MinValue)
        {
            mountAttemptStart = DateTime.Now;
            mountAttempts = 0;
        }

        var mountElapsed = (DateTime.Now - mountAttemptStart).TotalSeconds;

        if (mountAttempts < 5)
        {
            if (mountElapsed >= mountAttempts * 3.0) // 0s, 3s, 6s, 9s, 12s
            {
                mountAttempts++;

                // Log condition flags to diagnose mount failures
                var condition = Plugin.Condition;
                var casting = condition[ConditionFlag.Casting];
                var occupied = condition[ConditionFlag.Occupied];
                var betweenAreas = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
                var mounting = condition[ConditionFlag.Mounting71];
                _plugin.AddDebugLog($"[Mounting] Attempt {mountAttempts}/5 — Casting={casting} Occupied={occupied} BetweenAreas={betweenAreas} Mounting71={mounting}");

                nav.MountUp();
            }
            StateDetail = $"Mounting (attempt {mountAttempts}/5)...";
            return;
        }
        else
        {
            _plugin.AddDebugLog($"[Mounting] Failed to mount after 5 attempts - resetting bot");
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
        // Check for diving state change (Condition 81)
        bool isDiving = IsDivingForCurrentMap();
        if (TryHandleUnderwaterBounceTriggerFlow(isDiving, includeNearTarget: false))
            return;

        if (isDiving && !wasDiving)
        {
            if (CurrentLocation == null)
            {
                HandleError("No location data for underwater navigation.");
                return;
            }

            // Just entered diving state - switch to underwater navigation
            _plugin.AddDebugLog("[Underwater] Diving state detected - switching to underwater navigation");
            wasDiving = true;
            
            // Get current map entry for destination info
            var currentEntry = _plugin.MapLocationDatabase.FindEntry(CurrentLocation.TerritoryId, CurrentLocation.X, CurrentLocation.Z);
            int destinationIndex = currentEntry?.Index > 0 ? currentEntry.Index : -1;
            string destinationText = destinationIndex > 0 ? $"Destination #{destinationIndex}" : "Unknown";
            string zoneName = currentEntry?.ZoneName ?? "Unknown";
            
            // Resolve underwater target; thief maps use stored landing XYZ when no special nav exists.
            underwaterTargetPosition = ResolveUnderwaterTargetPosition(currentEntry, destinationIndex, out var underwaterTargetBasis);
            _plugin.AddDebugLog($"[Underwater] Using {underwaterTargetBasis} for {destinationText} - {zoneName}");

            // Stop current navigation and fly directly to target
            _plugin.NavigationService.StopNavigation();
            if (underwaterTargetPosition != Vector3.Zero)
            {
                _plugin.NavigationService.FlyToPosition(underwaterTargetPosition);
                StateDetail = $"[Underwater {destinationText}] Flying to {zoneName} XYZ: {CommandHelper.FormatVector(underwaterTargetPosition)}";
            }
            return;
        }
        else if (!isDiving && wasDiving)
        {
            // Exited diving state
            _plugin.AddDebugLog("[Underwater] Exited diving state");
            wasDiving = false;
        }

        TryDigWhileDiving("[Underwater] flying");
        
        // Rate limit diving checks to every 2 seconds
        if ((DateTime.Now - lastDivingCheck).TotalSeconds < 2.0) return;
        lastDivingCheck = DateTime.Now;
        
        // Original flying logic continues...
        if (CurrentLocation == null)
        {
            HandleError("No location data for navigation.");
            return;
        }

        var nav = _plugin.NavigationService;

        if (!stateActionIssued)
        {
            // Check if we're already close enough to skip pathfinding entirely
            var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            var initialNavTargets = ResolveOverworldNavigationTargets();
            var xzDist2 = CalculateXZDistance(playerPos, initialNavTargets.LandingTarget);
            if (xzDist2 <= MapDigXZRange)
            {
                if (currentLandingMode == OverworldLandingMode.UnderwaterBounce)
                {
                    _plugin.AddDebugLog(
                        $"[Flying] Already within {xzDist2:F1}y of landing target ({initialNavTargets.Basis}) - " +
                        "dive landing mode active, skipping immediate dismount/dig");
                    stateActionIssued = true;
                    if (TryHandleUnderwaterBounceTriggerFlow(isDiving))
                        return;
                }
                else
                {
                    if (TryHandleMapLandingAndDig(
                            "[Flying] immediate landing",
                            initialNavTargets.Basis,
                            playerPos,
                            initialNavTargets.LandingTarget,
                            xzDist2))
                        return;
                }
            }
            else
            {
                nav.FlyToPosition(initialNavTargets.NavigationTarget);
                _plugin.AddDebugLog(
                    $"[Flying] Issued navigation using {initialNavTargets.Basis} for {initialNavTargets.DestinationText} - {initialNavTargets.ZoneName}; " +
                    $"navTarget={FormatVectorCompact(initialNavTargets.NavigationTarget)}; " +
                    $"landingTarget={FormatVectorCompact(initialNavTargets.LandingTarget)}");
                StateDetail =
                    $"[{initialNavTargets.DestinationText}] Flying to {initialNavTargets.ZoneName} ({initialNavTargets.Basis}) XYZ: {CommandHelper.FormatVector(initialNavTargets.NavigationTarget)}";
                stateActionIssued = true;
                lastStuckCheckPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
                lastStuckCheckTime = DateTime.Now;
                return;
            }
        }

        if (nav.State == NavigationState.Error)
        {
            HandleError($"Navigation error: {nav.StateDetail}");
            return;
        }

        var now = DateTime.Now;
        if (TryFallbackToFlyFlagAfterVnavFailure(now))
            return;

        var currentPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var activeNavTargets = ResolveOverworldNavigationTargets();
        var distanceFromTarget = Vector3.Distance(currentPos, activeNavTargets.NavigationTarget);
        var xzDist = CalculateXZDistance(currentPos, activeNavTargets.LandingTarget);

        if (currentLandingMode == OverworldLandingMode.UnderwaterBounce
            && xzDist <= UnderwaterBounceTriggerXZRange
            && TryHandleUnderwaterBounceTriggerFlow(isDiving))
        {
            return;
        }
        
        // Stuck detection: only re-pathfind if stuck (10+ seconds without moving 5+ yalms)
        var sinceStuckCheck = (now - lastStuckCheckTime).TotalSeconds;
        if (sinceStuckCheck >= 10.0 && distanceFromTarget > 5.0f)
        {
            var movedDistance = Vector3.Distance(currentPos, lastStuckCheckPos);
            if (movedDistance < 5.0f)
            {
                // Stuck! Stop current navigation before re-pathfinding to prevent erratic movement
                nav.StopNavigation();
                nav.FlyToPosition(activeNavTargets.NavigationTarget);
                _plugin.AddDebugLog(
                    $"[Flying] Stuck detected (moved {movedDistance:F1}y in 10s) - stopped + re-pathfinding using {activeNavTargets.Basis}; " +
                    $"navDistance={distanceFromTarget:F1}y; current={FormatVectorCompact(currentPos)}; " +
                    $"navTarget={FormatVectorCompact(activeNavTargets.NavigationTarget)}; landingTarget={FormatVectorCompact(activeNavTargets.LandingTarget)}");
            }
            lastStuckCheckPos = currentPos;
            lastStuckCheckTime = now;
        }

        // Check if we're close enough to X,Z coordinates (within 5 yalms) — uses ground target, not elevated
        // If we're not mounted, we've already dismounted - proceed with dig regardless of nav state
        if (!_plugin.NavigationService.IsMounted() && dismountAttemptStart != DateTime.MinValue)
        {
            _plugin.AddDebugLog("Successfully dismounted - proceeding with map content");
            if (TryHandleMapLandingAndDig(
                    "[Flying] dismounted landing",
                    activeNavTargets.Basis,
                    currentPos,
                    activeNavTargets.LandingTarget,
                    xzDist))
                return;
        }
        
        if ((activeNavTargets.UseNavStateForLanding && (nav.State == NavigationState.Arrived || nav.State == NavigationState.Idle)) || xzDist < 5.0f)
        {
            if (currentLandingMode == OverworldLandingMode.MountToggle &&
                TryHandleMapLandingAndDig(
                    "[Flying] landing",
                    activeNavTargets.Basis,
                    currentPos,
                    activeNavTargets.LandingTarget,
                    xzDist))
            {
                return;
            }

            // We've arrived at the flag X,Z — now we need to dismount
            if (_plugin.NavigationService.IsMounted())
            {
                // Check if all party members are within 10y before dismounting (Issue 3)
                var bypassPartyWaitForDive = currentLandingMode == OverworldLandingMode.UnderwaterBounce;
                var waitForPartyDismount = _plugin.Configuration.PartyWaitBeforeDismount && !bypassPartyWaitForDive;
                _plugin.AddDebugLog(
                    $"[Dismount] PartyWaitBeforeDismount={_plugin.Configuration.PartyWaitBeforeDismount}, " +
                    $"LandingMode={currentLandingMode}, BypassForDive={bypassPartyWaitForDive}");
                if (bypassPartyWaitForDive)
                {
                    _plugin.AddDebugLog("[Dismount] Skipping party wait for thief-map dive landing.");
                }
                if (waitForPartyDismount)
                {
                    var partyWait = EvaluateOverworldLandingPartyWait(10.0);
                    if (!partyWait.CanProceed)
                    {
                        StateDetail = BuildOverworldLandingPartyWaitDetail(partyWait, 10.0);
                        return; // Don't attempt dismount yet
                    }
                }

                // Record when we first started trying to land at this location
                if (dismountAttemptStart == DateTime.MinValue)
                {
                    dismountAttemptStart = DateTime.Now;
                    descentMode = currentLandingMode == OverworldLandingMode.UnderwaterBounce;
                    descentStartTime = DateTime.Now;
                    descentStartY = Plugin.ObjectTable.LocalPlayer?.Position.Y ?? 0f;
                    _plugin.AddDebugLog(
                        $"[Flying] Landing phase ready via {activeNavTargets.Basis}; navState={nav.State}; " +
                        $"landingXZ={xzDist:F1}y; current={FormatVectorCompact(currentPos)}; " +
                        $"landingTarget={FormatVectorCompact(activeNavTargets.LandingTarget)}");
                    _plugin.AddDebugLog(currentLandingMode == OverworldLandingMode.UnderwaterBounce
                        ? "Close to target - attempting underwater descent+dismount mode (Ctrl+Space first)..."
                        : "Close to target - using /mount landing toggles until dismounted...");
                }

                var dismountElapsed = (DateTime.Now - dismountAttemptStart).TotalSeconds;
                var descentElapsed = (DateTime.Now - descentStartTime).TotalSeconds;

                if (currentLandingMode == OverworldLandingMode.MountToggle)
                {
                    if (dismountElapsed < 60.0)
                    {
                        _mountService.TryLandingToggle();
                        StateDetail = $"Landing by /mount toggle... ({dismountElapsed:F0}s)";
                        return;
                    }

                    StateDetail = $"Still trying to land by /mount toggle... ({dismountElapsed:F0}s)";
                    return;
                }

                // DESCENT+DISMOUNT MODE: Try Ctrl+Space first, monitor Y change
                if (descentMode)
                {
                    if (!descentInProgress)
                    {
                        // Start Ctrl+Space descent
                        descentInProgress = true;
                        _plugin.AddDebugLog($"[Flying] Starting Ctrl+Space descent attempt ({descentElapsed:F0}s into dismount)");
                        
                        System.Threading.Tasks.Task.Run(async () => {
                            await GameHelpers.PerformDescentAsync();
                            descentInProgress = false;
                        });
                    }
                    
                    // Monitor Y position change
                    var currentY = Plugin.ObjectTable.LocalPlayer?.Position.Y ?? 0f;
                    var yChange = Math.Abs(currentY - descentStartY);
                    
                    if (descentElapsed >= 5.0)
                    {
                        if (yChange < 5.0f)
                        {
                            // Y didn't change much - switch to normal dismount
                            _plugin.AddDebugLog($"[Flying] Ctrl+Space descent ineffective (Y change: {yChange:F1}y) - switching to normal dismount");
                            descentMode = false;
                            descentInProgress = false;
                        }
                        else
                        {
                            // Y changed significantly - reset monitoring and continue descent
                            _plugin.AddDebugLog($"[Flying] Ctrl+Space descent working (Y change: {yChange:F1}y) - continuing descent");
                            descentStartTime = DateTime.Now;
                            descentStartY = currentY;
                        }
                    }
                    
                    StateDetail = $"Descent mode... (Y change: {yChange:F1}y, {descentElapsed:F0}s)";
                    return;
                }

                // NORMAL DISMOUNT MODE: Standard dismount attempts
                if (dismountElapsed < 60.0)
                {
                    // Attempt dismount every 2 seconds
                    if ((int)dismountElapsed % 2 == 0)
                    {
                        _mountService.Dismount();
                    }
                    StateDetail = $"Normal dismount... ({dismountElapsed:F0}s)";
                    return;
                }

                // Fallback: Try Ctrl+Space as last resort
                if (!descentInProgress)
                {
                    descentInProgress = true;
                    _plugin.AddDebugLog($"[Flying] Normal dismount failed after {dismountElapsed:F0}s - trying Ctrl+Space as fallback");
                    
                    System.Threading.Tasks.Task.Run(async () => {
                        await GameHelpers.PerformDescentAsync();
                        descentInProgress = false;
                    });
                }
                
                StateDetail = $"Fallback descent... ({dismountElapsed:F0}s)";
                return;
            }
        }
    }

    private void TickOpeningChest()
    {
        // Check for diving state change - if we just entered diving, go to underwater navigation
        bool isDiving = IsDivingForCurrentMap();
        if (TryHandleUnderwaterBounceTriggerFlow(isDiving, includeNearTarget: false))
            return;

        if (isDiving && currentLandingMode == OverworldLandingMode.UnderwaterBounce)
        {
            if (!wasDiving)
            {
                underwaterTargetPosition = ResolveUnderwaterTargetPosition(out var destinationText, out var zoneName, out var underwaterTargetBasis);
                _plugin.AddDebugLog($"[Underwater] Diving detected during chest phase - holding thief-map trigger at {destinationText} - {zoneName} ({underwaterTargetBasis})");
                wasDiving = true;
            }

            SuppressUnderwaterBounceVnav();
        }
        else if (isDiving && !wasDiving)
        {
            if (CurrentLocation == null)
            {
                HandleError("No location data for underwater chest navigation.");
                return;
            }

            // Get current map entry for destination info
            var currentEntry = _plugin.MapLocationDatabase.FindEntry(CurrentLocation.TerritoryId, CurrentLocation.X, CurrentLocation.Z);
            int destinationIndex = currentEntry?.Index > 0 ? currentEntry.Index : -1;
            string destinationText = destinationIndex > 0 ? $"Destination #{destinationIndex}" : "Unknown";
            string zoneName = currentEntry?.ZoneName ?? "Unknown";
            
            _plugin.AddDebugLog($"[Underwater] Diving detected during chest phase - {destinationText} - {zoneName}");
            wasDiving = true;

            // Resolve underwater target; thief maps use stored landing XYZ when no special nav exists.
            underwaterTargetPosition = ResolveUnderwaterTargetPosition(currentEntry, destinationIndex, out var underwaterTargetBasis);
            _plugin.AddDebugLog($"[Underwater] Using {underwaterTargetBasis} for {destinationText} - {zoneName}");

            // Stop current navigation and fly directly to target
            _plugin.NavigationService.StopNavigation();
            if (underwaterTargetPosition != Vector3.Zero)
            {
                _plugin.NavigationService.FlyToPosition(underwaterTargetPosition);
            }
            return;
        }

        // Click Yes on any dialog (Open the treasure coffer? etc)
        GameHelpers.ClickYesIfVisible();
        
        // Check for portal EVERY tick - portals can appear during/after combat
        var portal = FindNearestPortal();
        if (portal != null)
        {
            _plugin.AddDebugLog("[OpeningChest] Portal detected - transitioning to portal interaction...");
            ResetOpeningChestCofferMountRecovery();
            StopPortalConflictingMovement();
            CheckForPortalAfterChest();
            return;
        }

        TryDigWhileDiving("[Underwater] chest phase");
        
        // No portal yet - keep working on chest
        var chest = _plugin.ChestDetectionService.FindNearestCoffer();
        var now = DateTime.Now;
        bool inCombat = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        var hasFlagRecoveryTarget = TryGetCurrentFlagRecoveryTarget(out var flagRecoveryTarget, out var distToFlag);
        var nearFlagForRecovery = hasFlagRecoveryTarget && distToFlag <= 30f;
        var shouldReturnToFlag = hasFlagRecoveryTarget && distToFlag > 30f;

        if (chest == null)
        {
            ResetOpeningChestCofferMountRecovery("because chest disappeared", stopNavigation: !inCombat);

            if (openingChestCombatInterrupted)
            {
                if (inCombat)
                {
                    chestDisappearedTime = DateTime.MinValue;
                    StateDetail = hasFlagRecoveryTarget
                        ? $"In combat - waiting to recover chest ({distToFlag:F1}y from flag)..."
                        : "In combat - waiting to recover chest...";
                    return;
                }

                var sinceCombatEnd = lastCombatEndTime == DateTime.MinValue
                    ? double.MaxValue
                    : (now - lastCombatEndTime).TotalSeconds;

                if (sinceCombatEnd < 2.0)
                {
                    chestDisappearedTime = DateTime.MinValue;
                    StateDetail = $"Combat ended - waiting for chest recovery... ({sinceCombatEnd:F1}/2.0s)";
                    return;
                }

                if (shouldReturnToFlag)
                {
                    if (!openingChestReturningToFlag)
                    {
                        _plugin.AddDebugLog($"[OpeningChest] Combat recovery: no chest visible and {distToFlag:F1}y from flag - moving back to flag");
                        openingChestReturningToFlag = true;
                    }

                    _plugin.NavigationService.MoveToPosition(flagRecoveryTarget);
                    autoMoveActive = true;
                    chestDisappearedTime = DateTime.MinValue;
                    StateDetail = $"Returning to flag after combat ({distToFlag:F1}y)...";
                    return;
                }

                if (openingChestReturningToFlag)
                {
                    _plugin.AddDebugLog($"[OpeningChest] Combat recovery: back near flag ({distToFlag:F1}y) - rechecking chest and dig");
                    if (autoMoveActive)
                    {
                        _plugin.NavigationService.StopNavigation();
                        autoMoveActive = false;
                    }
                    openingChestReturningToFlag = false;
                    chestDisappearedTime = DateTime.MinValue;
                }

                if (!openingChestRecoveryDigIssued && nearFlagForRecovery)
                {
                    var sinceDig = (now - lastDigTime).TotalSeconds;
                    if (sinceDig < 3.0)
                    {
                        StateDetail = $"Combat ended - waiting to retry dig... ({sinceDig:F1}/3.0s)";
                        return;
                    }

                    _plugin.AddDebugLog($"[OpeningChest] Combat recovery: no chest visible near flag ({distToFlag:F1}y) - retrying dig");
                    CommandHelper.SendCommand("/gaction dig");
                    lastDigTime = now;
                    openingChestRecoveryDigIssued = true;
                    chestDisappearedTime = now;
                    StateDetail = $"Retrying dig after combat ({distToFlag:F1}y from flag)...";
                    return;
                }

                if (chestDisappearedTime == DateTime.MinValue)
                {
                    chestDisappearedTime = now;
                    _plugin.AddDebugLog(openingChestRecoveryDigIssued
                        ? "[OpeningChest] Waiting for chest after combat recovery dig"
                        : hasFlagRecoveryTarget
                            ? $"[OpeningChest] Combat recovery: no chest visible after combat ({distToFlag:F1}y from flag) - waiting briefly before portal check"
                            : "[OpeningChest] Combat recovery: no chest visible after combat - waiting briefly before portal check");
                }

                var recoveryGrace = (now - chestDisappearedTime).TotalSeconds;
                if (recoveryGrace < 5.0)
                {
                    StateDetail = openingChestRecoveryDigIssued
                        ? $"Waiting for chest after combat dig... ({recoveryGrace:F1}/5.0s)"
                        : $"Waiting for chest after combat... ({recoveryGrace:F1}/5.0s)";
                    return;
                }

                _plugin.AddDebugLog(openingChestRecoveryDigIssued
                    ? "[OpeningChest] No chest found after combat recovery dig - checking for portal"
                    : "[OpeningChest] No chest found after combat recovery window - checking for portal");
                openingChestCombatInterrupted = false;
                openingChestRecoveryDigIssued = false;
                openingChestReturningToFlag = false;
                chestDisappearedTime = DateTime.MinValue;
                CheckForPortalAfterChest();
                return;
            }

            // Start grace period timer if not already started
            if (chestDisappearedTime == DateTime.MinValue)
            {
                chestDisappearedTime = now;
                _plugin.AddDebugLog("[OpeningChest] Chest disappeared - starting 5s grace period");
            }
            
            var gracePeriod = (now - chestDisappearedTime).TotalSeconds;
            
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

        chestConfirmedThisMap = true;

        // Chest exists - reset grace period timer
        chestDisappearedTime = DateTime.MinValue;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            StateDetail = "Waiting for player before coffer interaction...";
            return;
        }

        var dist = Vector3.Distance(player.Position, chest.Position);
        var yDelta = player.Position.Y - chest.Position.Y;
        var range = _plugin.Configuration.ChestInteractionRange;
        var chestName = chest.Name.TextValue;

        if (openingChestCombatInterrupted && !inCombat)
        {
            if (openingChestReturningToFlag && autoMoveActive)
            {
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
            }

            _plugin.AddDebugLog($"[OpeningChest] Chest reacquired after combat at {dist:F1}y - resuming approach");
            openingChestCombatInterrupted = false;
            openingChestRecoveryDigIssued = false;
            openingChestReturningToFlag = false;
            chestApproachStart = DateTime.MinValue;
        }

        if (inCombat)
        {
            ResetOpeningChestCofferMountRecovery();

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

        if (FlyToOpeningChestCoffer(chest, player.Position, dist, yDelta, range, chestName, now))
            return;

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
        
        // Continuously pathfind to chest location to counter BMR AI interference
        _plugin.NavigationService.MoveToPosition(chest.Position);
    }

    private void CheckForPortalAfterChest()
    {
        ResetOpeningChestCofferMountRecovery("before portal search", stopNavigation: true);

        if (ShouldSkipPortalWaitForSelectedMap())
        {
            EndPortalRetryWindow();
            _plugin.AddDebugLog("[Portal] Selected map is known outdoor/no-dungeon - skipping portal search.");
            TransitionTo(BotState.Completed, "Outdoor map complete - skipping portal search...");
            return;
        }

        // Transition to a portal-searching state that retries every 2s for 10s
        StartPortalRetryWindow();
        TransitionTo(BotState.Completed, "Searching for portal...");
    }

    private bool ShouldSkipPortalWaitForSelectedMap()
    {
        if (SelectedMapItemId == 0)
            return false;

        return TreasureMapData.KnownMaps.TryGetValue(SelectedMapItemId, out var mapInfo)
            && !mapInfo.HasDungeon
            && mapInfo.Category == MapCategory.Outdoor;
    }
    
    private IGameObject? FindNearestPortal(bool keepActivePortalWindow = false)
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
                _plugin.AddDebugLog($"[Portal] Found portal at {dist:F1}y distance, XYZ {FormatVectorCompact(portalObj.Position)}");

                // Verify portal is targetable (not a ghost object)
                if (!IsObjectTargetable(portalObj))
                {
                    _plugin.AddDebugLog($"[Portal] Portal is NOT targetable (ghost object) - ignoring");
                    return null;
                }
                
                var keepCapturedPortal = keepActivePortalWindow
                    && portalApproachPosition.HasValue
                    && Vector3.Distance(portalObj.Position, portalApproachPosition.Value) <= 5f;
                var keepTargetablePortal = keepActivePortalWindow;

                if (dist > 30f && !keepCapturedPortal && !keepTargetablePortal)
                {
                    _plugin.AddDebugLog($"[Portal] Portal too far ({dist:F1}y > 30y)");
                    return null;
                }
                else if (dist > 30f)
                {
                    var portalBasis = keepCapturedPortal && portalApproachPosition.HasValue
                        ? $"captured XYZ {FormatVectorCompact(portalApproachPosition.Value)}"
                        : "live targetable portal";
                    _plugin.AddDebugLog($"[Portal] Keeping active portal window despite distance {dist:F1}y; {portalBasis}");
                }

                portalConfirmedThisMap = true;
                return portalObj;
            }
        }
        
        return null;
    }

    private void OnCombatStart()
    {
        lastCombatEndTime = DateTime.MinValue; // Reset combat end time

        if (State == BotState.OpeningChest)
        {
            openingChestCombatInterrupted = true;
            openingChestRecoveryDigIssued = false;
            openingChestReturningToFlag = false;
            chestDisappearedTime = DateTime.MinValue;
            ResetOpeningChestCofferMountRecovery();
            _plugin.AddDebugLog("[OpeningChest] Combat interrupted chest recovery - will retry after combat");
        }
        
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

        if (State == BotState.OpeningChest && openingChestCombatInterrupted)
            _plugin.AddDebugLog("[OpeningChest] Combat ended - rechecking chest recovery");
        
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
            StartPortalRetryWindow();
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
            ResetDoorTransitionReadiness();
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
            ResetDoorTransitionReadiness();
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
            ResetDoorTransitionReadiness();
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
                    ResetDoorTransitionReadiness();
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
                    ResetDoorTransitionReadiness();
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
                        ResetDoorTransitionReadiness();
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
                doorTransitionReadySince = DateTime.MinValue;
                if (!doorTransitionReadyWaitLogged)
                {
                    _plugin.AddDebugLog($"[Dungeon] Door transition navigation waiting for character ready. Blockers: {DescribeCharacterReadyBlockers()}");
                    doorTransitionReadyWaitLogged = true;
                }

                StateDetail = $"Waiting for door transition cutscene/loading to clear...";
                return;
            }

            if (doorTransitionReadySince == DateTime.MinValue)
            {
                doorTransitionReadySince = DateTime.Now;
                if (doorTransitionReadyWaitLogged)
                {
                    _plugin.AddDebugLog($"[Dungeon] Door transition ready detected - waiting {DoorTransitionReadyStabilization.TotalSeconds:F1}s for settle.");
                    doorTransitionReadyWaitLogged = false;
                }

                StateDetail = $"Door transition ready - settling briefly...";
                return;
            }

            if ((DateTime.Now - doorTransitionReadySince) < DoorTransitionReadyStabilization)
            {
                StateDetail = $"Door transition ready - settling briefly...";
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
                        ResetDoorTransitionReadiness();
                    }
                }
                else
                {
                    _plugin.AddDebugLog($"[Dungeon] Door transition point no longer in range - done");
                    if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
                    doorTransitionNavigating = false;
                    ResetDoorTransitionReadiness();
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
            ResetDoorTransitionReadiness();
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
            if ((DateTime.Now - lastDungeonInteractionTime).TotalSeconds >= DungeonInteractionIntervalSeconds)
            {
                lastDungeonInteractionTime = DateTime.Now;
                dungeonInteractionAttemptCount++;
                Plugin.TargetManager.Target = target;
                
                _plugin.AddDebugLog($"[DungeonLooting] Interact attempt #{dungeonInteractionAttemptCount} (TargetSystem) with '{targetName}' at {dist:F1}y");
                GameHelpers.InteractWithObject(target);
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
            if ((DateTime.Now - lastDungeonInteractionTime).TotalSeconds >= DungeonInteractionIntervalSeconds)
            {
                lastDungeonInteractionTime = DateTime.Now;
                dungeonInteractionAttemptCount++;
                Plugin.TargetManager.Target = target;
                
                _plugin.AddDebugLog($"[DungeonLooting] Interact attempt #{dungeonInteractionAttemptCount} (TargetSystem) with '{targetName}' Kind={target.ObjectKind} at {dist:F1}y");
                GameHelpers.InteractWithObject(target);
                
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
            ResetDoorTransitionReadiness();
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

            if (!IsCharacterReady())
            {
                doorWalkThroughReadySince = DateTime.MinValue;
                if (!doorWalkThroughBlockedLogged)
                {
                    _plugin.AddDebugLog($"[Dungeon] Door walk-through is armed but character is still not ready. Blockers: {DescribeCharacterReadyBlockers()}");
                    doorWalkThroughBlockedLogged = true;
                }

                StateDetail = $"Waiting for door cutscene/loading to clear...";
                return;
            }

            if (doorWalkThroughReadySince == DateTime.MinValue)
            {
                doorWalkThroughReadySince = DateTime.Now;
                if (doorWalkThroughBlockedLogged)
                {
                    _plugin.AddDebugLog($"[Dungeon] Door walk-through ready detected - waiting {DoorTransitionReadyStabilization.TotalSeconds:F1}s before moving.");
                    doorWalkThroughBlockedLogged = false;
                }

                StateDetail = $"Door opened - settling before walk-through...";
                return;
            }

            if ((DateTime.Now - doorWalkThroughReadySince) < DoorTransitionReadyStabilization)
            {
                StateDetail = $"Door opened - settling before walk-through...";
                return;
            }

            if (doorWalkThroughStart == DateTime.MinValue)
            {
                doorWalkThroughStart = DateTime.Now;
                _plugin.AddDebugLog("[Dungeon] Starting door walk-through after cutscene/loading settled.");
            }

            var walkElapsed = (DateTime.Now - doorWalkThroughStart).TotalSeconds;

            // Timeout: if we haven't triggered BetweenAreas in 15s, give up and rescan
            if (walkElapsed > 15.0)
            {
                _plugin.AddDebugLog($"[Dungeon] Door walk-through timeout after {walkElapsed:F0}s - rescanning");
                if (autoMoveActive) { _plugin.NavigationService.StopNavigation(); autoMoveActive = false; }
                lastDoorOpenedPosition = null;
                doorWalkThroughStart = DateTime.MinValue;
                ResetDoorTransitionReadiness();
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
                        _plugin.AddDebugLog($"[Dungeon] Door opened! Armed walk-through to transition '{doorTransition.Label}' at {Vector3.Distance(player.Position, doorTransition.Position):F1}y. Ready={IsCharacterReady()} Blockers={(IsCharacterReady() ? "None" : DescribeCharacterReadyBlockers())}");
                        lastDoorOpenedPosition = doorTransition.Position;
                    }
                    else
                    {
                        _plugin.AddDebugLog($"[Dungeon] Door opened but no known transition point - using forward fallback. Ready={IsCharacterReady()} Blockers={(IsCharacterReady() ? "None" : DescribeCharacterReadyBlockers())}");
                        // Fallback: move forward from current position for 10s
                        lastDoorOpenedPosition = player.Position + new Vector3(0, 0, -10f); // Forward approximation
                    }
                    doorStuckStart = DateTime.MinValue;
                    doorWalkThroughStart = DateTime.MinValue; // Will be set next tick
                    ResetDoorTransitionReadiness();
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
        ResetDoorTransitionReadiness();

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

            // Use ProcessLootTarget for interaction cycling
            // (InteractWithObject via TargetSystem, 3-phase approach, stuck detection)
            ProcessLootTarget(target);
        }
    }

    private void TickCompleted()
    {
        if (TryHandleAdsCompletedHandoff())
            return;

        if (adsDutyHandoffActive)
        {
            return;
        }

        // If portalRetryStart is set, we're searching for a portal before finishing
        if (portalRetryStart != DateTime.MinValue)
        {
            var now = DateTime.Now;
            var sinceStart = now - portalRetryStart;
            bool loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                           Plugin.Condition[ConditionFlag.BetweenAreas51];

            if (loading)
            {
                adsDutyEntryConfirmedAt = DateTime.MinValue;
                adsDutyReadySince = DateTime.MinValue;
                StateDetail = "Portal accepted - waiting for loading to finish...";
                return;
            }

            if (TryHandleConfirmedDutyEntry("[Portal]"))
                return;

            // Search briefly for a portal, but keep a captured targetable portal alive longer for approach fallback.
            var portalWindowTimeout = portalApproachPosition.HasValue
                ? PortalActiveApproachTimeout
                : PortalSearchTimeout;
            if (sinceStart <= portalWindowTimeout)
            {
                // Click Yes on any visible dialog (portal confirmation from previous tick)
                if (GameHelpers.ClickYesIfVisible())
                {
                    _plugin.AddDebugLog("[Portal] Clicked Yes on portal dialog - waiting for loading screen...");
                    CommandHelper.SendCommand("/automove off");
                    if (_plugin.NavigationService.State != NavigationState.Idle)
                    {
                        _plugin.NavigationService.StopNavigation();
                    }
                    autoMoveActive = false;
                    // Don't transition to InDungeon yet - maps can set BoundByDuty before the player leaves the overworld.
                    StateDetail = "Portal accepted - waiting for treasure dungeon territory...";
                    return;
                }

                var portal = FindNearestPortal(keepActivePortalWindow: true);
                if (TryHandleConfirmedDutyEntry("[Portal]", portal != null || portalApproachPosition.HasValue))
                    return;

                if (portal != null)
                {
                    // Double-check portal is still targetable before attempting to move
                    if (!IsObjectTargetable(portal))
                    {
                        _plugin.AddDebugLog($"[Portal] Portal became untargetable - waiting...");
                        return;
                    }

                    Plugin.TargetManager.Target = portal;
                    var approachPosition = CapturePortalApproachPosition(portal);
                    var player = Plugin.ObjectTable.LocalPlayer;
                    var portalDist = player == null
                        ? float.MaxValue
                        : Vector3.Distance(player.Position, approachPosition);

                    if (portalDist > PortalInteractionRange)
                    {
                        if (!portalRegularVnavPathLogged)
                        {
                            portalRegularVnavPathLogged = true;
                            _plugin.AddDebugLog("[Portal] Portal approach uses captured XYZ vnav only; lockon+automove disabled.");
                        }

                        if (FlyToPortalApproachPosition(approachPosition, portalDist))
                        {
                            StateDetail = $"Approaching portal XYZ {FormatVectorCompact(approachPosition)} ({portalDist:F1}y)...";
                        }
                        return;
                    }

                    HandlePortalInInteractionRange(portal, approachPosition, portalDist, now);
                    return;
                }

                StateDetail = portalApproachPosition.HasValue
                    ? $"Waiting for captured portal approach... ({sinceStart.TotalSeconds:F0}/{portalWindowTimeout.TotalSeconds:F0}s)"
                    : $"Searching for portal... ({sinceStart.TotalSeconds:F0}/{portalWindowTimeout.TotalSeconds:F0}s)";
                return;
            }

            var timedOutPortal = FindNearestPortal(keepActivePortalWindow: true);
            if (timedOutPortal != null)
                CapturePortalApproachPosition(timedOutPortal);

            var mapDutyStillActive = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                                     Plugin.Condition[ConditionFlag.BoundByDuty56];
            var portalStillAvailable = portalApproachPosition.HasValue || timedOutPortal != null;

            if (TryHandleConfirmedDutyEntry("[Portal]", portalAvailable: portalStillAvailable))
                return;

            if (portalStillAvailable || mapDutyStillActive)
            {
                var currentTerritory = Plugin.ClientState.TerritoryType;
                if (mapDutyStillActive && !IsTreasureDungeonTerritory(currentTerritory))
                    LogMapDutyOutsideDungeon("[Portal]", currentTerritory);

                if (now - lastPortalTimeoutHoldLogTime >= TimeSpan.FromSeconds(5.0))
                {
                    lastPortalTimeoutHoldLogTime = now;
                    var holdReason = portalApproachPosition.HasValue
                        ? $"captured portal XYZ {FormatVectorCompact(portalApproachPosition.Value)}"
                        : timedOutPortal != null
                            ? "live targetable portal"
                            : "map-duty state";
                    _plugin.AddDebugLog(
                        $"[Portal] Portal window timeout reached, but {holdReason} is still active - continuing instead of marking map complete.");
                }

                portalRetryStart = now;
                StateDetail = portalStillAvailable
                    ? "Portal still active - continuing portal interaction..."
                    : $"Map duty active in territory {currentTerritory} - waiting for treasure dungeon territory...";
                return;
            }

            // Time elapsed, no usable portal entry - map is complete (no dungeon)
            if (TryGuardMapCompletionWithActiveKeyItem("[PortalTimeout]"))
                return;

            _plugin.AddDebugLog($"[Portal] No portal found after {PortalSearchTimeout.TotalSeconds:F0}s - map complete (no dungeon)");
            if (autoMoveActive)
            {
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
            }
            EndPortalRetryWindow();
            adsDutyEntryConfirmedAt = DateTime.MinValue;
        }

        if (TryGuardMapCompletionWithActiveKeyItem("[Completed]"))
            return;
        
        if (!stateActionIssued)
        {
            _plugin.AddDebugLog("[Completed] Map run complete.");
            stateActionIssued = true;
        }
        
        // CRITICAL: Do NOT start next map if still in a dungeon
        bool stillInDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                           Plugin.Condition[ConditionFlag.BoundByDuty56];
        if (stillInDuty)
        {
            var currentTerritory = Plugin.ClientState.TerritoryType;
            if (_plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver
                && _plugin.IsAdsAvailable
                && IsTreasureDungeonTerritory(currentTerritory))
            {
                _plugin.AddDebugLog($"[Completed][ADS] Still in confirmed treasure dungeon territory {currentTerritory}; recovering by starting ADS handoff.");
                if (TryHandleConfirmedDutyEntry("[Completed][Recovery]"))
                    return;
            }
            else if (_plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver && _plugin.IsAdsAvailable)
            {
                _plugin.AddDebugLog($"[Completed][ADS] Still in map-duty state in territory {currentTerritory}; not starting ADS outside a treasure dungeon.");
            }

            if (!IsTreasureDungeonTerritory(currentTerritory))
            {
                LogMapDutyOutsideDungeon("[Completed]", currentTerritory);
                StateDetail = $"Map duty active in territory {currentTerritory} - waiting for treasure dungeon territory...";
                return;
            }

            _plugin.AddDebugLog("[Completed] ERROR: Still in dungeon (BoundByDuty) - cannot start next map!");
            TransitionTo(BotState.Error, "Still in dungeon - cannot start next map. Manual intervention required.");
            return;
        }
        
        KrangleService.ClearCache();

        if (_plugin.Configuration.AutoStartNextMap)
        {
            if (!CanStartNextMapAfterPartyWait())
                return;

            _plugin.AddDebugLog("[Completed] AutoStartNextMap enabled - scanning for maps");
            var mapSources = _plugin.InventoryService.ScanForMapSources();
            var maps = GetEnabledMapCandidates(mapSources, includeInventory: true, includeSaddlebags: true);
            _plugin.AddDebugLog($"[Completed] Found {maps.Count} runnable map type(s) in inventory/saddlebags");
            
            if (maps.Count > 0)
            {
                RetryCount = 0;
                CurrentLocation = null;
                ResetPerMapCommandTriggers();
                TransitionTo(BotState.SelectingMap, "Auto-starting next map...");
                return;
            }

            _plugin.AddDebugLog("[Completed] No runnable maps in inventory or loaded saddlebags. Checking retainers via XADB.");
            if (_plugin.Configuration.EnableRetainerMapRetrieval)
            {
                var enabledForRetainers = _plugin.Configuration.EnabledMapTypes.Count > 0
                    ? _plugin.Configuration.EnabledMapTypes.ToList()
                    : TreasureMapData.KnownMaps.Keys.ToList();
                var retainerResult = _plugin.RetainerMapRetrievalService.StartOrTick(enabledForRetainers);
                switch (retainerResult)
                {
                    case RetainerMapRetrievalResult.Running:
                    case RetainerMapRetrievalResult.Retrieved:
                        RetryCount = 0;
                        CurrentLocation = null;
                        ResetPerMapCommandTriggers();
                        TransitionTo(BotState.SelectingMap, "Auto-starting next retainer map...");
                        return;

                    case RetainerMapRetrievalResult.Error:
                        _plugin.AddDebugLog($"[Completed] Retainer map retrieval error: {_plugin.RetainerMapRetrievalService.LastError}");
                        StopOnRetainerRetrievalError($"Could not retrieve retainer map: {_plugin.RetainerMapRetrievalService.LastError}");
                        return;

                    case RetainerMapRetrievalResult.NotAvailable:
                        _plugin.AddDebugLog("[Completed] No enabled retainer maps found via XADB.");
                        break;
                }
            }
            else
            {
                _plugin.AddDebugLog("[Completed] Retainer map retrieval disabled.");
            }
        }

        RunFinishCommandsOnce("[Completed] run complete");
        RetryCount = 0;
        TransitionTo(BotState.Idle, "Run complete.");
    }

    private bool TryGuardMapCompletionWithActiveKeyItem(string source)
    {
        if (!_plugin.InventoryService.TryFindTreasureMapKeyItem(out var keyItem))
        {
            ResetKeyItemMapRecoveryState(clearActiveKey: true);
            return false;
        }

        UpdateActiveKeyItemMap(keyItem, source);

        if (chestConfirmedThisMap || portalConfirmedThisMap || dungeonConfirmedThisMap)
            return false;

        var now = DateTime.Now;
        if (now - lastKeyItemCompletionGuardLogAt >= TimeSpan.FromSeconds(5.0))
        {
            lastKeyItemCompletionGuardLogAt = now;
            _plugin.AddDebugLog(
                $"{source} Active key-item map still exists ({keyItem.DisplayName}) and no chest/portal/dungeon was confirmed - refusing to complete.");
        }

        SetWarning($"Treasure map key item '{keyItem.DisplayName}' is still active. Retrying the map instead of marking it complete.");
        EndPortalRetryWindow();

        if (autoMoveActive)
        {
            _plugin.NavigationService.StopNavigation();
            autoMoveActive = false;
        }

        mapCountChecked = true;
        mapOpeningRetried = false;
        digIssuedThisMap = false;
        digIssuedAt = DateTime.MinValue;
        chestDisappearedTime = DateTime.MinValue;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player != null && TryGetCurrentFlagRecoveryTarget(out var flagTarget, out _))
        {
            var xzDist = CalculateXZDistance(player.Position, flagTarget);
            if (xzDist <= MapDigXZRange)
            {
                _plugin.AddDebugLog($"{source} Key item still active and player is {xzDist:F1}y XZ from flag - retrying dig.");
                TransitionTo(BotState.Flying, "Treasure map key item still active - retrying dig...");
                return true;
            }

            _plugin.AddDebugLog($"{source} Key item still active but player is {xzDist:F1}y XZ from flag - recovering flag and navigating again.");
        }
        else
        {
            _plugin.AddDebugLog($"{source} Key item still active but current flag/location is unavailable - reopening the active key item.");
        }

        CurrentLocation = null;
        ResetKeyItemMapRecoveryState();
        TransitionTo(BotState.DetectingLocation, $"Recovering active map flag for {keyItem.DisplayName}...");
        return true;
    }

    private bool CanStartNextMapAfterPartyWait()
    {
        var party = _plugin.PartyService;
        party.UpdatePartyStatus();

        if (party.PartyMembers.Count <= 1)
        {
            LogLandingPartyWaitOnce(
                "CompletedNextMap:solo",
                "[PartyWait][CompletedNextMap] Solo or no party members - next map allowed");
            return true;
        }

        var outOfZoneNames = party.PartyMembers
            .Where(member => !member.IsInSameZone)
            .Select(member => member.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (outOfZoneNames.Count == 0)
        {
            LogLandingPartyWaitOnce(
                $"CompletedNextMap:clear:{party.PartyMembers.Count}",
                "[PartyWait][CompletedNextMap] All party members loaded in same zone - next map allowed");
            return true;
        }

        var loadedCount = party.PartyMembers.Count - outOfZoneNames.Count;
        var missingText = string.Join(", ", outOfZoneNames);
        StateDetail =
            $"Waiting for party to load after dungeon ({loadedCount}/{party.PartyMembers.Count} in zone; missing: {missingText})...";

        LogLandingPartyWaitOnce(
            $"CompletedNextMap:block:{string.Join("|", outOfZoneNames)}",
            $"[PartyWait][CompletedNextMap] Blocking next map; out-of-zone members: {missingText}");

        return false;
    }

    private bool TryHandleConfirmedDutyEntry(string source, bool? portalAvailable = null)
    {
        bool inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                      Plugin.Condition[ConditionFlag.BoundByDuty56];
        if (!inDuty)
            return false;

        bool loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                       Plugin.Condition[ConditionFlag.BetweenAreas51];
        var currentTerritory = Plugin.ClientState.TerritoryType;

        if (!IsTreasureDungeonTerritory(currentTerritory))
        {
            adsDutyEntryConfirmedAt = DateTime.MinValue;
            adsDutyReadySince = DateTime.MinValue;

            if (loading)
            {
                ResetPortalApproachTrackingForAreaChange();
                StateDetail = $"Portal accepted - loading treasure dungeon territory from {currentTerritory}...";
                return true;
            }

            if (portalAvailable == false)
            {
                ResetPortalApproachTrackingForAreaChange();
                LogMapDutyOutsideDungeon(source, currentTerritory);
                StateDetail = $"Map duty active in territory {currentTerritory} - waiting for treasure dungeon territory...";
                return true;
            }

            if (portalAvailable == true)
            {
                StateDetail = $"Map duty active in territory {currentTerritory} - continuing portal interaction...";
            }

            return false;
        }

        dungeonConfirmedThisMap = true;

        if (_plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver && _plugin.IsAdsAvailable)
        {
            if (loading)
            {
                ResetPortalApproachTrackingForAreaChange();
                adsDutyEntryConfirmedAt = DateTime.MinValue;
                StateDetail = $"Treasure dungeon territory {currentTerritory} detected - waiting for loading to finish before ADS handoff...";
                return true;
            }

            if (adsDutyEntryConfirmedAt == DateTime.MinValue)
            {
                adsDutyEntryConfirmedAt = DateTime.Now;
                StateDetail = $"Treasure dungeon territory {currentTerritory} detected - waiting for ADS-safe handoff seam...";
                _plugin.AddDebugLog($"{source}[ADS] Treasure dungeon territory {currentTerritory} confirmed; waiting for ADS-safe handoff.");
                return true;
            }

            if (!IsCharacterReady())
            {
                adsDutyReadySince = DateTime.MinValue;
                StateDetail = $"Treasure dungeon territory {currentTerritory} detected - waiting for ADS-safe handoff seam... ({DescribeCharacterReadyBlockers()})";
                return true;
            }

            if (adsDutyReadySince == DateTime.MinValue)
            {
                adsDutyReadySince = DateTime.Now;
                StateDetail = $"Treasure dungeon territory {currentTerritory} detected - waiting for ADS-safe handoff settle...";
                return true;
            }

            if ((DateTime.Now - adsDutyReadySince).TotalSeconds < 2.0)
            {
                StateDetail = $"Treasure dungeon territory {currentTerritory} detected - waiting for ADS-safe handoff settle...";
                return true;
            }

            _plugin.AddDebugLog($"{source}[ADS] Treasure dungeon territory {currentTerritory} settled - handing dungeon phase to ADS.");
            if (autoMoveActive)
            {
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
            }

            EndPortalRetryWindow();
            QueueDungeonMapFlagClear($"{source}[ADS]");
            ResetAdsHandoffTracking(resetStatus: true);
            adsDutyHandoffActive = true;
            adsDutyHandoffStarted = DateTime.Now;
            SendAdsInsideCommand($"{source}[ADS] Sent initial /ads inside after duty entry settled.", includeAssistCommands: true);
            TransitionTo(BotState.Completed, "ADS handoff active - waiting for dungeon to finish...");
            return true;
        }

        if (_plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver && !_plugin.IsAdsAvailable)
        {
            _plugin.ShowAdsMissingToast();
            _plugin.AddDebugLog($"{source}[ADS] ADS handoff requested, but ADS is not installed/loaded. Falling back to legacy dungeon solver.");
        }

        RunDutyEntryCommandsOnce($"{source} legacy dungeon entry");
        _plugin.AddDebugLog($"{source} Treasure dungeon territory {currentTerritory} confirmed - entering dungeon.");
        EndPortalRetryWindow();
        QueueDungeonMapFlagClear(source);
        adsDutyEntryConfirmedAt = DateTime.MinValue;
        dungeonEntryProcessed = false;
        dungeonFloor = 0;
        excludedDoorEntityId = null;
        doorStuckStart = DateTime.MinValue;
        lastDoorOpenedPosition = null;
        doorWalkThroughStart = DateTime.MinValue;
        ResetDoorTransitionReadiness();

        currentObjective = DungeonObjective.ClearingChests;
        dungeonLoadWaitStart = DateTime.MinValue;
        processedChests.Clear();
        processedSpheres.Clear();
        failedObjects.Clear();
        sphereInteractionTimes.Clear();
        _plugin.AddDebugLog("[Objective] New dungeon entry - all objectives reset");

        TransitionTo(BotState.InDungeon, "Entering dungeon instance...");
        return true;
    }

    private static bool IsTreasureDungeonTerritory(uint territoryId)
    {
        if (territoryId == 0)
            return false;

        if (territoryId == 794)
            return true; // The Shifting Altars of Uznair: roulette-style treasure dungeon.

        return DungeonLocationData.HasDungeonData(territoryId)
            || TreasureMapData.KnownMaps.Values.Any(map => map.HasDungeon
                && (map.DungeonTerritoryId == territoryId || map.SecondTerritoryId == territoryId));
    }

    private void LogMapDutyOutsideDungeon(string source, uint territoryId)
    {
        var now = DateTime.Now;
        if ((now - lastMapDutyOutsideDungeonLog).TotalSeconds < 5.0)
            return;

        lastMapDutyOutsideDungeonLog = now;
        _plugin.AddDebugLog($"{source} BoundByDuty active in territory {territoryId}, but this is not a known treasure dungeon territory. Waiting; no map flag clear and no ADS handoff.");
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

    private bool TryHandleAdsCompletedHandoff()
    {
        if (!adsDutyHandoffActive)
            return false;

        var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                     Plugin.Condition[ConditionFlag.BoundByDuty56];
        var elapsed = adsDutyHandoffStarted == DateTime.MinValue
            ? 0.0
            : (DateTime.Now - adsDutyHandoffStarted).TotalSeconds;

        if (!inDuty)
        {
            ResetAdsHandoffTracking();
            _plugin.AddDebugLog("[ADS] Duty exit detected after ADS handoff - resuming normal completion flow.");
            return false;
        }

        var adsStatus = _plugin.AdsStatusService.Refresh();
        if (adsStatus.IsOwned)
        {
            if (!adsOwnershipObserved)
            {
                adsOwnershipObserved = true;
                adsUnreadableStatusLogged = false;
                _plugin.AddDebugLog($"[ADS] Ownership confirmed via status: {adsStatus.OwnershipMode}/{adsStatus.ExecutionPhase}.");
            }

            StateDetail = $"ADS owns the duty - waiting for completion... ({elapsed:F0}s, {adsStatus.ExecutionPhase})";
            return true;
        }

        if (!adsOwnershipObserved)
        {
            if (!adsStatus.StatusReadable && !adsUnreadableStatusLogged)
            {
                adsUnreadableStatusLogged = true;
                _plugin.AddDebugLog("[ADS] Handoff pending, but ADS status is unreadable - waiting for ownership before retrying /ads inside.");
            }

            if (!adsInsideRetrySent
                && adsInsideSentAt != DateTime.MinValue
                && (DateTime.Now - adsInsideSentAt).TotalSeconds >= 5.0)
            {
                adsInsideRetrySent = true;
                SendAdsInsideCommand("[ADS] Ownership was not confirmed after the initial handoff - sending one bounded /ads inside retry.", includeAssistCommands: false);
            }

            StateDetail = adsStatus.StatusReadable
                ? $"ADS handoff pending - waiting for ownership... ({elapsed:F0}s, {adsStatus.OwnershipMode}/{adsStatus.ExecutionPhase})"
                : $"ADS handoff pending - waiting for ownership... ({elapsed:F0}s, status unavailable)";
            return true;
        }

        if (!adsStatus.StatusReadable)
        {
            if (!adsUnreadableStatusLogged)
            {
                adsUnreadableStatusLogged = true;
                _plugin.AddDebugLog("[ADS] Ownership was seen earlier, but ADS status is currently unreadable - waiting for readable status before issuing /ads leave.");
            }

            StateDetail = $"ADS ownership was seen - waiting for readable status... ({elapsed:F0}s)";
            return true;
        }

        if (!adsLeaveIssued)
        {
            adsLeaveIssued = true;
            CommandHelper.SendCommand("/ads stop");
            _plugin.AddDebugLog($"[ADS] ADS no longer owns the duty ({adsStatus.OwnershipMode}/{adsStatus.ExecutionPhase}) - sending /ads stop before leave.");
            CommandHelper.SendCommand("/ads leave");
            _plugin.AddDebugLog($"[ADS] ADS no longer owns the duty ({adsStatus.OwnershipMode}/{adsStatus.ExecutionPhase}) - sending /ads leave.");
            StateDetail = "ADS released ownership - stopping ADS and leaving duty...";
            return true;
        }

        StateDetail = $"ADS leave requested - waiting for duty exit... ({elapsed:F0}s, {adsStatus.ExecutionPhase})";
        return true;
    }

    private void ResetAdsHandoffTracking(bool resetStatus = false)
    {
        adsDutyHandoffActive = false;
        adsDutyHandoffStarted = DateTime.MinValue;
        adsDutyEntryConfirmedAt = DateTime.MinValue;
        adsDutyReadySince = DateTime.MinValue;
        adsOwnershipObserved = false;
        adsInsideSentAt = DateTime.MinValue;
        adsInsideRetrySent = false;
        adsLeaveIssued = false;
        adsUnreadableStatusLogged = false;
        if (resetStatus)
            _plugin.AdsStatusService.Reset();
    }

    private void SendAdsInsideCommand(string logMessage, bool includeAssistCommands)
    {
        if (includeAssistCommands)
        {
            RunDutyEntryCommandsOnce("[ADS] duty handoff");
        }

        adsInsideSentAt = DateTime.Now;
        adsUnreadableStatusLogged = false;
        CommandHelper.SendCommand("/ads inside");
        _plugin.AddDebugLog(logMessage);
    }

    private bool IsDungeonState() =>
        State == BotState.InDungeon || State == BotState.DungeonCombat ||
        State == BotState.DungeonLooting || State == BotState.DungeonProgressing;

    private void UpdateMountedRotationLifecycle()
    {
        if (adsDutyHandoffActive)
            return;

        var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56];
        var mountedOrMounting = Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.Mounting71];

        if (inDuty)
        {
            RestoreMountedRotationLifecycle("duty entry");
            return;
        }

        if (mountedOrMounting)
        {
            SuppressMountedRotationLifecycle();
            return;
        }

        RestoreMountedRotationLifecycle("dismount");
    }

    private void SuppressMountedRotationLifecycle()
    {
        if (mountedRotationSuppressed)
            return;

        CommandHelper.SendCommand("/vbmai off");
        CommandHelper.SendCommand("/bmrai off");
        CommandHelper.SendCommand("/rotation cancel");
        CommandHelper.SendCommand("/rotation Settings DummyBoss False");
        CommandHelper.SendCommand("/rotation Settings DisableTargetDummys True");
	
        mountedRotationSuppressed = true;
        _plugin.AddDebugLog("[Rotation] Mounted lifecycle suppression active.");
    }

    private void RestoreMountedRotationLifecycle(string reason)
    {
        if (!mountedRotationSuppressed)
            return;

        CommandHelper.SendCommand("/vbmai on");
        CommandHelper.SendCommand("/bmrai on");
        CommandHelper.SendCommand("/rotation auto");
        mountedRotationSuppressed = false;
        _plugin.AddDebugLog($"[Rotation] Mounted lifecycle restore after {reason}.");
    }

    private static OverworldLandingMode ResolveLandingMode(uint mapItemId)
    {
        return IsThiefMap(mapItemId)
            ? OverworldLandingMode.UnderwaterBounce
            : OverworldLandingMode.MountToggle;
    }

    private static bool IsThiefMap(uint mapItemId)
    {
        return mapItemId == ThiefMapItemId;
    }

    private void ResetDoorTransitionReadiness()
    {
        doorWalkThroughReadySince = DateTime.MinValue;
        doorTransitionReadySince = DateTime.MinValue;
        doorWalkThroughBlockedLogged = false;
        doorTransitionReadyWaitLogged = false;
    }

    private string DescribeCharacterReadyBlockers()
    {
        var blockers = new List<string>();
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            blockers.Add("PlayerNull");
        else if (player.IsCasting)
            blockers.Add("Casting");

        if (Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            blockers.Add("OccupiedInCutSceneEvent");
        if (Plugin.Condition[ConditionFlag.Occupied33])
            blockers.Add("Occupied33");
        if (Plugin.Condition[ConditionFlag.WatchingCutscene])
            blockers.Add("WatchingCutscene");
        if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent])
            blockers.Add("OccupiedInQuestEvent");
        if (Plugin.Condition[ConditionFlag.Occupied39])
            blockers.Add("Occupied39");
        if (Plugin.Condition[ConditionFlag.BetweenAreas])
            blockers.Add("BetweenAreas");
        if (Plugin.Condition[ConditionFlag.BetweenAreas51])
            blockers.Add("BetweenAreas51");

        return blockers.Count == 0 ? "Unknown" : string.Join(", ", blockers);
    }

    private bool IsCharacterReady()
    {
        // Character is ready when: not in cutscene, not casting, not loading
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return false;
        if (player.IsCasting) return false;
        if (Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]) return false;
        if (Plugin.Condition[ConditionFlag.Occupied33]) return false;
        if (Plugin.Condition[ConditionFlag.WatchingCutscene]) return false;
        if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent]) return false;
        if (Plugin.Condition[ConditionFlag.Occupied39]) return false;
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

                // Handle unnamed objects - GOLD ROOM FIX
                if (string.IsNullOrEmpty(name))
                {
                    // CRITICAL FIX: In gold rooms, chests are unnamed EventObj objects
                    // These should be treated as loot, not progression objects
                    if (lootOnly)
                    {
                        // For lootOnly: include unnamed EventObj that are targetable and not attempted
                        // This catches gold room chests which are EventObj with empty names
                        return obj.IsTargetable && !attemptedCoffers.Contains(obj.EntityId);
                    }
                    
                    // For progression: unnamed EventObj are doors (tighter radius)
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

    private void SetWarning(string message)
    {
        if (WarningMessage == message)
            return;

        WarningMessage = message;
        _plugin.AddDebugLog($"[Warning] {message}");
    }

    private void ClearWarning()
    {
        WarningMessage = string.Empty;
    }

    private void LogDungeonObjects()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null) return;

        var objects = Plugin.ObjectTable
            .Where(obj => obj != null &&
                   !string.IsNullOrEmpty(obj.Name.ToString()) &&
                   Vector3.Distance(player.Position, obj.Position) <= 50f &&
                   obj.ObjectKind != ObjectKind.Pc &&
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
        const string addonName = "TreasureHighLow";

        if (!GameHelpers.IsAddonVisible(addonName))
        {
            treasureHighLowHandledThisOpen = false;
            return false;
        }

        if (autoMoveActive)
        {
            GameHelpers.StopAutoMove();
            autoMoveActive = false;
        }

        if (_plugin.NavigationService.State != NavigationState.Idle)
        {
            _plugin.NavigationService.StopNavigation();
        }

        if (GameHelpers.IsAddonCallbackSequencePending(addonName))
        {
            StateDetail = "Skipping Higher/Lower puzzle...";
            return true;
        }

        if (!treasureHighLowHandledThisOpen)
        {
            _plugin.AddDebugLog(
                "[CardGame] TreasureHighLow detected - scheduling Open Chest callbacks (-2 then 1).");

            if (GameHelpers.QueueTwoStepAddonCallbackSequence(
                    addonName,
                    true,
                    TreasureHighLowSecondCallbackDelay,
                    new object[] { -2 },
                    new object[] { 1 }))
            {
                treasureHighLowHandledThisOpen = true;
            }
            else
            {
                _plugin.AddDebugLog("[CardGame] Failed to queue TreasureHighLow skip sequence.");
            }
        }

        StateDetail = "Waiting for Higher/Lower puzzle to close...";
        return true;
    }

    // ─── Error Handling ───────────────────────────────────────────────────────

    private void HandleError(string message)
    {
        RetryCount++;
        _plugin.AddDebugLog($"[Error #{RetryCount}] {message}");

        // CRITICAL: If still in a duty (BoundByDuty), do NOT go to SelectingMap.
        // Go back to InDungeon to re-evaluate dungeon state instead of trying to start a new map.
        bool stillInDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                           Plugin.Condition[ConditionFlag.BoundByDuty56];

        _plugin.NavigationService.StopNavigation(clearFlag: !stillInDuty);

        if (stillInDuty)
        {
            _plugin.AddDebugLog($"[Error #{RetryCount}] Still in duty (BoundByDuty=true) - recovering to InDungeon instead of SelectingMap");
            dungeonEntryProcessed = false;
            TransitionTo(BotState.InDungeon, $"Error #{RetryCount} (recovered): {message}");
            return;
        }
        
        // Not in duty - safe to retry from SelectingMap
        TransitionTo(BotState.SelectingMap, $"Error #{RetryCount}: {message}");
    }

    private void StopOnRetainerRetrievalError(string message)
    {
        RetryCount++;
        _plugin.AddDebugLog($"[RetainerMap] Fatal error #{RetryCount}: {message}");
        _plugin.NavigationService.StopNavigation();
        _plugin.RetainerMapRetrievalService.Reset();
        TransitionTo(BotState.Error, message);
    }

    // ─── Transition ───────────────────────────────────────────────────────────

    private void TransitionTo(BotState newState, string detail)
    {
        var prev = State;
        if (prev == BotState.Flying || newState == BotState.Flying)
            ResetVnavFlyFlagFallbackState();

        State = newState;
        StateDetail = detail;
        stateStartTime = DateTime.Now;
        stateActionIssued = false;
        dismountAttemptStart = DateTime.MinValue;
        descentInProgress = false;
        lastLandingPartyWaitSignature = string.Empty;
        openingChestCombatInterrupted = false;
        treasureHighLowHandledThisOpen = false;
        openingChestRecoveryDigIssued = false;
        openingChestReturningToFlag = false;

        if (prev == BotState.OpeningChest && newState != BotState.OpeningChest)
            ResetOpeningChestCofferMountRecovery();

        if (prev == BotState.Completed && newState != BotState.Completed)
        {
            CommandHelper.SendCommand("/automove off");
            ResetPortalApproachTrackingForAreaChange();
        }

        // Stop navigation if it was active
        if (autoMoveActive)
        {
            _plugin.NavigationService.StopNavigation();
            autoMoveActive = false;
        }

        if (newState == BotState.OpeningChest && Plugin.Condition[ConditionFlag.InCombat])
        {
            openingChestCombatInterrupted = true;
            chestDisappearedTime = DateTime.MinValue;
            _plugin.AddDebugLog("[OpeningChest] Entered chest recovery while already in combat - will retry after combat");
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

    // ─── Cycling Modes ────────────────────────────────────────────────────────

    /// <summary>
    /// Start cycling through all unlocked aetherytes that don't have stored positions.
    /// Teleports to each one, records player position on arrival, then moves to next.
    /// </summary>
    public void StartCyclingAetherytes()
    {
        if (State != BotState.Idle && State != BotState.Error && State != BotState.Completed)
        {
            _plugin.AddDebugLog("[CycleAetherytes] Cannot start - bot is busy");
            return;
        }

        cycleAetheryteQueue = _plugin.AetherytePositionDatabase
            .GetMissingAetherytes(Plugin.DataManager)
            .ToList();
        if (cycleAetheryteQueue.Count == 0)
        {
            _plugin.AddDebugLog("[CycleAetherytes] All unlocked aetherytes already have stored positions!");
            _plugin.PrintChat("All aetheryte positions are already recorded!");
            return;
        }

        cycleAetheryteIndex = 0;
        cycleTeleportIssued = false;
        _plugin.AddDebugLog($"[CycleAetherytes] Starting cycle of {cycleAetheryteQueue.Count} missing aetherytes");
        _plugin.PrintChat($"Cycling {cycleAetheryteQueue.Count} missing aetheryte positions...");
        TransitionTo(BotState.CyclingAetherytes, $"Cycling aetherytes (0/{cycleAetheryteQueue.Count})...");
    }

    private unsafe void TickCyclingAetherytes()
    {
        // Extra safety: skip tick during zone transitions
        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            stateStartTime = DateTime.Now;
            return;
        }

        if (cycleAetheryteIndex >= cycleAetheryteQueue.Count)
        {
            _plugin.AddDebugLog($"[CycleAetherytes] Completed! Recorded {cycleAetheryteQueue.Count} aetheryte positions");
            _plugin.PrintChat($"Aetheryte cycling complete! {_plugin.AetherytePositionDatabase.Count} positions stored.");
            TransitionTo(BotState.Idle, "Aetheryte cycling complete!");
            return;
        }

        var current = cycleAetheryteQueue[cycleAetheryteIndex];
        StateDetail = $"Cycling aetherytes ({cycleAetheryteIndex + 1}/{cycleAetheryteQueue.Count}): {current.Name}";

        var nav = _plugin.NavigationService;
        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;

        // Step 1: Issue teleport
        if (!cycleTeleportIssued)
        {
            _plugin.AddDebugLog($"[CycleAetherytes] [{cycleAetheryteIndex + 1}/{cycleAetheryteQueue.Count}] Teleporting to {current.Name} (ID:{current.Id})");
            cycleCurrentAetheryteId = current.Id;
            nav.TeleportToAetheryte(current.Id);
            cycleTeleportIssued = true;
            cycleTeleportTime = DateTime.Now;
            cycleLastPosition = playerPos;
            cyclePositionChanged = false;
            return;
        }

        // Step 2: Wait for teleport to finish
        if (nav.IsTeleporting()) return;

        // Step 3: Wait for XYZ coordinates to change (teleport arrival)
        if (!cyclePositionChanged)
        {
            // Check if X, Y, or Z coordinates have changed (teleport completed)
            bool xChanged = Math.Abs(playerPos.X - cycleLastPosition.X) > 1.0f;
            bool yChanged = Math.Abs(playerPos.Y - cycleLastPosition.Y) > 1.0f;
            bool zChanged = Math.Abs(playerPos.Z - cycleLastPosition.Z) > 1.0f;
            
            if (xChanged || yChanged || zChanged)
            {
                _plugin.AddDebugLog($"[CycleAetherytes] {current.Name} - XYZ changed: ({cycleLastPosition.X:F1},{cycleLastPosition.Y:F1},{cycleLastPosition.Z:F1}) → ({playerPos.X:F1},{playerPos.Y:F1},{playerPos.Z:F1}), waiting 3 seconds");
                cyclePositionChanged = true;
                cyclePositionChangeTime = DateTime.Now;
            }
            else if ((DateTime.Now - cycleTeleportTime).TotalSeconds > 30.0)
            {
                // Timeout - move to next aetheryte
                _plugin.AddDebugLog($"[CycleAetherytes] {current.Name} - Timeout waiting for XYZ change, moving to next");
                cycleAetheryteIndex++;
                cycleTeleportIssued = false;
                stateStartTime = DateTime.Now;
            }
            return;
        }

        // Step 4: Wait 3 seconds after position change, then record
        var waitElapsed = (DateTime.Now - cyclePositionChangeTime).TotalSeconds;
        if (waitElapsed >= 3.0)
        {
            _plugin.AddDebugLog($"[CycleAetherytes] RECORDING {current.Name} at ({playerPos.X:F1}, {playerPos.Y:F1}, {playerPos.Z:F1})");
            _plugin.AetherytePositionDatabase.RecordPosition(
                current.Id, current.Name,
                playerPos.X, playerPos.Y, playerPos.Z);
            
            // Move to next aetheryte
            cycleAetheryteIndex++;
            cycleTeleportIssued = false;
            cyclePositionChanged = false;
            stateStartTime = DateTime.Now;
            
            _plugin.AddDebugLog($"[CycleAetherytes] Moving to next aetheryte ({cycleAetheryteIndex + 1}/{cycleAetheryteQueue.Count})");
        }
    }

    /// <summary>
    /// Start cycling through map locations that don't have RealXYZ data.
    /// Teleports to nearest aetheryte, flies to flag, lands, records position, moves to next.
    /// </summary>
    public void StartCyclingMapLocations(List<MapLocationEntry>? specificEntries = null)
    {
        if (State != BotState.Idle && State != BotState.Error && State != BotState.Completed)
        {
            _plugin.AddDebugLog("[CycleMapLocs] Cannot start - bot is busy");
            return;
        }

        if (specificEntries != null)
        {
            cycleMapLocationQueue = specificEntries;
        }
        else
        {
            // Get all locations missing RealXYZ
            cycleMapLocationQueue = _plugin.MapLocationDatabase.GetAllMerged()
                .Where(e => !e.HasRealXYZ)
                .ToList();
        }

        if (cycleMapLocationQueue.Count == 0)
        {
            _plugin.AddDebugLog("[CycleMapLocs] All locations already have RealXYZ!");
            _plugin.PrintChat("All map locations already have real XYZ data!");
            return;
        }

        cycleMapLocationIndex = 0;
        _plugin.AddDebugLog($"[CycleMapLocs] Starting cycle of {cycleMapLocationQueue.Count} missing XYZ locations");
        _plugin.PrintChat($"Cycling {cycleMapLocationQueue.Count} map locations missing real XYZ...");

        // Set up the first location and start the normal bot flow
        SetupNextCycleMapLocation();
    }

    private void SetupNextCycleMapLocation()
    {
        if (cycleMapLocationIndex >= cycleMapLocationQueue.Count)
        {
            _plugin.AddDebugLog($"[CycleMapLocs] Completed! Visited {cycleMapLocationQueue.Count} locations");
            _plugin.PrintChat($"Map location cycling complete!");
            TransitionTo(BotState.Idle, "Map location cycling complete!");
            return;
        }

        var entry = cycleMapLocationQueue[cycleMapLocationIndex];
        var flagPos = new Vector3(entry.FlagX, entry.FlagY, entry.FlagZ);
        var aetheryteId = _plugin.NavigationService.FindNearestAetheryte(entry.TerritoryId, flagPos, out _, out _);

        var location = new MapLocation
        {
            TerritoryId = entry.TerritoryId,
            ZoneName = entry.ZoneName,
            X = entry.FlagX,
            Y = entry.FlagY,
            Z = entry.FlagZ,
            NearestAetheryteId = aetheryteId,
        };

        // Populate aetheryte name
        if (aetheryteId > 0)
        {
            try
            {
                var aetheryteSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
                if (aetheryteSheet != null)
                {
                    var aetheryte = aetheryteSheet.GetRow(aetheryteId);
                    location.NearestAetheryteName = aetheryte.PlaceName.ValueNullable?.Name.ToString() ?? $"ID {aetheryteId}";
                }
            }
            catch { }
        }

        SetLocation(location);

        // Always mark destination flag on map
        // Skip during zone transitions to prevent AgentHUD.UpdateNaviMap crashes
        bool loading = Plugin.Condition[ConditionFlag.BetweenAreas] || 
                       Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (!loading)
        {
            GameHelpers.SetMapFlag(entry.TerritoryId, entry.FlagX, entry.FlagZ);
            _plugin.AddDebugLog($"[CycleMapLocs] [{cycleMapLocationIndex + 1}/{cycleMapLocationQueue.Count}] {entry.ZoneName} flag=({entry.FlagX:F1},{entry.FlagZ:F1})");
        }
        else
        {
            _plugin.AddDebugLog($"[CycleMapLocs] Skipping flag placement during zone transition");
        }

        // Use CyclingMapLocations state which runs the normal teleport→mount→fly flow
        // but skips dig/chest and instead records position after landing
        TransitionTo(BotState.CyclingMapLocations, $"Location {cycleMapLocationIndex + 1}/{cycleMapLocationQueue.Count}: {entry.ZoneName}");
    }

    /// <summary>
    /// Enter manual control mode during XYZ cycling. Stops navigation and lets the player move freely.
    /// </summary>
    public void CycleTakeControl()
    {
        if (State != BotState.CyclingMapLocations) return;
        cycleManualControl = true;
        _plugin.NavigationService.StopNavigation();
        _plugin.AddDebugLog("[CycleMapLocs] Manual control activated - navigate to the spot and click 'Mark This Spot'");
        StateDetail = $"MANUAL CONTROL - Location {cycleMapLocationIndex + 1}/{cycleMapLocationQueue.Count}: {CurrentLocation?.ZoneName ?? "?"}";
    }

    /// <summary>
    /// Record the player's current position as the RealXYZ for the current cycling location, then advance.
    /// </summary>
    public void CycleMarkThisSpot()
    {
        if (State != BotState.CyclingMapLocations) return;

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        if (playerPos == Vector3.Zero || CurrentLocation == null)
        {
            _plugin.AddDebugLog("[CycleMapLocs] Cannot mark - no player position or location");
            return;
        }

        var entry = cycleMapLocationQueue[cycleMapLocationIndex];
        _plugin.MapLocationDatabase.RecordLocation(
            CurrentLocation.TerritoryId,
            CurrentLocation.ZoneName,
            entry.MapName,
            CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z,
            playerPos.X, playerPos.Y, playerPos.Z);
        _plugin.AddDebugLog($"[CycleMapLocs] MANUAL mark XYZ: ({playerPos.X:F1}, {playerPos.Y:F1}, {playerPos.Z:F1})");
        _plugin.PrintChat($"Marked position ({playerPos.X:F1}, {playerPos.Y:F1}, {playerPos.Z:F1})");

        // Advance to next location
        cycleManualControl = false;
        cycleMapLocationIndex++;
        stateStartTime = DateTime.Now;
        stateActionIssued = false;
        cycleTeleportIssued = false;
        cycleLandingIssued = false;
        mountAttemptStart = DateTime.MinValue;
        mountAttempts = 0;
        SetupNextCycleMapLocation();
    }

    private void TickCyclingMapLocations()
    {
        // This state reuses the existing teleport→mount→fly logic
        // The entry point sets up CurrentLocation, then we drive the sub-flow here

        var nav = _plugin.NavigationService;
        var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
        var groundOnly = _plugin.Configuration.CycleGroundOnly;

        // Manual control mode - don't do anything, wait for user to Mark This Spot
        if (cycleManualControl) return;

        // Step 1: Teleport if needed
        if (!stateActionIssued)
        {
            if (CurrentLocation == null)
            {
                HandleError("[CycleMapLocs] No location set");
                return;
            }

            if (Plugin.ClientState.TerritoryType == CurrentLocation.TerritoryId)
            {
                // Already in zone - skip to mounting
                stateActionIssued = true;
                mountAttemptStart = DateTime.MinValue;
                mountAttempts = 0;
                // Fall through to mount/fly logic below
            }
            else if (CurrentLocation.NearestAetheryteId > 0)
            {
                nav.TeleportToAetheryte(CurrentLocation.NearestAetheryteId);
                stateActionIssued = true;
                cycleTeleportIssued = true;
                cycleTeleportTime = DateTime.Now;
                return;
            }
            else
            {
                // No aetheryte found - skip this location
                _plugin.AddDebugLog($"[CycleMapLocs] No aetheryte for {CurrentLocation.ZoneName} - skipping");
                cycleMapLocationIndex++;
                stateStartTime = DateTime.Now;
                stateActionIssued = false;
                cycleLandingIssued = false;
                SetupNextCycleMapLocation();
                return;
            }
        }

        // Step 2: Wait for teleport
        if (cycleTeleportIssued)
        {
            if ((DateTime.Now - cycleTeleportTime).TotalSeconds < 5.0) return;
            if (nav.IsTeleporting()) return;

            // Arrived - record aetheryte position passively
            if (CurrentLocation?.NearestAetheryteId > 0)
            {
                var pos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
                if (pos != Vector3.Zero)
                {
                    _plugin.AetherytePositionDatabase.RecordPosition(
                        CurrentLocation.NearestAetheryteId,
                        CurrentLocation.NearestAetheryteName,
                        pos.X, pos.Y, pos.Z);
                }
            }
            cycleTeleportIssued = false;
        }

        // Step 2.5: Handle landing/dismount completion (MUST run before Step 3 mount logic)
        if (cycleLandingIssued)
        {
            if (nav.IsFlying())
            {
                // Still descending - wait for ForceLand async to finish
                if ((DateTime.Now - cycleLandingTime).TotalSeconds > 15.0)
                {
                    _plugin.AddDebugLog("[CycleMapLocs] Landing timeout after 15s - skipping location");
                    cycleMapLocationIndex++;
                    stateStartTime = DateTime.Now;
                    stateActionIssued = false;
                    cycleTeleportIssued = false;
                    cycleLandingIssued = false;
                    mountAttemptStart = DateTime.MinValue;
                    mountAttempts = 0;
                    SetupNextCycleMapLocation();
                }
                return;
            }

            if (nav.IsMounted())
            {
                // Check party wait before dismounting
                if (_plugin.Configuration.PartyWaitBeforeDismount && !ArePartyMembersClose(10.0))
                {
                    StateDetail = "[Flying] Waiting for party before dismounting...";
                    return;
                }
                // On ground but still mounted - dismount
                _mountService.Dismount();
                return;
            }

            // Fully on foot - record position and advance to next location
            var playerPos2 = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            _plugin.AddDebugLog("[CycleMapLocs] Landed and dismounted - recording position");

            if (playerPos2 != Vector3.Zero && CurrentLocation != null)
            {
                var entry = cycleMapLocationQueue[cycleMapLocationIndex];
                _plugin.MapLocationDatabase.RecordLocation(
                    CurrentLocation.TerritoryId,
                    CurrentLocation.ZoneName,
                    entry.MapName,
                    CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z,
                    playerPos2.X, playerPos2.Y, playerPos2.Z);
                _plugin.AddDebugLog($"[CycleMapLocs] Recorded XYZ: ({playerPos2.X:F1}, {playerPos2.Y:F1}, {playerPos2.Z:F1})");
            }

            cycleMapLocationIndex++;
            stateStartTime = DateTime.Now;
            stateActionIssued = false;
            cycleTeleportIssued = false;
            cycleLandingIssued = false;
            mountAttemptStart = DateTime.MinValue;
            mountAttempts = 0;
            SetupNextCycleMapLocation();
            return;
        }

        // === Ground-only mode: mount and walk (no flying) ===
        if (groundOnly)
        {
            if (CurrentLocation == null) return;
            
            // Check if we need to teleport first
            if (!stateActionIssued)
            {
                if (Plugin.ClientState.TerritoryType != CurrentLocation.TerritoryId)
                {
                    // Need to teleport to this territory first
                    if (CurrentLocation.NearestAetheryteId > 0)
                    {
                        nav.TeleportToAetheryte(CurrentLocation.NearestAetheryteId);
                        stateActionIssued = true;
                        stateStartTime = DateTime.Now;
                        StateDetail = $"[Ground] Teleporting to {CurrentLocation.ZoneName}...";
                        return;
                    }
                    else
                    {
                        _plugin.AddDebugLog($"[Ground] No aetheryte for {CurrentLocation.ZoneName} - skipping");
                        cycleMapLocationIndex++;
                        SetupNextCycleMapLocation();
                        return;
                    }
                }
                else
                {
                    // Same territory - start by mounting
                    stateActionIssued = true;
                    stateStartTime = DateTime.Now;
                }
            }

            // Wait for teleport to complete
            if (Plugin.ClientState.TerritoryType != CurrentLocation.TerritoryId)
            {
                var teleportElapsed = (DateTime.Now - stateStartTime).TotalSeconds;
                if (teleportElapsed > 5.0 && !nav.IsTeleporting())
                {
                    // Teleport done, reset actionIssued to start mounting
                    stateActionIssued = false;
                }
                return;
            }

            // Mount if not mounted (but not flying)
            if (!nav.IsMounted() && !nav.IsFlying())
            {
                if (mountAttemptStart == DateTime.MinValue)
                {
                    mountAttemptStart = DateTime.Now;
                    mountAttempts = 0;
                }
                var mountElapsed = (DateTime.Now - mountAttemptStart).TotalSeconds;
                if (mountElapsed < 3.0) return; // Grace period
                if (mountAttempts < 5 && mountElapsed >= mountAttempts * 3.0)
                {
                    mountAttempts++;
                    nav.MountUp();
                    return;
                }
                if (mountAttempts >= 5)
                {
                    _plugin.AddDebugLog($"[Ground] Mount failed - proceeding on foot");
                    // Continue on foot instead of skipping location
                }
            }

            var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            var target = new Vector3(CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z);
            var xzDist = Math.Sqrt(Math.Pow(playerPos.X - target.X, 2) + Math.Pow(playerPos.Z - target.Z, 2));

            // Arrived - dismount and record position
            if (xzDist < 3.0)
            {
                // Dismount if mounted
                if (nav.IsMounted())
                {
                    // Check party wait before dismounting
                    if (_plugin.Configuration.PartyWaitBeforeDismount && !ArePartyMembersClose(10.0))
                    {
                        StateDetail = "[Ground] Waiting for party before dismounting...";
                        return;
                    }
                    _mountService.Dismount();
                    return; // Wait for next tick to record position
                }

                _plugin.AddDebugLog($"[Ground] Arrived ({xzDist:F1}y) - recording position");
                if (playerPos != Vector3.Zero)
                {
                    var entry = cycleMapLocationQueue[cycleMapLocationIndex];
                    _plugin.MapLocationDatabase.RecordLocation(
                        CurrentLocation.TerritoryId,
                        CurrentLocation.ZoneName,
                        entry.MapName,
                        CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z,
                        playerPos.X, playerPos.Y, playerPos.Z);
                    _plugin.AddDebugLog($"[CycleMapLocs] Recorded XYZ: ({playerPos.X:F1}, {playerPos.Y:F1}, {playerPos.Z:F1})");
                }

                // Reset for next location
                cycleMapLocationIndex++;
                stateStartTime = DateTime.Now;
                stateActionIssued = false;
                cycleTeleportIssued = false;
                cycleLandingIssued = false;
                mountAttemptStart = DateTime.MinValue;
                mountAttempts = 0;
                SetupNextCycleMapLocation();
                return;
            }

            // Walk to target (reissue every 5s or on first attempt)
            if (lastStuckCheckPos.Equals(Vector3.Zero) ||
                (!lastStuckCheckPos.Equals(playerPos) && (DateTime.Now - lastStuckCheckTime).TotalSeconds > 5.0))
            {
                // Stop current navigation before re-pathfinding to prevent erratic movement
                nav.StopNavigation();
                nav.MoveToPosition(target);
                lastStuckCheckPos = playerPos;
                lastStuckCheckTime = DateTime.Now;
                _plugin.AddDebugLog($"[CycleMapLocs] Ground walking to target ({xzDist:F0}y away)");
            }

            StateDetail = $"[Ground] Location {cycleMapLocationIndex + 1}/{cycleMapLocationQueue.Count}: {CurrentLocation?.ZoneName ?? "?"} ({xzDist:F0}y)";
            return;
        }

        // === Normal flying mode ===

        // Step 3: Mount if not mounted
        if (!nav.IsMounted() && !nav.IsFlying())
        {
            if (mountAttemptStart == DateTime.MinValue)
            {
                mountAttemptStart = DateTime.Now;
                mountAttempts = 0;
            }
            var mountElapsed = (DateTime.Now - mountAttemptStart).TotalSeconds;
            if (mountElapsed < 3.0) return; // Grace period
            if (mountAttempts < 5 && mountElapsed >= mountAttempts * 3.0)
            {
                mountAttempts++;
                nav.MountUp();
                return;
            }
            if (mountAttempts >= 5)
            {
                _plugin.AddDebugLog($"[CycleMapLocs] Mount failed - skipping location");
                cycleMapLocationIndex++;
                stateStartTime = DateTime.Now;
                stateActionIssued = false;
                cycleTeleportIssued = false;
                cycleLandingIssued = false;
                mountAttemptStart = DateTime.MinValue;
                mountAttempts = 0;
                SetupNextCycleMapLocation();
                return;
            }
            return;
        }

        // Step 4: Fly to flag
        if (nav.IsMounted() && CurrentLocation != null)
        {
            var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            var target = new Vector3(CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z);
            var xzDist = Math.Sqrt(Math.Pow(playerPos.X - target.X, 2) + Math.Pow(playerPos.Z - target.Z, 2));

            // Check if we should start landing (only call ForceLand ONCE)
            if (xzDist < 2.0 && nav.IsFlying() && !cycleLandingIssued)
            {
                _plugin.AddDebugLog($"[CycleMapLocs] Close enough ({xzDist:F0}y) - issuing ForceLand");
                _mountService.ForceLand();
                cycleLandingIssued = true;
                cycleLandingTime = DateTime.Now;
                return;
            }

            // If landing was issued, Step 2.5 above handles the rest
            if (cycleLandingIssued)
                return;

            // Not close yet - fly there (only issue command once to avoid spamming)
            if (!lastStuckCheckPos.Equals(Vector3.Zero) && !lastStuckCheckPos.Equals(playerPos) &&
                (DateTime.Now - lastStuckCheckTime).TotalSeconds > 5.0)
            {
                // Stop current navigation before re-pathfinding to prevent erratic movement
                nav.StopNavigation();
                var flyTarget = new Vector3(CurrentLocation.X, CurrentLocation.Y + 50f, CurrentLocation.Z);
                nav.FlyToPosition(flyTarget);
        }

        StateDetail = $"Location {cycleMapLocationIndex + 1}/{cycleMapLocationQueue.Count}: {CurrentLocation?.ZoneName ?? "?"} ({elapsed:F0}s)";
    }
}
    // ─── Alexandrite Farming ──────────────────────────────────────────────────

    private static readonly Vector3 AurianaPosition = new(62.98f, 31.29f, -737.07f);
    private const uint MorDhonaTerritoryId = 156;
    private const uint RevenantsTollAetheryteId = 24; // Revenant's Toll aetheryte
    private static DateTime lastPoeticsLog = DateTime.MinValue; // Rate limiting for poetics logging
    private const uint MysteriousMapItemId = 7884; // Mysterious Map
    
    // Underwater navigation tracking
    private bool wasDiving = false;
    private DateTime lastDivingCheck = DateTime.MinValue;
    private Vector3 underwaterTargetPosition = Vector3.Zero;
    private bool nonThiefDivingIgnoredLogged = false;
    private static DateTime lastDigTime = DateTime.MinValue;

    /// <summary>
    /// Start the Alexandrite farming loop: buy Mysterious Map from Auriana, run it, repeat.
    /// </summary>
    public void StartAlexandriteFarming(int runCount)
    {
        if (State != BotState.Idle && State != BotState.Error && State != BotState.Completed)
        {
            _plugin.AddDebugLog("[Alexandrite] Cannot start - bot is busy");
            return;
        }

        alexandriteRunsRemaining = runCount;
        alexandriteRunsCompleted = 0;
        alexandriteStep = 0;
        alexandriteActionIssued = false;
        alexandriteStepTime = DateTime.Now;

        _plugin.AddDebugLog($"[Alexandrite] Starting {runCount} run(s)");
        _plugin.PrintChat($"Starting Alexandrite farming: {runCount} run(s)");
        TransitionTo(BotState.AlexandriteFarming, $"Alexandrite run 1/{runCount}: Starting...");
    }

    private void TickAlexandriteFarming()
    {
        var nav = _plugin.NavigationService;
        var stepElapsed = (DateTime.Now - alexandriteStepTime).TotalSeconds;

        if (alexandriteRunsRemaining <= 0)
        {
            _plugin.AddDebugLog($"[Alexandrite] All runs complete! ({alexandriteRunsCompleted} total)");
            _plugin.PrintChat($"Alexandrite farming complete! {alexandriteRunsCompleted} runs done.");
            TransitionTo(BotState.Idle, "Alexandrite farming complete!");
            return;
        }

        // Check if we already have a Mysterious Map in inventory
        var hasMap = GameHelpers.GetInventoryItemCount(MysteriousMapItemId) > 0;

        switch (alexandriteStep)
        {
            case 0: // Teleport to Revenant's Toll
                if (!alexandriteActionIssued)
                {
                    if (Plugin.ClientState.TerritoryType == MorDhonaTerritoryId)
                    {
                        // Already in Mor Dhona
                        if (hasMap)
                        {
                            // Already have a map - skip buying, go to using it
                            _plugin.AddDebugLog("[Alexandrite] Already have Mysterious Map - skipping purchase");
                            alexandriteStep = 5; // Skip to map use
                            alexandriteStepTime = DateTime.Now;
                            alexandriteActionIssued = false;
                            return;
                        }
                        alexandriteStep = 1; // Skip teleport
                        alexandriteStepTime = DateTime.Now;
                        alexandriteActionIssued = false;
                        return;
                    }
                    else if (hasMap)
                    {
                        // Have map but not in Mor Dhona - just use it
                        _plugin.AddDebugLog("[Alexandrite] Already have Mysterious Map - skipping to use");
                        alexandriteStep = 5;
                        alexandriteStepTime = DateTime.Now;
                        alexandriteActionIssued = false;
                        return;
                    }

                    nav.TeleportToAetheryte(RevenantsTollAetheryteId);
                    alexandriteActionIssued = true;
                    alexandriteStepTime = DateTime.Now;
                    StateDetail = $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Teleporting...";
                    return;
                }

                if (stepElapsed < 5.0) return;
                if (nav.IsTeleporting()) return;

                if (Plugin.ClientState.TerritoryType == MorDhonaTerritoryId)
                {
                    alexandriteStep = 1;
                    alexandriteStepTime = DateTime.Now;
                    alexandriteActionIssued = false;
                    _plugin.AddDebugLog("[Alexandrite] Arrived in Mor Dhona");
                }
                else if (stepElapsed > 30.0)
                {
                    HandleError("[Alexandrite] Teleport to Mor Dhona timed out");
                }
                return;

            case 1: // Walk to Auriana NPC
                if (!alexandriteActionIssued)
                {
                    // Clear any existing navigation flags before starting purchase
                    CommandHelper.SendCommand("/vnav clearflag");
                    _plugin.AddDebugLog("[Alexandrite] Cleared navigation flags before purchase");
                    
                    nav.MoveToPosition(AurianaPosition);
                    alexandriteActionIssued = true;
                    alexandriteStepTime = DateTime.Now;
                    StateDetail = $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Walking to Auriana...";
                    _plugin.AddDebugLog("[Alexandrite] Walking to Auriana NPC");
                    return;
                }

                var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
                var distToNpc = Vector3.Distance(playerPos, AurianaPosition);
                if (distToNpc < 5.0f)
                {
                    nav.StopNavigation();
                    alexandriteStep = 2;
                    alexandriteStepTime = DateTime.Now;
                    alexandriteActionIssued = false;
                    _plugin.AddDebugLog($"[Alexandrite] Near Auriana ({distToNpc:F1}y)");
                }
                else if (stepElapsed > 60.0)
                {
                    HandleError("[Alexandrite] Walk to Auriana timed out");
                }
                return;

            case 2: // Interact with Auriana NPC
                if (!alexandriteActionIssued)
                {
                    // Target and interact with the nearest NPC named "Auriana"
                    var auriana = GameHelpers.FindNpcByName("Auriana");
                    if (auriana != null)
                    {
                        // Force enable TextAdvance for Auriana dialogue
                        if (_plugin.IsTextAdvanceAvailable)
                        {
                            CommandHelper.SendCommand("/at enable");
                            _plugin.AddDebugLog("[Alexandrite] Enabled TextAdvance for Auriana dialogue");
                        }
                        
                        GameHelpers.InteractWithObject(auriana);
                        alexandriteActionIssued = true;
                        alexandriteStepTime = DateTime.Now;
                        StateDetail = $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Talking to Auriana...";
                        _plugin.AddDebugLog("[Alexandrite] Interacting with Auriana");
                    }
                    else if (stepElapsed > 10.0)
                    {
                        HandleError("[Alexandrite] Auriana NPC not found");
                    }
                    return;
                }

                // Wait for SelectIconString dialog
                if (stepElapsed < 1.0) return;
                if (GameHelpers.IsAddonVisible("SelectIconString"))
                {
                    // Click "Allagan Tomestones of Poetics (Other)" - index 5 (1-based for callback)
                    GameHelpers.FireAddonCallback("SelectIconString", true, 5);
                    _plugin.AddDebugLog("[Alexandrite] Selected Poetics (Other) from Auriana menu");
                    // Start handling Yes/No dialog
                    alexandriteStep = 3;
                    alexandriteStepTime = DateTime.Now;
                    
                    // Force refresh poetics count after purchase (rate limited)
                    var currentPoetics = GameHelpers.GetCurrentPoetics();
                    // Only log poetics every 10 seconds to reduce spam
                    if ((DateTime.Now - lastPoeticsLog).TotalSeconds >= 10.0)
                    {
                        _plugin.AddDebugLog($"[Alexandrite] After purchase - Current poetics: {currentPoetics}/2000");
                        lastPoeticsLog = DateTime.Now;
                    }
                    alexandriteActionIssued = false;
                }
                else if (stepElapsed > 15.0)
                {
                    HandleError("[Alexandrite] SelectIconString dialog not appearing");
                }
                return;

            case 3: // Handle Yes/No dialog after SelectIconString
                // Wait a moment for dialog to appear, then fire SelectYesno True 0
                if (stepElapsed < 2.0) return; // Wait 2 seconds for dialog to appear
                
                if (stepElapsed % 2.0 < 0.5) // Every 2 seconds after initial wait
                {
                    // Only fire if dialog is actually visible
                    if (GameHelpers.IsAddonVisible("SelectYesno"))
                    {
                        GameHelpers.FireAddonCallback("SelectYesno", true, 0);
                        var mapCount = GameHelpers.GetInventoryItemCount(MysteriousMapItemId);
                        _plugin.AddDebugLog($"[Alexandrite] Fired SelectYesno callback, map count: {mapCount}");
                        
                        if (mapCount == 1)
                        {
                            _plugin.AddDebugLog("[Alexandrite] Mysterious Map purchased successfully");
                            alexandriteStep = 5; // Skip to map use
                            alexandriteStepTime = DateTime.Now;
                            alexandriteActionIssued = false;
                        }
                    }
                    else
                    {
                        _plugin.AddDebugLog("[Alexandrite] SelectYesno dialog not visible yet, waiting...");
                    }
                }
                else if (stepElapsed > 30.0)
                {
                    HandleError("[Alexandrite] Yes/No confirmation timed out");
                }
                return;

            case 4: // Shop Exchange - buy Mysterious Map (skipped - we handle Yes/No directly)
                // This step is now handled by the Yes/No logic in case 3
                // We skip directly to case 5 after successful purchase
                alexandriteStep = 5;
                alexandriteStepTime = DateTime.Now;
                alexandriteActionIssued = false;
                return;

            case 5: // Use the Mysterious Map (decipher)
                if (stepElapsed < 2.0) return; // Wait for shop to close

                if (!alexandriteActionIssued)
                {
                    // Use the map item to decipher it using the map-specific method
                    var used = GameHelpers.UseItem(MysteriousMapItemId, _plugin.InventoryService);
                    if (used)
                    {
                        alexandriteActionIssued = true;
                        alexandriteStepTime = DateTime.Now;
                        StateDetail = $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Deciphering map...";
                        _plugin.AddDebugLog("[Alexandrite] Using Mysterious Map");
                    }
                    else if (stepElapsed > 10.0)
                    {
                        HandleError("[Alexandrite] Failed to use Mysterious Map");
                    }
                    return;
                }

                // Wait for decipher dialog then let the normal bot handle the rest
                if (stepElapsed > 3.0)
                {
                    // Hand off to normal bot flow - enable bot and start
                    _plugin.Configuration.Enabled = true;
                    _plugin.Configuration.Save();

                    // Set selected map to Mysterious Map
                    SelectedMapItemId = MysteriousMapItemId;

                    alexandriteStep = 6;
                    alexandriteStepTime = DateTime.Now;
                    alexandriteActionIssued = false;

                    // Transition to detecting location (the map is already being deciphered)
                    TransitionTo(BotState.DetectingLocation, "Reading Mysterious Map location...");
                    _plugin.AddDebugLog("[Alexandrite] Handed off to main bot for map run");
                }
                return;

            case 6: // Wait for map run to complete (bot returns to Idle/Completed/Error)
                // The normal bot flow handles everything. When it finishes:
                if (State == BotState.Idle || State == BotState.Completed || State == BotState.Error)
                {
                    alexandriteRunsCompleted++;
                    alexandriteRunsRemaining--;
                    _plugin.AddDebugLog($"[Alexandrite] Run {alexandriteRunsCompleted} complete. {alexandriteRunsRemaining} remaining.");

                    if (alexandriteRunsRemaining > 0)
                    {
                        // Reset for next run
                        alexandriteStep = 0;
                        alexandriteStepTime = DateTime.Now;
                        alexandriteActionIssued = false;
                        TransitionTo(BotState.AlexandriteFarming, $"Alexandrite run {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Starting...");
                    }
                    else
                    {
                        _plugin.PrintChat($"Alexandrite farming complete! {alexandriteRunsCompleted} runs done.");
                        TransitionTo(BotState.Idle, "Alexandrite farming complete!");
                    }
                }
                return;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    public void SetLocation(MapLocation location)
    {
        CurrentLocation = location;
        _plugin.AddDebugLog($"Location set: {location.ZoneName} ({location.X:F1}, {location.Y:F1}, {location.Z:F1})");
    }

    private bool TryGetCurrentFlagRecoveryTarget(out Vector3 flagPosition, out float distToFlag)
    {
        flagPosition = Vector3.Zero;
        distToFlag = float.MaxValue;

        if (CurrentLocation == null || CurrentLocation.TerritoryId != Plugin.ClientState.TerritoryType)
            return false;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return false;

        flagPosition = new Vector3(CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z);
        distToFlag = Vector3.Distance(player.Position, flagPosition);
        return true;
    }

    private void QueueDungeonMapFlagClear(string source)
    {
        if (pendingDungeonMapFlagClear)
            return;

        pendingDungeonMapFlagClear = true;
        _plugin.AddDebugLog($"[MapFlag] Queued overworld flag clear after treasure dungeon entry via {source}");
    }

    private void TryClearPendingDungeonMapFlag()
    {
        if (!pendingDungeonMapFlagClear)
            return;

        bool loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                       Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (loading)
            return;

        var cleared = GameHelpers.ClearMapFlag(_plugin.MapFlagService.TryReadFlag);
        pendingDungeonMapFlagClear = false;
        _plugin.AddDebugLog($"[MapFlag] Cleared overworld flag after treasure dungeon entry (verified={cleared})");
    }

    private (Vector3 NavigationTarget, Vector3 LandingTarget, string Basis, string DestinationText, string ZoneName, bool UseNavStateForLanding)
        ResolveOverworldNavigationTargets()
    {
        if (CurrentLocation == null)
            return (Vector3.Zero, Vector3.Zero, "none", "Unknown", "Unknown", true);

        var rawGroundTarget = new Vector3(CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z);
        var dbEntry = _plugin.MapLocationDatabase.FindEntry(CurrentLocation.TerritoryId, CurrentLocation.X, CurrentLocation.Z);
        var destinationIndex = dbEntry?.Index > 0 ? dbEntry.Index : -1;
        var destinationText = destinationIndex > 0 ? $"Destination #{destinationIndex}" : "Unknown";
        var zoneName = dbEntry?.ZoneName ?? CurrentLocation.ZoneName ?? "Unknown";
        var canUseUnderwaterNavigation = CanUseUnderwaterNavigation();

        if (dbEntry != null && destinationIndex > 0)
        {
            if (dbEntry.HasRealXYZ && !canUseUnderwaterNavigation)
            {
                var realTarget = new Vector3(dbEntry.RealX, dbEntry.RealY, dbEntry.RealZ);
                return (realTarget, realTarget, "stored RealXYZ", destinationText, zoneName, true);
            }

            if (canUseUnderwaterNavigation && currentLandingMode == OverworldLandingMode.UnderwaterBounce)
            {
                var specialNav = _plugin.SpecialNavigationDatabase.FindEntry(destinationIndex);
                if (specialNav != null)
                {
                    return (
                        new Vector3(specialNav.PreX, specialNav.PreY, specialNav.PreZ),
                        rawGroundTarget,
                        "special pre-dive",
                        destinationText,
                        zoneName,
                        false);
                }
            }

            if (dbEntry.HasRealXYZ)
            {
                var realTarget = new Vector3(dbEntry.RealX, dbEntry.RealY, dbEntry.RealZ);
                return (realTarget, realTarget, "stored RealXYZ", destinationText, zoneName, true);
            }

            return (
                new Vector3(CurrentLocation.X, CurrentLocation.Y + 50f, CurrentLocation.Z),
                rawGroundTarget,
                "fallback +50Y",
                destinationText,
                zoneName,
                true);
        }

        return (
            new Vector3(CurrentLocation.X, CurrentLocation.Y + 50f, CurrentLocation.Z),
            rawGroundTarget,
            dbEntry != null ? "fallback +50Y" : "no-db fallback +50Y",
            destinationText,
            zoneName,
            true);
    }

    private static double CalculateXZDistance(Vector3 from, Vector3 to)
    {
        var dx = from.X - to.X;
        var dz = from.Z - to.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    private static string FormatVectorCompact(Vector3 value)
    {
        return $"<{value.X:F1}, {value.Y:F1}, {value.Z:F1}>";
    }

    private bool ArePartyMembersClose(double maxDistance)
    {
        return EvaluateLandingPartyWait(maxDistance, "CycleMapLocs").CanProceed;
    }

    private OverworldLandingPartyWaitResult EvaluateOverworldLandingPartyWait(double maxDistance)
    {
        var party = _plugin.PartyService;
        party.UpdatePartyStatus();
        if (party.PartyMembers.Count <= 1)
        {
            LogLandingPartyWaitOnce("OverworldLanding:solo", "[PartyWait][OverworldLanding] Solo or no party members - dismounting allowed");
            return new OverworldLandingPartyWaitResult
            {
                CanProceed = true,
                NearbyOthers = 0,
                RequiredOthers = 0,
                TotalOthers = 0,
            };
        }

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        if (playerPos == Vector3.Zero)
        {
            LogLandingPartyWaitOnce("OverworldLanding:no-local-player", "[PartyWait][OverworldLanding] Local player unavailable - holding dismount");
            return new OverworldLandingPartyWaitResult
            {
                CanProceed = false,
                NearbyOthers = 0,
                RequiredOthers = 0,
                TotalOthers = party.PartyMembers.Count - 1,
            };
        }

        var localPlayerName = Plugin.ObjectTable.LocalPlayer?.Name.TextValue;
        var sameZoneFarDescriptions = new List<string>();
        var outOfZoneNames = new List<string>();
        var nearbyOthers = 0;
        var totalOthers = 0;

        foreach (var member in party.PartyMembers)
        {
            if (member.Name == localPlayerName)
                continue;

            totalOthers++;

            if (!member.IsInSameZone)
            {
                outOfZoneNames.Add(member.Name);
                continue;
            }

            var dx = playerPos.X - member.Position.X;
            var dz = playerPos.Z - member.Position.Z;
            var xzDist = Math.Sqrt(dx * dx + dz * dz);
            if (xzDist <= maxDistance)
            {
                nearbyOthers++;
                continue;
            }

            sameZoneFarDescriptions.Add($"{member.Name} is {xzDist:F1}y away (XZ)");
        }

        var useThreshold = _plugin.Configuration.PartyWaitBeforeDismountUseCountThreshold;
        var configuredThreshold = Math.Clamp(_plugin.Configuration.PartyWaitBeforeDismountRequiredOthers, 1, 7);
        var requiredOthers = useThreshold ? Math.Min(configuredThreshold, totalOthers) : totalOthers;
        var canProceed = nearbyOthers >= requiredOthers;

        var farSignature = string.Join("|", sameZoneFarDescriptions.OrderBy(text => text, StringComparer.Ordinal));
        var outOfZoneSignature = string.Join("|", outOfZoneNames.OrderBy(name => name, StringComparer.Ordinal));
        var modeSignature = useThreshold ? $"threshold:{requiredOthers}" : "full-party";

        if (!canProceed)
        {
            var message =
                $"[PartyWait][OverworldLanding] Blocking dismount; nearby {nearbyOthers}/{totalOthers} within {maxDistance:F1}y, " +
                $"required {requiredOthers}";
            if (sameZoneFarDescriptions.Count > 0)
                message += $"; same-zone far: {string.Join(", ", sameZoneFarDescriptions)}";
            if (outOfZoneNames.Count > 0)
                message += $"; out-of-zone: {string.Join(", ", outOfZoneNames)}";

            LogLandingPartyWaitOnce(
                $"OverworldLanding:block:{modeSignature}:near:{nearbyOthers}:far:{farSignature}:ooz:{outOfZoneSignature}",
                message);
        }
        else
        {
            var message =
                $"[PartyWait][OverworldLanding] Dismount allowed; nearby {nearbyOthers}/{totalOthers} within {maxDistance:F1}y, " +
                $"required {requiredOthers}";
            if (sameZoneFarDescriptions.Count > 0)
                message += $"; threshold satisfied with same-zone far members still trailing: {string.Join(", ", sameZoneFarDescriptions)}";
            if (outOfZoneNames.Count > 0)
                message += $"; threshold satisfied with out-of-zone members not yet present: {string.Join(", ", outOfZoneNames)}";

            LogLandingPartyWaitOnce(
                $"OverworldLanding:clear:{modeSignature}:near:{nearbyOthers}:far:{sameZoneFarDescriptions.Count}:ooz:{outOfZoneNames.Count}",
                message);
        }

        return new OverworldLandingPartyWaitResult
        {
            CanProceed = canProceed,
            NearbyOthers = nearbyOthers,
            RequiredOthers = requiredOthers,
            TotalOthers = totalOthers,
            SameZoneFarCount = sameZoneFarDescriptions.Count,
            OutOfZoneCount = outOfZoneNames.Count,
        };
    }

    private string BuildOverworldLandingPartyWaitDetail(OverworldLandingPartyWaitResult result, double maxDistance)
    {
        var summary =
            $"Waiting for party ({result.NearbyOthers}/{result.TotalOthers} nearby within {maxDistance:F0}y, " +
            $"need {result.RequiredOthers} before dismounting";

        if (result.SameZoneFarCount > 0)
            summary += $", {result.SameZoneFarCount} far";

        if (result.OutOfZoneCount > 0)
            summary += $", {result.OutOfZoneCount} out-of-zone";

        return summary + ")...";
    }

    private (bool CanProceed, int NearbyCount, int TotalSameZone, int IgnoredOutOfZone) EvaluateLandingPartyWait(double maxDistance, string context)
    {
        var party = _plugin.PartyService;
        party.UpdatePartyStatus();
        if (party.PartyMembers.Count <= 1)
        {
            LogLandingPartyWaitOnce($"{context}:solo", $"[PartyWait][{context}] Solo or no party members - dismounting allowed");
            return (true, 0, 0, 0);
        }

        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        if (playerPos == Vector3.Zero)
        {
            LogLandingPartyWaitOnce($"{context}:no-local-player", $"[PartyWait][{context}] Local player unavailable - holding dismount");
            return (false, 0, 0, 0);
        }

        var blockingNames = new List<string>();
        var blockingDescriptions = new List<string>();
        var ignoredOutOfZoneNames = new List<string>();
        var nearbyCount = 0;
        var totalSameZone = 0;
        foreach (var member in party.PartyMembers)
        {
            if (member.Name == Plugin.ObjectTable.LocalPlayer?.Name.TextValue) continue;

            if (!member.IsInSameZone)
            {
                ignoredOutOfZoneNames.Add(member.Name);
                continue;
            }

            totalSameZone++;
            var dx = playerPos.X - member.Position.X;
            var dz = playerPos.Z - member.Position.Z;
            var xzDist = Math.Sqrt(dx * dx + dz * dz);
            if (xzDist <= maxDistance)
            {
                nearbyCount++;
                continue;
            }

            blockingNames.Add(member.Name);
            blockingDescriptions.Add($"{member.Name} is {xzDist:F1}y away (XZ)");
        }

        var blockerSignature = string.Join("|", blockingNames.OrderBy(name => name, StringComparer.Ordinal));
        var ignoredSignature = string.Join("|", ignoredOutOfZoneNames.OrderBy(name => name, StringComparer.Ordinal));

        if (blockingNames.Count > 0)
        {
            var message = $"[PartyWait][{context}] Blocking dismount; same-zone blockers: {string.Join(", ", blockingDescriptions)}";
            if (ignoredOutOfZoneNames.Count > 0)
                message += $"; ignoring out-of-zone members: {string.Join(", ", ignoredOutOfZoneNames)}";

            LogLandingPartyWaitOnce($"{context}:block:{blockerSignature}:ignored:{ignoredSignature}", message);
            return (false, nearbyCount, totalSameZone, ignoredOutOfZoneNames.Count);
        }

        if (ignoredOutOfZoneNames.Count > 0)
        {
            LogLandingPartyWaitOnce(
                $"{context}:proceed-ignored:{ignoredSignature}",
                $"[PartyWait][{context}] Dismount allowed; ignoring out-of-zone members: {string.Join(", ", ignoredOutOfZoneNames)}");
        }
        else
        {
            LogLandingPartyWaitOnce(
                $"{context}:clear:{nearbyCount}:{totalSameZone}",
                $"[PartyWait][{context}] All same-zone party members within {maxDistance:F1}y - dismounting allowed");
        }

        return (true, nearbyCount, totalSameZone, ignoredOutOfZoneNames.Count);
    }

    private void LogLandingPartyWaitOnce(string signature, string message)
    {
        if (lastLandingPartyWaitSignature == signature)
            return;

        lastLandingPartyWaitSignature = signature;
        _plugin.AddDebugLog(message);
    }

}
