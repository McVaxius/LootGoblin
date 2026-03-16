using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using LootGoblin.IPC;
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
    public SpecialNavigationDatabase SpecialNavigationDatabase { get; init; }
    public AetherytePositionDatabase AetherytePositionDatabase { get; init; }
    public AutoDutyDetectionService AutoDutyDetectionService { get; init; }

    // IPC
    public GlobeTrotterIPC GlobeTrotterIPC { get; init; }
    public VNavIPC VNavIPC { get; init; }
    public YesAlreadyIPC YesAlreadyIPC { get; init; }

    // Mount data
    public string[] MountNames { get; private set; } = Array.Empty<string>();
    public RotationPluginIPC RotationPluginIPC { get; init; }

    // TextAdvance dependency check
    public bool IsTextAdvanceAvailable
    {
        get
        {
            try
            {
                foreach (var p in PluginInterface.InstalledPlugins)
                {
                    if (string.Equals(p.InternalName, "TextAdvance", StringComparison.OrdinalIgnoreCase) && p.IsLoaded)
                        return true;
                }
            }
            catch { }
            return false;
        }
    }

    public List<string> DebugLog { get; } = new();
    private const int MaxDebugLogLines = 200;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Initialize services
        InventoryService = new InventoryService(this, DataManager, Log);
        MapDetectionService = new MapDetectionService(this, GameGui, Log);

        // Initialize IPC
        GlobeTrotterIPC = new GlobeTrotterIPC(this, PluginInterface, Log);
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

        // Auto-update community data on login
        ClientState.Login += OnLogin;

        // Initialize state machine
        StateManager = new StateManager(this, Framework, Log);

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

        // Load mount names from game data
        LoadMountNames();

        // Initialize ECommons callback hook for addon interactions
        try
        {
            Callback.InstallHook();
            AddDebugLog("ECommons callback hook installed.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to install ECommons callback hook: {ex.Message}");
        }

        AddDebugLog("Loot Goblin loaded.");
        Log.Information("===Loot Goblin loaded!===");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        StateManager?.Dispose();
        NavigationService?.Dispose();
        PartyService?.Dispose();
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
        GlobeTrotterIPC.Dispose();
        MapDetectionService.Dispose();
        InventoryService.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);

        // Dispose ECommons callback hook
        try
        {
            Callback.UninstallHook();
            AddDebugLog("ECommons callback hook uninstalled.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to uninstall ECommons callback hook: {ex.Message}");
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

            default:
                MainWindow.Toggle();
                break;
        }
    }

    public void PrintChat(string message)
    {
        ChatGui.Print($"[LootGoblin] {message}");
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
