using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons;
using LootGoblin.IPC;
using LootGoblin.Models;
using LootGoblin.Services;
using LootGoblin.Windows;

namespace LootGoblin;

public sealed class Plugin : IDalamudPlugin
{
    private static Plugin? instance;
    internal static Plugin? Instance => instance;

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    private const string CommandName = "/lootgoblin";
    private const string CommandAlias = "/lg";

    public Configuration Configuration { get; init; }
    internal MapGatherCharacterConfig ActiveMapGatherConfig { get; private set; } = new();
    internal ulong ActiveMapGatherContentId { get; private set; }
    internal string ActiveMapGatherCharacterKey { get; private set; } = string.Empty;
    internal DedicatedDiagnosticLog DedicatedDiagnosticLog { get; init; }

    public readonly WindowSystem WindowSystem = new("LootGoblin");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    public AlexandriteMapWindow AlexandriteMapWindow { get; init; }

    // Services
    public InventoryService InventoryService { get; init; }
    public MapDetectionService MapDetectionService { get; init; }
    public NavigationService NavigationService { get; init; }
    public PartyService PartyService { get; init; }
    public StateManager StateManager { get; init; }
    public ChestDetectionService ChestDetectionService { get; init; }
    public MapLocationDatabase MapLocationDatabase { get; init; }
    public TreasureMapLocationService TreasureMapLocationService { get; init; }
    public SpecialNavigationDatabase SpecialNavigationDatabase { get; init; }
    public AetherytePositionDatabase AetherytePositionDatabase { get; init; }
    public AdsStatusService AdsStatusService { get; init; }
    public AdsReflectionIpcService AdsReflectionIpcService { get; init; }
    public RetainerMapRetrievalService RetainerMapRetrievalService { get; init; }
    public FateSyncService FateSyncService { get; init; }
    public FoodService FoodService { get; init; }
    public JobSwitchService JobSwitchService { get; init; }
    public MapAllowanceService MapAllowanceService { get; init; }
    public GatherBuddyRebornService GatherBuddyRebornService { get; init; }
    public LootGoblinMapGatherIpcService MapGatherIpcService { get; init; }

    // IPC
    public MapFlagService MapFlagService { get; init; }
    public VNavIPC VNavIPC { get; init; }
    public YesAlreadyIPC YesAlreadyIPC { get; init; }

    // Mount data
    public string[] MountNames { get; private set; } = Array.Empty<string>();
    public RotationPluginIPC RotationPluginIPC { get; init; }

    // TextAdvance dependency check
    public bool IsTextAdvanceAvailable => IsPluginLoaded("TextAdvance");
    public bool IsLifestreamAvailable => IsPluginLoaded("Lifestream", "Lifestream");
    public bool IsXaDatabaseAvailable => IsPluginLoaded("xadb", "XADatabase") || IsPluginLoaded("xadb", "XA Database");
    public bool IsXaSlaveAvailable => IsPluginLoaded("xaslave", "XASlave") || IsPluginLoaded("xaslave", "XA Slave");
    public bool IsMapPartyAssistAvailable => IsPluginLoaded("MapPartyAssist", "Map Party Assist");

    public List<string> DebugLog { get; } = new();
    private const int MaxDebugLogLines = 200;
    private DateTime lastAdsMissingToastAt = DateTime.MinValue;
    private DateTime lastLifestreamMissingToastAt = DateTime.MinValue;
    private DateTime lastDependencyRefreshAt = DateTime.MinValue;
    private IChatGui.OnHandleableChatMessageDelegate? chatMessageObserver;
    private static readonly TimeSpan DependencyRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PluginAvailabilityCacheTtl = TimeSpan.FromSeconds(2);
    private static readonly string[] LegacyFinishCommandDefaults = { "/li fc", "/rotation cancel", "/bmrai off", "/vbmai off", string.Empty };
    private readonly Dictionary<string, PluginAvailabilityCacheEntry> pluginAvailabilityCache = new(StringComparer.Ordinal);
    private bool ecommonsInitialized;
    private DateTime nextFrameworkHitchLogUtc = DateTime.MinValue;
    private double lastSlowUpdateMs;
    private string lastSlowUpdateSource = "none";
    private IReadOnlyList<uint> cachedRetainerRunnableMapIds = Array.Empty<uint>();
    private string cachedRetainerRunnableMapIdsSignature = string.Empty;
    private bool observedEnabledState;
    private readonly MogtomeEventClient mogtomeEventClient;
    private long mogtomeReminderGeneration;

    private sealed record PluginAvailabilityCacheEntry(bool IsLoaded, DateTime ExpiresAtUtc);

    public bool IsAdsAvailable
    {
        get
        {
            return IsPluginLoaded("ADS", "ADS");
        }
    }

