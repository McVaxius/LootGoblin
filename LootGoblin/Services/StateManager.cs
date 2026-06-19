using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
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
using FFXIVClientStructs.FFXIV.Component.GUI;

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

internal enum MapGatherStep
{
    Idle,
    SwitchingToGatherJob,
    StartingGatherBuddy,
    WaitingForMap,
    ClosingGatherWindow,
    SwitchingBack,
}

public class StateManager : IDisposable
{
    private readonly record struct ActiveMapTargetKey(uint EventItemId, uint MapItemId);
    private readonly record struct CompletedKeyItemStaleState(
        bool HasCompletionEvidence,
        bool MapDutyActive,
        bool HasTargetableCoffer,
        bool HasTargetablePortal,
        bool HasCapturedPortalPosition,
        bool PortalRetryWindowOpen)
    {
        public bool IsStale =>
            HasCompletionEvidence &&
            !MapDutyActive &&
            !HasTargetableCoffer &&
            !HasTargetablePortal &&
            !HasCapturedPortalPosition &&
            !PortalRetryWindowOpen;
    }

    private readonly record struct PartyMountWaitGate(
        bool SnapshotValid,
        int MountedOthers,
        int SeenOthers,
        int TotalOthers,
        int ExpectedOthers,
        int RequiredOthers,
        bool CanProceed,
        IReadOnlyList<string> UnavailableNames);

    private const uint ThiefMapItemId = 19770;
    private readonly Plugin _plugin;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly Dictionary<ActiveMapTargetKey, MapLocation> activeMapTargetCache = new();
    private static readonly TimeSpan AreaMapAutoCloseRetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan AreaMapAutoCloseTimeout = TimeSpan.FromSeconds(5);
    private const int AreaMapAutoCloseMaxAttempts = 8;

    public BotState State { get; private set; } = BotState.Idle;
    public string StateDetail { get; private set; } = "";
    public string WarningMessage { get; private set; } = "";
    public bool IsPaused { get; private set; }
    public int RetryCount { get; private set; }
    public uint SelectedMapItemId { get; private set; }
    public MapLocation? CurrentLocation { get; private set; }
    public bool BossModOutdoorSuppressionActive => bossModOutdoorSuppressionActive;
    public string BossModOutdoorSuppressionReason => bossModOutdoorSuppressionReason;

    private readonly DiagnosticSnapshotPolicy diagnosticSnapshotPolicy = new();
    private string lastDiagnosticTerritorySignature = string.Empty;
    private string lastDiagnosticRepairSignature = string.Empty;
    private string lastDiagnosticPartyBlockerSignature = string.Empty;
    private string lastDiagnosticAdsOwnershipSignature = string.Empty;
    private string lastTransitionSource = "initial";
    private bool preTerminalSnapshotWritten;
    private string lastPreTerminalSnapshotSource = string.Empty;

    private enum OpeningChestFlagFallbackKind
    {
        None,
        Coffer,
        Portal
    }

    private enum OverworldRecoveryNavigationKind
    {
        FlyTo,
        MoveTo
    }

    private sealed record SelectYesnoDiagnosticSnapshot(
        DateTime CapturedAt,
        bool? PromptVisible,
        string Prompt,
        uint Territory,
        bool Loading,
        bool Duty,
        bool Combat,
        string Sanctuary,
        int PartyTotal,
        int PartyLoaded,
        int PartySameTerritory,
        int PartyLoadedSameTerritory,
        BotState BotState,
        string Detail,
        string MapSelected,
        uint MapSelectedId,
        string CurrentLocation,
        string ActiveTarget,
        string RepairPhase,
        string RepairMode,
        string RepairSource,
        string LowestDurabilityPercent,
        int RepairThresholdPercent);

    private sealed record PendingSelectYesnoAfterDiagnostic(
        string Prompt,
        string Source,
        SelectYesnoDiagnosticSnapshot Before,
        DateTime DueAt);

    private DateTime stateStartTime = DateTime.Now;
    private DateTime lastTickTime = DateTime.MinValue;
    private DateTime nextFrameworkHitchLogUtc = DateTime.MinValue;
    private double lastSlowUpdateMs;
    private string lastSlowUpdateSource = "none";
    private DateTime lastSelectYesnoWatchdogTime = DateTime.MinValue;
    private readonly Queue<PendingSelectYesnoAfterDiagnostic> pendingSelectYesnoAfterDiagnostics = new();
    private DateTime pendingPartyTeleportOfferObservedAt = DateTime.MinValue;
    private string pendingPartyTeleportOfferText = string.Empty;
    private bool acceptedPartyTeleportOfferRestartPending;
    private DateTime acceptedPartyTeleportOfferAt = DateTime.MinValue;
    private bool acceptedPartyTeleportOfferSawBetweenAreas;
    private DateTime acceptedPartyTeleportOfferLastLoadingAt = DateTime.MinValue;
    private DateTime lastMapScanTime = DateTime.MinValue;
    private int mapScanCounter = 0; // Counter for reducing log spam
    private bool stateActionIssued;
    private bool startCombatJobSwitchIssued;
    private DateTime startCombatJobSwitchStartedAt = DateTime.MinValue;
    private DateTime startCombatJobLastDismountAttemptAt = DateTime.MinValue;
    private MapGatherStep mapGatherStep = MapGatherStep.Idle;
    private uint mapGatherTargetItemId;
    private string mapGatherTargetName = string.Empty;
    private int mapGatherInitialInventoryCount;
    private JobSnapshot mapGatherReturnJob;
    private bool mapGatherJobSwitchIssued;
    private bool mapGatherCancelIssued;
    private DateTime mapGatherLastCloseAttemptAt = DateTime.MinValue;
    private int mapGatherCloseAttemptCount;
    private DateTime mapGatherStepStartedAt = DateTime.MinValue;
    private DateTime mapGatherNextStatusAt = DateTime.MinValue;
    private bool mapGatherManualCommandActive;
    private readonly HashSet<uint> failedGatherMapIdsThisRun = new();
    private bool areaMapAutoCloseQueued;
    private DateTime areaMapAutoCloseQueuedAt = DateTime.MinValue;
    private DateTime areaMapAutoCloseLastAttemptAt = DateTime.MinValue;
    private int areaMapAutoCloseAttemptCount;
    private Vector3 lastStuckCheckPos; // Position at last stuck check
    private DateTime lastStuckCheckTime = DateTime.MinValue; // Time of last stuck check
    private string overworldRecoveryTargetKey = string.Empty;
    private uint overworldRecoveryTerritoryId;
    private Vector3 overworldRecoveryTarget;
    private Vector3 overworldRecoveryLastPosition;
    private float overworldRecoveryBestDistance = float.MaxValue;
    private DateTime overworldRecoveryLastProgressTime = DateTime.MinValue;
    private DateTime overworldRecoveryLastRepathTime = DateTime.MinValue;
    private DateTime overworldRecoveryLastNavmeshWaitLogTime = DateTime.MinValue;
    private DateTime overworldRecoveryLastTeleportDecisionLogTime = DateTime.MinValue;
    private string overworldRecoveryLastTeleportDecision = string.Empty;
    private int overworldRecoveryRepathCount;
    private string overworldRecoveryTeleportedTargetKey = string.Empty;
    private bool overworldRecoveryRequiresPartyMountWait;
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
    private uint openingChestCofferApproachEntityId; // Coffer being approached by vnav on the overworld
    private DateTime openingChestCofferApproachStartedAt = DateTime.MinValue; // Start of current on-foot coffer vnav attempt
    private DateTime openingChestCofferApproachLastProgressTime = DateTime.MinValue; // Last time coffer approach distance improved
    private DateTime lastOpeningChestCofferRepathTime = DateTime.MinValue; // Rate-limit coffer stop + repath recovery
    private float openingChestCofferApproachBestDistance = float.MaxValue; // Best 3D coffer distance during current approach
    private uint openingChestCofferWalkFailedEntityId; // Coffer whose near ground approach already failed
    private OpeningChestFlagFallbackKind openingChestFlagFallbackKind; // One-shot FlagX/RealY+5/FlagZ recovery owner
    private uint openingChestFlagFallbackEntityId; // Coffer entity using the one-shot fallback
    private Vector3? openingChestFlagFallbackPortalMarker; // Captured portal marker using the one-shot fallback
    private bool openingChestFlagFallbackTried; // Prevents loops for the current coffer/portal marker
    private bool openingChestFlagFallbackActive; // True while moving to FlagX/RealY+5/FlagZ
    private Vector3 openingChestFlagFallbackTarget; // Active fallback destination
    private Vector3 openingChestFlagFallbackOriginalTarget; // Failed coffer/portal target
    private DateTime openingChestFlagFallbackStartedAt = DateTime.MinValue;
    private DateTime lastOpeningChestFlagFallbackRepathTime = DateTime.MinValue;
    private Vector3? openingChestLastKnownCofferPosition; // Last targetable overworld Treasure Coffer XYZ seen this map
    private uint openingChestLastKnownCofferTerritoryId; // Territory for last known overworld coffer XYZ
    private uint openingChestLastKnownCofferEntityId; // Entity for last known overworld coffer XYZ
    private bool openingChestReturningToLastKnownCoffer; // True while recovering to captured coffer XYZ
    private DateTime lastOpeningChestLastKnownCofferLogTime = DateTime.MinValue; // Throttle captured coffer recovery logs
    private DateTime lastOpeningChestObjectScanLogTime = DateTime.MinValue; // Throttle missing-coffer ObjectTable diagnostics
    private DateTime lastOpeningChestUntargetableLogTime = DateTime.MinValue; // Throttle visible-but-untargetable coffer logs
    private DateTime lastOpeningChestTargetCommandTime = DateTime.MinValue; // Throttle /target fallback attempts
    private DateTime openingChestMissingCofferRecoveryStartedAt = DateTime.MinValue; // Bounds no-object recovery after dig
    private DateTime lastOpeningChestRecoveryDigTime = DateTime.MinValue; // Throttle recovery /gaction dig retries
    private string openingChestFlagRecoveryTargetLogKey = string.Empty; // One-shot recovery target source log per resolved target
    private int openingChestRecoveryDigRetryCount; // Bounded retry count for missing coffer after dig
    private int openingChestInteractionAttemptCount; // Cycles coffer interaction methods
    private uint openingChestInteractionEntityId; // Coffer currently being interacted with
    private uint openingChestCameraResetEntityId; // Coffer waiting for camera reset before direct interact
    private DateTime openingChestCameraResetReadyAt = DateTime.MinValue;
    private bool openingChestBotInteractionAttemptedThisMap; // Used to flag manual/inconclusive evidence
    private DateTime portalApproachStartedAt = DateTime.MinValue; // Tracks progress for portal FlyToPosition
    private float portalApproachStartDistance = float.MaxValue; // Distance when current portal approach started
    private DateTime lastPortalRepathTime = DateTime.MinValue; // Rate-limit portal stop + repath recovery
    private bool portalRegularVnavPathLogged; // One-shot log for portal vnav-only approach path
    private DateTime lastPortalTimeoutHoldLogTime = DateTime.MinValue; // Throttle timeout hold logs while portal/duty still active
    private DateTime lastPortalObjectScanLogTime = DateTime.MinValue; // Throttle active portal ObjectTable logs
    private int portalInteractionAttemptCount; // Alternates camera-based and no-camera TargetSystem while portal dialog is pending
    private DateTime portalInteractionFirstAttemptAt = DateTime.MinValue; // First TargetSystem attempt since last dialog/progress
    private DateTime portalInteractionLastAttemptAt = DateTime.MinValue; // Last portal TargetSystem attempt
    private DateTime portalInteractionLastProgressAt = DateTime.MinValue; // Last close-distance/dialog/loading progress
    private uint portalInteractionEntityId; // Portal entity owning the no-dialog recovery window
    private Vector3 portalInteractionLastPlayerPosition; // Player XYZ at last portal interaction
    private Vector3 portalInteractionLastPortalPosition; // Portal XYZ at last portal interaction
    private float portalInteractionLastDistance = float.MaxValue; // 3D distance at last portal interaction
    private float portalInteractionLastXzDistance = float.MaxValue; // XZ distance at last portal interaction
    private float portalInteractionLastYDistance = float.MaxValue; // Y delta at last portal interaction
    private float portalInteractionBestDistance = float.MaxValue; // Best 3D distance since last attempt window reset
    private int portalInteractionAttemptsSinceProgress; // Failed interaction attempts without dialog/loading/progress
    private uint portalGroundApproachEntityId; // Portal being approached on foot; 0 means captured XYZ only
    private Vector3? portalGroundApproachTarget; // Captured portal XYZ for current on-foot attempt
    private DateTime portalGroundApproachStartedAt = DateTime.MinValue; // Start of current on-foot portal vnav attempt
    private DateTime portalGroundApproachLastProgressTime = DateTime.MinValue; // Last time portal ground distance improved
    private DateTime lastPortalGroundApproachRepathTime = DateTime.MinValue; // Rate-limit portal ground repaths
    private float portalGroundApproachBestDistance = float.MaxValue; // Best 3D portal distance during current ground attempt
    private uint portalGroundApproachFailedEntityId; // Portal entity whose near ground approach already failed
    private Vector3? portalGroundApproachFailedMarker; // Captured portal XYZ whose near ground approach already failed
    private bool portalCloseNudgeActive; // Direct ground vnav close approach after no-dialog attempts
    private uint portalCloseNudgeEntityId; // Portal entity being nudged toward
    private DateTime portalCloseNudgeStartedAt = DateTime.MinValue; // Bounds close nudge duration
    private DateTime portalCloseNudgeLastCommandAt = DateTime.MinValue; // Reissue close-nudge vnav sparingly
    private int portalCloseNudgeCount; // Number of no-dialog close nudges this portal window
    private DateTime lastPortalStuckDiagnosticLogTime = DateTime.MinValue; // Throttle portal stuck diagnostics
    private uint portalCameraResetEntityId; // Portal waiting for camera reset before direct interact
    private DateTime portalCameraResetReadyAt = DateTime.MinValue;
    private bool portalUnderwaterReadyLogged; // One-shot log when a diving/dismounted portal can be interacted directly
    private DateTime dismountAttemptStart = DateTime.MinValue; // When dismount first attempted at flag X,Z
    private bool descentInProgress = false; // Whether Ctrl+Space descent is currently running
    private DateTime descentStartTime = DateTime.MinValue; // When Ctrl+Space descent started
    private float descentStartY = 0f; // Y position when descent started
    private bool descentMode = false; // Whether we're in descent+dismount mode (Ctrl+Space first)
    private DateTime lastInteractionTime = DateTime.MinValue; // Throttle chest/portal interaction attempts
    private bool autoMoveActive; // Track if automove is currently on
    private bool pendingDungeonMapFlagClear; // Clear the overworld flag once dungeon entry has settled
    private DateTime treasureHighLowVisibleSince = DateTime.MinValue; // Start of the current Higher/Lower recovery session
    private DateTime treasureHighLowNextRetryAt = DateTime.MinValue; // Next time a close/reopen callback may be sent
    private DateTime treasureHighLowLastStatusLogAt = DateTime.MinValue; // Rate-limit visible-after-attempts logs
    private DateTime treasureHighLowLastMovementStopAt = DateTime.MinValue; // Rate-limit hard movement stop commands while addon is visible
    private int treasureHighLowAttemptCount; // Number of callbacks tried in the current addon session
    private bool treasureHighLowExhaustedLogged; // Prevent exhausted-strategy log spam
    private int treasureHighLowObservedStage = 1; // Local gamble stage estimate for solver/observe modes
    private string treasureHighLowLastSnapshotSignature = string.Empty; // One log per UI transition
    private string treasureHighLowLastDecisionSignature = string.Empty; // Prevent repeated clicks on unchanged UI
    private bool combatMovementForbidSentThisCombat; // One-shot BMR/VBM movement forbid guard per combat
    private bool bossModOutdoorSuppressionActive; // BMR/VBM off while outdoor BossMod danger/radar output is visible
    private string bossModOutdoorSuppressionReason = "off";
    private bool bossModDangerProbeLoggedOnce;
    private bool lastLoggedBmrActiveModule;
    private string lastLoggedBmrActiveModuleName = string.Empty;
    private int lastLoggedVbmForbiddenZonesCount;
    private bool betweenAreasMovementStopped; // Stop LootGoblin-owned movement once per loading screen
    private DateTime teleportCommandIssuedAt = DateTime.MinValue;
    private DateTime teleportDelayStartedAt = DateTime.MinValue;
    private Vector3 teleportOriginPosition = Vector3.Zero;
    private bool teleportSawBetweenAreas;
    private DateTime teleportLastLoadingAt = DateTime.MinValue;
    private DateTime teleportLoadingClearedAt = DateTime.MinValue;
    private bool portaPraetoriaTakeoffNudgePending;
    private bool portaPraetoriaTakeoffNudgeActive;
    private DateTime portaPraetoriaTakeoffNudgeStartedAt = DateTime.MinValue;
    private bool outdoorMapFlowHoldActive;
    private BotState outdoorMapFlowHoldState = BotState.Idle;
    private string outdoorMapFlowHoldReason = string.Empty;
    private DateTime outdoorMapFlowHoldStartedAt = DateTime.MinValue;
    private DateTime outdoorMapFlowHoldLastLogAt = DateTime.MinValue;
    private bool outdoorMapFlowHoldWasJoinedFate;
    private ushort outdoorMapFlowHoldFateId;
    private bool joinedFateMapProgressBypassPartyWait;
    private DateTime lastJoinedFateLandingToggleAt = DateTime.MinValue;
    private bool joinedFateCombatAutomationActive;
    private ushort joinedFateCombatAutomationFateId;
    private readonly HashSet<string> loggedUnavailableCombatAutomationCommands = [];
    
    // Map opening validation variables
    private int initialMapCount;
    private bool mapCountChecked = false;
    private bool mapOpeningRetried = false;
    private bool selectedMapRunCountPendingDecrement;
    private bool selectedMapRunCountDecremented;
    private const double TickIntervalSeconds = 0.5;
    private static readonly TimeSpan SelectYesnoWatchdogInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SelectYesnoAfterDiagnosticDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PartyTeleportOfferPendingTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PartyTeleportAcceptedSettleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PartyTeleportPostLoadingSettleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartPreflightDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CameraResetBeforeInteractDelay = TimeSpan.FromMilliseconds(150);
    private const double DungeonInteractionIntervalSeconds = 1.0;
    private const float MapDigXZRange = 5.0f;
    private const float OutdoorMapFlowLandingRecoveryXZRange = 8.0f;
    private const double SameZoneAetheryteTeleportSkipXZRange = 50.0;
    private const float OverworldRecoveryArrivedDistance = 5.0f;
    private const float OverworldRecoveryProgressMargin = 0.5f;
    private const int OverworldRecoveryTeleportRepathThreshold = 2;
    private static readonly TimeSpan OverworldRecoveryNoProgressRepathTimeout = TimeSpan.FromSeconds(10.0);
    private static readonly TimeSpan OverworldRecoveryNoProgressTeleportTimeout = TimeSpan.FromSeconds(25.0);
    private static readonly TimeSpan OverworldRecoveryTeleportDecisionLogInterval = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan TeleportArrivalSettleDelay = TimeSpan.FromSeconds(1.0);
    private const float PortalInteractionRange = 3.0f;
    private const float PortalStrictInteractionRange = 1.6f;
    private const float PortalApproachInteractionRange = 5.0f;
    private const float PortalNormalSearchRange = 30.0f;
    private const float OverworldRecoveryObjectSearchRange = 200.0f;
    private const float OpeningChestNormalCofferSearchRange = 100.0f;
    private const float OpeningChestCofferReturnRange = 30.0f;
    private const float OpeningChestCofferStrictInteractionRange = 3.0f;
    private const float OpeningChestCofferCloseDismountDistance = 5.0f;
    private const float OpeningChestCofferMountRecoveryDistance = 3.0f;
    private const float OpeningChestCofferMountRecoveryYDelta = 0.5f;
    private const float OpeningChestCofferWalkPreferredDistance = 15.0f;
    private const float OpeningChestCofferGroundApproachYDelta = 6.0f;
    private const float OpeningChestCofferProgressMargin = 0.35f;
    private const float OpeningChestNearbyObjectScanRange = 60.0f;
    private const int OpeningChestMissingCofferMaxDigRetries = 2;
    private const float CapturedLocationMatchXZRange = 10.0f;
    private const float UnderwaterBounceTriggerXZRange = 10.0f;
    private const float UnderwaterFlagApproachArrivalXZRange = 5.0f;
    private const float UnderwaterFlagApproachProgressMargin = 0.5f;
    private const float UnderwaterFlagApproachStallMovementThreshold = 0.5f;
    private const float UnderwaterFlagApproachTargetYRefreshThreshold = 5.0f;
    private const uint LochsTerritoryId = 621;
    private const uint PortaPraetoriaAetheryteId = 102;
    private const string PortaPraetoriaTakeoffNudgeTargetName = "Aetheryte";
    private const float LochsDivingFlagApproachDepthOffset = 50.0f;
    private const float PortalRunawayDistanceIncrease = 2.0f;
    private const int UnderwaterBounceAutomoveHoldMs = 250;
    private const int UnderwaterBounceDescentHoldMs = 1000;
    private const int UnderwaterXyzDigRetryMaxAttempts = 3;
    private static readonly TimeSpan UnderwaterBounceDescentInterval = TimeSpan.FromSeconds(1.25);
    private static readonly TimeSpan UnderwaterBounceLandingSettleWindow = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan UnderwaterXyzDigRetryDelay = TimeSpan.FromSeconds(10.0);
    private static readonly TimeSpan UnderwaterFlagApproachReissueInterval = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan UnderwaterFlagApproachReissueStopDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan UnderwaterFlagApproachStallTimeout = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan UnderwaterFlagApproachForceReflyCooldown = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan UnderwaterFlagApproachHeartbeatInterval = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan UnderwaterFlagApproachPendingWaitLogInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan UnderwaterTriggerLoopLogInterval = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan ThiefMapDigSuppressionLogInterval = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan ThiefWaterRecoveryLogInterval = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan PortaPraetoriaTakeoffNudgeDuration = TimeSpan.FromSeconds(2.0);
    private static readonly HashSet<int> LochsThiefDiveSpecialDestinationIndices = new() { 534, 536, 537, 538 };
    private static readonly TimeSpan PortalMountCommandInterval = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan PortalDismountCommandInterval = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan OpeningChestCofferMountCommandInterval = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan OpeningChestCofferDismountCommandInterval = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan OpeningChestCofferStallTimeout = OverworldRecoveryNoProgressRepathTimeout;
    private static readonly TimeSpan OpeningChestCofferRepathInterval = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan GroundApproachMinimumDuration = TimeSpan.FromSeconds(12.0);
    private static readonly TimeSpan GroundApproachNoProgressTimeout = TimeSpan.FromSeconds(6.0);
    private static readonly TimeSpan GroundApproachHardTimeout = TimeSpan.FromSeconds(30.0);
    private static readonly TimeSpan OpeningChestObjectScanLogInterval = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan OpeningChestTargetFallbackInterval = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan OpeningChestRecoveryDigInterval = TimeSpan.FromSeconds(6.0);
    private static readonly TimeSpan OpeningChestMissingCofferRecoveryTimeout = TimeSpan.FromSeconds(600.0); //ahh you ASDFASDFASDF i changed from this 45 to 600. this is a hard fail state that we don't want
    private static readonly TimeSpan OpeningChestInitialCofferWaitAfterDig = TimeSpan.FromSeconds(6.0);
    private static readonly TimeSpan KeyItemMapRecoveryTimeout = TimeSpan.FromSeconds(30.0);
    private static readonly TimeSpan KeyItemMapOpenRetryInterval = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan OutdoorMapFlowHoldLogInterval = TimeSpan.FromSeconds(10.0);
    private static readonly TimeSpan JoinedFateLandingToggleInterval = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan OpeningChestJoinedFateSettleDelay = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan PortalRunawayCheckDelay = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan PortalRepathInterval = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan PortalSearchTimeout = TimeSpan.FromSeconds(15.0);
    private static readonly TimeSpan PortalActiveApproachTimeout = TimeSpan.FromSeconds(60.0);
    private static readonly TimeSpan PortalObjectScanLogInterval = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan PortalNoDialogRecoveryTimeout = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan PortalNoDialogDiagnosticInterval = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan PortalCloseNudgeTimeout = TimeSpan.FromSeconds(8.0);
    private static readonly TimeSpan PortalCloseNudgeCommandInterval = TimeSpan.FromMilliseconds(600);
    private const int PortalNoDialogRecoveryAttemptThreshold = 3;
    private const float PortalInteractionProgressMargin = 0.35f;
    private static readonly TimeSpan AdsRepairStartGrace = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan AdsRepairTimeout = TimeSpan.FromMinutes(3.0);
    private static readonly TimeSpan AdsRepairRetryDelay = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan AdsRepairRecoveryInitialTeleportDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan AdsRepairRecoveryTeleportSettleDelay = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan AdsRepairRecoveryTeleportRetryCooldown = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan AdsRepairRecoveryTimeout = TimeSpan.FromSeconds(90.0);
    private const int AdsRepairMaxRetryAttempts = 3;
    private const float AdsRepairRecoveryTeleportPositionDeltaThreshold = 5.0f;
    private const int AdsRepairRecoveryTeleportMaxRetries = 2;
    private const string AdsRepairModeSelf = "self";
    private const string AdsRepairModeNpcNoInn = "npc-no-inn";
    private const string AdsRepairModeNpcNoTeleportNoInn = "npc-no-teleport-no-inn";
    private static readonly TimeSpan TreasureHighLowSettleDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan TreasureHighLowReopenRetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TreasureHighLowStatusLogInterval = TimeSpan.FromSeconds(5.0);
    private static readonly TimeSpan TreasureHighLowMovementStopInterval = TimeSpan.FromMilliseconds(500);
    private static readonly (string Description, bool UpdateState, int Arg)[] TreasureHighLowCloseAttempts =
    {
        ("TreasureHighLow true 1", true, 1),
        ("TreasureHighLow false -2", false, -2),
        ("TreasureHighLow true 1", true, 1),
        ("TreasureHighLow true -2", true, -2),
        ("TreasureHighLow true 1", true, 1),
        ("TreasureHighLow true -1", true, -1),
        ("TreasureHighLow false -1", false, -1),
    };
    private const int TreasureHighLowCashOutCallbackArg = 1;
    private const int TreasureHighLowHigherCallbackArg = -1;
    private const int TreasureHighLowLowerCallbackArg = -2;
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
    private string startMapRefreshScope = "Start";
    private DateTime startMapRefreshStartedAt = DateTime.MinValue;
    private DateTime startPreflightReadyAt = DateTime.MinValue;
    private bool completedSaddlebagRefreshAttempted;

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
    private bool openingChestCombatInterrupted; // Combat started while recovering an overworld chest
    private bool openingChestJoinedFateHoldActive; // Joined FATE paused overworld coffer/portal work
    private ushort openingChestJoinedFateId;
    private DateTime openingChestJoinedFateHoldStartedAt = DateTime.MinValue;
    private DateTime openingChestJoinedFateHoldLastLogAt = DateTime.MinValue;
    private bool openingChestRecoveryDigIssued; // One-shot dig retry after combat interruption
    private bool openingChestReturningToFlag; // One-shot path back to the flag after combat displacement
    private HashSet<uint> attemptedCoffers = new HashSet<uint>(); // Track which coffers we've tried to interact with
    private DateTime cofferNavigationStart = DateTime.MinValue; // When we started navigating to current coffer
    private uint currentCofferId = 0; // Track which chest we're currently working on (preserved during combat)
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
    private bool adsRepairHandoffActive;
    private bool adsRepairUtilityObserved;
    private DateTime adsRepairHandoffStarted = DateTime.MinValue;
    private string adsRepairRequestedMode = string.Empty;
    private string adsRepairSource = string.Empty;
    private bool continueStartAfterAdsRepair;
    private bool adsRepairRetryPending;
    private DateTime adsRepairRetryAt = DateTime.MinValue;
    private int adsRepairRetryAttemptCount;
    private string adsRepairRetryReason = string.Empty;
    private bool adsRepairRecoveryActive;
    private bool adsRepairRecoveryTeleportIssued;
    private DateTime adsRepairRecoveryStarted = DateTime.MinValue;
    private DateTime adsRepairRecoveryTeleportIssuedAt = DateTime.MinValue;
    private Vector3 adsRepairRecoveryStartPosition;
    private bool adsRepairRecoverySawBetweenAreas;
    private DateTime adsRepairRecoveryLastLoadingAt = DateTime.MinValue;
    private bool adsRepairRecoveryStartAttempted;
    private uint adsRepairRecoveryTerritoryId;
    private uint adsRepairRecoveryAetheryteId;
    private string adsRepairRecoveryAetheryteName = string.Empty;
    private string adsRepairRecoveryMode = string.Empty;
    private string adsRepairRecoverySource = string.Empty;
    private int adsRepairRecoveryLowestCondition;
    private int adsRepairRecoveryThreshold;
    private DateTime adsRepairRecoveryNextTeleportAttemptAt = DateTime.MinValue;
    private int adsRepairRecoveryTeleportRetryCount;
    private string adsRepairRecoveryLastTeleportFailure = string.Empty;
    private bool? combatAutomationEnabledState;
    private OverworldLandingMode currentLandingMode = OverworldLandingMode.MountToggle;
    private string lastLandingPartyWaitSignature = string.Empty;
    private string partyProximityGateKey = string.Empty;
    private string partyProximityGateSignature = string.Empty;
    private DateTime partyProximityGateStartedAt = DateTime.MinValue;
    private DateTime lastPartyProximityHeartbeatAt = DateTime.MinValue;
    private DateTime lastPartyMountWaitLogTime = DateTime.MinValue;
    private int waitingForPartyExpectedMemberCount;
    private bool landingCommandsRanThisMap;
    private bool dutyEntryCommandsRanThisMap;
    private bool finishCommandsRanThisRun;
    private bool returnWhenDoneRanThisRun;
    private bool digIssuedThisMap;
    private DateTime digIssuedAt = DateTime.MinValue;
    private bool chestConfirmedThisMap;
    private bool portalConfirmedThisMap;
    private bool dungeonConfirmedThisMap;
    private bool openingChestDiscoveredByChat;
    private bool openingChestOpenedByChat;
    private bool openingChestPortalByChat;
    private bool openingChestManualInterventionSuspected;
    private DateTime openingChestDiscoveredChatAt = DateTime.MinValue;
    private DateTime openingChestOpenedChatAt = DateTime.MinValue;
    private DateTime openingChestPortalChatAt = DateTime.MinValue;
    private DateTime keyItemMapRecoveryStartedAt = DateTime.MinValue;
    private DateTime keyItemMapNextOpenAttemptAt = DateTime.MinValue;
    private int keyItemMapOpenAttemptCount;
    private uint activeKeyItemMapItemId;
    private int activeKeyItemMapSlot = -1;
    private bool activeKeyItemRecoverySourceLogged;
    private bool activeKeyItemRecoveryUnderwaterLogged;
    private bool activeKeyItemRecoveryPopupShown;
    private DateTime lastKeyItemCompletionGuardLogAt = DateTime.MinValue;
    private bool completedStaleKeyItemSuppressionActive;
    private uint completedStaleKeyItemId;
    private int completedStaleKeyItemSlot = -1;
    private uint completedStaleKeyItemMapItemId;
    private DateTime lastCompletedStaleKeyItemGuardLogAt = DateTime.MinValue;
    private DateTime lastVnavPathFailureTime = DateTime.MinValue;
    private string lastVnavPathFailureText = string.Empty;
    private bool flyFlagFallbackUsedThisFlight;
    private DateTime lastUnderwaterBounceDescentStart = DateTime.MinValue;
    private bool underwaterBounceHoldLogged;
    private bool underwaterBounceSuppressedVnavLogged;
    private bool underwaterFlagApproachIssued;
    private bool underwaterFlagApproachLogged;
    private bool underwaterBounceHandoffLogged;
    private int activeUnderwaterBounceSpecialDestinationIndex = -1;
    private bool activeUnderwaterBounceSpecialEntryReached;
    private bool thiefWaterRemountRecoveryActive;
    private bool thiefWaterRemountRecoveryZoneWaitActive;
    private DateTime lastUnderwaterFlagApproachTime = DateTime.MinValue;
    private Vector3 lastUnderwaterFlagApproachTarget = Vector3.Zero;
    private double bestUnderwaterFlagApproachXZ = double.MaxValue;
    private DateTime lastUnderwaterFlagApproachProgressTime = DateTime.MinValue;
    private DateTime lastUnderwaterFlagApproachHeartbeatTime = DateTime.MinValue;
    private Vector3 lastUnderwaterFlagApproachSamplePosition = Vector3.Zero;
    private DateTime lastUnderwaterFlagApproachSampleTime = DateTime.MinValue;
    private DateTime lastUnderwaterFlagApproachForceReflyTime = DateTime.MinValue;
    private int underwaterFlagApproachReissueCount;
    private Vector3 pendingUnderwaterFlagApproachTarget = Vector3.Zero;
    private string pendingUnderwaterFlagApproachReason = string.Empty;
    private double pendingUnderwaterFlagApproachXZ = double.MaxValue;
    private DateTime pendingUnderwaterFlagApproachScheduledAt = DateTime.MinValue;
    private NavigationState pendingUnderwaterFlagApproachPriorNavState = NavigationState.Idle;
    private DateTime lastUnderwaterFlagApproachPendingWaitLogTime = DateTime.MinValue;
    private DateTime lastUnderwaterFlagApproachDisabledLogTime = DateTime.MinValue;
    private DateTime lastUnderwaterFlagApproachObjectDeferredLogTime = DateTime.MinValue;
    private bool underwaterFlagApproachSurfacedFallbackActive;
    private DateTime lastUnderwaterTriggerLoopLogTime = DateTime.MinValue;
    private DateTime lastThiefMapDigSuppressedLogTime = DateTime.MinValue;
    private DateTime lastThiefWaterRecoveryLogTime = DateTime.MinValue;

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
    private bool alexandriteSessionActive;
    private bool alexandriteAwaitingMapCompletion;
    private uint pendingAlexandriteMapTargetItemId;
    private bool alexandriteSawBetweenAreas;
    private DateTime alexandriteLastLoadingAt = DateTime.MinValue;
    private DateTime alexandriteLoadingClearedAt = DateTime.MinValue;
    private DateTime alexandriteApproachLastMountAttemptAt = DateTime.MinValue;
    public int AlexandriteRunsRemaining => alexandriteRunsRemaining;
    public int AlexandriteRunsCompleted => alexandriteRunsCompleted;

    private static readonly Dictionary<BotState, double> StateTimeouts = new()
    {
        { BotState.StartPreflight,    10  },
        { BotState.OpeningMap,        30  },
        { BotState.DetectingLocation, 30  },
        { BotState.Teleporting,       90  },
        { BotState.Mounting,          30  },
        { BotState.Flying,            300 },
        { BotState.OpeningChest,      120 },
        { BotState.DungeonCombat,      300 },
        { BotState.DungeonLooting,     120 },
        { BotState.DungeonProgressing, 120 },
        { BotState.CyclingAetherytes,   60  },
        { BotState.CyclingMapLocations, 300 },
        { BotState.AlexandriteFarming,  300 },
        { BotState.GatheringMap,        1200 },
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
        var updateStopwatch = Stopwatch.StartNew();
        var currentSection = "delayed-callbacks";
        var sectionStopwatch = Stopwatch.StartNew();
        var slowestSection = "none";
        var slowestMs = 0d;

        void CompleteSection()
        {
            var elapsedMs = sectionStopwatch.Elapsed.TotalMilliseconds;
            if (elapsedMs > slowestMs)
            {
                slowestMs = elapsedMs;
                slowestSection = currentSection;
            }
        }

        void StartSection(string section)
        {
            CompleteSection();
            currentSection = section;
            sectionStopwatch.Restart();
        }

        try
        {
        // Update delayed callbacks for SelectIconString
        GameHelpers.UpdateDelayedCallbacks();

        if (IsAreaTransitionActive())
        {
            StartSection("between-areas");
            HandleBetweenAreasTick();
            return;
        }

        betweenAreasMovementStopped = false;

        if (TickAcceptedPartyTeleportOfferRestart())
            return;
        
        // Auto-discard runs when bot is enabled (any state)
        StartSection("auto-discard");
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

        StartSection("companion");
        var companionOperationActive = _plugin.Configuration.Enabled
            && !IsPaused
            && State is not BotState.Idle and not BotState.Error;

        if (!companionOperationActive)
        {
            companionStanceDeferred = DateTime.MinValue;
        }

        // Companion chocobo summoning (every 15s when bot is actively operating)
        if (companionOperationActive && _plugin.Configuration.SummonChocobo && Plugin.ClientState.IsLoggedIn)
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

        StartSection("state-tick");
        var allowWithoutEnabled = State is BotState.CyclingAetherytes or BotState.CyclingMapLocations or BotState.AlexandriteFarming ||
                                  (State == BotState.GatheringMap && mapGatherManualCommandActive);
        if (!_plugin.Configuration.Enabled && !allowWithoutEnabled)
        {
            ResetAreaMapAutoClose();
            LogUnderwaterFlagApproachDisabledAbandoned(DateTime.Now);
            return;
        }
        if (IsPaused) return;
        if (State == BotState.Idle || State == BotState.Error) return;

        var now2 = DateTime.Now;
        if ((now2 - lastTickTime).TotalSeconds < TickIntervalSeconds) return;
        lastTickTime = now2;

        if (!Plugin.ClientState.IsLoggedIn)
        {
            // Lost connection is the only legitimate reason to stop the bot
            WritePreTerminalSnapshot("lost-connection");
            _plugin.NavigationService.StopNavigation();
            TransitionTo(BotState.Error, "Lost connection - not logged in.");
            return;
        }

        if ((_plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver || adsRepairHandoffActive) && _plugin.IsAdsAvailable)
            _plugin.AdsStatusService.Refresh();

        CheckStateTimeout();
        TickAreaMapAutoClose();
        Tick();
        }
        finally
        {
            try
            {
                UpdateDiagnosticSnapshots();
            }
            catch (Exception ex)
            {
                Plugin.LogError($"[Diagnostics] Snapshot scheduling failed: {ex.Message}");
            }

            CompleteSection();
            updateStopwatch.Stop();
            ReportFrameworkHitch(updateStopwatch.Elapsed.TotalMilliseconds, slowestSection, slowestMs);
        }
    }

    private static bool IsAreaTransitionActive()
        => Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51];

    private void ReportFrameworkHitch(double elapsedMs, string slowestSection, double slowestMs)
    {
        lastSlowUpdateMs = elapsedMs;
        lastSlowUpdateSource = slowestSection;
        if (elapsedMs < 100d)
            return;

        var now = DateTime.UtcNow;
        if (now < nextFrameworkHitchLogUtc)
            return;

        nextFrameworkHitchLogUtc = now.AddSeconds(5);
        Plugin.LogWarning(
            "[LootGoblin][HITCH] state framework update slow elapsedMs={ElapsedMs:0.0}; slowSection={SlowSection}; slowSectionMs={SlowSectionMs:0.0}; transition={Transition}; state={State}; navState={NavState}; detail='{Detail}'.",
            elapsedMs,
            slowestSection,
            slowestMs,
            IsAreaTransitionActive(),
            State,
            _plugin.NavigationService.State,
            StateDetail);
    }

    private void CheckStateTimeout()
    {
        if (!StateTimeouts.TryGetValue(State, out var timeout)) return;

        if (adsRepairHandoffActive)
        {
            stateStartTime = DateTime.Now;
            return;
        }

        if (ShouldHoldOutdoorMapFlowState(State) && TryGetOutdoorMapFlowHoldReason(out _))
        {
            stateStartTime = DateTime.Now;
            return;
        }

        if (State == BotState.OpeningChest && TryGetOutdoorMapFlowHoldReason(out _))
        {
            stateStartTime = DateTime.Now;
            return;
        }

        if (State == BotState.DetectingLocation && _plugin.InventoryService.HasTreasureMapKeyItem())
        {
            // Active key-item recovery has its own bounded timer in TickDetectingLocation.
            stateStartTime = DateTime.Now;
            return;
        }

        if (State == BotState.Teleporting && !stateActionIssued && teleportDelayStartedAt != DateTime.MinValue)
        {
            // Pre-command delay should not consume the existing teleport timeout window.
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

        if (_plugin.NavigationService.State == NavigationState.WaitingForNavmesh)
        {
            stateStartTime = DateTime.Now;
            return;
        }
        
        var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
        if (elapsed > timeout)
        {
            _plugin.AddDebugLog($"[TIMEOUT] State {State} timed out after {elapsed:F0}s (limit: {timeout}s)");
            if (State == BotState.GatheringMap)
            {
                FailMapGathering($"Map gathering timed out after {timeout}s.");
                return;
            }

            HandleError($"Timeout in state {State} after {timeout}s.");
        }
    }

    private void Tick()
    {
        // Guard: never access game memory during zone transitions (loading screens)
        bool loading = Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51];
        UpdatePendingSelectYesnoAfterDiagnostics(loading);
        if (loading)
        {
            HandleBetweenAreasTick();
            return;
        }
        betweenAreasMovementStopped = false;

        if (TrySkipCardGame())
            return;

        RunSelectYesnoWatchdog();
        if (TickAcceptedPartyTeleportOfferRestart())
            return;

        UpdateBossModOutdoorSuppression();

        bool currentlyInCombat = Plugin.Condition[ConditionFlag.InCombat];
        if (!currentlyInCombat && TryKeepCombatAutomationForJoinedFate("active joined-FATE tick"))
        {
            // Joined FATE owns combat automation until the hold clears.
        }
        else if (!currentlyInCombat)
        {
            SetCombatAutomationForCombatState(inCombat: false, "active non-combat tick");
        }

        // Universal combat pathfinding stop - only stop when actually in combat
        if (currentlyInCombat && autoMoveActive)
        {
            _plugin.NavigationService.StopNavigation();
            autoMoveActive = false;
        }

        // Combat start/end tracking for objective system
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

        TryClearPendingDungeonMapFlag();

        if (TickAdsRepairRecovery())
            return;

        if (TickAdsRepairRetry())
            return;

        if (TickAdsRepairHandoff())
            return;

        // Check for territory change and refresh maps to fix inventory index issues
        var currentTerritory = Plugin.ClientState.TerritoryType;
        if (lastGlobalTerritoryId != 0 && lastGlobalTerritoryId != currentTerritory)
        {
            ResetPortalApproachTrackingForAreaChange();
            ResetAllCameraResetBeforeInteractTracking();
            _plugin.AddDebugLog($"[Territory] Territory changed: {lastGlobalTerritoryId} -> {currentTerritory} - refreshing maps");
            _plugin.InventoryService.ScanForMaps();
        }
        lastGlobalTerritoryId = currentTerritory;

        if (TryYieldToActiveAdsDutyOwnership(currentTerritory))
            return;

        if (TryHoldOutdoorMapFlowTick())
            return;

        switch (State)
        {
            case BotState.StartPreflight: TickStartPreflight(); break;
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
            case BotState.GatheringMap:      TickGatheringMap();      break;
            case BotState.Completed:        TickCompleted();        break;
        }
    }

    private bool TryHoldOutdoorMapFlowTick()
    {
        if (!ShouldHoldOutdoorMapFlowState(State))
            return false;

        if (!TryGetOutdoorMapFlowHoldReason(out var reason))
        {
            return ReleaseOutdoorMapFlowHoldIfNeeded();
        }

        EnterOutdoorMapFlowHold(reason, $"[{State}]");
        return true;
    }

    private bool TryHoldCompletedNextMapStartup(string source)
    {
        if (!TryGetOutdoorMapFlowHoldReason(out var reason))
        {
            return ReleaseOutdoorMapFlowHoldIfNeeded();
        }

        EnterOutdoorMapFlowHold(reason, source);
        return true;
    }

    private static bool ShouldHoldOutdoorMapFlowState(BotState state)
        => state is BotState.SelectingMap
            or BotState.DetectingLocation
            or BotState.Teleporting
            or BotState.Mounting
            or BotState.WaitingForParty
            or BotState.Flying;

    private bool TryGetOutdoorMapFlowHoldReason(out string reason)
    {
        var joinedFate = _plugin.FateSyncService.TryGetJoinedFateId(out var fateId);
        if (Plugin.Condition[ConditionFlag.InCombat] && !IsMountedOrActualInFlight())
        {
            reason = joinedFate
                ? $"in combat during joined FATE {fateId}"
                : "in combat";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private bool IsJoinedFateOutdoorInterventionFlowActive()
    {
        if (State is BotState.Idle or BotState.Error)
            return false;

        if (IsTreasureDungeonTerritory(Plugin.ClientState.TerritoryType))
            return false;

        var activeOutdoorState = State is BotState.SelectingMap
            or BotState.OpeningMap
            or BotState.DetectingLocation
            or BotState.Teleporting
            or BotState.Mounting
            or BotState.WaitingForParty
            or BotState.Flying
            or BotState.OpeningChest
            || (State == BotState.Completed && _plugin.Configuration.AutoStartNextMap);

        if (!activeOutdoorState)
            return false;

        return CurrentLocation != null
            || SelectedMapItemId != 0
            || _plugin.InventoryService.HasTreasureMapKeyItem()
            || IsOverworldMapDutyActive()
            || digIssuedThisMap
            || chestConfirmedThisMap
            || portalConfirmedThisMap
            || openingChestLastKnownCofferPosition.HasValue
            || portalRetryStart != DateTime.MinValue
            || portalApproachPosition.HasValue;
    }

    private void EnterOutdoorMapFlowHold(string reason, string source)
    {
        var now = DateTime.Now;
        var joinedFateHold = _plugin.FateSyncService.TryGetJoinedFateId(out var fateId) &&
                             IsJoinedFateOutdoorInterventionFlowActive();
        var entering = !outdoorMapFlowHoldActive ||
                       outdoorMapFlowHoldState != State ||
                       !string.Equals(outdoorMapFlowHoldReason, reason, StringComparison.Ordinal);

        if (entering)
        {
            outdoorMapFlowHoldActive = true;
            outdoorMapFlowHoldState = State;
            outdoorMapFlowHoldReason = reason;
            outdoorMapFlowHoldStartedAt = now;
            outdoorMapFlowHoldLastLogAt = now;
            outdoorMapFlowHoldWasJoinedFate = joinedFateHold;
            outdoorMapFlowHoldFateId = joinedFateHold ? fateId : (ushort)0;

            PrepareOutdoorMapFlowHoldEntry(source, reason);
            _plugin.AddDebugLog($"{source}[OutdoorHold] Holding outdoor map flow in {State}: {reason}.");
        }
        else if (joinedFateHold)
        {
            outdoorMapFlowHoldWasJoinedFate = true;
            outdoorMapFlowHoldFateId = fateId;
        }
        else if (now - outdoorMapFlowHoldLastLogAt >= OutdoorMapFlowHoldLogInterval)
        {
            outdoorMapFlowHoldLastLogAt = now;
            _plugin.AddDebugLog($"{source}[OutdoorHold] Still holding outdoor map flow in {State}: {reason}.");
        }

        stateStartTime = now;
        TryKeepCombatAutomationForJoinedFate($"{source} outdoor hold");
        var elapsed = outdoorMapFlowHoldStartedAt == DateTime.MinValue
            ? TimeSpan.Zero
            : now - outdoorMapFlowHoldStartedAt;
        StateDetail = State == BotState.Teleporting
            ? $"Holding teleport while {reason} ({elapsed.TotalSeconds:F0}s)..."
            : $"Outdoor map flow paused while {reason} ({elapsed.TotalSeconds:F0}s)...";
    }

    private void PrepareOutdoorMapFlowHoldEntry(string source, string reason)
    {
        if (_plugin.FateSyncService.TryGetJoinedFateId(out var fateId) &&
            IsJoinedFateOutdoorInterventionFlowActive())
        {
            overworldRecoveryRequiresPartyMountWait = false;
            if (State == BotState.WaitingForParty)
                waitingForPartyExpectedMemberCount = 0;
            _plugin.AddDebugLog($"{source}[OutdoorHold] Joined FATE {fateId}: suspending party mount wait for this interruption.");
        }

        if (autoMoveActive)
        {
            CommandHelper.SendCommand("/automove off");
            autoMoveActive = false;
        }

        ResetPortaPraetoriaTakeoffNudge($"{source}[OutdoorHold] {reason}", stopAutomove: true);

        if (descentInProgress)
        {
            CommandHelper.SendCommand("/automove off");
            GameHelpers.KeyRelease(VirtualKey.W);
            GameHelpers.KeyRelease(VirtualKey.CONTROL);
            GameHelpers.KeyRelease(VirtualKey.SPACE);
            descentInProgress = false;
        }

        if (State == BotState.Teleporting)
        {
            if (!teleportSawBetweenAreas)
                ResetOutdoorMapTeleportAttempt($"{source}[OutdoorHold] {reason}");
            return;
        }

        if (State is BotState.Flying or BotState.Mounting or BotState.WaitingForParty)
        {
            if (_plugin.NavigationService.State != NavigationState.Idle)
                _plugin.NavigationService.StopNavigation();
        }

        if (State == BotState.Flying)
        {
            stateActionIssued = false;
            ResetVnavFlyFlagFallbackState();
            ResetOverworldRecoveryState();
            dismountAttemptStart = DateTime.MinValue;
        }
        else if (State == BotState.Mounting)
        {
            mountAttemptStart = DateTime.MinValue;
            mountAttempts = 0;
        }
        else if (State == BotState.SelectingMap)
        {
            lastMapScanTime = DateTime.MinValue;
        }
        else if (State == BotState.DetectingLocation)
        {
            keyItemMapRecoveryStartedAt = DateTime.MinValue;
        }
    }

    private bool TryHandleJoinedFateMountedIntervention(
        ushort fateId,
        string source,
        DateTime now,
        bool updateStateDetail)
    {
        var mountedOrFlying = _plugin.NavigationService.IsMounted() ||
                              _plugin.NavigationService.IsFlying() ||
                              Plugin.Condition[ConditionFlag.Mounting71] ||
                              Plugin.Condition[ConditionFlag.RidingPillion];
        if (!mountedOrFlying)
            return false;

        StopOutdoorMapFlowRecoveryNavigation();

        if (descentInProgress)
        {
            CommandHelper.SendCommand("/automove off");
            GameHelpers.KeyRelease(VirtualKey.W);
            GameHelpers.KeyRelease(VirtualKey.CONTROL);
            GameHelpers.KeyRelease(VirtualKey.SPACE);
            descentInProgress = false;
        }

        if (!Plugin.Condition[ConditionFlag.Mounting71] &&
            now - lastJoinedFateLandingToggleAt >= JoinedFateLandingToggleInterval)
        {
            lastJoinedFateLandingToggleAt = now;
            _mountService.TryLandingToggle();
            _plugin.AddDebugLog($"{source}[OutdoorHold] Joined FATE {fateId}: landing/dismount toggle sent for level sync.");
        }

        if (updateStateDetail)
            StateDetail = $"Joined FATE {fateId} active - landing/dismounting before level sync...";

        return true;
    }

    private bool ReleaseOutdoorMapFlowHoldIfNeeded()
    {
        if (!outdoorMapFlowHoldActive)
            return false;

        var now = DateTime.Now;
        var elapsed = outdoorMapFlowHoldStartedAt == DateTime.MinValue
            ? TimeSpan.Zero
            : now - outdoorMapFlowHoldStartedAt;
        var heldState = outdoorMapFlowHoldState;
        var heldReason = outdoorMapFlowHoldReason;
        var heldWasJoinedFate = outdoorMapFlowHoldWasJoinedFate;
        var heldFateId = outdoorMapFlowHoldFateId;
        _plugin.AddDebugLog($"[OutdoorHold] Released after {elapsed.TotalSeconds:F1}s from {heldState} ({heldReason}); recovering forward from {State}.");

        outdoorMapFlowHoldActive = false;
        outdoorMapFlowHoldState = BotState.Idle;
        outdoorMapFlowHoldReason = string.Empty;
        outdoorMapFlowHoldStartedAt = DateTime.MinValue;
        outdoorMapFlowHoldLastLogAt = DateTime.MinValue;
        outdoorMapFlowHoldWasJoinedFate = false;
        outdoorMapFlowHoldFateId = 0;
        stateStartTime = now;

        if (heldWasJoinedFate)
            ArmJoinedFateMapProgressBypass(heldFateId, heldState);

        if (State == BotState.Teleporting)
            stateActionIssued = teleportSawBetweenAreas;
        else if (State == BotState.DetectingLocation)
            keyItemMapRecoveryStartedAt = DateTime.MinValue;

        NormalizeLandingModeForSelectedMap("[OutdoorHold] release");
        _plugin.AddDebugLog(
            $"[OutdoorHold] Release map context: mapId={SelectedMapItemId}; landing={currentLandingMode}.");

        return TryRecoverForwardAfterOutdoorMapFlowHold(heldState, heldReason);
    }

    private void ClearOutdoorMapFlowHold()
    {
        outdoorMapFlowHoldActive = false;
        outdoorMapFlowHoldState = BotState.Idle;
        outdoorMapFlowHoldReason = string.Empty;
        outdoorMapFlowHoldStartedAt = DateTime.MinValue;
        outdoorMapFlowHoldLastLogAt = DateTime.MinValue;
        outdoorMapFlowHoldWasJoinedFate = false;
        outdoorMapFlowHoldFateId = 0;
    }

    private void ArmJoinedFateMapProgressBypass(ushort fateId, BotState heldState)
    {
        joinedFateMapProgressBypassPartyWait = true;
        overworldRecoveryRequiresPartyMountWait = false;
        waitingForPartyExpectedMemberCount = 0;
        var fateText = fateId == 0 ? "joined FATE" : $"joined FATE {fateId}";
        _plugin.AddDebugLog($"[OutdoorHold] {fateText} cleared from {heldState}; bypassing party waits until current map progress resumes.");
    }

    private bool ConsumeJoinedFateMountWaitBypass(string source)
    {
        if (!joinedFateMapProgressBypassPartyWait)
            return false;

        overworldRecoveryRequiresPartyMountWait = false;
        _plugin.AddDebugLog($"{source} Joined-FATE recovery bypassed party mount wait.");
        return true;
    }

    private void ClearOpeningChestJoinedFateHold()
    {
        openingChestJoinedFateHoldActive = false;
        openingChestJoinedFateId = 0;
        openingChestJoinedFateHoldStartedAt = DateTime.MinValue;
        openingChestJoinedFateHoldLastLogAt = DateTime.MinValue;
    }

    private bool TryRecoverForwardAfterOutdoorMapFlowHold(BotState heldState, string heldReason)
    {
        var loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                      Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (loading || IsTreasureDungeonTerritory(Plugin.ClientState.TerritoryType))
            return false;

        var portal = FindNearestPortal(keepActivePortalWindow: true);
        if (portal != null)
        {
            CapturePortalApproachPosition(portal);
            ResumePortalFlowAfterOutdoorHold(
                $"visible portal entity={portal.EntityId}",
                $"FATE/combat cleared - resuming portal after {heldState}...");
            return true;
        }

        if (portalApproachPosition.HasValue)
        {
            ResumePortalFlowAfterOutdoorHold(
                $"captured portal XYZ {FormatVectorCompact(portalApproachPosition.Value)}",
                $"FATE/combat cleared - resuming captured portal after {heldState}...");
            return true;
        }

        var coffer = _plugin.ChestDetectionService.FindNearestCoffer(OverworldRecoveryObjectSearchRange);
        if (coffer != null)
        {
            CaptureOpeningChestCofferPosition(coffer);
            chestConfirmedThisMap = true;
            chestDisappearedTime = DateTime.MinValue;
            _plugin.AddDebugLog(
                $"[OutdoorHold] FATE/combat cleared after {heldReason}; visible coffer '{coffer.Name.TextValue}' " +
                $"entity={coffer.EntityId} targetable={coffer.IsTargetable} - resuming chest recovery.");
            TransitionTo(BotState.OpeningChest, "FATE/combat cleared - resuming chest recovery...");
            return true;
        }

        if (TryGetOpeningChestLastKnownCofferPosition(out var knownCoffer, out var knownCofferDistance))
        {
            chestDisappearedTime = DateTime.MinValue;
            _plugin.AddDebugLog(
                $"[OutdoorHold] FATE/combat cleared after {heldReason}; known coffer XYZ " +
                $"{FormatVectorCompact(knownCoffer)} ({knownCofferDistance:F1}y) - resuming chest recovery.");
            TransitionTo(BotState.OpeningChest, "FATE/combat cleared - returning to known coffer...");
            return true;
        }

        if (digIssuedThisMap)
        {
            StopOutdoorMapFlowRecoveryNavigation();
            _plugin.AddDebugLog($"[OutdoorHold] FATE/combat cleared after {heldReason}; dig was already issued - resuming chest recovery.");
            TransitionTo(BotState.OpeningChest, "FATE/combat cleared - looking for treasure coffer...");
            return true;
        }

        if (TryGetCurrentMapLandingDistance(out var landingDistance, out var landingTarget, out var landingBasis) &&
            landingDistance <= GetCurrentMapLandingHoldRange())
        {
            StopOutdoorMapFlowRecoveryNavigation();
            var playerPosition = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            if (!IsThiefUnderwaterLandingMode() &&
                playerPosition != Vector3.Zero &&
                TryHandleMapLandingAndDig(
                    "[OutdoorHold] landing recovery",
                    landingBasis,
                    playerPosition,
                    landingTarget,
                    landingDistance))
            {
                _plugin.AddDebugLog(
                    $"[OutdoorHold] FATE/combat cleared after {heldReason}; recovered directly into landing/dig at " +
                    $"{landingDistance:F1}y XZ from {FormatVectorCompact(landingTarget)} ({landingBasis}).");
                return true;
            }

            _plugin.AddDebugLog(
                $"[OutdoorHold] FATE/combat cleared after {heldReason}; already at map target " +
                $"{FormatVectorCompact(landingTarget)} ({landingDistance:F1}y XZ, {landingBasis}) - resuming landing/dig.");
            TransitionTo(BotState.Flying, "FATE/combat cleared - landing at map target...");
            return true;
        }

        if (TryResumeActiveMapTargetAfterOutdoorHold(heldState, heldReason))
            return true;

        _plugin.AddDebugLog($"[OutdoorHold] No forward recovery target after {heldReason}; resuming held state {heldState}.");
        return false;
    }

    private bool TryResumeActiveMapTargetAfterOutdoorHold(BotState heldState, string heldReason)
    {
        if (CurrentLocation == null)
            return false;

        StopOutdoorMapFlowRecoveryNavigation();
        stateActionIssued = false;
        dismountAttemptStart = DateTime.MinValue;

        if (CurrentLocation.TerritoryId != Plugin.ClientState.TerritoryType)
        {
            _plugin.AddDebugLog(
                $"[OutdoorHold] FATE/combat cleared after {heldReason}; active map target is in territory {CurrentLocation.TerritoryId}, " +
                $"current territory is {Plugin.ClientState.TerritoryType} - retrying teleport.");
            TransitionTo(BotState.Teleporting, "FATE/combat cleared - teleporting back to map zone...");
            return true;
        }

        if (_plugin.NavigationService.IsMounted() || _plugin.NavigationService.IsFlying())
        {
            _plugin.AddDebugLog(
                $"[OutdoorHold] FATE/combat cleared after {heldReason}; resuming mounted travel to active map target from {heldState}.");
            TransitionTo(BotState.Flying, "FATE/combat cleared - flying to active map target...");
            return true;
        }

        _plugin.AddDebugLog(
            $"[OutdoorHold] FATE/combat cleared after {heldReason}; remounting to resume active map target from {heldState}.");
        TransitionTo(BotState.Mounting, "FATE/combat cleared - remounting for active map target...");
        return true;
    }

    private void StopOutdoorMapFlowRecoveryNavigation()
    {
        if (autoMoveActive)
        {
            CommandHelper.SendCommand("/automove off");
            autoMoveActive = false;
        }

        if (_plugin.NavigationService.State != NavigationState.Idle)
            _plugin.NavigationService.StopNavigation();
    }

    private void ResumePortalFlowAfterOutdoorHold(string reason, string detail)
    {
        if (portalRetryStart == DateTime.MinValue)
            portalRetryStart = DateTime.Now;

        _plugin.AddDebugLog($"[OutdoorHold] FATE/combat cleared; resuming portal flow from {reason}.");
        TransitionTo(BotState.Completed, detail);
    }

    private bool TryGetCurrentMapLandingDistance(
        out double distance,
        out Vector3 landingTarget,
        out string basis)
    {
        distance = double.MaxValue;
        landingTarget = Vector3.Zero;
        basis = string.Empty;

        if (CurrentLocation == null ||
            CurrentLocation.TerritoryId != Plugin.ClientState.TerritoryType)
        {
            return false;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return false;

        var targets = ResolveOverworldNavigationTargets();
        if (targets.LandingTarget == Vector3.Zero)
            return false;

        landingTarget = targets.LandingTarget;
        basis = targets.Basis;
        distance = CalculateXZDistance(player.Position, landingTarget);
        return true;
    }

    private double GetCurrentMapLandingHoldRange()
        => IsThiefUnderwaterLandingMode()
            ? UnderwaterBounceTriggerXZRange
            : Math.Max(MapDigXZRange, OutdoorMapFlowLandingRecoveryXZRange);

    private void ResetOutdoorMapTeleportAttempt(string source)
    {
        if (_plugin.NavigationService.State != NavigationState.Idle)
            _plugin.NavigationService.StopNavigation();

        ResetTeleportLifecycleTracking();
        stateActionIssued = false;
        stateStartTime = DateTime.Now;
        _plugin.AddDebugLog($"{source}: teleport attempt reset before area load; will retry after hold clears.");
    }

    private bool TryDeferTeleportCombatError(string message)
    {
        if (State != BotState.Teleporting || !IsTeleportCombatBlockedMessage(message))
            return false;

        if (TryGetOutdoorMapFlowHoldReason(out var reason))
        {
            EnterOutdoorMapFlowHold(reason, "[Teleporting]");
            return true;
        }

        ResetOutdoorMapTeleportAttempt("[Teleporting][OutdoorHold] combat reject");
        StateDetail = "Teleport blocked by combat - retrying...";
        return true;
    }

    private static bool IsTeleportCombatBlockedMessage(string message)
        => message.Contains("Cannot teleport while in combat", StringComparison.OrdinalIgnoreCase);

    private void RunSelectYesnoWatchdog()
    {
        var now = DateTime.Now;
        if (now - lastSelectYesnoWatchdogTime < SelectYesnoWatchdogInterval)
            return;

        lastSelectYesnoWatchdogTime = now;

        if (ClickYesIfVisibleWithDiagnostics("SelectYesnoWatchdog"))
            _plugin.AddDebugLog("[SelectYesnoWatchdog] Clicked Yes on visible SelectYesno dialog.");
    }

    internal bool ClickYesIfVisibleWithDiagnostics(string source)
    {
        SelectYesnoDiagnosticSnapshot? beforeSnapshot = null;
        if (!GameHelpers.ClickYesIfVisible(
                source,
                out var prompt,
                observedPrompt =>
                {
                    beforeSnapshot = CaptureSelectYesnoDiagnosticSnapshot(
                        NormalizeSelectYesnoPrompt(observedPrompt),
                        promptVisible: true,
                        allowAddonRead: false);
                    LogSelectYesnoObserved(observedPrompt, source, beforeSnapshot);
                }))
            return false;

        prompt = NormalizeSelectYesnoPrompt(prompt);
        beforeSnapshot ??= CaptureSelectYesnoDiagnosticSnapshot(prompt, promptVisible: true, allowAddonRead: false);
        LogSelectYesnoAccepted(prompt, source, beforeSnapshot);
        QueueSelectYesnoAfterDiagnostic(prompt, source, beforeSnapshot);

        var now = DateTime.Now;
        if (IsPendingPartyTeleportOfferActive(now))
            QueueAcceptedPartyTeleportOfferRestart(now);

        return true;
    }

    private void UpdatePendingSelectYesnoAfterDiagnostics(bool loading)
    {
        if (pendingSelectYesnoAfterDiagnostics.Count == 0)
            return;

        var now = DateTime.Now;
        while (pendingSelectYesnoAfterDiagnostics.TryPeek(out var pending) && pending.DueAt <= now)
        {
            pendingSelectYesnoAfterDiagnostics.Dequeue();
            var after = CaptureSelectYesnoDiagnosticSnapshot(pending.Prompt, promptVisible: null, allowAddonRead: !loading);
            LogSelectYesnoAfter(pending.Prompt, pending.Source, pending.Before, after);
        }
    }

    private SelectYesnoDiagnosticSnapshot CaptureSelectYesnoDiagnosticSnapshot(
        string prompt,
        bool? promptVisible,
        bool allowAddonRead)
    {
        var loading = IsAreaTransitionActive();
        var normalizedPrompt = NormalizeSelectYesnoPrompt(prompt);
        if (allowAddonRead && !loading)
        {
            if (GameHelpers.TryReadSelectYesnoPrompt(out var visiblePrompt))
            {
                promptVisible = true;
                normalizedPrompt = NormalizeSelectYesnoPrompt(visiblePrompt);
            }
            else
            {
                promptVisible = GameHelpers.IsAddonVisible("SelectYesno");
            }
        }

        TryRefreshSelectYesnoPartySnapshot(loading);
        var party = _plugin.PartyService.PartyMembers;
        var loadedPartyCount = party.Count(member => member.IsLoaded);
        var sameTerritoryPartyCount = party.Count(member => member.IsInSameTerritory);
        var loadedSameTerritoryPartyCount = party.Count(member => member.IsLoaded && member.IsInSameTerritory);
        var playerAvailable = !loading && Plugin.ClientState.IsLoggedIn && Plugin.ObjectTable.LocalPlayer != null;
        var sanctuary = playerAvailable ? FormatSelectYesnoBool(GameHelpers.IsInSanctuary()) : "unavailable";
        var lowestDurability = "unavailable";
        if (!loading &&
            Plugin.ClientState.IsLoggedIn &&
            _plugin.InventoryService.TryGetLowestEquippedGearConditionPercent(out var lowestCondition))
        {
            lowestDurability = lowestCondition.ToString();
        }

        return new SelectYesnoDiagnosticSnapshot(
            DateTime.Now,
            promptVisible,
            normalizedPrompt,
            Plugin.ClientState.TerritoryType,
            loading,
            Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56],
            Plugin.Condition[ConditionFlag.InCombat],
            sanctuary,
            party.Count,
            loadedPartyCount,
            sameTerritoryPartyCount,
            loadedSameTerritoryPartyCount,
            State,
            StateDetail,
            BuildSelectYesnoMapName(),
            SelectedMapItemId,
            BuildSelectYesnoCurrentLocation(),
            BuildSelectYesnoActiveTarget(loading),
            BuildDiagnosticRepairPhase(),
            BuildSelectYesnoRepairMode(),
            BuildSelectYesnoRepairSource(),
            lowestDurability,
            Math.Clamp(_plugin.Configuration.RepairThresholdPercent, 0, 100));
    }

    private void TryRefreshSelectYesnoPartySnapshot(bool loading)
    {
        if (loading || !Plugin.ClientState.IsLoggedIn)
            return;

        try
        {
            _plugin.PartyService.UpdatePartyStatus();
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[SelectYesno] Party snapshot refresh failed: {ex.Message}");
        }
    }

    private void LogSelectYesnoObserved(string prompt, string source, SelectYesnoDiagnosticSnapshot snapshot)
    {
        _plugin.AddDebugLog(
            $"[SelectYesno] observed prompt='{EscapeSelectYesnoDiagnostic(NormalizeSelectYesnoPrompt(prompt))}' source={source} {BuildSelectYesnoDiagnosticContext(snapshot)}");
    }

    private void LogSelectYesnoAccepted(string prompt, string source, SelectYesnoDiagnosticSnapshot snapshot)
    {
        _plugin.AddDebugLog(
            $"[SelectYesno] accepted prompt='{EscapeSelectYesnoDiagnostic(NormalizeSelectYesnoPrompt(prompt))}' source={source} result=callback-sent {BuildSelectYesnoDiagnosticContext(snapshot)} recent='{EscapeSelectYesnoDiagnostic(LootGoblinActionTrace.FormatRecent())}'");
    }

    private void QueueSelectYesnoAfterDiagnostic(string prompt, string source, SelectYesnoDiagnosticSnapshot beforeSnapshot)
    {
        pendingSelectYesnoAfterDiagnostics.Enqueue(new PendingSelectYesnoAfterDiagnostic(
            NormalizeSelectYesnoPrompt(prompt),
            source,
            beforeSnapshot,
            DateTime.Now + SelectYesnoAfterDiagnosticDelay));

        while (pendingSelectYesnoAfterDiagnostics.Count > 8)
            pendingSelectYesnoAfterDiagnostics.Dequeue();
    }

    private void LogSelectYesnoAfter(
        string prompt,
        string source,
        SelectYesnoDiagnosticSnapshot before,
        SelectYesnoDiagnosticSnapshot after)
    {
        _plugin.AddDebugLog(
            $"[SelectYesno] after prompt='{EscapeSelectYesnoDiagnostic(NormalizeSelectYesnoPrompt(prompt))}' source={source} " +
            $"party.total={before.PartyTotal}->{after.PartyTotal} party.loaded={before.PartyLoaded}->{after.PartyLoaded} " +
            $"party.sameTerritory={before.PartySameTerritory}->{after.PartySameTerritory} territory={before.Territory}->{after.Territory} " +
            $"duty={FormatSelectYesnoBool(before.Duty)}->{FormatSelectYesnoBool(after.Duty)} loading={FormatSelectYesnoBool(before.Loading)}->{FormatSelectYesnoBool(after.Loading)} " +
            $"visible={FormatSelectYesnoNullableBool(before.PromptVisible)}->{FormatSelectYesnoNullableBool(after.PromptVisible)} afterPrompt='{EscapeSelectYesnoDiagnostic(after.Prompt)}'");
    }

    private string BuildSelectYesnoDiagnosticContext(SelectYesnoDiagnosticSnapshot snapshot)
        => $"state={snapshot.BotState} detail='{EscapeSelectYesnoDiagnostic(snapshot.Detail)}' " +
           $"party.total={snapshot.PartyTotal} party.loaded={snapshot.PartyLoaded} party.sameTerritory={snapshot.PartySameTerritory} party.loadedSameTerritory={snapshot.PartyLoadedSameTerritory} " +
           $"territory={snapshot.Territory} duty={FormatSelectYesnoBool(snapshot.Duty)} combat={FormatSelectYesnoBool(snapshot.Combat)} loading={FormatSelectYesnoBool(snapshot.Loading)} sanctuary={snapshot.Sanctuary} " +
           $"map='{EscapeSelectYesnoDiagnostic(snapshot.MapSelected)}' selectedMapId={snapshot.MapSelectedId} target='{EscapeSelectYesnoDiagnostic(snapshot.ActiveTarget)}' location='{EscapeSelectYesnoDiagnostic(snapshot.CurrentLocation)}' " +
           $"repair.phase={snapshot.RepairPhase} repair.mode='{EscapeSelectYesnoDiagnostic(snapshot.RepairMode)}' repair.source='{EscapeSelectYesnoDiagnostic(snapshot.RepairSource)}' " +
           $"repair.lowestDurabilityPercent={snapshot.LowestDurabilityPercent} repair.thresholdPercent={snapshot.RepairThresholdPercent}";

    private string BuildSelectYesnoMapName()
        => SelectedMapItemId != 0 &&
           TreasureMapData.KnownMaps.TryGetValue(SelectedMapItemId, out var mapInfo)
            ? mapInfo.Name
            : SelectedMapItemId == 0
                ? "none"
                : $"ID {SelectedMapItemId}";

    private string BuildSelectYesnoCurrentLocation()
        => CurrentLocation == null
            ? "none"
            : $"{CurrentLocation.ZoneName}[territory={CurrentLocation.TerritoryId},xyz={FormatVectorCompact(new Vector3(CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z))},resolved={CurrentLocation.IsResolved}]";

    private static string BuildSelectYesnoActiveTarget(bool loading)
    {
        if (loading)
            return "unavailable-loading";

        var target = Plugin.TargetManager.Target;
        return target == null
            ? "none"
            : $"{target.Name}[kind={target.ObjectKind},entity={target.EntityId},xyz={FormatVectorCompact(target.Position)},targetable={target.IsTargetable}]";
    }

    private string BuildSelectYesnoRepairMode()
    {
        if (!string.IsNullOrWhiteSpace(adsRepairRecoveryMode))
            return adsRepairRecoveryMode;
        if (!string.IsNullOrWhiteSpace(adsRepairRequestedMode))
            return adsRepairRequestedMode;
        return ResolveAdsRepairMode();
    }

    private string BuildSelectYesnoRepairSource()
    {
        if (!string.IsNullOrWhiteSpace(adsRepairRecoverySource))
            return adsRepairRecoverySource;
        return adsRepairSource;
    }

    private static string NormalizeSelectYesnoPrompt(string prompt)
        => string.IsNullOrWhiteSpace(prompt) ? "<unreadable>" : prompt.Trim();

    private static string EscapeSelectYesnoDiagnostic(string value)
    {
        var escaped = (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

        return escaped.Length <= 360 ? escaped : escaped[..360] + "...";
    }

    private static string FormatSelectYesnoBool(bool value)
        => value ? "true" : "false";

    private static string FormatSelectYesnoNullableBool(bool? value)
        => value.HasValue ? FormatSelectYesnoBool(value.Value) : "unknown";

    private void ObservePartyTeleportOffer(string text)
    {
        if (acceptedPartyTeleportOfferRestartPending)
            return;

        if (!IsPartyTeleportOfferMessage(text))
            return;

        pendingPartyTeleportOfferObservedAt = DateTime.Now;
        pendingPartyTeleportOfferText = text.Trim();
        _plugin.AddDebugLog("[PartyTeleport] Offer observed; next accepted SelectYesno will restart LootGoblin after teleport settles.");
    }

    private static bool IsPartyTeleportOfferMessage(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith("You have been offered a Teleport to ", StringComparison.OrdinalIgnoreCase) &&
               trimmed.Contains(" from ", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsPendingPartyTeleportOfferActive(DateTime now)
    {
        if (pendingPartyTeleportOfferObservedAt == DateTime.MinValue)
            return false;

        if (now - pendingPartyTeleportOfferObservedAt <= PartyTeleportOfferPendingTtl)
            return true;

        ClearPendingPartyTeleportOffer("[PartyTeleport] Offer expired before SelectYesno was accepted.");
        return false;
    }

    private void QueueAcceptedPartyTeleportOfferRestart(DateTime acceptedAt)
    {
        if (acceptedPartyTeleportOfferRestartPending)
            return;

        var offerText = pendingPartyTeleportOfferText;
        ClearPendingPartyTeleportOffer(null);

        acceptedPartyTeleportOfferRestartPending = true;
        acceptedPartyTeleportOfferAt = acceptedAt;
        acceptedPartyTeleportOfferSawBetweenAreas = false;
        acceptedPartyTeleportOfferLastLoadingAt = DateTime.MinValue;

        StopMovementForAcceptedPartyTeleportOffer();
        _plugin.AddDebugLog(
            string.IsNullOrWhiteSpace(offerText)
                ? "[PartyTeleport] Accepted party teleport offer; queued LootGoblin fresh start after teleport settles."
                : $"[PartyTeleport] Accepted party teleport offer; queued LootGoblin fresh start after teleport settles. Offer='{offerText}'");
    }

    private bool TickAcceptedPartyTeleportOfferRestart()
    {
        if (!acceptedPartyTeleportOfferRestartPending)
            return false;

        if (!_plugin.Configuration.Enabled)
        {
            ClearAcceptedPartyTeleportOfferRestart("[PartyTeleport] Restart cancelled because LootGoblin is disabled.");
            return false;
        }

        var now = DateTime.Now;
        var loading = IsAreaTransitionActive() || _plugin.NavigationService.IsTeleporting();
        if (loading)
        {
            acceptedPartyTeleportOfferSawBetweenAreas = true;
            acceptedPartyTeleportOfferLastLoadingAt = now;
            StateDetail = "Accepted party teleport - waiting for loading to finish...";
            return true;
        }

        if (acceptedPartyTeleportOfferSawBetweenAreas &&
            now - acceptedPartyTeleportOfferLastLoadingAt < PartyTeleportPostLoadingSettleDelay)
        {
            StateDetail = "Accepted party teleport - waiting for arrival to settle...";
            return true;
        }

        if (!acceptedPartyTeleportOfferSawBetweenAreas &&
            now - acceptedPartyTeleportOfferAt < PartyTeleportAcceptedSettleDelay)
        {
            StateDetail = "Accepted party teleport - waiting for teleport handoff...";
            return true;
        }

        if (!GameHelpers.IsPlayerAvailable())
        {
            StateDetail = "Accepted party teleport - waiting for player to become available...";
            return true;
        }

        RestartAfterAcceptedPartyTeleportOffer();
        return true;
    }

    private void RestartAfterAcceptedPartyTeleportOffer()
    {
        ClearAcceptedPartyTeleportOfferRestart(null);
        PrepareFreshStartAfterAcceptedPartyTeleportOffer();
        TransitionTo(BotState.Idle, "Restarting after accepted party teleport offer...");
        _plugin.AddDebugLog("[PartyTeleport] Teleport settled; restarting LootGoblin through Start flow.");
        Start();
    }

    private void PrepareFreshStartAfterAcceptedPartyTeleportOffer()
    {
        StopMovementForAcceptedPartyTeleportOffer();
        ResetVnavFlyFlagFallbackState();
        ResetPortalApproachTrackingForAreaChange();
        EndPortalRetryWindow();
        ResetAdsHandoffTracking(resetStatus: true);
        ResetAdsRepairHandoffTracking();
        _plugin.RetainerMapRetrievalService.Reset();
        ClearSelectedMapRunCountDecrement("[PartyTeleport]");
        ResetSaddlebagRetrieval();
        ResetStartMapRefresh();
        ResetOpeningChestLifecycleState();
        ResetOpeningChestCofferMemory();
        ResetPortalGroundApproachTracking(resetFailure: true);
        ResetOverworldRecoveryState(clearTeleportedTarget: true);
        ResetKeyItemMapRecoveryState(clearActiveKey: true);
        _plugin.TreasureMapLocationService.ClearCapturedLocation();

        IsPaused = false;
        RetryCount = 0;
        CurrentLocation = null;
        SelectedMapItemId = 0;
        currentLandingMode = OverworldLandingMode.MountToggle;
        pendingDungeonMapFlagClear = false;
        completedSaddlebagRefreshAttempted = false;
        ResetRunCommandTriggers();
        ClearWarning();
    }

    private void StopMovementForAcceptedPartyTeleportOffer()
    {
        ResetPortaPraetoriaTakeoffNudge("[PartyTeleport] movement stop", stopAutomove: true);
        CommandHelper.SendCommand("/automove off");
        autoMoveActive = false;
        descentMode = false;
        if (descentInProgress)
        {
            GameHelpers.KeyRelease(VirtualKey.W);
            GameHelpers.KeyRelease(VirtualKey.CONTROL);
            GameHelpers.KeyRelease(VirtualKey.SPACE);
        }

        descentInProgress = false;
        underwaterTargetPosition = Vector3.Zero;
        ResetPendingUnderwaterFlagApproachReissue();
        if (_plugin.NavigationService.State != NavigationState.Idle)
            _plugin.NavigationService.StopNavigation();
    }

    private void ClearPendingPartyTeleportOffer(string? logMessage)
    {
        pendingPartyTeleportOfferObservedAt = DateTime.MinValue;
        pendingPartyTeleportOfferText = string.Empty;

        if (!string.IsNullOrWhiteSpace(logMessage))
            _plugin.AddDebugLog(logMessage);
    }

    private void ClearAcceptedPartyTeleportOfferRestart(string? logMessage)
    {
        acceptedPartyTeleportOfferRestartPending = false;
        acceptedPartyTeleportOfferAt = DateTime.MinValue;
        acceptedPartyTeleportOfferSawBetweenAreas = false;
        acceptedPartyTeleportOfferLastLoadingAt = DateTime.MinValue;

        if (!string.IsNullOrWhiteSpace(logMessage))
            _plugin.AddDebugLog(logMessage);
    }

    private void HandleBetweenAreasTick()
    {
        if (acceptedPartyTeleportOfferRestartPending)
        {
            acceptedPartyTeleportOfferSawBetweenAreas = true;
            acceptedPartyTeleportOfferLastLoadingAt = DateTime.Now;
        }

        if (adsRepairRecoveryActive)
        {
            adsRepairRecoverySawBetweenAreas = true;
            adsRepairRecoveryLastLoadingAt = DateTime.Now;
        }

        if (State == BotState.Teleporting)
        {
            teleportSawBetweenAreas = true;
            teleportLastLoadingAt = DateTime.Now;
            teleportLoadingClearedAt = DateTime.MinValue;
        }

        if (State == BotState.AlexandriteFarming && alexandriteStep == 0 && alexandriteActionIssued)
        {
            alexandriteSawBetweenAreas = true;
            alexandriteLastLoadingAt = DateTime.Now;
            alexandriteLoadingClearedAt = DateTime.MinValue;
        }

        var portaPraetoriaNudgeHadMovement = portaPraetoriaTakeoffNudgeActive;
        ResetPortalApproachTrackingForAreaChange();
        ResetAllCameraResetBeforeInteractTracking();
        stateStartTime = DateTime.Now; // Don't timeout while loading

        if (betweenAreasMovementStopped)
            return;

        var hadMovement = autoMoveActive
            || portaPraetoriaNudgeHadMovement
            || descentInProgress
            || descentMode
            || underwaterTargetPosition != Vector3.Zero
            || HasPendingUnderwaterFlagApproachReissue()
            || _plugin.NavigationService.State != NavigationState.Idle;

        if (descentInProgress || descentMode)
        {
            GameHelpers.KeyRelease(VirtualKey.W);
            GameHelpers.KeyRelease(VirtualKey.CONTROL);
            GameHelpers.KeyRelease(VirtualKey.SPACE);
        }

        if (_plugin.NavigationService.State != NavigationState.Idle)
            _plugin.NavigationService.StopNavigation();

        CommandHelper.SendCommand("/automove off");
        ResetPortaPraetoriaTakeoffNudge("[BetweenAreas] area load", stopAutomove: false);
        autoMoveActive = false;
        descentInProgress = false;
        descentMode = false;
        dismountAttemptStart = DateTime.MinValue;
        underwaterTargetPosition = Vector3.Zero;
        underwaterFlagApproachIssued = false;
        underwaterFlagApproachLogged = false;
        lastUnderwaterFlagApproachTime = DateTime.MinValue;
        ResetUnderwaterFlagApproachProgressState();
        ResetPendingUnderwaterFlagApproachReissue();
        underwaterFlagApproachSurfacedFallbackActive = false;
        ResetUnderwaterBounceSpecialNavigationState();
        ResetUnderwaterXyzDigRetryState();
        betweenAreasMovementStopped = true;

        if (hadMovement)
            _plugin.AddDebugLog("[BetweenAreas] Stopped LootGoblin movement/descent during load; preserving map flags.");
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

        var startMapFlagCleared = GameHelpers.ClearMapFlag(_plugin.MapFlagService.TryReadFlag);
        _plugin.AddDebugLog($"[Start] Preflight cleared map flag before start flow: verified={startMapFlagCleared}.");

        var mounted = Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.Mounting71];
        var startMountCommandSent = mounted && CommandHelper.TrySendCommand("/mount");
        _plugin.AddDebugLog($"[Start] Preflight dismount attempt: mounted={mounted}, sent={startMountCommandSent}.");

        ResetConfiguredCombatJobSwitch();
        startPreflightReadyAt = DateTime.Now + StartPreflightDelay;
        TransitionTo(BotState.StartPreflight, "Start preflight: cleared map flag, attempted dismount, waiting 1s...");
    }

    private void TickStartPreflight()
    {
        if (startPreflightReadyAt == DateTime.MinValue)
            startPreflightReadyAt = DateTime.Now;

        var remaining = startPreflightReadyAt - DateTime.Now;
        if (remaining > TimeSpan.Zero)
        {
            StateDetail = $"Start preflight: waiting {remaining.TotalSeconds:0.0}s before start flow...";
            return;
        }

        if (!TryPreparePendingAlexandriteStartPreflight())
            return;

        startPreflightReadyAt = DateTime.MinValue;
        ContinueStartAfterPreflight();
    }

    private bool TryPreparePendingAlexandriteStartPreflight()
    {
        if (!AlexandritePolicy.ShouldBypassStartMapRefresh(pendingAlexandriteMapTargetItemId))
            return true;

        var visibleAddons = GetVisibleAlexandritePurchaseAddons();
        if (visibleAddons.Count > 0)
        {
            foreach (var addonName in visibleAddons)
                GameHelpers.TryCloseAddonByCallback(addonName);

            stateStartTime = DateTime.Now;
            StateDetail = $"Alexandrite: closing purchase UI ({string.Join(", ", visibleAddons)})...";
            return false;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null ||
            player.IsCasting ||
            Plugin.Condition[ConditionFlag.Casting] ||
            !GameHelpers.IsPlayerAvailable())
        {
            stateStartTime = DateTime.Now;
            StateDetail = "Alexandrite: waiting for player readiness before opening Mysterious Map...";
            return false;
        }

        lastMapScanTime = DateTime.MinValue;
        return true;
    }

    private void ContinueStartAfterPreflight()
    {
        ClearWarning();
        ResetRunCommandTriggers();
        failedGatherMapIdsThisRun.Clear();
        ClearSelectedMapRunCountDecrement("[Start]");
        ResetSaddlebagRetrieval();
        ResetMapGathering(cancelGatherBuddy: true);
        ResetStartMapRefresh();
        ResetOpeningChestCofferApproachTracking();
        ResetOpeningChestCofferWalkFailure();
        ResetPortalGroundApproachTracking(resetFailure: true);
        _plugin.TreasureMapLocationService.ClearCapturedLocation();

        CommandHelper.TrySendCommand("/xldisableplugin AutoDuty");
        CommandHelper.TrySendCommand("/xldisableplugin ReAction");
        _plugin.AddDebugLog("[Start] Sent AutoDuty/ReAction disable commands.");

        _plugin.RefreshDependencyStatus(logStatus: true);

        // Check if already in duty; only known treasure dungeon territories enter dungeon handling.
        bool inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                      Plugin.Condition[ConditionFlag.BoundByDuty56];
        
        if (inDuty)
        {
            var currentTerritory = Plugin.ClientState.TerritoryType;
            var isTreasureDungeonTerritory = IsTreasureDungeonTerritory(currentTerritory);
            if (!isTreasureDungeonTerritory)
            {
                LogMapDutyOutsideDungeon("[Start]", currentTerritory);
                RetryCount = 0;
                CurrentLocation = null;
                SelectedMapItemId = 0;
                currentLandingMode = OverworldLandingMode.MountToggle;
                _plugin.YesAlreadyIPC.Pause();
                _plugin.AddDebugLog($"[Start] YesAlready paused: {_plugin.YesAlreadyIPC.IsPaused}");
                TransitionTo(BotState.OpeningChest, "Map duty active outside treasure dungeon - recovering coffer/portal...");
                return;
            }

            if (_plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver
                && _plugin.IsAdsAvailable)
            {
                _plugin.AddDebugLog($"[Start][ADS] Already in treasure dungeon territory {currentTerritory} - handing duty to ADS.");
                RetryCount = 0;
                CurrentLocation = null;
                SelectedMapItemId = 0;
                currentLandingMode = OverworldLandingMode.MountToggle;
                _plugin.YesAlreadyIPC.Pause();
                _plugin.AddDebugLog($"[Start][ADS] YesAlready paused: {_plugin.YesAlreadyIPC.IsPaused}");

                EndPortalRetryWindow();
                ResetAdsHandoffTracking(resetStatus: true);
                adsDutyHandoffActive = true;
                adsDutyHandoffStarted = DateTime.Now;
                SendAdsInsideCommand("[Start][ADS] Sent /ads inside for already-active treasure dungeon.", includeAssistCommands: true);
                TransitionTo(BotState.Completed, "ADS handoff active - waiting for dungeon to finish...");
                return;
            }

            if (_plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver && !_plugin.IsAdsAvailable)
            {
                _plugin.ShowAdsMissingToast();
                _plugin.AddDebugLog("[Start][ADS] ADS handoff requested from duty start, but ADS is not installed/loaded. Falling back to legacy dungeon solver.");
            }

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

        if (TryStartAdsRepairIfNeeded("[Start]", resumeStartAfterRepair: true))
        {
            if (adsRepairHandoffActive || adsRepairRecoveryActive || adsRepairRetryPending)
            {
                continueStartAfterAdsRepair = true;
                TransitionTo(BotState.Repairing, "Repairing gear before starting map run...");
            }

            return;
        }

        ContinueStartAfterRepair();
    }

    private void ContinueStartAfterRepair()
    {
        if (!EnsureConfiguredCombatJob())
            return;

        if (TryRecoverActiveKeyItemMap("[Start]", transitionToDetectingOnActive: true))
            return;

        if (_plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver && !_plugin.IsAdsAvailable)
        {
            _plugin.ShowAdsMissingToast();
        }

        if (AlexandritePolicy.ShouldBypassStartMapRefresh(pendingAlexandriteMapTargetItemId))
        {
            ResetStartMapRefresh();
            lastMapScanTime = DateTime.MinValue;
            TransitionTo(BotState.SelectingMap, "Alexandrite: selecting purchased Mysterious Map...");
            return;
        }

        BeginStartMapRefresh();
        TransitionTo(BotState.SelectingMap, "Starting map run - refreshing saddlebag maps...");
    }

    private bool EnsureConfiguredCombatJob()
    {
        var targetJobId = _plugin.Configuration.SelectedCombatJobId;
        if (targetJobId == 0)
        {
            ResetConfiguredCombatJobSwitch();
            return true;
        }

        var currentJobId = _plugin.JobSwitchService.GetCurrentClassJobId();
        if (currentJobId == targetJobId)
        {
            ResetConfiguredCombatJobSwitch();
            return true;
        }

        if (Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.Mounting71])
        {
            if (DateTime.Now - startCombatJobLastDismountAttemptAt > TimeSpan.FromSeconds(1))
            {
                startCombatJobLastDismountAttemptAt = DateTime.Now;
                CommandHelper.TrySendCommand("/mount");
            }

            stateStartTime = DateTime.Now;
            StateDetail = $"Dismounting before combat job switch to {LootGoblin.Models.ClassJobOptions.GetName(targetJobId)}...";
            return false;
        }

        if (!CanRunMapGatherAction(out var readyReason))
        {
            stateStartTime = DateTime.Now;
            StateDetail = $"Waiting to switch to combat job: {readyReason}";
            return false;
        }

        if (!startCombatJobSwitchIssued)
        {
            if (!_plugin.JobSwitchService.TrySwitchToJob(targetJobId, out var detail))
            {
                SetWarning(detail);
                TransitionTo(BotState.Error, $"Could not switch to configured combat job: {detail}");
                return false;
            }

            startCombatJobSwitchIssued = true;
            startCombatJobSwitchStartedAt = DateTime.Now;
            stateStartTime = DateTime.Now;
            StateDetail = detail;
            return false;
        }

        if (DateTime.Now - startCombatJobSwitchStartedAt > TimeSpan.FromSeconds(10))
        {
            var detail = $"Timed out switching to configured combat job {LootGoblin.Models.ClassJobOptions.GetName(targetJobId)}.";
            SetWarning(detail);
            TransitionTo(BotState.Error, detail);
            return false;
        }

        stateStartTime = DateTime.Now;
        StateDetail = $"Waiting for combat job switch to {LootGoblin.Models.ClassJobOptions.GetName(targetJobId)}...";
        return false;
    }

    private void ResetConfiguredCombatJobSwitch()
    {
        startCombatJobSwitchIssued = false;
        startCombatJobSwitchStartedAt = DateTime.MinValue;
        startCombatJobLastDismountAttemptAt = DateTime.MinValue;
    }

    public void Stop([CallerMemberName] string source = "unattributed")
    {
        WritePreTerminalSnapshot($"stop:{source}");
        ClearPendingPartyTeleportOffer(null);
        ClearAcceptedPartyTeleportOfferRestart(null);
        startPreflightReadyAt = DateTime.MinValue;
        _plugin.NavigationService.StopNavigation(clearFlag: true);
        ResetVnavFlyFlagFallbackState();
        SetCombatAutomationForCombatState(inCombat: false, "bot stop", force: true);
        ClearBossModOutdoorSuppressionState("bot stop");
        IsPaused = false;
        RetryCount = 0;
        EndPortalRetryWindow();
        dungeonEntryProcessed = false;
        ResetAdsHandoffTracking(resetStatus: true);
        ResetAdsRepairHandoffTracking();
        _plugin.RetainerMapRetrievalService.Reset();
        ResetConfiguredCombatJobSwitch();
        ResetMapGathering(cancelGatherBuddy: true);
        ClearSelectedMapRunCountDecrement("[Stop]");
        ResetSaddlebagRetrieval();
        ResetStartMapRefresh();
        currentLandingMode = OverworldLandingMode.MountToggle;
        ResetRunCommandTriggers();
        ResetKeyItemMapRecoveryState(clearActiveKey: true);
        ResetAlexandriteSessionState("[Stop]");
        ClearOutdoorMapFlowHold();
        ClearWarning();
        TransitionTo(BotState.Idle, $"Stopped by user ({source}).");
    }

    public void ResetAll([CallerMemberName] string source = "unattributed")
    {
        WritePreTerminalSnapshot($"reset-all:{source}");
        ClearPendingPartyTeleportOffer(null);
        ClearAcceptedPartyTeleportOfferRestart(null);
        startPreflightReadyAt = DateTime.MinValue;
        _plugin.NavigationService.StopNavigation(clearFlag: true);
        ResetVnavFlyFlagFallbackState();
        IsPaused = false;
        RetryCount = 0;
        CurrentLocation = null;
        SelectedMapItemId = 0;
        EndPortalRetryWindow();
        ResetAdsHandoffTracking(resetStatus: true);
        ResetAdsRepairHandoffTracking();
        _plugin.RetainerMapRetrievalService.Reset();
        ResetConfiguredCombatJobSwitch();
        ResetMapGathering(cancelGatherBuddy: true);
        failedGatherMapIdsThisRun.Clear();
        ClearSelectedMapRunCountDecrement("[ResetAll]");
        ResetSaddlebagRetrieval();
        ResetStartMapRefresh();
        currentLandingMode = OverworldLandingMode.MountToggle;
        ResetRunCommandTriggers();
        ResetKeyItemMapRecoveryState(clearActiveKey: true);
        ResetAlexandriteSessionState("[ResetAll]");
        SetCombatAutomationForCombatState(inCombat: false, "full reset", force: true);
        ClearBossModOutdoorSuppressionState("full reset");
        KrangleService.ClearCache();
        ClearOutdoorMapFlowHold();
        ClearWarning();
        TransitionTo(BotState.Idle, $"Full reset by user ({source}).");
        _plugin.AddDebugLog("All plugin states reset.");
    }

    public void Pause([CallerMemberName] string source = "unattributed")
    {
        if (State == BotState.Idle || State == BotState.Error) return;
        WritePreTerminalSnapshot($"pause:{source}");
        IsPaused = true;
        _plugin.NavigationService.StopNavigation();
        _plugin.AddDebugLog($"Bot paused; source={source}.");
    }

    public void Resume([CallerMemberName] string source = "unattributed")
    {
        if (!IsPaused) return;
        IsPaused = false;
        ResetDiagnosticTerminalTracking();
        stateActionIssued = false;
        _plugin.AddDebugLog($"Bot resumed; source={source}.");
        WriteDiagnosticSnapshot($"resume:{source}");
    }

    public void WritePreTerminalSnapshot(string source)
    {
        if (!_plugin.DedicatedDiagnosticLog.IsEnabled)
            return;

        var transitionAfterCleanup =
            source.StartsWith("transition:", StringComparison.Ordinal) &&
            (lastPreTerminalSnapshotSource.StartsWith("stop:", StringComparison.Ordinal) ||
             lastPreTerminalSnapshotSource.StartsWith("reset-all:", StringComparison.Ordinal) ||
             lastPreTerminalSnapshotSource.StartsWith("enabled-disable:", StringComparison.Ordinal) ||
             lastPreTerminalSnapshotSource.StartsWith("transition:", StringComparison.Ordinal));
        var stopAfterDisable =
            source.StartsWith("stop:", StringComparison.Ordinal) &&
            lastPreTerminalSnapshotSource.StartsWith("enabled-disable:", StringComparison.Ordinal);
        if (preTerminalSnapshotWritten &&
            (string.Equals(source, lastPreTerminalSnapshotSource, StringComparison.Ordinal) ||
             transitionAfterCleanup ||
             stopAfterDisable))
        {
            return;
        }

        preTerminalSnapshotWritten = true;
        lastPreTerminalSnapshotSource = source;
        diagnosticSnapshotPolicy.Reset();
        WriteDiagnosticSnapshot($"pre-terminal:{source}");
        _plugin.DedicatedDiagnosticLog.Flush();
    }

    public void ResetDiagnosticTerminalTracking()
    {
        preTerminalSnapshotWritten = false;
        lastPreTerminalSnapshotSource = string.Empty;
        diagnosticSnapshotPolicy.Reset();
    }

    public void WriteDiagnosticSnapshot(string source)
    {
        if (!_plugin.DedicatedDiagnosticLog.IsEnabled)
            return;

        try
        {
            var snapshot = BuildDiagnosticSnapshot(source);
            _plugin.DedicatedDiagnosticLog.WriteCritical("SNAPSHOT", snapshot.Format());
            UpdateDiagnosticSignatureBaselines();
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[Diagnostics] Snapshot write failed for '{source}': {ex.Message}");
        }
    }

    private void UpdateDiagnosticSnapshots()
    {
        if (!_plugin.DedicatedDiagnosticLog.IsEnabled)
            return;

        var activelyRunning =
            _plugin.Configuration.Enabled &&
            !IsPaused &&
            State is not BotState.Idle and not BotState.Error;
        if (diagnosticSnapshotPolicy.ShouldWritePeriodic(DateTime.UtcNow, activelyRunning))
            WriteDiagnosticSnapshot("periodic-60s");

        CaptureDiagnosticChange(
            ref lastDiagnosticTerritorySignature,
            BuildDiagnosticTerritorySignature(),
            "territory-change");
        CaptureDiagnosticChange(
            ref lastDiagnosticRepairSignature,
            BuildDiagnosticRepairSignature(),
            "repair-phase-change");
        CaptureDiagnosticChange(
            ref lastDiagnosticPartyBlockerSignature,
            BuildDiagnosticPartyBlockerSignature(),
            "party-blocker-change");
        CaptureDiagnosticChange(
            ref lastDiagnosticAdsOwnershipSignature,
            BuildDiagnosticAdsOwnershipSignature(),
            "ads-ownership-change");
    }

    private void CaptureDiagnosticChange(ref string previous, string current, string source)
    {
        if (string.IsNullOrEmpty(previous))
        {
            previous = current;
            return;
        }

        if (string.Equals(previous, current, StringComparison.Ordinal))
            return;

        previous = current;
        WriteDiagnosticSnapshot(source);
    }

    private DiagnosticSnapshot BuildDiagnosticSnapshot(string source)
    {
        var now = DateTime.Now;
        var nowUtc = DateTime.UtcNow;
        var loading = IsAreaTransitionActive();
        var player = Plugin.ObjectTable.LocalPlayer;
        var playerPosition = player?.Position ?? Vector3.Zero;
        var condition = Plugin.Condition;
        var ads = _plugin.AdsStatusService.Current;
        var adsAvailable = _plugin.IsAdsAvailable;
        var nav = _plugin.NavigationService;
        var vnav = _plugin.VNavIPC;
        var party = _plugin.PartyService.PartyMembers;
        var target = Plugin.TargetManager.Target;

        var lowestDurability = "unavailable";
        if (Plugin.ClientState.IsLoggedIn && !loading &&
            _plugin.InventoryService.TryGetLowestEquippedGearConditionPercent(out var lowestCondition))
        {
            lowestDurability = lowestCondition.ToString();
        }

        var loadedPartyCount = party.Count(member => member.IsLoaded);
        var sameTerritoryPartyCount = party.Count(member => member.IsInSameTerritory);
        var loadedSameTerritoryPartyCount = party.Count(member => member.IsLoaded && member.IsInSameTerritory);
        var blockingPartyMembers = party
            .Where(member =>
                !member.IsLocalPlayer &&
                (!member.IsLoaded || !member.IsInSameTerritory || !member.IsMounted))
            .Select(member =>
                $"{member.Name}[loaded={member.IsLoaded},territory={member.TerritoryStatus},mounted={member.IsMounted}]")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();

        var mapName = SelectedMapItemId != 0 &&
                      TreasureMapData.KnownMaps.TryGetValue(SelectedMapItemId, out var mapInfo)
            ? mapInfo.Name
            : SelectedMapItemId == 0
                ? "none"
                : $"ID {SelectedMapItemId}";
        var currentLocation = CurrentLocation == null
            ? "none"
            : $"{CurrentLocation.ZoneName}[territory={CurrentLocation.TerritoryId},xyz={FormatVectorCompact(new Vector3(CurrentLocation.X, CurrentLocation.Y, CurrentLocation.Z))},resolved={CurrentLocation.IsResolved}]";
        var activeTarget = target == null
            ? "none"
            : $"{target.Name}[kind={target.ObjectKind},entity={target.EntityId},xyz={FormatVectorCompact(target.Position)},targetable={target.IsTargetable}]";

        var fields = new List<KeyValuePair<string, string>>();
        void Add(string key, object? value)
            => fields.Add(new KeyValuePair<string, string>(key, value?.ToString() ?? "null"));

        Add("control.enabled", _plugin.Configuration.Enabled);
        Add("control.paused", IsPaused);
        Add("control.transitionSource", lastTransitionSource);
        Add("state.name", State);
        Add("state.detail", StateDetail);
        Add("state.ageSeconds", Math.Max(0, (now - stateStartTime).TotalSeconds).ToString("F1"));
        Add("state.retryCount", RetryCount);
        Add("world.territory", Plugin.ClientState.TerritoryType);
        Add("world.playerPosition", player == null ? "unavailable" : FormatVectorCompact(playerPosition));
        Add("world.loggedIn", Plugin.ClientState.IsLoggedIn);
        Add("world.loading", loading);
        Add("world.combat", condition[ConditionFlag.InCombat]);
        Add("world.mounted", condition[ConditionFlag.Mounted] || condition[ConditionFlag.Mounting71]);
        Add("world.duty", condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56]);
        Add("world.sanctuary", player != null && !loading ? GameHelpers.IsInSanctuary() : "unavailable");
        Add("ads.available", adsAvailable);
        Add("ads.readable", ads.StatusReadable);
        Add("ads.ownership", ads.OwnershipMode);
        Add("ads.executionPhase", ads.ExecutionPhase);
        Add("ads.executionStatus", ads.ExecutionStatus);
        Add("ads.utilityRunning", ads.UtilityRunning);
        Add("ads.utilityTask", ads.UtilityTask);
        Add("ads.utilityMode", ads.UtilityMode);
        Add("ads.utilityStatus", ads.UtilityStatus);
        Add("ads.utilityLastSuccess", ads.UtilityLastSuccess);
        Add("ads.utilityLastFailure", ads.UtilityLastFailure);
        Add("ads.dutyHandoffActive", adsDutyHandoffActive);
        Add("ads.ownershipObserved", adsOwnershipObserved);
        Add("repair.phase", BuildDiagnosticRepairPhase());
        Add("repair.handoffActive", adsRepairHandoffActive);
        Add("repair.utilityObserved", adsRepairUtilityObserved);
        Add("repair.retryPending", adsRepairRetryPending);
        Add("repair.retryAttempts", adsRepairRetryAttemptCount);
        Add("repair.recoveryActive", adsRepairRecoveryActive);
        Add("repair.recoveryTeleportIssued", adsRepairRecoveryTeleportIssued);
        Add("repair.recoveryTeleportRetries", adsRepairRecoveryTeleportRetryCount);
        Add("repair.mode", string.IsNullOrWhiteSpace(adsRepairRequestedMode) ? ResolveAdsRepairMode() : adsRepairRequestedMode);
        Add("repair.source", adsRepairSource);
        Add("repair.elapsedSeconds", GetDiagnosticRepairElapsed(now));
        Add("repair.lowestDurabilityPercent", lowestDurability);
        Add("repair.thresholdPercent", Math.Clamp(_plugin.Configuration.RepairThresholdPercent, 0, 100));
        Add("navigation.state", nav.State);
        Add("navigation.detail", nav.StateDetail);
        Add("navigation.target", FormatVectorCompact(nav.TargetPosition));
        Add("vnav.available", vnav.IsAvailable);
        Add("vnav.ready", FormatDiagnosticNullableBool(vnav.TryIsNavReady()));
        Add("vnav.running", FormatDiagnosticNullableBool(vnav.TryIsRunning()));
        Add("vnav.pathRunning", FormatDiagnosticNullableBool(vnav.TryIsPathRunning()));
        Add("vnav.pathfinding", FormatDiagnosticNullableBool(vnav.TryIsPathfindInProgress()));
        Add("party.state", _plugin.PartyService.State);
        Add("party.detail", _plugin.PartyService.StateDetail);
        Add("party.total", party.Count);
        Add("party.loaded", loadedPartyCount);
        Add("party.sameTerritory", sameTerritoryPartyCount);
        Add("party.loadedSameTerritory", loadedSameTerritoryPartyCount);
        Add("party.expected", waitingForPartyExpectedMemberCount);
        Add("party.blockers", blockingPartyMembers.Count == 0 ? "none" : string.Join(", ", blockingPartyMembers));
        Add("party.proximityGate", partyProximityGateSignature);
        Add("map.selected", mapName);
        Add("map.selectedId", SelectedMapItemId);
        Add("map.activeTarget", activeTarget);
        Add("map.currentLocation", currentLocation);
        Add("warning", WarningMessage);

        return new DiagnosticSnapshot(nowUtc, source, fields);
    }

    private void UpdateDiagnosticSignatureBaselines()
    {
        lastDiagnosticTerritorySignature = BuildDiagnosticTerritorySignature();
        lastDiagnosticRepairSignature = BuildDiagnosticRepairSignature();
        lastDiagnosticPartyBlockerSignature = BuildDiagnosticPartyBlockerSignature();
        lastDiagnosticAdsOwnershipSignature = BuildDiagnosticAdsOwnershipSignature();
    }

    private string BuildDiagnosticTerritorySignature()
        => $"{Plugin.ClientState.TerritoryType}|loading={IsAreaTransitionActive()}";

    private string BuildDiagnosticRepairSignature()
        => $"{BuildDiagnosticRepairPhase()}|handoff={adsRepairHandoffActive}|retry={adsRepairRetryPending}:{adsRepairRetryAttemptCount}|recovery={adsRepairRecoveryActive}:{adsRepairRecoveryTeleportIssued}:{adsRepairRecoveryTeleportRetryCount}|mode={adsRepairRequestedMode}";

    private string BuildDiagnosticPartyBlockerSignature()
    {
        var blockers = _plugin.PartyService.PartyMembers
            .Where(member =>
                !member.IsLocalPlayer &&
                (!member.IsLoaded || !member.IsInSameTerritory || !member.IsMounted))
            .Select(member => $"{member.Name}:{member.IsLoaded}:{member.TerritoryStatus}:{member.IsMounted}")
            .OrderBy(value => value, StringComparer.Ordinal);
        return $"{_plugin.PartyService.PartyMembers.Count}|{partyProximityGateSignature}|{string.Join(",", blockers)}";
    }

    private string BuildDiagnosticAdsOwnershipSignature()
    {
        var ads = _plugin.AdsStatusService.Current;
        return $"{_plugin.IsAdsAvailable}|{ads.StatusReadable}|{ads.OwnershipMode}|{ads.ExecutionPhase}|{ads.UtilityRunning}|{ads.UtilityTask}|{ads.UtilityMode}";
    }

    private string BuildDiagnosticRepairPhase()
    {
        if (adsRepairRecoveryActive)
            return "recovery";
        if (adsRepairRetryPending)
            return "retry-wait";
        if (adsRepairHandoffActive)
            return adsRepairUtilityObserved ? "utility-running-or-observed" : "handoff-starting";
        return "idle";
    }

    private string GetDiagnosticRepairElapsed(DateTime now)
    {
        var started = adsRepairRecoveryActive
            ? adsRepairRecoveryStarted
            : adsRepairHandoffActive
                ? adsRepairHandoffStarted
                : DateTime.MinValue;
        return started == DateTime.MinValue
            ? "0.0"
            : Math.Max(0, (now - started).TotalSeconds).ToString("F1");
    }

    private static string FormatDiagnosticNullableBool(bool? value)
        => value.HasValue ? value.Value.ToString() : "unavailable";

    private void MarkSelectedMapRunCountPending(string source)
    {
        selectedMapRunCountPendingDecrement = true;
        selectedMapRunCountDecremented = false;
        _plugin.AddDebugLog($"{source} Run count decrement armed for selected map ID {SelectedMapItemId}.");
    }

    private void ClearSelectedMapRunCountDecrement(string source)
    {
        if (!selectedMapRunCountPendingDecrement && !selectedMapRunCountDecremented)
            return;

        selectedMapRunCountPendingDecrement = false;
        selectedMapRunCountDecremented = false;
        _plugin.AddDebugLog($"{source} Cleared pending selected-map run count decrement.");
    }

    private void ConsumeSelectedMapRunCountIfPending(string source)
    {
        if (!selectedMapRunCountPendingDecrement || selectedMapRunCountDecremented)
            return;

        selectedMapRunCountPendingDecrement = false;
        selectedMapRunCountDecremented = true;

        if (SelectedMapItemId == 0)
        {
            _plugin.AddDebugLog($"{source} Run count decrement skipped: no selected map ID.");
            return;
        }

        var currentRunCount = _plugin.Configuration.GetMapRunCount(SelectedMapItemId);
        if (currentRunCount == Configuration.MapRunCountMax)
        {
            _plugin.AddDebugLog($"{source} Run count is max for map ID {SelectedMapItemId}; not decrementing.");
            return;
        }

        if (_plugin.Configuration.TryDecrementMapRunCount(SelectedMapItemId, out var remaining))
        {
            _plugin.AddDebugLog($"{source} Decremented run count for map ID {SelectedMapItemId}; remaining={remaining}.");
            return;
        }

        _plugin.AddDebugLog($"{source} Run count decrement skipped for map ID {SelectedMapItemId}; current={currentRunCount}.");
    }

    private bool TryRecoverActiveKeyItemMap(string source, bool transitionToDetectingOnActive)
    {
        if (!_plugin.InventoryService.TryFindTreasureMapKeyItem(out var keyItem))
        {
            ResetKeyItemMapRecoveryState(clearActiveKey: true);
            ClearCompletedStaleKeyItemSuppression($"{source} active key item missing");
            if (WarningMessage.Contains("key item", StringComparison.OrdinalIgnoreCase))
                ClearWarning();
            return false;
        }

        UpdateActiveKeyItemMap(keyItem, source);
        if (!selectedMapRunCountPendingDecrement)
            ShowActiveKeyItemRecoveryPopupOnce(keyItem);
        if (TryHandleCompletedStaleKeyItemSuppression(keyItem, source, out var suppressRecovery))
            return true;
        if (suppressRecovery)
            return false;

        mapCountChecked = true;
        mapOpeningRetried = false;
        initialMapCount = 0;

        if (TryResolveActiveKeyItemMapTarget(keyItem, out var recoveryLocation, out var recoverySource))
        {
            ResetKeyItemMapRecoveryState();
            if (selectedMapRunCountPendingDecrement)
                ConsumeSelectedMapRunCountIfPending(source);
            else if (WarningMessage.Contains("key item", StringComparison.OrdinalIgnoreCase))
                ClearWarning();

            RetryCount = 0;
            ResumeActiveKeyItemMapFromTarget(keyItem, recoveryLocation, recoverySource, source);
            return true;
        }

        var now = DateTime.Now;
        if (keyItemMapRecoveryStartedAt == DateTime.MinValue)
        {
            keyItemMapRecoveryStartedAt = now;
            _plugin.AddDebugLog(
                $"{source} Active key-item map found ({keyItem.DisplayName}) but no AgentMap flag, TreasureSpot capture, or cached target is available.");
        }

        var elapsed = now - keyItemMapRecoveryStartedAt;
        if (transitionToDetectingOnActive && State != BotState.DetectingLocation)
        {
            TransitionTo(BotState.DetectingLocation, $"Recovering active map target for {keyItem.DisplayName}...");
        }

        if (elapsed >= KeyItemMapRecoveryTimeout)
        {
            var failed =
                $"Treasure map key item '{keyItem.DisplayName}' is active, but no AgentMap flag, TreasureSpot capture, or cached target was available after {KeyItemMapRecoveryTimeout.TotalSeconds:F0}s. Manual intervention required.";
            SetWarning(failed);
            TransitionTo(BotState.Error, failed);
            return true;
        }

        TryOpenActiveKeyItemMap(keyItem);
        StateDetail = $"Opening active key-item map '{keyItem.DisplayName}'... ({elapsed.TotalSeconds:F0}/{KeyItemMapRecoveryTimeout.TotalSeconds:F0}s)";
        return true;
    }

    private void ShowActiveKeyItemRecoveryPopupOnce(TreasureMapKeyItem keyItem)
    {
        if (activeKeyItemRecoveryPopupShown)
            return;

        activeKeyItemRecoveryPopupShown = true;
        var message = $"Active map found: {keyItem.DisplayName}. Recovering it; run count not decremented.";
        try
        {
            Plugin.ToastGui.ShowNormal(message);
        }
        catch (Exception ex)
        {
            _plugin.AddDebugLog($"[KeyItemMap] Toast failed: {ex.Message}");
        }

        _plugin.AddDebugLog($"[KeyItemMap] {message}");
    }

    private void TryOpenActiveKeyItemMap(TreasureMapKeyItem keyItem)
    {
        var now = DateTime.Now;
        if (now < keyItemMapNextOpenAttemptAt)
            return;

        keyItemMapNextOpenAttemptAt = now.Add(KeyItemMapOpenRetryInterval);
        keyItemMapOpenAttemptCount++;

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
            _plugin.AddDebugLog($"[KeyItemMap] Recovering active treasure-map key item {keyItem.DisplayName}; run count not decremented unless this came from the newly opened selected map.");
            stateStartTime = DateTime.Now;
        }
    }

    private bool TryHandleCompletedStaleKeyItemSuppression(
        TreasureMapKeyItem keyItem,
        string source,
        out bool suppressRecovery)
    {
        suppressRecovery = false;

        var sameSuppressedKey = IsSameCompletedStaleKeyItem(keyItem);
        if (completedStaleKeyItemSuppressionActive && !sameSuppressedKey)
        {
            LogCompletedStaleKeyItemGuard(
                keyItem,
                source,
                BuildCompletedKeyItemStaleState(hasCompletionEvidence: false),
                "clear-different-key",
                force: true);
            ClearCompletedStaleKeyItemSuppression($"{source} different active key item");
        }

        if (State == BotState.OpeningChest)
            return false;

        var hasCompletionEvidence = HasCompletedKeyItemCompletionEvidence() || sameSuppressedKey;
        if (!hasCompletionEvidence)
            return false;

        var staleState = BuildCompletedKeyItemStaleState(hasCompletionEvidence);
        if (!staleState.IsStale)
        {
            if (sameSuppressedKey)
            {
                LogCompletedStaleKeyItemGuard(keyItem, source, staleState, "cancel-suppression", force: true);
                ClearCompletedStaleKeyItemSuppression($"{source} stale condition cleared");
            }
            else
            {
                LogCompletedStaleKeyItemGuard(keyItem, source, staleState, "not-suppressed");
            }

            return false;
        }

        var wasAlreadySuppressed = sameSuppressedKey;
        SetCompletedStaleKeyItemSuppression(keyItem);
        suppressRecovery = true;

        var action = State == BotState.DetectingLocation
            ? "suppress-transition-completed"
            : "suppress-active-key-recovery";
        LogCompletedStaleKeyItemGuard(
            keyItem,
            source,
            staleState,
            action,
            force: !wasAlreadySuppressed || State == BotState.DetectingLocation);

        if (WarningMessage.Contains("key item", StringComparison.OrdinalIgnoreCase))
            ClearWarning();

        if (State == BotState.DetectingLocation)
        {
            TransitionTo(BotState.Completed, $"Ignoring stale completed active key item for {keyItem.DisplayName}...");
            return true;
        }

        return false;
    }

    private bool HasCompletedKeyItemCompletionEvidence()
        => chestConfirmedThisMap ||
           portalConfirmedThisMap ||
           dungeonConfirmedThisMap ||
           openingChestOpenedByChat ||
           openingChestPortalByChat;

    private CompletedKeyItemStaleState BuildCompletedKeyItemStaleState(bool hasCompletionEvidence)
    {
        var hasTargetableCoffer = FindTargetableOverworldCoffer(OverworldRecoveryObjectSearchRange) != null;
        var hasTargetablePortal = FindTargetablePortal(OverworldRecoveryObjectSearchRange) != null;

        return new CompletedKeyItemStaleState(
            hasCompletionEvidence,
            IsOverworldMapDutyActive(),
            hasTargetableCoffer,
            hasTargetablePortal,
            portalApproachPosition.HasValue,
            portalRetryStart != DateTime.MinValue);
    }

    private void SetCompletedStaleKeyItemSuppression(TreasureMapKeyItem keyItem)
    {
        completedStaleKeyItemSuppressionActive = true;
        completedStaleKeyItemId = keyItem.ItemId;
        completedStaleKeyItemSlot = keyItem.Slot;
        completedStaleKeyItemMapItemId = ResolveKeyItemMapItemId(keyItem);
    }

    private bool IsSameCompletedStaleKeyItem(TreasureMapKeyItem keyItem)
    {
        if (!completedStaleKeyItemSuppressionActive)
            return false;

        if (completedStaleKeyItemId != keyItem.ItemId || completedStaleKeyItemSlot != keyItem.Slot)
            return false;

        var mapItemId = ResolveKeyItemMapItemId(keyItem);
        return completedStaleKeyItemMapItemId == 0 ||
               mapItemId == 0 ||
               completedStaleKeyItemMapItemId == mapItemId;
    }

    private uint ResolveKeyItemMapItemId(TreasureMapKeyItem keyItem)
        => keyItem.KnownMapItemId != 0 ? keyItem.KnownMapItemId : SelectedMapItemId;

    private void ClearCompletedStaleKeyItemSuppression(string reason)
    {
        if (!completedStaleKeyItemSuppressionActive)
            return;

        _plugin.AddDebugLog(
            $"[KeyItemMap][CompletionGuard] Cleared stale active key-item suppression ({reason}); " +
            $"keyItem={completedStaleKeyItemId} slot={completedStaleKeyItemSlot} mapId={completedStaleKeyItemMapItemId}.");

        completedStaleKeyItemSuppressionActive = false;
        completedStaleKeyItemId = 0;
        completedStaleKeyItemSlot = -1;
        completedStaleKeyItemMapItemId = 0;
        lastCompletedStaleKeyItemGuardLogAt = DateTime.MinValue;
    }

    private void LogCompletedStaleKeyItemGuard(
        TreasureMapKeyItem keyItem,
        string source,
        CompletedKeyItemStaleState staleState,
        string action,
        bool force = false)
    {
        var now = DateTime.Now;
        if (!force && now - lastCompletedStaleKeyItemGuardLogAt < TimeSpan.FromSeconds(5.0))
            return;

        lastCompletedStaleKeyItemGuardLogAt = now;
        _plugin.AddDebugLog(
            $"[KeyItemMap][CompletionGuard] {source} keyItem={keyItem.ItemId} slot={keyItem.Slot} " +
            $"mapId={ResolveKeyItemMapItemId(keyItem)} evidence=chest:{chestConfirmedThisMap},openedChat:{openingChestOpenedByChat}," +
            $"portal:{portalConfirmedThisMap},portalChat:{openingChestPortalByChat},dungeon:{dungeonConfirmedThisMap},prior:{staleState.HasCompletionEvidence} " +
            $"state=duty:{staleState.MapDutyActive},coffer200:{staleState.HasTargetableCoffer},portal200:{staleState.HasTargetablePortal}," +
            $"capturedPortal:{staleState.HasCapturedPortalPosition},portalRetry:{staleState.PortalRetryWindowOpen} action={action}.");
    }

    private void UpdateActiveKeyItemMap(TreasureMapKeyItem keyItem, string source)
    {
        var changed = activeKeyItemMapItemId != keyItem.ItemId || activeKeyItemMapSlot != keyItem.Slot;
        if (changed)
        {
            ResetKeyItemMapRecoveryState();
            activeMapTargetCache.Clear();
            digIssuedThisMap = false;
            digIssuedAt = DateTime.MinValue;
            chestConfirmedThisMap = false;
            portalConfirmedThisMap = false;
            dungeonConfirmedThisMap = false;
            ResetOpeningChestLifecycleState();
            ResetOpeningChestCofferMemory();
            CurrentLocation = null;
            ResetOverworldRecoveryState(clearTeleportedTarget: true);
            activeKeyItemMapItemId = keyItem.ItemId;
            activeKeyItemMapSlot = keyItem.Slot;
            activeKeyItemRecoverySourceLogged = false;
            activeKeyItemRecoveryUnderwaterLogged = false;
            activeKeyItemRecoveryPopupShown = false;
            _plugin.AddDebugLog($"{source} Active treasure map key item: {keyItem.DisplayName} (item {keyItem.ItemId}, slot {keyItem.Slot}).");
        }

        if (keyItem.KnownMapItemId != 0)
        {
            if (SelectedMapItemId != keyItem.KnownMapItemId ||
                currentLandingMode != ResolveLandingMode(keyItem.KnownMapItemId))
            {
                SelectedMapItemId = keyItem.KnownMapItemId;
                NormalizeLandingModeForSelectedMap(source);
                if (!IsThiefUnderwaterLandingMode())
                    ResetUnderwaterLandingState();
                _plugin.AddDebugLog($"{source} Matched key item to known map ID {SelectedMapItemId}; landing mode {currentLandingMode}.");
                if (IsThiefUnderwaterLandingMode())
                    LogThiefWaterInfo($"{source} Thief map key item matched; using underwater bounce landing mode.");
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

    private bool TryResolveActiveKeyItemMapTarget(
        TreasureMapKeyItem keyItem,
        out MapLocation location,
        out string recoverySource)
    {
        var expectedMapItemId = keyItem.KnownMapItemId != 0 ? keyItem.KnownMapItemId : SelectedMapItemId;

        var agentMapLocation = _plugin.MapFlagService.TryGetMapLocation();
        if (agentMapLocation != null)
        {
            CacheActiveMapTarget(keyItem, agentMapLocation, "AgentMap");
            location = CloneMapLocation(agentMapLocation);
            recoverySource = "AgentMap";
            return true;
        }

        if (_plugin.TreasureMapLocationService.TryGetLatestLocation(expectedMapItemId, keyItem.ItemId, out var capturedLocation))
        {
            CacheActiveMapTarget(keyItem, capturedLocation, "TreasureSpot");
            QueueAreaMapAutoCloseAfterTreasureCapture("[ActiveKeyItem] TreasureMapLocationService");
            location = CloneMapLocation(capturedLocation);
            recoverySource = "TreasureSpot";
            return true;
        }

        if (TryGetCachedActiveMapTarget(keyItem, out location))
        {
            recoverySource = "cache";
            return true;
        }

        if (CurrentLocation != null)
        {
            CacheActiveMapTarget(keyItem, CurrentLocation, "CurrentLocation");
            location = CloneMapLocation(CurrentLocation);
            recoverySource = "current location";
            return true;
        }

        location = new MapLocation();
        recoverySource = string.Empty;
        return false;
    }

    private void ResumeActiveKeyItemMapFromTarget(
        TreasureMapKeyItem keyItem,
        MapLocation location,
        string recoverySource,
        string source)
    {
        PopulateNearestAetheryte(location, out var aetheryteId, out var bestAethDist, out var usedXyz);
        SetLocation(location);
        LogActiveKeyItemRecoverySourceOnce(keyItem, recoverySource, source, location);

        mapCountChecked = true;
        mapOpeningRetried = false;

        var currentTerritory = Plugin.ClientState.TerritoryType;
        if (currentTerritory != location.TerritoryId)
        {
            if (aetheryteId == 0)
            {
                var failed = $"Active map target recovered from {recoverySource}, but no aetheryte is available for {location.ZoneName}. Manual intervention required.";
                SetWarning(failed);
                TransitionTo(BotState.Error, failed);
                return;
            }

            TransitionTo(BotState.Teleporting, $"Recovered active map target from {recoverySource}; teleporting to {location.ZoneName}...");
            return;
        }

        RouteSameTerritoryMapTarget(
            location,
            aetheryteId,
            bestAethDist,
            usedXyz,
            source,
            $"Recovered active map target from {recoverySource}; already at map location - landing and digging...",
            $"Recovered active map target from {recoverySource}; already mounted - resuming map run...",
            $"Recovered active map target from {recoverySource}; mounting up...",
            allowSameZoneTeleport: true);
    }

    private void LogActiveKeyItemRecoverySourceOnce(
        TreasureMapKeyItem keyItem,
        string recoverySource,
        string source,
        MapLocation location)
    {
        var isDiving = Plugin.Condition[ConditionFlag.Diving];
        if (!activeKeyItemRecoverySourceLogged)
        {
            activeKeyItemRecoverySourceLogged = true;
            _plugin.AddDebugLog(
                $"{source} Active key-item recovery source={recoverySource}; key={keyItem.DisplayName}; " +
                $"target=T{location.TerritoryId} {FormatVectorCompact(new Vector3(location.X, location.Y, location.Z))}; " +
                $"mapId={SelectedMapItemId}; landing={currentLandingMode}; diving={isDiving}.");
        }

        if (isDiving && CanUseUnderwaterNavigation() && !activeKeyItemRecoveryUnderwaterLogged)
        {
            activeKeyItemRecoveryUnderwaterLogged = true;
            _plugin.AddDebugLog("[Underwater] Active thief-map recovery is already Diving; routing by target range.");
        }
    }

    private bool TryGetActiveMapTargetKey(TreasureMapKeyItem? keyItem, out ActiveMapTargetKey key)
    {
        var eventItemId = keyItem?.ItemId ?? activeKeyItemMapItemId;
        var mapItemId = keyItem?.KnownMapItemId ?? SelectedMapItemId;

        if (eventItemId == 0 && _plugin.InventoryService.TryFindTreasureMapKeyItem(out var currentKeyItem))
        {
            eventItemId = currentKeyItem.ItemId;
            if (mapItemId == 0)
                mapItemId = currentKeyItem.KnownMapItemId;
        }

        if (mapItemId == 0)
            mapItemId = SelectedMapItemId;

        if (eventItemId == 0)
        {
            key = default;
            return false;
        }

        key = new ActiveMapTargetKey(eventItemId, mapItemId);
        return true;
    }

    private void CacheActiveMapTarget(TreasureMapKeyItem keyItem, MapLocation location, string source)
    {
        if (!TryGetActiveMapTargetKey(keyItem, out var key))
            return;

        activeMapTargetCache[key] = CloneMapLocation(location);
        _plugin.AddDebugLog($"[KeyItemMap] Cached active map target from {source}: key={key.EventItemId}/{key.MapItemId}, T{location.TerritoryId} {FormatVectorCompact(new Vector3(location.X, location.Y, location.Z))}.");
    }

    private void CacheActiveMapTarget(MapLocation location, string source)
    {
        if (!TryGetActiveMapTargetKey(null, out var key))
            return;

        activeMapTargetCache[key] = CloneMapLocation(location);
        _plugin.AddDebugLog($"[KeyItemMap] Cached active map target from {source}: key={key.EventItemId}/{key.MapItemId}, T{location.TerritoryId} {FormatVectorCompact(new Vector3(location.X, location.Y, location.Z))}.");
    }

    private bool TryGetCachedActiveMapTarget(TreasureMapKeyItem keyItem, out MapLocation location)
    {
        if (TryGetActiveMapTargetKey(keyItem, out var key) &&
            activeMapTargetCache.TryGetValue(key, out var cached))
        {
            location = CloneMapLocation(cached);
            return true;
        }

        location = new MapLocation();
        return false;
    }

    private static MapLocation CloneMapLocation(MapLocation location)
    {
        return new MapLocation
        {
            TerritoryId = location.TerritoryId,
            ZoneName = location.ZoneName,
            X = location.X,
            Y = location.Y,
            Z = location.Z,
            NearestAetheryteId = location.NearestAetheryteId,
            NearestAetheryteName = location.NearestAetheryteName,
            IsResolved = location.IsResolved,
        };
    }

    private List<uint> GetEnabledMapCandidates(
        Dictionary<uint, MapSourceCount> mapSources,
        bool includeInventory,
        bool includeSaddlebags)
    {
        includeSaddlebags &= _plugin.Configuration.EnableSaddlebagMapRetrieval;

        return mapSources
            .Where(kvp =>
            {
                if (!_plugin.Configuration.IsMapTypeEnabled(kvp.Key))
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
        if (!_plugin.Configuration.EnableSaddlebagMapRetrieval)
        {
            _plugin.AddDebugLog($"[Saddlebag] Retrieval disabled; skipping map ID {mapItemId}.");
            return;
        }

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

        if (!_plugin.Configuration.EnableSaddlebagMapRetrieval)
        {
            _plugin.AddDebugLog("[Saddlebag] Retrieval disabled while active; cancelling saddlebag retrieval.");
            ResetSaddlebagRetrieval();
            return;
        }

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
        BeginSaddlebagMapRefresh("Start");
    }

    private void BeginCompletedMapRefresh()
    {
        BeginSaddlebagMapRefresh("Completed");
    }

    private void BeginSaddlebagMapRefresh(string scope)
    {
        if (!_plugin.Configuration.EnableSaddlebagMapRetrieval)
        {
            ResetStartMapRefresh();
            _plugin.AddDebugLog($"[MapRefresh][{scope}] Saddlebag retrieval disabled; skipping saddlebag refresh.");
            return;
        }

        startMapRefreshPending = true;
        startMapRefreshOpenedSaddlebag = false;
        startMapRefreshScope = scope;
        startMapRefreshStartedAt = DateTime.Now;
        _plugin.AddDebugLog($"[MapRefresh][{startMapRefreshScope}] Queued saddlebag refresh before map scan.");
    }

    private void ResetStartMapRefresh()
    {
        startMapRefreshPending = false;
        startMapRefreshOpenedSaddlebag = false;
        startMapRefreshScope = "Start";
        startMapRefreshStartedAt = DateTime.MinValue;
    }

    private bool TickStartMapRefresh()
    {
        if (!startMapRefreshPending)
            return false;

        if (!_plugin.Configuration.EnableSaddlebagMapRetrieval)
        {
            _plugin.AddDebugLog($"[MapRefresh][{startMapRefreshScope}] Saddlebag retrieval disabled; cancelling refresh.");
            ResetStartMapRefresh();
            return false;
        }

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
                _plugin.AddDebugLog($"[MapRefresh][{startMapRefreshScope}] Opened saddlebag for map refresh.");
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
        var scope = startMapRefreshScope;
        _plugin.InventoryService.ScanForMapSources(includeSaddlebags: _plugin.Configuration.EnableSaddlebagMapRetrieval);
        RefreshCompletedRetainerMapCountsIfNeeded(scope);

        if (closeSaddlebagAfterScan && GameHelpers.IsAddonVisible("InventoryBuddy"))
        {
            GameHelpers.CloseCurrentAddon();
            _plugin.AddDebugLog($"[MapRefresh][{scope}] Closed saddlebag after map refresh.");
        }

        ResetStartMapRefresh();
        StateDetail = "Saddlebag map refresh complete.";
        _plugin.AddDebugLog($"[MapRefresh][{scope}] {detail}");
    }

    private void RefreshCompletedRetainerMapCountsIfNeeded(string scope)
    {
        if (scope != "Completed" || !_plugin.Configuration.EnableRetainerMapRetrieval)
            return;

        if (!_plugin.IsXaDatabaseAvailable)
        {
            _plugin.RetainerMapRetrievalService.ClearUnavailableXaDatabaseState();
            _plugin.AddDebugLog("[MapRefresh][Completed] XADB unavailable; skipped retainer refresh.");
            return;
        }

        var enabledMapIds = _plugin.Configuration.GetRunnableMapIds(TreasureMapData.AllMapItemIds);
        var counts = _plugin.RetainerMapRetrievalService.GetRetainerMapCounts(enabledMapIds, refreshFirst: true);
        _plugin.AddDebugLog(
            $"[MapRefresh][Completed] Refreshed XADB retainer maps for {FormatMapIds(enabledMapIds)}; " +
            $"{counts.Values.Sum()} item(s) across {counts.Count} map type(s).");
    }

    private bool TryRetrieveRetainerMap(IReadOnlyCollection<uint> enabledMapIds, string emptyInventoryError, bool allowGatherFallback = false)
    {
        if (!_plugin.Configuration.EnableRetainerMapRetrieval)
        {
            _plugin.AddDebugLog($"[RetainerMap] Retrieval disabled. {emptyInventoryError}");
            if (allowGatherFallback && TryStartMapGatherFallback(enabledMapIds, emptyInventoryError))
                return true;

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
                if (allowGatherFallback && TryStartMapGatherFallback(enabledMapIds, emptyInventoryError))
                    return true;

                HandleError(emptyInventoryError);
                return true;
        }

        return false;
    }

    public void StartConfiguredMapGatherCommand()
    {
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
        {
            const string message = "Cannot gather a map from Main Menu. Log in first.";
            SetWarning(message);
            _plugin.PrintChat(message);
            _plugin.AddDebugLog("[Gather] Manual gather ignored because client is not logged in.");
            return;
        }

        if (State != BotState.Idle && State != BotState.Error && State != BotState.Completed)
        {
            var message = $"Cannot gather a map while LootGoblin is busy ({State}).";
            SetWarning(message);
            _plugin.PrintChat(message);
            _plugin.AddDebugLog($"[Gather] Manual gather ignored because state is {State}.");
            return;
        }

        if (mapGatherStep != MapGatherStep.Idle)
        {
            _plugin.PrintChat("Map gathering is already running.");
            _plugin.AddDebugLog("[Gather] Manual gather ignored because a gather step is already active.");
            return;
        }

        if (_plugin.SelectedGatherJobId == 0)
        {
            const string message = "Gather job not configured.";
            SetWarning(message);
            _plugin.PrintChat(message);
            _plugin.AddDebugLog("[Gather] Manual gather skipped: gather job not configured.");
            return;
        }

        var candidates = _plugin.ActiveGatherEnabledMapTypes
            .Where(itemId =>
                _plugin.IsMapGatherEnabled(itemId) &&
                TreasureMapData.KnownMaps.TryGetValue(itemId, out var mapInfo) &&
                mapInfo.IsGatherable)
            .ToList();

        if (candidates.Count == 0)
        {
            const string message = "No gatherable map configured.";
            SetWarning(message);
            _plugin.PrintChat(message);
            _plugin.AddDebugLog("[Gather] Manual gather skipped: no gatherable configured map.");
            return;
        }

        var targetItemId = candidates[0];
        var targetName = TreasureMapData.KnownMaps.TryGetValue(targetItemId, out var info)
            ? info.Name
            : $"ID {targetItemId}";
        var inventoryCount = _plugin.InventoryService.GetMapCount(targetItemId);
        if (inventoryCount > 0)
        {
            _plugin.PrintChat($"{targetName} already in inventory; not gathering.");
            _plugin.AddDebugLog($"[Gather] Manual gather skipped: {targetName} already in inventory ({inventoryCount}).");
            return;
        }

        if (TryBeginMapGather(targetItemId, targetName, manualCommand: true, failureContext: "manual gather command"))
            _plugin.PrintChat($"Gathering {targetName}.");
        else if (!string.IsNullOrWhiteSpace(WarningMessage))
            _plugin.PrintChat(WarningMessage);
    }

    private bool TryStartMapGatherFallback(IReadOnlyCollection<uint> enabledMapIds, string fallbackError)
    {
        if (_plugin.SelectedGatherJobId == 0)
        {
            _plugin.AddDebugLog("[Gather] Gather job not configured; skipping map gathering fallback.");
            return false;
        }

        if (mapGatherStep != MapGatherStep.Idle)
            return true;

        var candidates = enabledMapIds
            .Where(itemId =>
                !failedGatherMapIdsThisRun.Contains(itemId) &&
                _plugin.IsMapGatherEnabled(itemId) &&
                TreasureMapData.KnownMaps.TryGetValue(itemId, out var mapInfo) &&
                mapInfo.IsGatherable)
            .ToList();

        if (candidates.Count == 0)
        {
            _plugin.AddDebugLog("[Gather] No enabled gatherable map fallback candidate.");
            return false;
        }

        var targetItemId = candidates[0];
        var targetName = TreasureMapData.KnownMaps.TryGetValue(targetItemId, out var info)
            ? info.Name
            : $"ID {targetItemId}";

        return TryBeginMapGather(targetItemId, targetName, manualCommand: false, failureContext: fallbackError);
    }

    private bool TryBeginMapGather(uint targetItemId, string targetName, bool manualCommand, string failureContext)
    {
        if (!CanRunMapGatherAction(out var readyReason))
        {
            StateDetail = $"Waiting to gather {targetName}: {readyReason}";
            if (manualCommand)
            {
                SetWarning($"Cannot gather {targetName}: {readyReason}.");
                _plugin.AddDebugLog($"[Gather] Manual gather not started: {readyReason}");
                return false;
            }

            _plugin.AddDebugLog($"[Gather] Fallback deferred: {readyReason}");
            return true;
        }

        if (!_plugin.MapAllowanceService.IsAllowanceReady(out var allowanceDetail))
        {
            SetWarning($"Skipping map gathering for {targetName}: {allowanceDetail}");
            _plugin.AddDebugLog($"[Gather] {targetName} skipped: {allowanceDetail}");
            return false;
        }

        _plugin.GatherBuddyRebornService.CheckAvailability(logStatus: true);
        var gatherJobId = _plugin.SelectedGatherJobId;
        var currentJobId = _plugin.JobSwitchService.GetCurrentClassJobId();
        _plugin.AddDebugLog(
            $"[Gather] {(manualCommand ? "Manual" : "Fallback")} targetMap={targetName} ({targetItemId}); currentJob={FormatClassJob(currentJobId)}; expectedJob={FormatClassJob(gatherJobId)}; GatherBuddy={_plugin.GatherBuddyRebornService.StatusText}");
        if (!_plugin.GatherBuddyRebornService.IsAvailable)
        {
            SetWarning($"Cannot gather {targetName}: {_plugin.GatherBuddyRebornService.StatusText}");
            _plugin.AddDebugLog($"[Gather] GatherBuddy unavailable for targetMap={targetName} ({targetItemId}); status={_plugin.GatherBuddyRebornService.StatusText}; context={failureContext}");
            return false;
        }

        if (!_plugin.JobSwitchService.TryCaptureCurrentJob(out mapGatherReturnJob, out var snapshotDetail))
        {
            SetWarning($"Cannot gather {targetName}: {snapshotDetail}.");
            return false;
        }

        mapGatherTargetItemId = targetItemId;
        mapGatherTargetName = targetName;
        mapGatherInitialInventoryCount = _plugin.InventoryService.GetMapCount(targetItemId);
        mapGatherStepStartedAt = DateTime.Now;
        mapGatherNextStatusAt = DateTime.MinValue;
        mapGatherManualCommandActive = manualCommand;
        SetCombatAutomationForCombatState(inCombat: false, "map gathering", force: true);
        _plugin.NavigationService.StopNavigation(clearFlag: true);
        _plugin.AddDebugLog(
            $"[Gather] Starting {(manualCommand ? "manual gather" : "fallback")} for {targetName}; initial inventory={mapGatherInitialInventoryCount}; return job={snapshotDetail}.");
        EnterMapGatherStep(MapGatherStep.SwitchingToGatherJob, $"Switching to gather job for {targetName}...");
        TransitionTo(BotState.GatheringMap, manualCommand ? $"Gathering {targetName}..." : $"Gathering missing {targetName}...");
        return true;
    }

    private void TickGatheringMap()
    {
        if (mapGatherTargetItemId == 0)
        {
            FailMapGathering("No map gather target is active.");
            return;
        }

        var currentCount = _plugin.InventoryService.GetMapCount(mapGatherTargetItemId);
        if (mapGatherStep is not MapGatherStep.ClosingGatherWindow and not MapGatherStep.SwitchingBack &&
            currentCount > mapGatherInitialInventoryCount)
        {
            _plugin.AddDebugLog(
                $"[Gather] Inventory count confirmed for {mapGatherTargetName}: {mapGatherInitialInventoryCount}->{currentCount}; closing gathering window before switch-back.");
            _plugin.MapAllowanceService.MarkAllowanceConsumedByGather();
            EnterMapGatherStep(MapGatherStep.ClosingGatherWindow, $"Closing gathering window for {mapGatherTargetName}...");
        }

        if (DateTime.Now - mapGatherStepStartedAt > TimeSpan.FromSeconds(90) &&
            mapGatherStep is MapGatherStep.SwitchingToGatherJob or MapGatherStep.StartingGatherBuddy or MapGatherStep.ClosingGatherWindow or MapGatherStep.SwitchingBack)
        {
            FailMapGathering(BuildMapGatherTimeoutDetail());
            return;
        }

        switch (mapGatherStep)
        {
            case MapGatherStep.SwitchingToGatherJob:
                TickMapGatherSwitchToGatherJob();
                break;

            case MapGatherStep.StartingGatherBuddy:
                TickMapGatherStartGatherBuddy();
                break;

            case MapGatherStep.WaitingForMap:
                TickMapGatherWaitingForMap();
                break;

            case MapGatherStep.ClosingGatherWindow:
                TickMapGatherClosingGatherWindow();
                break;

            case MapGatherStep.SwitchingBack:
                TickMapGatherSwitchBack();
                break;
        }
    }

    private void TickMapGatherSwitchToGatherJob()
    {
        var gatherJobId = _plugin.SelectedGatherJobId;
        if (gatherJobId == 0)
        {
            FailMapGathering("Gather job was cleared while gathering.");
            return;
        }

        var currentJob = _plugin.JobSwitchService.GetCurrentClassJobId();
        LogMapGatherJobWaitStatus(currentJob, gatherJobId);

        if (currentJob == gatherJobId)
        {
            EnterMapGatherStep(MapGatherStep.StartingGatherBuddy, $"Starting GatherBuddy Reborn for {mapGatherTargetName}...");
            return;
        }

        if (mapGatherJobSwitchIssued)
        {
            StateDetail = $"Waiting for gather job switch to {ClassJobOptions.GetName(gatherJobId)}; current job {FormatClassJob(currentJob)}...";
            return;
        }

        if (!CanRunMapGatherAction(out var readyReason))
        {
            StateDetail = $"Waiting to switch to gather job: {readyReason}";
            return;
        }

        if (!_plugin.JobSwitchService.TrySwitchToJob(gatherJobId, out var detail))
        {
            FailMapGathering($"Could not switch to gather job: {detail}");
            return;
        }

        mapGatherJobSwitchIssued = true;
        _plugin.AddDebugLog(
            $"[Gather] Gather job switch issued for targetMap={mapGatherTargetName} ({mapGatherTargetItemId}); currentJob={FormatClassJob(currentJob)}; expectedJob={FormatClassJob(gatherJobId)}; detail={detail}");
        StateDetail = detail;
    }

    private void TickMapGatherStartGatherBuddy()
    {
        if (DateTime.Now - mapGatherStepStartedAt < TimeSpan.FromMilliseconds(750))
        {
            StateDetail = $"Waiting for gather job to settle before starting GatherBuddy Reborn for {mapGatherTargetName}...";
            return;
        }

        if (!CanRunMapGatherAction(out var readyReason))
        {
            StateDetail = $"Waiting to start GatherBuddy Reborn: {readyReason}";
            return;
        }

        var expectedJobId = _plugin.SelectedGatherJobId;
        if (expectedJobId == 0)
        {
            FailMapGathering("Gather job was cleared while gathering.");
            return;
        }

        var currentJob = _plugin.JobSwitchService.GetCurrentClassJobId();
        if (currentJob != expectedJobId)
        {
            FailMapGathering(
                $"Current job changed before GatherBuddy start; currentJob={FormatClassJob(currentJob)}; expectedJob={FormatClassJob(expectedJobId)}.");
            return;
        }

        var gatherBuddyStatus = GetGatherBuddyStatusText();
        _plugin.AddDebugLog(
            $"[Gather] Starting GatherBuddy for targetMap={mapGatherTargetName} ({mapGatherTargetItemId}); currentJob={FormatClassJob(currentJob)}; expectedJob={FormatClassJob(expectedJobId)}; GatherBuddy={gatherBuddyStatus}");

        if (!_plugin.GatherBuddyRebornService.StartOneShot(mapGatherTargetItemId, mapGatherTargetName, expectedJobId, out var detail))
        {
            _plugin.AddDebugLog(
                $"[Gather] GatherBuddy start failed for targetMap={mapGatherTargetName} ({mapGatherTargetItemId}); detail={detail}");
            FailMapGathering(detail);
            return;
        }

        EnterMapGatherStep(MapGatherStep.WaitingForMap, $"Waiting for GatherBuddy Reborn to gather {mapGatherTargetName}...");
    }

    private void TickMapGatherWaitingForMap()
    {
        if (!_plugin.GatherBuddyRebornService.ValidateOneShotTarget(out var targetDetail))
        {
            FailMapGathering(targetDetail);
            return;
        }

        if (DateTime.Now >= mapGatherNextStatusAt)
        {
            mapGatherNextStatusAt = DateTime.Now.AddSeconds(2);
            if (_plugin.GatherBuddyRebornService.TryGetAutoGatherStatus(out var status) &&
                !string.IsNullOrWhiteSpace(status))
            {
                StateDetail = $"Gathering {mapGatherTargetName}: {status}";
            }
            else
            {
                StateDetail = $"Gathering {mapGatherTargetName}...";
            }

            _plugin.AddDebugLog($"[Gather] {StateDetail}");
        }

        if (!_plugin.GatherBuddyRebornService.IsAutoGatherEnabled() &&
            DateTime.Now - mapGatherStepStartedAt > TimeSpan.FromSeconds(10))
        {
            FailMapGathering($"GatherBuddy Reborn auto-gather stopped before {mapGatherTargetName} was obtained.");
        }
    }

    private void TickMapGatherClosingGatherWindow()
    {
        if (!mapGatherCancelIssued)
        {
            mapGatherCancelIssued = true;
            _plugin.GatherBuddyRebornService.Cancel();
            _plugin.AddDebugLog($"[Gather] GatherBuddy cancel issued after inventory confirmation for {mapGatherTargetName}.");
        }

        var gatheringVisible = GameHelpers.IsAddonVisible("Gathering");
        var masterpieceVisible = GameHelpers.IsAddonVisible("GatheringMasterpiece");
        var gatheringCondition = Plugin.Condition[ConditionFlag.Gathering];
        var executingGatheringAction = Plugin.Condition[ConditionFlag.ExecutingGatheringAction];

        if (!gatheringVisible && !masterpieceVisible && !gatheringCondition && !executingGatheringAction)
        {
            _plugin.AddDebugLog($"[Gather] Gathering window close complete for {mapGatherTargetName}; switching back.");
            EnterMapGatherStep(MapGatherStep.SwitchingBack, $"Gathering window closed for {mapGatherTargetName}; switching back...");
            return;
        }

        StateDetail = $"Closing gathering window for {mapGatherTargetName}...";

        if (DateTime.Now < mapGatherLastCloseAttemptAt.AddMilliseconds(750))
            return;

        mapGatherLastCloseAttemptAt = DateTime.Now;
        mapGatherCloseAttemptCount++;

        var attemptedCallback = false;
        var callbackSucceeded = false;
        if (gatheringVisible)
        {
            attemptedCallback = true;
            var closed = GameHelpers.TryCloseAddonByCallback("Gathering");
            callbackSucceeded |= closed;
            _plugin.AddDebugLog($"[Gather] Close attempt {mapGatherCloseAttemptCount}: Gathering callback result={closed}.");
        }

        if (masterpieceVisible)
        {
            attemptedCallback = true;
            var closed = GameHelpers.TryCloseAddonByCallback("GatheringMasterpiece");
            callbackSucceeded |= closed;
            _plugin.AddDebugLog($"[Gather] Close attempt {mapGatherCloseAttemptCount}: GatheringMasterpiece callback result={closed}.");
        }

        if ((!attemptedCallback || !callbackSucceeded || mapGatherCloseAttemptCount >= 3) &&
            (gatheringVisible || masterpieceVisible || gatheringCondition || executingGatheringAction))
        {
            GameHelpers.CloseCurrentAddon();
            _plugin.AddDebugLog($"[Gather] Close attempt {mapGatherCloseAttemptCount}: Escape fallback issued.");
        }

        _plugin.AddDebugLog($"[Gather] Waiting for gathering close; {DescribeMapGatherCloseGate()}; attempts={mapGatherCloseAttemptCount}.");
    }

    private void TickMapGatherSwitchBack()
    {
        if (!CanRunMapGatherAction(out var readyReason))
        {
            StateDetail = $"Waiting to switch back after gathering: {readyReason}";
            return;
        }

        if (!_plugin.JobSwitchService.TrySwitchToSnapshot(mapGatherReturnJob, out var detail))
        {
            SetWarning($"Gathered {mapGatherTargetName}, but could not switch back to combat job: {detail}");
            _plugin.AddDebugLog($"[Gather] Switch-back failed: {detail}");
        }
        else
        {
            _plugin.AddDebugLog($"[Gather] {detail}");
        }

        var targetName = mapGatherTargetName;
        var manualCommand = mapGatherManualCommandActive;
        failedGatherMapIdsThisRun.Remove(mapGatherTargetItemId);
        ResetMapGathering(cancelGatherBuddy: false);
        lastMapScanTime = DateTime.MinValue;
        RetryCount = 0;
        CurrentLocation = null;
        ResetPerMapCommandTriggers();
        if (manualCommand)
        {
            _plugin.PrintChat($"Gathered {targetName}.");
            TransitionTo(BotState.Idle, $"Gathered {targetName}.");
            return;
        }

        TransitionTo(BotState.SelectingMap, $"Gathered {targetName}; rechecking maps...");
    }

    private void EnterMapGatherStep(MapGatherStep nextStep, string detail)
    {
        mapGatherStep = nextStep;
        mapGatherStepStartedAt = DateTime.Now;
        mapGatherNextStatusAt = DateTime.MinValue;
        if (nextStep == MapGatherStep.SwitchingToGatherJob)
            mapGatherJobSwitchIssued = false;
        if (nextStep == MapGatherStep.ClosingGatherWindow)
        {
            mapGatherCancelIssued = false;
            mapGatherLastCloseAttemptAt = DateTime.MinValue;
            mapGatherCloseAttemptCount = 0;
        }
        StateDetail = detail;
        _plugin.AddDebugLog($"[Gather] {detail}");
    }

    private void FailMapGathering(string detail)
    {
        var targetName = string.IsNullOrWhiteSpace(mapGatherTargetName)
            ? $"ID {mapGatherTargetItemId}"
            : mapGatherTargetName;

        if (mapGatherTargetItemId != 0)
            failedGatherMapIdsThisRun.Add(mapGatherTargetItemId);

        _plugin.AddDebugLog($"[Gather] ERROR: {detail}");
        _plugin.GatherBuddyRebornService.Cancel();

        if (mapGatherReturnJob.ClassJobId != 0)
            _plugin.AddDebugLog(
                $"[Gather] Leaving current job after gather failure; currentJob={FormatClassJob(_plugin.JobSwitchService.GetCurrentClassJobId())}; returnJob={FormatClassJob(mapGatherReturnJob.ClassJobId)}.");

        var manualCommand = mapGatherManualCommandActive;
        ResetMapGathering(cancelGatherBuddy: false);
        SetWarning($"Could not gather {targetName}: {detail}");
        if (manualCommand)
        {
            _plugin.PrintChat(WarningMessage);
            TransitionTo(BotState.Error, WarningMessage);
            return;
        }

        HandleError($"Could not gather {targetName}: {detail}");
    }

    private void ResetMapGathering(bool cancelGatherBuddy)
    {
        if (cancelGatherBuddy)
            _plugin.GatherBuddyRebornService.Cancel();

        mapGatherStep = MapGatherStep.Idle;
        mapGatherTargetItemId = 0;
        mapGatherTargetName = string.Empty;
        mapGatherInitialInventoryCount = 0;
        mapGatherReturnJob = default;
        mapGatherJobSwitchIssued = false;
        mapGatherCancelIssued = false;
        mapGatherLastCloseAttemptAt = DateTime.MinValue;
        mapGatherCloseAttemptCount = 0;
        mapGatherStepStartedAt = DateTime.MinValue;
        mapGatherNextStatusAt = DateTime.MinValue;
        mapGatherManualCommandActive = false;
    }

    private void LogMapGatherJobWaitStatus(uint currentJobId, uint expectedJobId)
    {
        if (DateTime.Now < mapGatherNextStatusAt)
            return;

        mapGatherNextStatusAt = DateTime.Now.AddSeconds(2);
        _plugin.AddDebugLog(
            $"[Gather] Waiting for gather job; targetMap={mapGatherTargetName} ({mapGatherTargetItemId}); currentJob={FormatClassJob(currentJobId)}; expectedJob={FormatClassJob(expectedJobId)}; switchIssued={mapGatherJobSwitchIssued}.");
    }

    private string BuildMapGatherTimeoutDetail()
    {
        var currentJobId = _plugin.JobSwitchService.GetCurrentClassJobId();
        var expectedJobId = _plugin.SelectedGatherJobId;
        var gatherBuddyStatus = GetGatherBuddyStatusText();
        var detail =
            $"{mapGatherStep} timed out while gathering {mapGatherTargetName}; targetMap={mapGatherTargetName} ({mapGatherTargetItemId}); currentJob={FormatClassJob(currentJobId)}; expectedJob={FormatClassJob(expectedJobId)}; GatherBuddy={gatherBuddyStatus}";
        if (mapGatherStep == MapGatherStep.ClosingGatherWindow)
            detail += $"; closeGate={DescribeMapGatherCloseGate()}; closeAttempts={mapGatherCloseAttemptCount}; cancelIssued={mapGatherCancelIssued}";
        return $"{detail}.";
    }

    private static string DescribeMapGatherCloseGate()
    {
        var visibleAddons = new List<string>();
        if (GameHelpers.IsAddonVisible("Gathering"))
            visibleAddons.Add("Gathering");
        if (GameHelpers.IsAddonVisible("GatheringMasterpiece"))
            visibleAddons.Add("GatheringMasterpiece");

        var addons = visibleAddons.Count == 0
            ? "none"
            : string.Join(",", visibleAddons);

        return $"addons={addons}; conditions=Gathering:{Plugin.Condition[ConditionFlag.Gathering]}, ExecutingGatheringAction:{Plugin.Condition[ConditionFlag.ExecutingGatheringAction]}";
    }

    private string GetGatherBuddyStatusText()
        => _plugin.GatherBuddyRebornService.TryGetAutoGatherStatus(out var status) && !string.IsNullOrWhiteSpace(status)
            ? status
            : _plugin.GatherBuddyRebornService.StatusText;

    private static string FormatClassJob(uint jobId)
        => jobId == 0
            ? "unavailable (0)"
            : $"{ClassJobOptions.GetName(jobId)} ({jobId})";

    private bool CanRunMapGatherAction(out string reason)
    {
        if (!CanRunSaddlebagAction(out reason))
            return false;

        if (Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56])
        {
            reason = "in duty";
            return false;
        }

        reason = "ready";
        return true;
    }

    private static string FormatMapIds(IReadOnlyCollection<uint> mapIds)
        => mapIds.Count == 0 ? "none" : string.Join(", ", mapIds);

    private void ResetRunCommandTriggers()
    {
        ResetPerMapCommandTriggers();
        finishCommandsRanThisRun = false;
        returnWhenDoneRanThisRun = false;
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
        ResetOpeningChestLifecycleState();
        ResetKeyItemMapRecoveryState(clearActiveKey: true);
        ResetUnderwaterLandingState();
        ResetOpeningChestCofferMemory();
        _plugin.TreasureMapLocationService.ClearCapturedLocation();
    }

    private void ResetUnderwaterLandingState()
    {
        if (descentInProgress)
        {
            CommandHelper.SendCommand("/automove off");
            GameHelpers.KeyRelease(VirtualKey.W);
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
        underwaterFlagApproachIssued = false;
        underwaterFlagApproachLogged = false;
        underwaterBounceHandoffLogged = false;
        ResetUnderwaterBounceSpecialNavigationState();
        thiefWaterRemountRecoveryActive = false;
        thiefWaterRemountRecoveryZoneWaitActive = false;
        lastUnderwaterFlagApproachTime = DateTime.MinValue;
        ResetUnderwaterFlagApproachProgressState();
        ResetPendingUnderwaterFlagApproachReissue();
        underwaterFlagApproachSurfacedFallbackActive = false;
        lastUnderwaterTriggerLoopLogTime = DateTime.MinValue;
        lastThiefMapDigSuppressedLogTime = DateTime.MinValue;
        lastThiefWaterRecoveryLogTime = DateTime.MinValue;
        underwaterTargetPosition = Vector3.Zero;
        wasDiving = false;
        nonThiefDivingIgnoredLogged = false;
        ResetUnderwaterXyzDigRetryState();
    }

    private void StartSafeDescent(string source, bool includeForward = false)
    {
        if (descentInProgress)
            return;

        descentInProgress = true;
        Task.Run(async () =>
        {
            try
            {
                if (includeForward)
                    await GameHelpers.PerformForwardDescentAsync(UnderwaterBounceAutomoveHoldMs, UnderwaterBounceDescentHoldMs);
                else
                    await GameHelpers.PerformDescentAsync();
            }
            catch (Exception ex)
            {
                Plugin.LogError($"[StateManager] {source} descent failed: {ex}");
                _plugin.AddDebugLog($"{source}: descent failed ({ex.GetType().Name}: {ex.Message}).");
            }
            finally
            {
                GameHelpers.KeyRelease(VirtualKey.W);
                GameHelpers.KeyRelease(VirtualKey.CONTROL);
                GameHelpers.KeyRelease(VirtualKey.SPACE);
                descentInProgress = false;
            }
        });
    }

    public void NotifyVnavPathFailure(string text)
    {
        if (State != BotState.Flying)
            return;

        var now = DateTime.Now;
        var shouldLog = !string.Equals(lastVnavPathFailureText, text, StringComparison.Ordinal) ||
                        now - lastVnavPathFailureTime >= OverworldRecoveryTeleportDecisionLogInterval;
        lastVnavPathFailureTime = now;
        lastVnavPathFailureText = text;
        if (shouldLog)
            _plugin.AddDebugLog($"[Flying] Observed vnav path failure: {text}");
    }

    public void NotifyChatMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        ObservePartyTeleportOffer(text);

        if (TryDeferTeleportCombatError(text))
            return;

        if (IsLifestreamDestinationNotFoundMessage(text))
        {
            var navigationFailureHandled = _plugin.NavigationService.NotifyTeleportCommandFailure(text);
            if (HandleAdsRepairRecoveryTeleportFailure(text, navigationFailureHandled))
                return;

            if (navigationFailureHandled)
                return;
        }

        if (HandlePortalTooFarChatMessage(text))
            return;

        if (HandleOpeningChestTooFarChatMessage(text))
            return;

        if (text.Contains("You discover a treasure coffer!", StringComparison.OrdinalIgnoreCase))
        {
            if (!openingChestDiscoveredByChat)
            {
                openingChestDiscoveredByChat = true;
                openingChestDiscoveredChatAt = DateTime.Now;
                chestConfirmedThisMap = true;
                _plugin.AddDebugLog("[OpeningChest] Chat evidence: treasure coffer discovered.");
            }
            return;
        }

        if (text.Contains("You open the lock on the treasure coffer!", StringComparison.OrdinalIgnoreCase))
        {
            if (!openingChestOpenedByChat)
            {
                openingChestOpenedByChat = true;
                openingChestOpenedChatAt = DateTime.Now;
                chestConfirmedThisMap = true;
                _plugin.AddDebugLog("[OpeningChest] Chat evidence: treasure coffer lock opened.");
                MarkOpeningChestManualInterventionIfNeeded("coffer lock chat");
            }
            return;
        }

        if (IsTreasurePortalChatMessage(text))
        {
            if (!openingChestPortalByChat)
            {
                openingChestPortalByChat = true;
                openingChestPortalChatAt = DateTime.Now;
                portalConfirmedThisMap = true;
                _plugin.AddDebugLog($"[OpeningChest] Chat evidence: portal message observed ('{text}').");
                MarkOpeningChestManualInterventionIfNeeded("portal chat");
            }
        }
    }

    private static bool IsLifestreamDestinationNotFoundMessage(string text)
        => text.Contains("Destination could not be found", StringComparison.OrdinalIgnoreCase);

    private bool HandleOpeningChestTooFarChatMessage(string text)
    {
        if (State != BotState.OpeningChest || !IsOpeningChestTooFarChatMessage(text))
            return false;

        var player = Plugin.ObjectTable.LocalPlayer;
        var target = Plugin.TargetManager.Target;
        var targetIsCoffer = ChestDetectionService.IsCofferObject(target);
        var yDistance = 0f;
        var distance = 0f;

        if (player != null && targetIsCoffer)
        {
            var cofferTarget = target!;
            distance = Vector3.Distance(player.Position, cofferTarget.Position);
            yDistance = Math.Abs(player.Position.Y - cofferTarget.Position.Y);
            CaptureOpeningChestCofferPosition(cofferTarget);
        }

        StopOpeningChestCofferMovement("after too-far coffer chat");
        ResetOpeningChestCofferApproachTracking();
        ResetOpeningChestInteractionTracking();
        chestDisappearedTime = DateTime.MinValue;
        lastInteractionTime = DateTime.MinValue;

        var shouldTryGroundApproach = player != null &&
                                      targetIsCoffer &&
                                      ShouldUseNearOpeningChestCofferGroundApproach(target!, player.Position, ignoreFailure: true);
        if (targetIsCoffer && !shouldTryGroundApproach && yDistance >= OpeningChestCofferMountRecoveryYDelta)
        {
            var cofferTarget = target!;
            openingChestCofferWalkFailedEntityId = cofferTarget.EntityId;
            openingChestCofferMountRecoveryActive = true;
            openingChestCofferMountRecoveryRangeReached = false;
            openingChestCofferMountRecoveryEntityId = cofferTarget.EntityId;
        }
        else
        {
            ResetOpeningChestCofferWalkFailure();
            ResetOpeningChestCofferMountRecovery("after too-far coffer chat");
        }

        _plugin.AddDebugLog(targetIsCoffer
            ? $"[OpeningChest] Too-far chat while targeting coffer at {distance:F1}y, Y {yDistance:F1}y - forcing fresh close approach."
            : "[OpeningChest] Too-far chat during coffer flow - forcing fresh close approach.");
        StateDetail = "Coffer was too far to open - re-approaching...";
        return true;
    }

    private bool HandlePortalTooFarChatMessage(string text)
    {
        if (State != BotState.Completed ||
            portalRetryStart == DateTime.MinValue ||
            !IsOpeningChestTooFarChatMessage(text))
        {
            return false;
        }

        var now = DateTime.Now;
        var target = Plugin.TargetManager.Target;
        var portal = IsPortalObject(target)
            ? target
            : FindNearestPortal(keepActivePortalWindow: true);

        if (portal == null)
        {
            ResetPortalCloseNudgeTracking(stopMovement: true);
            ResetPortalNoDialogAttemptWindow(DateTime.MinValue);
            _plugin.AddDebugLog("[Portal] Too-far chat during portal retry but no targetable portal resolved - waiting for targetable portal.");
            StateDetail = "Portal was too far - waiting for targetable portal...";
            return true;
        }

        Plugin.TargetManager.Target = portal;
        var approachPosition = CapturePortalApproachPosition(portal);
        var player = Plugin.ObjectTable.LocalPlayer;
        var distance = player == null
            ? float.MaxValue
            : Vector3.Distance(player.Position, approachPosition);
        var xzDistance = player == null
            ? float.MaxValue
            : (float)CalculateXZDistance(player.Position, approachPosition);
        var yDistance = player == null
            ? float.MaxValue
            : Math.Abs(player.Position.Y - approachPosition.Y);

        _plugin.AddDebugLog(
            $"[Portal] Too-far chat while retrying portal entry: player={FormatVectorCompact(player?.Position ?? default)} " +
            $"portal={FormatVectorCompact(approachPosition)} dist={distance:F1}y xz={xzDistance:F1}y y={yDistance:F1}y.");
        if (!HasPortalInteractionAttemptFor(portal))
        {
            _plugin.AddDebugLog($"[Portal] Too-far chat ignored for close nudge; no direct interaction attempt recorded for portal entity={portal.EntityId}.");
            StateDetail = "Portal was too far - approaching targetable portal...";
            return true;
        }

        BeginPortalCloseNudge(portal, now, "too-far chat");
        StateDetail = "Portal was too far - ground-approaching closer...";
        return true;
    }

    private static bool IsOpeningChestTooFarChatMessage(string text)
    {
        return text.Contains("too far", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("far away", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPortalObject(IGameObject? obj)
        => obj != null &&
           string.Equals(obj.Name.TextValue, "Teleportation Portal", StringComparison.Ordinal);

    private static bool IsTreasurePortalChatMessage(string text)
    {
        if (!text.Contains("portal", StringComparison.OrdinalIgnoreCase))
            return false;

        return text.Contains("appear", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("open", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("teleportation", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("arcane", StringComparison.OrdinalIgnoreCase);
    }

    private void MarkOpeningChestManualInterventionIfNeeded(string source)
    {
        if (openingChestBotInteractionAttemptedThisMap || openingChestManualInterventionSuspected)
            return;

        if (State != BotState.OpeningChest && State != BotState.Completed && portalRetryStart == DateTime.MinValue)
            return;

        openingChestManualInterventionSuspected = true;
        _plugin.AddDebugLog(
            $"[OpeningChest] {source} arrived before any LootGoblin coffer interaction attempt - marking run inconclusive/manual.");
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
        activeKeyItemRecoverySourceLogged = false;
        activeKeyItemRecoveryUnderwaterLogged = false;
        activeKeyItemRecoveryPopupShown = false;
        activeMapTargetCache.Clear();
        ResetOverworldRecoveryState(clearTeleportedTarget: true);
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
        if (!string.IsNullOrWhiteSpace(lastVnavPathFailureText))
            _plugin.AddDebugLog($"[Flying] vnav failure text: {lastVnavPathFailureText}");

        var activeTargets = ResolveOverworldNavigationTargets();
        var flagLocation = _plugin.MapFlagService.TryReadFlag();
        var hasCurrentTerritoryFlag = flagLocation != null &&
                                      flagLocation.TerritoryId == Plugin.ClientState.TerritoryType;
        if (hasCurrentTerritoryFlag)
        {
            _plugin.AddDebugLog("[Flying] vnav flyto failed - current AgentMap flag is present in-zone, falling back to /vnav flyflag.");
            if (_plugin.NavigationService.State != NavigationState.Idle)
                _plugin.NavigationService.StopNavigation();
            _plugin.NavigationService.FlyToFlag();
            StateDetail = "Flying to current map flag after vnav flyto failure...";
        }
        else if (activeTargets.NavigationTarget != Vector3.Zero)
        {
            _plugin.AddDebugLog(
                $"[Flying] Suppressed /vnav flyflag fallback because no current in-zone AgentMap flag exists; " +
                $"reissuing explicit XYZ target {FormatVectorCompact(activeTargets.NavigationTarget)}.");
            if (_plugin.NavigationService.State != NavigationState.Idle)
                _plugin.NavigationService.StopNavigation();
            _plugin.NavigationService.FlyToPosition(activeTargets.NavigationTarget, force: true);
            StateDetail = "Reissuing explicit XYZ after vnav flyto failure...";
        }
        else
        {
            HandleError("vnav flyto failed and no current map flag or explicit XYZ target is available.");
            return true;
        }

        lastStuckCheckPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        lastStuckCheckTime = now;
        return true;
    }

    private string BuildOverworldMapTargetKey(MapLocation location)
    {
        if (TryGetActiveMapTargetKey(null, out var key))
            return $"{key.EventItemId}:{key.MapItemId}";

        return FormattableString.Invariant(
            $"{SelectedMapItemId}:{location.TerritoryId}:{location.X:0.0}:{location.Y:0.0}:{location.Z:0.0}");
    }

    private static string BuildOverworldRecoveryPositionKey(string prefix, uint territoryId, Vector3 target)
    {
        return FormattableString.Invariant(
            $"{prefix}:{territoryId}:{target.X:0.0}:{target.Y:0.0}:{target.Z:0.0}");
    }

    private void ResetOverworldRecoveryState(bool clearTeleportedTarget = false)
    {
        overworldRecoveryTargetKey = string.Empty;
        overworldRecoveryTerritoryId = 0;
        overworldRecoveryTarget = Vector3.Zero;
        overworldRecoveryLastPosition = Vector3.Zero;
        overworldRecoveryBestDistance = float.MaxValue;
        overworldRecoveryLastProgressTime = DateTime.MinValue;
        overworldRecoveryLastRepathTime = DateTime.MinValue;
        overworldRecoveryLastNavmeshWaitLogTime = DateTime.MinValue;
        overworldRecoveryLastTeleportDecisionLogTime = DateTime.MinValue;
        overworldRecoveryLastTeleportDecision = string.Empty;
        overworldRecoveryRepathCount = 0;
        if (clearTeleportedTarget)
            overworldRecoveryTeleportedTargetKey = string.Empty;
    }

    private bool TryRunOverworldRecoveryWatchdog(
        DateTime now,
        string source,
        string targetKind,
        string targetKey,
        uint territoryId,
        Vector3 target,
        OverworldRecoveryNavigationKind navigationKind)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var currentPos = player?.Position ?? Vector3.Zero;
        if (player == null ||
            currentPos == Vector3.Zero ||
            target == Vector3.Zero ||
            territoryId == 0 ||
            Plugin.ClientState.TerritoryType != territoryId)
        {
            ResetOverworldRecoveryState();
            return false;
        }

        var distanceFromTarget = Vector3.Distance(currentPos, target);
        if (TryHandleCloseMapTargetOverworldRecovery(source, targetKind, territoryId, currentPos))
            return true;

        if (distanceFromTarget <= OverworldRecoveryArrivedDistance)
        {
            ResetOverworldRecoveryState();
            return false;
        }

        var targetChanged = !string.Equals(overworldRecoveryTargetKey, targetKey, StringComparison.Ordinal) ||
                            overworldRecoveryTerritoryId != territoryId ||
                            Vector3.DistanceSquared(overworldRecoveryTarget, target) > 1.0f;
        if (targetChanged)
        {
            overworldRecoveryTargetKey = targetKey;
            overworldRecoveryTerritoryId = territoryId;
            overworldRecoveryTarget = target;
            overworldRecoveryLastPosition = currentPos;
            overworldRecoveryBestDistance = distanceFromTarget;
            overworldRecoveryLastProgressTime = now;
            overworldRecoveryLastRepathTime = DateTime.MinValue;
            overworldRecoveryLastNavmeshWaitLogTime = DateTime.MinValue;
            overworldRecoveryLastTeleportDecisionLogTime = DateTime.MinValue;
            overworldRecoveryLastTeleportDecision = string.Empty;
            overworldRecoveryRepathCount = 0;
            _plugin.AddDebugLog(
                $"[{source}][Recovery] Tracking {targetKind}; key={targetKey}; territory={territoryId}; " +
                $"target={FormatVectorCompact(target)}; current={FormatVectorCompact(currentPos)}; distance={distanceFromTarget:F1}y.");
            return false;
        }

        if (distanceFromTarget + OverworldRecoveryProgressMargin < overworldRecoveryBestDistance)
        {
            overworldRecoveryBestDistance = distanceFromTarget;
            overworldRecoveryLastProgressTime = now;
            overworldRecoveryLastPosition = currentPos;
            overworldRecoveryLastRepathTime = DateTime.MinValue;
            overworldRecoveryRepathCount = 0;
            return false;
        }

        if (_plugin.NavigationService.State == NavigationState.WaitingForNavmesh)
        {
            overworldRecoveryLastProgressTime = now;
            overworldRecoveryLastPosition = currentPos;
            StateDetail = _plugin.NavigationService.StateDetail;

            if (now - overworldRecoveryLastNavmeshWaitLogTime >= OverworldRecoveryTeleportDecisionLogInterval)
            {
                overworldRecoveryLastNavmeshWaitLogTime = now;
                _plugin.AddDebugLog(
                    $"[{source}][Recovery] Holding {targetKind} recovery while vnavmesh is building the navmesh; " +
                    $"currentDist={distanceFromTarget:F1}y; target={FormatVectorCompact(target)}.");
            }

            return true;
        }

        var noProgressFor = now - overworldRecoveryLastProgressTime;
        if (noProgressFor < OverworldRecoveryNoProgressRepathTimeout)
            return false;

        var pathfindInProgress = _plugin.VNavIPC.TryIsPathfindInProgress();
        var pathRunning = _plugin.VNavIPC.TryIsPathRunning();
        var navState = _plugin.NavigationService.State;
        var movedDistance = Vector3.Distance(currentPos, overworldRecoveryLastPosition);
        var shouldTeleport = overworldRecoveryRepathCount >= OverworldRecoveryTeleportRepathThreshold ||
                             noProgressFor >= OverworldRecoveryNoProgressTeleportTimeout;

        if (shouldTeleport &&
            TryTeleportForOverworldRecovery(
                now,
                source,
                targetKind,
                targetKey,
                territoryId,
                target,
                currentPos,
                distanceFromTarget,
                movedDistance,
                noProgressFor,
                pathfindInProgress,
                pathRunning,
                navState))
        {
            return true;
        }

        if (overworldRecoveryLastRepathTime != DateTime.MinValue &&
            now - overworldRecoveryLastRepathTime < OverworldRecoveryNoProgressRepathTimeout)
        {
            return false;
        }

        if (_plugin.NavigationService.State != NavigationState.Idle)
            _plugin.NavigationService.StopNavigation();

        IssueOverworldRecoveryNavigation(navigationKind, target);
        autoMoveActive = true;
        overworldRecoveryLastRepathTime = now;
        overworldRecoveryRepathCount++;

        _plugin.AddDebugLog(
            $"[{source}][Recovery] No progress to {targetKind} for {noProgressFor.TotalSeconds:F1}s - stopped and reissued {DescribeOverworldRecoveryNavigation(navigationKind)}. " +
            $"moved={movedDistance:F1}y; currentDist={distanceFromTarget:F1}y; bestDist={overworldRecoveryBestDistance:F1}y; " +
            $"repath={overworldRecoveryRepathCount}; nav={navState}; pathfind={FormatNullableBool(pathfindInProgress)}; " +
            $"pathRunning={FormatNullableBool(pathRunning)}; target={FormatVectorCompact(target)}.");
        StateDetail = $"Recovering {targetKind}: re-pathing ({distanceFromTarget:F1}y)...";
        return true;
    }

    private bool TryHandleCloseMapTargetOverworldRecovery(
        string source,
        string targetKind,
        uint territoryId,
        Vector3 currentPos)
    {
        if (!IsOverworldRecoveryMapTarget(targetKind) ||
            currentLandingMode != OverworldLandingMode.MountToggle ||
            CurrentLocation == null ||
            CurrentLocation.TerritoryId != territoryId ||
            Plugin.ClientState.TerritoryType != territoryId)
        {
            return false;
        }

        if (State == BotState.OpeningChest ||
            TryDescribeActiveOpeningChestCofferEvidence(includeLiveScan: false, out _))
        {
            return false;
        }

        if (!TryGetCurrentMapLandingDistance(out var landingDistance, out var landingTarget, out var landingBasis))
            return false;

        var landingRange = GetCurrentMapLandingHoldRange();
        if (landingDistance > landingRange)
            return false;

        StopOutdoorMapFlowRecoveryNavigation();
        if (!TryHandleMapLandingAndDig(
                $"[{source}][Recovery] close map target",
                landingBasis,
                currentPos,
                landingTarget,
                landingDistance))
        {
            return false;
        }

        _plugin.AddDebugLog(
            $"[{source}][Recovery] Stuck map target teleport suppressed: already within landing range " +
            $"({landingDistance:F1}y XZ <= {landingRange:F1}y) of {FormatVectorCompact(landingTarget)} ({landingBasis}).");
        ResetOverworldRecoveryState();
        return true;
    }

    private void IssueOverworldRecoveryNavigation(OverworldRecoveryNavigationKind navigationKind, Vector3 target)
    {
        if (navigationKind == OverworldRecoveryNavigationKind.FlyTo)
            _plugin.NavigationService.FlyToPosition(target, force: true);
        else
            _plugin.NavigationService.MoveToPosition(target);
    }

    private bool TryTeleportForOverworldRecovery(
        DateTime now,
        string source,
        string targetKind,
        string targetKey,
        uint territoryId,
        Vector3 target,
        Vector3 currentPos,
        float distanceFromTarget,
        float movedDistance,
        TimeSpan noProgressFor,
        bool? pathfindInProgress,
        bool? pathRunning,
        NavigationState navState)
    {
        var blockedReason = GetOverworldRecoveryTeleportBlockReason(
            targetKind,
            targetKey,
            territoryId,
            target,
            currentPos,
            distanceFromTarget,
            out var aetheryteId,
            out var aetheryteName,
            out var playerDistanceToAetheryte);
        if (blockedReason != null)
        {
            LogOverworldRecoveryTeleportDecision(
                now,
                source,
                targetKind,
                targetKey,
                $"blocked: {blockedReason}",
                movedDistance,
                distanceFromTarget,
                noProgressFor,
                pathfindInProgress,
                pathRunning,
                navState);
            return false;
        }

        overworldRecoveryTeleportedTargetKey = targetKey;
        if (_plugin.NavigationService.State != NavigationState.Idle)
            _plugin.NavigationService.StopNavigation();
        autoMoveActive = false;

        if (CurrentLocation != null)
        {
            CurrentLocation.NearestAetheryteId = aetheryteId;
            CurrentLocation.NearestAetheryteName = aetheryteName;
        }

        _plugin.AddDebugLog(
            $"[{source}][Recovery] Teleporting to nearest safe aetheryte for stuck {targetKind}: {aetheryteName} (ID {aetheryteId}). " +
            $"noProgress={noProgressFor.TotalSeconds:F1}s; moved={movedDistance:F1}y; currentDist={distanceFromTarget:F1}y; " +
            $"bestDist={overworldRecoveryBestDistance:F1}y; repath={overworldRecoveryRepathCount}; " +
            $"playerToAetheryte={playerDistanceToAetheryte:F1}y; nav={navState}; pathfind={FormatNullableBool(pathfindInProgress)}; " +
            $"pathRunning={FormatNullableBool(pathRunning)}; current={FormatVectorCompact(currentPos)}; target={FormatVectorCompact(target)}.");
        overworldRecoveryRequiresPartyMountWait = true;
        ResetOverworldRecoveryState();
        TransitionTo(BotState.Teleporting, $"{source}: stuck {targetKind} - teleporting to nearest aetheryte...");
        return true;
    }

    private string? GetOverworldRecoveryTeleportBlockReason(
        string targetKind,
        string targetKey,
        uint territoryId,
        Vector3 target,
        Vector3 currentPos,
        float distanceFromTarget,
        out uint aetheryteId,
        out string aetheryteName,
        out double playerDistanceToAetheryte)
    {
        aetheryteId = 0;
        aetheryteName = string.Empty;
        playerDistanceToAetheryte = double.MaxValue;

        if (string.Equals(overworldRecoveryTeleportedTargetKey, targetKey, StringComparison.Ordinal))
            return "already teleported once for target";

        if (CurrentLocation == null)
            return "no current map location";

        if (CurrentLocation.TerritoryId != territoryId ||
            Plugin.ClientState.TerritoryType != territoryId)
        {
            return $"territory mismatch current={Plugin.ClientState.TerritoryType} target={territoryId}";
        }

        if (distanceFromTarget <= OverworldRecoveryArrivedDistance)
            return $"target within {OverworldRecoveryArrivedDistance:F1}y";

        if (Plugin.Condition[ConditionFlag.InCombat])
            return "in combat";

        if (Plugin.Condition[ConditionFlag.BetweenAreas] ||
            Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            return "loading";
        }

        if (_plugin.NavigationService.IsTeleporting())
            return "already teleporting";

        if (!_plugin.IsLifestreamAvailable)
            return "Lifestream unavailable";

        if ((State == BotState.OpeningChest || IsOpeningChestCofferRecoveryTargetKind(targetKind)) &&
            TryDescribeActiveOpeningChestCofferEvidence(includeLiveScan: true, out var cofferEvidence))
        {
            return $"opening chest coffer evidence active: {cofferEvidence}";
        }

        if (IsOverworldRecoveryMapTarget(targetKind) &&
            TryGetMapTargetTeleportBlockReason(currentPos, out var mapTargetBlockReason))
        {
            return mapTargetBlockReason;
        }

        aetheryteId = _plugin.NavigationService.FindNearestAetheryte(territoryId, target, out _, out _);
        if (aetheryteId == 0)
            return "no same-zone aetheryte";

        aetheryteName = GetAetheryteName(aetheryteId);
        var maybePlayerDistanceToAetheryte = _plugin.NavigationService.GetPlayerXZDistanceToAetheryte(aetheryteId);
        if (maybePlayerDistanceToAetheryte == null)
            return $"cannot verify distance to selected aetheryte {aetheryteId}";

        playerDistanceToAetheryte = maybePlayerDistanceToAetheryte.Value;
        if (playerDistanceToAetheryte <= SameZoneAetheryteTeleportSkipXZRange)
        {
            return $"already {playerDistanceToAetheryte:F1}y from selected aetheryte {aetheryteId}";
        }

        return null;
    }

    private static bool IsOverworldRecoveryMapTarget(string targetKind)
        => string.Equals(targetKind, "map target", StringComparison.Ordinal);

    private static bool IsOpeningChestCofferRecoveryTargetKind(string targetKind)
        => string.Equals(targetKind, "coffer", StringComparison.Ordinal) ||
           string.Equals(targetKind, "displaced coffer", StringComparison.Ordinal) ||
           string.Equals(targetKind, "captured coffer", StringComparison.Ordinal) ||
           string.Equals(targetKind, "missing coffer flag", StringComparison.Ordinal);

    private bool TryDescribeActiveOpeningChestCofferEvidence(bool includeLiveScan, out string evidence)
    {
        evidence = string.Empty;

        if (includeLiveScan)
        {
            var liveCoffer = FindTargetableOverworldCoffer(OverworldRecoveryObjectSearchRange);
            if (liveCoffer != null)
            {
                var player = Plugin.ObjectTable.LocalPlayer;
                var distance = player == null
                    ? float.MaxValue
                    : Vector3.Distance(player.Position, liveCoffer.Position);
                evidence =
                    $"live targetable coffer entity={liveCoffer.EntityId} dist={distance:F1}y " +
                    $"xyz={FormatVectorCompact(liveCoffer.Position)}";
                return true;
            }
        }

        if (openingChestDiscoveredByChat && !HasOpeningChestCofferCompletionEvidence())
        {
            var age = openingChestDiscoveredChatAt == DateTime.MinValue
                ? "unknown"
                : $"{(DateTime.Now - openingChestDiscoveredChatAt).TotalSeconds:F1}s";
            evidence = $"coffer discovery chat age={age}";
            return true;
        }

        if (!HasOpeningChestCofferCompletionEvidence() &&
            TryGetOpeningChestLastKnownCofferPosition(out var knownPosition, out var knownDistance))
        {
            evidence =
                $"captured coffer XYZ entity={openingChestLastKnownCofferEntityId} " +
                $"dist={knownDistance:F1}y xyz={FormatVectorCompact(knownPosition)}";
            return true;
        }

        return false;
    }

    private string GetAetheryteName(uint aetheryteId)
    {
        try
        {
            var aetheryteSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
            if (aetheryteSheet != null)
            {
                var aetheryte = aetheryteSheet.GetRow(aetheryteId);
                return aetheryte.PlaceName.ValueNullable?.Name.ToString() ?? $"ID {aetheryteId}";
            }
        }
        catch
        {
        }

        return $"ID {aetheryteId}";
    }

    private void LogOverworldRecoveryTeleportDecision(
        DateTime now,
        string source,
        string targetKind,
        string targetKey,
        string decision,
        float movedDistance,
        float distanceFromTarget,
        TimeSpan noProgressFor,
        bool? pathfindInProgress,
        bool? pathRunning,
        NavigationState navState)
    {
        var decisionKey = $"{targetKey}:{decision}";
        if (string.Equals(overworldRecoveryLastTeleportDecision, decisionKey, StringComparison.Ordinal) &&
            now - overworldRecoveryLastTeleportDecisionLogTime < OverworldRecoveryTeleportDecisionLogInterval)
        {
            return;
        }

        overworldRecoveryLastTeleportDecision = decisionKey;
        overworldRecoveryLastTeleportDecisionLogTime = now;
        _plugin.AddDebugLog(
            $"[{source}][Recovery] Teleport decision for {targetKind}: {decision}. " +
            $"noProgress={noProgressFor.TotalSeconds:F1}s; moved={movedDistance:F1}y; currentDist={distanceFromTarget:F1}y; " +
            $"bestDist={overworldRecoveryBestDistance:F1}y; repath={overworldRecoveryRepathCount}; " +
            $"nav={navState}; pathfind={FormatNullableBool(pathfindInProgress)}; pathRunning={FormatNullableBool(pathRunning)}.");
    }

    private static string DescribeOverworldRecoveryNavigation(OverworldRecoveryNavigationKind navigationKind)
    {
        return navigationKind == OverworldRecoveryNavigationKind.FlyTo ? "fly path" : "move path";
    }

    private static string FormatNullableBool(bool? value)
    {
        return value.HasValue ? (value.Value ? "true" : "false") : "null";
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

    private void RunReturnWhenDoneOnce(string reason)
    {
        if (returnWhenDoneRanThisRun || !_plugin.Configuration.ReturnWhenDoneEnabled)
            return;

        returnWhenDoneRanThisRun = true;
        var command = _plugin.Configuration.ReturnWhenDoneDestination switch
        {
            ReturnWhenDoneDestination.Personal => "/li home",
            ReturnWhenDoneDestination.Inn => "/li inn",
            _ => "/li fc",
        };

        if (CommandHelper.TrySendCommand(command))
            _plugin.AddDebugLog($"[ReturnWhenDone] Sent {command} for {reason}.");
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

            if (IsCombatAutomationCommand(trimmed))
            {
                if (SendCombatAutomationCommand(trimmed, $"command trigger for {reason}"))
                    sent++;
                continue;
            }

            if (CommandHelper.TrySendCommand(trimmed))
                sent++;
        }

        if (sent > 0)
            _plugin.AddDebugLog($"[CommandTrigger] Sent {sent} command(s) for {reason}.");
    }

    private static bool IsCombatAutomationCommand(string command) =>
        command.StartsWith("/bmrai", StringComparison.OrdinalIgnoreCase) ||
        command.StartsWith("/vbmai", StringComparison.OrdinalIgnoreCase);

    private bool SendCombatAutomationCommand(string command, string reason)
    {
        if (!IsCombatAutomationCommandAvailable(command, out var unavailableReason))
        {
            var logKey = $"{command}:{unavailableReason}";
            if (loggedUnavailableCombatAutomationCommands.Add(logKey))
                _plugin.AddDebugLog($"[CombatAutomation] Skipped {command} for {reason}: {unavailableReason}.");
            return false;
        }

        return CommandHelper.TrySendCommand(command);
    }

    private bool IsCombatAutomationCommandAvailable(string command, out string reason)
    {
        if (command.StartsWith("/bmrai", StringComparison.OrdinalIgnoreCase) &&
            !_plugin.RotationPluginIPC.IsBossModRebornAvailable)
        {
            reason = "BossModReborn plugin is not loaded";
            return false;
        }

        if (command.StartsWith("/vbmai", StringComparison.OrdinalIgnoreCase) &&
            !_plugin.RotationPluginIPC.IsVbmAvailable)
        {
            reason = "VBM plugin is not loaded";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryDigWhileDiving(string reason)
    {
        if (!Plugin.Condition[ConditionFlag.Diving])
            return false;

        if (!CanUseUnderwaterNavigation())
            return false;

        if ((DateTime.Now - lastDigTime).TotalSeconds < 3.0)
            return false;

        RunLandingCommandsOnce(reason);
        CommandHelper.SendCommand("/gaction dig");
        lastDigTime = DateTime.Now;
        digIssuedThisMap = true;
        digIssuedAt = lastDigTime;
        _plugin.AddDebugLog($"{reason}: issued /gaction dig while diving.");
        return true;
    }

    private bool TryDigThiefMapWhileDivingAtGate(
        string reason,
        DateTime now,
        Vector3 currentPos,
        Vector3 activeTarget,
        double xzDistance)
    {
        if (!CanUseUnderwaterNavigation())
            return TryDigWhileDiving(reason);

        if (!Plugin.Condition[ConditionFlag.Diving])
            return false;

        if (IsWithinThiefMapDigGate(activeTarget, xzDistance)
            || IsExplicitThiefMapDescentFallbackDig(activeTarget, xzDistance))
        {
            return TryDigWhileDiving(reason);
        }

        LogSuppressedThiefMapDig(now, reason, currentPos, activeTarget, xzDistance);
        return false;
    }

    private static bool IsWithinThiefMapDigGate(Vector3 activeTarget, double xzDistance)
    {
        return activeTarget != Vector3.Zero
            && xzDistance <= UnderwaterFlagApproachArrivalXZRange;
    }

    private bool IsExplicitThiefMapDescentFallbackDig(Vector3 activeTarget, double xzDistance)
    {
        if (!descentInProgress && !descentMode)
            return false;

        if (HasPendingUnderwaterFlagApproachReissue()
            || underwaterFlagApproachSurfacedFallbackActive)
        {
            return false;
        }

        if (activeTarget != Vector3.Zero
            && xzDistance > UnderwaterFlagApproachArrivalXZRange)
        {
            return false;
        }

        return activeTarget == Vector3.Zero || underwaterBounceHandoffLogged;
    }

    private void LogSuppressedThiefMapDig(
        DateTime now,
        string reason,
        Vector3 currentPos,
        Vector3 activeTarget,
        double xzDistance)
    {
        if (now - lastThiefMapDigSuppressedLogTime < ThiefMapDigSuppressionLogInterval)
            return;

        lastThiefMapDigSuppressedLogTime = now;
        var targetText = activeTarget == Vector3.Zero ? "none" : FormatVectorCompact(activeTarget);
        var xzText = double.IsNaN(xzDistance) || xzDistance == double.MaxValue
            ? "n/a"
            : $"{xzDistance:F1}y";

        _plugin.AddDebugLog(
            $"[Underwater] Suppressed thief-map dig during XYZ pathing: reason={reason}; " +
            $"current={FormatVectorCompact(currentPos)}; target={targetText}; xz={xzText}; " +
            $"arrival={UnderwaterFlagApproachArrivalXZRange:F1}y; nav={_plugin.NavigationService.State}; " +
            $"pending={FormatUnderwaterFlagApproachPending(now)}.");
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
        if (dismountAttemptStart == DateTime.MinValue)
        {
            dismountAttemptStart = now;
            stateActionIssued = true;
            _plugin.NavigationService.StopNavigation();
            _plugin.AddDebugLog(
                $"{reason}: landing phase ready via {landingBasis}; landingXZ={xzDist:F1}y; " +
                $"current={FormatVectorCompact(currentPos)}; landingTarget={FormatVectorCompact(landingTarget)}");
        }

        if (TryHoldForOverworldMapContentPartyWait(10.0))
        {
            if (!descentInProgress)
                StartSafeDescent($"{reason} party wait");
            return true;
        }

        if (descentInProgress)
        {
            GameHelpers.KeyRelease(VirtualKey.CONTROL);
            GameHelpers.KeyRelease(VirtualKey.SPACE);
            descentInProgress = false;
        }

        if (Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.InFlight])
        {
            var dismountElapsed = (now - dismountAttemptStart).TotalSeconds;
            _mountService.TryLandingToggle();
            StateDetail = $"Landing by /mount toggle... ({dismountElapsed:F0}s)";
            return true;
        }

        if (Plugin.Condition[ConditionFlag.Mounting71])
            return true;

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
                Plugin.LogError($"[StateManager] ContinueWith exception in TransitionTo (dig handoff): {ex.Message}");
            }
        }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);

        return true;
    }

    private bool TryHoldForOverworldMapContentPartyWait(double maxDistance)
    {
        if (!_plugin.Configuration.PartyWaitBeforeDismount)
            return false;

        if (joinedFateMapProgressBypassPartyWait)
        {
            overworldRecoveryRequiresPartyMountWait = false;
            LogLandingPartyWaitOnce(
                "JoinedFateRecovery:content-bypass",
                "[PartyWait][OverworldLanding] Joined-FATE recovery bypassing party wait for current map progress.");
            return false;
        }

        var partyWait = EvaluatePartyProximityGate(maxDistance, "OverworldMapContent");
        if (partyWait.CanProceed)
            return false;

        StateDetail = BuildOverworldLandingPartyWaitDetail(partyWait, maxDistance);
        return true;
    }

    private bool IsThiefUnderwaterPartyWaitContext()
    {
        return CanUseUnderwaterNavigation();
    }

    private bool ShouldWaitForPartyBeforeTakeoffForCurrentMap()
    {
        return IsThiefUnderwaterPartyWaitContext()
            ? _plugin.Configuration.WaitForPartyForThiefMapsUnderwater
            : _plugin.Configuration.WaitForParty;
    }

    private string GetCurrentTakeoffPartyWaitSettingName()
    {
        return IsThiefUnderwaterPartyWaitContext()
            ? "WaitForPartyForThiefMapsUnderwater"
            : "WaitForParty";
    }

    private bool ShouldWaitForUnderwaterMapContentParty()
    {
        return IsThiefUnderwaterPartyWaitContext()
            && _plugin.Configuration.WaitForPartyForThiefMapsUnderwater;
    }

    private bool TryHoldForUnderwaterMapContentPartyWait(double maxDistance)
    {
        if (!ShouldWaitForUnderwaterMapContentParty())
            return false;

        if (joinedFateMapProgressBypassPartyWait)
        {
            overworldRecoveryRequiresPartyMountWait = false;
            LogLandingPartyWaitOnce(
                "JoinedFateRecovery:underwater-content-bypass",
                "[PartyWait][UnderwaterTrigger] Joined-FATE recovery bypassing party wait for current thief-map progress.");
            return false;
        }

        var partyWait = EvaluatePartyProximityGate(maxDistance, "UnderwaterMapContent");
        if (partyWait.CanProceed)
            return false;

        PauseUnderwaterBounceDescentForPartyWait();
        StateDetail = BuildOverworldLandingPartyWaitDetail(partyWait, maxDistance);
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

    private bool IsThiefUnderwaterLandingMode()
    {
        return IsThiefMap(SelectedMapItemId)
            && currentLandingMode == OverworldLandingMode.UnderwaterBounce;
    }

    private void NormalizeLandingModeForSelectedMap(string source)
    {
        var expectedLandingMode = ResolveLandingMode(SelectedMapItemId);
        if (currentLandingMode == expectedLandingMode)
            return;

        var correctedStaleUnderwaterMode =
            SelectedMapItemId != 0 &&
            !IsThiefMap(SelectedMapItemId) &&
            currentLandingMode == OverworldLandingMode.UnderwaterBounce &&
            expectedLandingMode == OverworldLandingMode.MountToggle;

        currentLandingMode = expectedLandingMode;

        if (expectedLandingMode != OverworldLandingMode.UnderwaterBounce)
            ResetUnderwaterLandingState();

        if (correctedStaleUnderwaterMode)
        {
            _plugin.AddDebugLog(
                $"{source} [Landing][WARN] Corrected stale UnderwaterBounce for non-thief map ID {SelectedMapItemId}; using MountToggle.");
        }
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

        var correctedStaleUnderwaterMode = currentLandingMode == OverworldLandingMode.UnderwaterBounce;
        if (correctedStaleUnderwaterMode
            || descentInProgress
            || descentMode
            || underwaterTargetPosition != Vector3.Zero
            || wasDiving)
        {
            currentLandingMode = OverworldLandingMode.MountToggle;
            ResetUnderwaterLandingState();
            if (correctedStaleUnderwaterMode)
            {
                _plugin.AddDebugLog(
                    $"[Underwater][WARN] Corrected stale UnderwaterBounce while diving for non-thief map ID {SelectedMapItemId}; using MountToggle.");
            }
        }

        if (!nonThiefDivingIgnoredLogged)
        {
            nonThiefDivingIgnoredLogged = true;
            _plugin.AddDebugLog($"[Underwater] Ignoring Diving for non-thief map ID {SelectedMapItemId}; underwater navigation is Thief's Map only.");
        }

        return false;
    }

    private bool IsMountedOrMounting()
    {
        return Plugin.Condition[ConditionFlag.Mounted]
            || Plugin.Condition[ConditionFlag.Mounting71];
    }

    private bool IsMountedOrActualInFlight()
    {
        return _plugin.NavigationService.IsMounted()
            || Plugin.Condition[ConditionFlag.InFlight];
    }

    private bool IsPortaPraetoriaTakeoffNudgeLocation()
    {
        return Plugin.ClientState.TerritoryType == LochsTerritoryId
            && CurrentLocation?.TerritoryId == LochsTerritoryId
            && CurrentLocation.NearestAetheryteId == PortaPraetoriaAetheryteId;
    }

    private void QueuePortaPraetoriaTakeoffNudgeIfNeeded(uint currentTerritory, uint expectedTerritory)
    {
        if (currentTerritory != LochsTerritoryId ||
            expectedTerritory != LochsTerritoryId ||
            CurrentLocation?.NearestAetheryteId != PortaPraetoriaAetheryteId ||
            _plugin.NavigationService.LastTeleportAetheryteId != PortaPraetoriaAetheryteId)
        {
            return;
        }

        portaPraetoriaTakeoffNudgePending = true;
        portaPraetoriaTakeoffNudgeActive = false;
        portaPraetoriaTakeoffNudgeStartedAt = DateTime.MinValue;
        _plugin.AddDebugLog("[PortaPraetoria] Teleport arrival confirmed; queued takeoff nudge after mount/party wait.");
    }

    private bool TryHandlePortaPraetoriaTakeoffNudge(
        Vector3 currentPos,
        Vector3 landingTarget,
        string basis,
        string destinationText,
        string zoneName)
    {
        if (!portaPraetoriaTakeoffNudgePending && !portaPraetoriaTakeoffNudgeActive)
            return false;

        if (stateActionIssued && !portaPraetoriaTakeoffNudgeActive)
        {
            ResetPortaPraetoriaTakeoffNudge("[PortaPraetoria] flight nav already issued", stopAutomove: true);
            return false;
        }

        if (!IsPortaPraetoriaTakeoffNudgeLocation())
        {
            ResetPortaPraetoriaTakeoffNudge("[PortaPraetoria] no longer at Porta Praetoria route", stopAutomove: true);
            return false;
        }

        if (currentPos == Vector3.Zero || landingTarget == Vector3.Zero)
        {
            StateDetail = "Porta Praetoria takeoff nudge waiting for map target...";
            return true;
        }

        var distanceToTarget = CalculateXZDistance(currentPos, landingTarget);
        if (distanceToTarget <= GetCurrentMapLandingHoldRange())
        {
            ResetPortaPraetoriaTakeoffNudge("[PortaPraetoria] target already within landing range", stopAutomove: true);
            return false;
        }

        var loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                      Plugin.Condition[ConditionFlag.BetweenAreas51];
        var player = Plugin.ObjectTable.LocalPlayer;
        var ready = !loading &&
                    player != null &&
                    !player.IsCasting &&
                    !Plugin.Condition[ConditionFlag.Casting] &&
                    !Plugin.Condition[ConditionFlag.Mounting71] &&
                    _plugin.NavigationService.IsMounted();
        if (!ready)
        {
            StateDetail = "Porta Praetoria takeoff nudge waiting for mounted player readiness...";
            return true;
        }

        var now = DateTime.Now;
        if (!portaPraetoriaTakeoffNudgeActive)
        {
            portaPraetoriaTakeoffNudgeActive = true;
            portaPraetoriaTakeoffNudgeStartedAt = now;
            CommandHelper.SendCommand($"/target \"{PortaPraetoriaTakeoffNudgeTargetName}\"");
            CommandHelper.SendCommand("/lockon");
            CommandHelper.SendCommand("/automove on");
            _plugin.AddDebugLog(
                $"[PortaPraetoria] Starting takeoff nudge: /target \"{PortaPraetoriaTakeoffNudgeTargetName}\", /lockon, /automove on; " +
                $"target={destinationText} - {zoneName}; basis={basis}; distance={distanceToTarget:F1}y.");
        }

        var elapsed = now - portaPraetoriaTakeoffNudgeStartedAt;
        if (elapsed < PortaPraetoriaTakeoffNudgeDuration)
        {
            var remaining = PortaPraetoriaTakeoffNudgeDuration - elapsed;
            StateDetail = $"Porta Praetoria takeoff nudge toward {PortaPraetoriaTakeoffNudgeTargetName} ({remaining.TotalSeconds:F1}s)...";
            return true;
        }

        CommandHelper.SendCommand("/automove off");
        _plugin.AddDebugLog(
            $"[PortaPraetoria] Takeoff nudge complete: /automove off after {elapsed.TotalSeconds:F1}s; normal vnav flight may start next tick.");
        ClearPortaPraetoriaTakeoffNudge();
        StateDetail = "Porta Praetoria takeoff nudge complete; preparing vnav flight...";
        return true;
    }

    private void ClearPortaPraetoriaTakeoffNudge()
    {
        portaPraetoriaTakeoffNudgePending = false;
        portaPraetoriaTakeoffNudgeActive = false;
        portaPraetoriaTakeoffNudgeStartedAt = DateTime.MinValue;
    }

    private void ResetPortaPraetoriaTakeoffNudge(string reason, bool stopAutomove)
    {
        var hadNudge = portaPraetoriaTakeoffNudgePending || portaPraetoriaTakeoffNudgeActive;
        if (!hadNudge)
            return;

        if (portaPraetoriaTakeoffNudgeActive && stopAutomove)
            CommandHelper.SendCommand("/automove off");

        ClearPortaPraetoriaTakeoffNudge();
        _plugin.AddDebugLog($"{reason}; reset Porta Praetoria takeoff nudge.");
    }

    private void ResetTeleportLifecycleTracking()
    {
        teleportCommandIssuedAt = DateTime.MinValue;
        teleportDelayStartedAt = DateTime.MinValue;
        teleportOriginPosition = Vector3.Zero;
        teleportSawBetweenAreas = false;
        teleportLastLoadingAt = DateTime.MinValue;
        teleportLoadingClearedAt = DateTime.MinValue;
    }

    private int EstimateExpectedPartyMemberCount()
    {
        var partyListCount = Plugin.PartyList.Length;
        if (partyListCount <= 0)
            return Math.Max(1, _plugin.PartyService.LastValidMemberCount);

        var localPlayerEntityId = Plugin.ObjectTable.LocalPlayer?.EntityId ?? 0;
        if (localPlayerEntityId == 0)
            return Math.Max(partyListCount, _plugin.PartyService.LastValidMemberCount);

        for (var i = 0; i < Plugin.PartyList.Length; i++)
        {
            var member = Plugin.PartyList[i];
            if (member != null && member.EntityId == localPlayerEntityId)
                return Math.Max(partyListCount, _plugin.PartyService.LastValidMemberCount);
        }

        return Math.Max(partyListCount + 1, _plugin.PartyService.LastValidMemberCount);
    }

    private void CaptureWaitingForPartyExpectedMemberCount()
    {
        var priorLastValidMemberCount = _plugin.PartyService.LastValidMemberCount;
        var snapshotValid = _plugin.PartyService.UpdatePartyStatus();
        var snapshotCount = _plugin.PartyService.PartyMembers.Count;
        waitingForPartyExpectedMemberCount = Math.Max(
            1,
            Math.Max(Math.Max(snapshotCount, priorLastValidMemberCount), EstimateExpectedPartyMemberCount()));

        var mounted = _plugin.PartyService.PartyMembers.Count(member =>
            !member.IsLocalPlayer && IsLoadedSameTerritoryMounted(member));
        var seenOthers = _plugin.PartyService.PartyMembers.Count(member => !member.IsLocalPlayer);
        var expectedOthers = Math.Max(0, waitingForPartyExpectedMemberCount - 1);
        var requiredOthers = ResolvePartyMountWaitRequiredOthers(Math.Max(seenOthers, expectedOthers));
        var message =
            $"[PartyWait][Mount] Entered wait; expected={waitingForPartyExpectedMemberCount}, " +
            $"snapshotValid={snapshotValid}, mountedOthers={mounted}/{requiredOthers}, seenOthers={seenOthers}.";
        _log.Info(message);
        _plugin.AddDebugLog(message);
    }

    private int ResolvePartyMountWaitRequiredOthers(int totalOthers)
    {
        if (totalOthers <= 0)
            return 0;

        if (_plugin.Configuration.PartyWaitBeforeDismountUseCountThreshold)
        {
            return PartyGateSemantics.ResolveRequiredOthers(
                totalOthers,
                true,
                _plugin.Configuration.PartyWaitBeforeDismountRequiredOthers);
        }

        return _plugin.Configuration.RequireAllMounted ? totalOthers : 1;
    }

    private PartyMountWaitGate EvaluatePartyMountWaitGate()
    {
        var priorLastValidMemberCount = _plugin.PartyService.LastValidMemberCount;
        var snapshotValid = _plugin.PartyService.UpdatePartyStatus();
        var total = _plugin.PartyService.PartyMembers.Count;
        var seenOthers = _plugin.PartyService.PartyMembers.Count(member => !member.IsLocalPlayer);

        if (waitingForPartyExpectedMemberCount <= 0 && total > 0)
            waitingForPartyExpectedMemberCount = total;
        if (total > waitingForPartyExpectedMemberCount)
            waitingForPartyExpectedMemberCount = total;

        var expectedTotal = Math.Max(
            Math.Max(total, waitingForPartyExpectedMemberCount),
            Math.Max(priorLastValidMemberCount, EstimateExpectedPartyMemberCount()));
        var expectedOthers = Math.Max(0, expectedTotal - 1);
        var totalOthers = Math.Max(seenOthers, expectedOthers);
        var mountedOthers = _plugin.PartyService.PartyMembers.Count(member =>
            !member.IsLocalPlayer && IsLoadedSameTerritoryMounted(member));
        var requiredOthers = ResolvePartyMountWaitRequiredOthers(totalOthers);
        var unavailableNames = _plugin.PartyService.PartyMembers
            .Where(member =>
                !member.IsLocalPlayer &&
                !PartyGateSemantics.IsLoadedSameTerritory(member.IsLoaded, member.TerritoryStatus))
            .Select(member => string.IsNullOrWhiteSpace(member.Name) ? "Unknown" : member.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var canProceed = requiredOthers == 0 || (snapshotValid && mountedOthers >= requiredOthers);

        return new PartyMountWaitGate(
            snapshotValid,
            mountedOthers,
            seenOthers,
            totalOthers,
            expectedOthers,
            requiredOthers,
            canProceed,
            unavailableNames);
    }

    private bool ShouldHoldSameTerritoryTakeoffForParty(out PartyMountWaitGate gate)
    {
        gate = default;

        if (!ShouldWaitForPartyBeforeTakeoffForCurrentMap())
            return false;

        if (State is not (BotState.Idle
            or BotState.DetectingLocation
            or BotState.Teleporting
            or BotState.Mounting
            or BotState.WaitingForParty))
        {
            return false;
        }

        gate = EvaluatePartyMountWaitGate();
        return !gate.CanProceed;
    }

    private bool TryHoldSameTerritoryTakeoffForParty(string logPrefix)
    {
        if (!ShouldHoldSameTerritoryTakeoffForParty(out var gate))
            return false;

        var detail = $"Waiting for party before takeoff ({gate.MountedOthers}/{gate.RequiredOthers} required mounted others)...";
        var settingName = GetCurrentTakeoffPartyWaitSettingName();
        var mode = _plugin.Configuration.PartyWaitBeforeDismountUseCountThreshold
            ? "threshold"
            : _plugin.Configuration.RequireAllMounted ? "full-party" : "any-mounted";
        _plugin.AddDebugLog(
            $"{logPrefix} Already mounted, but {settingName} is enabled and party mount wait is not satisfied " +
            $"({gate.MountedOthers}/{gate.RequiredOthers} required others, mode={mode}, seenOthers={gate.SeenOthers}) - holding before flight.");

        if (State == BotState.WaitingForParty)
            StateDetail = detail;
        else
            TransitionTo(BotState.WaitingForParty, detail);

        return true;
    }

    private void LogThiefWaterInfo(string message)
    {
        _plugin.AddDebugLog(message);
        _log.Information(message);
    }

    private void LogThiefWaterInfoRateLimited(ref DateTime lastLogTime, TimeSpan interval, string message)
    {
        var now = DateTime.Now;
        if (now - lastLogTime < interval)
            return;

        lastLogTime = now;
        LogThiefWaterInfo(message);
    }

    private string BuildThiefWaterRemountZoneWaitDetail(IReadOnlyCollection<string> unavailableNames)
    {
        var total = _plugin.PartyService.PartyMembers.Count;
        var loaded = Math.Max(0, total - unavailableNames.Count);
        var missingText = unavailableNames.Count == 0
            ? "none"
            : string.Join(", ", unavailableNames);

        return $"Thief-map remount: waiting for party zone load ({loaded}/{total} loaded in same zone; missing: {missingText})...";
    }

    private void ResumeThiefWaterTravelAfterRemount(string detail)
    {
        thiefWaterRemountRecoveryZoneWaitActive = false;
        lastDivingCheck = DateTime.MinValue;
        TransitionTo(BotState.Flying, detail);
    }

    private bool TryRecoverThiefWaterTravelPosture(
        bool isDiving,
        Vector3 currentPos,
        Vector3 landingTarget,
        string targetBasis,
        string destinationText,
        string zoneName,
        bool alreadyDivingNewMapTarget = false)
    {
        if (!IsThiefUnderwaterLandingMode()
            || IsMountedOrMounting()
            || Plugin.Condition[ConditionFlag.InFlight]
            || currentPos == Vector3.Zero
            || landingTarget == Vector3.Zero)
        {
            return false;
        }

        if (IsUnderwaterBounceSpecialEntryHandoffActive())
            return false;

        var xzDist = CalculateXZDistance(currentPos, landingTarget);
        if (xzDist <= UnderwaterBounceTriggerXZRange || xzDist <= MapDigXZRange)
            return false;

        var postureMessage = isDiving
            ? (alreadyDivingNewMapTarget
                ? "[Underwater] Already diving far from new thief-map target; remounting before travel."
                : "[Underwater] Already diving far from thief-map target; remounting before travel.")
            : "[Underwater] Diving lost far from thief-map target; suppressing on-foot flyto.";

        LogThiefWaterInfoRateLimited(
            ref lastThiefWaterRecoveryLogTime,
            ThiefWaterRecoveryLogInterval,
            $"{postureMessage} " +
            $"xz={xzDist:F1}y; current={FormatVectorCompact(currentPos)}; " +
            $"landingTarget={FormatVectorCompact(landingTarget)}; basis={targetBasis}; " +
            $"{destinationText} - {zoneName}.");

        _plugin.NavigationService.StopNavigation();
        GameHelpers.StopAutoMove();
        autoMoveActive = false;
        ResetUnderwaterLandingState();
        thiefWaterRemountRecoveryActive = true;
        mountAttemptStart = DateTime.MinValue;
        mountAttempts = 0;
        lastDivingCheck = DateTime.MinValue;

        LogThiefWaterInfo(
            $"[Underwater] Remount recovery started for {(isDiving ? "already-diving" : "lost-diving")} thief-map travel; " +
            $"will not path on foot/swimming at {xzDist:F1}y from target.");
        TransitionTo(BotState.Mounting, isDiving
            ? "Thief-map water travel: remounting before far target..."
            : "Thief-map water recovery: remounting before travel...");
        return true;
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
                _plugin.AddDebugLog($"[Landing][WARN] Corrected stale UnderwaterBounce for non-thief map ID {SelectedMapItemId}; using MountToggle.");
            }
            return false;
        }

        if (!IsThiefUnderwaterLandingMode())
        {
            if (!ShouldAssumeThiefMapFromDiving())
                return false;

            currentLandingMode = OverworldLandingMode.UnderwaterBounce;
            LogThiefWaterInfo("[Underwater] Diving detected for thief map - enabling thief-map trigger flow");
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

        if (IsThiefUnderwaterLandingMode()
            && currentEntry != null
            && destinationIndex > 0)
        {
            var specialNav = _plugin.SpecialNavigationDatabase.FindEntry(destinationIndex);
            if (specialNav != null)
            {
                return ResolveUnderwaterBounceSpecialNavigationTarget(
                    specialNav,
                    currentEntry,
                    destinationIndex,
                    out basis);
            }
        }

        if (currentEntry != null && currentEntry.HasRealXYZ)
        {
            basis = "stored RealXYZ";
            return GetStoredRealTarget(currentEntry);
        }

        if (IsThiefUnderwaterLandingMode())
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

        if (activeUnderwaterBounceSpecialDestinationIndex > 0
            && ShouldUseUnderwaterBounceSpecialFinalTarget(activeUnderwaterBounceSpecialDestinationIndex))
        {
            return targets.LandingTarget;
        }

        var approachY = currentPos.Y;
        if (ShouldUseLochsDivingFlagApproachOffset())
        {
            approachY -= LochsDivingFlagApproachDepthOffset;
            basis += " + Lochs dive-depth offset";
        }

        return new Vector3(targets.LandingTarget.X, approachY, targets.LandingTarget.Z);
    }

    private bool ShouldUseLochsDivingFlagApproachOffset()
    {
        return Plugin.Condition[ConditionFlag.Diving]
            && Plugin.ClientState.TerritoryType == LochsTerritoryId
            && CurrentLocation?.TerritoryId == LochsTerritoryId;
    }

    private void ResetUnderwaterBounceSpecialNavigationState()
    {
        activeUnderwaterBounceSpecialDestinationIndex = -1;
        activeUnderwaterBounceSpecialEntryReached = false;
    }

    private void TrackUnderwaterBounceSpecialNavigation(int destinationIndex)
    {
        if (activeUnderwaterBounceSpecialDestinationIndex == destinationIndex)
            return;

        activeUnderwaterBounceSpecialDestinationIndex = destinationIndex;
        activeUnderwaterBounceSpecialEntryReached = false;
    }

    private static Vector3 GetStoredRealTarget(MapLocationEntry entry)
    {
        return new Vector3(entry.RealX, entry.RealY, entry.RealZ);
    }

    private static Vector3 GetSpecialNavigationEntryTarget(SpecialNavigationEntry specialNav)
    {
        return new Vector3(specialNav.PreX, specialNav.PreY, specialNav.PreZ);
    }

    private static Vector3 GetSpecialNavigationMainFallbackTarget(SpecialNavigationEntry specialNav)
    {
        return new Vector3(specialNav.MainX, specialNav.MainY, specialNav.MainZ);
    }

    private static Vector3 ResolveSpecialNavigationFinalTarget(
        SpecialNavigationEntry specialNav,
        MapLocationEntry? currentEntry,
        out string basis)
    {
        if (currentEntry != null && currentEntry.HasRealXYZ)
        {
            basis = "stored RealXYZ";
            return GetStoredRealTarget(currentEntry);
        }

        basis = "special navigation";
        return GetSpecialNavigationMainFallbackTarget(specialNav);
    }

    private Vector3 ResolveUnderwaterBounceSpecialNavigationTarget(
        SpecialNavigationEntry specialNav,
        MapLocationEntry? currentEntry,
        int destinationIndex,
        out string basis)
    {
        TrackUnderwaterBounceSpecialNavigation(destinationIndex);

        if (!ShouldUseUnderwaterBounceSpecialFinalTarget(destinationIndex))
        {
            basis = "special navigation entry";
            return GetSpecialNavigationEntryTarget(specialNav);
        }

        return ResolveSpecialNavigationFinalTarget(specialNav, currentEntry, out basis);
    }

    private bool ShouldUseUnderwaterBounceSpecialFinalTarget(int destinationIndex)
    {
        return Plugin.Condition[ConditionFlag.Diving]
            || wasDiving
            || underwaterBounceHandoffLogged
            || underwaterTargetPosition != Vector3.Zero
            || descentInProgress
            || descentMode
            || dismountAttemptStart != DateTime.MinValue
            || (activeUnderwaterBounceSpecialEntryReached
                && activeUnderwaterBounceSpecialDestinationIndex == destinationIndex);
    }

    private bool IsUnderwaterBounceSpecialEntryHandoffActive()
    {
        return activeUnderwaterBounceSpecialDestinationIndex > 0
            && activeUnderwaterBounceSpecialEntryReached
            && (descentInProgress
                || descentMode
                || dismountAttemptStart != DateTime.MinValue
                || wasDiving
                || underwaterTargetPosition != Vector3.Zero);
    }

    private static bool IsSpecialNavigationEntryBasis(string basis)
    {
        return string.Equals(basis, "special navigation entry", StringComparison.Ordinal);
    }

    private void MarkUnderwaterBounceSpecialEntryReachedIfNeeded(
        Vector3 currentPos,
        Vector3 entryTarget,
        string basis,
        string destinationText,
        string zoneName)
    {
        if (!IsSpecialNavigationEntryBasis(basis)
            || activeUnderwaterBounceSpecialDestinationIndex <= 0
            || activeUnderwaterBounceSpecialEntryReached)
        {
            return;
        }

        activeUnderwaterBounceSpecialEntryReached = true;
        _plugin.AddDebugLog(
            $"[Underwater] Reached special navigation entry for {destinationText} - {zoneName}; " +
            $"switching thief-map dive target to final XYZ. " +
            $"entry={FormatVectorCompact(entryTarget)}; current={FormatVectorCompact(currentPos)}");
    }

    private void ResetUnderwaterFlagApproachProgressState()
    {
        lastUnderwaterFlagApproachTarget = Vector3.Zero;
        bestUnderwaterFlagApproachXZ = double.MaxValue;
        lastUnderwaterFlagApproachProgressTime = DateTime.MinValue;
        lastUnderwaterFlagApproachHeartbeatTime = DateTime.MinValue;
        lastUnderwaterFlagApproachSamplePosition = Vector3.Zero;
        lastUnderwaterFlagApproachSampleTime = DateTime.MinValue;
        lastUnderwaterFlagApproachForceReflyTime = DateTime.MinValue;
        underwaterFlagApproachReissueCount = 0;
        lastUnderwaterFlagApproachObjectDeferredLogTime = DateTime.MinValue;
    }

    private bool HasPendingUnderwaterFlagApproachReissue()
    {
        return pendingUnderwaterFlagApproachTarget != Vector3.Zero
            && pendingUnderwaterFlagApproachScheduledAt != DateTime.MinValue;
    }

    private void ResetPendingUnderwaterFlagApproachReissue()
    {
        pendingUnderwaterFlagApproachTarget = Vector3.Zero;
        pendingUnderwaterFlagApproachReason = string.Empty;
        pendingUnderwaterFlagApproachXZ = double.MaxValue;
        pendingUnderwaterFlagApproachScheduledAt = DateTime.MinValue;
        pendingUnderwaterFlagApproachPriorNavState = NavigationState.Idle;
        lastUnderwaterFlagApproachPendingWaitLogTime = DateTime.MinValue;
        lastUnderwaterFlagApproachDisabledLogTime = DateTime.MinValue;
    }

    private bool HasActiveUnderwaterFlagApproach()
    {
        return IsThiefUnderwaterLandingMode()
            && (wasDiving
                || underwaterFlagApproachIssued
                || underwaterTargetPosition != Vector3.Zero
                || HasPendingUnderwaterFlagApproachReissue()
                || underwaterFlagApproachSurfacedFallbackActive);
    }

    private bool HasLoggableUnderwaterFlagApproach()
    {
        return IsThiefUnderwaterLandingMode()
            && State is not BotState.Idle
            && (underwaterTargetPosition != Vector3.Zero
                || HasPendingUnderwaterFlagApproachReissue());
    }

    private static string FormatVnavRunning(bool? vnavRunning)
    {
        return vnavRunning.HasValue
            ? (vnavRunning.Value ? "true" : "false")
            : "unknown";
    }

    private bool CanContinueUnderwaterFlagApproach(DateTime now)
    {
        if (_plugin.Configuration.Enabled
            && State is not (BotState.Idle or BotState.Error or BotState.Completed))
        {
            return true;
        }

        if (!_plugin.Configuration.Enabled && HasActiveUnderwaterFlagApproach())
            LogUnderwaterFlagApproachDisabledAbandoned(now);

        return false;
    }

    private void LogUnderwaterFlagApproachDisabledAbandoned(DateTime now)
    {
        if (!HasLoggableUnderwaterFlagApproach())
            return;

        if (now - lastUnderwaterFlagApproachDisabledLogTime < ThiefWaterRecoveryLogInterval)
            return;

        lastUnderwaterFlagApproachDisabledLogTime = now;
        var vnavRunning = _plugin.VNavIPC.TryIsRunning();
        LogThiefWaterInfo(
            $"[Underwater] Active thief-map flag approach abandoned because LootGoblin is disabled; " +
            $"state={State}; nav={_plugin.NavigationService.State}; vnavRunning={FormatVnavRunning(vnavRunning)}; " +
            $"target={FormatVectorCompact(underwaterTargetPosition)}; pending={FormatUnderwaterFlagApproachPending(now)}.");
    }

    private string FormatUnderwaterFlagApproachBestXZ()
    {
        return bestUnderwaterFlagApproachXZ == double.MaxValue
            ? "n/a"
            : $"{bestUnderwaterFlagApproachXZ:F1}y";
    }

    private string FormatUnderwaterFlagApproachPending(DateTime now)
    {
        if (!HasPendingUnderwaterFlagApproachReissue())
            return "none";

        var queuedXzText = pendingUnderwaterFlagApproachXZ == double.MaxValue
            ? "n/a"
            : $"{pendingUnderwaterFlagApproachXZ:F1}y";
        var dueInMs = Math.Max(0.0, (pendingUnderwaterFlagApproachScheduledAt - now).TotalMilliseconds);

        return
            $"{pendingUnderwaterFlagApproachReason}; " +
            $"target={FormatVectorCompact(pendingUnderwaterFlagApproachTarget)}; " +
            $"queuedXZ={queuedXzText}; " +
            $"scheduled={pendingUnderwaterFlagApproachScheduledAt:HH:mm:ss.fff}; " +
            $"dueIn={dueInMs:F0}ms; " +
            $"priorNav={pendingUnderwaterFlagApproachPriorNavState}";
    }

    private void LogUnderwaterFlagApproachEvent(
        DateTime now,
        string action,
        string reason,
        Vector3 currentPos,
        Vector3 target,
        double xzDistance,
        NavigationState navState,
        bool? vnavRunning = null)
    {
        vnavRunning ??= _plugin.VNavIPC.TryIsRunning();
        LogThiefWaterInfo(
            $"[Underwater] Thief-map flag approach {action}: reason={reason}; issue=#{underwaterFlagApproachReissueCount}; " +
            $"state={State}; isDiving={Plugin.Condition[ConditionFlag.Diving]}; " +
            $"current={FormatVectorCompact(currentPos)}; target={FormatVectorCompact(target)}; " +
            $"xz={xzDistance:F1}y; bestXZ={FormatUnderwaterFlagApproachBestXZ()}; nav={navState}; vnavRunning={FormatVnavRunning(vnavRunning)}; " +
            $"pending={FormatUnderwaterFlagApproachPending(now)}.");
    }

    private void LogUnderwaterFlagApproachHeartbeat(
        DateTime now,
        Vector3 currentPos,
        Vector3 target,
        double xzDistance,
        NavigationState navState,
        bool? vnavRunning = null)
    {
        if (xzDistance <= UnderwaterFlagApproachArrivalXZRange
            || now - lastUnderwaterFlagApproachHeartbeatTime < UnderwaterFlagApproachHeartbeatInterval)
        {
            return;
        }

        lastUnderwaterFlagApproachHeartbeatTime = now;
        LogUnderwaterFlagApproachEvent(now, "heartbeat", "approach", currentPos, target, xzDistance, navState, vnavRunning);
    }

    private void PauseUnderwaterBounceDescentUntilFlagArrival()
    {
        if (!descentInProgress)
            return;

        CommandHelper.SendCommand("/automove off");
        GameHelpers.KeyRelease(VirtualKey.W);
        GameHelpers.KeyRelease(VirtualKey.CONTROL);
        GameHelpers.KeyRelease(VirtualKey.SPACE);
        descentInProgress = false;
        lastUnderwaterBounceDescentStart = DateTime.MinValue;
        _plugin.AddDebugLog("[Underwater] Paused thief-map descent until flag X/Z arrival.");
    }

    private void PauseUnderwaterBounceDescentForPartyWait()
    {
        if (!descentInProgress)
            return;

        CommandHelper.SendCommand("/automove off");
        GameHelpers.KeyRelease(VirtualKey.W);
        GameHelpers.KeyRelease(VirtualKey.CONTROL);
        GameHelpers.KeyRelease(VirtualKey.SPACE);
        descentInProgress = false;
        lastUnderwaterBounceDescentStart = DateTime.MinValue;
        _plugin.AddDebugLog("[Underwater] Paused thief-map descent until party wait clears.");
    }

    private void TrackUnderwaterFlagApproachProgress(DateTime now, Vector3 target, double xzDistance)
    {
        if (target == Vector3.Zero)
            return;

        if (lastUnderwaterFlagApproachTarget == Vector3.Zero
            || Vector3.Distance(lastUnderwaterFlagApproachTarget, target) >= 1.0f)
        {
            lastUnderwaterFlagApproachTarget = target;
            bestUnderwaterFlagApproachXZ = xzDistance;
            lastUnderwaterFlagApproachProgressTime = now;
            ResetUnderwaterFlagApproachStallSample();
            return;
        }

        if (bestUnderwaterFlagApproachXZ == double.MaxValue
            || xzDistance <= bestUnderwaterFlagApproachXZ - UnderwaterFlagApproachProgressMargin)
        {
            bestUnderwaterFlagApproachXZ = xzDistance;
            lastUnderwaterFlagApproachProgressTime = now;
        }
    }

    private void ResetUnderwaterFlagApproachStallSample()
    {
        lastUnderwaterFlagApproachSamplePosition = Vector3.Zero;
        lastUnderwaterFlagApproachSampleTime = DateTime.MinValue;
    }

    private void SetUnderwaterFlagApproachStallSample(DateTime now, Vector3 currentPos)
    {
        lastUnderwaterFlagApproachSamplePosition = currentPos;
        lastUnderwaterFlagApproachSampleTime = now;
    }

    private static bool IsWithinUnderwaterFlagApproachStallThreshold(Vector3 currentPos, Vector3 samplePos)
    {
        return Math.Abs(currentPos.X - samplePos.X) < UnderwaterFlagApproachStallMovementThreshold
            && Math.Abs(currentPos.Y - samplePos.Y) < UnderwaterFlagApproachStallMovementThreshold
            && Math.Abs(currentPos.Z - samplePos.Z) < UnderwaterFlagApproachStallMovementThreshold;
    }

    private bool CanRecoverActiveUnderwaterFlagApproach()
    {
        if (!_plugin.Configuration.Enabled
            || State is BotState.Idle or BotState.Error or BotState.Completed
            || !Plugin.Condition[ConditionFlag.Diving]
            || HasPendingUnderwaterFlagApproachReissue()
            || descentInProgress
            || descentMode
            || dismountAttemptStart != DateTime.MinValue)
        {
            return false;
        }

        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
            return false;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || player.IsCasting || Plugin.Condition[ConditionFlag.Casting])
            return false;

        return !Plugin.Condition[ConditionFlag.Occupied]
            && !Plugin.Condition[ConditionFlag.OccupiedInQuestEvent]
            && !Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]
            && !Plugin.Condition[ConditionFlag.Occupied33]
            && !Plugin.Condition[ConditionFlag.Occupied39]
            && !Plugin.Condition[ConditionFlag.WatchingCutscene];
    }

    private void IssueActiveUnderwaterFlagApproach(
        DateTime now,
        string reason,
        Vector3 currentPos,
        Vector3 target,
        double xzDistance,
        bool force)
    {
        if (!CanContinueUnderwaterFlagApproach(now))
            return;

        _plugin.NavigationService.FlyToPosition(target, force: force);
        underwaterFlagApproachIssued = true;
        lastUnderwaterFlagApproachTime = now;
        lastUnderwaterFlagApproachProgressTime = now;
        underwaterFlagApproachReissueCount++;
        SetUnderwaterFlagApproachStallSample(now, currentPos);
        LogUnderwaterFlagApproachEvent(
            now,
            force ? "force flyto issued" : "flyto issued",
            reason,
            currentPos,
            target,
            xzDistance,
            _plugin.NavigationService.State);
    }

    private bool TryRecoverActiveUnderwaterFlagApproachStall(
        DateTime now,
        Vector3 currentPos,
        Vector3 target,
        double xzDistance)
    {
        if (target == Vector3.Zero || xzDistance <= UnderwaterFlagApproachArrivalXZRange)
        {
            ResetUnderwaterFlagApproachStallSample();
            return false;
        }

        if (!underwaterFlagApproachIssued)
        {
            SetUnderwaterFlagApproachStallSample(now, currentPos);
            return false;
        }

        if (lastUnderwaterFlagApproachSampleTime == DateTime.MinValue)
        {
            SetUnderwaterFlagApproachStallSample(now, currentPos);
            return false;
        }

        if (!IsWithinUnderwaterFlagApproachStallThreshold(currentPos, lastUnderwaterFlagApproachSamplePosition))
        {
            SetUnderwaterFlagApproachStallSample(now, currentPos);
            return false;
        }

        if (now - lastUnderwaterFlagApproachSampleTime < UnderwaterFlagApproachStallTimeout
            || now - lastUnderwaterFlagApproachForceReflyTime < UnderwaterFlagApproachForceReflyCooldown
            || !CanRecoverActiveUnderwaterFlagApproach())
        {
            return false;
        }

        lastUnderwaterFlagApproachForceReflyTime = now;
        IssueActiveUnderwaterFlagApproach(
            now,
            "underwater approach stalled",
            currentPos,
            target,
            xzDistance,
            force: true);
        return true;
    }

    private string? GetUnderwaterFlagApproachRetryReason(
        DateTime now,
        NavigationState navState,
        bool targetYRefreshed,
        string? forcedReason = null)
    {
        if (HasPendingUnderwaterFlagApproachReissue())
            return null;

        if (!CanContinueUnderwaterFlagApproach(now))
            return null;

        if (!string.IsNullOrEmpty(forcedReason))
            return forcedReason;

        if (!underwaterFlagApproachIssued)
            return "initial";

        if (targetYRefreshed)
            return "target-y-refresh";

        if (navState == NavigationState.Error
            && now - lastUnderwaterFlagApproachTime >= UnderwaterFlagApproachReissueInterval)
        {
            return "nav error";
        }

        if (navState == NavigationState.Idle
            && now - lastUnderwaterFlagApproachTime >= UnderwaterFlagApproachReissueInterval)
        {
            return "nav idle";
        }

        if (lastUnderwaterFlagApproachProgressTime != DateTime.MinValue
            && now - lastUnderwaterFlagApproachProgressTime >= UnderwaterFlagApproachStallTimeout)
        {
            return "stalled";
        }

        return null;
    }

    private void ScheduleUnderwaterFlagApproachReissue(
        DateTime now,
        string reason,
        Vector3 currentPos,
        Vector3 target,
        double xzDistance,
        NavigationState navState)
    {
        if (!CanContinueUnderwaterFlagApproach(now))
            return;

        if (Plugin.Condition[ConditionFlag.Diving])
        {
            IssueActiveUnderwaterFlagApproach(now, reason, currentPos, target, xzDistance, force: true);
            return;
        }

        if (HasPendingUnderwaterFlagApproachReissue())
        {
            LogUnderwaterFlagApproachEvent(now, "pending wait", reason, currentPos, pendingUnderwaterFlagApproachTarget, xzDistance, navState);
            return;
        }

        pendingUnderwaterFlagApproachTarget = target;
        pendingUnderwaterFlagApproachReason = reason;
        pendingUnderwaterFlagApproachXZ = xzDistance;
        pendingUnderwaterFlagApproachScheduledAt = now + UnderwaterFlagApproachReissueStopDelay;
        pendingUnderwaterFlagApproachPriorNavState = navState;
        lastUnderwaterFlagApproachPendingWaitLogTime = DateTime.MinValue;

        LogUnderwaterFlagApproachEvent(now, "retry scheduled", reason, currentPos, target, xzDistance, navState);
        _plugin.NavigationService.StopNavigation();
        autoMoveActive = false;
        LogUnderwaterFlagApproachEvent(now, "stop issued", reason, currentPos, target, xzDistance, _plugin.NavigationService.State);
        lastUnderwaterFlagApproachPendingWaitLogTime = now;
        LogUnderwaterFlagApproachEvent(now, "pending wait", reason, currentPos, target, xzDistance, _plugin.NavigationService.State);
    }

    private bool TryHandlePendingUnderwaterFlagApproachReissue(
        DateTime now,
        Vector3 currentPos,
        Vector3 latestTarget,
        double latestXZDistance)
    {
        if (!HasPendingUnderwaterFlagApproachReissue())
            return false;

        if (!CanContinueUnderwaterFlagApproach(now))
        {
            ResetPendingUnderwaterFlagApproachReissue();
            return true;
        }

        if (latestTarget != Vector3.Zero
            && Vector3.Distance(pendingUnderwaterFlagApproachTarget, latestTarget) >= 1.0f)
        {
            pendingUnderwaterFlagApproachTarget = latestTarget;
            pendingUnderwaterFlagApproachXZ = latestXZDistance;
            if (!pendingUnderwaterFlagApproachReason.Contains("target-y-refresh", StringComparison.Ordinal))
                pendingUnderwaterFlagApproachReason += "+target-y-refresh";

            LogUnderwaterFlagApproachEvent(
                now,
                "pending target refreshed",
                pendingUnderwaterFlagApproachReason,
                currentPos,
                pendingUnderwaterFlagApproachTarget,
                latestXZDistance,
                _plugin.NavigationService.State);
        }

        var pendingTarget = pendingUnderwaterFlagApproachTarget;
        var currentXZDistance = pendingTarget == Vector3.Zero
            ? latestXZDistance
            : CalculateXZDistance(currentPos, pendingTarget);

        if (now < pendingUnderwaterFlagApproachScheduledAt)
        {
            if (now - lastUnderwaterFlagApproachPendingWaitLogTime >= UnderwaterFlagApproachPendingWaitLogInterval)
            {
                lastUnderwaterFlagApproachPendingWaitLogTime = now;
                LogUnderwaterFlagApproachEvent(
                    now,
                    "pending wait",
                    pendingUnderwaterFlagApproachReason,
                    currentPos,
                    pendingTarget,
                    currentXZDistance,
                    _plugin.NavigationService.State);
            }

            return true;
        }

        var pendingReason = pendingUnderwaterFlagApproachReason;
        _plugin.NavigationService.FlyToPosition(pendingTarget);
        underwaterFlagApproachIssued = true;
        lastUnderwaterFlagApproachTime = now;
        lastUnderwaterFlagApproachProgressTime = now;
        underwaterFlagApproachReissueCount++;
        LogUnderwaterFlagApproachEvent(
            now,
            "retry fired",
            pendingReason,
            currentPos,
            pendingTarget,
            currentXZDistance,
            _plugin.NavigationService.State);
        ResetPendingUnderwaterFlagApproachReissue();
        return true;
    }

    private void FireUnderwaterFlagApproachImmediately(
        DateTime now,
        string reason,
        Vector3 currentPos,
        Vector3 target,
        double xzDistance)
    {
        if (!CanContinueUnderwaterFlagApproach(now))
            return;

        _plugin.NavigationService.FlyToPosition(target);
        underwaterFlagApproachIssued = true;
        lastUnderwaterFlagApproachTime = now;
        lastUnderwaterFlagApproachProgressTime = now;
        underwaterFlagApproachReissueCount++;
        LogUnderwaterFlagApproachEvent(
            now,
            "flyto issued",
            reason,
            currentPos,
            target,
            xzDistance,
            _plugin.NavigationService.State);
    }

    private void IssueUnderwaterFlagApproach(
        DateTime now,
        string reason,
        Vector3 currentPos,
        Vector3 target,
        double xzDistance)
    {
        if (!CanContinueUnderwaterFlagApproach(now))
            return;

        var navState = _plugin.NavigationService.State;
        var shouldSchedule = underwaterFlagApproachIssued
            || navState != NavigationState.Idle
            || reason != "initial";

        if (shouldSchedule)
        {
            ScheduleUnderwaterFlagApproachReissue(now, reason, currentPos, target, xzDistance, navState);
            return;
        }

        FireUnderwaterFlagApproachImmediately(now, reason, currentPos, target, xzDistance);
    }

    private bool TryRefreshUnderwaterFlagApproachTargetY(Vector3 currentPos, out string basis, out string destinationText, out string zoneName)
    {
        var refreshedTarget = ResolveUnderwaterFlagApproachTarget(currentPos, out basis, out destinationText, out zoneName);
        if (refreshedTarget == Vector3.Zero)
            return false;

        if (underwaterTargetPosition == Vector3.Zero)
        {
            underwaterTargetPosition = refreshedTarget;
            return false;
        }

        if (Math.Abs(refreshedTarget.Y - underwaterTargetPosition.Y) < UnderwaterFlagApproachTargetYRefreshThreshold)
            return false;

        underwaterTargetPosition = refreshedTarget;
        return true;
    }

    private bool TryGetCurrentLochsThiefDiveSpecialNavigation(
        out SpecialNavigationEntry? specialNav,
        out MapLocationEntry? currentEntry,
        out int destinationIndex)
    {
        specialNav = null;
        currentEntry = null;
        destinationIndex = -1;

        if (CurrentLocation == null
            || !IsThiefUnderwaterLandingMode()
            || CurrentLocation.TerritoryId != LochsTerritoryId)
        {
            return false;
        }

        currentEntry = _plugin.MapLocationDatabase.FindEntry(CurrentLocation.TerritoryId, CurrentLocation.X, CurrentLocation.Z);
        destinationIndex = currentEntry?.Index > 0
            ? currentEntry.Index
            : activeUnderwaterBounceSpecialDestinationIndex;

        if (!LochsThiefDiveSpecialDestinationIndices.Contains(destinationIndex))
            return false;

        specialNav = _plugin.SpecialNavigationDatabase.FindEntry(destinationIndex);
        return specialNav != null;
    }

    private bool TrySuppressLochsSpecialSurfacedFallbackRetry(DateTime now, Vector3 currentPos)
    {
        if (!TryGetCurrentLochsThiefDiveSpecialNavigation(out var specialNav, out var currentEntry, out var destinationIndex)
            || specialNav == null)
        {
            return false;
        }

        var entryTarget = GetSpecialNavigationEntryTarget(specialNav);
        var finalTarget = ResolveSpecialNavigationFinalTarget(specialNav, currentEntry, out var finalTargetBasis);
        var entryXZ = CalculateXZDistance(currentPos, entryTarget);
        var finalXZ = CalculateXZDistance(currentPos, finalTarget);

        if (finalXZ <= UnderwaterFlagApproachArrivalXZRange)
            return false;

        underwaterFlagApproachSurfacedFallbackActive = false;
        underwaterTargetPosition = Vector3.Zero;
        ResetPendingUnderwaterFlagApproachReissue();

        SuppressUnderwaterBounceVnav();

        var withinEntryHandoff = entryXZ <= UnderwaterBounceTriggerXZRange;
        var withinLandingHandoff = finalXZ <= UnderwaterBounceTriggerXZRange;
        var keepDescent = withinEntryHandoff
            || withinLandingHandoff
            || descentInProgress
            || descentMode
            || dismountAttemptStart != DateTime.MinValue;

        if (keepDescent)
        {
            if (TryHandleNonDivingThiefMapLandingAfterDescent(
                    now,
                    currentPos,
                    finalTarget,
                    finalXZ,
                    "[Underwater] thief-map surfaced recovery"))
            {
                return true;
            }

            var descentPulseIssued = EnsureUnderwaterBounceDescent(now, currentPos);
            var digIssued = TryDigThiefMapWhileDivingAtGate("[Underwater] thief-map trigger", now, currentPos, finalTarget, finalXZ);
            LogUnderwaterTriggerLoop(now, currentPos, Math.Min(entryXZ, finalXZ), descentPulseIssued, digIssued);
            StateDetail =
                $"Holding Lochs thief-map dive entry... (entry {entryXZ:F1}y, target {finalXZ:F1}y)";
        }
        else
        {
            StateDetail =
                $"Waiting for Lochs thief-map dive recovery... (entry {entryXZ:F1}y, target {finalXZ:F1}y)";
        }

        LogThiefWaterInfoRateLimited(
            ref lastThiefWaterRecoveryLogTime,
            ThiefWaterRecoveryLogInterval,
            $"[Underwater] Suppressed surfaced fallback flyto for Lochs special navigation #{destinationIndex}; " +
            $"waiting for Diving or entry/target handoff range. " +
            $"current={FormatVectorCompact(currentPos)}; entry={FormatVectorCompact(entryTarget)} ({entryXZ:F1}y); " +
            $"target={FormatVectorCompact(finalTarget)} ({finalXZ:F1}y, {finalTargetBasis}).");

        return true;
    }

    private bool TryHandleSurfacedUnderwaterFlagApproachFallback(DateTime now, Vector3 currentPos)
    {
        if (TrySuppressLochsSpecialSurfacedFallbackRetry(now, currentPos))
            return true;

        var targets = ResolveOverworldNavigationTargets();
        var destination = targets.LandingTarget;
        if (destination == Vector3.Zero)
        {
            ResetPendingUnderwaterFlagApproachReissue();
            EnsureUnderwaterBounceDescent(now, currentPos);
            StateDetail = "Descending for underwater thief-map trigger...";
            return true;
        }

        var destinationXZ = CalculateXZDistance(currentPos, destination);
        if (destinationXZ <= UnderwaterFlagApproachArrivalXZRange)
        {
            underwaterFlagApproachSurfacedFallbackActive = false;
            underwaterTargetPosition = Vector3.Zero;
            ResetPendingUnderwaterFlagApproachReissue();

            if (!underwaterBounceHandoffLogged)
            {
                underwaterBounceHandoffLogged = true;
                LogThiefWaterInfo(
                    $"[Underwater] Reached thief-map flag X/Z; handing off to descent/dig loop at " +
                    $"{FormatVectorCompact(currentPos)}.");
            }

            SuppressUnderwaterBounceVnav();
            if (TryHandleNonDivingThiefMapLandingAfterDescent(
                    now,
                    currentPos,
                    destination,
                    destinationXZ,
                    "[Underwater] thief-map surfaced recovery"))
            {
                return true;
            }

            var descentPulseIssued = EnsureUnderwaterBounceDescent(now, currentPos);
            var digIssued = TryDigThiefMapWhileDivingAtGate("[Underwater] thief-map trigger", now, currentPos, destination, destinationXZ);
            LogUnderwaterTriggerLoop(now, currentPos, destinationXZ, descentPulseIssued, digIssued);
            StateDetail = "Descending for underwater thief-map trigger...";
            return true;
        }

        var fallbackTarget = new Vector3(destination.X, currentPos.Y, destination.Z);
        var startingFallback = !underwaterFlagApproachSurfacedFallbackActive || underwaterTargetPosition == Vector3.Zero;
        var targetYRefreshed = !startingFallback
            && Math.Abs(fallbackTarget.Y - underwaterTargetPosition.Y) >= UnderwaterFlagApproachTargetYRefreshThreshold;

        underwaterFlagApproachSurfacedFallbackActive = true;
        underwaterTargetPosition = fallbackTarget;
        TrackUnderwaterFlagApproachProgress(now, underwaterTargetPosition, destinationXZ);
        PauseUnderwaterBounceDescentUntilFlagArrival();

        var navState = _plugin.NavigationService.State;
        var vnavRunning = _plugin.VNavIPC.TryIsRunning();
        LogUnderwaterFlagApproachHeartbeat(now, currentPos, underwaterTargetPosition, destinationXZ, navState, vnavRunning);
        if (TryHandlePendingUnderwaterFlagApproachReissue(now, currentPos, underwaterTargetPosition, destinationXZ))
        {
            StateDetail = $"Moving to surfaced thief-map flag X/Z... ({destinationXZ:F1}y)";
            return true;
        }

        var retryReason = GetUnderwaterFlagApproachRetryReason(
            now,
            navState,
            targetYRefreshed,
            startingFallback ? "surfaced fallback" : null);

        if (retryReason != null)
            IssueUnderwaterFlagApproach(now, retryReason, currentPos, underwaterTargetPosition, destinationXZ);

        StateDetail = $"Moving to surfaced thief-map flag X/Z... ({destinationXZ:F1}y)";
        return true;
    }

    private void SuppressUnderwaterBounceVnav()
    {
        if (Plugin.Condition[ConditionFlag.Diving])
            return;

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

    private bool EnsureUnderwaterBounceDescent(DateTime now, Vector3 currentPos)
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
            return false;

        lastUnderwaterBounceDescentStart = now;
        StartSafeDescent("[Underwater] thief-map trigger", includeForward: !Plugin.Condition[ConditionFlag.Diving]);
        return true;
    }

    private bool IsNonDivingThiefMapAirborneOrMounted()
    {
        return IsThiefUnderwaterLandingMode()
            && !Plugin.Condition[ConditionFlag.Diving]
            && (Plugin.Condition[ConditionFlag.InFlight]
                || Plugin.Condition[ConditionFlag.Mounted]
                || Plugin.Condition[ConditionFlag.Mounting71]);
    }

    private void ResetNonDivingThiefMapLandingSettleSample(DateTime now, Vector3 currentPos)
    {
        descentStartTime = now;
        descentStartY = currentPos.Y;
    }

    private bool IsNonDivingThiefMapLandingSettled(
        DateTime now,
        Vector3 currentPos,
        out double sampleElapsed,
        out float yChange)
    {
        if (descentStartTime == DateTime.MinValue)
        {
            ResetNonDivingThiefMapLandingSettleSample(now, currentPos);
            sampleElapsed = 0.0;
            yChange = 0.0f;
            return false;
        }

        sampleElapsed = (now - descentStartTime).TotalSeconds;
        yChange = Math.Abs(currentPos.Y - descentStartY);
        if (sampleElapsed < UnderwaterBounceLandingSettleWindow.TotalSeconds)
            return false;

        if (yChange <= UnderwaterFlagApproachStallMovementThreshold)
            return true;

        ResetNonDivingThiefMapLandingSettleSample(now, currentPos);
        return false;
    }

    private bool TryHandleNonDivingThiefMapLandingAfterDescent(
        DateTime now,
        Vector3 currentPos,
        Vector3 target,
        double xzDistance,
        string reason,
        bool issueDigWhenSafe = true)
    {
        if (!IsThiefUnderwaterLandingMode()
            || Plugin.Condition[ConditionFlag.Diving]
            || currentPos == Vector3.Zero
            || target == Vector3.Zero
            || xzDistance > UnderwaterBounceTriggerXZRange)
        {
            return false;
        }

        if (Plugin.Condition[ConditionFlag.Mounting71])
        {
            ResetNonDivingThiefMapLandingSettleSample(now, currentPos);
            StateDetail = $"Waiting for mount state before thief-map dig... ({xzDistance:F1}y XZ)";
            return true;
        }

        if (Plugin.Condition[ConditionFlag.InFlight] || Plugin.Condition[ConditionFlag.Mounted])
        {
            ResetNonDivingThiefMapLandingSettleSample(now, currentPos);
            var descentPulseIssued = EnsureUnderwaterBounceDescent(now, currentPos);
            LogUnderwaterTriggerLoop(now, currentPos, xzDistance, descentPulseIssued, digIssued: false);
            StateDetail = $"Descending for non-diving thief-map landing... ({xzDistance:F1}y XZ)";
            return true;
        }

        if (!IsNonDivingThiefMapLandingSettled(now, currentPos, out var sampleElapsed, out var yChange))
        {
            LogUnderwaterTriggerLoop(now, currentPos, xzDistance, descentPulseIssued: false, digIssued: false);
            StateDetail = sampleElapsed >= UnderwaterBounceLandingSettleWindow.TotalSeconds
                ? $"Waiting for thief-map landing to settle... (Y change {yChange:F1}y)"
                : $"Confirming thief-map landing settle... ({sampleElapsed:F1}/{UnderwaterBounceLandingSettleWindow.TotalSeconds:F1}s)";
            return true;
        }

        if (!issueDigWhenSafe)
            return false;

        return TryIssueNonDivingThiefMapDigAfterLanding(now, currentPos, target, xzDistance, reason);
    }

    private bool TryIssueNonDivingThiefMapDigAfterLanding(
        DateTime now,
        Vector3 currentPos,
        Vector3 target,
        double xzDistance,
        string reason)
    {
        if (digIssuedThisMap)
        {
            ResetUnderwaterLandingState();
            var elapsed = digIssuedAt == DateTime.MinValue ? 0 : (now - digIssuedAt).TotalSeconds;
            StateDetail = $"Waiting for treasure coffer after dig... ({elapsed:F1}s)";
            return true;
        }

        ResetUnderwaterLandingState();
        RecordMapLandingPosition();
        RunLandingCommandsOnce(reason);
        CommandHelper.SendCommand("/gaction dig");
        lastDigTime = now;
        digIssuedThisMap = true;
        digIssuedAt = now;
        _plugin.AddDebugLog(
            $"{reason}: issued /gaction dig after non-diving thief-map landing; " +
            $"current={FormatVectorCompact(currentPos)}; target={FormatVectorCompact(target)}; xz={xzDistance:F1}y.");

        System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ => {
            try
            {
                TransitionTo(BotState.OpeningChest, "Looking for treasure coffer to interact...");
            }
            catch (Exception ex)
            {
                Plugin.LogError($"[StateManager] ContinueWith exception in TransitionTo (thief-map landing dig handoff): {ex.Message}");
            }
        }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion);

        return true;
    }

    private bool TryGuardNonDivingThiefMapRecoveryDig(
        DateTime now,
        string reason,
        bool hasTarget,
        Vector3 target,
        double xzDistance)
    {
        if (!IsThiefUnderwaterLandingMode() || Plugin.Condition[ConditionFlag.Diving])
            return false;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null || !hasTarget || target == Vector3.Zero)
        {
            if (!IsNonDivingThiefMapAirborneOrMounted())
                return false;

            var currentPos = player?.Position ?? Vector3.Zero;
            if (currentPos != Vector3.Zero)
            {
                ResetNonDivingThiefMapLandingSettleSample(now, currentPos);
                EnsureUnderwaterBounceDescent(now, currentPos);
            }

            StateDetail = "Waiting for safe non-diving thief-map landing before retry dig...";
            return true;
        }

        var currentPosition = player.Position;
        xzDistance = CalculateXZDistance(currentPosition, target);
        if (xzDistance > UnderwaterBounceTriggerXZRange)
        {
            LogThiefWaterInfoRateLimited(
                ref lastThiefWaterRecoveryLogTime,
                ThiefWaterRecoveryLogInterval,
                $"{reason}: suppressed non-diving thief-map recovery dig at {xzDistance:F1}y XZ; " +
                "returning to landing recovery.");
            TransitionTo(BotState.Flying, "Thief-map recovery: returning to landing before dig...");
            return true;
        }

        return TryHandleNonDivingThiefMapLandingAfterDescent(
            now,
            currentPosition,
            target,
            xzDistance,
            reason,
            issueDigWhenSafe: false);
    }

    private bool TryHandleUnderwaterXyzDigRetryGate(
        DateTime now,
        Vector3 currentPos,
        Vector3 target,
        double xzDistance)
    {
        if (!IsUnderwaterXyzDigRetryGateEligible(target, xzDistance))
            return false;

        if (underwaterXyzDigRetryTarget == Vector3.Zero ||
            Vector3.Distance(underwaterXyzDigRetryTarget, target) >= 1.0f)
        {
            ResetUnderwaterXyzDigRetryState();
            underwaterXyzDigRetryTarget = target;
        }

        if (underwaterXyzDigRetryAttemptCount >= UnderwaterXyzDigRetryMaxAttempts)
        {
            if (underwaterXyzDigRetryWaitUntil != DateTime.MinValue &&
                now < underwaterXyzDigRetryWaitUntil)
            {
                StateDetail = BuildUnderwaterXyzDigRetryWaitDetail(now, "Waiting after underwater XYZ dig retry");
                return true;
            }

            if (underwaterXyzDigRetryWaitUntil != DateTime.MinValue)
            {
                var finalWaitSeconds = underwaterXyzDigRetryLastDigAt == DateTime.MinValue
                    ? UnderwaterXyzDigRetryDelay.TotalSeconds
                    : Math.Max(0.0, (now - underwaterXyzDigRetryLastDigAt).TotalSeconds);
                underwaterXyzDigRetryWaitUntil = DateTime.MinValue;
                _plugin.AddDebugLog(
                    $"[Underwater] XYZ dig retry final wait complete after {finalWaitSeconds:F1}s - allowing descent/fallback path.");
            }

            return false;
        }

        if (underwaterXyzDigRetryAttemptCount > 0 &&
            now < underwaterXyzDigRetryWaitUntil)
        {
            StateDetail = BuildUnderwaterXyzDigRetryWaitDetail(now, "Waiting before underwater XYZ dig retry");
            return true;
        }

        IssueUnderwaterXyzDigRetryAttempt(now, currentPos, target, xzDistance);
        return true;
    }

    private bool IsUnderwaterXyzDigRetryGateEligible(Vector3 target, double xzDistance)
    {
        return IsThiefUnderwaterLandingMode()
            && Plugin.Condition[ConditionFlag.Diving]
            && target != Vector3.Zero
            && xzDistance <= UnderwaterFlagApproachArrivalXZRange;
    }

    private string BuildUnderwaterXyzDigRetryWaitDetail(DateTime now, string prefix)
    {
        var remaining = underwaterXyzDigRetryWaitUntil == DateTime.MinValue
            ? 0.0
            : Math.Max(0.0, (underwaterXyzDigRetryWaitUntil - now).TotalSeconds);
        var nextAttempt = Math.Min(
            UnderwaterXyzDigRetryMaxAttempts,
            underwaterXyzDigRetryAttemptCount + 1);

        return $"{prefix} {nextAttempt}/{UnderwaterXyzDigRetryMaxAttempts}... ({remaining:F1}s)";
    }

    private void IssueUnderwaterXyzDigRetryAttempt(
        DateTime now,
        Vector3 currentPos,
        Vector3 target,
        double xzDistance)
    {
        underwaterXyzDigRetryAttemptCount++;
        underwaterXyzDigRetryLastDigAt = now;
        underwaterXyzDigRetryWaitUntil = now.Add(UnderwaterXyzDigRetryDelay);

        _plugin.NavigationService.FlyToPosition(target, force: true);
        RunLandingCommandsOnce("[Underwater] thief-map XYZ retry");
        CommandHelper.SendCommand("/gaction dig");
        lastDigTime = now;
        digIssuedThisMap = true;
        digIssuedAt = now;

        _plugin.AddDebugLog(
            $"[Underwater] XYZ dig retry attempt {underwaterXyzDigRetryAttemptCount}/{UnderwaterXyzDigRetryMaxAttempts}: " +
            $"force flyto {FormatVectorCompact(target)} -> /gaction dig; " +
            $"current={FormatVectorCompact(currentPos)}; flagXZ={xzDistance:F1}y; " +
            $"nextWait={UnderwaterXyzDigRetryDelay.TotalSeconds:F0}s.");

        StateDetail =
            $"Underwater XYZ dig retry {underwaterXyzDigRetryAttemptCount}/{UnderwaterXyzDigRetryMaxAttempts}...";
    }

    private void ResetUnderwaterXyzDigRetryState()
    {
        underwaterXyzDigRetryTarget = Vector3.Zero;
        underwaterXyzDigRetryAttemptCount = 0;
        underwaterXyzDigRetryLastDigAt = DateTime.MinValue;
        underwaterXyzDigRetryWaitUntil = DateTime.MinValue;
    }

    private void LogUnderwaterTriggerLoop(
        DateTime now,
        Vector3 currentPos,
        double targetXZDistance,
        bool descentPulseIssued,
        bool digIssued)
    {
        if (!descentPulseIssued
            && !digIssued
            && now - lastUnderwaterTriggerLoopLogTime < UnderwaterTriggerLoopLogInterval)
        {
            return;
        }

        lastUnderwaterTriggerLoopLogTime = now;
        _plugin.AddDebugLog(
            $"[Underwater] Trigger loop: y={currentPos.Y:F1}; flagXZ={targetXZDistance:F1}y; " +
            $"descentPulse={descentPulseIssued}; digIssued={digIssued}.");
    }

    private bool TryGetDeferredUnderwaterBounceObjectSummary(Vector3 currentPos, out string summary)
    {
        summary = string.Empty;
        IGameObject? nearestObject = null;
        var nearestObjectKind = string.Empty;
        var nearestDistance = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null)
                continue;

            var isPortal = string.Equals(obj.Name.TextValue, "Teleportation Portal", StringComparison.Ordinal);
            if (!isPortal && !ChestDetectionService.IsCofferObject(obj))
                continue;

            var distance = Vector3.Distance(currentPos, obj.Position);
            if (distance >= nearestDistance)
                continue;

            nearestObject = obj;
            nearestObjectKind = isPortal ? "portal" : "coffer";
            nearestDistance = distance;
        }

        if (nearestObject == null)
            return false;

        summary =
            $"{nearestObjectKind} '{nearestObject.Name.TextValue}' targetable={nearestObject.IsTargetable} " +
            $"at {nearestDistance:F1}y, XYZ {FormatVectorCompact(nearestObject.Position)}";
        return true;
    }

    private void LogDeferredUnderwaterBounceObjectHandoff(
        DateTime now,
        Vector3 currentPos,
        Vector3 target,
        double approachXZ)
    {
        if (now - lastUnderwaterFlagApproachObjectDeferredLogTime < ThiefWaterRecoveryLogInterval)
            return;

        if (!TryGetDeferredUnderwaterBounceObjectSummary(currentPos, out var objectSummary))
            return;

        lastUnderwaterFlagApproachObjectDeferredLogTime = now;
        LogThiefWaterInfo(
            $"[Underwater] Deferred visible {objectSummary} until thief-map flag X/Z arrival; " +
            $"flagXZ={approachXZ:F1}y > {UnderwaterFlagApproachArrivalXZRange:F1}y; " +
            $"current={FormatVectorCompact(currentPos)}; target={FormatVectorCompact(target)}.");
    }

    private bool TryHandleUnderwaterBounceObjectHandoff(out bool yieldToOpeningChest)
    {
        yieldToOpeningChest = false;

        if (!underwaterBounceHandoffLogged)
            return false;

        var portal = FindNearestPortal();
        if (portal != null)
        {
            _plugin.AddDebugLog("[Underwater] Portal detected after thief-map flag arrival - switching to portal interaction.");
            ResetUnderwaterXyzDigRetryState();
            CheckForPortalAfterChest();
            return true;
        }

        var chest = _plugin.ChestDetectionService.FindNearestCoffer();
        if (chest == null)
            return false;

        if (State == BotState.OpeningChest)
        {
            ResetUnderwaterXyzDigRetryState();
            yieldToOpeningChest = true;
            return false;
        }

        _plugin.AddDebugLog("[Underwater] Coffer detected after thief-map flag arrival - transitioning to chest flow.");
        ResetUnderwaterXyzDigRetryState();
        TransitionTo(BotState.OpeningChest, "Looking for treasure coffer to interact...");
        return true;
    }

    private bool TryHandleUnderwaterBounceTriggerFlow(bool isDiving, bool includeNearTarget = true)
    {
        if (!IsUnderwaterBounceTriggerFlow(includeNearTarget))
            return false;

        if (TryHandleConfirmedDutyEntry("[Underwater]"))
            return true;

        var now = DateTime.Now;
        var currentPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        if (currentPos == Vector3.Zero)
        {
            StateDetail = "Waiting for underwater thief-map player position...";
            return true;
        }

        if (underwaterBounceHandoffLogged && TryHoldForUnderwaterMapContentPartyWait(10.0))
            return true;

        if (underwaterBounceHandoffLogged)
        {
            if (TryHandleUnderwaterBounceObjectHandoff(out var yieldToOpeningChest))
                return true;

            if (yieldToOpeningChest)
                return false;
        }

        if (isDiving)
        {
            if (underwaterFlagApproachSurfacedFallbackActive)
            {
                underwaterFlagApproachSurfacedFallbackActive = false;
                underwaterTargetPosition = Vector3.Zero;
                underwaterFlagApproachIssued = false;
                underwaterFlagApproachLogged = false;
                lastUnderwaterFlagApproachTime = DateTime.MinValue;
                ResetUnderwaterFlagApproachProgressState();
                ResetPendingUnderwaterFlagApproachReissue();
                _plugin.AddDebugLog("[Underwater] Diving resumed during surfaced fallback - switching back to underwater flag approach.");
            }

            if (!wasDiving)
            {
                LogThiefWaterInfo("[Underwater] Diving state detected - swimming to flag X/Z before thief-map trigger descent");
                wasDiving = true;
            }

            var targetYRefreshed = TryRefreshUnderwaterFlagApproachTargetY(
                currentPos,
                out var basis,
                out var destinationText,
                out var zoneName);

            if (underwaterTargetPosition != Vector3.Zero && !underwaterFlagApproachLogged)
            {
                underwaterFlagApproachLogged = true;
                LogThiefWaterInfo(
                    $"[Underwater] Approaching thief-map flag X/Z at approach Y via {basis} for {destinationText} - {zoneName}; " +
                    $"target={FormatVectorCompact(underwaterTargetPosition)}");
            }

            if (underwaterTargetPosition == Vector3.Zero)
            {
                ResetPendingUnderwaterFlagApproachReissue();
                ResetUnderwaterFlagApproachStallSample();
                StateDetail = "Waiting for underwater thief-map flag target...";
                return true;
            }

            var approachXZ = CalculateXZDistance(currentPos, underwaterTargetPosition);
            TrackUnderwaterFlagApproachProgress(now, underwaterTargetPosition, approachXZ);

            if (approachXZ > UnderwaterFlagApproachArrivalXZRange)
            {
                PauseUnderwaterBounceDescentUntilFlagArrival();

                var navState = _plugin.NavigationService.State;
                var vnavRunning = _plugin.VNavIPC.TryIsRunning();
                LogUnderwaterFlagApproachHeartbeat(now, currentPos, underwaterTargetPosition, approachXZ, navState, vnavRunning);
                ResetPendingUnderwaterFlagApproachReissue();

                if (!underwaterFlagApproachIssued)
                {
                    IssueActiveUnderwaterFlagApproach(
                        now,
                        "initial",
                        currentPos,
                        underwaterTargetPosition,
                        approachXZ,
                        force: false);
                }
                else if (targetYRefreshed)
                {
                    IssueActiveUnderwaterFlagApproach(
                        now,
                        "target-y-refresh",
                        currentPos,
                        underwaterTargetPosition,
                        approachXZ,
                        force: true);
                }
                else
                {
                    TryRecoverActiveUnderwaterFlagApproachStall(now, currentPos, underwaterTargetPosition, approachXZ);
                }

                StateDetail = $"Swimming to underwater flag X/Z... ({approachXZ:F1}y)";
                LogDeferredUnderwaterBounceObjectHandoff(now, currentPos, underwaterTargetPosition, approachXZ);
                return true;
            }

            if (!underwaterBounceHandoffLogged)
            {
                underwaterBounceHandoffLogged = true;
                LogThiefWaterInfo(
                    $"[Underwater] Reached thief-map flag X/Z; handing off to descent/dig loop at " +
                    $"{FormatVectorCompact(currentPos)}.");
            }

            ResetPendingUnderwaterFlagApproachReissue();
            ResetUnderwaterFlagApproachStallSample();

            if (TryHoldForUnderwaterMapContentPartyWait(10.0))
                return true;

            if (TryHandleUnderwaterBounceObjectHandoff(out var yieldToOpeningChest))
                return true;

            if (yieldToOpeningChest)
                return false;

            if (TryHandleUnderwaterXyzDigRetryGate(now, currentPos, underwaterTargetPosition, approachXZ))
                return true;

            var descentPulseIssued = EnsureUnderwaterBounceDescent(now, currentPos);
            var digIssued = TryDigThiefMapWhileDivingAtGate("[Underwater] thief-map trigger", now, currentPos, underwaterTargetPosition, approachXZ);
            LogUnderwaterTriggerLoop(now, currentPos, approachXZ, descentPulseIssued, digIssued);
            StateDetail = "Holding underwater thief-map trigger...";
            return true;
        }

        var hadUnderwaterApproach = wasDiving
            || underwaterFlagApproachIssued
            || underwaterTargetPosition != Vector3.Zero
            || HasPendingUnderwaterFlagApproachReissue()
            || underwaterFlagApproachSurfacedFallbackActive;

        if (hadUnderwaterApproach)
        {
            if (!underwaterFlagApproachSurfacedFallbackActive)
                LogThiefWaterInfo("[Underwater] Diving lost; using surfaced fallback");

            wasDiving = false;
            ResetUnderwaterFlagApproachStallSample();
            if (TryHandleSurfacedUnderwaterFlagApproachFallback(now, currentPos))
                return true;
        }

        if (!isDiving)
            SuppressUnderwaterBounceVnav();

        if (TryHoldForUnderwaterMapContentPartyWait(10.0))
            return true;

        var nonDivingTarget = underwaterTargetPosition;
        if (nonDivingTarget == Vector3.Zero)
        {
            var targets = ResolveOverworldNavigationTargets();
            nonDivingTarget = targets.LandingTarget;
        }

        var nonDivingTargetXZ = nonDivingTarget == Vector3.Zero
            ? double.MaxValue
            : CalculateXZDistance(currentPos, nonDivingTarget);
        if (TryHandleNonDivingThiefMapLandingAfterDescent(
                now,
                currentPos,
                nonDivingTarget,
                nonDivingTargetXZ,
                "[Underwater] thief-map trigger"))
        {
            return true;
        }

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
        ResetOpeningChestFlagFallback("portal window reset");
        lastPortalMountCommandTime = DateTime.MinValue;
        portalLandingStartedAt = DateTime.MinValue;
        lastPortalDismountCommandTime = DateTime.MinValue;
        portalApproachStartedAt = DateTime.MinValue;
        portalApproachStartDistance = float.MaxValue;
        lastPortalRepathTime = DateTime.MinValue;
        portalRegularVnavPathLogged = false;
        lastPortalTimeoutHoldLogTime = DateTime.MinValue;
        lastPortalObjectScanLogTime = DateTime.MinValue;
        portalInteractionAttemptCount = 0;
        ResetPortalGroundApproachTracking(resetFailure: true);
        ResetPortalInteractionOutcomeTracking();
        ResetPortalCameraResetBeforeInteractTracking();
        portalUnderwaterReadyLogged = false;
    }

    private void ResetPortalApproachTrackingForAreaChange()
    {
        ResetOpeningChestCofferApproachTracking();
        ResetOpeningChestCofferWalkFailure();
        portalApproachStartedAt = DateTime.MinValue;
        portalApproachStartDistance = float.MaxValue;
        lastPortalRepathTime = DateTime.MinValue;
        ResetPortalGroundApproachTracking(resetFailure: true);
        ResetOpeningChestFlagFallback("area change");
        ResetPortalCloseNudgeTracking(stopMovement: true);
        ResetPortalNoDialogAttemptWindow(DateTime.MinValue);
        ResetPortalCameraResetBeforeInteractTracking();
    }

    private void ResetPortalInteractionOutcomeTracking()
    {
        ResetPortalNoDialogAttemptWindow(DateTime.MinValue);
        portalInteractionLastAttemptAt = DateTime.MinValue;
        portalInteractionLastProgressAt = DateTime.MinValue;
        portalInteractionLastPlayerPosition = default;
        portalInteractionLastPortalPosition = default;
        portalInteractionLastDistance = float.MaxValue;
        portalInteractionLastXzDistance = float.MaxValue;
        portalInteractionLastYDistance = float.MaxValue;
        portalInteractionBestDistance = float.MaxValue;
        lastPortalStuckDiagnosticLogTime = DateTime.MinValue;
        portalCloseNudgeCount = 0;
        ResetPortalCloseNudgeTracking(stopMovement: false);
    }

    private void ResetPortalNoDialogAttemptWindow(DateTime progressAt)
    {
        portalInteractionFirstAttemptAt = DateTime.MinValue;
        portalInteractionAttemptsSinceProgress = 0;
        portalInteractionEntityId = 0;
        if (progressAt != DateTime.MinValue)
            portalInteractionLastProgressAt = progressAt;
    }

    private void ResetPortalCloseNudgeTracking(bool stopMovement)
    {
        if (stopMovement && portalCloseNudgeActive)
        {
            CommandHelper.SendCommand("/automove off");
            if (_plugin.NavigationService.State != NavigationState.Idle)
                _plugin.NavigationService.StopNavigation();
            autoMoveActive = false;
        }

        portalCloseNudgeActive = false;
        portalCloseNudgeEntityId = 0;
        portalCloseNudgeStartedAt = DateTime.MinValue;
        portalCloseNudgeLastCommandAt = DateTime.MinValue;
    }

    private void ResetPortalGroundApproachTracking(bool resetFailure = false)
    {
        portalGroundApproachEntityId = 0;
        portalGroundApproachTarget = null;
        portalGroundApproachStartedAt = DateTime.MinValue;
        portalGroundApproachLastProgressTime = DateTime.MinValue;
        lastPortalGroundApproachRepathTime = DateTime.MinValue;
        portalGroundApproachBestDistance = float.MaxValue;

        if (resetFailure)
        {
            portalGroundApproachFailedEntityId = 0;
            portalGroundApproachFailedMarker = null;
        }
    }

    private Vector3 CapturePortalApproachPosition(IGameObject portal)
    {
        if ((portalGroundApproachEntityId != 0 && portalGroundApproachEntityId != portal.EntityId) ||
            (portalGroundApproachFailedEntityId != 0 && portalGroundApproachFailedEntityId != portal.EntityId))
        {
            ResetPortalGroundApproachTracking(resetFailure: true);
        }

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
            GameHelpers.KeyRelease(VirtualKey.W);
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
        var wasApproaching = openingChestCofferApproachEntityId != 0;

        if (stopNavigation && (wasActive || wasApproaching))
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
        ResetOpeningChestCofferApproachTracking();
    }

    private void ResetOpeningChestCofferApproachTracking()
    {
        openingChestCofferApproachEntityId = 0;
        openingChestCofferApproachStartedAt = DateTime.MinValue;
        openingChestCofferApproachLastProgressTime = DateTime.MinValue;
        lastOpeningChestCofferRepathTime = DateTime.MinValue;
        openingChestCofferApproachBestDistance = float.MaxValue;
    }

    private void ResetOpeningChestFlagFallback(string? reason = null, bool logIfActive = false)
    {
        var wasActive = openingChestFlagFallbackActive;
        var activeKind = openingChestFlagFallbackKind;
        var elapsed = openingChestFlagFallbackStartedAt == DateTime.MinValue
            ? 0.0
            : (DateTime.Now - openingChestFlagFallbackStartedAt).TotalSeconds;

        openingChestFlagFallbackKind = OpeningChestFlagFallbackKind.None;
        openingChestFlagFallbackEntityId = 0;
        openingChestFlagFallbackPortalMarker = null;
        openingChestFlagFallbackTried = false;
        openingChestFlagFallbackActive = false;
        openingChestFlagFallbackTarget = default;
        openingChestFlagFallbackOriginalTarget = default;
        openingChestFlagFallbackStartedAt = DateTime.MinValue;
        lastOpeningChestFlagFallbackRepathTime = DateTime.MinValue;

        if (logIfActive && wasActive)
        {
            var suffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({reason})";
            _plugin.AddDebugLog(
                $"[OpeningChest] {activeKind} flag fallback abandoned after {elapsed:F1}s{suffix}.");
        }
    }

    private void ResetOpeningChestCofferWalkFailure()
    {
        openingChestCofferWalkFailedEntityId = 0;
    }

    private void ResetOpeningChestCofferMemory()
    {
        openingChestLastKnownCofferPosition = null;
        openingChestLastKnownCofferTerritoryId = 0;
        openingChestLastKnownCofferEntityId = 0;
        openingChestReturningToLastKnownCoffer = false;
        lastOpeningChestLastKnownCofferLogTime = DateTime.MinValue;
        lastOpeningChestObjectScanLogTime = DateTime.MinValue;
        lastOpeningChestUntargetableLogTime = DateTime.MinValue;
        lastOpeningChestTargetCommandTime = DateTime.MinValue;
        ResetOpeningChestCofferApproachTracking();
        ResetOpeningChestCofferWalkFailure();
        ResetOpeningChestFlagFallback("coffer memory reset");
    }

    private void ResetOpeningChestLifecycleState()
    {
        openingChestDiscoveredByChat = false;
        openingChestOpenedByChat = false;
        openingChestPortalByChat = false;
        openingChestManualInterventionSuspected = false;
        openingChestDiscoveredChatAt = DateTime.MinValue;
        openingChestOpenedChatAt = DateTime.MinValue;
        openingChestPortalChatAt = DateTime.MinValue;
        openingChestBotInteractionAttemptedThisMap = false;
        ClearOpeningChestJoinedFateHold();
        ResetOpeningChestCofferApproachTracking();
        ResetOpeningChestCofferWalkFailure();
        ResetOpeningChestFlagFallback("opening chest lifecycle reset");
        openingChestFlagRecoveryTargetLogKey = string.Empty;
        ResetOpeningChestMissingCofferRecoveryState();
        ResetOpeningChestInteractionTracking();
    }

    private void ResetOpeningChestMissingCofferRecoveryState()
    {
        openingChestMissingCofferRecoveryStartedAt = DateTime.MinValue;
        lastOpeningChestRecoveryDigTime = DateTime.MinValue;
        openingChestRecoveryDigRetryCount = 0;
    }

    private void ResetOpeningChestInteractionTracking()
    {
        openingChestInteractionAttemptCount = 0;
        openingChestInteractionEntityId = 0;
        ResetOpeningChestCameraResetBeforeInteractTracking();
    }

    private static void ResetCameraResetBeforeInteractTracking(ref uint entityId, ref DateTime readyAt)
    {
        entityId = 0;
        readyAt = DateTime.MinValue;
    }

    private void ResetOpeningChestCameraResetBeforeInteractTracking()
    {
        ResetCameraResetBeforeInteractTracking(
            ref openingChestCameraResetEntityId,
            ref openingChestCameraResetReadyAt);
    }

    private void ResetPortalCameraResetBeforeInteractTracking()
    {
        ResetCameraResetBeforeInteractTracking(
            ref portalCameraResetEntityId,
            ref portalCameraResetReadyAt);
    }

    private void ResetAllCameraResetBeforeInteractTracking()
    {
        ResetOpeningChestCameraResetBeforeInteractTracking();
        ResetPortalCameraResetBeforeInteractTracking();
    }

    private bool TryHoldForCameraResetBeforeInteract(
        string source,
        IGameObject target,
        DateTime now,
        ref uint resetEntityId,
        ref DateTime resetReadyAt)
    {
        if (resetEntityId != target.EntityId)
        {
            resetEntityId = target.EntityId;
            resetReadyAt = DateTime.MinValue;
        }

        var targetName = target.Name.TextValue;
        if (resetReadyAt == DateTime.MinValue)
        {
            if (!GameHelpers.RequestCameraResetBeforeInteract())
            {
                ResetCameraResetBeforeInteractTracking(ref resetEntityId, ref resetReadyAt);
                _plugin.AddDebugLog(
                    $"{source} Camera reset unavailable before interacting with '{targetName}' - continuing with camera-based TargetSystem.");
                return false;
            }

            resetReadyAt = now + CameraResetBeforeInteractDelay;
            StateDetail = $"Resetting camera before interacting with '{targetName}'...";
            _plugin.AddDebugLog(
                $"{source} Requested camera reset before interacting with '{targetName}' - waiting {CameraResetBeforeInteractDelay.TotalMilliseconds:F0}ms.");
            return true;
        }

        if (now < resetReadyAt)
        {
            StateDetail = $"Waiting briefly for camera reset before interacting with '{targetName}'...";
            return true;
        }

        ResetCameraResetBeforeInteractTracking(ref resetEntityId, ref resetReadyAt);
        return false;
    }

    private void CaptureOpeningChestCofferPosition(IGameObject chest)
    {
        if (!ChestDetectionService.IsCofferObject(chest))
            return;

        var territoryId = Plugin.ClientState.TerritoryType;
        if (openingChestCofferWalkFailedEntityId != 0 &&
            openingChestCofferWalkFailedEntityId != chest.EntityId)
        {
            ResetOpeningChestCofferWalkFailure();
        }

        var previousPosition = openingChestLastKnownCofferPosition;
        var changed = openingChestLastKnownCofferEntityId != chest.EntityId
            || openingChestLastKnownCofferTerritoryId != territoryId
            || !previousPosition.HasValue
            || Vector3.DistanceSquared(previousPosition.Value, chest.Position) > 1.0f;

        openingChestLastKnownCofferPosition = chest.Position;
        openingChestLastKnownCofferTerritoryId = territoryId;
        openingChestLastKnownCofferEntityId = chest.EntityId;

        if (changed)
        {
            _plugin.AddDebugLog(
                $"[OpeningChest] Captured coffer XYZ {FormatVectorCompact(chest.Position)} in territory {territoryId}.");
        }
    }

    private bool TryGetOpeningChestLastKnownCofferPosition(out Vector3 position, out float distance)
    {
        position = Vector3.Zero;
        distance = float.MaxValue;

        if (!openingChestLastKnownCofferPosition.HasValue)
            return false;

        if (openingChestLastKnownCofferTerritoryId != Plugin.ClientState.TerritoryType)
            return false;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return false;

        position = openingChestLastKnownCofferPosition.Value;
        distance = Vector3.Distance(player.Position, position);
        return true;
    }

    private bool TryReturnToOpeningChestLastKnownCoffer(DateTime now)
    {
        if (!TryGetOpeningChestLastKnownCofferPosition(out var position, out var distance))
            return false;

        if (distance <= OpeningChestCofferReturnRange)
        {
            if (openingChestReturningToLastKnownCoffer)
            {
                StopOpeningChestCofferMovement($"near captured coffer XYZ {FormatVectorCompact(position)}");
                _plugin.AddDebugLog(
                    $"[OpeningChest] Back near captured coffer XYZ {FormatVectorCompact(position)} ({distance:F1}y) - rechecking objects.");
            }

            openingChestReturningToLastKnownCoffer = false;
            return false;
        }

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            StateDetail = $"In combat - waiting to return to coffer XYZ ({distance:F1}y)...";
            return true;
        }

        if (!openingChestReturningToLastKnownCoffer ||
            now - lastOpeningChestLastKnownCofferLogTime >= TimeSpan.FromSeconds(5.0))
        {
            lastOpeningChestLastKnownCofferLogTime = now;
            _plugin.AddDebugLog(
                $"[OpeningChest] Returning to captured coffer XYZ {FormatVectorCompact(position)} ({distance:F1}y).");
        }

        openingChestReturningToLastKnownCoffer = true;

        if (!EnsureOpeningChestCofferRecoveryMounted("captured coffer XYZ", distance, 0f, now))
            return true;

        NavigateToOpeningChestCofferPosition(position, distance, now);
        StateDetail = $"Returning to captured coffer XYZ ({distance:F1}y)...";
        return true;
    }

    private void StopOpeningChestCofferMovement(string reason)
    {
        var hadMovement = autoMoveActive || _plugin.NavigationService.State != NavigationState.Idle;

        if (autoMoveActive)
        {
            GameHelpers.StopAutoMove();
            autoMoveActive = false;
        }

        if (_plugin.NavigationService.State != NavigationState.Idle)
            _plugin.NavigationService.StopNavigation();

        if (hadMovement)
            _plugin.AddDebugLog($"[OpeningChest] Stopped coffer movement {reason}.");
    }

    private bool TryResolveOpeningChestFlagRealYFallbackTarget(
        bool allowPortalRetryWindow,
        out Vector3 target,
        out string basis)
    {
        target = Vector3.Zero;
        basis = string.Empty;

        var stateAllowed = State == BotState.OpeningChest ||
                           (allowPortalRetryWindow && State == BotState.Completed && portalRetryStart != DateTime.MinValue);
        if (!stateAllowed ||
            !digIssuedThisMap ||
            CurrentLocation == null ||
            CurrentLocation.TerritoryId != Plugin.ClientState.TerritoryType)
        {
            return false;
        }

        var entry = _plugin.MapLocationDatabase.FindEntry(CurrentLocation.TerritoryId, CurrentLocation.X, CurrentLocation.Z);
        if (entry == null || !entry.HasRealXYZ)
            return false;

        target = new Vector3(entry.FlagX, entry.RealY + 5f, entry.FlagZ);
        var destinationIndex = entry.Index > 0 ? $"Destination #{entry.Index}" : "Unknown destination";
        var zoneName = entry.ZoneName ?? CurrentLocation.ZoneName ?? "Unknown";
        basis = $"{destinationIndex} - {zoneName}; FlagXZ + RealY+5 from map DB";
        return true;
    }

    private bool OpeningChestFlagFallbackKeyMatches(
        OpeningChestFlagFallbackKind kind,
        uint entityId,
        Vector3? portalMarker)
    {
        if (openingChestFlagFallbackKind != kind)
            return false;

        return kind switch
        {
            OpeningChestFlagFallbackKind.Coffer => openingChestFlagFallbackEntityId == entityId,
            OpeningChestFlagFallbackKind.Portal => openingChestFlagFallbackPortalMarker.HasValue &&
                                                   portalMarker.HasValue &&
                                                   Vector3.DistanceSquared(openingChestFlagFallbackPortalMarker.Value, portalMarker.Value) <= 1.0f,
            _ => false
        };
    }

    private void SyncOpeningChestFlagFallbackKey(
        OpeningChestFlagFallbackKind kind,
        uint entityId,
        Vector3? portalMarker)
    {
        if (OpeningChestFlagFallbackKeyMatches(kind, entityId, portalMarker))
            return;

        ResetOpeningChestFlagFallback("target changed", logIfActive: true);
        openingChestFlagFallbackKind = kind;
        openingChestFlagFallbackEntityId = entityId;
        openingChestFlagFallbackPortalMarker = portalMarker;
    }

    private bool TryRunOpeningChestFlagFallback(
        OpeningChestFlagFallbackKind kind,
        uint entityId,
        Vector3? portalMarker,
        DateTime now)
    {
        if (!openingChestFlagFallbackActive ||
            !OpeningChestFlagFallbackKeyMatches(kind, entityId, portalMarker))
        {
            return false;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            StateDetail = $"Waiting for player during {kind.ToString().ToLowerInvariant()} flag fallback...";
            return true;
        }

        var distance = Vector3.Distance(player.Position, openingChestFlagFallbackTarget);
        var navInactiveNearTarget = _plugin.NavigationService.State == NavigationState.Idle &&
                                    distance <= PortalApproachInteractionRange;
        if (distance <= PortalInteractionRange || navInactiveNearTarget)
        {
            openingChestFlagFallbackActive = false;
            openingChestFlagFallbackStartedAt = DateTime.MinValue;
            lastOpeningChestFlagFallbackRepathTime = DateTime.MinValue;
            ResetOpeningChestCofferApproachTracking();
            portalApproachStartedAt = DateTime.MinValue;
            portalApproachStartDistance = float.MaxValue;
            _plugin.AddDebugLog(
                $"[OpeningChest] {kind} flag fallback completed at {distance:F1}y from {FormatVectorCompact(openingChestFlagFallbackTarget)} " +
                $"after old target {FormatVectorCompact(openingChestFlagFallbackOriginalTarget)} - resuming normal scan.");
            return false;
        }

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            StateDetail = $"In combat - pausing {kind.ToString().ToLowerInvariant()} flag fallback ({distance:F1}y)...";
            return true;
        }

        if (Plugin.Condition[ConditionFlag.Mounting71])
        {
            StateDetail = $"Mounting for {kind.ToString().ToLowerInvariant()} flag fallback ({distance:F1}y)...";
            return true;
        }

        if (!_plugin.NavigationService.IsMounted() && !_plugin.NavigationService.IsFlying())
        {
            if (now - lastOpeningChestCofferMountCommandTime >= OpeningChestCofferMountCommandInterval)
            {
                lastOpeningChestCofferMountCommandTime = now;
                _plugin.NavigationService.MountUp();
            }

            StateDetail = $"Mounting for {kind.ToString().ToLowerInvariant()} flag fallback ({distance:F1}y)...";
            return true;
        }

        var fallbackTargetKey = BuildOverworldRecoveryPositionKey(
            $"opening-{kind.ToString().ToLowerInvariant()}-flag-fallback",
            Plugin.ClientState.TerritoryType,
            openingChestFlagFallbackTarget);
        if (TryRunOverworldRecoveryWatchdog(
                now,
                "OpeningChest",
                $"{kind.ToString().ToLowerInvariant()} flag fallback",
                fallbackTargetKey,
                Plugin.ClientState.TerritoryType,
                openingChestFlagFallbackTarget,
                OverworldRecoveryNavigationKind.FlyTo))
        {
            return true;
        }

        var navInactive = _plugin.NavigationService.State == NavigationState.Idle || !_plugin.VNavIPC.IsNavigating;
        if (navInactive)
        {
            if (_plugin.NavigationService.State != NavigationState.Idle)
                _plugin.NavigationService.StopNavigation();

            _plugin.NavigationService.FlyToPosition(openingChestFlagFallbackTarget, force: true);
            autoMoveActive = true;
            lastOpeningChestFlagFallbackRepathTime = now;
        }

        StateDetail = $"Flying to {kind.ToString().ToLowerInvariant()} flag fallback ({distance:F1}y)...";
        return true;
    }

    private bool TryStartOpeningChestFlagFallback(
        OpeningChestFlagFallbackKind kind,
        uint entityId,
        Vector3? portalMarker,
        Vector3 originalTarget,
        float currentDistance,
        string reason,
        DateTime now)
    {
        SyncOpeningChestFlagFallbackKey(kind, entityId, portalMarker);
        if (openingChestFlagFallbackTried)
            return false;

        var allowPortalRetryWindow = kind == OpeningChestFlagFallbackKind.Portal;
        if (!TryResolveOpeningChestFlagRealYFallbackTarget(
                allowPortalRetryWindow,
                out var fallbackTarget,
                out var basis))
        {
            return false;
        }

        StopOpeningChestCofferMovement($"before {kind.ToString().ToLowerInvariant()} flag fallback");
        if (kind == OpeningChestFlagFallbackKind.Portal)
            StopPortalMovementBeforeVnav();

        openingChestFlagFallbackTried = true;
        openingChestFlagFallbackActive = true;
        openingChestFlagFallbackTarget = fallbackTarget;
        openingChestFlagFallbackOriginalTarget = originalTarget;
        openingChestFlagFallbackStartedAt = now;
        lastOpeningChestFlagFallbackRepathTime = DateTime.MinValue;
        ResetOpeningChestCofferApproachTracking();
        portalApproachStartedAt = DateTime.MinValue;
        portalApproachStartDistance = float.MaxValue;

        _plugin.AddDebugLog(
            $"[OpeningChest] Starting {kind.ToString().ToLowerInvariant()} flag fallback after {reason}: " +
            $"oldTarget={FormatVectorCompact(originalTarget)} fallback={FormatVectorCompact(fallbackTarget)} " +
            $"dist={currentDistance:F1}y basis={basis}.");
        return TryRunOpeningChestFlagFallback(kind, entityId, portalMarker, now);
    }

    private void NavigateToOpeningChestCofferPosition(Vector3 position, float distance, DateTime now)
    {
        var targetKey = BuildOverworldRecoveryPositionKey(
            "opening-captured-coffer",
            Plugin.ClientState.TerritoryType,
            position);
        if (TryRunOverworldRecoveryWatchdog(
                now,
                "OpeningChest",
                "captured coffer",
                targetKey,
                Plugin.ClientState.TerritoryType,
                position,
                OverworldRecoveryNavigationKind.FlyTo))
        {
            return;
        }

        if (!autoMoveActive || _plugin.NavigationService.State == NavigationState.Idle)
        {
            if (_plugin.NavigationService.State != NavigationState.Idle)
                _plugin.NavigationService.StopNavigation();

            _plugin.NavigationService.FlyToPosition(position, force: true);
            autoMoveActive = true;
            lastOpeningChestCofferRepathTime = now;
            _plugin.AddDebugLog(
                $"[OpeningChest] Flying to captured coffer XYZ {FormatVectorCompact(position)} ({distance:F1}y).");
        }
    }

    private bool NavigateToOpeningChestCoffer(IGameObject chest, string chestName, float distance, DateTime now, bool fly)
    {
        var action = fly ? "fly" : "move";
        SyncOpeningChestFlagFallbackKey(OpeningChestFlagFallbackKind.Coffer, chest.EntityId, null);
        if (TryRunOpeningChestFlagFallback(OpeningChestFlagFallbackKind.Coffer, chest.EntityId, null, now))
            return true;

        if (openingChestCofferApproachEntityId != chest.EntityId)
        {
            ResetOpeningChestCofferApproachTracking();
            openingChestCofferApproachEntityId = chest.EntityId;
            openingChestCofferApproachLastProgressTime = now;
            openingChestCofferApproachBestDistance = distance;
            StopOpeningChestCofferMovement("before starting fresh coffer vnav path");
            if (fly)
                _plugin.NavigationService.FlyToPosition(chest.Position);
            else
                _plugin.NavigationService.MoveToPosition(chest.Position);
            autoMoveActive = true;
            lastOpeningChestCofferRepathTime = now;
            _plugin.AddDebugLog($"[OpeningChest] Coffer '{chestName}' at {distance:F1}y - starting vnav {action} approach.");
            return false;
        }

        if (distance + OpeningChestCofferProgressMargin < openingChestCofferApproachBestDistance)
        {
            openingChestCofferApproachBestDistance = distance;
            openingChestCofferApproachLastProgressTime = now;
        }

        var targetKey = $"opening-coffer:{chest.EntityId}:{chest.Position.X:0.0}:{chest.Position.Y:0.0}:{chest.Position.Z:0.0}";
        if (TryRunOverworldRecoveryWatchdog(
                now,
                "OpeningChest",
                fly ? "displaced coffer" : "coffer",
                targetKey,
                Plugin.ClientState.TerritoryType,
                chest.Position,
                fly ? OverworldRecoveryNavigationKind.FlyTo : OverworldRecoveryNavigationKind.MoveTo))
        {
            return true;
        }

        var stalled = now - openingChestCofferApproachLastProgressTime >= OpeningChestCofferStallTimeout;
        var canRepath = now - lastOpeningChestCofferRepathTime >= OpeningChestCofferRepathInterval;
        if (!autoMoveActive || _plugin.NavigationService.State == NavigationState.Idle || (stalled && canRepath))
        {
            if (stalled &&
                TryStartOpeningChestFlagFallback(
                    OpeningChestFlagFallbackKind.Coffer,
                    chest.EntityId,
                    null,
                    chest.Position,
                    distance,
                    $"coffer vnav {action} stall",
                    now))
            {
                return true;
            }

            var reason = stalled
                ? $"after vnav stall at {distance:F1}y from '{chestName}'"
                : $"before repathing to '{chestName}'";
            StopOpeningChestCofferMovement(reason);
            if (fly)
                _plugin.NavigationService.FlyToPosition(chest.Position);
            else
                _plugin.NavigationService.MoveToPosition(chest.Position);
            autoMoveActive = true;
            lastOpeningChestCofferRepathTime = now;
            openingChestCofferApproachLastProgressTime = now;
            openingChestCofferApproachBestDistance = distance;

            if (stalled)
            {
                _plugin.AddDebugLog(
                    $"[OpeningChest] Coffer vnav {action} stalled short of '{chestName}' at {distance:F1}y - stopped and reissued path.");
            }
        }

        return stalled;
    }

    private static float GetOpeningChestCofferInteractionRange(float configuredRange)
    {
        return Math.Min(configuredRange, OpeningChestCofferStrictInteractionRange);
    }

    private bool ShouldUseNearOpeningChestCofferGroundApproach(IGameObject chest, Vector3 playerPosition, bool ignoreFailure = false)
    {
        if (Plugin.Condition[ConditionFlag.Diving] ||
            IsThiefUnderwaterLandingMode())
        {
            return false;
        }

        if (!chest.IsTargetable)
            return false;

        if (!ignoreFailure && openingChestCofferWalkFailedEntityId == chest.EntityId)
            return false;

        var distance = Vector3.Distance(playerPosition, chest.Position);
        var xzDistance = (float)CalculateXZDistance(playerPosition, chest.Position);
        var nearEnough = distance <= OpeningChestCofferWalkPreferredDistance ||
                         xzDistance <= OpeningChestCofferWalkPreferredDistance;
        if (!nearEnough)
            return false;

        var yDistance = Math.Abs(playerPosition.Y - chest.Position.Y);
        return yDistance <= OpeningChestCofferGroundApproachYDelta;
    }

    private bool TryRunOpeningChestCofferGroundApproach(
        IGameObject chest,
        string chestName,
        float distance,
        float yDistance,
        DateTime now)
    {
        SyncOpeningChestFlagFallbackKey(OpeningChestFlagFallbackKind.Coffer, chest.EntityId, null);

        if (openingChestCofferMountRecoveryActive)
            ResetOpeningChestCofferMountRecovery("before near coffer ground approach", stopNavigation: true);

        Plugin.TargetManager.Target = chest;

        var mountedOrFlyingOrMounting = _plugin.NavigationService.IsMounted()
            || _plugin.NavigationService.IsFlying()
            || Plugin.Condition[ConditionFlag.Mounting71];
        if (mountedOrFlyingOrMounting)
        {
            StopOpeningChestCofferMovement($"near slope-aware coffer '{chestName}' before ground approach");
            CommandHelper.SendCommand("/automove off");
            autoMoveActive = false;

            if (!Plugin.Condition[ConditionFlag.Mounting71] &&
                now - lastOpeningChestCofferDismountCommandTime >= OpeningChestCofferDismountCommandInterval)
            {
                lastOpeningChestCofferDismountCommandTime = now;
                _mountService.Dismount();
                _plugin.AddDebugLog(
                    $"[OpeningChest] Near coffer '{chestName}' at {distance:F1}y, Y {yDistance:F1}y - dismounting for on-foot vnav approach.");
            }

            StateDetail = $"Dismounting near '{chestName}' for ground approach ({distance:F1}y, Y {yDistance:F1}y)...";
            return true;
        }

        if (openingChestCofferApproachEntityId != chest.EntityId)
        {
            ResetOpeningChestFlagFallback("before near coffer ground approach", logIfActive: true);
            ResetOpeningChestCofferApproachTracking();
            openingChestCofferApproachEntityId = chest.EntityId;
            openingChestCofferApproachStartedAt = now;
            openingChestCofferApproachLastProgressTime = now;
            openingChestCofferApproachBestDistance = distance;

            StopOpeningChestCofferMovement($"before starting near coffer ground approach to '{chestName}'");
            CommandHelper.SendCommand("/automove off");
            _plugin.NavigationService.MoveToPosition(chest.Position);
            autoMoveActive = true;
            lastOpeningChestCofferRepathTime = now;
            _plugin.AddDebugLog(
                $"[OpeningChest] Coffer '{chestName}' at {distance:F1}y, Y {yDistance:F1}y - starting on-foot vnav ground approach.");
            StateDetail = $"Ground-approaching nearby coffer '{chestName}' ({distance:F1}y, Y {yDistance:F1}y)...";
            return true;
        }

        if (distance + OpeningChestCofferProgressMargin < openingChestCofferApproachBestDistance)
        {
            openingChestCofferApproachBestDistance = distance;
            openingChestCofferApproachLastProgressTime = now;
        }

        var elapsed = now - openingChestCofferApproachStartedAt;
        var noProgressFor = now - openingChestCofferApproachLastProgressTime;
        var hitHardCap = elapsed >= GroundApproachHardTimeout;
        var hitNoProgressCap = elapsed >= GroundApproachMinimumDuration &&
                               noProgressFor >= GroundApproachNoProgressTimeout;
        if (hitHardCap || hitNoProgressCap)
        {
            var bestDistance = openingChestCofferApproachBestDistance;
            CommandHelper.SendCommand("/automove off");
            if (_plugin.NavigationService.State != NavigationState.Idle)
                _plugin.NavigationService.StopNavigation();
            autoMoveActive = false;
            openingChestCofferWalkFailedEntityId = chest.EntityId;
            ResetOpeningChestCofferApproachTracking();
            _plugin.AddDebugLog(
                $"[OpeningChest] Ground approach to '{chestName}' failed after {elapsed.TotalSeconds:F1}s " +
                $"(best {bestDistance:F1}y, current {distance:F1}y, no progress {noProgressFor.TotalSeconds:F1}s) - allowing mounted recovery.");
            StateDetail = $"Ground approach failed for '{chestName}' ({distance:F1}y) - preparing mounted recovery...";
            return false;
        }

        var navInactive = _plugin.NavigationService.State == NavigationState.Idle ||
                          !_plugin.VNavIPC.IsNavigating;
        if (!autoMoveActive ||
            navInactive ||
            now - lastOpeningChestCofferRepathTime >= OpeningChestCofferRepathInterval)
        {
            if (navInactive && _plugin.NavigationService.State != NavigationState.Idle)
                _plugin.NavigationService.StopNavigation();

            _plugin.NavigationService.MoveToPosition(chest.Position);
            autoMoveActive = true;
            lastOpeningChestCofferRepathTime = now;
        }

        StateDetail = $"Ground-approaching nearby coffer '{chestName}' ({distance:F1}y, Y {yDistance:F1}y, {elapsed.TotalSeconds:F0}s)...";
        return true;
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
        if (inDuty && IsTreasureDungeonTerritory(Plugin.ClientState.TerritoryType))
            return false;

        var yDistance = Math.Abs(yDelta);
        var handoffDistance = Math.Min(range, OpeningChestCofferMountRecoveryDistance);
        var closeTargetableCoffer = chest.IsTargetable &&
                                    distance > handoffDistance &&
                                    distance < OpeningChestCofferCloseDismountDistance &&
                                    !Plugin.Condition[ConditionFlag.Diving];
        if (closeTargetableCoffer)
        {
            var mountedOrFlyingOrMounting = _plugin.NavigationService.IsMounted()
                || _plugin.NavigationService.IsFlying()
                || Plugin.Condition[ConditionFlag.Mounting71];
            if (mountedOrFlyingOrMounting)
            {
                Plugin.TargetManager.Target = chest;
                StopOpeningChestCofferMovement($"near close coffer '{chestName}' before dismount");

                if (now - lastOpeningChestCofferDismountCommandTime >= OpeningChestCofferDismountCommandInterval)
                {
                    lastOpeningChestCofferDismountCommandTime = now;
                    _mountService.Dismount();
                    _plugin.AddDebugLog(
                        $"[OpeningChest] Close coffer '{chestName}' at {distance:F1}y, Y {yDistance:F1}y - dismounting before ground handoff.");
                }

                StateDetail = $"Dismounting near '{chestName}' ({distance:F1}y, Y {yDistance:F1}y)...";
                return true;
            }

            if (openingChestCofferMountRecoveryActive || openingChestCofferMountRecoveryEntityId != 0)
                ResetOpeningChestCofferMountRecovery("after close coffer dismount", stopNavigation: true);

            return false;
        }

        if (ShouldUseNearOpeningChestCofferGroundApproach(chest, playerPosition))
        {
            if (distance <= range)
                return false;

            if (TryRunOpeningChestCofferGroundApproach(chest, chestName, distance, yDistance, now))
                return true;

            _plugin.AddDebugLog(
                $"[OpeningChest] Ground approach to '{chestName}' failed at {distance:F1}y, Y {yDistance:F1}y - mounted recovery taking over.");
        }

        var displaced = distance > OpeningChestCofferMountRecoveryDistance ||
                        yDistance >= OpeningChestCofferMountRecoveryYDelta;
        if (!displaced && !openingChestCofferMountRecoveryActive)
            return false;

        if (openingChestCofferMountRecoveryEntityId != 0 &&
            openingChestCofferMountRecoveryEntityId != chest.EntityId)
        {
            ResetOpeningChestCofferMountRecovery();
        }

        var withinRecoveryHandoff = distance <= handoffDistance &&
                                    yDistance < OpeningChestCofferMountRecoveryYDelta;
        if (withinRecoveryHandoff)
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
                    $"[OpeningChest] Coffer mount recovery reached handoff range for '{chestName}' ({distance:F1}y, Y {yDistance:F1}y).");
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
                _plugin.AddDebugLog(
                    $"[OpeningChest] Within {handoffDistance:F1}y and Y {OpeningChestCofferMountRecoveryYDelta:F1}y - stopped vnav before coffer interaction handoff.");
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

                StateDetail = $"Landing at '{chestName}' ({distance:F1}y, Y {yDistance:F1}y)...";
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
                if (AttemptOpeningChestCofferInteraction(chest, chestName, "after coffer recovery", now))
                    lastInteractionTime = now;
                else
                    return true;
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
                $"player={FormatVectorCompact(playerPosition)}; dist={distance:F1}y; Y={yDistance:F1}y - using mounted recovery.");

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

        NavigateToOpeningChestCoffer(chest, chestName, distance, now, fly: true);
        autoMoveActive = true;
        StateDetail = $"Flying to displaced coffer '{chestName}' ({distance:F1}y, Y {yDistance:F1}y)...";
        return true;
    }

    private bool AttemptOpeningChestCofferInteraction(IGameObject chest, string chestName, string reason, DateTime now)
    {
        if (openingChestInteractionEntityId != chest.EntityId)
        {
            openingChestInteractionEntityId = chest.EntityId;
            openingChestInteractionAttemptCount = 0;
            ResetOpeningChestCameraResetBeforeInteractTracking();
        }

        var nextAttempt = openingChestInteractionAttemptCount + 1;
        var useCameraRaycast = (nextAttempt - 1) % 2 == 0;
        if (useCameraRaycast &&
            TryHoldForCameraResetBeforeInteract(
                "[OpeningChest]",
                chest,
                now,
                ref openingChestCameraResetEntityId,
                ref openingChestCameraResetReadyAt))
        {
            return false;
        }

        openingChestInteractionAttemptCount = nextAttempt;
        openingChestBotInteractionAttemptedThisMap = true;
        Plugin.TargetManager.Target = chest;

        var methodName = useCameraRaycast
            ? "TargetSystem(camera+reset)"
            : "TargetSystem(no-camera)";
        _plugin.AddDebugLog(
            $"[OpeningChest] Interaction attempt #{openingChestInteractionAttemptCount} via {methodName} with '{chestName}' {reason}.");
        var interacted = GameHelpers.InteractWithObject(chest, useCameraRaycast);
        _plugin.AddDebugLog(
            $"[OpeningChest] Interaction attempt #{openingChestInteractionAttemptCount} {methodName} returned: {interacted}");
        return true;
    }

    private bool HasOpeningChestCofferCompletionEvidence()
    {
        return openingChestOpenedByChat ||
               openingChestPortalByChat ||
               portalConfirmedThisMap ||
               dungeonConfirmedThisMap;
    }

    private bool ShouldExpectOpeningChestCoffer()
    {
        return digIssuedThisMap ||
               openingChestDiscoveredByChat ||
               openingChestLastKnownCofferPosition.HasValue ||
               IsOverworldMapDutyActive();
    }

    private bool TryResolveOpeningChestCofferFromCurrentTarget(float maxRange, DateTime now, out IGameObject? chest)
    {
        chest = null;
        var target = Plugin.TargetManager.Target;
        if (!ChestDetectionService.IsCofferObject(target))
            return false;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return false;

        var cofferTarget = target!;
        var distance = Vector3.Distance(player.Position, cofferTarget.Position);
        if (distance > maxRange)
            return false;

        chest = cofferTarget;
        if (now - lastOpeningChestObjectScanLogTime >= OpeningChestObjectScanLogInterval)
        {
            lastOpeningChestObjectScanLogTime = now;
            _plugin.AddDebugLog(
                $"[OpeningChest] Resolved coffer from current target '{cofferTarget.Name.TextValue}' kind={cofferTarget.ObjectKind} " +
                $"targetable={cofferTarget.IsTargetable} at {distance:F1}y, XYZ {FormatVectorCompact(cofferTarget.Position)}.");
        }
        return true;
    }

    private bool TryIssueOpeningChestTargetFallback(DateTime now)
    {
        if (!openingChestDiscoveredByChat || openingChestOpenedByChat || openingChestPortalByChat)
            return false;

        if (now - lastOpeningChestTargetCommandTime < OpeningChestTargetFallbackInterval)
            return false;

        lastOpeningChestTargetCommandTime = now;
        _plugin.AddDebugLog("[OpeningChest] Coffer discovered by chat but ObjectTable lookup failed - sending /target \"Treasure Coffer\" fallback.");
        CommandHelper.SendCommand("/target \"Treasure Coffer\"");
        return true;
    }

    private void HandleVisibleUntargetableOpeningChestCoffer(IGameObject chest, DateTime now)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var distance = player == null
            ? float.MaxValue
            : Vector3.Distance(player.Position, chest.Position);

        var portal = FindNearestPortal();
        if (portal != null)
        {
            _plugin.AddDebugLog(
                $"[OpeningChest] Coffer '{chest.Name.TextValue}' is untargetable, but portal is visible - proceeding to portal flow.");
            CheckForPortalAfterChest();
            return;
        }

        if (openingChestOpenedByChat || openingChestPortalByChat)
        {
            _plugin.AddDebugLog(
                $"[OpeningChest] Coffer '{chest.Name.TextValue}' is untargetable after chat evidence - proceeding to portal/completion flow.");
            CheckForPortalAfterChest();
            return;
        }

        if (now - lastOpeningChestUntargetableLogTime >= OpeningChestObjectScanLogInterval)
        {
            lastOpeningChestUntargetableLogTime = now;
            _plugin.AddDebugLog(
                $"[OpeningChest] Coffer visible but not targetable; waiting. name='{chest.Name.TextValue}' " +
                $"kind={chest.ObjectKind} targetable={chest.IsTargetable} dist={distance:F1}y XYZ {FormatVectorCompact(chest.Position)}.");
        }

        StateDetail = $"Waiting for coffer to become targetable ({distance:F1}y)...";
    }

    private bool TryRecoverMissingOpeningChestCoffer(
        DateTime now,
        bool inCombat,
        bool hasFlagRecoveryTarget,
        Vector3 flagRecoveryTarget,
        float distToFlag)
    {
        if (!ShouldExpectOpeningChestCoffer())
            return false;

        if (inCombat)
        {
            chestDisappearedTime = DateTime.MinValue;
            StateDetail = hasFlagRecoveryTarget
                ? $"In combat - waiting to recover missing coffer ({distToFlag:F1}y from flag)..."
                : "In combat - waiting to recover missing coffer...";
            return true;
        }

        if (openingChestMissingCofferRecoveryStartedAt == DateTime.MinValue)
        {
            openingChestMissingCofferRecoveryStartedAt = now;
            _plugin.AddDebugLog("[OpeningChest] Expected coffer is missing after dig - starting bounded ObjectTable/flag recovery.");
        }

        LogOpeningChestObjectTableDiagnostics(now, "expected coffer missing");

        if (TryReturnToOpeningChestLastKnownCoffer(now))
        {
            chestDisappearedTime = DateTime.MinValue;
            return true;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        var xzDistToFlag = hasFlagRecoveryTarget && player != null
            ? (float)CalculateXZDistance(player.Position, flagRecoveryTarget)
            : distToFlag;

        if (hasFlagRecoveryTarget && xzDistToFlag > MapDigXZRange)
        {
            var nav = _plugin.NavigationService;
            if (!nav.IsMounted() && !nav.IsFlying())
            {
                if (Plugin.Condition[ConditionFlag.Mounting71])
                {
                    StateDetail = $"Mounting to return to flag and recover coffer ({xzDistToFlag:F1}y XZ)...";
                    return true;
                }

                if (now - lastOpeningChestCofferMountCommandTime >= OpeningChestCofferMountCommandInterval)
                {
                    lastOpeningChestCofferMountCommandTime = now;
                    nav.MountUp();
                    _plugin.AddDebugLog($"[OpeningChest] Missing coffer recovery: mounting before long return to flag ({xzDistToFlag:F1}y XZ).");
                }

                StateDetail = $"Mounting to return to flag and recover coffer ({xzDistToFlag:F1}y XZ)...";
                return true;
            }

            var targetKey = BuildOverworldRecoveryPositionKey(
                "opening-missing-coffer-flag",
                Plugin.ClientState.TerritoryType,
                flagRecoveryTarget);
            if (TryRunOverworldRecoveryWatchdog(
                    now,
                    "OpeningChest",
                    "missing coffer flag",
                    targetKey,
                    Plugin.ClientState.TerritoryType,
                    flagRecoveryTarget,
                    OverworldRecoveryNavigationKind.FlyTo))
            {
                return true;
            }

            var navInactive = _plugin.NavigationService.State == NavigationState.Idle || !_plugin.VNavIPC.IsNavigating;
            if (navInactive)
            {
                if (_plugin.NavigationService.State != NavigationState.Idle)
                    _plugin.NavigationService.StopNavigation();

                _plugin.NavigationService.FlyToPosition(flagRecoveryTarget, force: true);
                autoMoveActive = true;
                lastOpeningChestCofferRepathTime = now;
                _plugin.AddDebugLog($"[OpeningChest] Missing coffer recovery: flying back to flag ({xzDistToFlag:F1}y XZ).");
            }

            StateDetail = $"Returning to flag to recover coffer ({xzDistToFlag:F1}y XZ)...";
            return true;
        }

        if (hasFlagRecoveryTarget)
        {
            if (autoMoveActive && _plugin.NavigationService.State != NavigationState.Idle)
            {
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
            }

            if (TryGuardNonDivingThiefMapRecoveryDig(
                    now,
                    "[OpeningChest] missing coffer recovery",
                    hasFlagRecoveryTarget,
                    flagRecoveryTarget,
                    xzDistToFlag))
            {
                return true;
            }

            var timeSinceDig = digIssuedAt == DateTime.MinValue
                ? TimeSpan.MaxValue
                : now - digIssuedAt;
            if (openingChestRecoveryDigRetryCount == 0 && timeSinceDig < OpeningChestInitialCofferWaitAfterDig)
            {
                StateDetail = $"Waiting for coffer after dig... ({timeSinceDig.TotalSeconds:F1}/{OpeningChestInitialCofferWaitAfterDig.TotalSeconds:F1}s)";
                return true;
            }

            var canRetryDig = openingChestRecoveryDigRetryCount < OpeningChestMissingCofferMaxDigRetries &&
                              now - lastOpeningChestRecoveryDigTime >= OpeningChestRecoveryDigInterval &&
                              now - lastDigTime >= TimeSpan.FromSeconds(3.0);
            if (canRetryDig)
            {
                openingChestRecoveryDigRetryCount++;
                lastOpeningChestRecoveryDigTime = now;
                lastDigTime = now;
                digIssuedThisMap = true;
                digIssuedAt = now;
                CommandHelper.SendCommand("/gaction dig");
                _plugin.AddDebugLog(
                    $"[OpeningChest] Missing coffer recovery: retrying /gaction dig near flag " +
                    $"({openingChestRecoveryDigRetryCount}/{OpeningChestMissingCofferMaxDigRetries}).");
                StateDetail = $"Retrying dig for missing coffer ({openingChestRecoveryDigRetryCount}/{OpeningChestMissingCofferMaxDigRetries})...";
                return true;
            }
        }

        var recoveryElapsed = now - openingChestMissingCofferRecoveryStartedAt;
        if (recoveryElapsed >= OpeningChestMissingCofferRecoveryTimeout)
        {
            FailOpeningChestMissingCofferRecovery(recoveryElapsed);
            return true;
        }

        StateDetail = hasFlagRecoveryTarget
            ? $"Recovering missing coffer near flag ({xzDistToFlag:F1}y XZ, retry {openingChestRecoveryDigRetryCount}/{OpeningChestMissingCofferMaxDigRetries})..."
            : $"Recovering missing coffer (retry {openingChestRecoveryDigRetryCount}/{OpeningChestMissingCofferMaxDigRetries})...";
        return true;
    }

    private void LogOpeningChestObjectTableDiagnostics(DateTime now, string reason)
    {
        if (now - lastOpeningChestObjectScanLogTime < OpeningChestObjectScanLogInterval)
            return;

        lastOpeningChestObjectScanLogTime = now;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            _plugin.AddDebugLog($"[OpeningChest] Object scan skipped ({reason}) - local player unavailable.");
            return;
        }

        try
        {
            var nearby = Plugin.ObjectTable
                .Where(obj => obj != null)
                .Select(obj => new
                {
                    Object = obj,
                    Name = obj.Name.TextValue,
                    Distance = Vector3.Distance(player.Position, obj.Position),
                })
                .Where(candidate =>
                    candidate.Distance <= OpeningChestNearbyObjectScanRange &&
                    (candidate.Object.ObjectKind == ObjectKind.EventObj ||
                     candidate.Object.ObjectKind == ObjectKind.Treasure ||
                     ChestDetectionService.IsSafeCofferName(candidate.Name)))
                .OrderBy(candidate => candidate.Distance)
                .Take(10)
                .ToList();

            if (nearby.Count == 0)
            {
                _plugin.AddDebugLog(
                    $"[OpeningChest] Nearby ObjectTable scan ({reason}) found no EventObj/Treasure within {OpeningChestNearbyObjectScanRange:F0}y.");
                return;
            }

            var details = string.Join(" | ", nearby.Select(candidate =>
                $"'{candidate.Name}' kind={candidate.Object.ObjectKind} targetable={candidate.Object.IsTargetable} " +
                $"dist={candidate.Distance:F1}y xyz={FormatVectorCompact(candidate.Object.Position)}"));
            _plugin.AddDebugLog($"[OpeningChest] Nearby ObjectTable scan ({reason}): {details}");
        }
        catch (Exception ex)
        {
            _plugin.AddDebugLog($"[OpeningChest] Nearby ObjectTable scan failed ({reason}): {ex.Message}");
        }
    }

    private void FailOpeningChestMissingCofferRecovery(TimeSpan recoveryElapsed)
    {
        StopOpeningChestCofferMovement("after bounded missing-coffer recovery failure");
        var message =
            $"Treasure coffer was expected after /dig but could not be resolved after {recoveryElapsed.TotalSeconds:F0}s " +
            $"and {openingChestRecoveryDigRetryCount}/{OpeningChestMissingCofferMaxDigRetries} dig retries. Manual intervention required.";
        _plugin.AddDebugLog($"[OpeningChest] {message}");
        SetWarning(message);
        TransitionTo(BotState.Error, "Treasure coffer recovery failed after dig. Manual intervention required.");
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

    private bool IsPortalGroundApproachFailedTarget(uint entityId, Vector3 target)
    {
        if (entityId != 0 &&
            portalGroundApproachFailedEntityId != 0 &&
            portalGroundApproachFailedEntityId == entityId)
        {
            return true;
        }

        return portalGroundApproachFailedMarker.HasValue &&
               Vector3.DistanceSquared(portalGroundApproachFailedMarker.Value, target) <= 1.0f;
    }

    private bool IsActivePortalGroundApproachTarget(uint entityId, Vector3 target)
    {
        if (portalGroundApproachTarget == null)
            return false;

        if (entityId != 0 &&
            portalGroundApproachEntityId != 0 &&
            portalGroundApproachEntityId == entityId)
        {
            return true;
        }

        return Vector3.DistanceSquared(portalGroundApproachTarget.Value, target) <= 1.0f;
    }

    private bool ShouldUsePortalGroundApproach(uint entityId, Vector3 target, Vector3 playerPosition)
    {
        if (Plugin.Condition[ConditionFlag.Diving] ||
            IsThiefUnderwaterLandingMode())
        {
            return false;
        }

        if (IsPortalGroundApproachFailedTarget(entityId, target))
            return false;

        var distance = Vector3.Distance(playerPosition, target);
        var xzDistance = (float)CalculateXZDistance(playerPosition, target);
        var yDistance = Math.Abs(playerPosition.Y - target.Y);
        var nearEnough = distance <= OpeningChestCofferWalkPreferredDistance ||
                         xzDistance <= OpeningChestCofferWalkPreferredDistance;

        return nearEnough && yDistance <= OpeningChestCofferGroundApproachYDelta;
    }

    private bool TryRunPortalGroundApproach(uint entityId, Vector3 target, DateTime now, string source)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            StateDetail = "Waiting for player before portal ground approach...";
            return true;
        }

        if (!ShouldUsePortalGroundApproach(entityId, target, player.Position) &&
            !IsActivePortalGroundApproachTarget(entityId, target))
        {
            return false;
        }

        var distance = Vector3.Distance(player.Position, target);
        var xzDistance = (float)CalculateXZDistance(player.Position, target);
        var yDistance = Math.Abs(player.Position.Y - target.Y);
        if (distance <= PortalInteractionRange)
            return false;

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            StateDetail = $"In combat - waiting before portal ground approach ({distance:F1}y)...";
            return true;
        }

        var mountedOrFlyingOrMounting = _plugin.NavigationService.IsMounted()
            || _plugin.NavigationService.IsFlying()
            || Plugin.Condition[ConditionFlag.Mounting71];
        if (mountedOrFlyingOrMounting)
        {
            StopPortalMovementBeforeVnav();
            CommandHelper.SendCommand("/automove off");
            autoMoveActive = false;

            if (!Plugin.Condition[ConditionFlag.Mounting71] &&
                now - lastPortalDismountCommandTime >= PortalDismountCommandInterval)
            {
                lastPortalDismountCommandTime = now;
                _mountService.Dismount();
                _plugin.AddDebugLog(
                    $"[Portal] Near portal {source} at {distance:F1}y, Y {yDistance:F1}y - dismounting for on-foot vnav approach.");
            }

            StateDetail = $"Dismounting for portal ground approach ({distance:F1}y, Y {yDistance:F1}y)...";
            return true;
        }

        if (!IsActivePortalGroundApproachTarget(entityId, target))
        {
            ResetOpeningChestFlagFallback("before near portal ground approach", logIfActive: true);
            ResetPortalGroundApproachTracking();
            portalGroundApproachEntityId = entityId;
            portalGroundApproachTarget = target;
            portalGroundApproachStartedAt = now;
            portalGroundApproachLastProgressTime = now;
            portalGroundApproachBestDistance = distance;
            lastPortalGroundApproachRepathTime = now;
            StopPortalMovementBeforeVnav();
            CommandHelper.SendCommand("/automove off");
            _plugin.NavigationService.MoveToPosition(target);
            autoMoveActive = true;
            _plugin.AddDebugLog(
                $"[Portal] Near portal {source} at {FormatVectorCompact(target)} ({distance:F1}y, XZ {xzDistance:F1}y, Y {yDistance:F1}y) - starting on-foot vnav ground approach.");
            StateDetail = $"Ground-approaching portal ({distance:F1}y, Y {yDistance:F1}y)...";
            return true;
        }

        if (distance + OpeningChestCofferProgressMargin < portalGroundApproachBestDistance)
        {
            portalGroundApproachBestDistance = distance;
            portalGroundApproachLastProgressTime = now;
        }

        var elapsed = now - portalGroundApproachStartedAt;
        var noProgressFor = now - portalGroundApproachLastProgressTime;
        var hitHardCap = elapsed >= GroundApproachHardTimeout;
        var hitNoProgressCap = elapsed >= GroundApproachMinimumDuration &&
                               noProgressFor >= GroundApproachNoProgressTimeout;
        if (hitHardCap || hitNoProgressCap)
        {
            var bestDistance = portalGroundApproachBestDistance;
            CommandHelper.SendCommand("/automove off");
            if (_plugin.NavigationService.State != NavigationState.Idle)
                _plugin.NavigationService.StopNavigation();
            autoMoveActive = false;
            portalGroundApproachFailedEntityId = entityId;
            portalGroundApproachFailedMarker = target;
            ResetPortalGroundApproachTracking();
            _plugin.AddDebugLog(
                $"[Portal] Ground approach to {source} failed after {elapsed.TotalSeconds:F1}s " +
                $"(best {bestDistance:F1}y, current {distance:F1}y, no progress {noProgressFor.TotalSeconds:F1}s) - allowing mounted portal recovery.");
            StateDetail = $"Portal ground approach failed ({distance:F1}y) - preparing mounted recovery...";
            return false;
        }

        var navInactive = _plugin.NavigationService.State == NavigationState.Idle ||
                          !_plugin.VNavIPC.IsNavigating;
        if (!autoMoveActive ||
            navInactive ||
            now - lastPortalGroundApproachRepathTime >= OpeningChestCofferRepathInterval)
        {
            if (navInactive && _plugin.NavigationService.State != NavigationState.Idle)
                _plugin.NavigationService.StopNavigation();

            _plugin.NavigationService.MoveToPosition(target);
            autoMoveActive = true;
            lastPortalGroundApproachRepathTime = now;
        }

        StateDetail = $"Ground-approaching portal ({distance:F1}y, XZ {xzDistance:F1}y, Y {yDistance:F1}y, {elapsed.TotalSeconds:F0}s)...";
        return true;
    }

    private bool FlyToPortalApproachPosition(Vector3 position, float distance, bool force = false)
    {
        SyncOpeningChestFlagFallbackKey(OpeningChestFlagFallbackKind.Portal, 0, position);
        if (TryRunOpeningChestFlagFallback(OpeningChestFlagFallbackKind.Portal, 0, position, DateTime.Now))
            return true;

        if (!EnsurePortalFlyApproachMounted())
            return false;

        var now = DateTime.Now;
        if (portalApproachStartedAt != DateTime.MinValue
                 && now - portalApproachStartedAt >= PortalRunawayCheckDelay
                 && distance >= portalApproachStartDistance + PortalRunawayDistanceIncrease
                 && now - lastPortalRepathTime >= PortalRepathInterval)
        {
            if (TryStartOpeningChestFlagFallback(
                    OpeningChestFlagFallbackKind.Portal,
                    0,
                    position,
                    position,
                    distance,
                    $"portal approach distance grew from {portalApproachStartDistance:F1}y",
                    now))
            {
                return true;
            }

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

            if (TryStartOpeningChestFlagFallback(
                    OpeningChestFlagFallbackKind.Portal,
                    0,
                    position,
                    position,
                    distance,
                    "portal vnav inactive before range",
                    now))
            {
                return true;
            }

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
            GameHelpers.KeyRelease(VirtualKey.W);
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

    private bool IsPortalInteractionMountBlocked()
    {
        var isDiving = Plugin.Condition[ConditionFlag.Diving];
        return Plugin.Condition[ConditionFlag.Mounted]
            || Plugin.Condition[ConditionFlag.Mounting71]
            || (Plugin.Condition[ConditionFlag.InFlight] && !isDiving);
    }

    private void PrepareDismountedUnderwaterPortalInteraction()
    {
        if (!Plugin.Condition[ConditionFlag.Diving])
            return;

        CommandHelper.SendCommand("/automove off");
        GameHelpers.KeyRelease(VirtualKey.W);
        GameHelpers.KeyRelease(VirtualKey.CONTROL);
        GameHelpers.KeyRelease(VirtualKey.SPACE);
        descentMode = false;
        descentInProgress = false;
        underwaterTargetPosition = Vector3.Zero;

        if (!portalUnderwaterReadyLogged)
        {
            portalUnderwaterReadyLogged = true;
            _plugin.AddDebugLog("[Portal] Underwater portal ready while dismounted - released descent and interacting without dismount.");
        }
    }

    private bool AttemptPortalInteraction(
        IGameObject portal,
        Vector3 approachPosition,
        DateTime now,
        bool? forceCameraRaycast = null,
        string reason = "")
    {
        var nextAttempt = portalInteractionAttemptCount + 1;
        var useCameraRaycast = forceCameraRaycast ?? (nextAttempt - 1) % 2 == 0;
        if (useCameraRaycast &&
            TryHoldForCameraResetBeforeInteract(
                "[Portal]",
                portal,
                now,
                ref portalCameraResetEntityId,
                ref portalCameraResetReadyAt))
        {
            return false;
        }

        portalInteractionAttemptCount = nextAttempt;
        var methodName = useCameraRaycast
            ? "TargetSystem(camera+reset)"
            : "TargetSystem(no-camera)";
        _plugin.AddDebugLog(
            $"[Portal] Interaction attempt #{portalInteractionAttemptCount} via {methodName} for '{portal.Name.TextValue}' at XYZ {FormatVectorCompact(approachPosition)}{reason}.");
        var interacted = GameHelpers.InteractWithObject(portal, useCameraRaycast);
        RecordPortalInteractionAttempt(portal, approachPosition, now);
        _plugin.AddDebugLog(
            $"[Portal] Interaction attempt #{portalInteractionAttemptCount} {methodName} returned: {interacted}");
        return true;
    }

    private void RecordPortalInteractionAttempt(IGameObject portal, Vector3 portalPosition, DateTime now)
    {
        if (portalInteractionEntityId != 0 && portalInteractionEntityId != portal.EntityId)
        {
            ResetPortalNoDialogAttemptWindow(DateTime.MinValue);
            portalInteractionBestDistance = float.MaxValue;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player != null)
        {
            portalInteractionLastPlayerPosition = player.Position;
            portalInteractionLastPortalPosition = portalPosition;
            portalInteractionLastDistance = Vector3.Distance(player.Position, portalPosition);
            portalInteractionLastXzDistance = (float)CalculateXZDistance(player.Position, portalPosition);
            portalInteractionLastYDistance = Math.Abs(player.Position.Y - portalPosition.Y);

            if (portalInteractionLastDistance < portalInteractionBestDistance - PortalInteractionProgressMargin)
            {
                portalInteractionBestDistance = portalInteractionLastDistance;
                ResetPortalNoDialogAttemptWindow(now);
            }
        }
        else
        {
            portalInteractionLastPlayerPosition = default;
            portalInteractionLastPortalPosition = portalPosition;
            portalInteractionLastDistance = float.MaxValue;
            portalInteractionLastXzDistance = float.MaxValue;
            portalInteractionLastYDistance = float.MaxValue;
        }

        if (portalInteractionFirstAttemptAt == DateTime.MinValue)
            portalInteractionFirstAttemptAt = now;

        portalInteractionEntityId = portal.EntityId;
        portalInteractionLastAttemptAt = now;
        portalInteractionAttemptsSinceProgress++;
        LogPortalNoDialogDiagnostics(now, $"attempt #{portalInteractionAttemptCount}");
    }

    private void MarkPortalInteractionProgress(DateTime now, string source)
    {
        var hadPendingInteraction = portalInteractionAttemptsSinceProgress > 0 ||
                                    portalInteractionFirstAttemptAt != DateTime.MinValue ||
                                    portalCloseNudgeActive;
        ResetPortalNoDialogAttemptWindow(now);
        ResetPortalCloseNudgeTracking(stopMovement: false);
        ResetPortalGroundApproachTracking();
        lastPortalStuckDiagnosticLogTime = DateTime.MinValue;
        if (hadPendingInteraction)
            _plugin.AddDebugLog($"[Portal] Interaction progress observed via {source}; reset no-dialog attempt window.");
    }

    private bool HasPortalInteractionAttemptFor(IGameObject portal)
    {
        return portalInteractionEntityId == portal.EntityId
            && (portalInteractionAttemptsSinceProgress > 0 || portalInteractionFirstAttemptAt != DateTime.MinValue);
    }

    private bool ShouldRunPortalNoDialogRecovery(IGameObject portal, DateTime now, float portalDist, out string reason)
    {
        reason = string.Empty;
        if (portalRetryStart == DateTime.MinValue || portalDist > PortalApproachInteractionRange)
            return false;

        if (!HasPortalInteractionAttemptFor(portal))
            return false;

        if (portalInteractionAttemptsSinceProgress >= PortalNoDialogRecoveryAttemptThreshold)
        {
            reason = $"{portalInteractionAttemptsSinceProgress} interaction attempts without dialog/loading";
            return true;
        }

        if (portalInteractionFirstAttemptAt != DateTime.MinValue &&
            now - portalInteractionFirstAttemptAt >= PortalNoDialogRecoveryTimeout)
        {
            reason = $"{(now - portalInteractionFirstAttemptAt).TotalSeconds:F1}s without dialog/loading";
            return true;
        }

        return false;
    }

    private bool TryRunPortalCloseNudgeRecovery(IGameObject portal, Vector3 approachPosition, float portalDist, DateTime now)
    {
        if (portalCloseNudgeActive && portalCloseNudgeEntityId != portal.EntityId)
        {
            ResetPortalCloseNudgeTracking(stopMovement: true);
            return false;
        }

        var reason = "active close nudge";
        if (!portalCloseNudgeActive && !ShouldRunPortalNoDialogRecovery(portal, now, portalDist, out reason))
            return false;

        if (!portalCloseNudgeActive)
        {
            LogPortalNoDialogDiagnostics(now, reason);
            BeginPortalCloseNudge(portal, now, reason);
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            StateDetail = "Portal close nudge waiting for local player...";
            return true;
        }

        var currentDist = Vector3.Distance(player.Position, approachPosition);
        var currentXz = (float)CalculateXZDistance(player.Position, approachPosition);
        var currentY = Math.Abs(player.Position.Y - approachPosition.Y);
        var elapsed = now - portalCloseNudgeStartedAt;
        var reachedStrictRange = currentDist <= PortalStrictInteractionRange || currentXz <= PortalStrictInteractionRange;
        var timedOut = elapsed >= PortalCloseNudgeTimeout;

        var mountedOrFlyingOrMounting = _plugin.NavigationService.IsMounted()
            || _plugin.NavigationService.IsFlying()
            || Plugin.Condition[ConditionFlag.Mounting71];
        if (mountedOrFlyingOrMounting)
        {
            StopPortalMovementBeforeVnav();
            CommandHelper.SendCommand("/automove off");
            autoMoveActive = false;

            if (!Plugin.Condition[ConditionFlag.Mounting71] &&
                now - lastPortalDismountCommandTime >= PortalDismountCommandInterval)
            {
                lastPortalDismountCommandTime = now;
                _mountService.Dismount();
                _plugin.AddDebugLog(
                    $"[Portal] Close nudge requires ground approach - dismounting at {currentDist:F1}y, Y {currentY:F1}y.");
            }

            StateDetail = $"Dismounting for portal close nudge ({currentDist:F1}y, Y {currentY:F1}y)...";
            return true;
        }

        if (!reachedStrictRange && !timedOut)
        {
            var navInactive = _plugin.NavigationService.State == NavigationState.Idle ||
                              !_plugin.VNavIPC.IsNavigating;
            if (navInactive || now - portalCloseNudgeLastCommandAt >= PortalCloseNudgeCommandInterval)
            {
                Plugin.TargetManager.Target = portal;
                if (navInactive && _plugin.NavigationService.State != NavigationState.Idle)
                    _plugin.NavigationService.StopNavigation();

                _plugin.NavigationService.MoveToPosition(approachPosition);
                autoMoveActive = true;
                portalCloseNudgeLastCommandAt = now;
            }

            StateDetail = $"Ground-nudging closer to portal ({currentDist:F1}y, XZ {currentXz:F1}y)...";
            return true;
        }

        CommandHelper.SendCommand("/automove off");
        if (_plugin.NavigationService.State != NavigationState.Idle)
            _plugin.NavigationService.StopNavigation();
        autoMoveActive = false;
        ResetPortalCloseNudgeTracking(stopMovement: false);
        ResetPortalNoDialogAttemptWindow(now);
        portalInteractionBestDistance = currentDist;

        _plugin.AddDebugLog(
            $"[Portal] Close nudge {(reachedStrictRange ? "reached strict range" : "timed out")} after {elapsed.TotalSeconds:F1}s: " +
            $"player={FormatVectorCompact(player.Position)} portal={FormatVectorCompact(approachPosition)} " +
            $"dist={currentDist:F1}y xz={currentXz:F1}y y={currentY:F1}y. Retrying direct TargetSystem.");

        if (IsCharacterReady())
        {
            Plugin.TargetManager.Target = portal;
            if (AttemptPortalInteraction(portal, approachPosition, now, forceCameraRaycast: false, reason: " after close nudge"))
                lastInteractionTime = now;
        }
        else
        {
            StateDetail = $"Waiting to retry portal after close nudge ({DescribeCharacterReadyBlockers()})...";
        }

        return true;
    }

    private void BeginPortalCloseNudge(IGameObject portal, DateTime now, string reason)
    {
        StopPortalConflictingMovement();
        ResetPortalCameraResetBeforeInteractTracking();
        portalCloseNudgeActive = true;
        portalCloseNudgeEntityId = portal.EntityId;
        portalCloseNudgeStartedAt = now;
        portalCloseNudgeLastCommandAt = DateTime.MinValue;
        portalCloseNudgeCount++;
        _plugin.AddDebugLog($"[Portal] Ground close nudge #{portalCloseNudgeCount} started after {reason}; target entity={portal.EntityId}.");
    }

    private void LogPortalNoDialogDiagnostics(DateTime now, string reason)
    {
        if (now - lastPortalStuckDiagnosticLogTime < PortalNoDialogDiagnosticInterval)
            return;

        lastPortalStuckDiagnosticLogTime = now;
        var dialogWait = portalInteractionFirstAttemptAt == DateTime.MinValue
            ? 0.0
            : (now - portalInteractionFirstAttemptAt).TotalSeconds;
        var vnavRunning = _plugin.VNavIPC.TryIsRunning();
        var vnavState = vnavRunning.HasValue ? vnavRunning.Value.ToString() : "unknown";
        _plugin.AddDebugLog(
            $"[Portal] No-dialog diagnostic ({reason}): attemptsSinceProgress={portalInteractionAttemptsSinceProgress}, " +
            $"firstAttempt={FormatElapsedSince(portalInteractionFirstAttemptAt, now)}, " +
            $"lastAttempt={FormatElapsedSince(portalInteractionLastAttemptAt, now)}, dialogWait={dialogWait:F1}s, " +
            $"player={FormatVectorCompact(portalInteractionLastPlayerPosition)} portal={FormatVectorCompact(portalInteractionLastPortalPosition)} " +
            $"dist={portalInteractionLastDistance:F1}y xz={portalInteractionLastXzDistance:F1}y y={portalInteractionLastYDistance:F1}y, " +
            $"nav={_plugin.NavigationService.State}, vnavRunning={vnavState}, autoMove={autoMoveActive}, closeNudges={portalCloseNudgeCount}.");
    }

    private static string FormatElapsedSince(DateTime timestamp, DateTime now)
        => timestamp == DateTime.MinValue ? "never" : $"{(now - timestamp).TotalSeconds:F1}s ago";

    private void TryPortalApproachInteraction(IGameObject portal, Vector3 approachPosition, float portalDist, DateTime now)
    {
        if (portalDist > PortalApproachInteractionRange)
            return;

        Plugin.TargetManager.Target = portal;
        if ((now - lastInteractionTime).TotalSeconds < 1.0)
            return;

        _plugin.AddDebugLog(
            $"[Portal] Within approach interaction band ({portalDist:F1}y <= {PortalApproachInteractionRange:F1}y) - trying portal interaction while vnav continues.");
        if (TryRunPortalCloseNudgeRecovery(portal, approachPosition, portalDist, now))
            return;

        if (AttemptPortalInteraction(portal, approachPosition, now))
            lastInteractionTime = now;
    }

    private void HandlePortalInInteractionRange(IGameObject portal, Vector3 approachPosition, float portalDist, DateTime now)
    {
        CommandHelper.SendCommand("/automove off");
        autoMoveActive = false;
        portalApproachStartedAt = DateTime.MinValue;
        portalApproachStartDistance = float.MaxValue;
        ResetPortalGroundApproachTracking();

        if (_plugin.NavigationService.State != NavigationState.Idle)
        {
            _plugin.NavigationService.StopNavigation();
            _plugin.AddDebugLog($"[Portal] Within {PortalInteractionRange:F1}y - stopped vnav before portal interaction handoff.");
        }

        if (IsPortalInteractionMountBlocked())
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

        PrepareDismountedUnderwaterPortalInteraction();

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
            if (TryRunPortalCloseNudgeRecovery(portal, approachPosition, portalDist, now))
                return;

            if (AttemptPortalInteraction(portal, approachPosition, now))
                lastInteractionTime = now;
            else
                return;
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

        var mapSources = _plugin.InventoryService.ScanForMapSources(
            includeSaddlebags: _plugin.Configuration.EnableSaddlebagMapRetrieval);
        var maps = mapSources
            .Where(kvp => kvp.Value.Inventory > 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Inventory);
        mapScanCounter++;
        
        // Only log every 5 scans to reduce spam
        if (mapScanCounter % 5 == 1)
        {
            _plugin.AddDebugLog($"[TICK] Scanning inventory... Found {maps.Count} different map types (scan #{mapScanCounter})");
        }

        if (TrySelectPendingAlexandriteInventoryMap(mapSources))
            return;
        
        if (maps.Count == 0)
        {
            var saddlebagCandidates = GetEnabledMapCandidates(mapSources, includeInventory: false, includeSaddlebags: true);
            if (saddlebagCandidates.Count > 0)
            {
                _plugin.AddDebugLog($"[SelectingMap] No inventory maps. Retrieving enabled saddlebag map ID {saddlebagCandidates[0]}.");
                TryRetrieveSaddlebagMap(saddlebagCandidates[0]);
                return;
            }

            var enabledForRetainers = _plugin.Configuration.GetRunnableMapIds(TreasureMapData.AllMapItemIds);
            _plugin.AddDebugLog($"[SelectingMap] No inventory/saddlebag maps. Checking retainer maps via XADB for {FormatMapIds(enabledForRetainers)}.");
            if (TryRetrieveRetainerMap(enabledForRetainers, "No maps found in inventory, saddlebags, or retainers.", allowGatherFallback: true))
                return;

            return;
        }

        ClearWarning();

        var enabled = _plugin.Configuration.GetRunnableMapIds(TreasureMapData.AllMapItemIds);

        // Filter to only enabled map types.
        var candidates = maps
            .Where(kvp => _plugin.Configuration.IsMapTypeEnabled(kvp.Key) && kvp.Value > 0)
            .Select(kvp => kvp.Key)
            .ToList();

        if (candidates.Count == 0)
        {
            _plugin.AddDebugLog(
                $"[SelectingMap] No enabled local maps. Enabled={FormatMapIds(enabled)}; local map types={maps.Count}.");
            var saddlebagCandidates = GetEnabledMapCandidates(mapSources, includeInventory: false, includeSaddlebags: true);
            if (saddlebagCandidates.Count > 0)
            {
                _plugin.AddDebugLog($"[SelectingMap] Retrieving enabled saddlebag map ID {saddlebagCandidates[0]}.");
                TryRetrieveSaddlebagMap(saddlebagCandidates[0]);
                return;
            }

            _plugin.AddDebugLog("[SelectingMap] No enabled saddlebag maps. Checking enabled retainer maps via XADB.");
            if (TryRetrieveRetainerMap(enabled,
                    "No enabled maps in inventory, saddlebags, or retainers. Check map selection in UI.",
                    allowGatherFallback: true))
                return;

            return;
        }

        if (_plugin.RetainerMapRetrievalService.TryCloseRetainerUiBeforeMapOpen(out var closeRetainerStatus))
        {
            StateDetail = closeRetainerStatus;
            lastMapScanTime = DateTime.MinValue;
            return;
        }

        // Don't sort - use inventory order to match menu order
        SelectedMapItemId = candidates[0];
        var mapName = TreasureMapData.KnownMaps.TryGetValue(SelectedMapItemId, out var info) ? info.Name : $"ID {SelectedMapItemId}";
        NormalizeLandingModeForSelectedMap("[SelectingMap]");
        _plugin.AddDebugLog($"Selected: {mapName} (ID {SelectedMapItemId}).");
        _plugin.AddDebugLog($"[Landing] SelectedMapItemId={SelectedMapItemId}; LandingMode={currentLandingMode}.");
        if (IsThiefUnderwaterLandingMode())
            LogThiefWaterInfo($"[Underwater] Thief map selected; using {currentLandingMode} landing mode for map ID {SelectedMapItemId}.");
        
        // Initialize map count validation variables
        initialMapCount = _plugin.InventoryService.GetMapCount(SelectedMapItemId);
        mapCountChecked = false;
        mapOpeningRetried = false;
        MarkSelectedMapRunCountPending("[SelectingMap]");
        ClearCompletedStaleKeyItemSuppression("[SelectingMap] starting fresh map");
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

    private bool TrySelectPendingAlexandriteInventoryMap(Dictionary<uint, MapSourceCount> mapSources)
    {
        if (pendingAlexandriteMapTargetItemId == 0)
            return false;

        var targetItemId = pendingAlexandriteMapTargetItemId;
        if (targetItemId != MysteriousMapItemId)
        {
            pendingAlexandriteMapTargetItemId = 0;
            return false;
        }

        var mapName = TreasureMapData.KnownMaps.TryGetValue(targetItemId, out var info)
            ? info.Name
            : $"ID {targetItemId}";
        var inventoryCount = mapSources.TryGetValue(targetItemId, out var sourceCount)
            ? sourceCount.Inventory
            : 0;

        if (inventoryCount <= 0)
        {
            FailAlexandrite($"[Alexandrite] Expected {mapName} in inventory for normal map handoff, but it was not found.");
            return true;
        }

        ClearWarning();

        if (_plugin.RetainerMapRetrievalService.TryCloseRetainerUiBeforeMapOpen(out var closeRetainerStatus))
        {
            StateDetail = closeRetainerStatus;
            lastMapScanTime = DateTime.MinValue;
            return true;
        }

        SelectedMapItemId = targetItemId;
        NormalizeLandingModeForSelectedMap("[Alexandrite] map target");
        _plugin.AddDebugLog($"[Alexandrite] Selected pending normal map target: {mapName} (ID {SelectedMapItemId}).");
        _plugin.AddDebugLog($"[Landing] SelectedMapItemId={SelectedMapItemId}; LandingMode={currentLandingMode}.");

        initialMapCount = _plugin.InventoryService.GetMapCount(SelectedMapItemId);
        mapCountChecked = false;
        mapOpeningRetried = false;
        ClearSelectedMapRunCountDecrement("[Alexandrite] map target");
        ClearCompletedStaleKeyItemSuppression("[Alexandrite] starting fresh map");
        ResetPerMapCommandTriggers();
        _plugin.AddDebugLog($"[Alexandrite] Initial pending target map count: {initialMapCount}");

        var loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                      Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (!loading)
        {
            var cleared = GameHelpers.ClearMapFlag(_plugin.MapFlagService.TryReadFlag);
            _plugin.AddDebugLog($"[Alexandrite] Cleared existing map flag before normal opening (verified={cleared}).");
        }
        else
        {
            _plugin.AddDebugLog("[Alexandrite] Skipping flag clear during zone transition.");
        }

        TransitionTo(BotState.OpeningMap, $"Opening {mapName}...");
        return true;
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
        if (ClickYesIfVisibleWithDiagnostics("OpeningMap.decipher-confirm"))
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

    private void QueueAreaMapAutoCloseAfterTreasureCapture(string source)
    {
        if (!_plugin.Configuration.Enabled ||
            State is not (BotState.OpeningMap or BotState.DetectingLocation))
        {
            return;
        }

        if (!areaMapAutoCloseQueued)
        {
            areaMapAutoCloseQueued = true;
            areaMapAutoCloseQueuedAt = DateTime.Now;
            areaMapAutoCloseLastAttemptAt = DateTime.MinValue;
            areaMapAutoCloseAttemptCount = 0;
            _plugin.AddDebugLog($"[AreaMapClose] Queued after treasure map capture ({source}).");
        }

        TickAreaMapAutoClose();
    }

    private void TickAreaMapAutoClose()
    {
        if (!areaMapAutoCloseQueued)
            return;

        if (!_plugin.Configuration.Enabled)
        {
            ResetAreaMapAutoClose();
            return;
        }

        if (IsAreaTransitionActive())
            return;

        if (!GameHelpers.IsAddonVisible("AreaMap"))
        {
            ResetAreaMapAutoClose();
            return;
        }

        var now = DateTime.Now;
        if (now - areaMapAutoCloseQueuedAt > AreaMapAutoCloseTimeout ||
            areaMapAutoCloseAttemptCount >= AreaMapAutoCloseMaxAttempts)
        {
            _plugin.AddDebugLog($"[AreaMapClose] Gave up after {areaMapAutoCloseAttemptCount} attempts.");
            ResetAreaMapAutoClose();
            return;
        }

        if (now - areaMapAutoCloseLastAttemptAt < AreaMapAutoCloseRetryInterval)
            return;

        areaMapAutoCloseLastAttemptAt = now;
        areaMapAutoCloseAttemptCount++;
        var closed = GameHelpers.TryCloseAddonByCallback("AreaMap");
        _plugin.AddDebugLog($"[AreaMapClose] Attempt {areaMapAutoCloseAttemptCount}: callback result={closed}.");
    }

    private void ResetAreaMapAutoClose()
    {
        areaMapAutoCloseQueued = false;
        areaMapAutoCloseQueuedAt = DateTime.MinValue;
        areaMapAutoCloseLastAttemptAt = DateTime.MinValue;
        areaMapAutoCloseAttemptCount = 0;
    }

    private void PopulateNearestAetheryte(
        MapLocation location,
        out uint aetheryteId,
        out double bestAethDist,
        out bool usedXyz)
    {
        var flagPos = new Vector3(location.X, location.Y, location.Z);
        aetheryteId = _plugin.NavigationService.FindNearestAetheryte(location.TerritoryId, flagPos, out bestAethDist, out usedXyz);
        location.NearestAetheryteId = aetheryteId;

        if (aetheryteId == 0)
            return;

        try
        {
            var aetheryteSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
            if (aetheryteSheet != null)
            {
                var aetheryte = aetheryteSheet.GetRow(aetheryteId);
                location.NearestAetheryteName = aetheryte.PlaceName.ValueNullable?.Name.ToString() ?? $"ID {aetheryteId}";
            }
        }
        catch
        {
        }
    }

    private void RouteSameTerritoryMapTarget(
        MapLocation location,
        uint aetheryteId,
        double bestAethDist,
        bool usedXyz,
        string logPrefix,
        string closeTransitionDetail,
        string mountedTransitionDetail,
        string mountTransitionDetail,
        bool allowSameZoneTeleport = false)
    {
        NormalizeLandingModeForSelectedMap($"{logPrefix} same-zone route");

        var flagPos = new Vector3(location.X, location.Y, location.Z);
        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var resolvedTargets = ResolveOverworldNavigationTargets();
        var resolvedLandingTarget = resolvedTargets.LandingTarget != Vector3.Zero
            ? resolvedTargets.LandingTarget
            : flagPos;
        var playerXZDistToLanding = CalculateXZDistance(playerPos, resolvedLandingTarget);
        var playerDist = playerXZDistToLanding;
        var targetBasis = resolvedTargets.Basis == "none"
            ? (usedXyz ? "stored XYZ" : "flag XZ")
            : resolvedTargets.Basis;

        _plugin.AddDebugLog(
            $"{logPrefix} Already in zone: player landing XZ dist={playerDist:F0}y ({targetBasis}), " +
            $"best aetheryte dist={bestAethDist:F0}y");
        _plugin.AddDebugLog($"{logPrefix} Player pos: ({playerPos.X:F1}, {playerPos.Y:F1}, {playerPos.Z:F1}), Aetheryte ID: {aetheryteId}");

        var landingRange = GetCurrentMapLandingHoldRange();
        if (playerXZDistToLanding <= landingRange)
        {
            _plugin.AddDebugLog(
                $"{logPrefix} Already within landing range ({playerXZDistToLanding:F1}y XZ <= {landingRange:F1}y) " +
                $"of {FormatVectorCompact(resolvedLandingTarget)} ({targetBasis}) - landing/digging without teleport or mount setup.");
            TransitionTo(BotState.Flying, closeTransitionDetail);
            return;
        }

        var selectedAetheryteName = aetheryteId != 0
            ? GetAetheryteName(aetheryteId)
            : "none";
        var playerXZDistToAetheryte = aetheryteId != 0
            ? _plugin.NavigationService.GetPlayerXZDistanceToAetheryte(aetheryteId)
            : null;
        var bestAethDistText = bestAethDist == double.MaxValue ? "infinity" : $"{bestAethDist:F0}y";
        var playerDistToSelectedText = playerXZDistToAetheryte is { } selectedDistance
            ? $"{selectedDistance:F1}y"
            : "unknown";

        if (allowSameZoneTeleport)
        {
            _plugin.AddDebugLog(
                $"{logPrefix} Same-zone teleport check: playerToTarget={playerDist:F0}y; " +
                $"bestAetheryteToTarget={bestAethDistText}; selectedAetheryte={selectedAetheryteName} (ID {aetheryteId}); " +
                $"playerToSelectedAetheryte={playerDistToSelectedText}.");

            var selectedAetheryteIsCloser =
                aetheryteId != 0 &&
                bestAethDist != double.MaxValue &&
                bestAethDist < playerDist;

            if (selectedAetheryteIsCloser)
            {
                if (TryGetSameZoneTeleportProgressBlockReason(playerPos, out var progressBlockReason))
                {
                    _plugin.AddDebugLog(
                        $"{logPrefix} Selected aetheryte {selectedAetheryteName} (ID {aetheryteId}) is closer to target " +
                        $"({bestAethDist:F0}y < {playerDist:F0}y), but same-zone teleport is suppressed: {progressBlockReason}.");
                }
                else if (playerXZDistToAetheryte is { } aetherytePlayerDistance
                    && aetherytePlayerDistance <= SameZoneAetheryteTeleportSkipXZRange)
                {
                    _plugin.AddDebugLog(
                        $"{logPrefix} Selected aetheryte {selectedAetheryteName} (ID {aetheryteId}) is closer to target " +
                        $"({bestAethDist:F0}y < {playerDist:F0}y), but player is already within {SameZoneAetheryteTeleportSkipXZRange:F0}y XZ " +
                        $"of that aetheryte ({aetherytePlayerDistance:F1}y) - skipping same-zone teleport.");
                }
                else
                {
                    _plugin.AddDebugLog(
                        $"{logPrefix} Selected aetheryte {selectedAetheryteName} (ID {aetheryteId}) is closer to target " +
                        $"({bestAethDist:F0}y < {playerDist:F0}y); playerToSelectedAetheryte={playerDistToSelectedText} - teleporting.");
                    TransitionTo(BotState.Teleporting, $"In zone but aetheryte closer ({bestAethDist:F0}y vs {playerDist:F0}y) - teleporting...");
                    return;
                }
            }
        }

        if (IsMountedOrActualInFlight())
        {
            if (TryHoldSameTerritoryTakeoffForParty(logPrefix))
                return;

            overworldRecoveryRequiresPartyMountWait = false;
            _plugin.AddDebugLog($"{logPrefix} Already mounted or actually in flight - skipping mount setup.");
            TransitionTo(BotState.Flying, mountedTransitionDetail);
            return;
        }

        var isDiving = Plugin.Condition[ConditionFlag.Diving];
        if (isDiving && IsThiefUnderwaterLandingMode())
        {
            var targets = ResolveOverworldNavigationTargets();
            var landingTarget = targets.LandingTarget != Vector3.Zero
                ? targets.LandingTarget
                : flagPos;
            var targetXZ = CalculateXZDistance(playerPos, landingTarget);

            if (TryRecoverThiefWaterTravelPosture(
                    isDiving,
                    playerPos,
                    landingTarget,
                    targets.Basis,
                    targets.DestinationText,
                    targets.ZoneName,
                    alreadyDivingNewMapTarget: true))
            {
                return;
            }

            if (targetXZ <= UnderwaterBounceTriggerXZRange)
            {
                LogThiefWaterInfo(
                    $"{logPrefix} Already diving within thief-map trigger range; resuming underwater approach. " +
                    $"xz={targetXZ:F1}y; target={FormatVectorCompact(landingTarget)}; basis={targets.Basis}.");
                TransitionTo(BotState.Flying, "Already diving near thief-map target - swimming to flag...");
                return;
            }
        }

        if (!allowSameZoneTeleport)
        {
            _plugin.AddDebugLog($"{logPrefix} Same-zone target recovered while unmounted - mounting up.");
            TransitionTo(BotState.Mounting, mountTransitionDetail);
            return;
        }

        if (aetheryteId == 0)
            _plugin.AddDebugLog($"{logPrefix} No valid aetheryte found ({aetheryteId}) - mounting up");
        else if (bestAethDist == double.MaxValue)
            _plugin.AddDebugLog($"{logPrefix} Aetheryte has no position data (dist=infinity) - mounting up, no teleport possible");
        else if (playerXZDistToAetheryte is { } aetherytePlayerDistance
                 && aetherytePlayerDistance <= SameZoneAetheryteTeleportSkipXZRange
                 && bestAethDist < playerDist)
            _plugin.AddDebugLog($"{logPrefix} Already near selected aetheryte {selectedAetheryteName} (ID {aetheryteId}) - mounting up");
        else
            _plugin.AddDebugLog($"{logPrefix} Player is closer ({playerDist:F0}y <= {bestAethDist:F0}y) - mounting up, no teleport needed");

        TransitionTo(BotState.Mounting, mountTransitionDetail);
    }

    private bool TryGetSameZoneTeleportProgressBlockReason(
        Vector3 playerPos,
        out string reason)
        => TryGetMapTargetTeleportBlockReason(playerPos, out reason);

    private bool TryGetMapTargetTeleportBlockReason(
        Vector3 playerPos,
        out string reason)
    {
        if (TryGetCurrentMapLandingDistance(out var landingDistance, out var landingTarget, out var landingBasis))
        {
            var landingRange = GetCurrentMapLandingHoldRange();
            if (landingDistance <= landingRange)
            {
                reason =
                    $"already within landing range ({landingDistance:F1}y XZ <= {landingRange:F1}y) " +
                    $"of {FormatVectorCompact(landingTarget)} ({landingBasis})";
                return true;
            }
        }

        if (digIssuedThisMap)
        {
            reason = "dig already issued for active map";
            return true;
        }

        if (IsOverworldMapDutyActive())
        {
            reason = "active outdoor map duty";
            return true;
        }

        var coffer = _plugin.ChestDetectionService.FindNearestCoffer(OverworldRecoveryObjectSearchRange);
        if (coffer != null)
        {
            CaptureOpeningChestCofferPosition(coffer);
            var cofferDist = Vector3.Distance(playerPos, coffer.Position);
            reason = $"visible coffer entity={coffer.EntityId} targetable={coffer.IsTargetable} dist={cofferDist:F1}y";
            return true;
        }

        if (TryGetOpeningChestLastKnownCofferPosition(out var knownCoffer, out var knownCofferDistance))
        {
            reason = $"known coffer XYZ {FormatVectorCompact(knownCoffer)} dist={knownCofferDistance:F1}y";
            return true;
        }

        var portal = FindNearestPortal(keepActivePortalWindow: true);
        if (portal != null)
        {
            CapturePortalApproachPosition(portal);
            var portalDist = Vector3.Distance(playerPos, portal.Position);
            reason = $"visible portal entity={portal.EntityId} dist={portalDist:F1}y";
            return true;
        }

        if (portalApproachPosition.HasValue)
        {
            reason = $"known portal XYZ {FormatVectorCompact(portalApproachPosition.Value)}";
            return true;
        }

        if (portalRetryStart != DateTime.MinValue)
        {
            var retryAge = DateTime.Now - portalRetryStart;
            reason = $"portal retry window active ({retryAge.TotalSeconds:F0}s)";
            return true;
        }

        if (chestConfirmedThisMap ||
            openingChestDiscoveredByChat ||
            openingChestOpenedByChat ||
            openingChestPortalByChat ||
            portalConfirmedThisMap ||
            dungeonConfirmedThisMap)
        {
            reason =
                $"map progress evidence chest={chestConfirmedThisMap}, discoveredChat={openingChestDiscoveredByChat}, " +
                $"openedChat={openingChestOpenedByChat}, portalChat={openingChestPortalByChat}, " +
                $"portal={portalConfirmedThisMap}, dungeon={dungeonConfirmedThisMap}";
            return true;
        }

        reason = string.Empty;
        return false;
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
        {
            capturedLocation = latestCapturedLocation;
            QueueAreaMapAutoCloseAfterTreasureCapture("[DetectingLocation] TreasureMapLocationService");
        }

        var location = ResolveDetectedMapLocation(agentMapLocation, capturedLocation);

        if (location != null)
        {
            // Find nearest aetheryte to navigate from (pass flag position for closest-to-target selection)
            PopulateNearestAetheryte(location, out var aetheryteId, out var bestAethDist, out var usedXyz);

            SetLocation(location);
            ConsumeSelectedMapRunCountIfPending("[DetectingLocation]");

            if (Plugin.ClientState.TerritoryType == location.TerritoryId)
            {
                RouteSameTerritoryMapTarget(
                    location,
                    aetheryteId,
                    bestAethDist,
                    usedXyz,
                    "[DetectingLocation]",
                    "Already at map location - landing and digging...",
                    "Already mounted - flying to location...",
                    "Already in zone & closer than aetheryte! Mounting up...",
                    allowSameZoneTeleport: true);
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
        var now = DateTime.Now;

        if (!stateActionIssued)
        {
            ResetPortaPraetoriaTakeoffNudge("[Teleporting] starting teleport", stopAutomove: true);
            if (CurrentLocation == null || CurrentLocation.NearestAetheryteId == 0)
            {
                HandleError("No aetheryte ID for teleport.");
                return;
            }

            var delaySeconds = Math.Clamp(_plugin.Configuration.PartyTeleportDelaySeconds, 0, 300);
            if (delaySeconds > 0 && !HasTeleportDelayElapsed(delaySeconds))
                return;

            teleportCommandIssuedAt = now;
            teleportOriginPosition = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
            teleportSawBetweenAreas = false;
            teleportLastLoadingAt = DateTime.MinValue;
            teleportLoadingClearedAt = DateTime.MinValue;
            teleportDelayStartedAt = DateTime.MinValue;
            nav.TeleportToAetheryte(CurrentLocation.NearestAetheryteId);
            if (nav.State == NavigationState.Error && TryDeferTeleportCombatError(nav.StateDetail))
                return;

            stateActionIssued = true;
            return;
        }

        if (nav.State == NavigationState.Error)
        {
            if (TryDeferTeleportCombatError(nav.StateDetail))
                return;

            var destination = string.IsNullOrWhiteSpace(nav.LastTeleportDestinationName)
                ? CurrentLocation?.NearestAetheryteName
                : nav.LastTeleportDestinationName;
            HandleError($"Teleport to {destination} failed: {nav.StateDetail}");
            return;
        }

        var elapsed = teleportCommandIssuedAt == DateTime.MinValue
            ? now - stateStartTime
            : now - teleportCommandIssuedAt;
        var loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                      Plugin.Condition[ConditionFlag.BetweenAreas51] ||
                      nav.IsTeleporting();

        if (loading)
        {
            teleportSawBetweenAreas = true;
            teleportLastLoadingAt = now;
            teleportLoadingClearedAt = DateTime.MinValue;
            StateDetail = $"Teleporting... ({elapsed.TotalSeconds:F0}s)";
            return;
        }

        if (!teleportSawBetweenAreas)
        {
            StateDetail = $"Teleporting... waiting for area load ({elapsed.TotalSeconds:F0}s)";
            return;
        }

        if (teleportLoadingClearedAt == DateTime.MinValue)
            teleportLoadingClearedAt = now;

        var settleElapsed = now - teleportLoadingClearedAt;
        if (settleElapsed < TeleportArrivalSettleDelay)
        {
            StateDetail = $"Teleporting... settling ({elapsed.TotalSeconds:F0}s)";
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            StateDetail = $"Teleporting... waiting for player after load ({elapsed.TotalSeconds:F0}s)";
            return;
        }

        if (player.IsCasting || Plugin.Condition[ConditionFlag.Casting])
        {
            StateDetail = $"Teleporting... waiting for cast lock to clear ({elapsed.TotalSeconds:F0}s)";
            return;
        }

        var currentTerritory = Plugin.ClientState.TerritoryType;
        var expectedTerritory = CurrentLocation?.TerritoryId ?? 0;
        if (expectedTerritory == 0)
        {
            HandleError("No target territory after teleport.");
            return;
        }

        if (currentTerritory != expectedTerritory)
        {
            if (elapsed.TotalSeconds > 15)
            {
                HandleError($"Wrong territory after teleport: {currentTerritory} (expected {expectedTerritory}).");
                return;
            }

            StateDetail = $"Teleporting... waiting for target territory ({elapsed.TotalSeconds:F0}s)";
            return;
        }

        var playerPos = player.Position;
        var positionDelta = playerPos != Vector3.Zero && teleportOriginPosition != Vector3.Zero
            ? Vector3.Distance(playerPos, teleportOriginPosition)
            : 0.0f;
        var lastLoadingText = teleportLastLoadingAt == DateTime.MinValue
            ? "never"
            : $"{(now - teleportLastLoadingAt).TotalSeconds:F1}s ago";
        _plugin.AddDebugLog(
            $"[Teleporting] Arrived after {elapsed.TotalSeconds:F1}s; " +
            $"settle={settleElapsed.TotalSeconds:F1}s; moved={positionDelta:F1}y; lastLoading={lastLoadingText}.");

        // Passively record aetheryte position for future nearest-aetheryte lookups
        // Record when within 20y of estimated aetheryte location (XZ only)
        if (CurrentLocation?.NearestAetheryteId > 0)
        {
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

        QueuePortaPraetoriaTakeoffNudgeIfNeeded(currentTerritory, expectedTerritory);
        TransitionTo(BotState.Mounting, "Arrived! Mounting up...");
    }

    private bool HasTeleportDelayElapsed(int delaySeconds)
    {
        var now = DateTime.Now;
        if (teleportDelayStartedAt == DateTime.MinValue)
            teleportDelayStartedAt = now;

        var elapsed = now - teleportDelayStartedAt;
        if (elapsed.TotalSeconds >= delaySeconds)
            return true;

        StateDetail = $"Waiting before teleporting... ({elapsed.TotalSeconds:F0}s / {delaySeconds}s)";
        return false;
    }

    private void TickMounting()
    {
        var nav = _plugin.NavigationService;

        if (nav.IsMounted())
        {
            var thiefWaterRemountRecovered = thiefWaterRemountRecoveryActive;
            var waitForParty = ShouldWaitForPartyBeforeTakeoffForCurrentMap();
            var waitForPartySettingName = GetCurrentTakeoffPartyWaitSettingName();

            // Successfully mounted - reset counters and proceed
            mountAttemptStart = DateTime.MinValue;
            mountAttempts = 0;
            if (thiefWaterRemountRecovered)
            {
                thiefWaterRemountRecoveryActive = false;

                if (!waitForParty)
                {
                    LogThiefWaterInfo($"[Underwater] Remount recovery succeeded; {waitForPartySettingName} disabled, bypassing party wait for thief-map travel.");
                    ResumeThiefWaterTravelAfterRemount("Thief-map remount recovered - flying to location...");
                    return;
                }

                var gate = EvaluatePartyMountWaitGate();
                if (gate.CanProceed)
                {
                    LogThiefWaterInfo(
                        $"[Underwater] Remount recovery succeeded; party mount wait satisfied " +
                        $"({gate.MountedOthers}/{gate.RequiredOthers} required mounted others), resuming thief-map travel.");
                    ResumeThiefWaterTravelAfterRemount("Thief-map remount recovered - flying to location...");
                    return;
                }

                thiefWaterRemountRecoveryZoneWaitActive = true;
                var missingText = string.Join(", ", gate.UnavailableNames);
                LogThiefWaterInfo(gate.UnavailableNames.Count > 0
                    ? $"[Underwater] Remount recovery succeeded; waiting for party before thief-map travel: {gate.MountedOthers}/{gate.RequiredOthers} required mounted others; not loaded in same zone: {missingText}."
                    : $"[Underwater] Remount recovery succeeded; waiting for party to mount before thief-map travel: {gate.MountedOthers}/{gate.RequiredOthers} required mounted others.");
                TransitionTo(
                    BotState.WaitingForParty,
                    gate.UnavailableNames.Count > 0
                        ? BuildThiefWaterRemountZoneWaitDetail(gate.UnavailableNames)
                        : $"Thief-map remount: waiting for party to mount ({gate.MountedOthers}/{gate.RequiredOthers} required mounted others)...");
                return;
            }
            
            var partySize = Plugin.PartyList.Length;
            var recoveryWaitRequested = overworldRecoveryRequiresPartyMountWait;
            var bypassPartyWait = ConsumeJoinedFateMountWaitBypass("[Mounting]");
            var recoveryWait = recoveryWaitRequested && waitForParty && !bypassPartyWait;
            _plugin.AddDebugLog($"[Mounting] PartySize={partySize}, {waitForPartySettingName}={waitForParty}, RecoveryPartyWait={recoveryWaitRequested}");

            if (recoveryWaitRequested && !waitForParty)
            {
                overworldRecoveryRequiresPartyMountWait = false;
                _plugin.AddDebugLog($"[Mounting] Recovery party wait skipped because {waitForPartySettingName} is disabled.");
            }

            if (bypassPartyWait)
            {
                TransitionTo(BotState.Flying, "Joined-FATE recovery mounted - flying to location...");
                return;
            }

            if (recoveryWait || (partySize > 0 && waitForParty))
            {
                var gate = EvaluatePartyMountWaitGate();
                if (gate.CanProceed)
                {
                    overworldRecoveryRequiresPartyMountWait = false;
                    TransitionTo(
                        BotState.Flying,
                        recoveryWait
                            ? "Recovery teleport complete - party mount wait satisfied! Flying to location..."
                            : "Party mount wait satisfied! Flying to location...");
                }
                else
                {
                    TransitionTo(
                        BotState.WaitingForParty,
                        recoveryWait
                            ? $"Recovery teleport complete - waiting for party to mount ({gate.MountedOthers}/{gate.RequiredOthers} required mounted others)..."
                            : $"Waiting for party to mount ({gate.MountedOthers}/{gate.RequiredOthers} required mounted others)...");
                }
                return;
            }

            overworldRecoveryRequiresPartyMountWait = false;
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
            _plugin.AddDebugLog(thiefWaterRemountRecoveryActive
                ? "[Mounting] Failed to mount after 5 attempts during thief-map remount recovery"
                : "[Mounting] Failed to mount after 5 attempts - resetting bot");
            mountAttemptStart = DateTime.MinValue;
            mountAttempts = 0;
            if (thiefWaterRemountRecoveryActive)
            {
                thiefWaterRemountRecoveryActive = false;
                thiefWaterRemountRecoveryZoneWaitActive = false;
                _plugin.NavigationService.StopNavigation();
                LogThiefWaterInfo("[Underwater] Remount recovery failed after 5 attempts; stopping before unsafe on-foot/swim travel.");
                TransitionTo(BotState.Error, "Thief-map water remount recovery failed after 5 attempts. Manual intervention required.");
                return;
            }

            TransitionTo(BotState.Idle, "Mount failed - please restart");
            return;
        }
    }

    private void TickWaitingForParty()
    {
        var waitForParty = ShouldWaitForPartyBeforeTakeoffForCurrentMap();
        var waitForPartySettingName = GetCurrentTakeoffPartyWaitSettingName();

        if (ConsumeJoinedFateMountWaitBypass("[WaitingForParty]"))
        {
            if (_plugin.NavigationService.IsMounted())
            {
                TransitionTo(BotState.Flying, "Joined-FATE recovery - flying to location...");
            }
            else
            {
                TransitionTo(BotState.Mounting, "Joined-FATE recovery - remounting for map target...");
            }
            return;
        }

        if (!waitForParty)
        {
            overworldRecoveryRequiresPartyMountWait = false;
            if (thiefWaterRemountRecoveryZoneWaitActive)
            {
                LogThiefWaterInfo($"[Underwater] {waitForPartySettingName} disabled; bypassing thief-map party wait after remount.");
                ResumeThiefWaterTravelAfterRemount("Wait for party disabled - flying to thief-map location...");
                return;
            }

            TransitionTo(BotState.Flying, "Wait for party disabled - flying to location...");
            return;
        }

        var elapsed = (DateTime.Now - stateStartTime).TotalSeconds;
        var gate = EvaluatePartyMountWaitGate();
        var unavailableDetail = gate.UnavailableNames.Count == 0
            ? string.Empty
            : $"; not loaded in same zone: {string.Join(", ", gate.UnavailableNames)}";

        if (gate.CanProceed)
        {
            overworldRecoveryRequiresPartyMountWait = false;
            if (thiefWaterRemountRecoveryZoneWaitActive)
            {
                LogThiefWaterInfo(
                    $"[Underwater] Thief-map remount party wait complete; " +
                    $"{gate.MountedOthers}/{gate.RequiredOthers} required mounted others ready.");
                ResumeThiefWaterTravelAfterRemount("Required party members mounted - flying to thief-map location...");
                return;
            }

            TransitionTo(BotState.Flying, "Required party members mounted! Flying...");
            return;
        }

        if (!gate.SnapshotValid)
        {
            StateDetail = $"Waiting for party snapshot ({elapsed:F0}s elapsed, expecting {gate.TotalOthers} other players)...";
        }
        else if (!_plugin.Configuration.PartyWaitBeforeDismountUseCountThreshold &&
                 _plugin.Configuration.RequireAllMounted &&
                 gate.SeenOthers < gate.ExpectedOthers)
        {
            StateDetail =
                $"Waiting for full party snapshot ({gate.SeenOthers}/{gate.ExpectedOthers} other players seen, " +
                $"{gate.MountedOthers}/{gate.RequiredOthers} mounted, {elapsed:F0}s elapsed)...";
        }
        else if (gate.UnavailableNames.Count > 0 &&
                 !_plugin.Configuration.PartyWaitBeforeDismountUseCountThreshold &&
                 _plugin.Configuration.RequireAllMounted)
        {
            StateDetail = thiefWaterRemountRecoveryZoneWaitActive
                ? $"Thief-map remount: waiting for party zone load ({gate.MountedOthers}/{gate.RequiredOthers} mounted, {elapsed:F0}s elapsed{unavailableDetail})..."
                : $"Waiting for party zone load ({gate.MountedOthers}/{gate.RequiredOthers} mounted, {elapsed:F0}s elapsed{unavailableDetail})...";
        }
        else
        {
            StateDetail = thiefWaterRemountRecoveryZoneWaitActive
                ? $"Thief-map remount: waiting for party to mount ({gate.MountedOthers}/{gate.RequiredOthers} required mounted others, {elapsed:F0}s elapsed)..."
                : $"Waiting for party to mount ({gate.MountedOthers}/{gate.RequiredOthers} required mounted others, {elapsed:F0}s elapsed)...";
        }

        var now = DateTime.Now;
        if (now - lastPartyMountWaitLogTime >= TimeSpan.FromSeconds(10))
        {
            lastPartyMountWaitLogTime = now;
            var unavailableLog = gate.UnavailableNames.Count == 0
                ? string.Empty
                : $"; unavailable={string.Join(", ", gate.UnavailableNames)}";
            var validityLog = gate.SnapshotValid ? string.Empty : "; snapshot=invalid";
            var expectedLog = gate.ExpectedOthers > 0 ? $"; expectedOthers={gate.ExpectedOthers}" : string.Empty;
            var modeLog = _plugin.Configuration.PartyWaitBeforeDismountUseCountThreshold
                ? "; mode=threshold"
                : _plugin.Configuration.RequireAllMounted ? "; mode=full-party" : "; mode=any-mounted";
            var blockerDetails = _plugin.PartyService.PartyMembers
                .Where(member => !member.IsLocalPlayer && !IsLoadedSameTerritoryMounted(member))
                .Select(FormatPartyMemberClassification)
                .ToList();
            var blockersLog = blockerDetails.Count == 0
                ? string.Empty
                : $"; blockers={string.Join("; ", blockerDetails)}";
            var message =
                $"[PartyWait][Mount] Waiting {elapsed:F0}s; mountedOthers={gate.MountedOthers}/{gate.RequiredOthers}; " +
                $"seenOthers={gate.SeenOthers}/{gate.TotalOthers}{expectedLog}{modeLog}{unavailableLog}{validityLog}{blockersLog}.";
            _log.Info(message);
            _plugin.AddDebugLog(message);
        }
    }

    private void TickFlying()
    {
        NormalizeLandingModeForSelectedMap("[Flying]");

        // Check for diving state change (Condition 81)
        bool isDiving = IsDivingForCurrentMap();
        if (CurrentLocation == null)
        {
            HandleError("No location data for navigation.");
            return;
        }

        var nav = _plugin.NavigationService;
        var currentPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var activeNavTargets = ResolveOverworldNavigationTargets();

        if (TryHandlePortaPraetoriaTakeoffNudge(
                currentPos,
                activeNavTargets.LandingTarget,
                activeNavTargets.Basis,
                activeNavTargets.DestinationText,
                activeNavTargets.ZoneName))
        {
            return;
        }

        if (TryRecoverThiefWaterTravelPosture(
                isDiving,
                currentPos,
                activeNavTargets.LandingTarget,
                activeNavTargets.Basis,
                activeNavTargets.DestinationText,
                activeNavTargets.ZoneName))
        {
            return;
        }

        var mountedFarThiefTravel =
            IsThiefUnderwaterLandingMode()
            && !IsUnderwaterBounceSpecialEntryHandoffActive()
            && IsMountedOrActualInFlight()
            && currentPos != Vector3.Zero
            && activeNavTargets.LandingTarget != Vector3.Zero
            && CalculateXZDistance(currentPos, activeNavTargets.LandingTarget) > UnderwaterBounceTriggerXZRange;

        if (!mountedFarThiefTravel && TryHandleUnderwaterBounceTriggerFlow(isDiving, includeNearTarget: false))
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

            // Reissue directly while diving; do not stop vnav during Condition 81.
            if (underwaterTargetPosition != Vector3.Zero)
            {
                IssueActiveUnderwaterFlagApproach(
                    DateTime.Now,
                    "initial",
                    currentPos,
                    underwaterTargetPosition,
                    CalculateXZDistance(currentPos, underwaterTargetPosition),
                    force: true);
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

        // Rate limit diving checks to every 2 seconds
        if ((DateTime.Now - lastDivingCheck).TotalSeconds < 2.0) return;
        lastDivingCheck = DateTime.Now;

        if (!stateActionIssued)
        {
            // Check if we're already close enough to skip pathfinding entirely
            var playerPos = currentPos;
            var initialNavTargets = activeNavTargets;
            var xzDist2 = CalculateXZDistance(playerPos, initialNavTargets.LandingTarget);
            var initialLandingHoldRange = GetCurrentMapLandingHoldRange();
            if (xzDist2 <= initialLandingHoldRange)
            {
                if (IsThiefUnderwaterLandingMode())
                {
                    _plugin.AddDebugLog(
                        $"[Flying] Already within {xzDist2:F1}y of landing target ({initialNavTargets.Basis}) - " +
                        "dive landing mode active, skipping immediate dismount/dig");
                    stateActionIssued = true;
                    if (TryHandleUnderwaterBounceTriggerFlow(isDiving))
                    {
                        MarkUnderwaterBounceSpecialEntryReachedIfNeeded(
                            playerPos,
                            initialNavTargets.LandingTarget,
                            initialNavTargets.Basis,
                            initialNavTargets.DestinationText,
                            initialNavTargets.ZoneName);
                        return;
                    }
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

        currentPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        activeNavTargets = ResolveOverworldNavigationTargets();
        var xzDist = CalculateXZDistance(currentPos, activeNavTargets.LandingTarget);
        var landingHoldRange = GetCurrentMapLandingHoldRange();
        if (IsThiefUnderwaterLandingMode()
            && xzDist <= UnderwaterBounceTriggerXZRange
            && TryHandleUnderwaterBounceTriggerFlow(isDiving))
        {
            MarkUnderwaterBounceSpecialEntryReachedIfNeeded(
                currentPos,
                activeNavTargets.LandingTarget,
                activeNavTargets.Basis,
                activeNavTargets.DestinationText,
                activeNavTargets.ZoneName);
            return;
        }

        var recoveryTargetKey = $"{BuildOverworldMapTargetKey(CurrentLocation)}:{activeNavTargets.NavigationTarget.X:0.0}:{activeNavTargets.NavigationTarget.Y:0.0}:{activeNavTargets.NavigationTarget.Z:0.0}";
        if (TryRunOverworldRecoveryWatchdog(
                now,
                "Flying",
                "map target",
                recoveryTargetKey,
                CurrentLocation.TerritoryId,
                activeNavTargets.NavigationTarget,
                OverworldRecoveryNavigationKind.FlyTo))
        {
            return;
        }
        
        // Check if we're close enough to the resolved landing target; uses ground target, not elevated.
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
        
        if ((activeNavTargets.UseNavStateForLanding && (nav.State == NavigationState.Arrived || nav.State == NavigationState.Idle)) ||
            xzDist <= landingHoldRange)
        {
            if (!IsThiefUnderwaterLandingMode() &&
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
                var waitForUnderwaterParty = IsThiefUnderwaterLandingMode()
                    && ShouldWaitForUnderwaterMapContentParty();
                var waitForPartyDismount = IsThiefUnderwaterLandingMode()
                    ? waitForUnderwaterParty
                    : _plugin.Configuration.PartyWaitBeforeDismount;
                _plugin.AddDebugLog(
                    $"[Dismount] PartyWaitBeforeDismount={_plugin.Configuration.PartyWaitBeforeDismount}, " +
                    $"WaitForPartyForThiefMapsUnderwater={_plugin.Configuration.WaitForPartyForThiefMapsUnderwater}, " +
                    $"LandingMode={currentLandingMode}, UnderwaterPartyWait={waitForUnderwaterParty}");
                if (waitForPartyDismount)
                {
                    var partyWait = EvaluatePartyProximityGate(
                        10.0,
                        IsThiefUnderwaterLandingMode() ? "UnderwaterLanding" : "OverworldLanding");
                    if (!partyWait.CanProceed)
                    {
                        if (IsThiefUnderwaterLandingMode())
                            PauseUnderwaterBounceDescentForPartyWait();
                        StateDetail = BuildOverworldLandingPartyWaitDetail(partyWait, 10.0);
                        return; // Don't attempt dismount yet
                    }
                }

                // Record when we first started trying to land at this location
                if (dismountAttemptStart == DateTime.MinValue)
                {
                    dismountAttemptStart = DateTime.Now;
                    descentMode = IsThiefUnderwaterLandingMode();
                    descentStartTime = DateTime.Now;
                    descentStartY = Plugin.ObjectTable.LocalPlayer?.Position.Y ?? 0f;
                    _plugin.AddDebugLog(
                        $"[Flying] Landing phase ready via {activeNavTargets.Basis}; navState={nav.State}; " +
                        $"landingXZ={xzDist:F1}y; current={FormatVectorCompact(currentPos)}; " +
                        $"landingTarget={FormatVectorCompact(activeNavTargets.LandingTarget)}");
                    _plugin.AddDebugLog(IsThiefUnderwaterLandingMode()
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
                        _plugin.AddDebugLog($"[Flying] Starting Ctrl+Space descent attempt ({descentElapsed:F0}s into dismount)");
                        StartSafeDescent(
                            "[Flying] descent mode",
                            includeForward: IsThiefUnderwaterLandingMode() && !Plugin.Condition[ConditionFlag.Diving]);
                    }
                    
                    // Monitor Y position change
                    var currentY = Plugin.ObjectTable.LocalPlayer?.Position.Y ?? 0f;
                    var yChange = Math.Abs(currentY - descentStartY);
                    
                    if (descentElapsed >= 5.0)
                    {
                        if (IsThiefUnderwaterLandingMode())
                        {
                            _plugin.AddDebugLog(
                                yChange < 5.0f
                                    ? $"[Flying] Forward descent has not changed Y yet (Y change: {yChange:F1}y) - continuing toward water until Diving"
                                    : $"[Flying] Forward descent moving (Y change: {yChange:F1}y) - continuing until Diving");

                            descentStartTime = DateTime.Now;
                            descentStartY = currentY;
                        }
                        else
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
                    _plugin.AddDebugLog($"[Flying] Normal dismount failed after {dismountElapsed:F0}s - trying Ctrl+Space as fallback");
                    StartSafeDescent("[Flying] fallback descent");
                }
                
                StateDetail = $"Fallback descent... ({dismountElapsed:F0}s)";
                return;
            }
        }
    }

    private bool TryHoldOpeningChestForJoinedFate(DateTime now, IGameObject? visibleCoffer)
    {
        if (!_plugin.FateSyncService.TryGetJoinedFateId(out var fateId))
        {
            if (!openingChestJoinedFateHoldActive)
                return false;

            var heldFateId = openingChestJoinedFateId;
            openingChestJoinedFateHoldActive = false;
            openingChestJoinedFateId = 0;
            openingChestJoinedFateHoldStartedAt = DateTime.MinValue;
            openingChestJoinedFateHoldLastLogAt = DateTime.MinValue;
            lastCombatEndTime = now;
            openingChestCombatInterrupted = true;
            chestDisappearedTime = DateTime.MinValue;
            NormalizeLandingModeForSelectedMap("[OpeningChest][FATE] release");
            _plugin.AddDebugLog(
                $"[OpeningChest][FATE] Release map context: mapId={SelectedMapItemId}; landing={currentLandingMode}.");
            _plugin.AddDebugLog($"[OpeningChest][FATE] Joined FATE {heldFateId} cleared - resuming chest/portal recovery after settle.");
            StateDetail = "Joined FATE cleared - settling before chest/portal recovery...";
            return true;
        }

        if (visibleCoffer != null)
        {
            CaptureOpeningChestCofferPosition(visibleCoffer);
            chestConfirmedThisMap = true;
        }

        var visiblePortal = FindNearestPortal(keepActivePortalWindow: true);
        if (visiblePortal != null)
            CapturePortalApproachPosition(visiblePortal);

        var inJoinedFateCombat = Plugin.Condition[ConditionFlag.InCombat];
        if (inJoinedFateCombat)
            EnsureJoinedFateCombatAutomation(fateId, "opening chest joined-FATE hold");

        if (autoMoveActive || _plugin.NavigationService.State != NavigationState.Idle)
            StopOpeningChestCofferMovement($"while joined FATE {fateId} is active");
        ResetPortalCloseNudgeTracking(stopMovement: true);
        ResetOpeningChestFlagFallback($"joined FATE {fateId}", logIfActive: true);
        TryHandleJoinedFateMountedIntervention(fateId, "[OpeningChest][FATE]", now, updateStateDetail: false);

        openingChestCombatInterrupted = true;
        openingChestRecoveryDigIssued = false;
        openingChestReturningToFlag = false;
        openingChestReturningToLastKnownCoffer = false;
        chestDisappearedTime = DateTime.MinValue;

        var entering = !openingChestJoinedFateHoldActive || openingChestJoinedFateId != fateId;
        if (entering)
        {
            openingChestJoinedFateHoldActive = true;
            openingChestJoinedFateId = fateId;
            openingChestJoinedFateHoldStartedAt = now;
            openingChestJoinedFateHoldLastLogAt = now;
            var targetText = visiblePortal != null
                ? $"visible portal entity={visiblePortal.EntityId}"
                : visibleCoffer != null
                    ? $"visible coffer entity={visibleCoffer.EntityId} targetable={visibleCoffer.IsTargetable}"
                    : openingChestLastKnownCofferPosition.HasValue
                        ? $"known coffer XYZ {FormatVectorCompact(openingChestLastKnownCofferPosition.Value)}"
                        : "no visible coffer/portal yet";
            _plugin.AddDebugLog($"[OpeningChest][FATE] Holding chest recovery for joined FATE {fateId}; {targetText}.");
        }
        else if (now - openingChestJoinedFateHoldLastLogAt >= OutdoorMapFlowHoldLogInterval)
        {
            openingChestJoinedFateHoldLastLogAt = now;
            var elapsed = openingChestJoinedFateHoldStartedAt == DateTime.MinValue
                ? TimeSpan.Zero
                : now - openingChestJoinedFateHoldStartedAt;
            _plugin.AddDebugLog($"[OpeningChest][FATE] Still holding chest recovery for joined FATE {fateId} ({elapsed.TotalSeconds:F0}s).");
        }

        stateStartTime = now;
        var holdElapsed = openingChestJoinedFateHoldStartedAt == DateTime.MinValue
            ? TimeSpan.Zero
            : now - openingChestJoinedFateHoldStartedAt;
        var activityText = inJoinedFateCombat ? "combat" : "level sync/engagement";
        StateDetail = visiblePortal != null
            ? $"Joined FATE {fateId} active - holding portal recovery during {activityText} ({holdElapsed.TotalSeconds:F0}s)..."
            : $"Joined FATE {fateId} active - holding chest recovery during {activityText} ({holdElapsed.TotalSeconds:F0}s)...";
        return true;
    }

    private void TickOpeningChest()
    {
        // Check for diving state change - if we just entered diving, go to underwater navigation
        bool isDiving = IsDivingForCurrentMap();
        if (TryHandleUnderwaterBounceTriggerFlow(isDiving, includeNearTarget: false))
            return;

        if (isDiving && IsThiefUnderwaterLandingMode())
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

            // Reissue directly while diving; do not stop vnav during Condition 81.
            if (underwaterTargetPosition != Vector3.Zero)
            {
                var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
                IssueActiveUnderwaterFlagApproach(
                    DateTime.Now,
                    "initial",
                    playerPos,
                    underwaterTargetPosition,
                    CalculateXZDistance(playerPos, underwaterTargetPosition),
                    force: true);
            }
            return;
        }

        var now = DateTime.Now;
        bool inCombat = Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        bool mapDutyOutsideDungeon =
            (Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56]) &&
            !IsTreasureDungeonTerritory(Plugin.ClientState.TerritoryType);
        var useWideCofferSearch = openingChestCombatInterrupted
            || openingChestReturningToLastKnownCoffer
            || mapDutyOutsideDungeon
            || openingChestLastKnownCofferPosition.HasValue;
        var cofferSearchRange = useWideCofferSearch
            ? OverworldRecoveryObjectSearchRange
            : OpeningChestNormalCofferSearchRange;

        // No portal yet - keep working on chest
        var chest = _plugin.ChestDetectionService.FindNearestCoffer(cofferSearchRange);
        if (chest == null && TryResolveOpeningChestCofferFromCurrentTarget(cofferSearchRange, now, out var targetedCoffer))
        {
            chest = targetedCoffer;
        }

        if (chest == null)
            TryIssueOpeningChestTargetFallback(now);

        if (chest != null)
        {
            CaptureOpeningChestCofferPosition(chest);
            chestConfirmedThisMap = true;
        }

        if (TryHoldOpeningChestForJoinedFate(now, chest))
            return;

        // Click Yes on any dialog (Open the treasure coffer? etc)
        ClickYesIfVisibleWithDiagnostics("OpeningChest.generic-dialog");
        if (TrySkipCardGame())
            return;

        if (chest != null && !chest.IsTargetable)
        {
            HandleVisibleUntargetableOpeningChestCoffer(chest, now);
            return;
        }

        if (chest == null)
        {
            if (openingChestFlagFallbackKind == OpeningChestFlagFallbackKind.Coffer)
                ResetOpeningChestFlagFallback("coffer disappeared", logIfActive: true);
            var portal = FindNearestPortal();
            if (portal != null)
            {
                _plugin.AddDebugLog("[OpeningChest] Portal detected after targetable coffer cleared - transitioning to portal interaction...");
                ResetOpeningChestCofferMountRecovery();
                ResetOpeningChestCofferWalkFailure();
                StopPortalConflictingMovement();
                CheckForPortalAfterChest();
                return;
            }

            if (openingChestOpenedByChat || openingChestPortalByChat)
            {
                _plugin.AddDebugLog("[OpeningChest] Coffer open/portal chat evidence present and no coffer object remains - transitioning to portal/completion flow.");
                CheckForPortalAfterChest();
                return;
            }
        }

        var hasFlagRecoveryTarget = TryGetCurrentFlagRecoveryTarget(out var flagRecoveryTarget, out var distToFlag);
        var hasKnownCofferRecoveryTarget = TryGetOpeningChestLastKnownCofferPosition(
            out _,
            out var distToLastKnownCoffer);
        var nearKnownCofferForRecovery = hasKnownCofferRecoveryTarget &&
                                         distToLastKnownCoffer <= OpeningChestCofferReturnRange;
        if (chest == null)
        {
            ResetOpeningChestCofferMountRecovery("because chest disappeared", stopNavigation: !inCombat);
            ResetOpeningChestCofferWalkFailure();

            if (openingChestCombatInterrupted)
            {
                var localPlayer = Plugin.ObjectTable.LocalPlayer;
                var xzDistToFlag = hasFlagRecoveryTarget && localPlayer != null
                    ? (float)CalculateXZDistance(localPlayer.Position, flagRecoveryTarget)
                    : distToFlag;
                var useThiefMapRecovery = IsThiefUnderwaterLandingMode();
                var nearFlagForRecovery = hasFlagRecoveryTarget && distToFlag <= 30f;
                var shouldReturnToFlag = hasFlagRecoveryTarget &&
                                         (useThiefMapRecovery ? distToFlag > 30f : xzDistToFlag > 2f) &&
                                         !nearKnownCofferForRecovery;

                if (inCombat)
                {
                    chestDisappearedTime = DateTime.MinValue;
                    StateDetail = hasFlagRecoveryTarget
                        ? $"In combat - waiting to recover chest ({xzDistToFlag:F1}y XZ from flag)..."
                        : "In combat - waiting to recover chest...";
                    return;
                }

                if (TryReturnToOpeningChestLastKnownCoffer(now))
                {
                    chestDisappearedTime = DateTime.MinValue;
                    return;
                }

                if (shouldReturnToFlag)
                {
                    if (!openingChestReturningToFlag)
                    {
                        _plugin.AddDebugLog($"[OpeningChest] Combat recovery: no chest visible and {xzDistToFlag:F1}y XZ from flag - returning mounted/flying to flag");
                        openingChestReturningToFlag = true;
                    }

                    var nav = _plugin.NavigationService;
                    if (!nav.IsMounted() && !nav.IsFlying())
                    {
                        if (Plugin.Condition[ConditionFlag.Mounting71])
                        {
                            StateDetail = $"Mounting to return to flag after combat ({xzDistToFlag:F1}y XZ)...";
                            return;
                        }

                        if (now - lastOpeningChestCofferMountCommandTime >= OpeningChestCofferMountCommandInterval)
                        {
                            lastOpeningChestCofferMountCommandTime = now;
                            nav.MountUp();
                        }

                        StateDetail = $"Mounting to return to flag after combat ({xzDistToFlag:F1}y XZ)...";
                        return;
                    }

                    var targetKey = BuildOverworldRecoveryPositionKey(
                        "opening-combat-flag",
                        Plugin.ClientState.TerritoryType,
                        flagRecoveryTarget);
                    if (TryRunOverworldRecoveryWatchdog(
                            now,
                            "OpeningChest",
                            "combat recovery flag",
                            targetKey,
                            Plugin.ClientState.TerritoryType,
                            flagRecoveryTarget,
                            OverworldRecoveryNavigationKind.FlyTo))
                    {
                        return;
                    }

                    var navInactive = _plugin.NavigationService.State == NavigationState.Idle || !_plugin.VNavIPC.IsNavigating;
                    if (navInactive)
                    {
                        if (_plugin.NavigationService.State != NavigationState.Idle)
                            _plugin.NavigationService.StopNavigation();

                        _plugin.NavigationService.FlyToPosition(flagRecoveryTarget, force: true);
                        autoMoveActive = true;
                    }

                    chestDisappearedTime = DateTime.MinValue;
                    StateDetail = $"Returning to flag after combat ({xzDistToFlag:F1}y XZ)...";
                    return;
                }

                if (openingChestReturningToFlag)
                {
                    _plugin.AddDebugLog($"[OpeningChest] Combat recovery: back near flag ({xzDistToFlag:F1}y XZ) - rechecking chest and dig");
                    if (autoMoveActive)
                    {
                        _plugin.NavigationService.StopNavigation();
                        autoMoveActive = false;
                    }
                    openingChestReturningToFlag = false;
                    chestDisappearedTime = DateTime.MinValue;
                }

                if (hasFlagRecoveryTarget &&
                    xzDistToFlag <= 2f &&
                    !useThiefMapRecovery)
                {
                    openingChestCombatInterrupted = false;
                    openingChestRecoveryDigIssued = false;
                    digIssuedThisMap = false;
                    digIssuedAt = DateTime.MinValue;
                    TransitionTo(BotState.Flying, "Combat recovery: landing at map target...");
                    return;
                }

                if (!openingChestRecoveryDigIssued && nearFlagForRecovery)
                {
                    if (TryGuardNonDivingThiefMapRecoveryDig(
                            now,
                            "[OpeningChest] combat recovery",
                            hasFlagRecoveryTarget,
                            flagRecoveryTarget,
                            xzDistToFlag))
                    {
                        return;
                    }

                    var sinceDig = (now - lastDigTime).TotalSeconds;
                    if (sinceDig < 3.0)
                    {
                        StateDetail = $"Combat ended - waiting to retry dig... ({sinceDig:F1}/3.0s)";
                        return;
                    }

                    _plugin.AddDebugLog($"[OpeningChest] Combat recovery: no chest visible near flag ({xzDistToFlag:F1}y XZ) - retrying dig");
                    CommandHelper.SendCommand("/gaction dig");
                    lastDigTime = now;
                    openingChestRecoveryDigIssued = true;
                    chestDisappearedTime = now;
                    StateDetail = $"Retrying dig after combat ({xzDistToFlag:F1}y XZ from flag)...";
                    return;
                }

                if (chestDisappearedTime == DateTime.MinValue)
                {
                    chestDisappearedTime = now;
                    _plugin.AddDebugLog(openingChestRecoveryDigIssued
                        ? "[OpeningChest] Waiting for chest after combat recovery dig"
                        : hasFlagRecoveryTarget
                            ? $"[OpeningChest] Combat recovery: no chest visible after combat ({xzDistToFlag:F1}y XZ from flag) - waiting briefly before portal check"
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

                if (!HasOpeningChestCofferCompletionEvidence() &&
                    TryRecoverMissingOpeningChestCoffer(now, inCombat, hasFlagRecoveryTarget, flagRecoveryTarget, distToFlag))
                {
                    return;
                }

                _plugin.AddDebugLog(openingChestRecoveryDigIssued
                    ? "[OpeningChest] No chest found after combat recovery dig - checking for portal"
                    : "[OpeningChest] No chest found after combat recovery window - checking for portal");
                openingChestCombatInterrupted = false;
                openingChestRecoveryDigIssued = false;
                openingChestReturningToFlag = false;
                chestDisappearedTime = DateTime.MinValue;
                if (TryGuardMapCompletionWithActiveKeyItem("[OpeningChest][CombatRecovery]"))
                    return;
                CheckForPortalAfterChest();
                return;
            }

            if (!HasOpeningChestCofferCompletionEvidence() &&
                TryRecoverMissingOpeningChestCoffer(now, inCombat, hasFlagRecoveryTarget, flagRecoveryTarget, distToFlag))
            {
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
            if (TryGuardMapCompletionWithActiveKeyItem("[OpeningChest]"))
                return;
            CheckForPortalAfterChest();
            return;
        }

        chestConfirmedThisMap = true;
        openingChestReturningToLastKnownCoffer = false;
        ResetOpeningChestMissingCofferRecoveryState();

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
        var range = GetOpeningChestCofferInteractionRange(_plugin.Configuration.ChestInteractionRange);
        var chestName = chest.Name.TextValue;

        if (openingChestCombatInterrupted && !inCombat)
        {
            if (openingChestReturningToFlag && autoMoveActive)
            {
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
            }

            _plugin.AddDebugLog(
                $"[OpeningChest] Targetable coffer reacquired after combat at {dist:F1}y - resuming approach/interact immediately.");
            openingChestCombatInterrupted = false;
            openingChestRecoveryDigIssued = false;
            openingChestReturningToFlag = false;
            openingChestReturningToLastKnownCoffer = false;
        }

        if (inCombat)
        {
            ResetOpeningChestCofferMountRecovery();
            ResetOpeningChestCofferWalkFailure();
            StopOpeningChestCofferMovement("because combat started");

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

        // Not in combat - approach and interact with the coffer.
        Plugin.TargetManager.Target = chest;
        
        if (dist > range)
        {
            NavigateToOpeningChestCoffer(chest, chestName, dist, now, fly: false);
            StateDetail = $"Approaching '{chestName}' ({dist:F1}y away)...";
            return;
        }

        StopOpeningChestCofferMovement($"at coffer interaction range for '{chestName}'");
        ResetOpeningChestCofferApproachTracking();

        var mountedOrFlying = _plugin.NavigationService.IsMounted()
            || _plugin.NavigationService.IsFlying()
            || Plugin.Condition[ConditionFlag.Mounting71];
        if (mountedOrFlying)
        {
            if (!Plugin.Condition[ConditionFlag.Mounting71] &&
                now - lastOpeningChestCofferDismountCommandTime >= OpeningChestCofferDismountCommandInterval)
            {
                lastOpeningChestCofferDismountCommandTime = now;
                _mountService.Dismount();
            }

            StateDetail = $"Dismounting at '{chestName}' ({dist:F1}y)...";
            return;
        }

        if (!IsCharacterReady())
        {
            StateDetail = $"Waiting to interact with '{chestName}' ({DescribeCharacterReadyBlockers()})...";
            return;
        }

        // Continually try to interact every ~1 second (only when NOT in combat)
        if ((now - lastInteractionTime).TotalSeconds >= 1.0)
        {
            if (AttemptOpeningChestCofferInteraction(chest, chestName, "at interaction range", now))
            {
                lastInteractionTime = now;
                StateDetail = $"Interacting with '{chestName}' - waiting for portal...";
            }
        }
    }

    private void CheckForPortalAfterChest()
    {
        ResetOpeningChestCofferMountRecovery("before portal search", stopNavigation: true);
        ResetOpeningChestCofferWalkFailure();

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

    private bool ShouldLogPortalObjectScan(DateTime now)
    {
        if (portalRetryStart == DateTime.MinValue)
            return true;

        if (now - lastPortalObjectScanLogTime < PortalObjectScanLogInterval)
            return false;

        lastPortalObjectScanLogTime = now;
        return true;
    }
    
    private IGameObject? FindNearestPortal(bool keepActivePortalWindow = false)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        var maxRange = keepActivePortalWindow
            ? OverworldRecoveryObjectSearchRange
            : PortalNormalSearchRange;

        var portalCandidates = Plugin.ObjectTable
            .Where(obj => obj != null && obj.Name.ToString() == "Teleportation Portal")
            .Select(obj => new
            {
                Portal = obj,
                Distance = Vector3.Distance(player.Position, obj.Position),
            })
            .OrderBy(candidate => candidate.Distance)
            .ToList();

        var now = DateTime.Now;
        var logScanDetails = portalCandidates.Count > 0 && ShouldLogPortalObjectScan(now);

        foreach (var candidate in portalCandidates)
        {
            var portalObj = candidate.Portal;
            var dist = candidate.Distance;
            if (logScanDetails)
                _plugin.AddDebugLog($"[Portal] Found portal at {dist:F1}y distance, XYZ {FormatVectorCompact(portalObj.Position)}");

            // Verify portal is targetable (not a ghost object)
            if (!IsObjectTargetable(portalObj, logScanDetails))
            {
                if (logScanDetails)
                    _plugin.AddDebugLog("[Portal] Portal is NOT targetable (ghost object) - ignoring");
                continue;
            }

            if (dist > maxRange)
            {
                if (logScanDetails)
                    _plugin.AddDebugLog($"[Portal] Portal too far ({dist:F1}y > {maxRange:F0}y)");
                continue;
            }

            if (dist > PortalNormalSearchRange)
            {
                var keepCapturedPortal = keepActivePortalWindow
                    && portalApproachPosition.HasValue
                    && Vector3.Distance(portalObj.Position, portalApproachPosition.Value) <= 5f;
                var portalBasis = keepCapturedPortal && portalApproachPosition.HasValue
                    ? $"captured XYZ {FormatVectorCompact(portalApproachPosition.Value)}"
                    : "live targetable portal";
                if (logScanDetails)
                    _plugin.AddDebugLog($"[Portal] Keeping active portal window despite distance {dist:F1}y; {portalBasis}");
            }

            portalConfirmedThisMap = true;
            return portalObj;
        }
        
        return null;
    }

    private IGameObject? FindTargetablePortal(float maxRange)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        try
        {
            var portalCandidates = Plugin.ObjectTable
                .Where(obj => obj != null && obj.Name.ToString() == "Teleportation Portal")
                .Select(obj => new
                {
                    Portal = obj,
                    Distance = Vector3.Distance(player.Position, obj.Position),
                })
                .Where(candidate => candidate.Distance <= maxRange)
                .OrderBy(candidate => candidate.Distance)
                .ToList();

            foreach (var candidate in portalCandidates)
            {
                if (IsObjectTargetable(candidate.Portal, logResult: false))
                    return candidate.Portal;
            }
        }
        catch (Exception ex)
        {
            _plugin.AddDebugLog($"[Portal] Targetable portal scan failed: {ex.Message}");
        }

        return null;
    }

    private bool IsOverworldMapDutyActive()
    {
        var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                     Plugin.Condition[ConditionFlag.BoundByDuty56];
        return inDuty && !IsTreasureDungeonTerritory(Plugin.ClientState.TerritoryType);
    }

    private IGameObject? FindTargetableOverworldCoffer(float maxRange)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return null;

        try
        {
            return Plugin.ObjectTable
                .Where(obj => obj != null &&
                              ChestDetectionService.IsCofferObject(obj) &&
                              obj.IsTargetable)
                .Select(obj => new
                {
                    Coffer = obj,
                    Distance = Vector3.Distance(player.Position, obj.Position),
                })
                .Where(candidate => candidate.Distance <= maxRange)
                .OrderBy(candidate => candidate.Distance)
                .Select(candidate => candidate.Coffer)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _plugin.AddDebugLog($"[OpeningChest] Overworld coffer rescan failed: {ex.Message}");
            return null;
        }
    }

    private bool TryRecoverOverworldCofferFromCompleted(string source)
    {
        if (!IsOverworldMapDutyActive())
            return false;

        var loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                      Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (loading)
            return false;

        var chest = FindTargetableOverworldCoffer(OverworldRecoveryObjectSearchRange);
        if (chest == null)
            return false;

        var player = Plugin.ObjectTable.LocalPlayer;
        var dist = player == null
            ? float.MaxValue
            : Vector3.Distance(player.Position, chest.Position);
        CaptureOpeningChestCofferPosition(chest);
        _plugin.AddDebugLog(
            $"{source} Targetable Treasure Coffer found at {dist:F1}y while overworld map duty is active in territory {Plugin.ClientState.TerritoryType}; returning to OpeningChest.");

        EndPortalRetryWindow();
        chestDisappearedTime = DateTime.MinValue;
        TransitionTo(BotState.OpeningChest, "Recovered overworld treasure coffer - opening chest...");
        return true;
    }

    private void OnCombatStart()
    {
        SetCombatAutomationForCombatState(inCombat: true, "combat start");
        SendCombatMovementForbidOn(); // Keep this in mind for later. we may want to disable this have to see how it behaves in certain situations haha.
        ResetPortaPraetoriaTakeoffNudge("[Combat] combat start", stopAutomove: true);
        ResetOpeningChestFlagFallback("combat start", logIfActive: true);
        ResetPortalGroundApproachTracking(resetFailure: true);
        lastCombatEndTime = DateTime.MinValue; // Reset combat end time

        if (State == BotState.OpeningChest)
        {
            openingChestCombatInterrupted = true;
            openingChestRecoveryDigIssued = false;
            openingChestReturningToFlag = false;
            openingChestReturningToLastKnownCoffer = false;
            chestDisappearedTime = DateTime.MinValue;
            ResetOpeningChestCofferMountRecovery("because combat started", stopNavigation: true);
            ResetOpeningChestCofferWalkFailure();
            var evidence = TryDescribeActiveOpeningChestCofferEvidence(includeLiveScan: true, out var evidenceText)
                ? evidenceText
                : "no active coffer evidence";
            _plugin.AddDebugLog($"[OpeningChest] Combat paused chest recovery; {evidence}. Will rescan after combat.");
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
        if (!TryKeepCombatAutomationForJoinedFate("combat end while joined FATE"))
            SetCombatAutomationForCombatState(inCombat: false, "combat end");

        combatMovementForbidSentThisCombat = false;
        lastCombatEndTime = DateTime.Now;

        if (IsJoinedFateOutdoorInterventionFlowActive())
        {
            NormalizeLandingModeForSelectedMap("[CombatEnd]");
            _plugin.AddDebugLog(
                $"[CombatEnd] Map context: mapId={SelectedMapItemId}; landing={currentLandingMode}.");
        }
        
        // ALWAYS reset to chest priority after combat
        currentObjective = DungeonObjective.ClearingChests;
        dungeonLoadWaitStart = DateTime.MinValue;

        if (State == BotState.OpeningChest && openingChestCombatInterrupted)
        {
            var evidence = TryDescribeActiveOpeningChestCofferEvidence(includeLiveScan: true, out var evidenceText)
                ? evidenceText
                : "no active coffer evidence; bounded missing-coffer recovery will run before map-target/key-item recovery";
            _plugin.AddDebugLog($"[OpeningChest] Combat ended - resuming coffer recovery; {evidence}.");
        }
        
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
        ClickYesIfVisibleWithDiagnostics("InDungeon.generic-dialog");

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

        // Check for card game addon with observed TreasureHighLow false -2 -> TreasureHighLow true 1 pair.
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
        ClickYesIfVisibleWithDiagnostics("DungeonCombat.generic-dialog");

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
        ClickYesIfVisibleWithDiagnostics("DungeonLooting.generic-dialog");

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
        ClickYesIfVisibleWithDiagnostics("DungeonProgressing.generic-dialog");

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
                MarkPortalInteractionProgress(now, "loading screen");
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
                if (ClickYesIfVisibleWithDiagnostics("Portal.confirm"))
                {
                    MarkPortalInteractionProgress(now, "SelectYesno");
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
                if (portal == null && TryRecoverOverworldCofferFromCompleted("[Portal]"))
                    return;

                if (TryHandleConfirmedDutyEntry("[Portal]", portalAvailable: portal != null))
                    return;

                if (portal != null)
                {
                    // Double-check portal is still targetable before attempting to move
                    if (!IsObjectTargetable(portal, logResult: false))
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

                    if (portalCloseNudgeActive &&
                        TryRunPortalCloseNudgeRecovery(portal, approachPosition, portalDist, now))
                    {
                        return;
                    }

                    if (portalDist > PortalInteractionRange)
                    {
                        if (TryRunPortalGroundApproach(portal.EntityId, approachPosition, now, $"entity={portal.EntityId}"))
                            return;

                        if (!portalRegularVnavPathLogged)
                        {
                            portalRegularVnavPathLogged = true;
                            _plugin.AddDebugLog("[Portal] Portal approach uses captured XYZ vnav only; lockon+automove disabled.");
                        }

                        if (FlyToPortalApproachPosition(approachPosition, portalDist))
                        {
                            StateDetail = $"Approaching portal XYZ {FormatVectorCompact(approachPosition)} ({portalDist:F1}y)...";
                        }

                        TryPortalApproachInteraction(portal, approachPosition, portalDist, now);
                        return;
                    }

                    HandlePortalInInteractionRange(portal, approachPosition, portalDist, now);
                    return;
                }

                if (portalApproachPosition.HasValue)
                {
                    var player = Plugin.ObjectTable.LocalPlayer;
                    if (player != null)
                    {
                        var approachPosition = portalApproachPosition.Value;
                        var portalDist = Vector3.Distance(player.Position, approachPosition);

                        if (portalDist > PortalInteractionRange)
                        {
                            if (TryRunPortalGroundApproach(0, approachPosition, now, "captured XYZ"))
                                return;

                            if (!portalRegularVnavPathLogged)
                            {
                                portalRegularVnavPathLogged = true;
                                _plugin.AddDebugLog("[Portal] Portal object dropped out; flying to captured XYZ for recovery.");
                            }

                            if (FlyToPortalApproachPosition(approachPosition, portalDist))
                            {
                                StateDetail = $"Approaching captured portal XYZ {FormatVectorCompact(approachPosition)} ({portalDist:F1}y)...";
                            }
                            return;
                        }

                        if (_plugin.NavigationService.State != NavigationState.Idle)
                        {
                            _plugin.NavigationService.StopNavigation();
                            autoMoveActive = false;
                        }

                        ResetPortalGroundApproachTracking();

                        StateDetail = $"At captured portal XYZ - waiting for targetable portal... ({sinceStart.TotalSeconds:F0}/{portalWindowTimeout.TotalSeconds:F0}s)";
                        return;
                    }
                }

                StateDetail = portalApproachPosition.HasValue
                    ? $"Waiting for captured portal approach... ({sinceStart.TotalSeconds:F0}/{portalWindowTimeout.TotalSeconds:F0}s)"
                    : $"Searching for portal... ({sinceStart.TotalSeconds:F0}/{portalWindowTimeout.TotalSeconds:F0}s)";
                return;
            }

            var timedOutPortal = FindNearestPortal(keepActivePortalWindow: true);
            if (timedOutPortal != null)
                CapturePortalApproachPosition(timedOutPortal);
            else if (TryRecoverOverworldCofferFromCompleted("[PortalTimeout]"))
                return;

            var mapDutyStillActive = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                                     Plugin.Condition[ConditionFlag.BoundByDuty56];
            var portalTimeoutAction = PortalTimeoutPolicy.Evaluate(
                new PortalTimeoutState(
                    mapDutyStillActive,
                    timedOutPortal != null,
                    portalApproachPosition.HasValue));

            if (TryHandleConfirmedDutyEntry(
                    "[Portal]",
                    portalAvailable: portalTimeoutAction == PortalTimeoutAction.ContinuePortalInteraction))
                return;

            if (portalTimeoutAction != PortalTimeoutAction.CompleteMap)
            {
                var currentTerritory = Plugin.ClientState.TerritoryType;
                if (mapDutyStillActive && !IsTreasureDungeonTerritory(currentTerritory))
                    LogMapDutyOutsideDungeon("[Portal]", currentTerritory);

                if (now - lastPortalTimeoutHoldLogTime >= TimeSpan.FromSeconds(5.0))
                {
                    lastPortalTimeoutHoldLogTime = now;
                    var holdReason = portalTimeoutAction == PortalTimeoutAction.ContinuePortalInteraction
                        ? "live targetable portal"
                        : "map-duty state";
                    _plugin.AddDebugLog(
                        $"[Portal] Portal window timeout reached, but {holdReason} is still active - continuing instead of marking map complete.");
                }

                if (portalTimeoutAction == PortalTimeoutAction.ContinuePortalInteraction)
                    portalRetryStart = now;

                StateDetail = portalTimeoutAction == PortalTimeoutAction.ContinuePortalInteraction
                    ? "Portal still active - continuing portal interaction..."
                    : $"Map duty active in territory {currentTerritory} - waiting for duty to clear...";
                return;
            }

            // Time elapsed, no usable portal entry - map is complete (no dungeon)
            EndPortalRetryWindow();
            if (TryGuardMapCompletionWithActiveKeyItem("[PortalTimeout]"))
                return;

            _plugin.AddDebugLog($"[Portal] No portal found after {PortalSearchTimeout.TotalSeconds:F0}s - map complete (no dungeon)");
            if (autoMoveActive)
            {
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
            }
            adsDutyEntryConfirmedAt = DateTime.MinValue;
        }

        if (TryGuardMapCompletionWithActiveKeyItem("[Completed]"))
            return;
        
        if (!stateActionIssued)
        {
            if (openingChestManualInterventionSuspected)
                _plugin.AddDebugLog("[Completed] Map run ended with manual/inconclusive coffer evidence; not marking as clean LootGoblin chest-open success.");
            else
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
                if (TryRecoverOverworldCofferFromCompleted("[Completed]"))
                    return;

                LogMapDutyOutsideDungeon("[Completed]", currentTerritory);
                StateDetail = $"Map duty active in territory {currentTerritory} - recovering coffer/portal...";
                return;
            }

            _plugin.AddDebugLog("[Completed] ERROR: Still in dungeon (BoundByDuty) - cannot start next map!");
            TransitionTo(BotState.Error, "Still in dungeon - cannot start next map. Manual intervention required.");
            return;
        }

        if (TryStartAdsRepairIfNeeded("[Completed]", resumeStartAfterRepair: false))
            return;
        
        KrangleService.ClearCache();

        if (TryResumeAlexandriteAfterCompleted())
            return;

        if (_plugin.Configuration.AutoStartNextMap)
        {
            if (TryHoldCompletedNextMapStartup("[CompletedNextMap]"))
                return;

            if (!CanStartNextMapAfterPartyWait())
                return;

            if (TryRunCompletedMapRefreshBeforeDecisions())
                return;

            _plugin.AddDebugLog("[Completed] AutoStartNextMap enabled - scanning for maps");
            var mapSources = _plugin.InventoryService.ScanForMapSources(
                includeSaddlebags: _plugin.Configuration.EnableSaddlebagMapRetrieval);
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
                var enabledForRetainers = _plugin.Configuration.GetRunnableMapIds(TreasureMapData.AllMapItemIds);
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

            var enabledForGather = _plugin.Configuration.GetRunnableMapIds(TreasureMapData.AllMapItemIds);
            if (TryStartMapGatherFallback(enabledForGather, "No runnable maps in inventory, saddlebags, or retainers."))
                return;
        }

        if (TryRunCompletedMapRefreshBeforeDecisions())
            return;

        var remainingMaps = HasRemainingEnabledMaps("[Completed]");
        RunFinishCommandsOnce("[Completed] run complete");
        if (!remainingMaps)
            RunReturnWhenDoneOnce("[Completed] no maps remaining");
        RetryCount = 0;
        TransitionTo(BotState.Idle, "Run complete.");
    }

    private bool TryGuardMapCompletionWithActiveKeyItem(string source)
    {
        if (!_plugin.InventoryService.TryFindTreasureMapKeyItem(out var keyItem))
        {
            ResetKeyItemMapRecoveryState(clearActiveKey: true);
            ClearCompletedStaleKeyItemSuppression($"{source} active key item missing");
            return false;
        }

        UpdateActiveKeyItemMap(keyItem, source);

        var hadSuppressedKey = IsSameCompletedStaleKeyItem(keyItem);
        if (TryHandleCompletedStaleKeyItemSuppression(keyItem, source, out var suppressRecovery))
            return true;
        if (suppressRecovery || HasCompletedKeyItemCompletionEvidence() || hadSuppressedKey)
            return false;

        if (TryHoldCompletedNextMapStartup(source))
            return true;

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

        if (TryResolveActiveKeyItemMapTarget(keyItem, out var recoveryLocation, out var recoverySource))
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player != null && recoveryLocation.TerritoryId == Plugin.ClientState.TerritoryType)
            {
                var flagTarget = new Vector3(recoveryLocation.X, recoveryLocation.Y, recoveryLocation.Z);
                var xzDist = CalculateXZDistance(player.Position, flagTarget);
                _plugin.AddDebugLog(xzDist <= MapDigXZRange
                    ? $"{source} Key item still active and player is {xzDist:F1}y XZ from recovered target - retrying dig."
                    : $"{source} Key item still active and player is {xzDist:F1}y XZ from recovered target - resuming navigation.");
            }
            else
            {
                _plugin.AddDebugLog($"{source} Key item still active - recovered target from {recoverySource} and resuming map run.");
            }

            ResumeActiveKeyItemMapFromTarget(keyItem, recoveryLocation, recoverySource, source);
            return true;
        }

        _plugin.AddDebugLog($"{source} Key item still active but no current flag, capture, or cached target is available - entering bounded recovery.");
        ResetKeyItemMapRecoveryState();
        TransitionTo(BotState.DetectingLocation, $"Recovering active map target for {keyItem.DisplayName}...");
        return true;
    }

    private bool CanStartNextMapAfterPartyWait()
    {
        var party = _plugin.PartyService;
        if (!party.UpdatePartyStatus())
        {
            StateDetail = "Waiting for party snapshot before next map...";
            LogLandingPartyWaitOnce(
                "CompletedNextMap:snapshot-invalid",
                "[PartyWait][CompletedNextMap] Party snapshot unavailable - blocking next map");
            return false;
        }

        if (party.PartyMembers.Count <= 1)
        {
            LogLandingPartyWaitOnce(
                "CompletedNextMap:solo",
                "[PartyWait][CompletedNextMap] Solo or no party members - next map allowed");
            return true;
        }

        var useThreshold = _plugin.Configuration.PartyWaitBeforeDismountUseCountThreshold;
        var loadedOtherCount = party.PartyMembers.Count(member =>
            !member.IsLocalPlayer &&
            PartyGateSemantics.IsLoadedSameTerritory(member.IsLoaded, member.TerritoryStatus));
        var totalOtherCount = party.PartyMembers.Count(member => !member.IsLocalPlayer);
        var requiredOthers = PartyGateSemantics.ResolveRequiredOthers(
            totalOtherCount,
            useThreshold,
            _plugin.Configuration.PartyWaitBeforeDismountRequiredOthers);

        if (useThreshold)
        {
            if (loadedOtherCount >= requiredOthers)
            {
                LogLandingPartyWaitOnce(
                    $"CompletedNextMap:threshold-clear:{loadedOtherCount}:{requiredOthers}",
                    $"[PartyWait][CompletedNextMap] Required loaded party count met ({loadedOtherCount}/{requiredOthers} other players) - next map allowed");
                return true;
            }

            StateDetail =
                $"Waiting for party to load after dungeon ({loadedOtherCount}/{requiredOthers} required other players in zone)...";

            LogLandingPartyWaitOnce(
                $"CompletedNextMap:threshold-block:{loadedOtherCount}:{requiredOthers}",
                $"[PartyWait][CompletedNextMap] Blocking next map; loaded other players {loadedOtherCount}/{requiredOthers} required.");

            return false;
        }

        var unavailableNames = party.PartyMembers
            .Where(member => !PartyGateSemantics.IsLoadedSameTerritory(member.IsLoaded, member.TerritoryStatus))
            .Select(member => member.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (unavailableNames.Count == 0)
        {
            LogLandingPartyWaitOnce(
                $"CompletedNextMap:clear:{party.PartyMembers.Count}",
                "[PartyWait][CompletedNextMap] All party members loaded in same zone - next map allowed");
            return true;
        }

        var loadedCount = party.PartyMembers.Count - unavailableNames.Count;
        var missingText = string.Join(", ", unavailableNames);
        StateDetail =
            $"Waiting for party to load after dungeon ({loadedCount}/{party.PartyMembers.Count} in zone; missing: {missingText})...";

        LogLandingPartyWaitOnce(
            $"CompletedNextMap:block:{string.Join("|", unavailableNames)}",
            $"[PartyWait][CompletedNextMap] Blocking next map; members not loaded in same zone: {missingText}");

        return false;
    }

    private bool TryRunCompletedMapRefreshBeforeDecisions()
    {
        if (!_plugin.Configuration.EnableSaddlebagMapRetrieval)
            return false;

        if (!completedSaddlebagRefreshAttempted)
        {
            completedSaddlebagRefreshAttempted = true;
            BeginCompletedMapRefresh();
        }

        return TickStartMapRefresh();
    }

    private bool HasRemainingEnabledMaps(string source)
    {
        var mapSources = _plugin.InventoryService.ScanForMapSources(
            includeSaddlebags: _plugin.Configuration.EnableSaddlebagMapRetrieval);
        var loadedMaps = GetEnabledMapCandidates(mapSources, includeInventory: true, includeSaddlebags: true);
        if (loadedMaps.Count > 0)
        {
            var sourceLabel = _plugin.Configuration.EnableSaddlebagMapRetrieval
                ? "inventory/saddlebag"
                : "inventory";
            _plugin.AddDebugLog($"{source} Return deferred; {loadedMaps.Count} enabled {sourceLabel} map type(s) remain.");
            return true;
        }

        var enabledForRetainers = _plugin.Configuration.GetRunnableMapIds(TreasureMapData.AllMapItemIds);
        var hasRetainerMap = _plugin.RetainerMapRetrievalService.HasRetainerMapCandidate(enabledForRetainers);
        if (hasRetainerMap)
            _plugin.AddDebugLog($"{source} Return deferred; enabled retainer map remains via XADB.");

        return hasRetainerMap;
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
        ResetUnderwaterXyzDigRetryState();

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
        _plugin.AddDebugLog($"{source} BoundByDuty active in territory {territoryId}, but this is not a known treasure dungeon territory. Recovering coffer/portal; no map flag clear and no ADS handoff.");
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

    private bool TryStartAdsRepairIfNeeded(string source, bool resumeStartAfterRepair)
    {
        var threshold = Math.Clamp(_plugin.Configuration.RepairThresholdPercent, 0, 100);
        if (threshold <= 0)
            return false;

        if (!_plugin.InventoryService.TryGetLowestEquippedGearConditionPercent(out var lowestCondition))
        {
            _plugin.AddDebugLog($"{source}[Repair] Could not read equipped gear durability; continuing without repair.");
            return false;
        }

        if (lowestCondition >= threshold)
            return false;

        if (!_plugin.IsAdsAvailable)
        {
            TransitionTo(BotState.Error, $"Equipped gear durability is {lowestCondition}% below repair threshold {threshold}%, but ADS is not loaded.");
            return true;
        }

        var repairMode = ResolveAdsRepairMode();
        if (repairMode == AdsRepairModeNpcNoInn && !GameHelpers.IsInSanctuary())
        {
            return BeginAdsRepairTeleportRecovery(source, repairMode, lowestCondition, threshold, resumeStartAfterRepair);
        }

        continueStartAfterAdsRepair = resumeStartAfterRepair;
        return TryRequestAdsRepair(source, repairMode, lowestCondition, threshold);
    }

    private bool BeginAdsRepairTeleportRecovery(
        string source,
        string repairMode,
        int lowestCondition,
        int threshold,
        bool resumeStartAfterRepair)
    {
        adsRepairSource = source;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            TransitionTo(BotState.Error, $"Equipped gear durability is {lowestCondition}% below repair threshold {threshold}%, but player position is unavailable for ADS repair recovery.");
            return true;
        }

        var territoryId = Plugin.ClientState.TerritoryType;
        var playerPosition = player.Position;
        var aetheryteId = _plugin.NavigationService.FindNearestAetheryte(territoryId, playerPosition, out _, out _);
        if (aetheryteId == 0)
        {
            TransitionTo(
                BotState.Error,
                $"Equipped gear durability is {lowestCondition}% below repair threshold {threshold}%, but no unlocked aetheryte was found in current territory {territoryId} for ADS NPC no-inn repair.");
            return true;
        }

        var aetheryteName = GetAetheryteName(aetheryteId);
        var playerDistanceToAetheryte = _plugin.NavigationService.GetPlayerXZDistanceToAetheryte(aetheryteId);
        if (playerDistanceToAetheryte is { } aetheryteDistance &&
            aetheryteDistance <= SameZoneAetheryteTeleportSkipXZRange)
        {
            continueStartAfterAdsRepair = resumeStartAfterRepair;
            _plugin.AddDebugLog(
                $"{source}[Repair] Player already {aetheryteDistance:F1}y XZ from selected aetheryte " +
                $"{aetheryteName} (ID {aetheryteId}); starting ADS repair without teleport.");
            TryRequestAdsRepair(source, repairMode, lowestCondition, threshold);
            return true;
        }

        adsRepairRecoveryActive = true;
        adsRepairRecoveryTeleportIssued = false;
        adsRepairRecoveryStarted = DateTime.Now;
        adsRepairRecoveryTeleportIssuedAt = DateTime.MinValue;
        adsRepairRecoveryStartPosition = playerPosition;
        adsRepairRecoverySawBetweenAreas = false;
        adsRepairRecoveryLastLoadingAt = DateTime.MinValue;
        adsRepairRecoveryStartAttempted = false;
        adsRepairRecoveryTerritoryId = territoryId;
        adsRepairRecoveryAetheryteId = aetheryteId;
        adsRepairRecoveryAetheryteName = aetheryteName;
        adsRepairRecoveryMode = repairMode;
        adsRepairRecoverySource = source;
        adsRepairRecoveryLowestCondition = lowestCondition;
        adsRepairRecoveryThreshold = threshold;
        adsRepairRecoveryNextTeleportAttemptAt = DateTime.Now + AdsRepairRecoveryInitialTeleportDelay;
        adsRepairRecoveryTeleportRetryCount = 0;
        adsRepairRecoveryLastTeleportFailure = string.Empty;
        continueStartAfterAdsRepair = resumeStartAfterRepair;

        StateDetail = $"Gear durability {lowestCondition}% below {threshold}% - teleporting to {adsRepairRecoveryAetheryteName} for ADS repair...";
        _plugin.AddDebugLog(
            $"{source}[Repair] NPC no-inn repair needs sanctuary; selected nearest current-territory aetheryte " +
            $"{adsRepairRecoveryAetheryteName} (ID {aetheryteId}) from player position {FormatVectorCompact(playerPosition)}; " +
            $"waiting {AdsRepairRecoveryInitialTeleportDelay.TotalSeconds:F1}s before Lifestream command.");
        return true;
    }

    private bool TryRequestAdsRepair(string source, string repairMode, int lowestCondition, int threshold)
    {
        adsRepairSource = source;

        if (!_plugin.AdsStatusService.StartRepair(repairMode))
        {
            var adsStatus = _plugin.AdsStatusService.Refresh(force: true);
            if (IsNpcNoTeleportNoInnNoMenderSkip(repairMode, adsStatus))
            {
                var statusText = GetAdsRepairStatusText(adsStatus);
                ResetAdsRepairHandoffTracking();
                StateDetail = $"No nearby repair NPC for ADS {repairMode}; continuing map flow.";
                _plugin.AddDebugLog(
                    $"{source}[Repair] ADS {repairMode} soft skip: {statusText} Continuing map flow without repair.");
                return false;
            }

            ScheduleAdsRepairRetry(
                $"Could not start ADS repair ({repairMode}) at {lowestCondition}% durability.",
                adsStatus,
                lowestCondition,
                threshold);
            return true;
        }

        adsRepairHandoffActive = true;
        adsRepairUtilityObserved = false;
        adsRepairHandoffStarted = DateTime.Now;
        adsRepairRequestedMode = repairMode;
        StateDetail = $"ADS repair requested ({repairMode}); durability {lowestCondition}% below threshold {threshold}%...";
        _plugin.AddDebugLog($"{source}[Repair] Requested ADS repair mode {repairMode}; lowest durability {lowestCondition}%, threshold {threshold}%.");
        return true;
    }

    private bool TickAdsRepairRecovery()
    {
        if (!adsRepairRecoveryActive)
            return false;

        var now = DateTime.Now;
        var elapsed = now - adsRepairRecoveryStarted;
        if (elapsed > AdsRepairRecoveryTimeout)
        {
            FailAdsRepairRecovery(
                $"ADS repair recovery timed out after {AdsRepairRecoveryTimeout.TotalSeconds:F0}s while teleporting to {adsRepairRecoveryAetheryteName} (ID {adsRepairRecoveryAetheryteId}).");
            return true;
        }

        if (!_plugin.IsAdsAvailable)
        {
            FailAdsRepairRecovery("ADS unloaded during repair recovery; cannot start ADS repair.");
            return true;
        }

        if (Plugin.ClientState.TerritoryType != adsRepairRecoveryTerritoryId)
        {
            FailAdsRepairRecovery(
                $"Wrong territory during ADS repair recovery: {Plugin.ClientState.TerritoryType} (expected {adsRepairRecoveryTerritoryId}).");
            return true;
        }

        if (!adsRepairRecoveryTeleportIssued)
        {
            if (!_plugin.IsLifestreamAvailable)
            {
                _plugin.ShowLifestreamMissingToast();
                FailAdsRepairRecovery("Lifestream is not loaded; cannot teleport to aetheryte for ADS NPC no-inn repair.");
                return true;
            }

            if (Plugin.Condition[ConditionFlag.InCombat])
            {
                FailAdsRepairRecovery("Cannot teleport to aetheryte for ADS NPC no-inn repair while in combat.");
                return true;
            }

            if (now < adsRepairRecoveryNextTeleportAttemptAt)
            {
                var waitRemaining = adsRepairRecoveryNextTeleportAttemptAt - now;
                var retryText = adsRepairRecoveryTeleportRetryCount > 0
                    ? $"retry {adsRepairRecoveryTeleportRetryCount}/{AdsRepairRecoveryTeleportMaxRetries}"
                    : "initial command";
                StateDetail = $"Waiting {waitRemaining.TotalSeconds:F1}s before repair teleport {retryText} to {adsRepairRecoveryAetheryteName}...";
                return true;
            }

            if (!TryRefreshAdsRepairRecoveryAetheryte(out var refreshFailure))
            {
                FailAdsRepairRecovery(refreshFailure);
                return true;
            }

            if (_plugin.NavigationService.State != NavigationState.Idle)
                _plugin.NavigationService.StopNavigation();

            adsRepairRecoveryStartPosition = Plugin.ObjectTable.LocalPlayer?.Position ?? adsRepairRecoveryStartPosition;
            adsRepairRecoverySawBetweenAreas = false;
            adsRepairRecoveryLastLoadingAt = DateTime.MinValue;
            adsRepairRecoveryStartAttempted = false;
            adsRepairRecoveryLastTeleportFailure = string.Empty;

            _plugin.NavigationService.TeleportToAetheryte(adsRepairRecoveryAetheryteId);
            adsRepairRecoveryTeleportIssued = true;
            adsRepairRecoveryTeleportIssuedAt = now;
            StateDetail = $"Teleporting to {adsRepairRecoveryAetheryteName} for ADS repair...";

            if (_plugin.NavigationService.State == NavigationState.Error)
            {
                FailAdsRepairRecovery($"Could not teleport to {adsRepairRecoveryAetheryteName} for ADS repair: {_plugin.NavigationService.StateDetail}");
                return true;
            }

            return true;
        }

        if (_plugin.NavigationService.State == NavigationState.Error)
        {
            FailAdsRepairRecovery($"Teleport to {adsRepairRecoveryAetheryteName} for ADS repair failed: {_plugin.NavigationService.StateDetail}");
            return true;
        }

        var loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                      Plugin.Condition[ConditionFlag.BetweenAreas51] ||
                      _plugin.NavigationService.IsTeleporting();
        var teleportElapsed = now - adsRepairRecoveryTeleportIssuedAt;
        if (teleportElapsed < AdsRepairRecoveryTeleportSettleDelay || loading)
        {
            StateDetail = $"Teleporting to {adsRepairRecoveryAetheryteName} for ADS repair... ({teleportElapsed.TotalSeconds:F0}s)";
            return true;
        }

        var playerPosition = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var positionDelta = playerPosition != Vector3.Zero && adsRepairRecoveryStartPosition != Vector3.Zero
            ? Vector3.Distance(playerPosition, adsRepairRecoveryStartPosition)
            : 0.0f;
        var positionChanged = positionDelta >= AdsRepairRecoveryTeleportPositionDeltaThreshold;
        if (!adsRepairRecoverySawBetweenAreas && !positionChanged)
        {
            StateDetail =
                $"Waiting for repair teleport settle proof at {adsRepairRecoveryAetheryteName} " +
                $"({teleportElapsed.TotalSeconds:F0}s, moved {positionDelta:F1}y)...";
            return true;
        }

        var lastLoadingText = adsRepairRecoveryLastLoadingAt == DateTime.MinValue
            ? "never"
            : $"{(now - adsRepairRecoveryLastLoadingAt).TotalSeconds:F1}s ago";
        var sanctuaryHeuristic = GameHelpers.IsInSanctuary();
        _plugin.AddDebugLog(
            $"{adsRepairRecoverySource}[Repair] Repair teleport settled at {adsRepairRecoveryAetheryteName} " +
            $"(ID {adsRepairRecoveryAetheryteId}); territory={Plugin.ClientState.TerritoryType}; " +
            $"positionDelta={positionDelta:F1}y; sawBetweenAreas={adsRepairRecoverySawBetweenAreas}; " +
            $"lastLoading={lastLoadingText}; sanctuaryHeuristic={sanctuaryHeuristic}.");

        return StartAdsRepairAfterRecovery("Starting ADS after repair teleport settle.");
    }

    private bool HandleAdsRepairRecoveryTeleportFailure(string message, bool navigationFailureHandled)
    {
        if (!adsRepairRecoveryActive || !adsRepairRecoveryTeleportIssued)
            return false;

        var destination = !string.IsNullOrWhiteSpace(_plugin.NavigationService.LastTeleportDestinationName)
            ? _plugin.NavigationService.LastTeleportDestinationName
            : adsRepairRecoveryAetheryteName;
        var aetheryteId = _plugin.NavigationService.LastTeleportAetheryteId != 0
            ? _plugin.NavigationService.LastTeleportAetheryteId
            : adsRepairRecoveryAetheryteId;
        var cleanMessage = string.IsNullOrWhiteSpace(message)
            ? "Destination could not be found."
            : message.Trim();
        var source = string.IsNullOrWhiteSpace(adsRepairRecoverySource)
            ? "[RepairRecovery]"
            : adsRepairRecoverySource;
        var commandAttempts = adsRepairRecoveryTeleportRetryCount + 1;

        adsRepairRecoveryLastTeleportFailure = cleanMessage;
        if (adsRepairRecoveryTeleportRetryCount >= AdsRepairRecoveryTeleportMaxRetries)
        {
            FailAdsRepairRecovery(
                $"Lifestream rejected ADS repair teleport to {destination} (ID {aetheryteId}) " +
                $"after {commandAttempts} command attempt(s): {cleanMessage}");
            return true;
        }

        adsRepairRecoveryTeleportRetryCount++;
        adsRepairRecoveryTeleportIssued = false;
        adsRepairRecoveryTeleportIssuedAt = DateTime.MinValue;
        adsRepairRecoverySawBetweenAreas = false;
        adsRepairRecoveryLastLoadingAt = DateTime.MinValue;
        adsRepairRecoveryNextTeleportAttemptAt = DateTime.Now + AdsRepairRecoveryTeleportRetryCooldown;

        var retryDetail = $"{adsRepairRecoveryTeleportRetryCount}/{AdsRepairRecoveryTeleportMaxRetries}";
        StateDetail =
            $"Lifestream rejected repair teleport to {destination}: {cleanMessage}. Retrying ({retryDetail})...";
        _plugin.AddDebugLog(
            $"{source}[Repair] Lifestream rejected repair teleport to {destination} (ID {aetheryteId}): {cleanMessage}. " +
            $"Retrying after {AdsRepairRecoveryTeleportRetryCooldown.TotalSeconds:F1}s ({retryDetail}); " +
            $"navigationStateHandled={navigationFailureHandled}.");
        return true;
    }

    private bool TryRefreshAdsRepairRecoveryAetheryte(out string failureMessage)
    {
        failureMessage = string.Empty;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            failureMessage = "Player position is unavailable while resolving ADS repair recovery aetheryte.";
            return false;
        }

        var territoryId = Plugin.ClientState.TerritoryType;
        var aetheryteId = _plugin.NavigationService.FindNearestAetheryte(territoryId, player.Position, out _, out _);
        if (aetheryteId == 0)
        {
            var lastFailure = string.IsNullOrWhiteSpace(adsRepairRecoveryLastTeleportFailure)
                ? string.Empty
                : $" Last Lifestream error: {adsRepairRecoveryLastTeleportFailure}";
            failureMessage =
                $"No unlocked aetheryte was found in current territory {territoryId} for ADS NPC no-inn repair retry." +
                lastFailure;
            return false;
        }

        var aetheryteName = GetAetheryteName(aetheryteId);
        if (aetheryteId != adsRepairRecoveryAetheryteId)
        {
            _plugin.AddDebugLog(
                $"{adsRepairRecoverySource}[Repair] Re-resolved repair teleport aetheryte: " +
                $"{adsRepairRecoveryAetheryteName} (ID {adsRepairRecoveryAetheryteId}) -> {aetheryteName} (ID {aetheryteId}).");
        }

        adsRepairRecoveryTerritoryId = territoryId;
        adsRepairRecoveryAetheryteId = aetheryteId;
        adsRepairRecoveryAetheryteName = aetheryteName;
        return true;
    }

    private bool StartAdsRepairAfterRecovery(string startLogMessage)
    {
        if (adsRepairRecoveryStartAttempted)
            return true;

        var source = string.IsNullOrWhiteSpace(adsRepairRecoverySource)
            ? "[RepairRecovery]"
            : adsRepairRecoverySource;
        var repairMode = string.IsNullOrWhiteSpace(adsRepairRecoveryMode)
            ? ResolveAdsRepairMode()
            : adsRepairRecoveryMode;
        var lowestCondition = adsRepairRecoveryLowestCondition;
        var threshold = adsRepairRecoveryThreshold;

        adsRepairRecoveryStartAttempted = true;
        _plugin.AddDebugLog($"{source}[Repair] {startLogMessage}");
        ResetAdsRepairRecoveryTracking();

        TryRequestAdsRepair(source, repairMode, lowestCondition, threshold);
        return true;
    }

    private void FailAdsRepairRecovery(string message)
    {
        TransitionTo(BotState.Error, message);
    }

    private bool TickAdsRepairRetry()
    {
        if (!adsRepairRetryPending)
            return false;

        var now = DateTime.Now;
        var source = GetAdsRepairSource();
        var waitRemaining = adsRepairRetryAt - now;
        if (waitRemaining > TimeSpan.Zero)
        {
            var retryReason = string.IsNullOrWhiteSpace(adsRepairRetryReason)
                ? "ADS repair failed."
                : adsRepairRetryReason;
            StateDetail =
                $"{retryReason} Retrying ADS repair in {waitRemaining.TotalSeconds:F1}s " +
                $"({adsRepairRetryAttemptCount}/{AdsRepairMaxRetryAttempts})...";
            return true;
        }

        adsRepairRetryPending = false;
        adsRepairRetryAt = DateTime.MinValue;
        adsRepairRetryReason = string.Empty;

        var threshold = Math.Clamp(_plugin.Configuration.RepairThresholdPercent, 0, 100);
        if (threshold <= 0)
            return CompleteAdsRepair(_plugin.AdsStatusService.Refresh(force: true));

        if (!_plugin.InventoryService.TryGetLowestEquippedGearConditionPercent(out var lowestCondition))
        {
            TransitionTo(BotState.Error, "Could not read equipped gear durability before ADS repair retry.");
            return true;
        }

        if (lowestCondition >= threshold)
            return CompleteAdsRepair(_plugin.AdsStatusService.Refresh(force: true));

        if (!_plugin.IsAdsAvailable)
        {
            var adsStatus = _plugin.AdsStatusService.Refresh(force: true);
            var diagnostic = BuildAdsRepairFailureDiagnostic(adsStatus, lowestCondition, threshold);
            TransitionTo(
                BotState.Error,
                $"ADS unloaded before repair retry. {diagnostic}");
            return true;
        }

        var repairMode = ResolveAdsRepairMode();
        _plugin.AddDebugLog(
            $"{source}[Repair] Retrying ADS repair {adsRepairRetryAttemptCount}/{AdsRepairMaxRetryAttempts}; " +
            $"lowest durability {lowestCondition}%, threshold {threshold}%, mode {repairMode}.");

        if (repairMode == AdsRepairModeNpcNoInn && !GameHelpers.IsInSanctuary())
            return BeginAdsRepairTeleportRecovery(source, repairMode, lowestCondition, threshold, continueStartAfterAdsRepair);

        var resumeStartAfterSoftSkip = continueStartAfterAdsRepair;
        if (TryRequestAdsRepair(source, repairMode, lowestCondition, threshold))
            return true;

        if (resumeStartAfterSoftSkip)
        {
            ContinueStartAfterRepair();
            return true;
        }

        return false;
    }

    private bool TickAdsRepairHandoff()
    {
        if (!adsRepairHandoffActive)
            return false;

        var elapsed = DateTime.Now - adsRepairHandoffStarted;
        if (elapsed > AdsRepairTimeout)
        {
            var status = _plugin.AdsStatusService.Refresh(force: true);
            return FinishOrRetryAdsRepairAttempt(
                $"ADS repair timed out after {AdsRepairTimeout.TotalSeconds:F0}s.",
                status);
        }

        var adsStatus = _plugin.AdsStatusService.Refresh(force: true);
        if (!adsStatus.StatusReadable)
        {
            StateDetail = $"ADS repair requested ({adsRepairRequestedMode}) - waiting for ADS status... ({elapsed.TotalSeconds:F0}s)";
            return true;
        }

        if (adsStatus.UtilityRunning)
        {
            adsRepairUtilityObserved = true;
            StateDetail = $"ADS repair running ({adsStatus.UtilityMode})... ({elapsed.TotalSeconds:F0}s, {adsStatus.UtilityStatus})";
            return true;
        }

        if (!adsRepairUtilityObserved && elapsed < AdsRepairStartGrace)
        {
            StateDetail = $"ADS repair requested ({adsRepairRequestedMode}) - waiting for utility start... ({elapsed.TotalSeconds:F0}s)";
            return true;
        }

        var threshold = Math.Clamp(_plugin.Configuration.RepairThresholdPercent, 0, 100);
        if (threshold > 0)
        {
            if (!_plugin.InventoryService.TryGetLowestEquippedGearConditionPercent(out var lowestCondition))
            {
                TransitionTo(BotState.Error, "ADS repair finished, but equipped gear durability could not be read.");
                return true;
            }

            if (lowestCondition < threshold)
            {
                ScheduleAdsRepairRetry(
                    BuildAdsRepairStoppedFailureMessage(adsStatus),
                    adsStatus,
                    lowestCondition,
                    threshold);
                return true;
            }
        }

        return CompleteAdsRepair(adsStatus);
    }

    private bool FinishOrRetryAdsRepairAttempt(string failureMessage, AdsStatusSnapshot adsStatus)
    {
        var threshold = Math.Clamp(_plugin.Configuration.RepairThresholdPercent, 0, 100);
        if (threshold <= 0)
            return CompleteAdsRepair(adsStatus);

        if (!_plugin.InventoryService.TryGetLowestEquippedGearConditionPercent(out var lowestCondition))
        {
            TransitionTo(BotState.Error, $"{failureMessage} Could not read equipped gear durability.");
            return true;
        }

        if (lowestCondition >= threshold)
            return CompleteAdsRepair(adsStatus);

        ScheduleAdsRepairRetry(failureMessage, adsStatus, lowestCondition, threshold);
        return true;
    }

    private void ScheduleAdsRepairRetry(
        string failureMessage,
        AdsStatusSnapshot adsStatus,
        int lowestCondition,
        int threshold)
    {
        var source = GetAdsRepairSource();
        var statusText = GetAdsRepairFailureOrStatusText(adsStatus);
        var diagnostic = BuildAdsRepairFailureDiagnostic(adsStatus, lowestCondition, threshold);

        if (!_plugin.IsAdsAvailable)
        {
            TransitionTo(
                BotState.Error,
                $"{failureMessage} {diagnostic} ADS is not loaded.");
            return;
        }

        if (adsRepairRetryAttemptCount >= AdsRepairMaxRetryAttempts)
        {
            TransitionTo(
                BotState.Error,
                $"{failureMessage} {diagnostic}");
            return;
        }

        adsRepairRetryAttemptCount++;
        adsRepairRetryPending = true;
        adsRepairRetryAt = DateTime.Now + AdsRepairRetryDelay;
        adsRepairRetryReason = failureMessage;
        ClearAdsRepairAttemptTrackingForRetry();

        StateDetail =
            $"{failureMessage} Durability {lowestCondition}%/{threshold}%. " +
            $"Retrying in {AdsRepairRetryDelay.TotalSeconds:F1}s " +
            $"({adsRepairRetryAttemptCount}/{AdsRepairMaxRetryAttempts})...";
        _plugin.AddDebugLog(
            $"{source}[Repair] {failureMessage} Durability still {lowestCondition}% below threshold {threshold}%. " +
            $"Retrying ADS repair after {AdsRepairRetryDelay.TotalSeconds:F1}s " +
            $"({adsRepairRetryAttemptCount}/{AdsRepairMaxRetryAttempts}). ADS: {statusText}");
    }

    private bool CompleteAdsRepair(AdsStatusSnapshot adsStatus)
    {
        var source = GetAdsRepairSource();
        _plugin.AddDebugLog($"{source}[Repair] ADS repair complete; continuing map loop. ADS: {GetAdsRepairStatusText(adsStatus)}");

        if (continueStartAfterAdsRepair)
        {
            ResetAdsRepairHandoffTracking();
            ContinueStartAfterRepair();
            return true;
        }

        ResetAdsRepairHandoffTracking();
        return false;
    }

    private string GetAdsRepairSource()
        => string.IsNullOrWhiteSpace(adsRepairSource) ? "[Repair]" : adsRepairSource;

    private string ResolveAdsRepairMode()
        => _plugin.Configuration.RepairMode switch
        {
            RepairMode.Self => AdsRepairModeSelf,
            RepairMode.NpcNoInnNoTeleport => AdsRepairModeNpcNoTeleportNoInn,
            _ => AdsRepairModeNpcNoInn,
        };

    private static bool IsNpcNoTeleportNoInnNoMenderSkip(string repairMode, AdsStatusSnapshot status)
    {
        if (!string.Equals(repairMode, AdsRepairModeNpcNoTeleportNoInn, StringComparison.Ordinal))
            return false;

        return ContainsNoMenderWithin120Y(status.UtilityLastFailure)
               || ContainsNoMenderWithin120Y(status.UtilityStatus);
    }

    private static bool ContainsNoMenderWithin120Y(string value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains("No repair NPC found within 120y", StringComparison.OrdinalIgnoreCase);

    private static string BuildAdsRepairStoppedFailureMessage(AdsStatusSnapshot status)
        => $"ADS repair stopped before durability reached threshold; ADS {GetAdsRepairFailureOrStatusText(status)}.";

    private string BuildAdsRepairFailureDiagnostic(AdsStatusSnapshot status, int lowestCondition, int threshold)
    {
        var mode = GetAdsRepairModeText(status);
        var statusText = GetAdsRepairFailureOrStatusText(status);
        return
            $"Lowest durability {lowestCondition}%, threshold {threshold}%, ADS mode {mode}, " +
            $"ADS {statusText}, retry count {adsRepairRetryAttemptCount}/{AdsRepairMaxRetryAttempts}, " +
            $"territory {Plugin.ClientState.TerritoryType}.";
    }

    private string GetAdsRepairModeText(AdsStatusSnapshot status)
    {
        if (!string.IsNullOrWhiteSpace(status.UtilityMode))
            return status.UtilityMode;

        if (!string.IsNullOrWhiteSpace(adsRepairRequestedMode))
            return adsRepairRequestedMode;

        return ResolveAdsRepairMode();
    }

    private static string GetAdsRepairFailureOrStatusText(AdsStatusSnapshot status)
    {
        if (!status.StatusReadable)
            return "status unavailable";

        if (!string.IsNullOrWhiteSpace(status.UtilityLastFailure))
            return $"failure: {status.UtilityLastFailure}";

        if (!string.IsNullOrWhiteSpace(status.UtilityStatus))
            return $"status: {status.UtilityStatus}";

        if (!string.IsNullOrWhiteSpace(status.UtilityLastSuccess))
            return $"last success: {status.UtilityLastSuccess}";

        return "utility status unavailable";
    }

    private static string GetAdsRepairStatusText(AdsStatusSnapshot status)
    {
        if (!string.IsNullOrWhiteSpace(status.UtilityLastFailure))
            return status.UtilityLastFailure;

        if (!string.IsNullOrWhiteSpace(status.UtilityLastSuccess))
            return status.UtilityLastSuccess;

        return string.IsNullOrWhiteSpace(status.UtilityStatus)
            ? "No ADS utility status."
            : status.UtilityStatus;
    }

    private void ResetAdsRepairHandoffTracking()
    {
        ClearAdsRepairAttemptTrackingForRetry();
        adsRepairSource = string.Empty;
        continueStartAfterAdsRepair = false;
        ResetAdsRepairRetryTracking();
    }

    private void ClearAdsRepairAttemptTrackingForRetry()
    {
        adsRepairHandoffActive = false;
        adsRepairUtilityObserved = false;
        adsRepairHandoffStarted = DateTime.MinValue;
        adsRepairRequestedMode = string.Empty;
        ResetAdsRepairRecoveryTracking();
    }

    private void ResetAdsRepairRetryTracking()
    {
        adsRepairRetryPending = false;
        adsRepairRetryAt = DateTime.MinValue;
        adsRepairRetryAttemptCount = 0;
        adsRepairRetryReason = string.Empty;
    }

    private void ResetAdsRepairRecoveryTracking()
    {
        adsRepairRecoveryActive = false;
        adsRepairRecoveryTeleportIssued = false;
        adsRepairRecoveryStarted = DateTime.MinValue;
        adsRepairRecoveryTeleportIssuedAt = DateTime.MinValue;
        adsRepairRecoveryStartPosition = Vector3.Zero;
        adsRepairRecoverySawBetweenAreas = false;
        adsRepairRecoveryLastLoadingAt = DateTime.MinValue;
        adsRepairRecoveryStartAttempted = false;
        adsRepairRecoveryTerritoryId = 0;
        adsRepairRecoveryAetheryteId = 0;
        adsRepairRecoveryAetheryteName = string.Empty;
        adsRepairRecoveryMode = string.Empty;
        adsRepairRecoverySource = string.Empty;
        adsRepairRecoveryLowestCondition = 0;
        adsRepairRecoveryThreshold = 0;
        adsRepairRecoveryNextTeleportAttemptAt = DateTime.MinValue;
        adsRepairRecoveryTeleportRetryCount = 0;
        adsRepairRecoveryLastTeleportFailure = string.Empty;
    }

    private bool TryYieldToActiveAdsDutyOwnership(uint currentTerritory)
    {
        if (!_plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver || !_plugin.IsAdsAvailable)
            return false;

        var inDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                     Plugin.Condition[ConditionFlag.BoundByDuty56];
        if (!inDuty || !IsTreasureDungeonTerritory(currentTerritory))
            return false;

        if (adsDutyHandoffActive)
        {
            if (State != BotState.Completed)
            {
                _plugin.NavigationService.StopNavigation();
                autoMoveActive = false;
                TransitionTo(BotState.Completed, "ADS handoff active - waiting for dungeon to finish...");
                return true;
            }

            return false;
        }

        var adsStatus = _plugin.AdsStatusService.Current.StatusReadable
            ? _plugin.AdsStatusService.Current
            : _plugin.AdsStatusService.Refresh(force: true);
        if (!adsStatus.IsOwned)
            return false;

        _plugin.NavigationService.StopNavigation();
        autoMoveActive = false;
        EndPortalRetryWindow();
        QueueDungeonMapFlagClear("[ADS] active ownership guard");
        adsDutyHandoffActive = true;
        adsDutyHandoffStarted = DateTime.Now;
        adsOwnershipObserved = true;
        adsUnreadableStatusLogged = false;
        _plugin.AddDebugLog($"[ADS] Active ADS ownership detected from LootGoblin state {State}; yielding dungeon control ({adsStatus.OwnershipMode}/{adsStatus.ExecutionPhase}).");
        TransitionTo(BotState.Completed, "ADS owns the duty - waiting for completion...");
        return true;
    }

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
        RestoreBossModOutdoorSuppressionIfActive("[ADS] duty handoff");

        if (includeAssistCommands)
        {
            RunDutyEntryCommandsOnce("[ADS] duty handoff");
        }

        adsInsideSentAt = DateTime.Now;
        adsUnreadableStatusLogged = false;
        CommandHelper.SendCommand("/ads inside");
        _plugin.AddDebugLog(logMessage);
    }

    private void UpdateBossModOutdoorSuppression()
    {
        _plugin.RotationPluginIPC.RefreshBossModDangerStatus();
        LogBossModDangerProbeTransition();

        var loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                      Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (loading)
            return;

        if (_plugin.FateSyncService.TryGetJoinedFateId(out var fateId) &&
            IsJoinedFateCombatAutomationFlowActive())
        {
            if (bossModOutdoorSuppressionActive)
                RestoreBossModOutdoorSuppression($"joined FATE {fateId} combat automation", markCombatAutomationEnabled: true);

            bossModOutdoorSuppressionReason = $"joined FATE {fateId} combat automation active";
            return;
        }

        var dangerDetected = _plugin.RotationPluginIPC.BossModDangerDetected;
        var outdoorFlow = IsOutdoorBossModSuppressionFlowActive();
        var shouldSuppress = outdoorFlow && dangerDetected;
        if (shouldSuppress)
        {
            var reason = BuildBossModOutdoorSuppressionReason();
            if (!bossModOutdoorSuppressionActive)
                StartBossModOutdoorSuppression(reason);
            else
                bossModOutdoorSuppressionReason = reason;

            return;
        }

        if (bossModOutdoorSuppressionActive)
        {
            var restoreReason = GetBossModOutdoorSuppressionRestoreReason(dangerDetected, outdoorFlow);
            if (!string.IsNullOrWhiteSpace(restoreReason))
                RestoreBossModOutdoorSuppression(restoreReason, markCombatAutomationEnabled: Plugin.Condition[ConditionFlag.InCombat]);

            return;
        }

        bossModOutdoorSuppressionReason = dangerDetected
            ? "danger detected outside outdoor flow"
            : "off";
    }

    private void LogBossModDangerProbeTransition()
    {
        var bmrActive = _plugin.RotationPluginIPC.BmrHasActiveModule;
        var bmrModule = _plugin.RotationPluginIPC.BmrActiveModuleName ?? string.Empty;
        var vbmForbiddenZones = _plugin.RotationPluginIPC.VbmForbiddenZonesCount;
        if (!bossModDangerProbeLoggedOnce)
        {
            bossModDangerProbeLoggedOnce = true;
            lastLoggedBmrActiveModule = bmrActive;
            lastLoggedBmrActiveModuleName = bmrModule;
            lastLoggedVbmForbiddenZonesCount = vbmForbiddenZones;
            return;
        }

        if (lastLoggedBmrActiveModule == bmrActive
            && string.Equals(lastLoggedBmrActiveModuleName, bmrModule, StringComparison.Ordinal)
            && lastLoggedVbmForbiddenZonesCount == vbmForbiddenZones)
        {
            return;
        }

        lastLoggedBmrActiveModule = bmrActive;
        lastLoggedBmrActiveModuleName = bmrModule;
        lastLoggedVbmForbiddenZonesCount = vbmForbiddenZones;
        var moduleText = string.IsNullOrWhiteSpace(bmrModule) ? "none" : bmrModule;
        _plugin.AddDebugLog(
            $"[BossModDanger] Detection changed: BMR active module={(bmrActive ? "yes" : "no")} ({moduleText}), VBM forbidden zones={vbmForbiddenZones}.");
    }

    private void StartBossModOutdoorSuppression(string reason)
    {
        bossModOutdoorSuppressionActive = true;
        bossModOutdoorSuppressionReason = reason;
        SendCombatAutomationCommand("/bmrai off", $"outdoor suppression start: {reason}");
        SendCombatAutomationCommand("/vbmai off", $"outdoor suppression start: {reason}");
        combatAutomationEnabledState = false;
        _plugin.AddDebugLog($"[BossModDanger] Outdoor suppression started: {reason}. Requested BMR/VBM off.");
    }

    private void RestoreBossModOutdoorSuppressionIfActive(string reason)
    {
        if (!bossModOutdoorSuppressionActive)
            return;

        RestoreBossModOutdoorSuppression(reason, markCombatAutomationEnabled: Plugin.Condition[ConditionFlag.InCombat]);
    }

    private void RestoreBossModOutdoorSuppression(string reason, bool markCombatAutomationEnabled = false)
    {
        SendCombatAutomationCommand("/bmrai on", $"outdoor suppression restore: {reason}");
        SendCombatAutomationCommand("/vbmai on", $"outdoor suppression restore: {reason}");
        bossModOutdoorSuppressionActive = false;
        bossModOutdoorSuppressionReason = $"restored: {reason}";
        if (markCombatAutomationEnabled)
            combatAutomationEnabledState = true;
        _plugin.AddDebugLog($"[BossModDanger] Outdoor suppression restored ({reason}). Requested BMR/VBM on.");
    }

    private void ClearBossModOutdoorSuppressionState(string reason)
    {
        if (bossModOutdoorSuppressionActive)
            _plugin.AddDebugLog($"[BossModDanger] Outdoor suppression cleared without restore for {reason}.");

        bossModOutdoorSuppressionActive = false;
        bossModOutdoorSuppressionReason = "off";
    }

    private string BuildBossModOutdoorSuppressionReason()
        => $"{_plugin.RotationPluginIPC.BossModDangerReason}; state={State}; territory={Plugin.ClientState.TerritoryType}";

    private string GetBossModOutdoorSuppressionRestoreReason(bool dangerDetected, bool outdoorFlow)
    {
        var territory = Plugin.ClientState.TerritoryType;
        if (IsTreasureDungeonTerritory(territory))
            return $"treasure dungeon territory {territory}";

        if (adsDutyHandoffActive)
            return "ADS duty handoff active";

        if (IsDungeonState(State))
            return $"dungeon state {State}";

        return !dangerDetected
            ? "BossMod danger signal cleared"
            : !outdoorFlow
                ? "left outdoor flow"
                : string.Empty;
    }

    private bool IsOutdoorBossModSuppressionFlowActive()
    {
        if (State is BotState.Idle or BotState.Error)
            return false;

        var territory = Plugin.ClientState.TerritoryType;
        if (IsTreasureDungeonTerritory(territory))
            return false;

        return State is BotState.SelectingMap
            or BotState.OpeningMap
            or BotState.DetectingLocation
            or BotState.Teleporting
            or BotState.Mounting
            or BotState.WaitingForParty
            or BotState.Flying
            or BotState.OpeningChest
            or BotState.InCombat
            || IsOverworldMapDutyActive()
            || portalRetryStart != DateTime.MinValue
            || portalApproachPosition.HasValue;
    }

    private static bool IsDungeonState(BotState state)
        => state is BotState.InDungeon
            or BotState.DungeonCombat
            or BotState.DungeonLooting
            or BotState.DungeonProgressing;

    private bool TryKeepCombatAutomationForJoinedFate(string reason)
    {
        if (!_plugin.FateSyncService.TryGetJoinedFateId(out var fateId) ||
            !IsJoinedFateCombatAutomationFlowActive())
        {
            return false;
        }

        EnsureJoinedFateCombatAutomation(fateId, reason);
        return true;
    }

    private bool IsJoinedFateCombatAutomationFlowActive()
    {
        if (State is BotState.Idle or BotState.Error)
            return false;

        if (IsTreasureDungeonTerritory(Plugin.ClientState.TerritoryType))
            return false;

        var activeTreasureState = State is BotState.SelectingMap
            or BotState.OpeningMap
            or BotState.DetectingLocation
            or BotState.Teleporting
            or BotState.Mounting
            or BotState.WaitingForParty
            or BotState.Flying
            or BotState.OpeningChest
            || (State == BotState.Completed && _plugin.Configuration.AutoStartNextMap);

        return activeTreasureState
            || IsOverworldMapDutyActive()
            || portalRetryStart != DateTime.MinValue
            || portalApproachPosition.HasValue
            || CurrentLocation?.TerritoryId == Plugin.ClientState.TerritoryType;
    }

    private void EnsureJoinedFateCombatAutomation(ushort fateId, string reason)
    {
        if (bossModOutdoorSuppressionActive)
            RestoreBossModOutdoorSuppression($"joined FATE {fateId} combat automation for {reason}", markCombatAutomationEnabled: true);

        if (joinedFateCombatAutomationActive &&
            joinedFateCombatAutomationFateId == fateId &&
            combatAutomationEnabledState == true)
        {
            return;
        }

        SendCombatAutomationCommand("/bmrai on", $"joined FATE {fateId} combat automation: {reason}");
        SendCombatAutomationCommand("/vbmai on", $"joined FATE {fateId} combat automation: {reason}");
        combatAutomationEnabledState = true;
        joinedFateCombatAutomationActive = true;
        joinedFateCombatAutomationFateId = fateId;
        _plugin.AddDebugLog($"[FATE] Joined FATE {fateId} combat automation active for {reason}. Requested BMR/VBM on.");
    }

    private void SetCombatAutomationForCombatState(bool inCombat, string reason, bool force = false)
    {
        if (inCombat && bossModOutdoorSuppressionActive)
        {
            if (_plugin.FateSyncService.TryGetJoinedFateId(out var fateId) &&
                IsJoinedFateCombatAutomationFlowActive())
            {
                EnsureJoinedFateCombatAutomation(fateId, $"combat enable for {reason}");
                return;
            }

            if (IsOutdoorBossModSuppressionFlowActive() && _plugin.RotationPluginIPC.BossModDangerDetected)
            {
                _plugin.AddDebugLog($"[BossModDanger] Suppression active; skipped BMR/VBM combat enable for {reason}.");
                return;
            }

            RestoreBossModOutdoorSuppression($"combat enable for {reason}", markCombatAutomationEnabled: true);
            return;
        }

        if (!force && combatAutomationEnabledState == inCombat)
            return;

        SendCombatAutomationCommand(inCombat ? "/bmrai on" : "/bmrai off", reason);
        SendCombatAutomationCommand(inCombat ? "/vbmai on" : "/vbmai off", reason);
        combatAutomationEnabledState = inCombat;
        if (!inCombat)
        {
            joinedFateCombatAutomationActive = false;
            joinedFateCombatAutomationFateId = 0;
        }
        _plugin.AddDebugLog($"[CombatAutomation] BMR/VBM {(inCombat ? "enabled" : "disabled")} for {reason}.");
    }

    private void SendCombatMovementForbidOn()
    {
        if (combatMovementForbidSentThisCombat)
            return;

        SendCombatAutomationCommand("/bmrai forbidmovement on", "combat movement forbid");
        SendCombatAutomationCommand("/vbmai forbidmovement on", "combat movement forbid");
        combatMovementForbidSentThisCombat = true;
        _plugin.AddDebugLog("[CombatAutomation] BMR/VBM forbidmovement enabled for combat start.");
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

    private bool IsObjectTargetable(IGameObject obj, bool logResult = true)
    {
        // Verify object can actually be targeted (not a ghost object)
        // Quick check without blocking delays
        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            if (logResult)
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
                if (logResult)
                    _plugin.AddDebugLog($"[ObjectCheck] '{obj.Name}' targetable");
                return true;
            }
        }
        catch (Exception ex)
        {
            if (logResult)
                _plugin.AddDebugLog($"[ObjectCheck] Exception: {ex.Message}");
        }
        
        if (logResult)
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
        const string notificationChallengeAddonName = "_NotificationChallenge";
        const string notificationAddonName = "_Notification";
        var now = DateTime.Now;
        var treasureHighLowVisible = GameHelpers.IsAddonVisible(addonName);
        var notificationChallengeVisible = GameHelpers.IsAddonVisible(notificationChallengeAddonName);

        if (!treasureHighLowVisible && !notificationChallengeVisible)
        {
            if (treasureHighLowVisibleSince != DateTime.MinValue)
            {
                var sessionDuration = now - treasureHighLowVisibleSince;
                _plugin.AddDebugLog(
                    $"[CardGame] Higher/Lower flow cleared after {sessionDuration.TotalSeconds:F1}s and " +
                    $"{treasureHighLowAttemptCount} callback attempt(s).");
            }

            ResetTreasureHighLowRetryState();
            return false;
        }

        StopMovementForTreasureHighLow(now);

        if (treasureHighLowVisibleSince == DateTime.MinValue)
        {
            treasureHighLowVisibleSince = now;
            treasureHighLowAttemptCount = 0;
            treasureHighLowNextRetryAt = now;
            treasureHighLowLastStatusLogAt = now;
            _plugin.AddDebugLog(
                "[CardGame] Higher/Lower flow detected - checking TreasureHighLow before each callback attempt and reopening via _Notification true 0 1 when _NotificationChallenge is visible.");
        }

        LogTreasureHighLowStillVisible(now, treasureHighLowVisible, notificationChallengeVisible);

        if (_plugin.Configuration.TreasureHighLowMode != TreasureHighLowMode.Skip)
        {
            return TryHandleTreasureHighLowNonSkipMode(
                now,
                treasureHighLowVisible,
                notificationChallengeVisible);
        }

        if (treasureHighLowAttemptCount >= TreasureHighLowCloseAttempts.Length)
        {
            if (!treasureHighLowExhaustedLogged)
            {
                treasureHighLowExhaustedLogged = true;
            LogTreasureHighLowInfo(
                $"[CardGame] Exhausted {TreasureHighLowCloseAttempts.Length} Higher/Lower callback attempts; holding movement and waiting for manual/game resolution.");
            }

            StateDetail = "Waiting for Higher/Lower puzzle after exhausting callback attempts...";
            return true;
        }

        if (now < treasureHighLowNextRetryAt)
        {
            StateDetail = $"Waiting for Higher/Lower puzzle UI to settle (attempt {treasureHighLowAttemptCount})...";
            return true;
        }

        if (!treasureHighLowVisible)
        {
            treasureHighLowNextRetryAt = now.Add(TreasureHighLowReopenRetryInterval);
            LogTreasureHighLowInfo(
                $"[CardGame] TreasureHighLow missing while _NotificationChallenge is visible before attempt {treasureHighLowAttemptCount + 1}; firing _Notification true 0 1.");

            var reopenFired = GameHelpers.TryFireAddonCallbackIfExists(notificationAddonName, true, 0, 1);
            LogTreasureHighLowInfo(
                $"[CardGame] Callback result: _Notification true [0,1] fired={reopenFired}; {BuildTreasureHighLowLogContext()}");
            if (!reopenFired)
            {
                _plugin.AddDebugLog(
                    "[CardGame] Failed to fire _Notification true 0 1; will retry while _NotificationChallenge remains visible.");
            }

            StateDetail = $"Reopening Higher/Lower puzzle (attempt {treasureHighLowAttemptCount + 1})...";
            return true;
        }

        var attempt = TreasureHighLowCloseAttempts[treasureHighLowAttemptCount];
        treasureHighLowAttemptCount++;
        treasureHighLowNextRetryAt = now.Add(TreasureHighLowSettleDelay);
        LogTreasureHighLowInfo(
            $"[CardGame] Attempt {treasureHighLowAttemptCount}/{TreasureHighLowCloseAttempts.Length}: {attempt.Description}");

        var attemptFired = GameHelpers.TryFireAddonCallback(addonName, attempt.UpdateState, attempt.Arg);
        LogTreasureHighLowInfo(
            $"[CardGame] Callback result: {attempt.Description} fired={attemptFired}; {BuildTreasureHighLowLogContext()}");
        if (!attemptFired)
        {
            _plugin.AddDebugLog(
                $"[CardGame] Attempt {treasureHighLowAttemptCount} failed to fire {attempt.Description}; next tick will re-check addon state.");
        }

        StateDetail = $"Trying Higher/Lower callback attempt {treasureHighLowAttemptCount}...";
        return true;
    }

    private void StopMovementForTreasureHighLow(DateTime now)
    {
        if (now - treasureHighLowLastMovementStopAt < TreasureHighLowMovementStopInterval)
            return;

        treasureHighLowLastMovementStopAt = now;
        CommandHelper.SendCommand("/automove off");
        CommandHelper.SendCommand("/vnav stop");
        autoMoveActive = false;

        if (_plugin.NavigationService.State != NavigationState.Idle)
            _plugin.NavigationService.StopNavigation();
    }

    private bool TryHandleTreasureHighLowNonSkipMode(
        DateTime now,
        bool treasureHighLowVisible,
        bool notificationChallengeVisible)
    {
        if (!treasureHighLowVisible)
        {
            if (now >= treasureHighLowNextRetryAt)
            {
                treasureHighLowNextRetryAt = now.Add(TreasureHighLowReopenRetryInterval);
                LogTreasureHighLowInfo(
                    $"[CardGame] Higher/Lower mode {_plugin.Configuration.TreasureHighLowMode} is holding because TreasureHighLow is not visible (NotificationChallengeVisible={notificationChallengeVisible}); skip/reopen callbacks are disabled outside Skip mode. {BuildTreasureHighLowLogContext()}");
            }

            StateDetail = $"Waiting for Higher/Lower puzzle UI ({_plugin.Configuration.TreasureHighLowMode})...";
            return true;
        }

        return TryHandleTreasureHighLowSolverMode(now);
    }

    private bool TryHandleTreasureHighLowSolverMode(DateTime now)
    {
        if (now < treasureHighLowNextRetryAt)
        {
            StateDetail = "Waiting for Higher/Lower solver UI to settle...";
            return true;
        }

        var snapshot = ReadTreasureHighLowSnapshot();
        LogTreasureHighLowSnapshot(snapshot);

        if (_plugin.Configuration.TreasureHighLowMode == TreasureHighLowMode.ObserveOnly)
        {
            if (now >= treasureHighLowNextRetryAt)
            {
                LogTreasureHighLowInfo(
                    $"[CardGame] ObserveOnly holding Higher/Lower puzzle without callbacks. {BuildTreasureHighLowLogContext()}");
            }

            StateDetail = "Observing Higher/Lower puzzle...";
            treasureHighLowNextRetryAt = now.Add(TreasureHighLowSettleDelay);
            return true;
        }

        if (!snapshot.IsReliable)
        {
            LogTreasureHighLowInfo(
                $"[CardGame] SolveExpectedValue snapshot incomplete ({snapshot.ReliabilityReason}); holding/retrying without skip/cashout. {BuildTreasureHighLowLogContext()}");
            StateDetail = "Waiting for reliable Higher/Lower solver snapshot...";
            treasureHighLowNextRetryAt = now.Add(TreasureHighLowSettleDelay);
            return true;
        }

        if (snapshot.Signature == treasureHighLowLastDecisionSignature)
        {
            StateDetail = "Waiting for Higher/Lower UI to change after solver callback...";
            return true;
        }

        var stage = Math.Clamp(snapshot.Stage ?? treasureHighLowObservedStage, 1, 5);
        var decision = TreasureHighLowSolver.Decide(stage, snapshot.Card!.Value);

        if (!TryResolveTreasureHighLowWorldObject(decision.Action, out var target, out var targetReason))
        {
            LogTreasureHighLowInfo(
                $"[CardGame] Solver decision {decision.Action} ({decision.Reason}) held: {targetReason}. {BuildTreasureHighLowLogContext()}");
            treasureHighLowNextRetryAt = now.Add(TreasureHighLowSettleDelay);
            StateDetail = $"Waiting for targetable Higher/Lower world object for {decision.Action}...";
            return true;
        }

        LogTreasureHighLowInfo(
            $"[CardGame] Solver decision: {decision.Action} ({decision.Reason}); interacting world object '{target.Name.TextValue}'.");

        var solverFired = GameHelpers.InteractWithObject(target);
        treasureHighLowAttemptCount++;
        treasureHighLowNextRetryAt = now.Add(TreasureHighLowSettleDelay);
        treasureHighLowLastDecisionSignature = snapshot.Signature;

        if (solverFired && decision.Action is (TreasureHighLowAction.PlayHigher or TreasureHighLowAction.PlayLower))
            treasureHighLowObservedStage = Math.Clamp(stage + 1, 1, 5);

        StateDetail = solverFired
            ? $"Playing Higher/Lower stage {stage}: {decision.Action}..."
            : $"Waiting to retry Higher/Lower world interaction for {decision.Action}...";
        return true;
    }

    private bool TryResolveTreasureHighLowWorldObject(
        TreasureHighLowAction action,
        out IGameObject target,
        out string reason)
    {
        target = null!;
        var targetName = action switch
        {
            TreasureHighLowAction.PlayHigher => "High",
            TreasureHighLowAction.PlayLower => "Low",
            TreasureHighLowAction.CashOut => string.Empty,
            _ => string.Empty,
        };

        if (string.IsNullOrWhiteSpace(targetName))
        {
            reason = "cash-out has no verified world-object interaction path";
            return false;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        var candidates = Plugin.ObjectTable
            .Where(obj => obj != null
                          && obj.IsTargetable
                          && obj.Name.TextValue.Equals(targetName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(obj => player == null ? 0f : Vector3.Distance(player.Position, obj.Position))
            .ToList();

        if (candidates.Count == 0)
        {
            reason = $"targetable '{targetName}' object not found";
            return false;
        }

        target = candidates[0];
        var distance = player == null ? 0f : Vector3.Distance(player.Position, target.Position);
        reason = $"resolved '{targetName}' at {distance:F1}y";
        return true;
    }

    private void LogTreasureHighLowSnapshot(TreasureHighLowSnapshot snapshot)
    {
        if (snapshot.Signature == treasureHighLowLastSnapshotSignature)
            return;

        treasureHighLowLastSnapshotSignature = snapshot.Signature;
        LogTreasureHighLowInfo(
            $"[CardGame] Snapshot: card={(snapshot.Card?.ToString() ?? "?")}, " +
            $"cardCandidates=[{string.Join(",", snapshot.CardCandidates)}], " +
            $"stage={(snapshot.Stage?.ToString() ?? treasureHighLowObservedStage.ToString())} ({snapshot.StageSource}), " +
            $"reliable={snapshot.IsReliable}, reason={snapshot.ReliabilityReason}, {BuildTreasureHighLowLogContext()}, " +
            $"texts=[{FormatTreasureHighLowTexts(snapshot.VisibleTexts)}]");
    }

    private void LogTreasureHighLowInfo(string message)
    {
        _plugin.AddDebugLog(message);
        Plugin.Log.Information(message);
    }

    private unsafe TreasureHighLowSnapshot ReadTreasureHighLowSnapshot()
    {
        var visibleTexts = new List<TreasureHighLowTextEntry>();

        try
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("TreasureHighLow");
            if (addon == null || !addon->IsVisible)
                return TreasureHighLowSnapshot.Unavailable("TreasureHighLow not visible");

            CollectTextFromKnownNodeRanges(addon, visibleTexts);
        }
        catch (Exception ex)
        {
            return TreasureHighLowSnapshot.Unavailable($"{ex.GetType().Name}: {ex.Message}");
        }

        var cardCandidates = visibleTexts
            .Select(entry => entry.Text)
            .SelectMany(ExtractStandaloneCardDigits)
            .Distinct()
            .ToList();

        var parsedStage = ExtractStage(visibleTexts.Select(entry => entry.Text).ToList());
        var stage = parsedStage ?? treasureHighLowObservedStage;
        var stageSource = parsedStage.HasValue ? "parsed text" : "observed local estimate";
        var card = cardCandidates.Count == 1 ? cardCandidates[0] : (int?)null;
        var reliable = card.HasValue && stage is >= 1 and <= 5;
        var reason = reliable
            ? "single visible card digit and bounded stage"
            : $"card candidates={string.Join(",", cardCandidates)} stage={stage} source={stageSource}";

        return new TreasureHighLowSnapshot(card, cardCandidates, stage, stageSource, reliable, reason, visibleTexts);
    }

    private static unsafe void CollectTextFromKnownNodeRanges(AtkUnitBase* unit, List<TreasureHighLowTextEntry> visibleTexts)
    {
        for (var id = 1; id <= 220; id++)
            TryCollectTextNode(unit, (uint)id, visibleTexts);

        for (var id = 50000; id <= 51100; id++)
            TryCollectTextNode(unit, (uint)id, visibleTexts);
    }

    private static unsafe void TryCollectTextNode(AtkUnitBase* unit, uint nodeId, List<TreasureHighLowTextEntry> visibleTexts)
    {
        var node = unit->GetNodeById(nodeId);
        if (node == null ||
            node->Type != NodeType.Text ||
            !node->IsVisible())
        {
            return;
        }

        var textNode = (AtkTextNode*)node;
        var text = textNode->NodeText.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(text) &&
            !visibleTexts.Any(entry => entry.NodeId == nodeId && entry.Text == text))
        {
            visibleTexts.Add(new TreasureHighLowTextEntry(nodeId, text));
        }
    }

    private static IEnumerable<int> ExtractStandaloneCardDigits(string text)
    {
        var parts = text.Split(new[] { ' ', '\t', '\r', '\n', ':', '/', '-', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.Length == 1 && part[0] >= '1' && part[0] <= '9')
                yield return part[0] - '0';
        }
    }

    private static int? ExtractStage(IReadOnlyList<string> visibleTexts)
    {
        foreach (var text in visibleTexts)
        {
            var lower = text.ToLowerInvariant();
            if (!lower.Contains("round") &&
                !lower.Contains("stage") &&
                !lower.Contains("attempt") &&
                !lower.Contains("try") &&
                !lower.Contains("gamble"))
            {
                continue;
            }

            foreach (var value in ExtractStandaloneCardDigits(text))
            {
                if (value is >= 1 and <= 5)
                    return value;
            }
        }

        return null;
    }

    private string BuildTreasureHighLowLogContext()
        => $"territory={Plugin.ClientState.TerritoryType}, botState={State}, mode={_plugin.Configuration.TreasureHighLowMode}, attempt={treasureHighLowAttemptCount}";

    private static string FormatTreasureHighLowTexts(IReadOnlyList<TreasureHighLowTextEntry> visibleTexts)
        => string.Join(" | ", visibleTexts.Take(12).Select(entry => $"{entry.NodeId}:{entry.Text}"));

    private void LogTreasureHighLowStillVisible(
        DateTime now,
        bool treasureHighLowVisible,
        bool notificationChallengeVisible)
    {
        if (treasureHighLowAttemptCount <= 0 ||
            treasureHighLowVisibleSince == DateTime.MinValue ||
            now - treasureHighLowLastStatusLogAt < TreasureHighLowStatusLogInterval)
        {
            return;
        }

        treasureHighLowLastStatusLogAt = now;
        var sessionDuration = now - treasureHighLowVisibleSince;
        _plugin.AddDebugLog(
            $"[CardGame] Higher/Lower flow still active after {treasureHighLowAttemptCount} callback attempt(s) " +
            $"over {sessionDuration.TotalSeconds:F1}s; TreasureHighLowVisible={treasureHighLowVisible}, " +
            $"NotificationChallengeVisible={notificationChallengeVisible}.");
    }

    private void ResetTreasureHighLowRetryState()
    {
        treasureHighLowVisibleSince = DateTime.MinValue;
        treasureHighLowNextRetryAt = DateTime.MinValue;
        treasureHighLowLastStatusLogAt = DateTime.MinValue;
        treasureHighLowLastMovementStopAt = DateTime.MinValue;
        treasureHighLowAttemptCount = 0;
        treasureHighLowExhaustedLogged = false;
        treasureHighLowObservedStage = 1;
        treasureHighLowLastSnapshotSignature = string.Empty;
        treasureHighLowLastDecisionSignature = string.Empty;
    }

    private sealed record TreasureHighLowSnapshot(
        int? Card,
        IReadOnlyList<int> CardCandidates,
        int? Stage,
        string StageSource,
        bool IsReliable,
        string ReliabilityReason,
        IReadOnlyList<TreasureHighLowTextEntry> VisibleTexts)
    {
        public string Signature =>
            $"{Card?.ToString() ?? "?"}:{Stage?.ToString() ?? "?"}:{IsReliable}:{string.Join("|", VisibleTexts.Take(12).Select(entry => $"{entry.NodeId}:{entry.Text}"))}";

        public static TreasureHighLowSnapshot Unavailable(string reason)
            => new(null, Array.Empty<int>(), null, "unavailable", false, reason, Array.Empty<TreasureHighLowTextEntry>());
    }

    private sealed record TreasureHighLowTextEntry(uint NodeId, string Text);

    // ─── Error Handling ───────────────────────────────────────────────────────

    private void HandleError(string message)
    {
        if (TryDeferTeleportCombatError(message))
            return;

        RetryCount++;
        _plugin.AddDebugLog($"[Error #{RetryCount}] {message}");
        WriteDiagnosticSnapshot($"error-observed:{message}");
        ResetAllCameraResetBeforeInteractTracking();

        // BoundByDuty is also used by overworld treasure-map combat. Only known treasure
        // dungeon territories may recover into the dungeon solver.
        bool stillInDuty = Plugin.Condition[ConditionFlag.BoundByDuty] ||
                           Plugin.Condition[ConditionFlag.BoundByDuty56];
        var currentTerritory = Plugin.ClientState.TerritoryType;
        var inTreasureDungeonDuty = stillInDuty && IsTreasureDungeonTerritory(currentTerritory);
        TreasureMapKeyItem? activeMapKeyItem = null;
        var hasActiveMapKeyItem = !stillInDuty && _plugin.InventoryService.TryFindTreasureMapKeyItem(out activeMapKeyItem);

        _plugin.NavigationService.StopNavigation(clearFlag: !stillInDuty && !hasActiveMapKeyItem);

        if (inTreasureDungeonDuty)
        {
            _plugin.AddDebugLog($"[Error #{RetryCount}] Still in known treasure dungeon territory {currentTerritory} - recovering to InDungeon instead of SelectingMap");
            dungeonEntryProcessed = false;
            TransitionTo(BotState.InDungeon, $"Error #{RetryCount} (recovered): {message}");
            return;
        }

        if (stillInDuty)
        {
            LogMapDutyOutsideDungeon($"[Error #{RetryCount}]", currentTerritory);

            if (State == BotState.Completed || portalRetryStart != DateTime.MinValue || portalApproachPosition.HasValue)
            {
                if (portalRetryStart == DateTime.MinValue)
                    portalRetryStart = DateTime.Now;

                TransitionTo(BotState.Completed, $"Error #{RetryCount}: recovering overworld portal search...");
                return;
            }

            TransitionTo(BotState.OpeningChest, $"Error #{RetryCount}: recovering overworld coffer/portal...");
            return;
        }

        if (hasActiveMapKeyItem && activeMapKeyItem != null)
        {
            UpdateActiveKeyItemMap(activeMapKeyItem, $"[Error #{RetryCount}]");
            if (!selectedMapRunCountPendingDecrement)
                ShowActiveKeyItemRecoveryPopupOnce(activeMapKeyItem);
            mapCountChecked = true;
            mapOpeningRetried = false;
            digIssuedThisMap = false;
            digIssuedAt = DateTime.MinValue;

            if (TryResolveActiveKeyItemMapTarget(activeMapKeyItem, out var recoveryLocation, out var recoverySource))
            {
                ConsumeSelectedMapRunCountIfPending($"[Error #{RetryCount}]");
                _plugin.AddDebugLog($"[Error #{RetryCount}] Active key item still has target from {recoverySource}; resuming map run.");
                ResumeActiveKeyItemMapFromTarget(activeMapKeyItem, recoveryLocation, recoverySource, $"[Error #{RetryCount}]");
                return;
            }

            var failed =
                $"Treasure map key item '{activeMapKeyItem.DisplayName}' is active after error '{message}', but no AgentMap flag, TreasureSpot capture, or cached target is available. Manual intervention required.";
            SetWarning(failed);
            TransitionTo(BotState.Error, failed);
            return;
        }
        
        // Not in duty - safe to retry from SelectingMap
        ClearSelectedMapRunCountDecrement($"[Error #{RetryCount}]");
        TransitionTo(BotState.SelectingMap, $"Error #{RetryCount}: {message}");
    }

    private void StopOnRetainerRetrievalError(string message)
    {
        RetryCount++;
        _plugin.AddDebugLog($"[RetainerMap] Fatal error #{RetryCount}: {message}");
        WritePreTerminalSnapshot("retainer-retrieval-error");
        _plugin.NavigationService.StopNavigation();
        _plugin.RetainerMapRetrievalService.Reset();
        TransitionTo(BotState.Error, message);
    }

    // ─── Transition ───────────────────────────────────────────────────────────

    private static bool ShouldResetOpeningChestLifecycleForTransition(BotState newState)
        => newState is BotState.Idle
            or BotState.Error
            or BotState.Repairing
            or BotState.SelectingMap
            or BotState.StartPreflight
            or BotState.OpeningMap
            or BotState.InDungeon
            or BotState.DungeonCombat
            or BotState.DungeonLooting
            or BotState.DungeonProgressing
            or BotState.CyclingAetherytes
            or BotState.CyclingMapLocations
            or BotState.AlexandriteFarming
            or BotState.GatheringMap;

    private void TransitionTo(BotState newState, string detail, [CallerMemberName] string transitionSource = "")
    {
        var prev = State;
        var terminalTransition = newState is BotState.Idle or BotState.Error;
        if (terminalTransition)
            WritePreTerminalSnapshot($"transition:{transitionSource}:{prev}->{newState}");
        else
            ResetDiagnosticTerminalTracking();

        var resetOpeningChestLifecycle = ShouldResetOpeningChestLifecycleForTransition(newState);
        if (newState == BotState.Error)
            ResetAdsRepairHandoffTracking();

        if (newState == BotState.Teleporting && prev != BotState.Teleporting)
            ResetPortaPraetoriaTakeoffNudge("[TransitionTo] new teleport", stopAutomove: true);

        if (prev == BotState.GatheringMap && newState != BotState.GatheringMap && mapGatherStep != MapGatherStep.Idle)
            ResetMapGathering(cancelGatherBuddy: true);

        if (newState is BotState.Idle
            or BotState.Error
            or BotState.Completed
            or BotState.InDungeon
            or BotState.DungeonCombat
            or BotState.DungeonLooting
            or BotState.DungeonProgressing)
        {
            ResetPortaPraetoriaTakeoffNudge($"[TransitionTo] state entered {newState}", stopAutomove: true);
        }

        if (newState == BotState.Teleporting || prev == BotState.Teleporting)
            ResetTeleportLifecycleTracking();

        if (prev == BotState.Flying || newState == BotState.Flying)
            ResetVnavFlyFlagFallbackState();

        if ((prev == BotState.Flying || prev == BotState.OpeningChest) &&
            newState != BotState.Flying &&
            newState != BotState.OpeningChest)
        {
            ResetOverworldRecoveryState();
        }

        if (newState is BotState.Idle or BotState.Error or BotState.Completed or BotState.InDungeon)
            overworldRecoveryRequiresPartyMountWait = false;

        if (newState is BotState.Idle or BotState.Error or BotState.SelectingMap or BotState.OpeningChest or BotState.Completed or BotState.InDungeon)
            joinedFateMapProgressBypassPartyWait = false;

        if (newState is BotState.Idle or BotState.Error or BotState.InDungeon)
            ClearOutdoorMapFlowHold();

        if (newState != BotState.StartPreflight)
            startPreflightReadyAt = DateTime.MinValue;

        State = newState;
        StateDetail = detail;
        lastTransitionSource = transitionSource;
        LootGoblinActionTrace.Record("state-transition", $"{prev}->{newState} source={transitionSource} detail={detail}");
        stateStartTime = DateTime.Now;
        stateActionIssued = false;
        dismountAttemptStart = DateTime.MinValue;
        if (descentInProgress)
        {
            CommandHelper.SendCommand("/automove off");
            GameHelpers.KeyRelease(VirtualKey.W);
            GameHelpers.KeyRelease(VirtualKey.CONTROL);
            GameHelpers.KeyRelease(VirtualKey.SPACE);
        }
        descentInProgress = false;
        lastLandingPartyWaitSignature = string.Empty;
        ResetPartyProximityGateTracking();
        if (prev == BotState.WaitingForParty || newState == BotState.WaitingForParty)
            lastPartyMountWaitLogTime = DateTime.MinValue;
        if (newState == BotState.WaitingForParty)
            CaptureWaitingForPartyExpectedMemberCount();
        else if (prev == BotState.WaitingForParty)
            waitingForPartyExpectedMemberCount = 0;
        ResetTreasureHighLowRetryState();
        if (resetOpeningChestLifecycle)
        {
            openingChestCombatInterrupted = false;
            openingChestRecoveryDigIssued = false;
            openingChestReturningToFlag = false;
            openingChestReturningToLastKnownCoffer = false;
        }

        if (newState is BotState.Idle or BotState.Error or BotState.Completed or BotState.InDungeon)
            ResetUnderwaterXyzDigRetryState();

        if (newState is BotState.Idle or BotState.Error or BotState.InDungeon)
            ResetAllCameraResetBeforeInteractTracking();

        if (prev == BotState.OpeningChest && newState != BotState.OpeningChest)
        {
            ClearOpeningChestJoinedFateHold();
            ResetOpeningChestCofferMountRecovery();
            if (resetOpeningChestLifecycle)
            {
                ResetOpeningChestLifecycleState();
                ResetOpeningChestCofferMemory();
            }
        }

        if (prev == BotState.Completed && newState != BotState.Completed)
        {
            CommandHelper.SendCommand("/automove off");
            ResetPortalApproachTrackingForAreaChange();
            completedSaddlebagRefreshAttempted = false;
        }

        // Stop navigation if it was active
        if (autoMoveActive)
        {
            _plugin.NavigationService.StopNavigation();
            autoMoveActive = false;
        }

        if (newState == BotState.OpeningChest)
            chestDisappearedTime = DateTime.MinValue;

        if (newState == BotState.Completed && prev != BotState.Completed)
            completedSaddlebagRefreshAttempted = false;

        if (newState == BotState.OpeningChest && Plugin.Condition[ConditionFlag.InCombat])
        {
            openingChestCombatInterrupted = true;
            _plugin.AddDebugLog("[OpeningChest] Entered chest recovery while already in combat - will retry after combat");
        }

        if (bossModOutdoorSuppressionActive)
        {
            if (newState == BotState.Error)
                ClearBossModOutdoorSuppressionState($"terminal state {newState}");
            else if (newState == BotState.Idle || IsDungeonState(newState))
                RestoreBossModOutdoorSuppression($"state entered {newState}", markCombatAutomationEnabled: Plugin.Condition[ConditionFlag.InCombat]);
        }

        // Unpause YesAlready when bot reaches terminal states
        if (newState == BotState.Idle || newState == BotState.Error)
        {
            if (newState == BotState.Error || combatAutomationEnabledState != false)
                SetCombatAutomationForCombatState(inCombat: false, $"terminal state {newState}", force: true);
            if (newState == BotState.Error)
                ResetAlexandriteSessionState("[TransitionTo] error");
            ClearCompletedStaleKeyItemSuppression($"terminal state {newState}");
            _plugin.YesAlreadyIPC.Unpause();
            _plugin.AddDebugLog($"[TransitionTo] YesAlready unpaused: {!_plugin.YesAlreadyIPC.IsPaused}");
        }

        if (_plugin.Configuration.EnableStateLogging)
            _plugin.AddDebugLog($"[State] {prev} → {newState} | {detail}");

        if (!terminalTransition)
            WriteDiagnosticSnapshot($"state-transition:{transitionSource}:{prev}->{newState}");
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
                if (TryHoldForCycleMapPartyWait(10.0, "CycleMapLocsFlying", "[Flying]"))
                {
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
                    if (TryHoldForCycleMapPartyWait(10.0, "CycleMapLocsGround", "[Ground]"))
                    {
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
    private const uint MorDhonaTerritoryId = AlexandritePolicy.MorDhonaTerritoryId;
    private static DateTime lastPoeticsLog = DateTime.MinValue; // Rate limiting for poetics logging
    private const uint MysteriousMapItemId = AlexandritePolicy.MysteriousMapItemId; // Mysterious Map
    private static readonly string[] AlexandritePurchaseCleanupAddons =
    [
        "SelectYesno",
        "ShopExchangeCurrency",
        "SelectIconString",
    ];
    
    // Underwater navigation tracking
    private bool wasDiving = false;
    private DateTime lastDivingCheck = DateTime.MinValue;
    private Vector3 underwaterTargetPosition = Vector3.Zero;
    private bool nonThiefDivingIgnoredLogged = false;
    private Vector3 underwaterXyzDigRetryTarget = Vector3.Zero;
    private int underwaterXyzDigRetryAttemptCount;
    private DateTime underwaterXyzDigRetryLastDigAt = DateTime.MinValue;
    private DateTime underwaterXyzDigRetryWaitUntil = DateTime.MinValue;
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

        alexandriteSessionActive = true;
        alexandriteAwaitingMapCompletion = false;
        alexandriteRunsRemaining = runCount;
        alexandriteRunsCompleted = 0;
        alexandriteStep = 0;
        alexandriteActionIssued = false;
        pendingAlexandriteMapTargetItemId = 0;
        ResetAlexandriteLifestreamWait();
        ResetAlexandriteApproachState();
        alexandriteStepTime = DateTime.Now;

        _plugin.AddDebugLog($"[Alexandrite] Starting {runCount} run(s)");
        _plugin.PrintChat($"Starting Alexandrite farming: {runCount} run(s)");
        TransitionTo(BotState.AlexandriteFarming, $"Alexandrite run 1/{runCount}: Starting...");
    }

    private void TickAlexandriteFarming()
    {
        var nav = _plugin.NavigationService;
        var now = DateTime.Now;
        var stepElapsed = (now - alexandriteStepTime).TotalSeconds;

        if (alexandriteRunsRemaining <= 0)
        {
            _plugin.AddDebugLog($"[Alexandrite] All runs complete! ({alexandriteRunsCompleted} total)");
            _plugin.PrintChat($"Alexandrite farming complete! {alexandriteRunsCompleted} runs done.");
            TransitionTo(BotState.Idle, "Alexandrite farming complete!");
            return;
        }

        switch (alexandriteStep)
        {
            case 0: // Return to Revenant's Toll when needed
                if (!alexandriteActionIssued)
                {
                    var buyStartAction = AlexandritePolicy.EvaluateBuyStart(
                        GameHelpers.GetInventoryItemCount(MysteriousMapItemId),
                        Plugin.ClientState.TerritoryType,
                        Plugin.ObjectTable.LocalPlayer?.Position,
                        AurianaPosition);

                    if (buyStartAction == AlexandriteBuyStartAction.UseInventoryMap)
                    {
                        _plugin.AddDebugLog("[Alexandrite] Already have Mysterious Map - skipping purchase");
                        BeginAlexandriteInventoryMapRun("[Alexandrite] existing map");
                        return;
                    }

                    if (buyStartAction == AlexandriteBuyStartAction.SkipLifestream)
                    {
                        _plugin.AddDebugLog("[Alexandrite] Already near Auriana in Mor Dhona - skipping /li rev");
                        alexandriteStep = 1; // Skip teleport
                        alexandriteStepTime = now;
                        alexandriteActionIssued = false;
                        ResetAlexandriteLifestreamWait();
                        ResetAlexandriteApproachState();
                        return;
                    }

                    if (!_plugin.IsLifestreamAvailable)
                    {
                        _plugin.ShowLifestreamMissingToast();
                        HandleError("[Alexandrite] Lifestream is not loaded; cannot return to Revenant's Toll.");
                        return;
                    }

                    if (Plugin.Condition[ConditionFlag.InCombat])
                    {
                        HandleError("[Alexandrite] Cannot return to Revenant's Toll while in combat.");
                        return;
                    }

                    if (nav.State != NavigationState.Idle)
                        nav.StopNavigation();

                    ResetAlexandriteLifestreamWait();
                    if (!CommandHelper.TrySendCommand(AlexandritePolicy.LifestreamRevenantsTollCommand))
                    {
                        HandleError("[Alexandrite] Failed to send /li rev.");
                        return;
                    }

                    alexandriteActionIssued = true;
                    alexandriteStepTime = now;
                    StateDetail = $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Returning to Revenant's Toll...";
                    _plugin.AddDebugLog($"[Alexandrite] Sent {AlexandritePolicy.LifestreamRevenantsTollCommand} before purchase");
                    return;
                }

                if (TryFinishAlexandriteLifestreamReturn(now, stepElapsed))
                {
                    alexandriteStep = 1;
                    alexandriteStepTime = now;
                    alexandriteActionIssued = false;
                    ResetAlexandriteApproachState();
                    _plugin.AddDebugLog("[Alexandrite] Ready in Mor Dhona after /li rev");
                }
                else if (stepElapsed > 60.0)
                {
                    HandleError("[Alexandrite] /li rev return to Mor Dhona timed out");
                }
                return;

            case 1: // Mounted approach to Auriana NPC
                var player = Plugin.ObjectTable.LocalPlayer;
                var hasPlayerPosition = player != null;
                var distToNpc = hasPlayerPosition
                    ? Vector3.Distance(player!.Position, AurianaPosition)
                    : float.MaxValue;
                var mounted = Plugin.Condition[ConditionFlag.Mounted];
                var mounting = Plugin.Condition[ConditionFlag.Mounting71];
                var approachAction = AlexandritePolicy.EvaluateApproach(
                    hasPlayerPosition,
                    distToNpc,
                    mounted,
                    mounting);

                switch (approachAction)
                {
                    case AlexandriteApproachAction.WaitForPlayerPosition:
                        StateDetail = $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Waiting for player position...";
                        return;

                    case AlexandriteApproachAction.Mount:
                        if (now - alexandriteApproachLastMountAttemptAt >= TimeSpan.FromSeconds(3))
                        {
                            alexandriteApproachLastMountAttemptAt = now;
                            nav.MountUp();
                            _plugin.AddDebugLog("[Alexandrite] Mounting before Auriana approach");
                        }
                        StateDetail = $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Mounting for Auriana...";
                        if (stepElapsed > 90.0)
                            HandleError("[Alexandrite] Mount before Auriana approach timed out");
                        return;

                    case AlexandriteApproachAction.WaitForMounting:
                        StateDetail = $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Waiting for mount...";
                        return;

                    case AlexandriteApproachAction.MoveToAuriana:
                        if (!alexandriteActionIssued)
                        {
                            CommandHelper.SendCommand("/vnav clearflag");
                            _plugin.AddDebugLog("[Alexandrite] Cleared navigation flags before purchase");
                            alexandriteActionIssued = true;
                        }

                        nav.MoveToPosition(AurianaPosition);
                        StateDetail = $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Riding to Auriana...";
                        if (stepElapsed > 90.0)
                            HandleError("[Alexandrite] Mounted approach to Auriana timed out");
                        return;

                    case AlexandriteApproachAction.Dismount:
                        nav.StopNavigation();
                        _mountService.Dismount();
                        StateDetail = $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Dismounting at Auriana...";
                        return;

                    case AlexandriteApproachAction.Interact:
                        nav.StopNavigation();
                        alexandriteStep = 2;
                        alexandriteStepTime = now;
                        alexandriteActionIssued = false;
                        ResetAlexandriteApproachState();
                        _plugin.AddDebugLog($"[Alexandrite] Near Auriana ({distToNpc:F1}y) and unmounted");
                        return;
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
                        var accepted = ClickYesIfVisibleWithDiagnostics("Alexandrite.purchase-confirm");
                        var mapCount = GameHelpers.GetInventoryItemCount(MysteriousMapItemId);
                        _plugin.AddDebugLog($"[Alexandrite] SelectYesno accept attempted={accepted}, map count: {mapCount}");
                        
                        if (mapCount > 0)
                        {
                            _plugin.AddDebugLog("[Alexandrite] Mysterious Map purchased successfully");
                            BeginAlexandriteInventoryMapRun("[Alexandrite] purchased map");
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

            case 5: // Hand Mysterious Map to normal flow
                if (GameHelpers.GetInventoryItemCount(MysteriousMapItemId) > 0)
                {
                    BeginAlexandriteInventoryMapRun("[Alexandrite] inventory map");
                    return;
                }

                if (stepElapsed > 10.0)
                    FailAlexandrite("[Alexandrite] Expected Mysterious Map in inventory for normal map handoff, but it was not found.");
                return;

        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private bool TryFinishAlexandriteLifestreamReturn(DateTime now, double stepElapsed)
    {
        var loading = IsAreaTransitionActive() || _plugin.NavigationService.IsTeleporting();
        if (loading)
        {
            alexandriteSawBetweenAreas = true;
            alexandriteLastLoadingAt = now;
            alexandriteLoadingClearedAt = DateTime.MinValue;
        }
        else if (alexandriteLoadingClearedAt == DateTime.MinValue)
        {
            alexandriteLoadingClearedAt = now;
        }

        var settleAnchor = alexandriteLoadingClearedAt == DateTime.MinValue
            ? alexandriteStepTime
            : alexandriteLoadingClearedAt;
        var settleElapsed = now - settleAnchor;
        var player = Plugin.ObjectTable.LocalPlayer;
        var decision = AlexandritePolicy.EvaluateLifestreamArrival(
            loading,
            Plugin.ClientState.TerritoryType,
            settleElapsed,
            TeleportArrivalSettleDelay,
            player != null,
            player?.IsCasting == true,
            Plugin.Condition[ConditionFlag.Casting],
            GameHelpers.IsPlayerAvailable());

        if (decision.CanAdvance)
        {
            var lastLoadingText = alexandriteLastLoadingAt == DateTime.MinValue
                ? "never"
                : $"{(now - alexandriteLastLoadingAt).TotalSeconds:F1}s ago";
            _plugin.AddDebugLog(
                $"[Alexandrite] /li rev settled after {stepElapsed:F1}s; " +
                $"sawBetweenAreas={alexandriteSawBetweenAreas}; lastLoading={lastLoadingText}.");
            ResetAlexandriteLifestreamWait();
            return true;
        }

        StateDetail = decision.WaitReason switch
        {
            AlexandriteLifestreamArrivalWaitReason.Loading =>
                $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Waiting for area load...",
            AlexandriteLifestreamArrivalWaitReason.WrongTerritory =>
                $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Waiting for Mor Dhona...",
            AlexandriteLifestreamArrivalWaitReason.Settling =>
                $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Waiting for arrival settle...",
            AlexandriteLifestreamArrivalWaitReason.NoPlayer =>
                $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Waiting for player...",
            AlexandriteLifestreamArrivalWaitReason.Casting =>
                $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Waiting for cast lock...",
            AlexandriteLifestreamArrivalWaitReason.PlayerUnavailable =>
                $"Alexandrite {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Waiting for player readiness...",
            _ => StateDetail,
        };

        return false;
    }

    private void ResetAlexandriteLifestreamWait()
    {
        alexandriteSawBetweenAreas = false;
        alexandriteLastLoadingAt = DateTime.MinValue;
        alexandriteLoadingClearedAt = DateTime.MinValue;
    }

    private void ResetAlexandriteApproachState()
    {
        alexandriteApproachLastMountAttemptAt = DateTime.MinValue;
    }

    private void BeginAlexandriteInventoryMapRun(string source)
    {
        _plugin.SetBotEnabled(true, "alexandrite:map-handoff");
        pendingAlexandriteMapTargetItemId = MysteriousMapItemId;
        ClearSelectedMapRunCountDecrement(source);
        CloseAlexandritePurchaseAddonsOnce(source);
        _plugin.AddDebugLog($"{source} Handing Mysterious Map to normal map flow. targetItemId={pendingAlexandriteMapTargetItemId}.");

        alexandriteAwaitingMapCompletion = true;
        alexandriteStep = 0;
        alexandriteStepTime = DateTime.Now;
        alexandriteActionIssued = false;
        ResetAlexandriteLifestreamWait();
        ResetAlexandriteApproachState();

        var loading = Plugin.Condition[ConditionFlag.BetweenAreas] ||
                      Plugin.Condition[ConditionFlag.BetweenAreas51];
        if (!loading)
        {
            var cleared = GameHelpers.ClearMapFlag(_plugin.MapFlagService.TryReadFlag);
            _plugin.AddDebugLog($"{source} Preflight cleared map flag before normal handoff (verified={cleared}).");
        }

        var mounted = Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.Mounting71];
        var mountCommandSent = mounted && CommandHelper.TrySendCommand("/mount");
        _plugin.AddDebugLog($"{source} Preflight dismount attempt: mounted={mounted}, sent={mountCommandSent}.");

        ResetConfiguredCombatJobSwitch();
        startPreflightReadyAt = DateTime.Now + StartPreflightDelay;
        TransitionTo(BotState.StartPreflight, "Alexandrite: starting normal Mysterious Map flow...");
    }

    private bool TryResumeAlexandriteAfterCompleted()
    {
        if (!alexandriteSessionActive || !alexandriteAwaitingMapCompletion)
            return false;

        alexandriteAwaitingMapCompletion = false;
        alexandriteRunsCompleted++;
        alexandriteRunsRemaining = Math.Max(0, alexandriteRunsRemaining - 1);
        _plugin.AddDebugLog($"[Alexandrite] Run {alexandriteRunsCompleted} complete. {alexandriteRunsRemaining} remaining.");

        RetryCount = 0;
        CurrentLocation = null;
        SelectedMapItemId = 0;
        pendingAlexandriteMapTargetItemId = 0;
        currentLandingMode = OverworldLandingMode.MountToggle;
        ResetKeyItemMapRecoveryState(clearActiveKey: true);
        ClearSelectedMapRunCountDecrement("[Alexandrite] completed run");

        if (alexandriteRunsRemaining > 0)
        {
            alexandriteStep = 0;
            alexandriteStepTime = DateTime.Now;
            alexandriteActionIssued = false;
            ResetAlexandriteLifestreamWait();
            ResetAlexandriteApproachState();
            TransitionTo(
                BotState.AlexandriteFarming,
                $"Alexandrite run {alexandriteRunsCompleted + 1}/{alexandriteRunsCompleted + alexandriteRunsRemaining}: Starting...");
            return true;
        }

        _plugin.PrintChat($"Alexandrite farming complete! {alexandriteRunsCompleted} runs done.");
        ResetAlexandriteSessionState("[Alexandrite] complete");
        TransitionTo(BotState.Idle, "Alexandrite farming complete!");
        return true;
    }

    private void CloseAlexandritePurchaseAddonsOnce(string source)
    {
        var visibleAddons = GetVisibleAlexandritePurchaseAddons();
        if (visibleAddons.Count == 0)
            return;

        foreach (var addonName in visibleAddons)
            GameHelpers.TryCloseAddonByCallback(addonName);

        _plugin.AddDebugLog($"{source} Closed visible Alexandrite purchase UI once.");
    }

    private List<string> GetVisibleAlexandritePurchaseAddons()
        => AlexandritePurchaseCleanupAddons
            .Where(GameHelpers.IsAddonVisible)
            .ToList();

    private void FailAlexandrite(string message)
    {
        _plugin.AddDebugLog(message);
        Plugin.LogWarning(message);
        SetWarning(message);
        _plugin.PrintChat(message);
        TransitionTo(BotState.Error, message);
    }

    private void ResetAlexandriteSessionState(string source)
    {
        if (alexandriteSessionActive || alexandriteAwaitingMapCompletion || alexandriteRunsRemaining != 0)
            _plugin.AddDebugLog($"{source} Reset Alexandrite session state.");

        alexandriteSessionActive = false;
        alexandriteAwaitingMapCompletion = false;
        alexandriteRunsRemaining = 0;
        alexandriteRunsCompleted = 0;
        alexandriteStep = 0;
        alexandriteActionIssued = false;
        pendingAlexandriteMapTargetItemId = 0;
        ResetAlexandriteLifestreamWait();
        ResetAlexandriteApproachState();
        alexandriteStepTime = DateTime.MinValue;
    }

    public void SetLocation(MapLocation location)
    {
        CurrentLocation = location;
        CacheActiveMapTarget(location, "SetLocation");
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

        var resolvedTargets = ResolveOverworldNavigationTargets();
        string targetSource;
        if (resolvedTargets.LandingTarget != Vector3.Zero)
        {
            flagPosition = resolvedTargets.LandingTarget;
            targetSource = $"resolved landing XYZ ({resolvedTargets.Basis})";
        }
        else
        {
            flagPosition = new Vector3(CurrentLocation.X, player.Position.Y, CurrentLocation.Z);
            targetSource = "safe flag XZ fallback at player Y";
        }

        var targetLogKey =
            $"{Plugin.ClientState.TerritoryType}:{targetSource}:{flagPosition.X:F1}:{flagPosition.Z:F1}";
        if (!string.Equals(openingChestFlagRecoveryTargetLogKey, targetLogKey, StringComparison.Ordinal))
        {
            openingChestFlagRecoveryTargetLogKey = targetLogKey;
            _plugin.AddDebugLog(
                $"[OpeningChest] Flag recovery target uses {targetSource}: {FormatVectorCompact(flagPosition)}.");
        }

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

        if (canUseUnderwaterNavigation && IsThiefUnderwaterLandingMode())
        {
            if (dbEntry != null && destinationIndex > 0)
            {
                var specialNav = _plugin.SpecialNavigationDatabase.FindEntry(destinationIndex);
                if (specialNav != null)
                {
                    var target = ResolveUnderwaterBounceSpecialNavigationTarget(
                        specialNav,
                        dbEntry,
                        destinationIndex,
                        out var basis);
                    return (
                        target,
                        target,
                        basis,
                        destinationText,
                        zoneName,
                        false);
                }
            }

            if (dbEntry != null && dbEntry.HasRealXYZ)
            {
                var realTarget = GetStoredRealTarget(dbEntry);
                return (realTarget, realTarget, "stored RealXYZ", destinationText, zoneName, false);
            }

            return (
                rawGroundTarget,
                rawGroundTarget,
                "fallback flag XYZ",
                destinationText,
                zoneName,
                false);
        }

        if (dbEntry != null && destinationIndex > 0)
        {
            if (dbEntry.HasRealXYZ && !canUseUnderwaterNavigation)
            {
                var realTarget = GetStoredRealTarget(dbEntry);
                return (realTarget, realTarget, "stored RealXYZ", destinationText, zoneName, true);
            }

            if (dbEntry.HasRealXYZ)
            {
                var realTarget = GetStoredRealTarget(dbEntry);
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

    private static bool IsLoadedSameTerritoryMounted(PartyMember member)
        => PartyGateSemantics.IsLoadedSameTerritoryMounted(
            member.IsLoaded,
            member.TerritoryStatus,
            member.IsMounted);

    private static string FormatPartyMemberClassification(PartyMember member)
    {
        var territory = member.TerritoryStatus switch
        {
            PartyTerritoryStatus.Same => "same-territory",
            PartyTerritoryStatus.Different => "out-of-territory",
            _ => "territory-unknown",
        };
        var loaded = member.IsLoaded ? "loaded" : "unloaded";
        var position = member.PositionSource switch
        {
            PartyPositionSource.DirectActor => "position=actor",
            PartyPositionSource.PartyList => "position=party-list",
            _ => "position=unresolved",
        };
        var mount = member.IsMounted ? "mounted" : "not-mounted";
        var name = string.IsNullOrWhiteSpace(member.Name) ? "Unknown" : member.Name;
        return $"{name}[{territory}, {loaded}, {position}, {mount}]";
    }

    private bool TryHoldForCycleMapPartyWait(double maxDistance, string context, string detailPrefix)
    {
        if (!_plugin.Configuration.PartyWaitBeforeDismount)
            return false;

        var result = EvaluatePartyProximityGate(maxDistance, context);
        if (result.CanProceed)
            return false;

        StateDetail = $"{detailPrefix} {BuildOverworldLandingPartyWaitDetail(result, maxDistance)}";
        return true;
    }

    private PartyProximityResult EvaluatePartyProximityGate(double maxDistance, string context)
    {
        var gateKey = BuildPartyProximityGateKey(context);
        var now = DateTime.Now;
        if (partyProximityGateKey != gateKey)
        {
            partyProximityGateKey = gateKey;
            partyProximityGateStartedAt = now;
            partyProximityGateSignature = string.Empty;
            lastPartyProximityHeartbeatAt = DateTime.MinValue;
        }

        var elapsed = now - partyProximityGateStartedAt;
        var timedOut = elapsed >= TimeSpan.FromSeconds(Math.Max(1, _plugin.Configuration.PartyWaitTimeout));
        var party = _plugin.PartyService;
        var snapshotValid = party.UpdatePartyStatus();
        var result = PartyProximityEvaluator.Evaluate(
            snapshotValid,
            party.PartyMembers.Select(member => member.ToProximityMember()).ToList(),
            maxDistance,
            _plugin.Configuration.PartyWaitBeforeDismountUseCountThreshold,
            _plugin.Configuration.PartyWaitBeforeDismountRequiredOthers,
            timedOut);

        LogPartyProximityGate(context, result, maxDistance, elapsed, now);
        return result;
    }

    private string BuildOverworldLandingPartyWaitDetail(PartyProximityResult result, double maxDistance)
    {
        if (!result.SnapshotValid || !result.LocalSnapshotValid)
        {
            var expectedOthers = Math.Max(0, EstimateExpectedPartyMemberCount() - 1);
            return expectedOthers > 0
                ? $"Waiting for valid party snapshot before map content (expecting {expectedOthers} other players)..."
                : "Waiting for valid party snapshot before map content...";
        }

        if (result.TimedOut &&
            !_plugin.Configuration.PartyWaitBeforeDismountUseCountThreshold &&
            result.ResolvedSameTerritoryCount == 0)
        {
            return "Party wait timeout reached; waiting for at least one resolved same-territory party member...";
        }

        var summary =
            $"Waiting for party ({result.NearbyOthers}/{result.TotalOthers} nearby within {maxDistance:F0}y, " +
            $"need {result.RequiredOthers} before map content";

        if (result.SameTerritoryFarCount > 0)
            summary += $", {result.SameTerritoryFarCount} same-territory far";

        if (result.UnresolvedCount > 0)
            summary += $", {result.UnresolvedCount} unresolved";

        if (result.OutOfTerritoryCount > 0)
            summary += $", {result.OutOfTerritoryCount} out-of-territory";

        if (result.GuardedRecoveryUsed)
            summary += ", guarded recovery active";

        return summary + ")...";
    }

    private string BuildPartyProximityGateKey(string context)
    {
        var target = CurrentLocation == null
            ? "none"
            : $"{CurrentLocation.TerritoryId}:{CurrentLocation.X:F1}:{CurrentLocation.Y:F1}:{CurrentLocation.Z:F1}";
        return $"{context}|state={State}|map={SelectedMapItemId}|territory={Plugin.ClientState.TerritoryType}|target={target}|mode={currentLandingMode}";
    }

    private void LogPartyProximityGate(
        string context,
        PartyProximityResult result,
        double maxDistance,
        TimeSpan elapsed,
        DateTime now)
    {
        var memberDetails = result.Members
            .Where(evaluation => !evaluation.Member.IsLocalPlayer)
            .Select(FormatPartyProximityMember)
            .ToList();
        var memberSignature = string.Join(
            "|",
            result.Members
                .Where(evaluation => !evaluation.Member.IsLocalPlayer)
                .Select(evaluation =>
                    $"{evaluation.Member.ContentId}:{evaluation.Member.EntityId}:{evaluation.Status}:{evaluation.XzDistance:F1}:{evaluation.Member.IsLoaded}:{evaluation.Member.PositionSource}"));
        var signature =
            $"{context}:{result.CanProceed}:{result.SnapshotValid}:{result.TimedOut}:{result.GuardedRecoveryUsed}:" +
            $"{result.NearbyOthers}:{result.RequiredOthers}:{result.TotalOthers}:{memberSignature}";
        var heartbeatDue = !result.CanProceed &&
                           now - lastPartyProximityHeartbeatAt >= TimeSpan.FromSeconds(10);
        if (partyProximityGateSignature == signature && !heartbeatDue)
            return;

        partyProximityGateSignature = signature;
        lastPartyProximityHeartbeatAt = now;
        var mode = _plugin.Configuration.PartyWaitBeforeDismountUseCountThreshold
            ? "threshold"
            : "full-party";
        var outcome = result.CanProceed
            ? result.GuardedRecoveryUsed ? "ALLOW guarded-recovery" : "ALLOW"
            : "HOLD";
        var detail = memberDetails.Count == 0 ? "none" : string.Join("; ", memberDetails);
        var message =
            $"[PartyWait][{context}] {outcome}; elapsed={elapsed.TotalSeconds:F0}s; timeout={_plugin.Configuration.PartyWaitTimeout}s; " +
            $"mode={mode}; nearby={result.NearbyOthers}/{result.TotalOthers}; required={result.RequiredOthers}; " +
            $"resolvedSameTerritory={result.ResolvedSameTerritoryCount}; range={maxDistance:F1}y XZ; members={detail}";

        _log.Info(message);
        _plugin.AddDebugLog(message);
    }

    private static string FormatPartyProximityMember(PartyProximityMemberEvaluation evaluation)
    {
        var member = evaluation.Member;
        var territory = member.TerritoryStatus switch
        {
            PartyTerritoryStatus.Same => "same-territory",
            PartyTerritoryStatus.Different => "out-of-territory",
            _ => "territory-unknown",
        };
        var loaded = member.IsLoaded ? "loaded" : "unloaded";
        var position = member.PositionSource switch
        {
            PartyPositionSource.DirectActor => "position=actor",
            PartyPositionSource.PartyList => "position=party-list",
            _ => "position=unresolved",
        };
        var distance = evaluation.XzDistance.HasValue
            ? $", xz={evaluation.XzDistance.Value:F1}y"
            : string.Empty;
        var name = string.IsNullOrWhiteSpace(member.Name) ? "Unknown" : member.Name;
        return $"{name}[{territory}, {loaded}, {position}, status={evaluation.Status}{distance}]";
    }

    private void ResetPartyProximityGateTracking()
    {
        partyProximityGateKey = string.Empty;
        partyProximityGateSignature = string.Empty;
        partyProximityGateStartedAt = DateTime.MinValue;
        lastPartyProximityHeartbeatAt = DateTime.MinValue;
    }

    private void LogLandingPartyWaitOnce(string signature, string message)
    {
        if (lastLandingPartyWaitSignature == signature)
            return;

        lastLandingPartyWaitSignature = signature;
        _log.Info(message);
        _plugin.AddDebugLog(message);
    }

}
