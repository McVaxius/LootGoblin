using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using LootGoblin.Models;
using LootGoblin.Services;
using Lumina.Excel.Sheets;

namespace LootGoblin.Windows;

public class ConfigWindow : Window, IDisposable
{
    private static readonly Vector4 ColorGrey = new(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 ColorRed = new(1f, 0.3f, 0.3f, 1f);
    private static readonly Vector4 ColorGreen = new(0.3f, 1f, 0.3f, 1f);
    private static readonly Vector4 ColorYellow = new(1f, 1f, 0.3f, 1f);
    
    private readonly Configuration configuration;
    private readonly Plugin plugin;
    private string mountSearch = "";
    private string foodSearch = "";
    private readonly List<(uint Id, string Name)> foodItems = new();
    private bool foodItemsLoaded = false;
    private readonly List<string> landingCommandTriggerDrafts = new();
    private readonly List<string> finishCommandTriggerDrafts = new();
    private bool commandTriggerDraftsDirty;
    private bool commandTriggerDraftsInitialized;
    private string commandTriggerStatus = string.Empty;

    public ConfigWindow(Plugin plugin) : base("Loot Goblin Settings###LootGoblinConfig")
    {
        Flags = ImGuiWindowFlags.None;

        Size = new Vector2(560, 560);
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
            Plugin.LogError($"[ConfigWindow] Failed to load food items: {ex.Message}");
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

        if (ImGui.BeginTabBar("##LootGoblinSettingsTabs"))
        {
            if (ImGui.BeginTabItem("Run"))
            {
                DrawRunTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Maps"))
            {
                DrawMapsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Travel"))
            {
                DrawTravelTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Party"))
            {
                DrawPartyTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Dungeon/Loot"))
            {
                DrawDungeonLootTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Integrations"))
            {
                DrawIntegrationsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Interface"))
            {
                DrawInterfaceTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Advanced"))
            {
                DrawAdvancedTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawRunTab()
    {
        DrawJobCombo(
            "Combat job",
            configuration.SelectedCombatJobId,
            ClassJobOptions.CombatJobs,
            "Current job",
            value => configuration.SelectedCombatJobId = value,
            "Blank uses the player's current job at start.");

        DrawJobCombo(
            "Gather job (current character)",
            plugin.SelectedGatherJobId,
            ClassJobOptions.GatherJobs,
            "Disabled",
            value => plugin.SetSelectedGatherJobId(value),
            "Blank disables map gathering.",
            saveAfterSet: false);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

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
        ImGui.SetNextItemWidth(180);
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

        var repairThreshold = Math.Clamp(configuration.RepairThresholdPercent, 0, 100);
        if (ImGui.SliderInt("Repair threshold %", ref repairThreshold, 0, 100))
        {
            configuration.RepairThresholdPercent = repairThreshold;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("0 disables repair. When equipped gear drops below this value, LootGoblin asks ADS to repair before continuing.");

        var repairModes = new[] { "Self", "NPC no inn", "NPC No Inn + No TP" };
        var repairModeIndex = configuration.RepairMode switch
        {
            RepairMode.Self => 0,
            RepairMode.NpcNoInnNoTeleport => 2,
            _ => 1,
        };
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("Repair mode", ref repairModeIndex, repairModes, repairModes.Length))
        {
            configuration.RepairMode = repairModeIndex switch
            {
                0 => RepairMode.Self,
                2 => RepairMode.NpcNoInnNoTeleport,
                _ => RepairMode.NpcNoInn,
            };
            configuration.Save();
        }
    }

    private void DrawJobCombo(
        string label,
        uint selectedJobId,
        IReadOnlyList<ClassJobOption> options,
        string blankLabel,
        Action<uint> setter,
        string tooltip,
        bool saveAfterSet = true)
    {
        var currentIndex = 0;
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].Id == selectedJobId)
            {
                currentIndex = i + 1;
                break;
            }
        }

        var comboLabels = new string[options.Count + 1];
        comboLabels[0] = blankLabel;
        for (var i = 0; i < options.Count; i++)
            comboLabels[i + 1] = options[i].Name;

        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo(label, ref currentIndex, comboLabels, comboLabels.Length))
        {
            setter(currentIndex == 0 ? 0 : options[currentIndex - 1].Id);
            if (saveAfterSet)
                configuration.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private void DrawMapsTab()
    {
        DrawConfigCheckbox("Fetch maps from retainers", configuration.EnableRetainerMapRetrieval, value => configuration.EnableRetainerMapRetrieval = value,
            "When no enabled map is in inventory, LootGoblin checks XA Database for retainer-owned maps and tries to withdraw one at a retainer bell.");
        DrawConfigCheckbox("Fetch maps from saddlebags", configuration.EnableSaddlebagMapRetrieval, value => configuration.EnableSaddlebagMapRetrieval = value,
            "When no enabled map is in inventory, LootGoblin can open /saddlebag and move one enabled map into inventory. Disable to ignore saddlebags entirely.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawConfigCheckbox("Auto-update locations on login", configuration.AutoUpdateLocOnLogin, value => configuration.AutoUpdateLocOnLogin = value);

        var db = plugin.MapLocationDatabase;
        if (db.IsDownloading)
        {
            ImGui.TextColored(ColorYellow, "Downloading...");
        }
        else
        {
            if (ImGui.Button("Download Updated Locs"))
                _ = plugin.DownloadCommunityLocationsForCurrentVersionAsync();
            if (!string.IsNullOrEmpty(db.LastDownloadResult))
            {
                ImGui.SameLine();
                var dlColor = db.LastDownloadResult.StartsWith("OK") ? ColorGreen :
                              db.LastDownloadResult.StartsWith("Error") ? ColorRed : ColorGrey;
                ImGui.TextColored(dlColor, db.LastDownloadResult);
            }
        }
    }

    private void DrawTravelTab()
    {
        DrawConfigCheckbox("Auto Teleport", configuration.AutoTeleport, value => configuration.AutoTeleport = value);
        DrawConfigCheckbox("Require vnavmesh", configuration.RequireVNav, value => configuration.RequireVNav = value);
        DrawConfigCheckbox("Do not use Tamamizu aetheryte", configuration.AvoidTamamizuAetheryte, value => configuration.AvoidTamamizuAetheryte = value,
            "Skips Tamamizu when choosing the map teleport destination.");

        var navTimeout = configuration.NavigationTimeout;
        if (ImGui.SliderFloat("Nav Timeout (s)", ref navTimeout, 30f, 600f, "%.0f"))
        {
            configuration.NavigationTimeout = navTimeout;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawMountSelection();
    }

    private void DrawPartyTab()
    {
        var waitForParty = configuration.WaitForParty;
        if (ImGui.Checkbox("Wait for Party", ref waitForParty))
        {
            Plugin.Log.Info($"[Config] Wait for Party changed from {configuration.WaitForParty} to {waitForParty}");
            configuration.WaitForParty = waitForParty;
            configuration.Save();
            Plugin.Log.Info($"[Config] Wait for Party saved as: {configuration.WaitForParty}");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, the bot waits for party members before takeoff, remount recovery, and map travel handoffs.");

        DrawConfigCheckbox("Wait for Party for thief maps / underwater", configuration.WaitForPartyForThiefMapsUnderwater, value => configuration.WaitForPartyForThiefMapsUnderwater = value,
            "Overrides the general Wait for Party setting for thief-map underwater travel, remount recovery, and descent/dig waits.");
        DrawConfigCheckbox("Require All Mounted", configuration.RequireAllMounted, value => configuration.RequireAllMounted = value,
            "When count threshold is off, takeoff and remount waits require every other party member mounted. Turn off to continue when any other same-zone party member is mounted.");

        var partyTimeout = configuration.PartyWaitTimeout;
        if (ImGui.SliderInt("Party Wait Timeout (s)", ref partyTimeout, 30, 300))
        {
            configuration.PartyWaitTimeout = partyTimeout;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Applies to all party waits. In count-threshold mode, timeout does not lower the configured player count. " +
                "In full-party proximity waits, timeout can allow guarded recovery when unresolved or out-of-territory members are the only blockers.");
        }

        var teleportDelay = Math.Clamp(configuration.PartyTeleportDelaySeconds, 0, 300);
        ImGui.SetNextItemWidth(80f);
        if (ImGui.InputInt("Time to wait before teleporting (s)##PartyTeleportDelaySeconds", ref teleportDelay))
        {
            configuration.PartyTeleportDelaySeconds = Math.Clamp(teleportDelay, 0, 300);
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Extra delay before sending the map teleport command after the map target is resolved.");

        if (ImGui.Button("OPEN ADS LOOT OPTIONS"))
            OpenAdsLootOptions();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawConfigCheckbox("Wait for party before dismounting", configuration.PartyWaitBeforeDismount, value => configuration.PartyWaitBeforeDismount = value,
            "Wait at the destination until party members are within 10 yalms before dismounting.");
        if (configuration.WaitForParty || configuration.PartyWaitBeforeDismount || configuration.WaitForPartyForThiefMapsUnderwater)
        {
            DrawConfigCheckbox("Specify number of party to wait for", configuration.PartyWaitBeforeDismountUseCountThreshold, value => configuration.PartyWaitBeforeDismountUseCountThreshold = value,
                "Use a count of other party members instead of waiting for the entire party roster. Applies to takeoff, remount, landing, underwater, and next-map waits.");
            if (configuration.PartyWaitBeforeDismountUseCountThreshold)
            {
                var requiredOthers = Math.Clamp(configuration.PartyWaitBeforeDismountRequiredOthers, 1, 7);
                ImGui.SetNextItemWidth(80f);
                if (ImGui.InputInt("Players to wait for##PartyWaitBeforeDismountRequiredOthers", ref requiredOthers))
                {
                    configuration.PartyWaitBeforeDismountRequiredOthers = Math.Clamp(requiredOthers, 1, 7);
                    configuration.Save();
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Number of other party members required for every party wait. Local player is not counted.");
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawConfigCheckbox("Summon Chocobo", configuration.SummonChocobo, value => configuration.SummonChocobo = value,
            "Auto-summon chocobo companion using Gysahl Greens when timer is low. Will not summon in sanctuaries or duties.");
        if (configuration.SummonChocobo)
            DrawCompanionStanceCombo();
    }

    private static void OpenAdsLootOptions()
    {
        try
        {
            if (Plugin.PluginInterface.GetIpcSubscriber<bool>("ADS.ToggleLootUi").InvokeFunc())
                return;

            Plugin.LogWarning("[LootGoblin][ADS] ADS.ToggleLootUi returned false; not falling back to /ads loot.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[LootGoblin][ADS] ADS.ToggleLootUi unavailable: {ex.Message}");
            CommandHelper.SendCommand("/ads loot");
        }
    }

    private void DrawDungeonLootTab()
    {
        var useAdsDungeonSolver = configuration.UseAdsInsteadOfLegacyDungeonSolver;
        if (ImGui.Checkbox("Use ADS for dungeon phase", ref useAdsDungeonSolver))
        {
            configuration.UseAdsInsteadOfLegacyDungeonSolver = useAdsDungeonSolver;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("After a portal is accepted and duty entry is confirmed, LootGoblin sends /ads inside and waits for ADS to finish the dungeon instead of running its legacy dungeon solver.");

        if (configuration.UseAdsInsteadOfLegacyDungeonSolver && !plugin.IsAdsAvailable)
            ImGui.TextColored(ColorRed, "ADS is not loaded. Install ADS or disable this setting.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawConfigCheckbox("Auto Loot Chest", configuration.AutoLootChest, value => configuration.AutoLootChest = value);

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

        DrawGamblerLureModeCombo();
    }

    private void DrawIntegrationsTab()
    {
        DrawFoodSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawConfigCheckbox("Auto Discard (/ays discard)", configuration.EnableAutoDiscard, value => configuration.EnableAutoDiscard = value,
            "Runs /ays discard every 30s during a mounted safe idle window. Defers while in combat, loading, or cutscene-like states. Requires AutoRetainer plugin.");
        DrawConfigCheckbox("Auto Sync FATE", configuration.AutoSyncFate, value => configuration.AutoSyncFate = value,
            "Runs /levelsync on after joining a FATE. Defers while mounted or riding pillion.");

        DrawAdsBmrAdjustmentsSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("Command Triggers");
        ImGui.Spacing();

        DrawCommandTriggerList("Landing / Duty Entry", landingCommandTriggerDrafts, Configuration.LandingOrDutyCommandTriggerDefaults);
        ImGui.Spacing();
        DrawCommandTriggerList("Finish", finishCommandTriggerDrafts, Configuration.FinishCommandTriggerDefaults);

        if (!string.IsNullOrWhiteSpace(commandTriggerStatus))
            ImGui.TextDisabled(commandTriggerStatus);
    }

    private void DrawInterfaceTab()
    {
        DrawConfigCheckbox("Show Main Window on Login", configuration.ShowMainWindow, value => configuration.ShowMainWindow = value);
        DrawConfigCheckbox("Movable Settings Window", configuration.IsConfigWindowMovable, value => configuration.IsConfigWindowMovable = value);
        DrawConfigCheckbox("Krangle Names", configuration.KrangleNames, value => configuration.KrangleNames = value);
    }

    private void DrawAdvancedTab()
    {
        DrawConfigCheckbox("Debug Mode", configuration.DebugMode, value => configuration.DebugMode = value);
        DrawConfigCheckbox("Enable State Logging", configuration.EnableStateLogging, value => configuration.EnableStateLogging = value);
        DrawConfigCheckbox("Map Diagnostics", configuration.ShowDebugMapCompletion, value => configuration.ShowDebugMapCompletion = value,
            "Shows Location Data diagnostics in the main window and enables map/aetheryte collection tools.");
        DrawConfigCheckbox("Ground-only map diagnostics", configuration.CycleGroundOnly, value => configuration.CycleGroundOnly = value);

        ImGui.Spacing();
        var dedicatedDiagnosticLog = configuration.EnableDedicatedDiagnosticLog;
        if (ImGui.Checkbox("Write dedicated LootGoblin diagnostic log", ref dedicatedDiagnosticLog))
            plugin.SetDedicatedDiagnosticLogEnabled(dedicatedDiagnosticLog);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Writes high-signal events and state snapshots. Rotates at 20 MB and retains the newest 10 files.");

        ImGui.TextDisabled(plugin.DedicatedDiagnosticLog.DirectoryPath);
        if (ImGui.Button("Open Log Folder"))
            plugin.OpenDiagnosticLogFolder();

        ImGui.SameLine();
        if (!configuration.EnableDedicatedDiagnosticLog)
            ImGui.BeginDisabled();
        if (ImGui.Button("Write Snapshot Now"))
            plugin.WriteDiagnosticSnapshotNow();
        if (!configuration.EnableDedicatedDiagnosticLog)
            ImGui.EndDisabled();

        ImGui.Spacing();
        if (ImGui.Button("Disable Pandora's Box"))
            CommandHelper.TrySendCommand("/xldisableplugin Pandora's Box");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Disable Pandora's Box through Dalamud.");

        ImGui.Spacing();
        DrawAdsRepairTestButton();
    }

    private void DrawConfigCheckbox(string label, bool currentValue, Action<bool> setter, string? tooltip = null)
    {
        var value = currentValue;
        if (ImGui.Checkbox(label, ref value))
        {
            setter(value);
            configuration.Save();
        }

        if (!string.IsNullOrWhiteSpace(tooltip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
    }

    private void DrawCompanionStanceCombo()
    {
        var stances = new[] { "Free Stance", "Defender Stance", "Attacker Stance", "Healer Stance", "Follow" };
        var stanceIdx = Array.IndexOf(stances, configuration.CompanionStance);
        if (stanceIdx < 0) stanceIdx = 0;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Companion Stance", ref stanceIdx, stances, stances.Length))
        {
            configuration.CompanionStance = stances[stanceIdx];
            configuration.Save();
        }
    }

    private void DrawGamblerLureModeCombo()
    {
        var treasureHighLowModes = new[] { "Solve EV", "Skip", "Observe only" };
        var treasureHighLowModeIndex = configuration.TreasureHighLowMode switch
        {
            TreasureHighLowMode.Skip => 1,
            TreasureHighLowMode.ObserveOnly => 2,
            _ => 0,
        };
        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("Gambler's Lure Solver", ref treasureHighLowModeIndex, treasureHighLowModes, treasureHighLowModes.Length))
        {
            configuration.TreasureHighLowMode = treasureHighLowModeIndex switch
            {
                1 => TreasureHighLowMode.Skip,
                2 => TreasureHighLowMode.ObserveOnly,
                _ => TreasureHighLowMode.SolveExpectedValue,
            };
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Solve EV clicks only after it reads a reliable card/stage; otherwise it holds and retries. Skip keeps current skip/close behavior. Observe logs readable state only and never clicks.");
    }

    private void DrawMountSelection()
    {
        ImGui.Text("Mount Selection");
        ImGui.SameLine();
        ImGui.TextDisabled("(Used for manual mounting)");

        var mountNames = plugin.MountNames;
        var currentMount = configuration.SelectedMount;
        ImGui.SetNextItemWidth(320);
        if (ImGui.BeginCombo("##MountSelect", string.IsNullOrEmpty(currentMount) ? "(none)" : currentMount))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##MountSearch", ref mountSearch, 64);
            ImGui.Separator();

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

    private void DrawAdsRepairTestButton()
    {
        var repairMode = ResolveAdsRepairMode(configuration.RepairMode);
        var repairModeLabel = GetRepairModeLabel(configuration.RepairMode);
        if (ImGui.Button("Test ADS Repair Mode"))
        {
            if (!plugin.IsAdsAvailable)
            {
                var message = $"ADS is not loaded; {repairModeLabel} repair test not started.";
                plugin.PrintChat(message);
                plugin.AddDebugLog($"[Repair] {message}");
            }
            else if (configuration.RepairMode == RepairMode.NpcNoInn && !GameHelpers.IsInSanctuary())
            {
                const string message = "ADS NPC no-inn repair can only start from a sanctuary.";
                plugin.PrintChat(message);
                plugin.AddDebugLog($"[Repair] {message}");
            }
            else if (plugin.AdsStatusService.StartRepair(repairMode))
            {
                var message = $"ADS {repairModeLabel} repair test requested.";
                plugin.PrintChat(message);
                plugin.AddDebugLog($"[Repair] {message}");
            }
            else
            {
                var adsStatus = plugin.AdsStatusService.Refresh(force: true);
                var statusText = string.IsNullOrWhiteSpace(adsStatus.UtilityStatus)
                    ? "ADS did not accept the repair request."
                    : adsStatus.UtilityStatus;
                var message = $"ADS {repairModeLabel} repair test failed: {statusText}";
                plugin.PrintChat(message);
                plugin.AddDebugLog($"[Repair] {message}");
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Debug-only IPC test: ADS.StartRepair(\"{repairMode}\").");
    }

    private static string ResolveAdsRepairMode(RepairMode repairMode)
        => repairMode switch
        {
            RepairMode.Self => "self",
            RepairMode.NpcNoInnNoTeleport => "npc-no-teleport-no-inn",
            _ => "npc-no-inn",
        };

    private static string GetRepairModeLabel(RepairMode repairMode)
        => repairMode switch
        {
            RepairMode.Self => "self",
            RepairMode.NpcNoInnNoTeleport => "NPC no-inn/no-teleport",
            _ => "NPC no-inn",
        };

    private void DrawAdsBmrAdjustmentsSection()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text("ADS / BMR Adjustments");
        ImGui.Spacing();

        var reduceRange = configuration.BmrReduceActivationRangeForOutdoorAreas;
        if (ImGui.Checkbox("BMR reduce activation range for outdoor areas", ref reduceRange))
        {
            configuration.BmrReduceActivationRangeForOutdoorAreas = reduceRange;
            configuration.Save();
            plugin.AdsReflectionIpcService.QueueImmediateUpdate();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"When enabled, LootGoblin asks ADS to set BMR MaxLoadDistance to {AdsReflectionIpcService.ReducedOutdoorMaxLoadDistance:0}.");

        var disableHunts = configuration.BmrDisableHuntModules;
        if (ImGui.Checkbox("BMR Disable Hunt Modules", ref disableHunts))
        {
            configuration.BmrDisableHuntModules = disableHunts;
            configuration.Save();
            plugin.AdsReflectionIpcService.QueueImmediateUpdate();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When enabled, LootGoblin asks ADS to disable BMR hunt modules.");

        var reflection = plugin.AdsReflectionIpcService;
        var statusColor = !reflection.IsAdsAvailable && reflection.HasPendingActions
            ? ColorYellow
            : reflection.StatusText.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                ? ColorYellow
                : ColorGreen;
        ImGui.TextColored(statusColor, $"ADS reflection: {reflection.StatusText}");

        if (reflection.NextAttemptAtUtc is { } nextAttempt && nextAttempt > DateTime.UtcNow)
        {
            var seconds = Math.Max(0, (int)Math.Ceiling((nextAttempt - DateTime.UtcNow).TotalSeconds));
            ImGui.TextColored(ColorGrey, $"  Next retry/reassert in {seconds}s.");
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

    private void DrawCommandTriggerList(string label, List<string> drafts, IReadOnlyList<string> defaults)
    {
        EnsureCommandTriggerDraftRows(drafts);

        ImGui.Text(label);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Defaults##{label}"))
        {
            ReplaceCommandTriggerValues(drafts, defaults);
            commandTriggerDraftsDirty = true;
            SaveCommandTriggerDraftsIfDirty($"{label} defaults");
        }

        for (var i = 0; i < drafts.Count; i++)
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

            ImGui.SameLine();
            if (ImGui.SmallButton($"+##{label}_{i}"))
            {
                drafts.Insert(i + 1, string.Empty);
                commandTriggerDraftsDirty = true;
                SaveCommandTriggerDraftsIfDirty($"{label} insert {i + 2}");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton($"-##{label}_{i}"))
            {
                if (drafts.Count > 1)
                {
                    drafts.RemoveAt(i);
                    i--;
                }
                else if (!string.IsNullOrEmpty(drafts[0]))
                {
                    drafts[0] = string.Empty;
                }
                else
                {
                    continue;
                }

                commandTriggerDraftsDirty = true;
                SaveCommandTriggerDraftsIfDirty($"{label} remove {i + 2}");
            }
        }
    }

    private void RefreshCommandTriggerDrafts()
    {
        CopyCommandTriggerValues(configuration.LandingOrDutyCommandTriggers, landingCommandTriggerDrafts);
        CopyCommandTriggerValues(configuration.FinishCommandTriggers, finishCommandTriggerDrafts);
        commandTriggerDraftsDirty = false;
        commandTriggerDraftsInitialized = true;
    }

    private void SaveCommandTriggerDraftsIfDirty(string reason)
    {
        if (!commandTriggerDraftsDirty)
            return;

        configuration.LandingOrDutyCommandTriggers ??= Configuration.CreateDefaultLandingOrDutyCommandTriggers();
        configuration.FinishCommandTriggers ??= Configuration.CreateDefaultFinishCommandTriggers();

        ReplaceCommandTriggerValues(configuration.LandingOrDutyCommandTriggers, landingCommandTriggerDrafts);
        ReplaceCommandTriggerValues(configuration.FinishCommandTriggers, finishCommandTriggerDrafts);
        configuration.Save();
        commandTriggerDraftsDirty = false;
        commandTriggerStatus = "Command triggers saved.";
        Plugin.Log.Information($"[ConfigWindow] Command triggers saved ({reason}).");
    }

    private static void CopyCommandTriggerValues(IReadOnlyList<string>? source, List<string> destination)
    {
        destination.Clear();
        if (source != null)
        {
            foreach (var value in source)
                destination.Add(value ?? string.Empty);
        }

        EnsureCommandTriggerDraftRows(destination);
    }

    private static void ReplaceCommandTriggerValues(List<string> destination, IReadOnlyList<string> source)
    {
        destination.Clear();
        foreach (var value in source)
            destination.Add(value ?? string.Empty);

        EnsureCommandTriggerDraftRows(destination);
    }

    private static void EnsureCommandTriggerDraftRows(List<string> commands)
    {
        if (commands.Count == 0)
            commands.Add(string.Empty);
    }
}