    public Plugin()
    {
        instance = this;
        var loadedConfiguration = PluginInterface.GetPluginConfig() as Configuration;
        var isNewConfiguration = loadedConfiguration == null;
        Configuration = loadedConfiguration ?? new Configuration();
        if (ApplyConfigurationMigrations(Configuration, isNewConfiguration))
            Configuration.Save();
        observedEnabledState = Configuration.Enabled;

        var diagnosticDirectory = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "Diagnostics");
        DedicatedDiagnosticLog = new DedicatedDiagnosticLog(diagnosticDirectory);
        if (Configuration.EnableDedicatedDiagnosticLog && !TryEnableDedicatedDiagnosticLog("plugin load"))
        {
            Configuration.EnableDedicatedDiagnosticLog = false;
            Configuration.Save();
        }

        try
        {
            ECommonsMain.Init(PluginInterface, this);
            ecommonsInitialized = true;
            AddDebugLog("ECommons initialized.");
        }
        catch (Exception ex)
        {
            LogError($"Failed to initialize ECommons: {ex}");
        }

        // Initialize services
        InventoryService = new InventoryService(this, DataManager, Log);
        MapDetectionService = new MapDetectionService(this, GameGui, Log);

        // Initialize IPC
        MapFlagService = new MapFlagService(this, Log);
        VNavIPC = new VNavIPC(this, PluginInterface, Log);
        RotationPluginIPC = new RotationPluginIPC(this, PluginInterface, Log);
        YesAlreadyIPC = new YesAlreadyIPC(this, Log);

        // Initialize navigation (after IPC so VNavIPC is available)
        NavigationService = new NavigationService(this, Condition, ClientState, DataManager, Log);

        // Initialize party service
        PartyService = new PartyService(this, PartyList, ObjectTable, ClientState, Condition, Log);

        // Initialize chest detection
        ChestDetectionService = new ChestDetectionService(this, Log);

        // Initialize map location database
        MapLocationDatabase = new MapLocationDatabase(this, Log);
        MapLocationDatabase.PopulateFromTreasureSpot(DataManager);
        TreasureMapLocationService = new TreasureMapLocationService(this, DataManager, SigScanner, GameInteropProvider, Log);

        // Initialize special navigation database
        SpecialNavigationDatabase = new SpecialNavigationDatabase(this, Log);

        // Initialize aetheryte position database (records player positions at aetherytes)
        AddDebugLog("[Plugin] Initializing AetherytePositionDatabase...");
        AetherytePositionDatabase = new AetherytePositionDatabase(this, Log);
        AddDebugLog($"[Plugin] AetherytePositionDatabase initialized: {(AetherytePositionDatabase != null ? "OK" : "NULL")}");
        if (AetherytePositionDatabase != null)
        {
            AddDebugLog($"[Plugin] AetherytePositionDatabase has {AetherytePositionDatabase.Count} positions loaded");
        }

        AdsStatusService = new AdsStatusService(this, PluginInterface, Log);
        AdsReflectionIpcService = new AdsReflectionIpcService(this, PluginInterface, Log);
        RetainerMapRetrievalService = new RetainerMapRetrievalService(this, Log);
        FateSyncService = new FateSyncService(this);
        FoodService = new FoodService(this);
        JobSwitchService = new JobSwitchService(this, Log);
        MapAllowanceService = new MapAllowanceService(this, Log);
        GatherBuddyRebornService = new GatherBuddyRebornService(this, PluginInterface, Log);
        RefreshActiveMapGatherCharacterBinding();
        mogtomeEventClient = new MogtomeEventClient(Log);

        // Auto-update community data on login
        ClientState.Login += OnLogin;
        if (ClientState.IsLoggedIn)
        {
            QueueCommunityLocationRefresh("plugin load while already logged in");
            QueueMogtomeReminder("plugin load while already logged in");
        }

