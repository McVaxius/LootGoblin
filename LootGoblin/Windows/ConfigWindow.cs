using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using LootGoblin.Services;
using Lumina.Excel.Sheets;

namespace LootGoblin.Windows;

public class ConfigWindow : Window, IDisposable
{
    private const int CommandTriggerSlotCount = 5;
    private static readonly Vector4 ColorGrey = new(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 ColorRed = new(1f, 0.3f, 0.3f, 1f);
    private static readonly string[] LandingOrDutyCommandDefaults = { "/rotation Auto", "/bmrai on", "/vbmai on", "/echo wheee", string.Empty };
    private static readonly string[] FinishCommandDefaults = { "/rotation cancel", "/bmrai off", "/vbmai off", string.Empty, string.Empty };
    
    private readonly Configuration configuration;
    private readonly Plugin plugin;
    private string mountSearch = "";
    private string foodSearch = "";
    private readonly List<(uint Id, string Name)> foodItems = new();
    private bool foodItemsLoaded = false;
    private readonly string[] landingCommandTriggerDrafts = new string[CommandTriggerSlotCount];
    private readonly string[] finishCommandTriggerDrafts = new string[CommandTriggerSlotCount];
    private bool commandTriggerDraftsDirty;
    private bool commandTriggerDraftsInitialized;
    private string commandTriggerStatus = string.Empty;

    public ConfigWindow(Plugin plugin) : base("Loot Goblin Settings###LootGoblinConfig")
    {
        Flags = ImGuiWindowFlags.None;

        Size = new Vector2(350, 520);
        SizeCondition = ImGuiCond.FirstUseEver;

        configuration = plugin.Configuration;
        this.plugin = plugin;
    }

    public void Dispose()
    {
        SaveCommandTriggerDraftsIfDirty("dispose");
    }

    public override void OnClose()
    {
        SaveCommandTriggerDraftsIfDirty("close");
    }

    private void EnsureFoodItemsLoaded()
    {
        if (foodItemsLoaded) return;
        foodItemsLoaded = true;

        try
        {
            var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
            if (itemSheet == null) return;

            foreach (var item in itemSheet)
            {
                if (item.RowId == 0) continue;
                if (item.ItemUICategory.RowId != 46) continue;

                var name = item.Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                foodItems.Add((item.RowId, name));
            }

            Plugin.Log.Information($"[ConfigWindow] Loaded {foodItems.Count} food items from Lumina");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[ConfigWindow] Failed to load food items: {ex.Message}");
        }
    }

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
        if (!commandTriggerDraftsInitialized || ImGui.IsWindowAppearing())
            RefreshCommandTriggerDrafts();

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

        var repairThreshold = Math.Clamp(configuration.RepairThresholdPercent, 0, 100);
        if (ImGui.SliderInt("Repair threshold %", ref repairThreshold, 0, 100))
        {
            configuration.RepairThresholdPercent = repairThreshold;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("0 disables repair. When equipped gear drops below this value, LootGoblin asks ADS to repair before continuing.");

        var repairModes = new[] { "Self", "NPC no inn" };
        var repairModeIndex = configuration.RepairMode == RepairMode.Self ? 0 : 1;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Repair mode", ref repairModeIndex, repairModes, repairModes.Length))
        {
            configuration.RepairMode = repairModeIndex == 0 ? RepairMode.Self : RepairMode.NpcNoInn;
            configuration.Save();
        }

        var returnWhenDone = configuration.ReturnWhenDoneEnabled;
        if (ImGui.Checkbox("Return when done", ref returnWhenDone))
        {
            configuration.ReturnWhenDoneEnabled = returnWhenDone;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Runs the selected Lifestream return only after no enabled inventory, saddlebag, or retainer maps remain.");

        var returnDestinations = new[] { "FC", "Personal", "Inn" };
        var returnDestinationIndex = configuration.ReturnWhenDoneDestination switch
        {
            ReturnWhenDoneDestination.Personal => 1,
            ReturnWhenDoneDestination.Inn => 2,
            _ => 0,
        };
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Return destination", ref returnDestinationIndex, returnDestinations, returnDestinations.Length))
        {
            configuration.ReturnWhenDoneDestination = returnDestinationIndex switch
            {
                1 => ReturnWhenDoneDestination.Personal,
                2 => ReturnWhenDoneDestination.Inn,
                _ => ReturnWhenDoneDestination.FC,
            };
            configuration.Save();
        }

        var retainerMapRetrieval = configuration.EnableRetainerMapRetrieval;
        if (ImGui.Checkbox("Fetch maps from retainers", ref retainerMapRetrieval))
        {
            configuration.EnableRetainerMapRetrieval = retainerMapRetrieval;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When no enabled map is in inventory or loaded saddlebags, LootGoblin checks XA Database for retainer-owned maps and tries to withdraw one at a retainer bell.");

        var autoDiscard = configuration.EnableAutoDiscard;
        if (ImGui.Checkbox("Auto Discard (/ays discard)", ref autoDiscard))
        {
            configuration.EnableAutoDiscard = autoDiscard;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Runs /ays discard every 30s during a mounted safe idle window.\nDefers while in combat, loading, or cutscene-like states.\nRequires AutoRetainer plugin.");

        var autoSyncFate = configuration.AutoSyncFate;
        if (ImGui.Checkbox("Auto Sync FATE", ref autoSyncFate))
        {
            configuration.AutoSyncFate = autoSyncFate;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Runs /levelsync on after joining a FATE.\nDefers while mounted or riding pillion.");
        ImGui.SameLine();
        if (ImGui.SmallButton("DISABLE PANDORA's BOX"))
        {
            CommandHelper.TrySendCommand("/xldisableplugin Pandora's Box");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Disable Pandora's Box through Dalamud.");

        DrawFoodSection();
        ImGui.Spacing();

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

        DrawCommandTriggerList(
            "Landing / Duty Entry",
            landingCommandTriggerDrafts,
            LandingOrDutyCommandDefaults);

        ImGui.Spacing();

        DrawCommandTriggerList(
            "Finish",
            finishCommandTriggerDrafts,
            FinishCommandDefaults);

        if (!string.IsNullOrWhiteSpace(commandTriggerStatus))
            ImGui.TextDisabled(commandTriggerStatus);

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

        var treasureHighLowModes = new[] { "Skip", "Solve EV", "Observe only" };
        var treasureHighLowModeIndex = configuration.TreasureHighLowMode switch
        {
            TreasureHighLowMode.SolveExpectedValue => 1,
            TreasureHighLowMode.ObserveOnly => 2,
            _ => 0,
        };
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Higher/Lower", ref treasureHighLowModeIndex, treasureHighLowModes, treasureHighLowModes.Length))
        {
            configuration.TreasureHighLowMode = treasureHighLowModeIndex switch
            {
                1 => TreasureHighLowMode.SolveExpectedValue,
                2 => TreasureHighLowMode.ObserveOnly,
                _ => TreasureHighLowMode.Skip,
            };
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Skip keeps current behavior. Observe logs readable state only. Solve EV clicks only after it reads a reliable card/stage; otherwise it falls back to skip.");


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

    private void DrawFoodSection()
    {
        EnsureFoodItemsLoaded();
        BackfillLegacyFoodSelection();

        ImGui.Text("Food");

        var foodId = configuration.FeedMeItemId;
        var foodName = configuration.FeedMeItem;
        if (DrawItemSearchDropdown("Food", ref foodSearch, foodItems, ref foodId, ref foodName))
        {
            configuration.FeedMeItemId = foodId;
            configuration.FeedMeItem = foodName;
            plugin.FoodService.InvalidateFoodCache();
            configuration.Save();
        }

        if (configuration.FeedMeItemId > 0)
        {
            var qualityLabel = configuration.FeedMeUseHighQuality ? "HQ" : "NQ";
            ImGui.Text($"  Selected: {configuration.FeedMeItem} [{qualityLabel}] ({configuration.FeedMeItemId})");

            var useHighQuality = configuration.FeedMeUseHighQuality;
            if (ImGui.Checkbox("Use HQ food", ref useHighQuality))
            {
                configuration.FeedMeUseHighQuality = useHighQuality;
                plugin.FoodService.InvalidateFoodCache();
                configuration.Save();
            }

            if (ImGui.SmallButton("Clear Food"))
            {
                configuration.FeedMeItemId = 0;
                configuration.FeedMeItem = "";
                configuration.FeedMeUseHighQuality = false;
                foodSearch = "";
                plugin.FoodService.InvalidateFoodCache();
                configuration.Save();
            }
        }
        else
        {
            ImGui.TextDisabled("  No food selected.");
        }

        var feedMeSearch = configuration.FeedMeSearch;
        if (ImGui.Checkbox("Search for Food if Depleted", ref feedMeSearch))
        {
            configuration.FeedMeSearch = feedMeSearch;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("If selected food runs out, search inventory for a fallback food from the FrenRider priority list.");
    }

    private void BackfillLegacyFoodSelection()
    {
        if (configuration.FeedMeItemId > 0)
        {
            if (!string.IsNullOrWhiteSpace(configuration.FeedMeItem)) return;

            var selected = foodItems.FirstOrDefault(item => item.Id == (uint)configuration.FeedMeItemId);
            if (selected.Id == 0)
            {
                var itemName = GameHelpers.LookupItemName((uint)configuration.FeedMeItemId);
                if (string.IsNullOrWhiteSpace(itemName)) return;

                selected = ((uint)configuration.FeedMeItemId, itemName);
            }

            configuration.FeedMeItem = selected.Name;
            plugin.FoodService.InvalidateFoodCache();
            configuration.Save();
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration.FeedMeItem)) return;

        var match = foodItems.FirstOrDefault(item =>
            item.Name.Equals(configuration.FeedMeItem.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match.Id == 0) return;

        configuration.FeedMeItemId = (int)match.Id;
        configuration.FeedMeItem = match.Name;
        plugin.FoodService.InvalidateFoodCache();
        configuration.Save();
    }

    private static bool DrawItemSearchDropdown(
        string label,
        ref string search,
        List<(uint Id, string Name)> items,
        ref int selectedId,
        ref string selectedName)
    {
        var changed = false;
        var displayText = selectedId > 0 ? $"{selectedName} ({selectedId})" : $"Select {label}...";

        ImGui.SetNextItemWidth(300);
        if (ImGui.BeginCombo($"##{label}Select", displayText))
        {
            ImGui.SetNextItemWidth(280);
            ImGui.InputText($"Search##{label}", ref search, 128);
            ImGui.Separator();

            const int maxResults = 20;
            var shown = 0;

            if (!string.IsNullOrWhiteSpace(search) && search.Length >= 2)
            {
                var searchTerm = search.Trim();
                var searchLower = searchTerm.ToLowerInvariant();
                var isNumeric = uint.TryParse(searchTerm, out _);

                for (var i = 0; i < items.Count && shown < maxResults; i++)
                {
                    var item = items[i];
                    var match = isNumeric
                        ? item.Id.ToString().Contains(searchTerm, StringComparison.Ordinal)
                        : item.Name.ToLowerInvariant().Contains(searchLower);

                    if (!match) continue;
                    shown++;

                    var isSelected = (int)item.Id == selectedId;
                    if (ImGui.Selectable($"{item.Name} ({item.Id})##{label}{i}", isSelected))
                    {
                        selectedId = (int)item.Id;
                        selectedName = item.Name;
                        changed = true;
                    }
                }

                if (shown == 0)
                    ImGui.TextDisabled("No results.");
            }
            else
            {
                ImGui.TextDisabled("Type at least 2 characters to search.");
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private void DrawCommandTriggerList(string label, string[] drafts, string[] defaults)
    {
        ImGui.Text(label);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Defaults##{label}"))
        {
            CopyCommandTriggerValues(defaults, drafts);
            commandTriggerDraftsDirty = true;
            SaveCommandTriggerDraftsIfDirty($"{label} defaults");
        }

        for (var i = 0; i < CommandTriggerSlotCount; i++)
        {
            ImGui.SetNextItemWidth(300);
            var command = drafts[i];
            var inputLabel = $"##{label}_{i}";
            if (ImGui.InputText(inputLabel, ref command, 128, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                drafts[i] = command;
                commandTriggerDraftsDirty = true;
                SaveCommandTriggerDraftsIfDirty($"{label} slot {i + 1} enter");
                continue;
            }

            if (!string.Equals(drafts[i], command, StringComparison.Ordinal))
            {
                drafts[i] = command;
                commandTriggerDraftsDirty = true;
            }

            if (commandTriggerDraftsDirty && ImGui.IsItemDeactivatedAfterEdit())
                SaveCommandTriggerDraftsIfDirty($"{label} slot {i + 1} edit");
        }
    }

    private void RefreshCommandTriggerDrafts()
    {
        configuration.LandingOrDutyCommandTriggers ??= new List<string>();
        configuration.FinishCommandTriggers ??= new List<string>();

        var changed = EnsureCommandTriggerSlots(configuration.LandingOrDutyCommandTriggers, LandingOrDutyCommandDefaults);
        changed |= EnsureCommandTriggerSlots(configuration.FinishCommandTriggers, FinishCommandDefaults);
        if (changed)
        {
            configuration.Save();
            commandTriggerStatus = "Command triggers normalized.";
            Plugin.Log.Information("[ConfigWindow] Command triggers normalized to 5 slots.");
        }

        CopyCommandTriggerValues(configuration.LandingOrDutyCommandTriggers, landingCommandTriggerDrafts);
        CopyCommandTriggerValues(configuration.FinishCommandTriggers, finishCommandTriggerDrafts);
        commandTriggerDraftsDirty = false;
        commandTriggerDraftsInitialized = true;
    }

    private void SaveCommandTriggerDraftsIfDirty(string reason)
    {
        if (!commandTriggerDraftsDirty)
            return;

        configuration.LandingOrDutyCommandTriggers ??= new List<string>();
        configuration.FinishCommandTriggers ??= new List<string>();

        ReplaceCommandTriggerValues(configuration.LandingOrDutyCommandTriggers, landingCommandTriggerDrafts);
        ReplaceCommandTriggerValues(configuration.FinishCommandTriggers, finishCommandTriggerDrafts);
        configuration.Save();
        commandTriggerDraftsDirty = false;
        commandTriggerStatus = "Command triggers saved.";
        Plugin.Log.Information($"[ConfigWindow] Command triggers saved ({reason}).");
    }

    private static void CopyCommandTriggerValues(IReadOnlyList<string> source, string[] destination)
    {
        for (var i = 0; i < CommandTriggerSlotCount; i++)
            destination[i] = i < source.Count ? source[i] ?? string.Empty : string.Empty;
    }

    private static void ReplaceCommandTriggerValues(List<string> destination, IReadOnlyList<string> source)
    {
        destination.Clear();
        for (var i = 0; i < CommandTriggerSlotCount; i++)
            destination.Add(source[i] ?? string.Empty);
    }

    private static bool EnsureCommandTriggerSlots(List<string> commands, string[] defaults)
    {
        var changed = false;
        while (commands.Count < CommandTriggerSlotCount)
        {
            commands.Add(commands.Count < defaults.Length ? defaults[commands.Count] : string.Empty);
            changed = true;
        }

        while (commands.Count > CommandTriggerSlotCount)
        {
            commands.RemoveAt(commands.Count - 1);
            changed = true;
        }

        return changed;
    }
}
