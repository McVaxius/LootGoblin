using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Game.Gui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Internal;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.ImGuiMethods;
using ECommons.UIHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI;
using LootGoblin.IPC;
using LootGoblin.Models;
using LootGoblin.Services;
using AethertekUI;

namespace LootGoblin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
    private static readonly Vector4 ColorGreen = new(0.3f, 1f, 0.3f, 1f);
    private static readonly Vector4 ColorRed = new(1f, 0.3f, 0.3f, 1f);
    private static readonly Vector4 ColorYellow = new(1f, 1f, 0.3f, 1f);
    private static readonly Vector4 ColorGrey = new(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 ColorCyan = new(0.3f, 1f, 1f, 1f);
    private static readonly Vector4 ColorBlue = new(0.3f, 0.6f, 1f, 1f);
	private static readonly Vector4 ColorOrange = new(1f, 0.6f, 0f, 1f);

    private readonly Plugin plugin;
    private static readonly string MainTitle = $"Loot Goblin {typeof(MainWindow).Assembly.GetName().Version}";
    private Dictionary<uint, int> cachedMaps = new();
    private Dictionary<uint, MapSourceCount> cachedMapSources = new();
    private DateTime lastScanTime = DateTime.MinValue;
    private const double ScanCooldownSeconds = 2.0;
    private static readonly TimeSpan ManualMapRefreshSaddlebagTimeout = TimeSpan.FromSeconds(6);
    private bool manualMapRefreshPending;
    private bool manualMapRefreshOpenedSaddlebag;
    private DateTime manualMapRefreshStartedAt = DateTime.MinValue;
    private string manualMapRefreshStatus = string.Empty;

    public MainWindow(Plugin plugin)
        : base("Loot Goblin##MainWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        Size = new Vector2(1080, 950);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags |= ImGuiWindowFlags.HorizontalScrollbar;
    }

    public void Dispose() { }

    private bool DiagnosticsVisible => plugin.Configuration.DebugMode || plugin.Configuration.ShowDebugMapCompletion;

    public override void PreDraw()
    {
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
        => windowMotion.Restore(this);

    public override void Draw()
    {
        windowMotion.DrawChrome();
        UiGui.Title("Loot Goblin", MainTitle);
        using var controls = MaterialControls.Push(LootGoblinPresentation.Controls(plugin.Configuration.UiCompact ? 32 : 40));
        DrawHeaderSection();
        ImGui.Separator();
        DrawBotControlSection();
        DrawSummaryStrip();
        Panel("##MapQueuePanel", DrawMapInventorySection);
        if (ImGui.GetContentRegionAvail().X >= 800 * MaterialTheme.Metrics.Scale && ImGui.BeginTable("##LootGoblinRunParty", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            Panel("##CurrentRunPanel", DrawCurrentRunSection);
            ImGui.TableNextColumn();
            Panel("##PartyPanel", () => { DrawPartySection(); DrawDependencySection(); });
            ImGui.EndTable();
        }
        else
        {
            Panel("##CurrentRunPanel", DrawCurrentRunSection);
            Panel("##PartyPanel", () => { DrawPartySection(); DrawDependencySection(); });
        }
        Panel("##NavigationPanel", DrawNavigationSection);
        Panel("##CommandsPanel", DrawCommandsSection);
        DrawStatusSection();

        if (DiagnosticsVisible)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawMapCompletionSection();
        }

        if (DiagnosticsVisible)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawDebugLogSection();
        }
    }

    private uint windowRootId;
    private readonly Dictionary<string, float> panelHeights = new();
    private static float Scale(float value) => value * MaterialTheme.Metrics.Scale;
    private static void NextGroup(float width)
    {
        var right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= right) ImGui.SameLine();
    }
    private void Panel(string id, Action draw)
    {
        var c = MaterialTheme.Current.Colors;
        var padding = Scale(plugin.Configuration.UiCompact ? 10 : 14);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, c.Surface);
        ImGui.PushStyleColor(ImGuiCol.Border, c.OutlineVariant);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Scale(4));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding));
        // API 15's binding predates child auto-resize flags. Measure native content
        // for the next frame, retaining each panel's own draw list and original ID root.
        var height = panelHeights.GetValueOrDefault(id, Scale(id == "##MapQueuePanel" ? 300 : 220));
        var visible = ImGui.BeginChild(id, new Vector2(0, height), true, ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.HorizontalScrollbar);
        try
        {
            if (visible)
            {
                ImGuiP.PushOverrideID(windowRootId);
                try { draw(); }
                finally { ImGui.PopID(); }
                panelHeights[id] = Math.Max(padding * 2 + ImGui.GetTextLineHeight(), ImGui.GetCursorPosY() + ImGui.GetScrollY() + padding);
            }
        }
        finally { ImGui.EndChild(); ImGui.PopStyleVar(2); ImGui.PopStyleColor(2); }
        ImGui.Dummy(new Vector2(0, Scale(plugin.Configuration.UiCompact ? 8 : 12)));
    }

    private void DrawSummaryStrip()
    {
        var compact = plugin.Configuration.UiCompact;
        string[] labels = ["Bot State", "Party", "Food"];
        string[] values = [plugin.StateManager.State.ToString(),
            Plugin.PartyList.Length > 0 ? UiText.F("{0} members", Plugin.PartyList.Length) : UiText.T("Solo"),
            string.IsNullOrWhiteSpace(plugin.FoodService.FoodStatus) ? "Idle" : plugin.FoodService.FoodStatus];
        MaterialIcon[] icons = [MaterialIcon.Pulse, MaterialIcon.Group, MaterialIcon.Utensils];
        var available = ImGui.GetContentRegionAvail().X;
        var cellPadding = ImGui.GetStyle().CellPadding.X * 2;
        var fittedWidth = Math.Max(0, (available - labels.Length * cellPadding - 2) / labels.Length);
        var widths = labels.Select((label, index) => Math.Max(fittedWidth, LootGoblinPresentation.SummaryWidth(label, values[index], compact) - cellPadding)).ToArray();
        var height = Scale(compact ? 52 : 76) + (widths.Sum() + labels.Length * cellPadding > available ? ImGui.GetStyle().ScrollbarSize : 0);
        if (!ImGui.BeginTable("##LootGoblinSummary", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollX,
            new Vector2(0, height))) return;
        for (var column = 0; column < labels.Length; column++)
            ImGui.TableSetupColumn(labels[column], ImGuiTableColumnFlags.WidthFixed, widths[column]);
        for (var column = 0; column < labels.Length; column++)
        {
            ImGui.TableNextColumn();
            LootGoblinPresentation.Summary(labels[column], values[column], icons[column], compact);
        }
        ImGui.EndTable();
        ImGui.Separator();
    }

    private void DrawHeaderSection()
    {
        windowRootId = ImGui.GetID("");
        var compact = plugin.Configuration.UiCompact;
        LootGoblinPresentation.Chest(ImGui.GetCursorScreenPos(), Scale(compact ? 34 : 44), MaterialTheme.Current.Colors.Primary);
        ImGui.Dummy(new Vector2(Scale(compact ? 38 : 48), Scale(compact ? 34 : 44)));
        ImGui.SameLine();
        ImGui.BeginGroup();
        using (UiText.Font(compact ? UiFontRole.PluginName : UiFontRole.Title)) MaterialText.Text("Loot Goblin");
        UiGui.TextDisabled("Automate your treasure map adventures.");
        ImGui.EndGroup();
        NextGroup(UiGui.ButtonWidth("\u2661 Ko-fi \u2661", MaterialIcon.Heart, UiText.T("Support development on Ko-fi")));
        if (UiGui.Button("\u2661 Ko-fi \u2661", new Vector2(0, Scale(compact ? 32 : 40)), MaterialIcon.Heart, UiText.T("Support development on Ko-fi")))
        {
            System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = "https://ko-fi.com/mcvaxius",
                UseShellExecute = true
            });
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("Support development on Ko-fi");
        if (plugin.Configuration.UiCompactVisibleOnMainWindow)
        {
            NextGroup(Scale(42));
            using (MaterialControls.Push(LootGoblinPresentation.Controls(22))) plugin.DrawCompactSelector();
        }
        if (plugin.Configuration.UiLanguageVisibleOnMainWindow)
        { NextGroup(Scale(190)); plugin.DrawLanguageSelector(); }
        NextGroup(UiGui.CheckboxWidth("Transparency")); plugin.DrawTransparencyToggle();
    }

    private void DrawCompactWarnings()
    {
        var warnings = new List<string>();
        if (!Plugin.ClientState.IsLoggedIn)
            warnings.Add("not logged in");
        if (!plugin.VNavIPC.IsAvailable)
            warnings.Add("vnavmesh missing");
        if (!plugin.IsLifestreamAvailable)
            warnings.Add("Lifestream missing");
        if (plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver && !plugin.IsAdsAvailable)
            warnings.Add("ADS missing");
        if (!string.IsNullOrWhiteSpace(plugin.StateManager.WarningMessage))
            warnings.Add(plugin.StateManager.WarningMessage);

        if (warnings.Count == 0)
        {
            var origin = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            var diameter = Scale(plugin.Configuration.UiCompact ? 30 : 38);
            draw.AddCircleFilled(origin + new Vector2(diameter * .5f), diameter * .5f, ImGui.GetColorU32(new Vector4(ColorGreen.X, ColorGreen.Y, ColorGreen.Z, .2f)), 32);
            draw.AddCircleFilled(origin + new Vector2(diameter * .5f), diameter * .36f, ImGui.GetColorU32(ColorGreen), 32);
            ImGui.Dummy(new Vector2(diameter, diameter)); ImGui.SameLine();
            ImGui.BeginGroup();
            using (UiText.Font(UiFontRole.Counter)) UiGui.TextColored(MaterialTheme.Current.Colors.OnSurface, "Ready");
            UiGui.TextDisabled("Plugin loaded and ready.");
            ImGui.EndGroup();
            return;
        }

        UiGui.TextColored(ColorYellow, UiText.F($"Attention: {string.Join(" | ", warnings.Select(UiText.T))}"));
    }

    private void DrawStatusSection()
    {
        if (!UiGui.CollapsingHeader("Status"))
            return;

        var enabled = plugin.Configuration.Enabled;
        var statusText = enabled ? "ENABLED" : "DISABLED";
        var statusColor = enabled ? ColorGreen : ColorRed;

        UiGui.Text("Status: ");
        ImGui.SameLine();
        UiGui.TextColored(statusColor, statusText);

        ImGui.SameLine();
        UiGui.Text("  |  Bot State: ");
        ImGui.SameLine();
        var navState = plugin.NavigationService.State;
        var navColor = navState == NavigationState.Error ? ColorRed :
                       navState == NavigationState.Idle ? ColorYellow : ColorCyan;
        UiGui.TextColored(navColor, navState.ToString());

        var loggedIn = Plugin.ClientState.IsLoggedIn;
        UiGui.Text("Logged In: ");
        ImGui.SameLine();
        UiGui.TextColored(loggedIn ? ColorGreen : ColorRed, loggedIn ? "Yes" : "No");

        if (!string.IsNullOrWhiteSpace(plugin.StateManager.WarningMessage))
        {
            UiGui.TextColored(ColorRed, plugin.StateManager.WarningMessage);
        }

        if (loggedIn)
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player != null)
            {
                var playerName = plugin.Configuration.KrangleNames ? KrangleService.KrangleName(player.Name.TextValue) : player.Name.TextValue;
                var serverName = plugin.Configuration.KrangleNames ? KrangleService.KrangleServer(player.HomeWorld.Value.Name.ToString()) : player.HomeWorld.Value.Name.ToString();
                ImGui.SameLine();
                UiGui.Text(UiText.F($"  |  {playerName} @ {serverName}"));
            }
        }

        var partyCount = Plugin.PartyList.Length;
        UiGui.Text("Party: ");
        ImGui.SameLine();
        UiGui.Text(partyCount > 0 ? UiText.F("{0} members", partyCount) : "Solo");

        var foodStatus = plugin.FoodService.FoodStatus;
        if (!string.IsNullOrWhiteSpace(foodStatus))
        {
            var foodColor =
                foodStatus.StartsWith("Well Fed", StringComparison.OrdinalIgnoreCase) ||
                foodStatus.StartsWith("Ate ", StringComparison.OrdinalIgnoreCase)
                    ? ColorGreen
                    : foodStatus.StartsWith("Paused", StringComparison.OrdinalIgnoreCase) ||
                      foodStatus.StartsWith("Bot disabled", StringComparison.OrdinalIgnoreCase)
                        ? ColorGrey
                        : foodStatus.StartsWith("Failed", StringComparison.OrdinalIgnoreCase) ||
                          foodStatus.StartsWith("Out of", StringComparison.OrdinalIgnoreCase) ||
                          foodStatus.StartsWith("No food", StringComparison.OrdinalIgnoreCase)
                            ? ColorRed
                            : ColorYellow;

            UiGui.Text("Food: ");
            ImGui.SameLine();
            UiGui.TextColored(foodColor, foodStatus);
        }

        // Summon Chocobo status
        if (plugin.Configuration.SummonChocobo && loggedIn)
        {
            var buddyTime = GameHelpers.GetBuddyTimeRemaining();
            var greensCount = GameHelpers.GetInventoryItemCount(GameHelpers.GysahlGreensItemId);
            var mins = (int)(buddyTime / 60);
            var secs = (int)(buddyTime % 60);
            var timerText = buddyTime > 0 ? UiText.F("{0}m{1:D2}s", mins, secs) : "Not summoned";
            var timerColor = buddyTime > 900 ? ColorGreen : buddyTime > 0 ? ColorYellow : ColorGrey;
            var greensColor = greensCount > 0 ? ColorGreen : ColorRed;

            UiGui.Text("Chocobo: ");
            ImGui.SameLine();
            UiGui.TextColored(timerColor, timerText);
            ImGui.SameLine();
            UiGui.TextColored(greensColor, UiText.F($"  |  Gysahl Greens: {greensCount}"));
        }

        DrawBossModDangerStatusLine();
    }

    private void DrawBossModDangerStatusLine()
    {
        var rotation = plugin.RotationPluginIPC;
        var moduleName = string.IsNullOrWhiteSpace(rotation.BmrActiveModuleName)
            ? string.Empty
            : $" ({rotation.BmrActiveModuleName})";
        var suppressionActive = plugin.StateManager.BossModOutdoorSuppressionActive;
        var suppressionReason = plugin.StateManager.BossModOutdoorSuppressionReason;
        var dangerColor = rotation.BossModDangerDetected ? ColorYellow : ColorGrey;
        var suppressionColor = suppressionActive ? ColorYellow : ColorGrey;

        UiGui.Text("BossMod danger: ");
        ImGui.SameLine();
        UiGui.TextColored(dangerColor, UiText.F($"BMR active module: {UiText.T(rotation.BmrHasActiveModule ? "yes" : "no")}{moduleName}"));
        ImGui.SameLine();
        UiGui.TextColored(dangerColor, UiText.F($"  |  VBM forbidden zones: {rotation.VbmForbiddenZonesCount}"));
        ImGui.SameLine();
        UiGui.TextColored(suppressionColor, UiText.F($"  |  Outdoor suppression: {UiText.T(suppressionActive ? "on" : "off")}"));
        if (!string.IsNullOrWhiteSpace(suppressionReason) && suppressionReason != "off")
        {
            ImGui.SameLine();
            UiGui.TextColored(ColorGrey, UiText.F($"({suppressionReason})"));
        }
    }

    private void DrawMapInventorySection()
    {
        if (UiGui.CollapsingHeader("Map Queue", ImGuiTreeNodeFlags.DefaultOpen, MaterialIcon.List))
        {
            var autoStartNextMap = plugin.Configuration.AutoStartNextMap;
            if (UiGui.Checkbox("Auto Start next map", ref autoStartNextMap))
            {
                plugin.Configuration.AutoStartNextMap = autoStartNextMap;
                plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                UiGui.SetTooltip("Automatically starts the next runnable selected map after completing one.");

            ImGui.SameLine();
            var showAllKnownMapTypes = plugin.Configuration.ShowAllKnownMapTypes;
            if (UiGui.Checkbox("Show all map types##MapQueueShowAllKnownMapTypes", ref showAllKnownMapTypes))
            {
                plugin.Configuration.ShowAllKnownMapTypes = showAllKnownMapTypes;
                plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                UiGui.SetTooltip("Shows runnable map rows even when none are currently in inventory or loaded saddlebags.");

            ImGui.Spacing();

            if (Plugin.ClientState.IsLoggedIn)
            {
                TickManualMapRefresh();

                var now = DateTime.Now;
                if (!manualMapRefreshPending && (now - lastScanTime).TotalSeconds >= ScanCooldownSeconds)
                {
                    RefreshMapSourceCache(includeRetainers: false, refreshXaDatabase: false);
                }

                var displayedMapSources = GetDisplayedMapSources();
                var mapAllowanceStatus = plugin.MapAllowanceService.GetStatus();
                var hasGatherJob = plugin.SelectedGatherJobId != 0;

                DrawMapAllowanceHeader(mapAllowanceStatus, hasGatherJob, plugin.Configuration.DebugMode);
                ImGui.Spacing();

                if (displayedMapSources.Count == 0)
                {
                    var sourceText = plugin.Configuration.EnableSaddlebagMapRetrieval
                        ? "inventory or loaded saddlebags"
                        : "inventory";
                    UiGui.TextColored(ColorGrey, UiText.F($"  No treasure maps found in {UiText.T(sourceText)}."));
                }
                else
                {
                    var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();

                    // Show warning if multiple map types detected
                    if (displayedMapSources.Count > 1)
                    {
                        UiGui.TextColored(ColorGrey, "  Multiple map types detected - use checkboxes to select which to run");
                        ImGui.Spacing();
                    }

                    // Sort entries lowest MinLevel first (matches StateManager selection order)
                    var sortedMaps = displayedMapSources
                        .OrderBy(kvp => LootGoblin.Models.TreasureMapData.KnownMaps.TryGetValue(kvp.Key, out var i) ? i.MinLevel : 999)
                        .ToList();

                    UiGui.TextColored(ColorGrey, "  Checked maps run. Use max for unlimited runs or a number for finite runs.");
                    ImGui.Spacing();
                    var mapRoot = ImGui.GetID("");
                    var rowHeight = Scale(plugin.Configuration.UiCompact ? 36 : 44);
                    using var queueRows = new MaterialStyleScope();
                    queueRows.Style(ImGuiStyleVar.CellPadding, new Vector2(ImGui.GetStyle().CellPadding.X, Scale(2)));
                    var columnWidths = LootGoblinPresentation.QueueWidths(sortedMaps.Select(entry =>
                        itemSheet?.GetRow(entry.Key).Name.ToString()
                        ?? (TreasureMapData.KnownMaps.TryGetValue(entry.Key, out var info) ? info.Name : UiText.F("Unknown Map (ID: {0})", entry.Key))));
                    if (ImGui.BeginTable("##LootGoblinMapQueue", 9, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersInnerH
                            | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit,
                            new Vector2(0, Scale(38) + Math.Min(Scale(320), sortedMaps.Count * rowHeight) + ImGui.GetStyle().ScrollbarSize)))
                    {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    string[] columnLabels = ["Map", "Enabled", "Run count", "Inventory", "Saddlebag", "Retainer", "Gather", "Buy", "Max gil"];
                    for (var column = 0; column < columnLabels.Length; column++)
                    {
                        ImGui.TableSetupColumn(columnLabels[column], ImGuiTableColumnFlags.WidthFixed, columnWidths[column]);
                        UiGui.EnsureColumnMinimum(column, columnWidths[column]);
                    }
                    using (UiText.Font(UiFontRole.BodyStrong)) UiGui.TableHeadersRow(Scale(38));
                    ImGuiP.PushOverrideID(mapRoot);
                    try
                    {
                    foreach (var kvp in sortedMaps)
                    {
                        var itemId = kvp.Key;
                        var quantity = kvp.Value.Total;
                        
                        var item = itemSheet?.GetRow(itemId);
                        var itemName = item?.Name.ToString();
                        if (string.IsNullOrEmpty(itemName) &&
                            LootGoblin.Models.TreasureMapData.KnownMaps.TryGetValue(itemId, out var mapInfo))
                        {
                            itemName = mapInfo.Name;
                        }
                        if (string.IsNullOrEmpty(itemName))
                            itemName = UiText.F("Unknown Map (ID: {0})", itemId);
                        
                        var desc = item?.Description.ToString() ?? "";
                        var (mapTier, mapLevel) = ParseMapTierAndLevel(desc);
                        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
                        ImGui.TableNextColumn();
                        using (UiText.Font(UiFontRole.BodyStrong)) MaterialText.Text(itemName);
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip();
                            MaterialText.Text(itemName);
                            if (mapTier > 0) MaterialText.Text(UiText.F("Tier {0}", mapTier));
                            if (mapLevel > 0) MaterialText.Text(UiText.F("(Lvl {0})", mapLevel));
                            if (!string.IsNullOrEmpty(desc)) MaterialText.TextWrapped(desc);
                            ImGui.EndTooltip();
                        }
                        ImGui.TableNextColumn();
                        var isEnabled = plugin.Configuration.IsMapTypeEnabled(itemId);
                        if (UiGui.Checkbox($"##map_{itemId}", ref isEnabled))
                        {
                            plugin.Configuration.SetMapTypeEnabled(itemId, isEnabled, TreasureMapData.AllMapItemIds);
                            plugin.Configuration.Save();
                        }
                        ImGui.TableNextColumn();
                        DrawMapRunCountEditor(itemId, isEnabled);
                        ImGui.TableNextColumn(); MaterialText.Text(UiText.F("{0}", kvp.Value.Inventory));
                        ImGui.TableNextColumn();
                        var combinedSaddlebag = kvp.Value.Saddlebag + kvp.Value.PremiumSaddlebag;
                        MaterialText.Text(UiText.F("{0}", combinedSaddlebag));
                        if (ImGui.IsItemHovered())
                            MaterialText.SetTooltip(UiText.F("Saddlebag includes regular ({0}) + premium ({1}) saddlebag counts.", kvp.Value.Saddlebag, kvp.Value.PremiumSaddlebag));
                        ImGui.TableNextColumn(); MaterialText.Text(UiText.F("{0}", kvp.Value.Retainer));
                        ImGui.TableNextColumn();
                        DrawMapGatherCheckbox(itemId, mapAllowanceStatus);
                        ImGui.TableNextColumn();
                        var isMarketable = item is { } itemRow && itemRow.ItemSearchCategory.RowId != 0;
                        if (isMarketable)
                        {
                            DrawMapPurchaseControls(itemId, tableColumns: true);
                        }
                        else
                        {
                            MaterialText.TextDisabled("—"); ImGui.TableNextColumn(); MaterialText.TextDisabled("—");
                        }
                    }
                    }
                    finally { ImGui.PopID(); ImGui.EndTable(); }
                    }
                }

                ImGui.Spacing();
                if (manualMapRefreshPending)
                    ImGui.BeginDisabled();
                if (UiGui.Button("Refresh Maps", new Vector2(0, 0), MaterialIcon.Refresh))
                {
                    StartManualMapRefresh();
                }
                if (manualMapRefreshPending)
                    ImGui.EndDisabled();
                if (!string.IsNullOrWhiteSpace(manualMapRefreshStatus))
                {
                    ImGui.SameLine();
                    UiGui.TextColored(ColorGrey, manualMapRefreshStatus);
                }
                
                // Debug button to read decipher menu indices
                if (plugin.Configuration.ShowDebugMapCompletion && cachedMaps.Count > 0)
                {
                    ImGui.Spacing();
                    if (UiGui.Button("[READ MAP INDICES]"))
                    {
                        ReadMapIndicesFromDecipherMenu();
                    }
                    if (ImGui.IsItemHovered())
                    {
                        UiGui.SetTooltip("Opens decipher menu and reads all map entries to show correct indices");
                    }
                }
            }
            else
            {
                UiGui.TextColored(ColorGrey, "  Log in to scan inventory.");
            }
        }
    }

    private static void DrawMapAllowanceHeader(MapAllowanceStatus status, bool hasGatherJob, bool showUnavailableReason)
    {
        var header = MapAllowanceHeaderPolicy.Evaluate(status, hasGatherJob, showUnavailableReason);
        switch (header.Kind)
        {
            case MapAllowanceHeaderKind.Cooldown:
                UiGui.TextColored(ColorYellow, UiText.F($"  {header.PrimaryText}"));
                if (header.ShowLegend)
                {
                    UiGui.TextColored(ColorGrey, UiText.F($"  {MapAllowanceHeaderPolicy.LegendLineOne}"));
                    UiGui.TextColored(ColorGrey, UiText.F($"  {MapAllowanceHeaderPolicy.LegendLineTwo}"));
                }
                break;

            case MapAllowanceHeaderKind.Ready:
                UiGui.TextColored(ColorGreen, UiText.F($"  {header.PrimaryText}"));
                break;

            case MapAllowanceHeaderKind.Unavailable:
                UiGui.TextColored(ColorGrey, UiText.F($"  {header.PrimaryText}"));
                break;
        }
    }

    private void DrawMapGatherCheckbox(uint itemId, MapAllowanceStatus mapAllowanceStatus)
    {
        var hasGatherJob = plugin.SelectedGatherJobId != 0;
        var isKnownMap = TreasureMapData.KnownMaps.TryGetValue(itemId, out var mapInfo);
        var isGatherable = isKnownMap && mapInfo!.IsGatherable;
        var gatherEnabled = plugin.IsMapGatherEnabled(itemId);
        var state = MapGatherIconPolicy.Evaluate(new MapGatherIconInput(
            isKnownMap,
            isGatherable,
            hasGatherJob,
            gatherEnabled,
            mapAllowanceStatus.IsAvailable,
            mapAllowanceStatus.IsReady,
            mapAllowanceStatus.CompactText));
        gatherEnabled = state.GatherEnabled;

        var icon = state.Icon == MapGatherIconKind.Unavailable
            ? FontAwesomeIcon.Times
            : FontAwesomeIcon.Seedling;
        var enabledColor = state.Icon == MapGatherIconKind.Unavailable ? ColorBlue : ColorGreen;
        var disabledColor = state.Icon == MapGatherIconKind.Unavailable ? ColorBlue : ColorGrey;

        if (ImGuiEx.Checkbox(
                icon,
                enabledColor,
                disabledColor,
                null,
                null,
                $"##gather_{itemId}",
                ref gatherEnabled,
                state.IsInteractive))
        {
            plugin.SetMapGatherEnabled(itemId, gatherEnabled);
        }

        if (state.ShowCooldownOverlay)
        {
            var itemMin = ImGui.GetItemRectMin();
            var itemMax = ImGui.GetItemRectMax();
            var drawList = ImGui.GetWindowDrawList();
            var overlayColor = ImGui.GetColorU32(ColorRed);
            drawList.AddLine(itemMin, itemMax, overlayColor, 2f);
            drawList.AddLine(
                new Vector2(itemMin.X, itemMax.Y),
                new Vector2(itemMax.X, itemMin.Y),
                overlayColor,
                2f);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            DrawMapGatherTooltip(state);
    }

    private static void DrawMapGatherTooltip(MapGatherIconState state)
    {
        if (state.Icon != MapGatherIconKind.Seedling)
        {
            UiGui.SetTooltip(state.Tooltip);
            return;
        }

        ImGui.BeginTooltip();
        UiGui.TextUnformatted(state.Tooltip);
        UiGui.TextColored(ColorGrey, "Seedling: toggle missing-map gathering");
        UiGui.TextColored(ColorGrey, "Blue X: unavailable through gathering");
        UiGui.TextColored(ColorGrey, "Red X overlay: allowance cooldown");
        ImGui.EndTooltip();
    }

    private void DrawMapPurchaseControls(uint itemId, bool tableColumns = false)
    {
        if (!plugin.EmptorIPC.IsAvailable)
        {
            var openMarketboardSettings = false;
            if (ImGuiEx.Checkbox(
                    FontAwesomeIcon.ShoppingCart,
                    ColorGrey,
                    ColorGrey,
                    null,
                    null,
                    $"##purchase_unavailable_{itemId}",
                    ref openMarketboardSettings,
                    true))
            {
                plugin.OpenMarketboardSettings();
            }

            var itemMin = ImGui.GetItemRectMin();
            var itemMax = ImGui.GetItemRectMax();
            var drawList = ImGui.GetWindowDrawList();
            var overlayColor = ImGui.GetColorU32(ColorRed);
            drawList.AddLine(itemMin, itemMax, overlayColor, 2f);
            drawList.AddLine(
                new Vector2(itemMin.X, itemMax.Y),
                new Vector2(itemMax.X, itemMin.Y),
                overlayColor,
                2f);

            if (ImGui.IsItemHovered())
                UiGui.SetTooltip("Emptor is unavailable. Open Marketboard settings for installation guidance.");
            if (tableColumns) { ImGui.TableNextColumn(); MaterialText.TextDisabled("—"); }
            return;
        }

        var gilCap = plugin.Configuration.GetMapPurchaseGilCap(itemId);
        var purchaseEnabled = plugin.Configuration.IsMapPurchaseEnabled(itemId);
        var canEnable = gilCap > 0;

        if (ImGuiEx.Checkbox(
                FontAwesomeIcon.ShoppingCart,
                ColorGreen,
                ColorGrey,
                null,
                null,
                $"##purchase_{itemId}",
                ref purchaseEnabled,
                canEnable))
        {
            plugin.Configuration.SetMapPurchaseEnabled(itemId, purchaseEnabled);
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiGui.SetTooltip(canEnable
                ? "Cart: buy missing maps through Emptor after gathering is unavailable. A market trip may prepare up to three, capped by remaining runs."
                : "Set a positive maximum gil price before enabling this cart.");
        }

        if (tableColumns) ImGui.TableNextColumn(); else ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Min(90f * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (UiGui.InputInt($"##purchase_cap_{itemId}", ref gilCap, 0, 0))
        {
            plugin.Configuration.SetMapPurchaseGilCap(itemId, gilCap);
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            DrawMapPriceCeilingTooltip(itemId);

        if (plugin.EmptorIPC.TryGetPriceSnapshot(itemId, out var snapshot) &&
            snapshot.HasPositiveHint &&
            snapshot.NqMinimumListing is { } priceHint)
        {
            ImGui.SameLine();
            if (UiGui.SmallButton($"Use##purchase_hint_{itemId}"))
            {
                plugin.Configuration.SetMapPurchaseGilCap(itemId, (int)priceHint);
                plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                UiGui.SetTooltip("Copy this session's positive NQ minimum-listing hint into the ceiling. This does not enable the cart.");
        }
    }

    private void DrawMapPriceCeilingTooltip(uint itemId)
    {
        var emptor = plugin.EmptorIPC;
        var scope = emptor.LastPriceLookupScope ?? plugin.GetEmptorPriceLookupScope();
        var hasSnapshot = emptor.TryGetPriceSnapshot(itemId, out var snapshot);

        ImGui.BeginTooltip();
        UiGui.TextUnformatted("Maximum gil for one map. Zero disables purchasing for this map.");
        ImGui.Separator();

        if (hasSnapshot && snapshot.NqMinimumListing is > 0)
            UiGui.TextUnformatted(UiText.F($"Emptor NQ minimum listing: {snapshot.NqMinimumListing:N0} gil"));
        else
            UiGui.TextUnformatted("Emptor NQ minimum listing: unavailable");

        UiGui.TextUnformatted(UiText.F("World: {0}", hasSnapshot && !string.IsNullOrWhiteSpace(snapshot.World) ? snapshot.World : UiText.T("unavailable")));
        UiGui.TextUnformatted(UiText.F("Location: {0}", hasSnapshot && !string.IsNullOrWhiteSpace(snapshot.Location) ? snapshot.Location : UiText.T("unavailable")));
        UiGui.TextUnformatted(UiText.F("Age: {0}", hasSnapshot && !string.IsNullOrWhiteSpace(snapshot.Age) ? snapshot.Age : UiText.T("not reported")));
        UiGui.TextUnformatted(UiText.F("Lookup scope: {0}", UiText.T(EmptorIPC.GetScopeLabel(hasSnapshot ? snapshot.Scope : scope))));

        var unavailableReason = hasSnapshot ? snapshot.Error : emptor.PriceStatusText;
        if (!string.IsNullOrWhiteSpace(unavailableReason) && (!hasSnapshot || !snapshot.HasPositiveHint))
            UiGui.TextColored(ColorYellow, unavailableReason);

        ImGui.Spacing();
        UiGui.TextColored(ColorGrey, "Price hints live only for this Loot Goblin session; they are not saved to disk.");
        UiGui.TextColored(ColorGrey, "Listings can change after lookup. Refresh is manual and rate-limited to five minutes.");
        ImGui.EndTooltip();
    }

    private void DrawMapRunCountEditor(uint itemId, bool isEnabled)
    {
        var runCount = plugin.Configuration.GetMapRunCount(itemId);

        ImGui.SetNextItemWidth(Math.Min(60f * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
        if (isEnabled && runCount == Configuration.MapRunCountMax)
        {
            var maxText = "max";
            ImGui.BeginDisabled();
            UiGui.InputText($"##map_count_{itemId}", ref maxText, 8);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                UiGui.SetTooltip("Runs this map type until no available maps remain.");
            return;
        }

        var editableCount = Math.Max(0, runCount);
        if (UiGui.InputInt($"##map_count_{itemId}", ref editableCount))
        {
            plugin.Configuration.SetMapRunCount(itemId, editableCount);
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            UiGui.SetTooltip("0 disables this map type. Positive numbers run that many resolved maps.");

        runCount = plugin.Configuration.GetMapRunCount(itemId);
        if (runCount > 0 && runCount != Configuration.MapRunCountMax)
        {
            ImGui.SameLine();
            if (UiGui.SmallButton($"Max##map_max_{itemId}"))
            {
                plugin.Configuration.SetMapRunCountToMax(itemId);
                plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                UiGui.SetTooltip("Switch this map type back to unlimited runs.");
        }
    }

    private void RefreshMapSourceCache(bool includeRetainers, bool refreshXaDatabase)
    {
        var previousRetainerCounts = cachedMapSources
            .Where(kvp => kvp.Value.Retainer > 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Retainer);

        cachedMapSources = plugin.InventoryService.ScanForMapSources(
            includeSaddlebags: plugin.Configuration.EnableSaddlebagMapRetrieval);

        if (!includeRetainers)
        {
            foreach (var kvp in previousRetainerCounts)
            {
                if (!cachedMapSources.TryGetValue(kvp.Key, out var count))
                {
                    count = new MapSourceCount();
                    cachedMapSources[kvp.Key] = count;
                }

                count.Retainer = kvp.Value;
            }
        }
        else
        {
            if (!plugin.IsXaDatabaseAvailable)
            {
                plugin.RetainerMapRetrievalService.ClearUnavailableXaDatabaseState();
                plugin.AddDebugLog("[MapRefresh] XADB unavailable; skipped retainer count refresh.");
            }
            else
            {
                var mapIds = plugin.Configuration.GetRunnableMapIds(TreasureMapData.AllMapItemIds);
                var retainerCounts = plugin.RetainerMapRetrievalService.GetRetainerMapCounts(mapIds, refreshXaDatabase);
                foreach (var kvp in retainerCounts)
                {
                    if (!cachedMapSources.TryGetValue(kvp.Key, out var count))
                    {
                        count = new MapSourceCount();
                        cachedMapSources[kvp.Key] = count;
                    }

                    count.Retainer = kvp.Value;
                }
            }
        }

        cachedMaps = cachedMapSources
            .Where(kvp => kvp.Value.Inventory > 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Inventory);
        lastScanTime = DateTime.Now;
    }

    private void StartManualMapRefresh()
    {
        if (manualMapRefreshPending)
            return;

        manualMapRefreshPending = true;
        manualMapRefreshOpenedSaddlebag = false;
        manualMapRefreshStartedAt = DateTime.Now;
        manualMapRefreshStatus = "refreshing...";

        if (!plugin.Configuration.EnableSaddlebagMapRetrieval)
        {
            CompleteManualMapRefresh(closeSaddlebagAfterScan: false, "Manual map refresh without saddlebag retrieval.");
            return;
        }

        if (GameHelpers.IsAddonVisible("InventoryBuddy"))
        {
            CompleteManualMapRefresh(closeSaddlebagAfterScan: false, "Manual map refresh with saddlebag already open.");
            return;
        }

        CommandHelper.SendCommand("/saddlebag");
        manualMapRefreshOpenedSaddlebag = true;
        plugin.AddDebugLog("[MapRefresh] Opened saddlebag for manual map refresh.");
    }

    private void TickManualMapRefresh()
    {
        if (!manualMapRefreshPending)
            return;

        if (!plugin.Configuration.EnableSaddlebagMapRetrieval)
        {
            CompleteManualMapRefresh(closeSaddlebagAfterScan: false, "Manual map refresh completed after saddlebag retrieval was disabled.");
            return;
        }

        if (GameHelpers.IsAddonVisible("InventoryBuddy"))
        {
            CompleteManualMapRefresh(closeSaddlebagAfterScan: manualMapRefreshOpenedSaddlebag, "Manual map refresh after saddlebag opened.");
            return;
        }

        if (DateTime.Now - manualMapRefreshStartedAt < ManualMapRefreshSaddlebagTimeout)
            return;

        plugin.AddDebugLog("[MapRefresh] Saddlebag did not become visible before timeout; scanning loaded containers anyway.");
        CompleteManualMapRefresh(closeSaddlebagAfterScan: false, "Manual map refresh after saddlebag timeout.");
    }

    private void CompleteManualMapRefresh(bool closeSaddlebagAfterScan, string logMessage)
    {
        RefreshMapSourceCache(includeRetainers: true, refreshXaDatabase: true);

        if (closeSaddlebagAfterScan && GameHelpers.IsAddonVisible("InventoryBuddy"))
        {
            GameHelpers.CloseCurrentAddon();
            plugin.AddDebugLog("[MapRefresh] Closed saddlebag after manual refresh.");
        }

        manualMapRefreshPending = false;
        manualMapRefreshOpenedSaddlebag = false;
        manualMapRefreshStatus = "refreshed";
        plugin.AddDebugLog(logMessage);
    }

    private Dictionary<uint, MapSourceCount> GetDisplayedMapSources()
    {
        var sources = cachedMapSources.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        foreach (var mapId in plugin.ActiveGatherEnabledMapTypes)
        {
            if (!sources.ContainsKey(mapId))
                sources[mapId] = new MapSourceCount();
        }

        foreach (var mapId in plugin.Configuration.PurchaseEnabledMapTypes ?? new List<uint>())
        {
            if (!sources.ContainsKey(mapId))
                sources[mapId] = new MapSourceCount();
        }

        if (!plugin.Configuration.ShowAllKnownMapTypes)
            return sources;

        foreach (var mapId in TreasureMapData.KnownMaps.Keys)
        {
            if (!sources.ContainsKey(mapId))
                sources[mapId] = new MapSourceCount();
        }

        return sources;
    }

    private void DrawMapCompletionSection()
    {
        if (UiGui.CollapsingHeader("Location Data"))
        {
            var maps = TreasureMapData.KnownMaps.Values
                .OrderBy(m => m.MinLevel)
                .ThenBy(m => m.Name)
                .ToList();

            // === Implementation Summary ===
            var implemented = maps.Count(m => m.Status == ImplementationStatus.Implemented);
            var wip = maps.Count(m => m.Status == ImplementationStatus.WIP);
            var notStarted = maps.Count(m => m.Status == ImplementationStatus.NotStarted);
            UiGui.Text(UiText.F($"  Maps: {maps.Count}  "));
            ImGui.SameLine();
            UiGui.TextColored(ColorGreen, UiText.F($"Done: {implemented}"));
            ImGui.SameLine();
            UiGui.TextColored(ColorYellow, UiText.F($"  WIP: {wip}"));
            ImGui.SameLine();
            if (notStarted > 0)
                UiGui.TextColored(ColorGrey, UiText.F($"  Not Started: {notStarted}"));

            // === Location Database Summary ===
            var db = plugin.MapLocationDatabase;
            UiGui.Text(UiText.F($"  Locations: {db.TotalLocations} total  "));
            ImGui.SameLine();
            UiGui.TextColored(ColorGreen, UiText.F($"Resolved: {db.ResolvedLocations}"));
            ImGui.SameLine();
            UiGui.TextColored(ColorGrey, UiText.F($"  Missing: {db.TotalLocations - db.ResolvedLocations}"));

            UiGui.Text(UiText.F($"  Community: {db.CommunityEntries.Count}  "));
            ImGui.SameLine();
            UiGui.Text(UiText.F($"User: {db.UserEntries.Count}  "));
            ImGui.SameLine();
            UiGui.Text(UiText.F($"TreasureSpot: {db.TreasureSpotEntries.Count}"));

            // === Aetheryte Position Database Summary ===
            var aethDb = plugin.AetherytePositionDatabase;
            if (Plugin.ClientState.IsLoggedIn)
            {
                var totalUnlocked = aethDb.GetTotalUnlockedCount();
                var recorded = aethDb.Count;
                var missing = totalUnlocked - recorded;
                UiGui.Text(UiText.F($"  Aetherytes: {recorded}/{totalUnlocked} positions stored  "));
                if (missing > 0)
                {
                    ImGui.SameLine();
                    UiGui.TextColored(ColorYellow, UiText.F($"({missing} missing)"));
                }
                else if (totalUnlocked > 0)
                {
                    ImGui.SameLine();
                    UiGui.TextColored(ColorGreen, "(all recorded)");
                }
            }
            else
            {
                UiGui.Text(UiText.F($"  Aetherytes: {aethDb.Count} positions stored"));
            }

            var userOnly = db.UserOnlyResolved;
            if (userOnly > 0)
            {
                UiGui.TextColored(ColorCyan, UiText.F($"  ★ You have {userOnly} location(s) not in community DB - consider sharing!"));
                ImGui.SameLine();
                if (UiGui.SmallButton("Open Data Folder"))
                {
                    try
                    {
                        System.Diagnostics.Process.Start("explorer.exe", aethDb.ConfigDirectory);
                    }
                    catch { }
                }
            }

            ImGui.Spacing();

            // === Cycling Mode Controls ===
            if (Plugin.ClientState.IsLoggedIn)
            {
                var sm = plugin.StateManager;
                var isBusy = sm.State != BotState.Idle && sm.State != BotState.Error && sm.State != BotState.Completed;

                if (sm.State == BotState.CyclingAetherytes || sm.State == BotState.CyclingMapLocations)
                {
                    UiGui.TextColored(ColorCyan, UiText.F($"  {sm.StateDetail}"));

                    // XYZ diff display during cycling
                    if (sm.State == BotState.CyclingMapLocations && sm.CurrentLocation != null)
                    {
                        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero;
                        var dx = playerPos.X - sm.CurrentLocation.X;
                        var dy = playerPos.Y - sm.CurrentLocation.Y;
                        var dz = playerPos.Z - sm.CurrentLocation.Z;
                        UiGui.TextColored(ColorGrey, UiText.F($"  Diff: X={dx:F1} Y={dy:F1} Z={dz:F1}  Dist={Math.Sqrt(dx*dx+dz*dz):F0}y"));
                    }

                    if (UiGui.Button("Stop Cycling"))
                    {
                        sm.Stop("main-window:stop-cycling");
                    }

                    // Manual control buttons during XYZ cycling
                    if (sm.State == BotState.CyclingMapLocations)
                    {
                        ImGui.SameLine();
                        if (sm.CycleManualControl)
                        {
                            if (UiGui.Button("Mark This Spot"))
                            {
                                sm.CycleMarkThisSpot();
                            }
                        }
                        else
                        {
                            if (UiGui.Button("Take Control"))
                            {
                                sm.CycleTakeControl();
                            }
                        }
                    }
                }
                else
                {
                    // Debug controls - only shown when /lg debug is enabled
                    if (plugin.Configuration.ShowDebugMapCompletion)
                    {
                        if (isBusy)
                            ImGui.BeginDisabled();

                        if (UiGui.Button("Cycle Missing Aetherytes"))
                        {
                            sm.StartCyclingAetherytes();
                        }
                        ImGui.SameLine();
                        if (UiGui.Button("Cycle Missing XYZ"))
                        {
                            sm.StartCyclingMapLocations();
                        }

                        // Aetheryte management buttons
                        ImGui.Spacing();
                        if (UiGui.Button("Reset All Aetherytes"))
                        {
                            if (ImGui.IsItemHovered())
                                UiGui.SetTooltip("Clear user positions - restore community defaults");
                            // TODO: Add confirmation dialog
                            plugin.AetherytePositionDatabase.ClearAllPositions();
                        }
                        ImGui.SameLine();
                        if (UiGui.Button("Fresh Scan"))
                        {
                            if (ImGui.IsItemHovered())
                                UiGui.SetTooltip("Clear ALL positions for fresh scanning (dev use)");
                            // TODO: Add confirmation dialog
                            plugin.AetherytePositionDatabase.ClearAllPositionsForFreshScan();
                        }
                        ImGui.SameLine();
                        if (UiGui.Button("Open Config Folder"))
                        {
                            if (ImGui.IsItemHovered())
                                UiGui.SetTooltip("Open the folder containing AetherytePositions.json for sharing");
                            System.Diagnostics.Process.Start("explorer.exe", plugin.AetherytePositionDatabase.ConfigDirectory);
                        }

                        if (isBusy)
                            ImGui.EndDisabled();
                    }
                }

            }

            ImGui.Spacing();

            // === Download / Auto-Update Controls ===
            if (db.IsDownloading)
            {
                UiGui.TextColored(ColorYellow, "  Downloading...");
            }
            else
            {
                if (UiGui.Button("Download Updated Locs"))
                {
                    _ = plugin.DownloadCommunityLocationsForCurrentVersionAsync();
                }
                if (!string.IsNullOrEmpty(db.LastDownloadResult))
                {
                    ImGui.SameLine();
                    var dlColor = db.LastDownloadResult.StartsWith("OK") ? ColorGreen :
                                  db.LastDownloadResult.StartsWith("Error") ? ColorRed : ColorGrey;
                    UiGui.TextColored(dlColor, db.LastDownloadResult);
                }
            }

            ImGui.Spacing();

            // === Map Type Details (grouped by expansion) ===
            var grouped = maps.GroupBy(m => m.Expansion).ToList();
            foreach (var group in grouped)
            {
                if (MaterialText.TreeNode($"{group.Key} ({group.Count(m => m.Status == ImplementationStatus.Implemented)}/{group.Count()})##exp_{group.Key}"))
                {
                    foreach (var map in group)
                    {
                        // Status icon
                        var statusColor = map.Status switch
                        {
                            ImplementationStatus.Implemented => ColorGreen,
                            ImplementationStatus.WIP => ColorYellow,
                            _ => ColorRed,
                        };
                        var statusIcon = map.Status switch
                        {
                            ImplementationStatus.Implemented => "[OK]",
                            ImplementationStatus.WIP => "[WIP]",
                            _ => "[--]",
                        };
                        UiGui.TextColored(statusColor, statusIcon);
                        ImGui.SameLine();

                        // Name + instance name(s)
                        var displayName = map.Name;
                        if (!string.IsNullOrEmpty(map.InstanceName))
                        {
                            if (!string.IsNullOrEmpty(map.SecondInstanceName))
                                displayName += $" [{map.InstanceName} / {map.SecondInstanceName}]";
                            else
                                displayName += $" [{map.InstanceName}]";
                        }
                        UiGui.Text(displayName);
                        ImGui.SameLine();

                        // Category tag
                        var catColor = map.Category switch
                        {
                            MapCategory.Roulette => ColorCyan,
                            MapCategory.GuaranteedPortal => ColorGreen,
                            MapCategory.AllTypesRandom => ColorOrange,
                            MapCategory.Dungeon => ColorYellow,
                            _ => ColorGrey,
                        };
                        var catLabel = map.Category switch
                        {
                            MapCategory.Roulette => "[Roulette]",
                            MapCategory.GuaranteedPortal => "[Guaranteed]",
                            MapCategory.Dungeon => "[Dungeon]",
                            MapCategory.AllTypesRandom => "[All 3 Types]",
                            _ => "[Outdoor]",
                        };
                        UiGui.TextColored(catColor, catLabel);

                        // Second line: Tier, Level, Territory
                        UiGui.Text(UiText.F($"      {map.Tier} | Lvl {map.MinLevel}"));
                        if (map.DungeonTerritoryId > 0)
                        {
                            ImGui.SameLine();
                            if (map.SecondTerritoryId > 0)
                                UiGui.TextColored(ColorGrey, UiText.F($" | Territory {map.DungeonTerritoryId} / {map.SecondTerritoryId}"));
                            else
                                UiGui.TextColored(ColorGrey, UiText.F($" | Territory {map.DungeonTerritoryId}"));
                        }
                    }
                    ImGui.TreePop();
                }
            }

            // === Zone Location Stats ===
            if (MaterialText.TreeNode("Location Data by Zone##zonestats", display: UiText.T("Location Data by Zone")))
            {
                var zoneStats = db.GetZoneStats();
                foreach (var kvp in zoneStats.OrderBy(z => z.Key))
                {
                    var zone = kvp.Key;
                    var (total, resolved, zoneUserOnly) = kvp.Value;
                    var pct = total > 0 ? (int)(100.0 * resolved / total) : 0;

                    var zoneColor = pct >= 100 ? ColorGreen : pct > 0 ? ColorYellow : ColorGrey;
                    UiGui.TextColored(zoneColor, UiText.F($"  {zone}: {resolved}/{total} ({pct}%)"));
                    if (zoneUserOnly > 0)
                    {
                        ImGui.SameLine();
                        UiGui.TextColored(ColorCyan, UiText.F($" [+{zoneUserOnly} yours]"));
                    }
                }
                ImGui.TreePop();
            }
        }
    }

    private void DrawBotControlSection()
    {
        {
            ImGui.BeginGroup(); DrawCompactWarnings(); ImGui.EndGroup();
            using var font = UiText.Font(UiFontRole.Action);
            var sm = plugin.StateManager;
            var loggedIn = Plugin.ClientState.IsLoggedIn;
            var buttonWidth = Scale(110);
            var buttonHeight = Scale(plugin.Configuration.UiCompact ? 40 : 52);
            NextGroup(Math.Max(buttonWidth, UiGui.ButtonWidth("Start", MaterialIcon.Play)));

            var canStart = loggedIn && (sm.State == BotState.Idle || sm.State == BotState.Error);
            if (!canStart)
                ImGui.BeginDisabled();
            ImGui.PushStyleColor(ImGuiCol.Button, MaterialTheme.Current.Colors.PrimaryContainer);
            if (UiGui.Button("Start", new Vector2(buttonWidth, buttonHeight), MaterialIcon.Play))
            {
                plugin.SetBotEnabled(true, "main-window:start");
                sm.Start();
            }
            ImGui.PopStyleColor();
            if (!canStart)
                ImGui.EndDisabled();

            NextGroup(Math.Max(buttonWidth, UiGui.ButtonWidth(sm.IsPaused ? "Resume" : "Pause", sm.IsPaused ? MaterialIcon.Play : MaterialIcon.Pause)));

            if (sm.IsPaused)
            {
                var canResume = loggedIn;
                if (!canResume)
                    ImGui.BeginDisabled();
                if (UiGui.Button("Resume", new Vector2(buttonWidth, buttonHeight), MaterialIcon.Play))
                    sm.Resume("main-window:resume");
                if (!canResume)
                    ImGui.EndDisabled();
            }
            else
            {
                var canPause = loggedIn && sm.State != BotState.Idle && sm.State != BotState.Error;
                if (!canPause)
                    ImGui.BeginDisabled();
                if (UiGui.Button("Pause", new Vector2(buttonWidth, buttonHeight), MaterialIcon.Pause))
                    sm.Pause("main-window:pause");
                if (!canPause)
                    ImGui.EndDisabled();
            }

            NextGroup(Math.Max(buttonWidth, UiGui.ButtonWidth("Stop", MaterialIcon.Stop)));

            var canStop = loggedIn && sm.State != BotState.Idle && sm.State != BotState.Error;
            if (!canStop)
                ImGui.BeginDisabled();
            if (UiGui.Button("Stop", new Vector2(buttonWidth, buttonHeight), MaterialIcon.Stop))
            {
                if (!sm.IsPaused)
                    plugin.SetBotEnabled(false, "main-window:stop");

                sm.Stop("main-window:stop");
            }
            if (!canStop)
                ImGui.EndDisabled();

            NextGroup(Math.Max(buttonWidth, UiGui.ButtonWidth("Alexandrite", MaterialIcon.Crystal)));
            if (UiGui.Button("Alexandrite", new Vector2(buttonWidth, buttonHeight), MaterialIcon.Crystal))
            {
                plugin.AlexandriteMapWindow.IsOpen = !plugin.AlexandriteMapWindow.IsOpen;
            }

            NextGroup(Math.Max(buttonWidth, UiGui.ButtonWidth("Settings", MaterialIcon.Settings)));
            if (UiGui.Button("Settings", new Vector2(buttonWidth, buttonHeight), MaterialIcon.Settings))
            {
                plugin.ToggleConfigUi();
            }

            NextGroup(Math.Max(buttonWidth, UiGui.ButtonWidth("Report Issue", MaterialIcon.Chat)));
            if (UiGui.Button("Report Issue", new Vector2(buttonWidth, buttonHeight), MaterialIcon.Chat))
            {
                ReportIssue();
            }
        }
    }

    private void DrawCurrentRunSection()
    {
        if (UiGui.CollapsingHeader("Current Run", ImGuiTreeNodeFlags.DefaultOpen, MaterialIcon.Target))
        {
            if (!Plugin.ClientState.IsLoggedIn)
            {
                UiGui.TextColored(ColorGrey, "  Log in to view current run.");
                return;
            }

            var sm = plugin.StateManager;
            var valueColumn = Math.Max(LootGoblinPresentation.DetailColumn("  State: ", "  Map: ", "  Retainer: ", "  Zone: "),
                ImGui.GetCursorPosX() + Scale(plugin.Configuration.UiCompact ? 148 : 176));
            ImGui.Dummy(new Vector2(0, Scale(plugin.Configuration.UiCompact ? 4 : 8)));
            using var rows = new MaterialStyleScope();
            rows.Style(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X,
                Math.Max(ImGui.GetStyle().ItemSpacing.Y, Scale(plugin.Configuration.UiCompact ? 36 : 44) - ImGui.GetTextLineHeight())));
            LootGoblinPresentation.DetailLabel("  Map: ", valueColumn);
            if (sm.SelectedMapItemId > 0)
            {
                var item = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRow(sm.SelectedMapItemId);
                var mapName = item?.Name.ToString() ?? UiText.F("ID {0}", sm.SelectedMapItemId);
                UiGui.TextColored(ColorCyan, mapName);
            }
            else
                UiGui.TextColored(ColorGrey, "(none)");
            LootGoblinPresentation.DetailLabel("  Zone: ", valueColumn);
            UiGui.TextColored(sm.CurrentLocation != null ? ColorCyan : ColorGrey, sm.CurrentLocation?.ZoneName ?? "(none)");
            LootGoblinPresentation.DetailLabel("  State: ", valueColumn);
            var stateColor = sm.State == BotState.Error ? ColorRed :
                             sm.State == BotState.Idle ? ColorGrey :
                             sm.State == BotState.Completed ? ColorGreen : ColorCyan;
            UiGui.TextColored(stateColor, sm.State.ToString());

            if (sm.IsPaused)
            {
                ImGui.SameLine();
                UiGui.TextColored(ColorYellow, " [PAUSED]");
            }

            if (!string.IsNullOrEmpty(sm.StateDetail) && !string.Equals(sm.StateDetail, sm.State.ToString(), StringComparison.Ordinal))
            {
                UiGui.Text("  ");
                ImGui.SameLine();
                UiGui.TextColored(ColorGrey, sm.StateDetail);
            }

            if (sm.RetryCount > 0)
            {
                UiGui.Text("  ");
                ImGui.SameLine();
                UiGui.TextColored(ColorYellow, UiText.F($"Errors: {sm.RetryCount}"));
            }

            var retainer = plugin.RetainerMapRetrievalService;
            if (retainer.IsRunning || !string.IsNullOrWhiteSpace(retainer.LastError))
            {
                LootGoblinPresentation.DetailLabel("  Retainer: ", valueColumn);
                var color = string.IsNullOrWhiteSpace(retainer.LastError) ? ColorCyan : ColorRed;
                UiGui.TextColored(color, retainer.StatusText);
            }
        }
    }

    private void DrawNavigationSection()
    {
        if (UiGui.CollapsingHeader("Navigation", icon: MaterialIcon.Send))
        {
            if (!Plugin.ClientState.IsLoggedIn)
            {
                UiGui.TextColored(ColorGrey, "  Log in to use navigation.");
                return;
            }

            var nav = plugin.NavigationService;
            var vnav = plugin.VNavIPC;

            // State display
            UiGui.Text("  State: ");
            ImGui.SameLine();
            var stateColor = nav.State == NavigationState.Error ? ColorRed :
                             nav.State == NavigationState.Idle ? ColorGrey : ColorCyan;
            UiGui.TextColored(stateColor, nav.State.ToString());
            if (!string.IsNullOrEmpty(nav.StateDetail))
            {
                ImGui.SameLine();
                UiGui.TextColored(ColorGrey, UiText.F($"  {nav.StateDetail}"));
            }

            // Condition indicators
            UiGui.Text("  ");
            ImGui.SameLine();
            UiGui.TextColored(nav.IsMounted() ? ColorGreen : ColorGrey, nav.IsMounted() ? "[Mounted]" : "[On Foot]");
            ImGui.SameLine();
            UiGui.TextColored(nav.IsFlying() ? ColorCyan : ColorGrey, nav.IsFlying() ? "[Flying]" : "[Grounded]");
            ImGui.SameLine();
            UiGui.TextColored(nav.IsInCombat() ? ColorRed : ColorGrey, nav.IsInCombat() ? "[In Combat]" : "[No Combat]");

            if (!vnav.IsAvailable)
            {
                ImGui.Spacing();
                UiGui.TextColored(ColorRed, "  vnavmesh required for navigation.");
            }
        }
    }

    private void DrawPartySection()
    {
        if (UiGui.CollapsingHeader("Party Status", ImGuiTreeNodeFlags.DefaultOpen, MaterialIcon.Group))
        {
            if (!Plugin.ClientState.IsLoggedIn)
            {
                UiGui.TextColored(ColorGrey, "  Log in to check party status.");
                return;
            }

            var party = plugin.PartyService;
            party.UpdatePartyStatus();

            var memberCount = party.PartyMembers.Count;
            UiGui.Text(UiText.F($"  Members: {memberCount}"));
            if (memberCount > 1)
            {
                ImGui.SameLine();
                var mountedCount = party.PartyMembers.Count(m => m.IsMounted);
                UiGui.TextColored(ColorGreen, UiText.F($" ({mountedCount}/{memberCount} mounted)"));
            }

            if (party.PartyMembers.Count > 1)
            {
                ImGui.Spacing();
                var localPlayer = Plugin.ObjectTable.LocalPlayer;
                var localPos = localPlayer?.Position ?? Vector3.Zero;
                
                foreach (var member in party.PartyMembers)
                {
                    var krangled = plugin.Configuration.KrangleNames ? KrangleService.KrangleName(member.Name) : member.Name;
                    UiGui.Text(UiText.F($"    {krangled}"));
                    ImGui.SameLine();

                    var dx = localPos.X - member.Position.X;
                    var dz = localPos.Z - member.Position.Z;
                    var xzDistance = Math.Sqrt(dx * dx + dz * dz);
                    var distText = member.IsInSameTerritory && member.HasPosition
                        ? UiText.F($"{xzDistance:F0}y XZ")
                        : "N/A";
                    var territoryText = member.TerritoryStatus switch
                    {
                        PartyTerritoryStatus.Same => "same territory",
                        PartyTerritoryStatus.Different => "different territory",
                        _ => "territory unresolved",
                    };
                    var loadText = member.IsLoaded ? "loaded" : "unloaded";
                    var positionText = member.PositionSource switch
                    {
                        PartyPositionSource.DirectActor => "actor position",
                        PartyPositionSource.PartyList => "party-list position",
                        _ => "position unresolved",
                    };
                    var mountText = member.IsMounted ? "Mounted" : "Not Mounted";
                    var statusColor = member.IsLoaded && member.IsInSameTerritory ? ColorGreen : ColorGrey;
                    UiGui.TextColored(
                        statusColor,
                        UiText.F($"[{UiText.T(mountText)}] [{UiText.T(territoryText)}, {UiText.T(loadText)}, {UiText.T(positionText)}] {distText}"));

                    if (member.IsFlying)
                    {
                        ImGui.SameLine();
                        UiGui.TextColored(ColorCyan, "[Flying]");
                    }

                    ImGui.SameLine();
                    var xyz = member.HasPosition
                        ? UiText.F("({0:F0}, {1:F0}, {2:F0})", member.Position.X, member.Position.Y, member.Position.Z)
                        : "(No Position)";
                    UiGui.TextColored(ColorGrey, xyz);
                }
            }

            ImGui.Spacing();
            UiGui.Text("  Mount wait: ");
            ImGui.SameLine();
            UiGui.TextColored(plugin.Configuration.WaitForParty ? ColorGreen : ColorGrey,
                plugin.Configuration.WaitForParty ? "enabled" : "off");
            ImGui.SameLine();
            UiGui.TextColored(plugin.Configuration.RequireAllMounted ? ColorGreen : ColorGrey,
                plugin.Configuration.RequireAllMounted ? " | all mounted" : " | any mounted");

            UiGui.Text("  Dismount wait: ");
            ImGui.SameLine();
            UiGui.TextColored(plugin.Configuration.PartyWaitBeforeDismount ? ColorGreen : ColorGrey,
                plugin.Configuration.PartyWaitBeforeDismount ? "enabled" : "off");
            if (plugin.Configuration.PartyWaitBeforeDismount &&
                plugin.Configuration.PartyWaitBeforeDismountUseCountThreshold)
            {
                ImGui.SameLine();
                var requiredOthers = Math.Clamp(plugin.Configuration.PartyWaitBeforeDismountRequiredOthers, 1, 7);
                UiGui.TextColored(ColorGrey, UiText.F($" | wait for {requiredOthers} other player(s)"));
            }
        }
    }

    private void DrawDependencySection()
    {
        if (UiGui.CollapsingHeader("Dependencies", ImGuiTreeNodeFlags.DefaultOpen, MaterialIcon.Settings))
        {
            // Required
            UiGui.Text("Required:");
            ImGui.Spacing();

            DrawPluginStatus("  vnavmesh", plugin.VNavIPC.IsAvailable, true);
            DrawPluginStatus("  Lifestream", plugin.IsLifestreamAvailable, true);
            DrawPluginStatus("  ADS", plugin.IsAdsAvailable, plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver);

            if (!plugin.IsLifestreamAvailable)
            {
                UiGui.TextColored(ColorRed, "  Lifestream missing. LootGoblin cannot issue /li travel without it.");
            }

            if (plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver && !plugin.IsAdsAvailable)
            {
                UiGui.TextColored(ColorRed, "  ADS dungeon handoff is enabled. Install ADS or disable it in settings.");
            }

            ImGui.Spacing();
            if (UiGui.CollapsingHeader("Integrations"))
            {
                DrawPluginStatus("  Map Flag Reader", plugin.MapFlagService.IsAvailable, false);
                DrawPluginStatus("  TextAdvance", plugin.IsTextAdvanceAvailable, false);
                ImGui.Spacing();
                UiGui.Text("Optional (Retainer/Saddlebag Retrieval):");
                ImGui.Spacing();

                DrawPluginStatus("  xadb", plugin.IsXaDatabaseAvailable, false);
                ImGui.SameLine();
                UiGui.TextColored(ColorGrey, "needed for retainer map lookup");
                DrawPluginStatus("  xaslave", plugin.IsXaSlaveAvailable, false);
                ImGui.SameLine();
                UiGui.TextColored(ColorGrey, "needed for assisted retainer/saddlebag retrieval");

                ImGui.Spacing();
                UiGui.Text("Optional (Map Gathering):");
                ImGui.Spacing();

                DrawPluginStatus("  GatherBuddy Reborn", plugin.GatherBuddyRebornService.IsAvailable, false);
                ImGui.SameLine();
                UiGui.TextColored(ColorGrey, plugin.GatherBuddyRebornService.StatusText);

                ImGui.Spacing();
                UiGui.Text("Optional (Treasure Map Statistics):");
                ImGui.Spacing();

                DrawPluginStatus("  MapPartyAssist", plugin.IsMapPartyAssistAvailable, false);
                ImGui.SameLine();
                UiGui.TextColored(ColorGrey, "by SaMo; used for treasure map statistics");

                ImGui.Spacing();
                UiGui.Text("Optional (Combat/Rotation):");
                ImGui.Spacing();

                foreach (var rp in plugin.RotationPluginIPC.RotationPlugins)
                {
                    DrawPluginStatus($"  {rp.DisplayName}", rp.IsAvailable, false);
                    if (rp.IsAvailable && rp.HasTreasureMapSupport)
                    {
                        ImGui.SameLine();
                        UiGui.TextColored(ColorGreen, " [Map AI]");
                    }
                }

                ImGui.Spacing();
            }

            if (UiGui.Button("Refresh Dependencies"))
            {
                plugin.VNavIPC.CheckAvailability();
                plugin.MapFlagService.CheckAvailability();
                plugin.RotationPluginIPC.CheckAvailability();
                plugin.GatherBuddyRebornService.CheckAvailability(logStatus: true);
                plugin.AddDebugLog("Dependency check refreshed.");
            }
        }
    }

    private void DrawPluginStatus(string label, bool available, bool required)
    {
        var valueColumn = Math.Max(LootGoblinPresentation.DetailColumn("  vnavmesh: ", "  Lifestream: ", "  ADS: "),
            LootGoblinPresentation.DetailColumn(UiText.F($"{label}: ")));
        UiGui.Text(UiText.F($"{label}: "));
        ImGui.SameLine(valueColumn);
        var statusColor = available ? ColorGreen : required ? ColorRed : ColorYellow;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddCircleFilled(origin + new Vector2(Scale(5), ImGui.GetTextLineHeight() * .5f),
            Scale(5), ImGui.GetColorU32(statusColor), 24);
        ImGui.Dummy(new Vector2(Scale(12), ImGui.GetTextLineHeight()));
        ImGui.SameLine();
        UiGui.TextColored(available ? MaterialTheme.Current.Colors.OnSurface : statusColor,
            available ? "Available" : required ? "MISSING" : "Not found");
    }

    private void DrawCommandsSection()
    {
        if (UiGui.CollapsingHeader("Commands", icon: MaterialIcon.Terminal))
        {
            UiGui.Text("/lootgoblin or /lg");
            UiGui.Text("  (no args) - Toggle this window");
            UiGui.Text("  config    - Open settings");
            UiGui.Text("  on        - Enable bot");
            UiGui.Text("  off       - Disable bot");
            UiGui.Text("  status    - Print current status");
        }
    }

    private static (int tier, int level) ParseMapTierAndLevel(string description)
    {
        int tier = 0;
        int level = 0;

        if (string.IsNullOrEmpty(description))
            return (tier, level);

        // Parse grade number - handles both "risk-reward grade X" (DT) and "classified as grade X" (older)
        // Search for "grade " followed by a number
        var searchFrom = 0;
        while (searchFrom < description.Length)
        {
            var gradeIndex = description.IndexOf("grade ", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (gradeIndex < 0) break;
            var afterGrade = description.Substring(gradeIndex + "grade ".Length).Trim();
            var gradeEnd = afterGrade.IndexOfAny(new[] { ' ', '.', ',', '\n', '\r' });
            var gradeStr = gradeEnd > 0 ? afterGrade.Substring(0, gradeEnd) : afterGrade;
            if (int.TryParse(gradeStr, out var parsedTier))
            {
                tier = parsedTier;
                break;
            }
            searchFrom = gradeIndex + 1;
        }

        // Parse "Level X" for map level
        var levelIndex = description.IndexOf("Level", StringComparison.OrdinalIgnoreCase);
        if (levelIndex >= 0)
        {
            var afterLevel = description.Substring(levelIndex + "Level".Length).Trim();
            var levelEnd = afterLevel.IndexOfAny(new[] { ' ', '.', ',', '\n' });
            var levelStr = levelEnd > 0 ? afterLevel.Substring(0, levelEnd) : afterLevel;
            if (int.TryParse(levelStr, out var parsedLevel))
                level = parsedLevel;
        }

        return (tier, level);
    }

    private void ReadMapIndicesFromDecipherMenu()
    {
        if (cachedMaps.Count == 0)
        {
            plugin.AddDebugLog("[READ INDICES] No maps in inventory to compare against");
            return;
        }
        
        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                // Open decipher menu safely with /gaction decipher
                Plugin.Log.Information("[READ INDICES] Opening decipher menu with /gaction decipher");
                
                // Use /gaction decipher to open menu safely (no map consumption)
                Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    CommandHelper.SendCommand("/gaction decipher");
                }).ConfigureAwait(false);
                
                // Wait for menu to appear
                await System.Threading.Tasks.Task.Delay(1000);
                
                // Read the menu entries
                await ReadSelectIconStringEntries(plugin);
            }
            catch (Exception ex)
            {
                plugin.AddDebugLog($"[READ INDICES] Error: {ex.Message}");
            }
        });
    }

    private static unsafe System.Threading.Tasks.Task ReadSelectIconStringEntries(Plugin plugin)
    {
        return System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                // Wait for addon to be ready
                AddonSelectIconString* addon = null;
                int entryCount = 0;
                
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    System.Threading.Thread.Sleep(100);
                    
                    nint addonPtr = Plugin.GameGui.GetAddonByName("SelectIconString", 1);
                    if (addonPtr == 0) continue;

                    addon = (AddonSelectIconString*)addonPtr;
                    if (!addon->AtkUnitBase.IsVisible) continue;

                    var addonMaster = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectIconString(&addon->AtkUnitBase);
                    entryCount = addonMaster.EntryCount;
                    
                    if (entryCount > 0)
                    {
                        Plugin.Log.Information($"[READ INDICES] Addon ready with {entryCount} entries");
                        break;
                    }
                }

                if (addon == null || entryCount == 0)
                {
                    Plugin.LogError("[READ INDICES] SelectIconString addon not ready after 2 seconds");
                    return;
                }

                // Get enabled maps from main window for comparison
                var enabledTypes = plugin.Configuration.GetRunnableMapIds(TreasureMapData.AllMapItemIds);
                var cachedMaps = plugin.InventoryService.ScanForMaps();
                var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                var enabledInventoryMapCount = cachedMaps.Keys.Count(plugin.Configuration.IsMapTypeEnabled);

                Plugin.Log.Information($"[READ INDICES] === SELECTICONSTRING MENU ANALYSIS ===");
                Plugin.Log.Information($"[READ INDICES] Total entries in menu: {entryCount}");
                Plugin.Log.Information($"[READ INDICES] Enabled maps in inventory: {enabledInventoryMapCount}");
                Plugin.Log.Information($"[READ INDICES] Total maps in inventory: {cachedMaps.Count}");
                Plugin.Log.Information($"[READ INDICES] ======================================");

                // Read all entries using node traversal as specified
                var addonNode = &addon->AtkUnitBase;
                
                for (int i = 0; i < Math.Min(entryCount, 30); i++) // Cap at 30 entries
                {
                    try
                    {
                        // Node traversal: 2 (List Component Node) -> 51001 + i (Text Node)
                        var textNodePtr = addonNode->GetNodeById((ushort)(51001 + i));
                        string entryText = "";
                        
                        if (textNodePtr != null)
                        {
                            var textNode = (AtkTextNode*)textNodePtr;
                            if (textNode->AtkResNode.Type == NodeType.Text && textNode->AtkResNode.IsVisible())
                            {
                                entryText = textNode->NodeText.ToString();
                            }
                        }
                        
                        // Fallback to AddonMaster if node traversal fails
                        if (string.IsNullOrEmpty(entryText))
                        {
                            var addonMaster2 = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectIconString(&addon->AtkUnitBase);
                            if (i < addonMaster2.EntryCount)
                            {
                                entryText = addonMaster2.Entries[i].Text;
                            }
                        }
                        
                        // Check if this entry matches any enabled maps
                        string matchIndicator = "";
                        if (!string.IsNullOrEmpty(entryText))
                        {
                            foreach (var enabledMapId in enabledTypes)
                            {
                                var mapItem = itemSheet?.GetRow(enabledMapId);
                                if (mapItem != null)
                                {
                                    var mapName = mapItem.Value.Name.ToString();
                                    if (entryText.Contains(mapName))
                                    {
                                        matchIndicator = $" ✓ MATCHES: {mapName} (ID: {enabledMapId})";
                                        break;
                                    }
                                }
                            }
                        }
                        
                        Plugin.Log.Information($"[READ INDICES] Entry[{i:D2}]: '{entryText}'{matchIndicator}");
                    }
                    catch (Exception ex)
                    {
                        Plugin.LogError($"[READ INDICES] Error reading entry {i}: {ex.Message}");
                    }
                }
                
                Plugin.Log.Information($"[READ INDICES] ======================================");
                Plugin.Log.Information($"[READ INDICES] Analysis complete. Close the decipher menu to continue.");
                
                // Auto-close the menu after a delay
                System.Threading.Thread.Sleep(5000);
                GameHelpers.KeyPress(VirtualKey.ESCAPE);
            }
            catch (Exception ex)
            {
                Plugin.LogError($"[READ INDICES] ReadSelectIconStringEntries failed: {ex.Message}\n{ex.StackTrace}");
            }
        });
    }

    private void DrawDebugLogSection()
    {
        if (UiGui.CollapsingHeader("Debug Log", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var logHeight = ImGui.GetContentRegionAvail().Y - 5;
            if (logHeight < 100) logHeight = 100;

            if (ImGui.BeginChild("DebugLogScroll", new Vector2(0, logHeight), true, ImGuiWindowFlags.HorizontalScrollbar))
            {
                foreach (var line in plugin.DebugLog)
                {
                    MaterialText.TextWrapped(line);
                }

                if (plugin.DebugLog.Count > 0)
                    ImGui.SetScrollHereY(1.0f);
            }
            ImGui.EndChild();
        }
    }

    private async void ReportIssue()
    {
        try
        {
            var reportInfo = new System.Text.StringBuilder();
            
            // Basic info
            reportInfo.AppendLine("=== LootGoblin Issue Report ===");
            reportInfo.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            reportInfo.AppendLine();
            
            // Plugin info
            reportInfo.AppendLine("Plugin Information:");
            reportInfo.AppendLine($"Version: {plugin.GetType().Assembly.GetName().Version}");
            reportInfo.AppendLine($"Enabled: {plugin.Configuration.Enabled}");
            reportInfo.AppendLine($"Bot State: {plugin.StateManager.State}");
            reportInfo.AppendLine($"State Detail: {plugin.StateManager.StateDetail}");
            reportInfo.AppendLine();
            
            // Player info
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player != null)
            {
                reportInfo.AppendLine("Player Information:");
                reportInfo.AppendLine($"Name: {KrangleName(player.Name.ToString())}");
                reportInfo.AppendLine($"Level: {player.Level}");
                reportInfo.AppendLine($"Class Job: {player.ClassJob.Value.Name}");
                reportInfo.AppendLine($"Position: X={player.Position.X:F2}, Y={player.Position.Y:F2}, Z={player.Position.Z:F2}");
                reportInfo.AppendLine($"Territory: {Plugin.ClientState.TerritoryType} ({(uint)Plugin.ClientState.TerritoryType})");
                reportInfo.AppendLine();
            }
            
            // Current map info
            if (plugin.StateManager.SelectedMapItemId > 0)
            {
                var item = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRow(plugin.StateManager.SelectedMapItemId);
                var mapName = item?.Name.ToString() ?? $"ID {plugin.StateManager.SelectedMapItemId}";
                reportInfo.AppendLine("Current Map Information:");
                reportInfo.AppendLine($"Map ID: {plugin.StateManager.SelectedMapItemId}");
                reportInfo.AppendLine($"Map Name: {mapName}");
                reportInfo.AppendLine();
            }
            
            // Aetheryte info
            var aetheryteDb = plugin.AetherytePositionDatabase;
            if (aetheryteDb != null)
            {
                reportInfo.AppendLine("Aetheryte Information:");
                reportInfo.AppendLine($"Total Stored: {aetheryteDb.Count}");
                reportInfo.AppendLine($"Unlocked Count: {aetheryteDb.GetTotalUnlockedCount()}");
                reportInfo.AppendLine($"Current Territory: {Plugin.ClientState.TerritoryType}");
                reportInfo.AppendLine();
            }
            
            // Map location info
            var mapLocationDb = plugin.MapLocationDatabase;
            if (mapLocationDb != null)
            {
                reportInfo.AppendLine("Map Location Information:");
                reportInfo.AppendLine($"Total Locations: {mapLocationDb.TotalLocations}");
                reportInfo.AppendLine($"Resolved Locations: {mapLocationDb.ResolvedLocations}");
                reportInfo.AppendLine($"Community Entries: {mapLocationDb.CommunityEntries.Count}");
                
                if (plugin.StateManager.CurrentLocation != null)
                {
                    var loc = plugin.StateManager.CurrentLocation;
                    reportInfo.AppendLine($"Current Location at ({loc.X:F1}, {loc.Y:F1}, {loc.Z:F1})");
                }
                reportInfo.AppendLine();
            }
            
            // Configuration info
            var enabledMapTypes = plugin.Configuration.GetRunnableMapIds(TreasureMapData.AllMapItemIds);
            reportInfo.AppendLine("Configuration:");
            reportInfo.AppendLine($"Map Type Filter: {(plugin.Configuration.UseMapTypeFilter ? "Explicit" : "All known maps")}");
            reportInfo.AppendLine($"Runnable Map Types: {(enabledMapTypes.Count == 0 ? "none" : string.Join(", ", enabledMapTypes))}");
            reportInfo.AppendLine($"Chest Interaction Range: {plugin.Configuration.ChestInteractionRange}y");
            reportInfo.AppendLine($"Auto Loot Chest: {plugin.Configuration.AutoLootChest}");
            reportInfo.AppendLine($"Chest Open Timeout: {plugin.Configuration.ChestOpenTimeout}s");
            reportInfo.AppendLine();
            
            // Recent debug log (last 20 lines)
            reportInfo.AppendLine("Recent Debug Log (last 20 lines):");
            var recentLogs = plugin.DebugLog.TakeLast(20);
            foreach (var log in recentLogs)
            {
                reportInfo.AppendLine($"  {log}");
            }
            reportInfo.AppendLine();
            
            // System info
            reportInfo.AppendLine("System Information:");
            reportInfo.AppendLine($"FFXIV Client: {Plugin.ClientState.ClientLanguage.ToString()}");
            reportInfo.AppendLine($"Dalamud API: {plugin.GetType().Assembly.GetName().Version}");
            reportInfo.AppendLine($"OS: {Environment.OSVersion}");
            reportInfo.AppendLine();
            
            reportInfo.AppendLine("=== End Report ===");
            
            // Log the full report to debug log (user can copy from there)
            var reportLines = reportInfo.ToString().Split('\n');
            foreach (var line in reportLines)
            {
                plugin.AddDebugLog($"[REPORT] {line}");
            }
            
            // Open GitHub issues page with pre-filled content
            var issueUrl = "https://github.com/McVaxius/LootGoblin/issues/new";
            
            // Generate context for title
            var context = plugin.StateManager.State.ToString();
            if (plugin.StateManager.State == BotState.OpeningMap)
                context = "Opening Map";
            else if (plugin.StateManager.State == BotState.Flying)
                context = "Flying to Map";
            else if (plugin.StateManager.State == BotState.OpeningChest)
                context = "Opening Chest";
            else if (plugin.StateManager.State == BotState.Completed)
                context = "Completed";
            else if (plugin.StateManager.State == BotState.Error)
                context = "Error";
            
            var title = $"Report generated by plugin - context {context}";
            var body = reportInfo.ToString();
            
            // URL encode the parameters
            var encodedTitle = Uri.EscapeDataString(title);
            var encodedBody = Uri.EscapeDataString(body);
            
            // GitHub issues URL with pre-filled title and body
            var fullUrl = $"{issueUrl}?title={encodedTitle}&body={encodedBody}";
            
            await Task.Run(() => {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = fullUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    plugin.AddDebugLog($"[ReportIssue] Could not open browser: {ex.Message}");
                    // Fallback: log the URL so user can copy it manually
                    plugin.AddDebugLog($"[ReportIssue] Manual URL: {fullUrl}");
                }
            });
            
            plugin.AddDebugLog($"[ReportIssue] GitHub issues page opened with pre-filled report - context: {context}");
        }
        catch (Exception ex)
        {
            plugin.AddDebugLog($"[ReportIssue] Error generating report: {ex.Message}");
        }
    }

    private string KrangleName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "[REDACTED]";
        
        // Simple krangling: replace characters with similar-looking ones
        var krangled = new System.Text.StringBuilder();
        var random = new Random(name.GetHashCode()); // Seed with name for consistency
        
        foreach (var c in name)
        {
            if (char.IsLetter(c))
            {
                // Replace with random letter of same case
                var replacement = (char)('a' + random.Next(26));
                if (char.IsUpper(c))
                    replacement = char.ToUpper(replacement);
                krangled.Append(replacement);
            }
            else if (char.IsDigit(c))
            {
                // Replace with random digit
                krangled.Append(random.Next(10).ToString());
            }
            else
            {
                // Keep non-alphanumeric characters
                krangled.Append(c);
            }
        }
        
        return krangled.ToString();
    }
}