        // Initialize state machine
        StateManager = new StateManager(this, Framework, Log);
        MapGatherIpcService = new LootGoblinMapGatherIpcService(this, PluginInterface, Log);
        if (DedicatedDiagnosticLog.IsEnabled)
        {
            StateManager.WriteDiagnosticSnapshot("dedicated-log-initial");
            DedicatedDiagnosticLog.Flush();
        }
        SubscribeChatObservers();

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        AlexandriteMapWindow = new AlexandriteMapWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(AlexandriteMapWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Loot Goblin main window. Args: config, start, stop, on, off, status"
        });

        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Loot Goblin main window. Args: config, start, stop, on, off, status"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

        // Load mount names from game data
        LoadMountNames();

        AddDebugLog("Loot Goblin loaded.");
        Log.Information("===Loot Goblin loaded!===");
    }

    public void Dispose()
    {
        SaveActiveMapGatherConfig("plugin unload");
        StateManager?.WriteDiagnosticSnapshot("plugin-unload");
        DedicatedDiagnosticLog.Flush();

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        Framework.Update -= OnFrameworkUpdate;
        UnsubscribeChatObservers();

        StateManager?.Dispose();
        NavigationService?.Dispose();
        PartyService?.Dispose();
        TreasureMapLocationService?.Dispose();
        SpecialNavigationDatabase?.Dispose();
        WindowSystem.RemoveAllWindows();

        ConfigWindow?.Dispose();
        MainWindow?.Dispose();
        AlexandriteMapWindow?.Dispose();

        YesAlreadyIPC.Dispose();
        ChestDetectionService.Dispose();
        RotationPluginIPC.Dispose();
        VNavIPC.Dispose();
        MapFlagService.Dispose();
        MapDetectionService.Dispose();
        InventoryService.Dispose();
        AdsStatusService.Dispose();
        AdsReflectionIpcService.Dispose();
        RetainerMapRetrievalService.Dispose();
        GatherBuddyRebornService.Dispose();
        MapGatherIpcService.Dispose();
        MapAllowanceService.Dispose();
        JobSwitchService.Dispose();
        Interlocked.Increment(ref mogtomeReminderGeneration);
        mogtomeEventClient.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);

        if (ecommonsInitialized)
        {
            try
            {
                ECommonsMain.Dispose();
                ecommonsInitialized = false;
                AddDebugLog("ECommons disposed.");
            }
            catch (Exception ex)
            {
                LogError($"Failed to dispose ECommons: {ex}");
            }
        }

        ClientState.Login -= OnLogin;

        Log.Information("===Loot Goblin unloaded!===");
        DedicatedDiagnosticLog.Disable("plugin unload");
        instance = null;
    }

    private void OnLogin()
    {
        RefreshActiveMapGatherCharacterBinding();
        QueueCommunityLocationRefresh("login");
        QueueMogtomeReminder("login");
    }

    private void QueueMogtomeReminder(string reason)
    {
        var generation = Interlocked.Increment(ref mogtomeReminderGeneration);
        _ = ObserveMogtomeReminderAsync(generation, reason);
    }

    private async Task ObserveMogtomeReminderAsync(long generation, string reason)
    {
        try
        {
            var events = await mogtomeEventClient.GetEventsAsync().ConfigureAwait(false);
            if (!MogtomeEventPolicy.IsActive(events, DateTimeOffset.UtcNow))
                return;

            await Framework.RunOnFrameworkThread(() =>
            {
                if (!ClientState.IsLoggedIn || generation != Volatile.Read(ref mogtomeReminderGeneration))
                    return;

                ToastGui.ShowNormal(
                    "Moogle Treasure Trove is active — check the current rewards for treasure maps and riches to hunt with Loot Goblin.");
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(
                $"[LootGoblin][Mogtome] Reminder check during {reason} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal uint SelectedGatherJobId => ActiveMapGatherConfig.SelectedGatherJobId;

    internal IReadOnlyList<uint> ActiveGatherEnabledMapTypes
        => ActiveMapGatherConfig.GatherEnabledMapTypes is { } mapTypes
            ? mapTypes
            : Array.Empty<uint>();

    internal bool IsMapGatherEnabled(uint itemId)
        => ActiveMapGatherConfig.IsMapGatherEnabled(itemId);

    internal void SetSelectedGatherJobId(uint jobId)
    {
        if (!ClassJobOptions.IsGatherJob(jobId))
            jobId = 0;

        if (ActiveMapGatherConfig.SelectedGatherJobId == jobId)
            return;

        ActiveMapGatherConfig.SelectedGatherJobId = jobId;
        ActiveMapGatherConfig.Normalize();
        SaveActiveMapGatherConfig("gather job changed");
    }

    internal void SetMapGatherEnabled(uint itemId, bool enabled)
    {
        ActiveMapGatherConfig.SetMapGatherEnabled(itemId, enabled);
        SaveActiveMapGatherConfig("gather map selection changed");
    }

    internal void SetActiveMapAllowanceStatus(ulong contentId, MapAllowanceStatus status)
    {
        if (!MapAllowanceSnapshotPolicy.ShouldWrite(ActiveMapGatherContentId, contentId))
            return;

        ActiveMapGatherConfig.SetMapAllowanceSnapshot(status);
        SaveActiveMapGatherConfig("map allowance snapshot");
    }

    private void RefreshActiveMapGatherCharacterBinding()
    {
        if (!TryResolveCurrentMapGatherCharacter(out var contentId, out var characterKey))
        {
            if (!ClientState.IsLoggedIn && ActiveMapGatherContentId != 0)
                ClearActiveMapGatherCharacterBinding("logout");

            return;
        }

        if (ActiveMapGatherContentId == contentId &&
            string.Equals(ActiveMapGatherCharacterKey, characterKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SaveActiveMapGatherConfig("character switch");

        Configuration.MapGatherCharacterConfigs ??= new MapGatherCharacterConfigStore();
        ActiveMapGatherConfig = Configuration.MapGatherCharacterConfigs.BindCharacter(
            characterKey,
            Configuration.SelectedGatherJobId,
            Configuration.GatherEnabledMapTypes,
            out var migratedLegacySettings);
        ActiveMapGatherContentId = contentId;
        ActiveMapGatherCharacterKey = characterKey;
        MapAllowanceService?.OnActiveCharacterChanged(contentId);
        Configuration.Save();

        AddDebugLog(migratedLegacySettings
            ? $"[GatherProfile] Bound character {characterKey}; migrated legacy gather settings."
            : $"[GatherProfile] Bound character {characterKey}.");
    }

    private static bool TryResolveCurrentMapGatherCharacter(out ulong contentId, out string characterKey)
    {
        contentId = 0;
        characterKey = string.Empty;

        if (!ClientState.IsLoggedIn || !PlayerState.IsLoaded)
            return false;

        contentId = PlayerState.ContentId;
        if (contentId == 0)
            return false;

        characterKey = contentId.ToString("X");
        return true;
    }

    private void ClearActiveMapGatherCharacterBinding(string reason)
    {
        SaveActiveMapGatherConfig(reason);
        ActiveMapGatherContentId = 0;
        ActiveMapGatherCharacterKey = string.Empty;
        ActiveMapGatherConfig = new MapGatherCharacterConfig();
        MapAllowanceService?.ClearActiveCharacterState();
        AddDebugLog($"[GatherProfile] Cleared active character binding ({reason}).");
    }

    internal void SaveActiveMapGatherConfig(string reason)
    {
        if (ActiveMapGatherContentId == 0)
            return;

        ActiveMapGatherConfig.Normalize();
        Configuration.Save();
        Log.Debug($"[GatherProfile] Saved character {ActiveMapGatherCharacterKey} ({reason}).");
    }

    private void QueueCommunityLocationRefresh(string reason)
    {
        if (!Configuration.AutoUpdateLocOnLogin)
            return;

        var currentVersion = GetCurrentPluginVersion();
        if (string.Equals(Configuration.LastCommunityLocationsRefreshPluginVersion, currentVersion, StringComparison.Ordinal))
        {
            AddDebugLog($"[MapLocDB] Community data already refreshed for plugin v{currentVersion}");
            return;
        }

        AddDebugLog($"[MapLocDB] Auto-updating community data for plugin v{currentVersion} ({reason})...");
        _ = ObserveCommunityLocationsRefreshAsync(reason);
    }

    private async Task ObserveCommunityLocationsRefreshAsync(string reason)
    {
        try
        {
            await DownloadCommunityLocationsForCurrentVersionAsync();
        }
        catch (Exception ex)
        {
            LogError($"[MapLocDB] Auto-update failed during {reason}: {ex}");
            AddDebugLog($"[MapLocDB] Auto-update failed during {reason}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<bool> DownloadCommunityLocationsForCurrentVersionAsync()
    {
        var currentVersion = GetCurrentPluginVersion();
        var success = await MapLocationDatabase.DownloadCommunityDataAsync();

        if (!success)
        {
            AddDebugLog($"[MapLocDB] Community refresh version remains '{Configuration.LastCommunityLocationsRefreshPluginVersion}'");
            return false;
        }

        Configuration.LastCommunityLocationsRefreshPluginVersion = currentVersion;
        Configuration.Save();
        AddDebugLog($"[MapLocDB] Community refresh marked complete for plugin v{currentVersion}");
        return true;
    }

    private static string GetCurrentPluginVersion()
    {
        return typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        RefreshActiveMapGatherCharacterBinding();
        ObserveEnabledState();

        var updateStopwatch = Stopwatch.StartNew();
        var slowestSection = "none";
        var slowestMs = 0d;
        var now = DateTime.Now;

        void Measure(string section, Action action)
        {
            var sectionStopwatch = Stopwatch.StartNew();
            action();
            sectionStopwatch.Stop();

            var elapsedMs = sectionStopwatch.Elapsed.TotalMilliseconds;
            if (elapsedMs > slowestMs)
            {
                slowestMs = elapsedMs;
                slowestSection = section;
            }
        }

        try
        {
            var loading = IsAreaTransitionActive();
            if (!loading)
            {
                Measure("fate-sync", FateSyncService.Update);
                Measure("food", FoodService.Update);
                Measure("ads-reflection", () => AdsReflectionIpcService.Update());
            }

            Measure("navigation", NavigationService.Update);

            if (!loading && RetainerMapRetrievalService.IsRunning)
            {
                Measure("retainer-map", () =>
                {
                    var enabledMaps = GetCachedRetainerRunnableMapIds();
                    RetainerMapRetrievalService.StartOrTick(enabledMaps);
                });
            }

            if (!loading && now - lastDependencyRefreshAt >= DependencyRefreshInterval)
            {
                lastDependencyRefreshAt = now;
                Measure("dependency-refresh", () => RefreshDependencyStatus(logStatus: false));
            }
        }
        finally
        {
            updateStopwatch.Stop();
            ReportFrameworkHitch(updateStopwatch.Elapsed.TotalMilliseconds, slowestSection, slowestMs);
        }
    }

    private static bool IsAreaTransitionActive()
        => Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51];

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
        LogWarning(
            "[LootGoblin][HITCH] plugin framework update slow elapsedMs={ElapsedMs:0.0}; slowSection={SlowSection}; slowSectionMs={SlowSectionMs:0.0}; transition={Transition}; state={State}; navState={NavState}; enabled={Enabled}.",
            elapsedMs,
            slowestSection,
            slowestMs,
            IsAreaTransitionActive(),
            StateManager.State,
            NavigationService.State,
            Configuration.Enabled);
    }

    public void RefreshDependencyStatus(bool logStatus = false)
    {
        pluginAvailabilityCache.Clear();
        VNavIPC.CheckAvailability(logStatus);
        MapFlagService.CheckAvailability(logStatus);
        TreasureMapLocationService.CheckAvailability(logStatus);
        RotationPluginIPC.CheckAvailability(logStatus);
        GatherBuddyRebornService.CheckAvailability(logStatus);
    }

    private IReadOnlyList<uint> GetCachedRetainerRunnableMapIds()
    {
        var enabledSignature = Configuration.EnabledMapTypes is { } enabledMapTypes
            ? string.Join(",", enabledMapTypes)
            : string.Empty;
        var countSignature = Configuration.MapRunCounts == null
            ? string.Empty
            : string.Join(
                ",",
                Configuration.MapRunCounts
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => $"{kvp.Key}:{kvp.Value}"));
        var signature = $"{enabledSignature}|{countSignature}";
        if (string.Equals(signature, cachedRetainerRunnableMapIdsSignature, StringComparison.Ordinal))
            return cachedRetainerRunnableMapIds;

        cachedRetainerRunnableMapIds = Configuration.GetRunnableMapIds(TreasureMapData.AllMapItemIds);
        cachedRetainerRunnableMapIdsSignature = signature;
        return cachedRetainerRunnableMapIds;
    }

    private void OnCommand(string command, string args)
    {
        var trimmedArgs = args.Trim();
        var arg = trimmedArgs.ToLowerInvariant();

        switch (arg)
        {
            case "config":
            case "settings":
                ConfigWindow.Toggle();
                break;

            case "on":
            case "enable":
                SetBotEnabled(true, "command:on");
                PrintChat("Loot Goblin enabled.");
                break;

            case "start":
                SetBotEnabled(true, "command:start");
                PrintChat(StateManager.Start()
                    ? "Loot Goblin started."
                    : string.IsNullOrWhiteSpace(StateManager.WarningMessage)
                        ? "Loot Goblin not started."
                        : StateManager.WarningMessage);
                break;

            case "stop":
                SetBotEnabled(false, "command:stop");
                StateManager.Stop("command:stop");
                PrintChat("Loot Goblin stopped.");
                break;

            case "off":
            case "disable":
                SetBotEnabled(false, "command:off");
                PrintChat("Loot Goblin disabled.");
                break;

            case "status":
                var status = Configuration.Enabled ? "ENABLED" : "DISABLED";
                PrintChat($"Loot Goblin is {status}.");
                break;

            case "debug":
                Configuration.ShowDebugMapCompletion = !Configuration.ShowDebugMapCompletion;
                Configuration.Save();
                var debugState = Configuration.ShowDebugMapCompletion ? "ON" : "OFF";
                PrintChat($"Diagnostics: {debugState}");
                AddDebugLog($"Diagnostics toggled via command: {debugState}");
                break;

            case "fetchretainer":
                if (RetainerMapRetrievalService.StartManualFetch())
                {
                    PrintChat("Retainer map retrieval started.");
                    AddDebugLog("[RetainerMap] Manual fetch requested.");
                }
                else
                {
                    PrintChat($"Retainer map retrieval not started: {RetainerMapRetrievalService.StatusText}");
                    AddDebugLog($"[RetainerMap] Manual fetch not started: {RetainerMapRetrievalService.StatusText}");
                }
                break;

            default:
                if (arg == "gather")
                {
                    StateManager.StartConfiguredMapGatherCommand();
                    break;
                }

                if (arg.StartsWith("gather ", StringComparison.Ordinal))
                {
                    StateManager.StartMapGatherCommand(trimmedArgs["gather".Length..].Trim());
                    break;
                }

                MainWindow.Toggle();
                break;
        }
    }

    public void PrintChat(string message)
    {
        ChatGui.Print($"[LootGoblin] {message}");
    }

    private void SubscribeChatObservers()
    {
        chatMessageObserver = message => ObserveChatMessage(message.Message.TextValue);
        ChatGui.ChatMessage += chatMessageObserver;
    }

    private void UnsubscribeChatObservers()
    {
        if (chatMessageObserver == null)
            return;

        ChatGui.ChatMessage -= chatMessageObserver;
        chatMessageObserver = null;
    }

    private void ObserveChatMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        StateManager.NotifyChatMessage(text);

        var isPathFailure = IsVnavPathFailure(text);
        var isNavmeshMissing = text.Contains("navmesh", StringComparison.OrdinalIgnoreCase) &&
                               text.Contains("missing", StringComparison.OrdinalIgnoreCase);
        if (!isPathFailure && !isNavmeshMissing)
            return;

        if (NavigationService.NotifyVnavCommandRejectedWhileNavmeshBuilding(text))
            return;

        if (!isPathFailure)
            return;

        StateManager.NotifyVnavPathFailure(text);
    }

    private static bool IsVnavPathFailure(string text)
    {
        return text.Contains("Failed to find path", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("failed to resolve path", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("failed to resolve nav", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("could not find path", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unable to find path", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsPluginLoaded(string internalName, string? nameFragment = null)
    {
        var key = $"{internalName}|{nameFragment ?? string.Empty}";
        var now = DateTime.UtcNow;
        if (pluginAvailabilityCache.TryGetValue(key, out var cached) && now < cached.ExpiresAtUtc)
            return cached.IsLoaded;

        var loaded = false;
        try
        {
            foreach (var p in PluginInterface.InstalledPlugins)
            {
                if (!p.IsLoaded)
                    continue;

                if (string.Equals(p.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
                {
                    loaded = true;
                    break;
                }

                if (!string.IsNullOrWhiteSpace(nameFragment) &&
                    (p.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase) ||
                     p.InternalName.Contains(nameFragment, StringComparison.OrdinalIgnoreCase)))
                {
                    loaded = true;
                    break;
                }
            }
        }
        catch
        {
        }

        pluginAvailabilityCache[key] = new PluginAvailabilityCacheEntry(loaded, now + PluginAvailabilityCacheTtl);
        return loaded;
    }

    public void ShowAdsMissingToast()
    {
        if ((DateTime.Now - lastAdsMissingToastAt).TotalSeconds < 10.0)
            return;

        lastAdsMissingToastAt = DateTime.Now;

        const string message = "ADS is enabled for LootGoblin dungeon phase, but ADS is not installed or loaded. Install ADS or disable it in LootGoblin settings.";
        ToastGui.ShowError(message);
        PrintChat(message);
        AddDebugLog($"[ADS] {message}");
    }

    public void ShowLifestreamMissingToast()
    {
        if ((DateTime.Now - lastLifestreamMissingToastAt).TotalSeconds < 10.0)
            return;

        lastLifestreamMissingToastAt = DateTime.Now;

        const string message = "Lifestream is not installed or loaded. LootGoblin uses Lifestream for /li travel, so install Lifestream before running map travel.";
        ToastGui.ShowError(message);
        PrintChat(message);
        AddDebugLog($"[Lifestream] {message}");
    }

    public void AddDebugLog(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        DebugLog.Add(entry);
        if (DebugLog.Count > MaxDebugLogLines)
            DebugLog.RemoveAt(0);

        if (Configuration.DebugMode)
            Log.Debug(message);

        DedicatedDiagnosticLog.Write("EVENT", message);
    }

    internal static void AddDebugLogStatic(string message)
    {
        try
        {
            instance?.AddDebugLog(message);
        }
        catch
        {
            // Diagnostics must never affect plugin behavior.
        }
    }

    public void SetBotEnabled(bool enabled, string source, bool save = true)
    {
        var previous = Configuration.Enabled;
        if (previous == enabled)
        {
            AddDebugLog($"[Control] Enabled already {enabled}; source={source}.");
            return;
        }

        if (!enabled)
            StateManager?.WritePreTerminalSnapshot($"enabled-disable:{source}");

        Configuration.Enabled = enabled;
        observedEnabledState = enabled;
        if (save)
            Configuration.Save();

        AddDebugLog($"[Control] Enabled {previous} -> {enabled}; source={source}.");
        if (enabled)
        {
            StateManager?.ResetDiagnosticTerminalTracking();
            StateManager?.WriteDiagnosticSnapshot($"enabled-enable:{source}");
        }
        else
            DedicatedDiagnosticLog.Flush();
    }

    public void SetDedicatedDiagnosticLogEnabled(bool enabled)
    {
        if (Configuration.EnableDedicatedDiagnosticLog == enabled && DedicatedDiagnosticLog.IsEnabled == enabled)
            return;

        if (enabled)
        {
            if (!TryEnableDedicatedDiagnosticLog("Advanced settings"))
            {
                Configuration.EnableDedicatedDiagnosticLog = false;
                Configuration.Save();
                return;
            }

            Configuration.EnableDedicatedDiagnosticLog = true;
            Configuration.Save();
            AddDebugLog("[Diagnostics] Dedicated diagnostic log enabled from Advanced settings.");
            StateManager?.ResetDiagnosticTerminalTracking();
            StateManager?.WriteDiagnosticSnapshot("dedicated-log-enabled");
            DedicatedDiagnosticLog.Flush();
            return;
        }

        StateManager?.WritePreTerminalSnapshot("dedicated-log-disabled");
        AddDebugLog("[Diagnostics] Dedicated diagnostic log disabled from Advanced settings.");
        DedicatedDiagnosticLog.Flush();
        Configuration.EnableDedicatedDiagnosticLog = false;
        Configuration.Save();
        DedicatedDiagnosticLog.Disable("disabled from Advanced settings");
    }

    public void WriteDiagnosticSnapshotNow()
    {
        StateManager.WriteDiagnosticSnapshot("manual");
        DedicatedDiagnosticLog.Flush();
    }

    public void OpenDiagnosticLogFolder()
    {
        try
        {
            Directory.CreateDirectory(DedicatedDiagnosticLog.DirectoryPath);
            Process.Start("explorer.exe", DedicatedDiagnosticLog.DirectoryPath);
        }
        catch (Exception ex)
        {
            LogError($"[Diagnostics] Could not open diagnostic log folder: {ex.Message}");
        }
    }

    public static void LogWarning(string messageTemplate, params object[] values)
    {
        Log.Warning(messageTemplate, values);
        instance?.DedicatedDiagnosticLog?.WriteCritical("WARN", RenderDiagnosticMessage(messageTemplate, values));
    }

    public static void LogError(string messageTemplate, params object[] values)
    {
        Log.Error(messageTemplate, values);
        instance?.DedicatedDiagnosticLog?.WriteCritical("ERROR", RenderDiagnosticMessage(messageTemplate, values));
    }

    private bool TryEnableDedicatedDiagnosticLog(string source)
    {
        try
        {
            DedicatedDiagnosticLog.Enable();
            return DedicatedDiagnosticLog.IsEnabled;
        }
        catch (Exception ex)
        {
            LogError($"[Diagnostics] Could not enable dedicated diagnostic log from {source}: {ex.Message}");
            return false;
        }
    }

    private void ObserveEnabledState()
    {
        if (Configuration.Enabled == observedEnabledState)
            return;

        var previous = observedEnabledState;
        observedEnabledState = Configuration.Enabled;
        if (!Configuration.Enabled)
            StateManager.WritePreTerminalSnapshot("enabled-disable:unattributed");

        AddDebugLog($"[Control][WARN] Unattributed Enabled change {previous} -> {Configuration.Enabled}.");
        if (Configuration.Enabled)
        {
            StateManager.ResetDiagnosticTerminalTracking();
            StateManager.WriteDiagnosticSnapshot("enabled-enable:unattributed");
        }
        else
            DedicatedDiagnosticLog.Flush();
    }

    private static string RenderDiagnosticMessage(string messageTemplate, IReadOnlyList<object> values)
    {
        if (values.Count == 0)
            return messageTemplate;

        return $"{messageTemplate} | values=[{string.Join(", ", values.Select(value => value?.ToString() ?? "null"))}]";
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();

    private static bool ApplyConfigurationMigrations(Configuration configuration, bool isNewConfiguration)
    {
        var changed = false;
        if (configuration.Version < 1)
        {
            if (configuration.FinishCommandTriggers != null
                && configuration.FinishCommandTriggers.SequenceEqual(LegacyFinishCommandDefaults, StringComparer.Ordinal))
            {
                configuration.FinishCommandTriggers.Clear();
                configuration.FinishCommandTriggers.AddRange(Configuration.FinishCommandTriggerDefaults);
                changed = true;
            }

            configuration.Version = 1;
            changed = true;
        }

        if (configuration.Version < 2)
        {
            configuration.EnabledMapTypes ??= new List<uint>();
            if (configuration.EnabledMapTypes.Any())
                configuration.UseMapTypeFilter = true;

            configuration.Version = 2;
            changed = true;
        }

        if (configuration.Version < 3)
        {
            configuration.EnabledMapTypes ??= new List<uint>();
            configuration.EnabledMapTypes = configuration.UseMapTypeFilter
                ? configuration.EnabledMapTypes
                    .Where(itemId => itemId != 0)
                    .Distinct()
                    .ToList()
                : new List<uint>();
            configuration.UseMapTypeFilter = true;
            configuration.Version = 3;
            changed = true;
        }

        if (configuration.Version < 4)
        {
            configuration.MapRunCounts ??= new Dictionary<uint, int>();
            foreach (var itemId in configuration.EnabledMapTypes ?? new List<uint>())
            {
                if (itemId != 0 && !configuration.MapRunCounts.ContainsKey(itemId))
                    configuration.MapRunCounts[itemId] = Configuration.MapRunCountMax;
            }

            configuration.NormalizeConfiguredMapRuns();
            configuration.Version = 4;
            changed = true;
        }

        if (configuration.Version < 5)
        {
            configuration.BmrReduceActivationRangeForOutdoorAreas = true;
            configuration.BmrDisableHuntModules = true;
            configuration.Version = 5;
            changed = true;
        }

        if (configuration.Version < 6)
        {
            configuration.TreasureHighLowMode = TreasureHighLowMode.SolveExpectedValue;
            configuration.Version = 6;
            changed = true;
        }

        if (configuration.Version < 7)
        {
            configuration.SelectedCombatJobId = 0;
            configuration.SelectedGatherJobId = 0;
            configuration.GatherEnabledMapTypes ??= new List<uint>();
            configuration.Version = 7;
            changed = true;
        }

        if (configuration.Version < 8)
        {
            configuration.MapGatherCharacterConfigs ??= new MapGatherCharacterConfigStore();
            configuration.Version = 8;
            changed = true;
        }

        if (configuration.Version < 9)
        {
            configuration.MaxMapAllowanceWaitMinutes = 10;
            configuration.Version = 9;
            changed = true;
        }

        configuration.NormalizeConfiguredMapRuns();
        configuration.NormalizeConfiguredJobAndGatherMaps();
        configuration.MaxMapAllowanceWaitMinutes = Math.Clamp(configuration.MaxMapAllowanceWaitMinutes, 0, 1440);
        configuration.MapGatherCharacterConfigs ??= new MapGatherCharacterConfigStore();
        configuration.MapGatherCharacterConfigs.Normalize();

        if (isNewConfiguration || configuration.LandingOrDutyCommandTriggers == null)
        {
            configuration.LandingOrDutyCommandTriggers = Configuration.CreateDefaultLandingOrDutyCommandTriggers();
            changed = true;
        }

        if (isNewConfiguration || configuration.FinishCommandTriggers == null)
        {
            configuration.FinishCommandTriggers = Configuration.CreateDefaultFinishCommandTriggers();
            changed = true;
        }

        var clampedRepairThreshold = Math.Clamp(configuration.RepairThresholdPercent, 0, 100);
        if (configuration.RepairThresholdPercent != clampedRepairThreshold)
        {
            configuration.RepairThresholdPercent = clampedRepairThreshold;
            changed = true;
        }

        if (!Enum.IsDefined(typeof(RepairMode), configuration.RepairMode))
        {
            configuration.RepairMode = RepairMode.NpcNoInn;
            changed = true;
        }

        if (!Enum.IsDefined(typeof(ReturnWhenDoneDestination), configuration.ReturnWhenDoneDestination))
        {
            configuration.ReturnWhenDoneDestination = ReturnWhenDoneDestination.FC;
            changed = true;
        }

        if (!Enum.IsDefined(typeof(TreasureHighLowMode), configuration.TreasureHighLowMode))
        {
            configuration.TreasureHighLowMode = TreasureHighLowMode.SolveExpectedValue;
            changed = true;
        }

        if (!Enum.IsDefined(typeof(RsrTargetHostileType), configuration.RsrTargetHostileType))
        {
            configuration.RsrTargetHostileType = Configuration.DefaultRsrTargetHostileType;
            changed = true;
        }

        return changed;
    }

    private void LoadMountNames()
    {
        try
        {
            var names = new List<string> { "Mount Roulette" };
            var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Mount>();
            if (sheet != null)
            {
                foreach (var row in sheet)
                {
                    var name = row.Singular.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
            }
            names.Sort(1, names.Count - 1, StringComparer.OrdinalIgnoreCase);
            MountNames = names.ToArray();
            Log.Information($"Loaded {MountNames.Length} mount names from game data");
        }
        catch (Exception ex)
        {
            LogError($"Failed to load mount names: {ex.Message}");
            MountNames = new[] { "Mount Roulette", "Company Chocobo" };
        }
    }
}
