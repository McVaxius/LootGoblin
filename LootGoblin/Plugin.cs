using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using LootGoblin.IPC;
using LootGoblin.Models;
using LootGoblin.Services;
using LootGoblin.Windows;

namespace LootGoblin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
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

    public readonly WindowSystem WindowSystem = new("LootGoblin");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    public AlexandriteMapWindow AlexandriteMapWindow { get; init; }
    public AutoDutyWarningWindow AutoDutyWarningWindow { get; init; }

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
    public AutoDutyDetectionService AutoDutyDetectionService { get; init; }
    public AdsStatusService AdsStatusService { get; init; }
    public RetainerMapRetrievalService RetainerMapRetrievalService { get; init; }
    public FateSyncService FateSyncService { get; init; }

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

    public List<string> DebugLog { get; } = new();
    private const int MaxDebugLogLines = 200;
    private DateTime lastAdsMissingToastAt = DateTime.MinValue;
    private DateTime lastLifestreamMissingToastAt = DateTime.MinValue;
    private DateTime lastDependencyRefreshAt = DateTime.MinValue;
    private IChatGui.OnHandleableChatMessageDelegate? chatMessageObserver;
    private static readonly TimeSpan DependencyRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly string[] LegacyFinishCommandDefaults = { "/li fc", "/rotation cancel", "/bmrai off", "/vbmai off", string.Empty };
    private static readonly string[] CurrentFinishCommandDefaults = { "/rotation cancel", "/bmrai off", "/vbmai off", string.Empty, string.Empty };
    private bool ecommonsInitialized;
    private bool ecommonsCallbackHookInstalled;

    public bool IsAdsAvailable
    {
        get
        {
            return IsPluginLoaded("ADS", "ADS");
        }
    }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (ApplyConfigurationMigrations(Configuration))
            Configuration.Save();

        try
        {
            ECommonsMain.Init(PluginInterface, this);
            ecommonsInitialized = true;
            AddDebugLog("ECommons initialized.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to initialize ECommons: {ex}");
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
        RetainerMapRetrievalService = new RetainerMapRetrievalService(this, Log);
        FateSyncService = new FateSyncService(this);

        // Auto-update community data on login
        ClientState.Login += OnLogin;

        // Initialize state machine
        StateManager = new StateManager(this, Framework, Log);
        SubscribeChatObservers();

        // Initialize AutoDuty warning system
        AutoDutyWarningWindow = new AutoDutyWarningWindow(this, ChatGui, Log);
        AutoDutyDetectionService = new AutoDutyDetectionService(this, ChatGui, Framework, Log, AutoDutyWarningWindow);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        AlexandriteMapWindow = new AlexandriteMapWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(AlexandriteMapWindow);
        WindowSystem.AddWindow(AutoDutyWarningWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Loot Goblin main window. Args: config, on, off, status"
        });

        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Loot Goblin main window. Args: config, on, off, status"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

        // Load mount names from game data
        LoadMountNames();

        // Initialize ECommons callback hook for addon interactions
        try
        {
            Callback.InstallHook();
            ecommonsCallbackHookInstalled = true;
            AddDebugLog("ECommons callback hook installed.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to install ECommons callback hook: {ex}");
        }

        AddDebugLog("Loot Goblin loaded.");
        Log.Information("===Loot Goblin loaded!===");
    }

    public void Dispose()
    {
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
        AutoDutyDetectionService?.Dispose();
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
        RetainerMapRetrievalService.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);

        if (ecommonsCallbackHookInstalled)
        {
            try
            {
                Callback.UninstallHook();
                ecommonsCallbackHookInstalled = false;
                AddDebugLog("ECommons callback hook uninstalled.");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to uninstall ECommons callback hook: {ex}");
            }
        }

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
                Log.Error($"Failed to dispose ECommons: {ex}");
            }
        }

        ClientState.Login -= OnLogin;

        Log.Information("===Loot Goblin unloaded!===");
    }

    private void OnLogin()
    {
        if (Configuration.AutoUpdateLocOnLogin)
        {
            AddDebugLog("[MapLocDB] Auto-updating community data on login...");
            _ = MapLocationDatabase.DownloadCommunityDataAsync();
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.Now;

        FateSyncService.Update();

        if (RetainerMapRetrievalService.IsRunning)
        {
            var enabledMaps = Configuration.EnabledMapTypes.Count > 0
                ? Configuration.EnabledMapTypes
                : TreasureMapData.KnownMaps.Keys.ToList();
            RetainerMapRetrievalService.StartOrTick(enabledMaps);
        }

        if (now - lastDependencyRefreshAt < DependencyRefreshInterval)
            return;

        lastDependencyRefreshAt = now;
        RefreshDependencyStatus(logStatus: false);
    }

    public void RefreshDependencyStatus(bool logStatus = false)
    {
        VNavIPC.CheckAvailability(logStatus);
        MapFlagService.CheckAvailability(logStatus);
        TreasureMapLocationService.CheckAvailability(logStatus);
        RotationPluginIPC.CheckAvailability(logStatus);
    }

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim().ToLowerInvariant();

        switch (arg)
        {
            case "config":
            case "settings":
                ConfigWindow.Toggle();
                break;

            case "on":
            case "enable":
                Configuration.Enabled = true;
                Configuration.Save();
                PrintChat("Loot Goblin enabled.");
                AddDebugLog("Bot enabled via command.");
                break;

            case "off":
            case "disable":
                Configuration.Enabled = false;
                Configuration.Save();
                PrintChat("Loot Goblin disabled.");
                AddDebugLog("Bot disabled via command.");
                break;

            case "status":
                var status = Configuration.Enabled ? "ENABLED" : "DISABLED";
                PrintChat($"Loot Goblin is {status}.");
                break;

            case "debug":
                Configuration.ShowDebugMapCompletion = !Configuration.ShowDebugMapCompletion;
                Configuration.Save();
                var debugState = Configuration.ShowDebugMapCompletion ? "ON" : "OFF";
                PrintChat($"Map Completion debug controls: {debugState}");
                AddDebugLog($"Debug map completion controls toggled: {debugState}");
                break;

            case "testautoduty":
                PrintChat("Testing AutoDuty detection...");
                var isDetected = AutoDutyDetectionService.IsAutoDutyDetected();
                PrintChat($"AutoDuty detected: {isDetected}");
                
                if (isDetected)
                {
                    PrintChat("AutoDuty detected - showing warning window");
                    AutoDutyDetectionService.ForceShowWarning();
                }
                else
                {
                    PrintChat("AutoDuty not detected - cannot show warning window");
                }
                break;

            case "resetautoduty":
                PrintChat("Resetting AutoDuty detection state");
                AutoDutyDetectionService.ResetWarning();
                PrintChat("AutoDuty detection state reset");
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

        if (!text.Contains("Failed to find path", StringComparison.OrdinalIgnoreCase))
            return;

        StateManager.NotifyVnavPathFailure(text);
    }

    private bool IsPluginLoaded(string internalName, string? nameFragment = null)
    {
        try
        {
            foreach (var p in PluginInterface.InstalledPlugins)
            {
                if (!p.IsLoaded)
                    continue;

                if (string.Equals(p.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!string.IsNullOrWhiteSpace(nameFragment) &&
                    (p.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase) ||
                     p.InternalName.Contains(nameFragment, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
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
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();

    private static bool ApplyConfigurationMigrations(Configuration configuration)
    {
        var changed = false;
        if (configuration.Version < 1)
        {
            if (configuration.FinishCommandTriggers != null
                && configuration.FinishCommandTriggers.SequenceEqual(LegacyFinishCommandDefaults, StringComparer.Ordinal))
            {
                configuration.FinishCommandTriggers.Clear();
                configuration.FinishCommandTriggers.AddRange(CurrentFinishCommandDefaults);
                changed = true;
            }

            configuration.Version = 1;
            changed = true;
        }

        if (configuration.LandingOrDutyCommandTriggers == null)
        {
            configuration.LandingOrDutyCommandTriggers = new List<string>();
            changed = true;
        }

        if (configuration.FinishCommandTriggers == null)
        {
            configuration.FinishCommandTriggers = new List<string>(CurrentFinishCommandDefaults);
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
            Log.Error($"Failed to load mount names: {ex.Message}");
            MountNames = new[] { "Mount Roulette", "Company Chocobo" };
        }
    }
}
