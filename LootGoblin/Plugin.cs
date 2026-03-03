using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
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
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    private const string CommandName = "/lootgoblin";
    private const string CommandAlias = "/lg";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("LootGoblin");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    // Services
    public InventoryService InventoryService { get; init; }
    public MapDetectionService MapDetectionService { get; init; }
    public NavigationService NavigationService { get; init; }
    public PartyService PartyService { get; init; }

    // IPC
    public GlobeTrotterIPC GlobeTrotterIPC { get; init; }
    public VNavIPC VNavIPC { get; init; }
    public RotationPluginIPC RotationPluginIPC { get; init; }

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

        // Initialize navigation (after IPC so VNavIPC is available)
        NavigationService = new NavigationService(this, Condition, ClientState, DataManager, Log);

        // Initialize party service
        PartyService = new PartyService(this, PartyList, ObjectTable, ClientState, Condition, Log);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

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

        AddDebugLog("Loot Goblin loaded.");
        Log.Information("===Loot Goblin loaded!===");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        PartyService.Dispose();
        NavigationService.Dispose();
        RotationPluginIPC.Dispose();
        VNavIPC.Dispose();
        GlobeTrotterIPC.Dispose();
        MapDetectionService.Dispose();
        InventoryService.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);

        Log.Information("===Loot Goblin unloaded!===");
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
}
