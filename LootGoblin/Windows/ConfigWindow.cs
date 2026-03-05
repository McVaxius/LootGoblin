using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace LootGoblin.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;
    private readonly Plugin plugin;
    private string mountSearch = "";

    public ConfigWindow(Plugin plugin) : base("Loot Goblin Settings###LootGoblinConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(350, 520);
        SizeCondition = ImGuiCond.FirstUseEver;

        configuration = plugin.Configuration;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
        ImGui.Text("Loot Goblin Settings");
        ImGui.Separator();
        ImGui.Spacing();

        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Bot Enabled", ref enabled))
        {
            configuration.Enabled = enabled;
            configuration.Save();
        }

        var showMain = configuration.ShowMainWindow;
        if (ImGui.Checkbox("Show Main Window on Login", ref showMain))
        {
            configuration.ShowMainWindow = showMain;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("UI Settings");
        ImGui.Spacing();

        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable Config Window", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }

        var debug = configuration.DebugMode;
        if (ImGui.Checkbox("Debug Mode", ref debug))
        {
            configuration.DebugMode = debug;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Navigation");
        ImGui.Spacing();

        var autoTeleport = configuration.AutoTeleport;
        if (ImGui.Checkbox("Auto Teleport", ref autoTeleport))
        {
            configuration.AutoTeleport = autoTeleport;
            configuration.Save();
        }

        var requireVNav = configuration.RequireVNav;
        if (ImGui.Checkbox("Require vnavmesh", ref requireVNav))
        {
            configuration.RequireVNav = requireVNav;
            configuration.Save();
        }

        var navTimeout = configuration.NavigationTimeout;
        if (ImGui.SliderFloat("Nav Timeout (s)", ref navTimeout, 30f, 600f, "%.0f"))
        {
            configuration.NavigationTimeout = navTimeout;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Party Coordination");
        ImGui.Spacing();

        var waitForParty = configuration.WaitForParty;
        if (ImGui.Checkbox("Wait for Party", ref waitForParty))
        {
            configuration.WaitForParty = waitForParty;
            configuration.Save();
        }

        var requireAllMounted = configuration.RequireAllMounted;
        if (ImGui.Checkbox("Require All Mounted", ref requireAllMounted))
        {
            configuration.RequireAllMounted = requireAllMounted;
            configuration.Save();
        }

        var allowPillion = configuration.AllowPillionRiders;
        if (ImGui.Checkbox("Allow Pillion Riders", ref allowPillion))
        {
            configuration.AllowPillionRiders = allowPillion;
            configuration.Save();
        }

        var partyTimeout = configuration.PartyWaitTimeout;
        if (ImGui.SliderInt("Party Wait Timeout (s)", ref partyTimeout, 30, 300))
        {
            configuration.PartyWaitTimeout = partyTimeout;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Bot Automation");
        ImGui.Spacing();

        var autoNext = configuration.AutoStartNextMap;
        if (ImGui.Checkbox("Auto-Start Next Map", ref autoNext))
        {
            configuration.AutoStartNextMap = autoNext;
            configuration.Save();
        }

        var stateLogging = configuration.EnableStateLogging;
        if (ImGui.Checkbox("Enable State Logging", ref stateLogging))
        {
            configuration.EnableStateLogging = stateLogging;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Chest Interaction");
        ImGui.Spacing();

        var autoLoot = configuration.AutoLootChest;
        if (ImGui.Checkbox("Auto Loot Chest", ref autoLoot))
        {
            configuration.AutoLootChest = autoLoot;
            configuration.Save();
        }

        var chestRange = configuration.ChestInteractionRange;
        if (ImGui.SliderFloat("Interaction Range (y)", ref chestRange, 1f, 15f))
        {
            configuration.ChestInteractionRange = chestRange;
            configuration.Save();
        }

        var chestTimeout = configuration.ChestOpenTimeout;
        if (ImGui.SliderInt("Chest Open Timeout (s)", ref chestTimeout, 5, 30))
        {
            configuration.ChestOpenTimeout = chestTimeout;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Mount Settings");
        ImGui.Spacing();

        // Mount Name (searchable dropdown from game data)
        ImGui.Text("Mount Selection");
        ImGui.SameLine();
        ImGui.TextDisabled("(Used for manual mounting)");
        
        var mountNames = plugin.MountNames;
        var currentMount = configuration.SelectedMount;
        ImGui.SetNextItemWidth(300);
        if (ImGui.BeginCombo("##MountSelect", string.IsNullOrEmpty(currentMount) ? "(none)" : currentMount))
        {
            // Search field - fixed at top
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##MountSearch", ref mountSearch, 64);
            ImGui.Separator();
            
            // Scrollable list area
            ImGui.BeginChild("##MountList", new Vector2(0, 200), false);
            for (var i = 0; i < mountNames.Length; i++)
            {
                if (!string.IsNullOrEmpty(mountSearch) &&
                    !mountNames[i].Contains(mountSearch, StringComparison.OrdinalIgnoreCase))
                    continue;

                var isSelected = mountNames[i] == currentMount;
                if (ImGui.Selectable(mountNames[i], isSelected))
                {
                    configuration.SelectedMount = mountNames[i];
                    configuration.Save();
                    mountSearch = "";
                }
                if (isSelected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndChild();
            ImGui.EndCombo();
        }
    }
}
