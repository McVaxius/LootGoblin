using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using LootGoblin.Models;
using LootGoblin.Services;
using Lumina.Excel.Sheets;
using AethertekUI;

namespace LootGoblin.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly AethertekUI.Dalamud.MaterialSupportLog supportLog = new();
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
    private static readonly Vector4 ColorGrey = new(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 ColorRed = new(1f, 0.3f, 0.3f, 1f);
    private static readonly Vector4 ColorGreen = new(0.3f, 1f, 0.3f, 1f);
    private static readonly Vector4 ColorYellow = new(1f, 1f, 0.3f, 1f);
    // Include conditional controls here so they remain discoverable regardless of current selections.
    private static readonly Dictionary<string, string> SettingsTabSearchTerms = new()
    {
        ["Run"] = "Combat job current job gearset class selection; Gather job (current character) disabled gathering prerequisites " +
            "GatherBuddyReborn GBR botanist miner fisher unlocked Map Queue gatherable maps; " +
            "Max map allowance wait (min) minutes cooldown timers; Return when done return destination Lifestream FC free company personal house inn; " +
            "ADS repair threshold % percent repair mode durability gear equipment self NPC no inn + no TP teleport factory defaults",
        ["Maps"] = "Map Queue map selection enabled types run counts gather choices show all known maps; " +
            "Fetch maps from retainers retrieval withdraw retainer bell XA Database XADB; " +
            "Fetch maps from saddlebags inventory saddlebag storage; Auto-update locations on login download updated locs " +
            "community location data coordinates database dependencies factory defaults",
        ["Marketboard"] = "Emptor install repository copy repo URL open /xlsettings /xlplugins plugin installer API v1 v2 v3 v4 v5; " +
            "Purchase requirements buy buying shopping cart marketable maps maximum gil price cap ceiling quantity orders trip; " +
            "Refresh Emptor Prices hints NQ minimum listing session cooldown countdown pending scope; " +
            "Emptor marketboard city Limsa Lominsa Ul'dah Gridania Kugane Crystarium Old Sharlayan Tuliyollal; " +
            "Include same-data-center server travel for purchasing maps world visit rotate sticky success; " +
            "Continue if not all party members rejoin after seconds party restore restoration timeout invite roster leader disband; " +
            "Include data center travel for purchasing maps DC region Lifestream starting world; " +
            "Include visiting OCE for maps once local data centers are exhausted Oceania Materia factory defaults",
        ["Travel"] = "Auto Teleport Lifestream /li; Require vnavmesh vnav navigation pathfinding movement flying requirements; " +
            "Do not use Tamamizu aetheryte avoid skip destination; Nav Timeout (s) seconds; " +
            "Mount Selection search manual mounting unlocked Company Chocobo factory defaults",
        ["Party"] = "Wait for Party; Wait for Party for thief maps / underwater; Require All Mounted takeoff remount recovery; " +
            "Party Wait Timeout (s) seconds; Time to wait before teleporting (s) delay; OPEN ADS LOOT OPTIONS; " +
            "Wait for party before dismounting landing proximity distance 10 yalms; Specify number of party to wait for " +
            "Players to wait for count threshold other players excludes self; Summon Chocobo companion Gysahl Greens " +
            "Companion Stance Free Defender Attacker Healer Follow sanctuary duties optional factory defaults",
        ["Dungeon/Loot"] = "Use ADS for dungeon phase handoff /ads inside legacy solver portal duty entry; " +
            "Completed-duty exit leave duty Loot Goblin: exit after delay exit when all others have left party departure " +
            "ADS: exit after delay /ads leave No automatic exit manual Duty-end delay (s) seconds; " +
            "Auto Loot Chest coffer Interaction Range (y) yalms Chest Open Timeout (s); " +
            "Gambler's Lure Solver Higher/Lower High/Low cards minigame Solve EV expected value Skip Observe only factory defaults",
        ["Integrations"] = "Optional automation plugins; Food search selected Use HQ food high quality NQ Clear Food " +
            "Search for Food if Depleted inventory fallback buff Boiled Egg FrenRider; " +
            "Auto Discard (/ays discard) AutoRetainer configured discard list inventory space cleanup safe mounted windows enabled; " +
            "Auto Sync FATE /levelsync; RSR hostile targeting RotationSolver Reborn All Attackable Targets Previously Engaged Targets " +
            "All Targets When Solo in Duty All Targets When Solo Solo Deep Dungeon Smart; " +
            "ADS BMR Adjustments BossMod Reborn reduce activation range for outdoor areas MaxLoadDistance Disable Hunt Modules reflection; " +
            "Command Triggers slash commands Landing Duty Entry Finish Defaults add remove slots global profile " +
            "Use current-character command trigger override /rotation auto manual cancel /bmrai /vbmai VBM /fr /cbt follow combat factory defaults",
        ["Interface"] = "Appearance Language Color Compact mode Teal Blue Pink Custom RGB; " +
            "Show Main Window on Login visibility behavior; Movable Settings Window move lock position; " +
            "Krangle Names name obfuscation privacy player party server world display factory defaults",
        ["Advanced"] = "Obstacle maps on BossMod Reborn BMR; Debug Mode Enable State Logging Map Diagnostics location data aetheryte collection tools " +
            "Ground-only map diagnostics; Write dedicated LootGoblin diagnostic log Open Log Folder Write Snapshot Now " +
            "troubleshooting debug logs; Disable Pandora's Box; Test ADS Repair Mode factory defaults",
    };
    private const string EmptorRepositoryUrl = "https://raw.githubusercontent.com/Evernow/DalamudPlugins/main/pluginmaster.json";
    private static readonly string[] RsrTargetHostileTypeLabels =
    {
        "All Attackable Targets",
        "Previously Engaged Targets",
        "All Targets When Solo in Duty",
        "All Targets When Solo",
        "Solo Deep Dungeon Smart",
    };
    
    private readonly Configuration configuration;
    private readonly Plugin plugin;
    private string settingsSearch = string.Empty;
    private string mountSearch = "";
    private string foodSearch = "";
    private readonly List<(uint Id, string Name)> foodItems = new();
    private bool foodItemsLoaded = false;
    private readonly List<string> landingCommandTriggerDrafts = new();
    private readonly List<string> finishCommandTriggerDrafts = new();
    private bool commandTriggerDraftsDirty;
    private bool commandTriggerDraftsInitialized;
    private bool commandTriggerDraftsUseCharacterOverride;
    private string commandTriggerDraftsCharacterKey = string.Empty;
    private string commandTriggerStatus = string.Empty;
    private bool selectMarketboardTab;

    public ConfigWindow(Plugin plugin) : base("Loot Goblin Settings###LootGoblinConfig")
    {
        Flags = ImGuiWindowFlags.HorizontalScrollbar;

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
        settingsSearch = string.Empty;
    }

    internal void OpenMarketboardTab()
    {
        selectMarketboardTab = true;
        IsOpen = true;
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
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
        => windowMotion.Restore(this);

    public override void Draw()
    {
        windowMotion.DrawChrome();
        UiGui.Title("Loot Goblin Settings", UiText.T("Loot Goblin Settings"));
        if (!commandTriggerDraftsInitialized || ImGui.IsWindowAppearing() || CommandTriggerDraftSourceChanged())
        {
            SaveCommandTriggerDraftsIfDirty("draft source refresh");
            RefreshCommandTriggerDrafts();
        }

        UiGui.Text("Loot Goblin Settings");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(Math.Min(220 * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        UiGui.InputText("Search settings", ref settingsSearch, 128);
        ImGui.SameLine();
        if (UiGui.SmallButton("Clear##SettingsSearch"))
            settingsSearch = string.Empty;

        var searchWords = settingsSearch.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matchingTabs = searchWords.Length == 0
            ? Array.Empty<string>()
            : SettingsTabSearchTerms
                .Where(tab =>
                {
                    var terms = UiText.SearchTerms(tab.Key + " " + tab.Value);
                    return searchWords.All(word => terms.Contains(word, StringComparison.OrdinalIgnoreCase));
                })
                .Select(tab => tab.Key)
                .ToArray();

        if (searchWords.Length > 0 && matchingTabs.Length == 0)
            UiGui.TextDisabled("No matching tabs.");
        else
            UiGui.TextDisabled("Matching tabs are highlighted. Select a tab to view settings.");
        ImGui.Spacing();

        using var tabs = MaterialTabs.Begin("##LootGoblinSettingsTabs",
            new[] { "Run", "Maps", "Marketboard", "Travel", "Party", "Dungeon/Loot", "Integrations", "Interface", "Advanced" }.Select(UiText.T).ToArray(), ImGuiTabBarFlags.FittingPolicyScroll);
        if (tabs.Visible)
        {
            if (BeginSettingsTab("Run", matchingTabs))
            {
                DrawRunTab();
                ImGui.EndTabItem();
            }

            if (BeginSettingsTab("Maps", matchingTabs))
            {
                DrawMapsTab();
                ImGui.EndTabItem();
            }

            var marketboardTabFlags = selectMarketboardTab
                ? ImGuiTabItemFlags.SetSelected
                : ImGuiTabItemFlags.None;
            if (BeginSettingsTab("Marketboard", matchingTabs, marketboardTabFlags))
            {
                DrawMarketboardTab();
                ImGui.EndTabItem();
            }
            selectMarketboardTab = false;

            if (BeginSettingsTab("Travel", matchingTabs))
            {
                DrawTravelTab();
                ImGui.EndTabItem();
            }

            if (BeginSettingsTab("Party", matchingTabs))
            {
                DrawPartyTab();
                ImGui.EndTabItem();
            }

            if (BeginSettingsTab("Dungeon/Loot", matchingTabs))
            {
                DrawDungeonLootTab();
                ImGui.EndTabItem();
            }

            if (BeginSettingsTab("Integrations", matchingTabs))
            {
                DrawIntegrationsTab();
                ImGui.EndTabItem();
            }

            if (BeginSettingsTab("Interface", matchingTabs))
            {
                DrawInterfaceTab();
                ImGui.EndTabItem();
            }

            if (BeginSettingsTab("Advanced", matchingTabs))
            {
                DrawAdvancedTab();
                ImGui.EndTabItem();
            }

        }
    }

    private static bool BeginSettingsTab(string label, string[] matchingTabs, ImGuiTabItemFlags flags = ImGuiTabItemFlags.None)
    {
        var matches = matchingTabs.Contains(label);
        if (matches)
            ImGui.PushStyleColor(ImGuiCol.Text, ColorYellow);

        var selected = UiGui.BeginTabItem(label, flags);
        if (matches)
            ImGui.PopStyleColor();

        return selected;
    }

    private void DrawRunTab()
    {
        UiGui.TextWrapped("Choose a combat job, or keep Current job. Gathering needs GatherBuddyReborn, an unlocked gather job with a gearset, and a gatherable map enabled for gathering in Map Queue.");
        UiGui.TextWrapped("Factory defaults: current combat job; gathering off. The controls below show your current selections.");
        ImGui.Spacing();

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

        var maxMapAllowanceWaitMinutes = Math.Clamp(configuration.MaxMapAllowanceWaitMinutes, 0, 1440);
        ImGui.SetNextItemWidth(Math.Min(120 * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (UiGui.InputInt("Max map allowance wait (min)", ref maxMapAllowanceWaitMinutes))
        {
            configuration.MaxMapAllowanceWaitMinutes = Math.Clamp(maxMapAllowanceWaitMinutes, 0, 1440);
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("When missing-map gathering is the only remaining action, wait this long for a map allowance before finishing the run.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        UiGui.TextWrapped("Return when done optionally uses Lifestream once enabled map sources are exhausted. Factory default: off.");
        var returnWhenDone = configuration.ReturnWhenDoneEnabled;
        if (UiGui.Checkbox("Return when done", ref returnWhenDone))
        {
            configuration.ReturnWhenDoneEnabled = returnWhenDone;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Runs the selected Lifestream return only after no enabled inventory, saddlebag, or retainer maps remain.");

        var returnDestinations = new[] { "FC", "Personal", "Inn" };
        var returnDestinationIndex = configuration.ReturnWhenDoneDestination switch
        {
            ReturnWhenDoneDestination.Personal => 1,
            ReturnWhenDoneDestination.Inn => 2,
            _ => 0,
        };
        ImGui.SetNextItemWidth(Math.Min(180 * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (UiGui.Combo("Return destination", ref returnDestinationIndex, returnDestinations, returnDestinations.Length))
        {
            configuration.ReturnWhenDoneDestination = returnDestinationIndex switch
            {
                1 => ReturnWhenDoneDestination.Personal,
                2 => ReturnWhenDoneDestination.Inn,
                _ => ReturnWhenDoneDestination.FC,
            };
            configuration.Save();
        }

        ImGui.Spacing();
        UiGui.TextWrapped("Repair uses ADS; a 0% threshold disables it. Factory defaults: 75%, NPC no inn.");
        var repairThreshold = Math.Clamp(configuration.RepairThresholdPercent, 0, 100);
        if (UiGui.SliderInt("Repair threshold %", ref repairThreshold, 0, 100))
        {
            configuration.RepairThresholdPercent = repairThreshold;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("0 disables repair. When equipped gear drops below this value, LootGoblin asks ADS to repair before continuing.");

        var repairModes = new[] { "Self", "NPC no inn", "NPC No Inn + No TP" };
        var repairModeIndex = configuration.RepairMode switch
        {
            RepairMode.Self => 0,
            RepairMode.NpcNoInnNoTeleport => 2,
            _ => 1,
        };
        ImGui.SetNextItemWidth(Math.Min(180 * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (UiGui.Combo("Repair mode", ref repairModeIndex, repairModes, repairModes.Length))
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

        ImGui.SetNextItemWidth(Math.Min(220 * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (UiGui.Combo(label, ref currentIndex, comboLabels, comboLabels.Length))
        {
            setter(currentIndex == 0 ? 0 : options[currentIndex - 1].Id);
            if (saveAfterSet)
                configuration.Save();
        }

        if (ImGui.IsItemHovered())
            UiGui.SetTooltip(tooltip);
    }

    private void DrawMapsTab()
    {
        UiGui.TextWrapped("Use Map Queue in the main window to select maps and run counts. Retrieval below supplies enabled maps when inventory runs out.");
        UiGui.TextWrapped("Retainers need XA Database (XADB) and a reachable retainer bell. Saddlebag retrieval uses /saddlebag. Both retrieval options are on by factory default.");
        ImGui.Spacing();

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
            UiGui.TextColored(ColorYellow, "Downloading...");
        }
        else
        {
            if (UiGui.Button("Download Updated Locs"))
                _ = plugin.DownloadCommunityLocationsForCurrentVersionAsync();
            if (!string.IsNullOrEmpty(db.LastDownloadResult))
            {
                ImGui.SameLine();
                var dlColor = db.LastDownloadResult.StartsWith("OK") ? ColorGreen :
                              db.LastDownloadResult.StartsWith("Error") ? ColorRed : ColorGrey;
                UiGui.TextColored(dlColor, db.LastDownloadResult);
            }
        }
    }

    private void DrawTravelTab()
    {
        UiGui.TextWrapped("Map travel uses Lifestream for teleports and vnavmesh for movement. Choose an unlocked mount below for manual mounting.");
        UiGui.TextWrapped("Factory defaults: Auto Teleport and Require vnavmesh on; Company Chocobo selected.");
        ImGui.Spacing();

        DrawConfigCheckbox("Auto Teleport", configuration.AutoTeleport, value => configuration.AutoTeleport = value);
        DrawConfigCheckbox("Require vnavmesh", configuration.RequireVNav, value => configuration.RequireVNav = value);
        DrawConfigCheckbox("Do not use Tamamizu aetheryte", configuration.AvoidTamamizuAetheryte, value => configuration.AvoidTamamizuAetheryte = value,
            "Skips Tamamizu when choosing the map teleport destination.");

        var navTimeout = configuration.NavigationTimeout;
        if (UiGui.SliderFloat("Nav Timeout (s)", ref navTimeout, 30f, 600f, "%.0f"))
        {
            configuration.NavigationTimeout = navTimeout;
            configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawMountSelection();
    }

    private void DrawMarketboardTab()
    {
        plugin.EmptorIPC.RefreshStatus();
        plugin.LifestreamIPC.RefreshStatus();

        if (!plugin.EmptorIPC.IsAvailable)
            DrawEmptorInstallGuidance();
        else
        {
            DrawEmptorPriceRefreshControls();
            ImGui.Spacing();
        }

        UiGui.Text("Purchase requirements");
        UiGui.BulletText("Enable a cart on a marketable map row and set a positive maximum gil price.");
        UiGui.BulletText("LootGoblin submits quantity-one orders and can prepare up to three maps per trip.");
        UiGui.BulletText("Limsa Lominsa is the default. City selection requires Emptor v4+; blank Ul'dah remains compatible with v1-v3.");
        UiGui.BulletText("Emptor v5+ supplies session-only NQ minimum-listing price hints for known marketable maps.");

        var configuredCityKey = configuration.EmptorMarketboardCityKey?.Trim() ?? string.Empty;
        var cityNeedsV4 = !string.IsNullOrEmpty(configuredCityKey) &&
                          plugin.EmptorIPC.ApiVersion is > 0 and < 4;
        var emptorStatus = cityNeedsV4
            ? $"Configured city '{configuredCityKey}' requires Emptor API v4 or newer; detected v{plugin.EmptorIPC.ApiVersion}."
            : plugin.EmptorIPC.StatusText;
        UiGui.TextColored(
            plugin.EmptorIPC.IsAvailable && !cityNeedsV4 ? ColorGreen : ColorRed,
            emptorStatus);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiGui.SetTooltip("Blank/default city omits the city request field for Emptor v1-v3 compatibility. A selected city is sent only through Emptor API v4 or newer.");

        var cityOptions = plugin.EmptorIPC.CityOptions;
        var currentCityLabel = cityOptions
            .FirstOrDefault(city => string.Equals(city.Key, configuredCityKey, StringComparison.OrdinalIgnoreCase))
            ?.Label;
        if (string.IsNullOrWhiteSpace(currentCityLabel))
            currentCityLabel = $"Unknown ({configuredCityKey})";
        if (UiGui.BeginCombo("Emptor marketboard city", currentCityLabel))
        {
            foreach (var city in cityOptions)
            {
                var selected = string.Equals(city.Key, configuredCityKey, StringComparison.OrdinalIgnoreCase);
                if (UiGui.Selectable(city.Label, selected))
                {
                    configuration.EmptorMarketboardCityKey = city.Key;
                    configuration.Save();
                    configuredCityKey = city.Key;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Chooses where Emptor travels for its marketboard. Ul'dah stores a blank key and keeps Emptor's default behavior.");
        UiGui.TextColored(ColorGrey, plugin.EmptorIPC.CityOptionsStatusText);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawConfigCheckbox(
            "Include same-data-center server travel for purchasing maps",
            configuration.EnableSameDataCenterMapTravel,
            value => configuration.EnableSameDataCenterMapTravel = value,
            "Try the current world first, then each remaining public world on the current data center once per map search.");

        if (!configuration.EnableSameDataCenterMapTravel)
            ImGui.BeginDisabled();

        var rotate = configuration.MarketWorldStartMode == MarketWorldStartMode.Rotate;
        if (UiGui.RadioButton("Rotate", rotate))
        {
            configuration.MarketWorldStartMode = MarketWorldStartMode.Rotate;
            configuration.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiGui.SetTooltip("For the next map search, start after the world where the previous search ended, wrapping to the beginning when needed.");

        var sticky = configuration.MarketWorldStartMode == MarketWorldStartMode.StickySuccess;
        if (UiGui.RadioButton("Sticky success", sticky))
        {
            configuration.MarketWorldStartMode = MarketWorldStartMode.StickySuccess;
            configuration.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiGui.SetTooltip("Start on the last world that successfully sold a map, then try each remaining unvisited enabled world if it has no acceptable listing. This is the default.");

        if (!configuration.EnableSameDataCenterMapTravel)
            ImGui.EndDisabled();

        UiGui.TextColored(
            plugin.LifestreamIPC.IsAvailable ? ColorGreen : ColorGrey,
            plugin.LifestreamIPC.StatusText);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiGui.SetTooltip("Lifestream is required for off-world searches. LootGoblin verifies arrival and returns to the starting world before restoring the party.");

        ImGui.Spacing();
        var continueAfterTimeout = configuration.ContinueAfterPartialPartyRestore;
        if (UiGui.Checkbox("Continue if not all party members rejoin after", ref continueAfterTimeout))
        {
            configuration.ContinueAfterPartialPartyRestore = continueAfterTimeout;
            configuration.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiGui.SetTooltip("Invite missing captured members immediately and every 30 seconds after returning. Checked continues with whoever rejoined after timeout; unchecked stops.");
        ImGui.SameLine();
        var restoreTimeout = Math.Clamp(configuration.PartyRestoreTimeoutSeconds, 30, 3600);
        ImGui.SetNextItemWidth(Math.Min(90f * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (UiGui.InputInt("seconds##MarketPartyRestoreTimeout", ref restoreTimeout))
        {
            configuration.PartyRestoreTimeoutSeconds = Math.Clamp(restoreTimeout, 30, 3600);
            configuration.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiGui.SetTooltip("Maximum time to wait for the captured roster to rejoin. Allowed range is 30–3600 seconds; default is 300.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawConfigCheckbox(
            "Include data center travel for purchasing maps",
            configuration.IncludeDataCenterTravelForMapPurchases,
            value => configuration.IncludeDataCenterTravelForMapPurchases = value,
            "After the current data center is exhausted, visit public worlds on other data centers in the current region once per map search.");

        if (!configuration.IncludeDataCenterTravelForMapPurchases)
            ImGui.BeginDisabled();
        DrawConfigCheckbox(
            "Include visiting OCE for maps once local data centers are exhausted",
            configuration.IncludeOceTravelForMapPurchases,
            value => configuration.IncludeOceTravelForMapPurchases = value,
            "After enabled local-region data centers are exhausted, try public OCE worlds. Requires data-center travel and defaults off.");
        if (!configuration.IncludeDataCenterTravelForMapPurchases)
            ImGui.EndDisabled();
    }

    private void DrawEmptorPriceRefreshControls()
    {
        var emptor = plugin.EmptorIPC;
        var now = DateTime.UtcNow;
        var remaining = emptor.GetManualPriceRefreshCooldown(now);
        var pending = emptor.IsPriceRefreshPending || emptor.IsManualPriceRefreshRequested;
        var blocked = pending || !emptor.IsAvailable || emptor.ApiVersion < 5 || remaining > TimeSpan.Zero;

        if (blocked)
            ImGui.BeginDisabled();
        if (UiGui.SmallButton("Refresh Emptor Prices"))
            emptor.RequestManualPriceRefresh(Plugin.ClientState.IsLoggedIn, out _);
        if (blocked)
            ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var tooltip = pending
                ? "An Emptor price lookup is pending."
                : !emptor.IsAvailable || emptor.ApiVersion < 5
                    ? "Emptor API v5 or newer is required for price hints."
                    : remaining > TimeSpan.Zero
                        ? $"Manual refresh is available in {FormatPriceCountdown(remaining)}."
                        : "Queue one global refresh for all marketable known maps using the current travel scope.";
            UiGui.SetTooltip(tooltip);
        }

        if (remaining > TimeSpan.Zero)
        {
            ImGui.SameLine();
            UiGui.TextColored(ColorGrey, UiText.F($"Next manual refresh: {FormatPriceCountdown(remaining)}"));
        }

        UiGui.TextColored(ColorGrey, UiText.F($"  {emptor.PriceStatusText}"));
    }

    private static string FormatPriceCountdown(TimeSpan remaining)
        => UiText.F($"{Math.Max(0, (int)remaining.TotalMinutes):00}:{Math.Max(0, remaining.Seconds):00}");

    private static void DrawEmptorInstallGuidance()
    {
        ImGui.SetWindowFontScale(1.35f);
        UiGui.TextColored(ColorRed, "EMPTOR IS NOT INSTALLED OR LOADED");
        ImGui.SetWindowFontScale(1f);
        UiGui.TextWrapped("Add Emptor's custom repository in Dalamud Settings, then install Emptor from the plugin installer.");

        if (UiGui.Button("Copy Emptor repo URL"))
            ImGui.SetClipboardText(EmptorRepositoryUrl);
        ImGui.SameLine();
        if (UiGui.Button("Open /xlsettings"))
            CommandHelper.SendCommand("/xlsettings");
        ImGui.SameLine();
        if (UiGui.Button("Open /xlplugins"))
            CommandHelper.SendCommand("/xlplugins");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawPartyTab()
    {
        UiGui.TextWrapped("Party waits coordinate takeoff, remounting and arrival; thief-map underwater waits have their own switch. Player-count thresholds count other players, excluding you.");
        UiGui.TextWrapped("The optional chocobo companion needs Gysahl Greens. Factory defaults: party and all-mounted waits on; companion summon off.");
        ImGui.Spacing();

        var waitForParty = configuration.WaitForParty;
        if (UiGui.Checkbox("Wait for Party", ref waitForParty))
        {
            Plugin.Log.Info($"[Config] Wait for Party changed from {configuration.WaitForParty} to {waitForParty}");
            configuration.WaitForParty = waitForParty;
            configuration.Save();
            Plugin.Log.Info($"[Config] Wait for Party saved as: {configuration.WaitForParty}");
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("When enabled, the bot waits for party members before takeoff, remount recovery, and map travel handoffs.");

        DrawConfigCheckbox("Wait for Party for thief maps / underwater", configuration.WaitForPartyForThiefMapsUnderwater, value => configuration.WaitForPartyForThiefMapsUnderwater = value,
            "Overrides the general Wait for Party setting for thief-map underwater travel, remount recovery, and descent/dig waits.");
        DrawConfigCheckbox("Require All Mounted", configuration.RequireAllMounted, value => configuration.RequireAllMounted = value,
            "When count threshold is off, takeoff and remount waits require every other party member mounted. Turn off to continue when any other same-zone party member is mounted.");

        var partyTimeout = configuration.PartyWaitTimeout;
        if (UiGui.SliderInt("Party Wait Timeout (s)", ref partyTimeout, 30, 300))
        {
            configuration.PartyWaitTimeout = partyTimeout;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            UiGui.SetTooltip(
                "Applies to all party waits. In count-threshold mode, timeout does not lower the configured player count. " +
                "In full-party proximity waits, timeout can allow guarded recovery when unresolved or out-of-territory members are the only blockers.");
        }

        var teleportDelay = Math.Clamp(configuration.PartyTeleportDelaySeconds, 0, 300);
        ImGui.SetNextItemWidth(Math.Min(80f * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (UiGui.InputInt("Time to wait before teleporting (s)##PartyTeleportDelaySeconds", ref teleportDelay))
        {
            configuration.PartyTeleportDelaySeconds = Math.Clamp(teleportDelay, 0, 300);
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Extra delay before sending the map teleport command after the map target is resolved.");

        if (UiGui.Button("OPEN ADS LOOT OPTIONS"))
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
                ImGui.SetNextItemWidth(Math.Min(80f * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
                if (UiGui.InputInt("Players to wait for##PartyWaitBeforeDismountRequiredOthers", ref requiredOthers))
                {
                    configuration.PartyWaitBeforeDismountRequiredOthers = Math.Clamp(requiredOthers, 1, 7);
                    configuration.Save();
                }
                if (ImGui.IsItemHovered())
                    UiGui.SetTooltip("Number of other party members required for every party wait. Local player is not counted.");
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
        UiGui.TextWrapped("ADS handoff runs /ads inside after confirmed duty entry. Completed-duty exit chooses a local delay, party departure, ADS delay or manual exit; chest and solver options are below.");
        UiGui.TextWrapped("Factory defaults: ADS handoff on; ADS exit after 20 seconds; Auto Loot Chest on; Gambler's Lure set to Solve EV (expected value).");
        ImGui.Spacing();

        var useAdsDungeonSolver = configuration.UseAdsInsteadOfLegacyDungeonSolver;
        if (UiGui.Checkbox("Use ADS for dungeon phase", ref useAdsDungeonSolver))
        {
            configuration.UseAdsInsteadOfLegacyDungeonSolver = useAdsDungeonSolver;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("After a portal is accepted and duty entry is confirmed, LootGoblin sends /ads inside and waits for ADS to finish the dungeon instead of running its legacy dungeon solver.");

        if (configuration.UseAdsInsteadOfLegacyDungeonSolver && !plugin.IsAdsAvailable)
            UiGui.TextColored(ColorRed, "ADS is not loaded. Install ADS or disable this setting.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawDutyExitBehaviour();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawConfigCheckbox("Auto Loot Chest", configuration.AutoLootChest, value => configuration.AutoLootChest = value);

        var chestRange = configuration.ChestInteractionRange;
        if (UiGui.SliderFloat("Interaction Range (y)", ref chestRange, 1f, 15f))
        {
            configuration.ChestInteractionRange = chestRange;
            configuration.Save();
        }

        var chestTimeout = configuration.ChestOpenTimeout;
        if (UiGui.SliderInt("Chest Open Timeout (s)", ref chestTimeout, 5, 30))
        {
            configuration.ChestOpenTimeout = chestTimeout;
            configuration.Save();
        }

        DrawGamblerLureModeCombo();
    }

    private void DrawDutyExitBehaviour()
    {
        UiGui.Text("Completed-duty exit");

        if (UiGui.RadioButton("Loot Goblin: exit after delay", configuration.CompletedDutyExitMode == DutyExitMode.LocalAfterDelay))
        {
            configuration.CompletedDutyExitMode = DutyExitMode.LocalAfterDelay;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Use Loot Goblin's local Leave Duty flow after the configured delay.");

        if (UiGui.RadioButton("Loot Goblin: exit when all others have left", configuration.CompletedDutyExitMode == DutyExitMode.LocalWhenPartyLeaves))
        {
            configuration.CompletedDutyExitMode = DutyExitMode.LocalWhenPartyLeaves;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Use Loot Goblin's local Leave Duty flow once no other loaded party members remain in the duty territory.");

        if (UiGui.RadioButton("ADS: exit after delay", configuration.CompletedDutyExitMode == DutyExitMode.AdsAfterDelay))
        {
            configuration.CompletedDutyExitMode = DutyExitMode.AdsAfterDelay;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Send /ads leave once after the configured delay.");

        if (UiGui.RadioButton("No automatic exit", configuration.CompletedDutyExitMode == DutyExitMode.None))
        {
            configuration.CompletedDutyExitMode = DutyExitMode.None;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Stop dungeon progression after completion and wait for a manual duty exit.");

        var exitDelaySeconds = Math.Max(1, configuration.DutyExitDelaySeconds);
        ImGui.SetNextItemWidth(Math.Min(80f * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (UiGui.InputInt("Duty-end delay (s)", ref exitDelaySeconds))
        {
            configuration.DutyExitDelaySeconds = Math.Max(1, exitDelaySeconds);
            configuration.Save();
        }
    }

    private void DrawIntegrationsTab()
    {
        UiGui.TextWrapped("Configure optional food, discard and combat automation here. Command Triggers run slash commands at Landing / Duty Entry and Finish; review them for the plugins you use.");
        ImGui.Spacing();

        DrawFoodSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawConfigCheckbox("Auto Discard (/ays discard)", configuration.EnableAutoDiscard, value => configuration.EnableAutoDiscard = value,
            "While Loot Goblin is enabled, runs /ays discard every 30s during safe mounted windows. Defers in combat, loading or cutscenes. Requires AutoRetainer and a configured discard list.");
        UiGui.TextWrapped("Auto Discard needs AutoRetainer with a configured discard list. When selected, it runs while Loot Goblin is enabled, during safe mounted windows. Factory default: off.");
        ImGui.Spacing();
        DrawConfigCheckbox("Auto Sync FATE", configuration.AutoSyncFate, value => configuration.AutoSyncFate = value,
            "Runs /levelsync on after joining a FATE and pauses coffer/portal recovery for joined-FATE handling. Turn off to ignore joined FATEs during map coffer flow.");

        var rsrTargetHostileTypeIndex = (int)configuration.RsrTargetHostileType;
        ImGui.SetNextItemWidth(Math.Min(240f * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (UiGui.Combo("RSR hostile targeting", ref rsrTargetHostileTypeIndex, RsrTargetHostileTypeLabels, RsrTargetHostileTypeLabels.Length))
        {
            configuration.RsrTargetHostileType = (RsrTargetHostileType)rsrTargetHostileTypeIndex;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Applied through RSR IPC immediately before /rotation auto or /rotation manual command triggers.");

        DrawAdsBmrAdjustmentsSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        UiGui.Text("Command Triggers");
        DrawCommandTriggerScopeSelector();
        ImGui.Spacing();

        DrawCommandTriggerList("Landing / Duty Entry", landingCommandTriggerDrafts, Configuration.LandingOrDutyCommandTriggerDefaults);
        ImGui.Spacing();
        DrawCommandTriggerList("Finish", finishCommandTriggerDrafts, Configuration.FinishCommandTriggerDefaults);

        if (!string.IsNullOrWhiteSpace(commandTriggerStatus))
            UiGui.TextDisabled(commandTriggerStatus);
    }

    private void DrawInterfaceTab()
    {
        UiGui.Text("Window appearance");
        ImGui.Separator();
        plugin.DrawAppearanceSelector();
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + 48 * MaterialTheme.Metrics.Scale
            <= ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X) ImGui.SameLine();
        using (MaterialControls.Push(LootGoblinPresentation.Controls(22))) plugin.DrawCompactSelector();
        plugin.DrawWindowSettings();
        ImGui.Separator();
        UiGui.TextWrapped("Control login visibility and settings-window movement. Krangle Names obfuscates player and server names displayed by Loot Goblin.");
        UiGui.TextWrapped("Factory defaults: show the main window on login and allow settings movement; name obfuscation off.");
        ImGui.Spacing();

        DrawConfigCheckbox("Show Main Window on Login", configuration.ShowMainWindow, value => configuration.ShowMainWindow = value);
        DrawConfigCheckbox("Movable Settings Window", configuration.IsConfigWindowMovable, value => configuration.IsConfigWindowMovable = value);
        DrawConfigCheckbox("Krangle Names", configuration.KrangleNames, value => configuration.KrangleNames = value);
    }

    private void DrawAdvancedTab()
    {
        supportLog.Draw(Plugin.PluginInterface, key => UiText.T(key),
            path => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }), ex => Plugin.Log.Error(ex, "Dalamud log export failed."), Plugin.CommandManager);
        DrawConfigCheckbox("Obstacle maps on", configuration.ObstacleMapsOn, value => configuration.ObstacleMapsOn = value,
            "Controls BossMod Reborn only. Default: off. Applies on Start and after each configured command batch, regardless of combat provider.");
        ImGui.Spacing();

        UiGui.TextWrapped("Use these controls for diagnostics and troubleshooting. The snapshot button needs dedicated logging; Test ADS Repair Mode starts a repair using the mode selected in Run.");
        UiGui.TextWrapped("Factory defaults: state logging on; Debug Mode, map diagnostics and dedicated file logging off.");
        ImGui.Spacing();

        DrawConfigCheckbox("Debug Mode", configuration.DebugMode, value => configuration.DebugMode = value);
        DrawConfigCheckbox("Enable State Logging", configuration.EnableStateLogging, value => configuration.EnableStateLogging = value);
        DrawConfigCheckbox("Map Diagnostics", configuration.ShowDebugMapCompletion, value => configuration.ShowDebugMapCompletion = value,
            "Shows Location Data diagnostics in the main window and enables map/aetheryte collection tools.");
        DrawConfigCheckbox("Ground-only map diagnostics", configuration.CycleGroundOnly, value => configuration.CycleGroundOnly = value);

        ImGui.Spacing();
        var dedicatedDiagnosticLog = configuration.EnableDedicatedDiagnosticLog;
        if (UiGui.Checkbox("Write dedicated LootGoblin diagnostic log", ref dedicatedDiagnosticLog))
            plugin.SetDedicatedDiagnosticLogEnabled(dedicatedDiagnosticLog);
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Writes high-signal events and state snapshots. Rotates at 20 MB and retains the newest 10 files.");

        UiGui.TextDisabled(plugin.DedicatedDiagnosticLog.DirectoryPath);
        if (UiGui.Button("Open Log Folder"))
            plugin.OpenDiagnosticLogFolder();

        ImGui.SameLine();
        if (!configuration.EnableDedicatedDiagnosticLog)
            ImGui.BeginDisabled();
        if (UiGui.Button("Write Snapshot Now"))
            plugin.WriteDiagnosticSnapshotNow();
        if (!configuration.EnableDedicatedDiagnosticLog)
            ImGui.EndDisabled();

        ImGui.Spacing();
        if (UiGui.Button("Disable Pandora's Box"))
            CommandHelper.TrySendCommand("/xldisableplugin Pandora's Box");
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Disable Pandora's Box through Dalamud.");

        ImGui.Spacing();
        DrawAdsRepairTestButton();
    }

    private void DrawConfigCheckbox(string label, bool currentValue, Action<bool> setter, string? tooltip = null)
    {
        var value = currentValue;
        if (UiGui.Checkbox(label, ref value))
        {
            setter(value);
            configuration.Save();
        }

        if (!string.IsNullOrWhiteSpace(tooltip) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiGui.SetTooltip(tooltip);
    }

    private void DrawCompanionStanceCombo()
    {
        var stances = new[] { "Free Stance", "Defender Stance", "Attacker Stance", "Healer Stance", "Follow" };
        var stanceIdx = Array.IndexOf(stances, configuration.CompanionStance);
        if (stanceIdx < 0) stanceIdx = 0;
        ImGui.SetNextItemWidth(Math.Min(220 * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (UiGui.Combo("Companion Stance", ref stanceIdx, stances, stances.Length))
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
        ImGui.SetNextItemWidth(Math.Min(180 * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (UiGui.Combo("Gambler's Lure Solver", ref treasureHighLowModeIndex, treasureHighLowModes, treasureHighLowModes.Length))
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
            UiGui.SetTooltip("Solve EV clicks only after it reads a reliable card/stage; otherwise it holds and retries. Skip keeps current skip/close behavior. Observe logs readable state only and never clicks.");
    }

    private void DrawMountSelection()
    {
        UiGui.Text("Mount Selection");
        ImGui.SameLine();
        UiGui.TextDisabled("(Used for manual mounting)");

        var mountNames = plugin.MountNames;
        var currentMount = configuration.SelectedMount;
        ImGui.SetNextItemWidth(Math.Min(320 * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (UiGui.BeginCombo("##MountSelect", string.IsNullOrEmpty(currentMount) ? UiText.T("(none)") : currentMount, translatePreview: false))
        {
            ImGui.SetNextItemWidth(-1);
            UiGui.InputText("##MountSearch", ref mountSearch, 64);
            ImGui.Separator();

            ImGui.BeginChild("##MountList", new Vector2(0, 200), false, ImGuiWindowFlags.HorizontalScrollbar);
            for (var i = 0; i < mountNames.Length; i++)
            {
                if (!string.IsNullOrEmpty(mountSearch) &&
                    !mountNames[i].Contains(mountSearch, StringComparison.OrdinalIgnoreCase))
                    continue;

                var isSelected = mountNames[i] == currentMount;
                if (UiGui.Selectable(mountNames[i], isSelected, mountNames[i]))
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
        if (UiGui.Button("Test ADS Repair Mode"))
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
            UiGui.SetTooltip(UiText.F($"Debug-only IPC test: ADS.StartRepair(\"{repairMode}\")."));
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
        UiGui.Text("ADS / BMR Adjustments");
        ImGui.Spacing();

        var reduceRange = configuration.BmrReduceActivationRangeForOutdoorAreas;
        if (UiGui.Checkbox("BMR reduce activation range for outdoor areas", ref reduceRange))
        {
            configuration.BmrReduceActivationRangeForOutdoorAreas = reduceRange;
            configuration.Save();
            plugin.AdsReflectionIpcService.QueueImmediateUpdate();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip(UiText.F($"When enabled, LootGoblin asks ADS to set BMR MaxLoadDistance to {AdsReflectionIpcService.ReducedOutdoorMaxLoadDistance:0}."));

        var disableHunts = configuration.BmrDisableHuntModules;
        if (UiGui.Checkbox("BMR Disable Hunt Modules", ref disableHunts))
        {
            configuration.BmrDisableHuntModules = disableHunts;
            configuration.Save();
            plugin.AdsReflectionIpcService.QueueImmediateUpdate();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("When enabled, LootGoblin asks ADS to disable BMR hunt modules.");

        var reflection = plugin.AdsReflectionIpcService;
        var statusColor = !reflection.IsAdsAvailable && reflection.HasPendingActions
            ? ColorYellow
            : reflection.StatusText.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                ? ColorYellow
                : ColorGreen;
        UiGui.TextColored(statusColor, UiText.F($"ADS reflection: {UiText.T(reflection.StatusText)}"));

        if (reflection.NextAttemptAtUtc is { } nextAttempt && nextAttempt > DateTime.UtcNow)
        {
            var seconds = Math.Max(0, (int)Math.Ceiling((nextAttempt - DateTime.UtcNow).TotalSeconds));
            UiGui.TextColored(ColorGrey, UiText.F($"  Next retry/reassert in {seconds}s."));
        }
    }

    private void DrawFoodSection()
    {
        EnsureFoodItemsLoaded();
        BackfillLegacyFoodSelection();

        UiGui.Text("Food");

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
            UiGui.Text(UiText.F($"  Selected: {configuration.FeedMeItem} [{qualityLabel}] ({configuration.FeedMeItemId})"));

            var useHighQuality = configuration.FeedMeUseHighQuality;
            if (UiGui.Checkbox("Use HQ food", ref useHighQuality))
            {
                configuration.FeedMeUseHighQuality = useHighQuality;
                plugin.FoodService.InvalidateFoodCache();
                configuration.Save();
            }

            if (UiGui.SmallButton("Clear Food"))
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
            UiGui.TextDisabled("  No food selected.");
        }

        var feedMeSearch = configuration.FeedMeSearch;
        if (UiGui.Checkbox("Search for Food if Depleted", ref feedMeSearch))
        {
            configuration.FeedMeSearch = feedMeSearch;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("If selected food runs out, search inventory for a fallback food from the FrenRider priority list.");
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
        var displayText = selectedId > 0 ? UiText.F("{0} ({1})", selectedName, selectedId) : UiText.F("Select {0}...", UiText.T(label));

        ImGui.SetNextItemWidth(Math.Min(300 * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (UiGui.BeginCombo($"##{label}Select", displayText, translatePreview: false))
        {
            ImGui.SetNextItemWidth(Math.Min(280 * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
            UiGui.InputText($"Search##{label}", ref search, 128);
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
                    if (UiGui.Selectable($"{item.Name} ({item.Id})##{label}{i}", isSelected, UiText.F("{0} ({1})", item.Name, item.Id)))
                    {
                        selectedId = (int)item.Id;
                        selectedName = item.Name;
                        changed = true;
                    }
                }

                if (shown == 0)
                    UiGui.TextDisabled("No results.");
            }
            else
            {
                UiGui.TextDisabled("Type at least 2 characters to search.");
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private void DrawCommandTriggerScopeSelector()
    {
        if (plugin.ActiveMapGatherContentId == 0)
        {
            UiGui.TextDisabled("Editing global command triggers. Log in to edit a character override.");
            return;
        }

        var useCharacterOverride = plugin.ActiveMapGatherConfig.OverrideCommandTriggers;
        if (UiGui.Checkbox("Use current-character command trigger override", ref useCharacterOverride))
        {
            SaveCommandTriggerDraftsIfDirty("command trigger override toggle");
            plugin.ActiveMapGatherConfig.SetCommandTriggerOverride(
                useCharacterOverride,
                configuration.LandingOrDutyCommandTriggers,
                configuration.FinishCommandTriggers);
            plugin.SaveActiveMapGatherConfig("command trigger override changed");
            RefreshCommandTriggerDrafts();
        }

        UiGui.TextDisabled(useCharacterOverride
            ? $"Editing character profile {plugin.ActiveMapGatherCharacterKey}."
            : "Editing global command triggers.");
    }

    private void DrawCommandTriggerList(string label, List<string> drafts, IReadOnlyList<string> defaults)
    {
        EnsureCommandTriggerDraftRows(drafts);

        UiGui.Text(label);
        ImGui.SameLine();
        if (UiGui.SmallButton($"Defaults##{label}"))
        {
            ReplaceCommandTriggerValues(drafts, defaults);
            commandTriggerDraftsDirty = true;
            SaveCommandTriggerDraftsIfDirty($"{label} defaults");
        }

        for (var i = 0; i < drafts.Count; i++)
        {
            ImGui.SetNextItemWidth(Math.Min(300 * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
            var command = drafts[i];
            var inputLabel = $"##{label}_{i}";
            if (UiGui.InputText(inputLabel, ref command, 128, ImGuiInputTextFlags.EnterReturnsTrue))
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
            if (UiGui.SmallButton($"+##{label}_{i}"))
            {
                drafts.Insert(i + 1, string.Empty);
                commandTriggerDraftsDirty = true;
                SaveCommandTriggerDraftsIfDirty($"{label} insert {i + 2}");
            }

            ImGui.SameLine();
            if (UiGui.SmallButton($"-##{label}_{i}"))
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
        var useCharacterOverride = IsUsingCharacterCommandTriggerOverride();
        var activeProfile = plugin.ActiveMapGatherConfig;
        CopyCommandTriggerValues(
            useCharacterOverride
                ? activeProfile.GetLandingOrDutyCommandTriggers(configuration.LandingOrDutyCommandTriggers)
                : configuration.LandingOrDutyCommandTriggers,
            landingCommandTriggerDrafts);
        CopyCommandTriggerValues(
            useCharacterOverride
                ? activeProfile.GetFinishCommandTriggers(configuration.FinishCommandTriggers)
                : configuration.FinishCommandTriggers,
            finishCommandTriggerDrafts);
        commandTriggerDraftsDirty = false;
        commandTriggerDraftsInitialized = true;
        commandTriggerDraftsUseCharacterOverride = useCharacterOverride;
        commandTriggerDraftsCharacterKey = plugin.ActiveMapGatherCharacterKey;
    }

    private void SaveCommandTriggerDraftsIfDirty(string reason)
    {
        if (!commandTriggerDraftsDirty)
            return;

        if (IsUsingCharacterCommandTriggerOverride())
        {
            var activeProfile = plugin.ActiveMapGatherConfig;
            activeProfile.SetCommandTriggerOverride(
                true,
                configuration.LandingOrDutyCommandTriggers,
                configuration.FinishCommandTriggers);
            activeProfile.LandingOrDutyCommandTriggers ??= new List<string>();
            activeProfile.FinishCommandTriggers ??= new List<string>();
            ReplaceCommandTriggerValues(activeProfile.LandingOrDutyCommandTriggers, landingCommandTriggerDrafts);
            ReplaceCommandTriggerValues(activeProfile.FinishCommandTriggers, finishCommandTriggerDrafts);
            plugin.SaveActiveMapGatherConfig($"command triggers saved: {reason}");
            commandTriggerStatus = "Character command triggers saved.";
        }
        else
        {
            configuration.LandingOrDutyCommandTriggers ??= Configuration.CreateDefaultLandingOrDutyCommandTriggers();
            configuration.FinishCommandTriggers ??= Configuration.CreateDefaultFinishCommandTriggers();

            ReplaceCommandTriggerValues(configuration.LandingOrDutyCommandTriggers, landingCommandTriggerDrafts);
            ReplaceCommandTriggerValues(configuration.FinishCommandTriggers, finishCommandTriggerDrafts);
            configuration.Save();
            commandTriggerStatus = "Global command triggers saved.";
        }

        commandTriggerDraftsDirty = false;
        Plugin.Log.Information($"[ConfigWindow] Command triggers saved ({reason}).");
    }

    private bool IsUsingCharacterCommandTriggerOverride()
        => plugin.ActiveMapGatherContentId != 0 && plugin.ActiveMapGatherConfig.OverrideCommandTriggers;

    private bool CommandTriggerDraftSourceChanged()
        => commandTriggerDraftsInitialized &&
           (commandTriggerDraftsUseCharacterOverride != IsUsingCharacterCommandTriggerOverride() ||
            !string.Equals(commandTriggerDraftsCharacterKey, plugin.ActiveMapGatherCharacterKey, StringComparison.OrdinalIgnoreCase));

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
