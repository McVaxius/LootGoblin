using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace LootGoblin.Windows;

public class ConfigWindow : Window, IDisposable
{
    private static readonly Vector4 ColorGrey = new(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 ColorRed = new(1f, 0.3f, 0.3f, 1f);
    
    private readonly Configuration configuration;
    private readonly Plugin plugin;
    private string mountSearch = "";

    public ConfigWindow(Plugin plugin) : base("Loot Goblin Settings###LootGoblinConfig")
    {
        Flags = ImGuiWindowFlags.None;

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
            Plugin.Log.Info($"[Config] Wait for Party changed from {configuration.WaitForParty} to {waitForParty}");
            configuration.WaitForParty = waitForParty;
            configuration.Save();
            Plugin.Log.Info($"[Config] Wait for Party saved as: {configuration.WaitForParty}");
        }
        ImGui.SameLine();
        ImGui.TextColored(ColorGrey, "(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("When enabled, the bot will wait for party members\n" +
                       "to mount up before taking off.\n" +
                       "\n" +
                       "Note: Use the 'Wait for party before dismounting'\n" +
                       "option in the main window to wait for party members\n" +
                       "to reach the destination before dismounting.");
            ImGui.EndTooltip();
        }

        var requireAllMounted = configuration.RequireAllMounted;
        if (ImGui.Checkbox("Require All Mounted", ref requireAllMounted))
        {
            configuration.RequireAllMounted = requireAllMounted;
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

        var useAdsDungeonSolver = configuration.UseAdsInsteadOfLegacyDungeonSolver;
        if (ImGui.Checkbox("Use ADS for dungeon phase", ref useAdsDungeonSolver))
        {
            configuration.UseAdsInsteadOfLegacyDungeonSolver = useAdsDungeonSolver;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("After a portal is accepted and duty entry is confirmed, LootGoblin will send /ads inside and wait for ADS to finish the dungeon instead of running its legacy dungeon solver.");

        if (configuration.UseAdsInsteadOfLegacyDungeonSolver && !plugin.IsAdsAvailable)
        {
            ImGui.TextColored(ColorRed, "ADS is not loaded. Install ADS or disable this setting.");
        }

        var autoDiscard = configuration.EnableAutoDiscard;
        if (ImGui.Checkbox("Auto Discard (/ays discard)", ref autoDiscard))
        {
            configuration.EnableAutoDiscard = autoDiscard;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Runs /ays discard every 30s during a mounted safe idle window.\nDefers while in combat, loading, or cutscene-like states.\nRequires AutoRetainer plugin.");

        var summonChocobo = configuration.SummonChocobo;
        if (ImGui.Checkbox("Summon Chocobo", ref summonChocobo))
        {
            configuration.SummonChocobo = summonChocobo;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Auto-summon chocobo companion using Gysahl Greens when timer is low.\nWill not summon in sanctuaries or duties.");

        if (configuration.SummonChocobo)
        {
            var stances = new[] { "Free Stance", "Defender Stance", "Attacker Stance", "Healer Stance", "Follow" };
            var stanceIdx = Array.IndexOf(stances, configuration.CompanionStance);
            if (stanceIdx < 0) stanceIdx = 0;
            ImGui.SetNextItemWidth(200);
            if (ImGui.Combo("Companion Stance", ref stanceIdx, stances, stances.Length))
            {
                configuration.CompanionStance = stances[stanceIdx];
                configuration.Save();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Command Triggers");
        ImGui.Spacing();

        configuration.LandingOrDutyCommandTriggers ??= new List<string>();
        configuration.FinishCommandTriggers ??= new List<string>();

        DrawCommandTriggerList(
            "Landing / Duty Entry",
            configuration.LandingOrDutyCommandTriggers,
            new[] { "/rotation Auto", "/bmrai on", "/vbmai on", "/echo wheee" });

        ImGui.Spacing();

        DrawCommandTriggerList(
            "Finish",
            configuration.FinishCommandTriggers,
            new[] { "/li fc", "/rotation cancel", "/bmrai off", "/vbmai off" });

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

    private void DrawCommandTriggerList(string label, List<string> commands, string[] defaults)
    {
        EnsureCommandTriggerSlots(commands, defaults);

        ImGui.Text(label);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Defaults##{label}"))
        {
            commands.Clear();
            commands.AddRange(defaults);
            configuration.Save();
        }

        for (var i = 0; i < 4; i++)
        {
            var command = commands[i];
            ImGui.SetNextItemWidth(300);
            if (ImGui.InputText($"##{label}_{i}", ref command, 128))
            {
                commands[i] = command;
                configuration.Save();
            }
        }
    }

    private static void EnsureCommandTriggerSlots(List<string> commands, string[] defaults)
    {
        while (commands.Count < 4)
        {
            commands.Add(commands.Count < defaults.Length ? defaults[commands.Count] : string.Empty);
        }

        while (commands.Count > 4)
        {
            commands.RemoveAt(commands.Count - 1);
        }
    }
}
