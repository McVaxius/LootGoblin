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

namespace LootGoblin.Windows;

public class MainWindow : Window, IDisposable
{
    private static readonly Vector4 ColorGreen = new(0.3f, 1f, 0.3f, 1f);
    private static readonly Vector4 ColorRed = new(1f, 0.3f, 0.3f, 1f);
    private static readonly Vector4 ColorYellow = new(1f, 1f, 0.3f, 1f);
    private static readonly Vector4 ColorGrey = new(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 ColorCyan = new(0.3f, 1f, 1f, 1f);
    private static readonly Vector4 ColorBlue = new(0.3f, 0.6f, 1f, 1f);
	private static readonly Vector4 ColorOrange = new(1f, 0.6f, 0f, 1f);

    private readonly Plugin plugin;
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
    }

    public void Dispose() { }

    private bool DiagnosticsVisible => plugin.Configuration.DebugMode || plugin.Configuration.ShowDebugMapCompletion;

    public override void Draw()
    {
        DrawHeaderSection();
        ImGui.Separator();
        ImGui.Spacing();

        DrawBotControlSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawStatusSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawMapInventorySection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawCurrentRunSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawPartySection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawDependencySection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawCommandsSection();

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

    private void DrawHeaderSection()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        ImGui.Text($"Loot Goblin v{version}");

        ImGui.SameLine(ImGui.GetWindowWidth() - 120);
        if (ImGui.SmallButton("\u2661 Ko-fi \u2661"))
        {
            System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = "https://ko-fi.com/mcvaxius",
                UseShellExecute = true
            });
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support development on Ko-fi");

        ImGui.Spacing();
        DrawCompactWarnings();
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
            ImGui.TextColored(ColorGreen, "Ready");
            return;
        }

        ImGui.TextColored(ColorYellow, $"Attention: {string.Join(" | ", warnings)}");
    }

    private void DrawStatusSection()
    {
        if (!ImGui.CollapsingHeader("Status", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        var enabled = plugin.Configuration.Enabled;
        var statusText = enabled ? "ENABLED" : "DISABLED";
        var statusColor = enabled ? ColorGreen : ColorRed;

        ImGui.Text("Status: ");
        ImGui.SameLine();
        ImGui.TextColored(statusColor, statusText);

        ImGui.SameLine();
        ImGui.Text("  |  Bot State: ");
        ImGui.SameLine();
        var navState = plugin.NavigationService.State;
        var navColor = navState == NavigationState.Error ? ColorRed :
                       navState == NavigationState.Idle ? ColorYellow : ColorCyan;
        ImGui.TextColored(navColor, navState.ToString());

        var loggedIn = Plugin.ClientState.IsLoggedIn;
        ImGui.Text("Logged In: ");
        ImGui.SameLine();
        ImGui.TextColored(loggedIn ? ColorGreen : ColorRed, loggedIn ? "Yes" : "No");

        if (!string.IsNullOrWhiteSpace(plugin.StateManager.WarningMessage))
        {
            ImGui.TextColored(ColorRed, plugin.StateManager.WarningMessage);
        }

        if (loggedIn)
        {
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player != null)
            {
                var playerName = plugin.Configuration.KrangleNames ? KrangleService.KrangleName(player.Name.TextValue) : player.Name.TextValue;
                var serverName = plugin.Configuration.KrangleNames ? KrangleService.KrangleServer(player.HomeWorld.Value.Name.ToString()) : player.HomeWorld.Value.Name.ToString();
                ImGui.SameLine();
                ImGui.Text($"  |  {playerName} @ {serverName}");
            }
        }

        var partyCount = Plugin.PartyList.Length;
        ImGui.Text("Party: ");
        ImGui.SameLine();
        ImGui.Text(partyCount > 0 ? $"{partyCount} members" : "Solo");

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

            ImGui.Text("Food: ");
            ImGui.SameLine();
            ImGui.TextColored(foodColor, foodStatus);
        }

        // Summon Chocobo status
        if (plugin.Configuration.SummonChocobo && loggedIn)
        {
            var buddyTime = GameHelpers.GetBuddyTimeRemaining();
            var greensCount = GameHelpers.GetInventoryItemCount(GameHelpers.GysahlGreensItemId);
            var mins = (int)(buddyTime / 60);
            var secs = (int)(buddyTime % 60);
            var timerText = buddyTime > 0 ? $"{mins}m{secs:D2}s" : "Not summoned";
            var timerColor = buddyTime > 900 ? ColorGreen : buddyTime > 0 ? ColorYellow : ColorGrey;
            var greensColor = greensCount > 0 ? ColorGreen : ColorRed;

            ImGui.Text("Chocobo: ");
            ImGui.SameLine();
            ImGui.TextColored(timerColor, timerText);
            ImGui.SameLine();
            ImGui.TextColored(greensColor, $"  |  Gysahl Greens: {greensCount}");
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

        ImGui.Text("BossMod danger: ");
        ImGui.SameLine();
        ImGui.TextColored(dangerColor, $"BMR active module: {(rotation.BmrHasActiveModule ? "yes" : "no")}{moduleName}");
        ImGui.SameLine();
        ImGui.TextColored(dangerColor, $"  |  VBM forbidden zones: {rotation.VbmForbiddenZonesCount}");
        ImGui.SameLine();
        ImGui.TextColored(suppressionColor, $"  |  Outdoor suppression: {(suppressionActive ? "on" : "off")}");
        if (!string.IsNullOrWhiteSpace(suppressionReason) && suppressionReason != "off")
        {
            ImGui.SameLine();
            ImGui.TextColored(ColorGrey, $"({suppressionReason})");
        }
    }

    private void DrawMapInventorySection()
    {
        if (ImGui.CollapsingHeader("Map Queue", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var autoStartNextMap = plugin.Configuration.AutoStartNextMap;
            if (ImGui.Checkbox("Auto Start next map", ref autoStartNextMap))
            {
                plugin.Configuration.AutoStartNextMap = autoStartNextMap;
                plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Automatically starts the next runnable selected map after completing one.");

            ImGui.SameLine();
            var showAllKnownMapTypes = plugin.Configuration.ShowAllKnownMapTypes;
            if (ImGui.Checkbox("Show all map types##MapQueueShowAllKnownMapTypes", ref showAllKnownMapTypes))
            {
                plugin.Configuration.ShowAllKnownMapTypes = showAllKnownMapTypes;
                plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Shows runnable map rows even when none are currently in inventory or loaded saddlebags.");

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
                    ImGui.TextColored(ColorGrey, $"  No treasure maps found in {sourceText}.");
                }
                else
                {
                    var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();

                    // Show warning if multiple map types detected
                    if (displayedMapSources.Count > 1)
                    {
                        ImGui.TextColored(ColorGrey, "  Multiple map types detected - use checkboxes to select which to run");
                        ImGui.Spacing();
                    }

                    // Sort entries lowest MinLevel first (matches StateManager selection order)
                    var sortedMaps = displayedMapSources
                        .OrderBy(kvp => LootGoblin.Models.TreasureMapData.KnownMaps.TryGetValue(kvp.Key, out var i) ? i.MinLevel : 999)
                        .ToList();

                    ImGui.TextColored(ColorGrey, "  Checked maps run. Use max for unlimited runs or a number for finite runs.");
                    ImGui.Spacing();

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
                            itemName = $"Unknown Map (ID: {itemId})";
                        
                        var desc = item?.Description.ToString() ?? "";
                        var (mapTier, mapLevel) = ParseMapTierAndLevel(desc);

                        var isEnabled = plugin.Configuration.IsMapTypeEnabled(itemId);
                        if (ImGui.Checkbox($"##map_{itemId}", ref isEnabled))
                        {
                            plugin.Configuration.SetMapTypeEnabled(itemId, isEnabled, TreasureMapData.AllMapItemIds);
                            plugin.Configuration.Save();
                        }
                        ImGui.SameLine();
                        DrawMapRunCountEditor(itemId, isEnabled);
                        ImGui.SameLine();
                        DrawMapGatherCheckbox(itemId, mapAllowanceStatus);
                        ImGui.SameLine();
                        var isMarketable = item is { } itemRow && itemRow.ItemSearchCategory.RowId != 0;
                        if (isMarketable)
                        {
                            DrawMapPurchaseControls(itemId);
                            ImGui.SameLine();
                        }
                        ImGui.Text($"{itemName} x{quantity}");
                        ImGui.SameLine();
                        var combinedSaddlebag = kvp.Value.Saddlebag + kvp.Value.PremiumSaddlebag;
                        ImGui.TextColored(ColorGrey, $"  [Inv {kvp.Value.Inventory} | Saddlebag {combinedSaddlebag} | Retainer {kvp.Value.Retainer}]");
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"Saddlebag includes regular ({kvp.Value.Saddlebag}) + premium ({kvp.Value.PremiumSaddlebag}) saddlebag counts.");
                        if (mapTier > 0)
                        {
                            ImGui.SameLine();
                            ImGui.TextColored(ColorCyan, $"  Tier {mapTier}");
                        }
                        if (mapLevel > 0)
                        {
                            ImGui.SameLine();
                            ImGui.TextColored(ColorGrey, $"  (Lvl {mapLevel})");
                        }
                    }
                }

                ImGui.Spacing();
                if (manualMapRefreshPending)
                    ImGui.BeginDisabled();
                if (ImGui.Button("Refresh Maps"))
                {
                    StartManualMapRefresh();
                }
                if (manualMapRefreshPending)
                    ImGui.EndDisabled();
                if (!string.IsNullOrWhiteSpace(manualMapRefreshStatus))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColorGrey, manualMapRefreshStatus);
                }
                
                // Debug button to read decipher menu indices
                if (plugin.Configuration.ShowDebugMapCompletion && cachedMaps.Count > 0)
                {
                    ImGui.Spacing();
                    if (ImGui.Button("[READ MAP INDICES]"))
                    {
                        ReadMapIndicesFromDecipherMenu();
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Opens decipher menu and reads all map entries to show correct indices");
                    }
                }
            }
            else
            {
                ImGui.TextColored(ColorGrey, "  Log in to scan inventory.");
            }
        }
    }

    private static void DrawMapAllowanceHeader(MapAllowanceStatus status, bool hasGatherJob, bool showUnavailableReason)
    {
        var header = MapAllowanceHeaderPolicy.Evaluate(status, hasGatherJob, showUnavailableReason);
        switch (header.Kind)
        {
            case MapAllowanceHeaderKind.Cooldown:
                ImGui.TextColored(ColorYellow, $"  {header.PrimaryText}");
                if (header.ShowLegend)
                {
                    ImGui.TextColored(ColorGrey, $"  {MapAllowanceHeaderPolicy.LegendLineOne}");
                    ImGui.TextColored(ColorGrey, $"  {MapAllowanceHeaderPolicy.LegendLineTwo}");
                }
                break;

            case MapAllowanceHeaderKind.Ready:
                ImGui.TextColored(ColorGreen, $"  {header.PrimaryText}");
                break;

            case MapAllowanceHeaderKind.Unavailable:
                ImGui.TextColored(ColorGrey, $"  {header.PrimaryText}");
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
            ImGui.SetTooltip(state.Tooltip);
            return;
        }

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(state.Tooltip);
        ImGui.TextColored(ColorGrey, "Seedling: toggle missing-map gathering");
        ImGui.TextColored(ColorGrey, "Blue X: unavailable through gathering");
        ImGui.TextColored(ColorGrey, "Red X overlay: allowance cooldown");
        ImGui.EndTooltip();
    }

    private void DrawMapPurchaseControls(uint itemId)
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
                ImGui.SetTooltip("Emptor is unavailable. Open Marketboard settings for installation guidance.");
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
            ImGui.SetTooltip(canEnable
                ? "Cart: buy missing maps through Emptor after gathering is unavailable. A market trip may prepare up to three, capped by remaining runs."
                : "Set a positive maximum gil price before enabling this cart.");
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputInt($"##purchase_cap_{itemId}", ref gilCap, 0, 0))
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
            if (ImGui.SmallButton($"Use##purchase_hint_{itemId}"))
            {
                plugin.Configuration.SetMapPurchaseGilCap(itemId, (int)priceHint);
                plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Copy this session's positive NQ minimum-listing hint into the ceiling. This does not enable the cart.");
        }
    }

    private void DrawMapPriceCeilingTooltip(uint itemId)
    {
        var emptor = plugin.EmptorIPC;
        var scope = emptor.LastPriceLookupScope ?? plugin.GetEmptorPriceLookupScope();
        var hasSnapshot = emptor.TryGetPriceSnapshot(itemId, out var snapshot);

        ImGui.BeginTooltip();
        ImGui.TextUnformatted("Maximum gil for one map. Zero disables purchasing for this map.");
        ImGui.Separator();

        if (hasSnapshot && snapshot.NqMinimumListing is > 0)
            ImGui.TextUnformatted($"Emptor NQ minimum listing: {snapshot.NqMinimumListing:N0} gil");
        else
            ImGui.TextUnformatted("Emptor NQ minimum listing: unavailable");

        ImGui.TextUnformatted($"World: {(hasSnapshot && !string.IsNullOrWhiteSpace(snapshot.World) ? snapshot.World : "unavailable")}");
        ImGui.TextUnformatted($"Location: {(hasSnapshot && !string.IsNullOrWhiteSpace(snapshot.Location) ? snapshot.Location : "unavailable")}");
        ImGui.TextUnformatted($"Age: {(hasSnapshot && !string.IsNullOrWhiteSpace(snapshot.Age) ? snapshot.Age : "not reported")}");
        ImGui.TextUnformatted($"Lookup scope: {EmptorIPC.GetScopeLabel(hasSnapshot ? snapshot.Scope : scope)}");

        var unavailableReason = hasSnapshot ? snapshot.Error : emptor.PriceStatusText;
        if (!string.IsNullOrWhiteSpace(unavailableReason) && (!hasSnapshot || !snapshot.HasPositiveHint))
            ImGui.TextColored(ColorYellow, unavailableReason);

        ImGui.Spacing();
        ImGui.TextColored(ColorGrey, "Price hints live only for this Loot Goblin session; they are not saved to disk.");
        ImGui.TextColored(ColorGrey, "Listings can change after lookup. Refresh is manual and rate-limited to five minutes.");
        ImGui.EndTooltip();
    }

    private void DrawMapRunCountEditor(uint itemId, bool isEnabled)
    {
        var runCount = plugin.Configuration.GetMapRunCount(itemId);

        ImGui.SetNextItemWidth(60f);
        if (isEnabled && runCount == Configuration.MapRunCountMax)
        {
            var maxText = "max";
            ImGui.BeginDisabled();
            ImGui.InputText($"##map_count_{itemId}", ref maxText, 8);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Runs this map type until no available maps remain.");
            return;
        }

        var editableCount = Math.Max(0, runCount);
        if (ImGui.InputInt($"##map_count_{itemId}", ref editableCount))
        {
            plugin.Configuration.SetMapRunCount(itemId, editableCount);
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("0 disables this map type. Positive numbers run that many resolved maps.");

        runCount = plugin.Configuration.GetMapRunCount(itemId);
        if (runCount > 0 && runCount != Configuration.MapRunCountMax)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Max##map_max_{itemId}"))
            {
                plugin.Configuration.SetMapRunCountToMax(itemId);
                plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Switch this map type back to unlimited runs.");
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
        if (ImGui.CollapsingHeader("Location Data"))
        {
            var maps = TreasureMapData.KnownMaps.Values
                .OrderBy(m => m.MinLevel)
                .ThenBy(m => m.Name)
                .ToList();

            // === Implementation Summary ===
            var implemented = maps.Count(m => m.Status == ImplementationStatus.Implemented);
            var wip = maps.Count(m => m.Status == ImplementationStatus.WIP);
            var notStarted = maps.Count(m => m.Status == ImplementationStatus.NotStarted);
            ImGui.Text($"  Maps: {maps.Count}  ");
            ImGui.SameLine();
            ImGui.TextColored(ColorGreen, $"Done: {implemented}");
            ImGui.SameLine();
            ImGui.TextColored(ColorYellow, $"  WIP: {wip}");
            ImGui.SameLine();
            if (notStarted > 0)
                ImGui.TextColored(ColorGrey, $"  Not Started: {notStarted}");

            // === Location Database Summary ===
            var db = plugin.MapLocationDatabase;
            ImGui.Text($"  Locations: {db.TotalLocations} total  ");
            ImGui.SameLine();
            ImGui.TextColored(ColorGreen, $"Resolved: {db.ResolvedLocations}");
            ImGui.SameLine();
            ImGui.TextColored(ColorGrey, $"  Missing: {db.TotalLocations - db.ResolvedLocations}");

            ImGui.Text($"  Community: {db.CommunityEntries.Count}  ");
            ImGui.SameLine();
            ImGui.Text($"User: {db.UserEntries.Count}  ");
            ImGui.SameLine();
            ImGui.Text($"TreasureSpot: {db.TreasureSpotEntries.Count}");

            // === Aetheryte Position Database Summary ===
            var aethDb = plugin.AetherytePositionDatabase;
            if (Plugin.ClientState.IsLoggedIn)
            {
                var totalUnlocked = aethDb.GetTotalUnlockedCount();
                var recorded = aethDb.Count;
                var missing = totalUnlocked - recorded;
                ImGui.Text($"  Aetherytes: {recorded}/{totalUnlocked} positions stored  ");
                if (missing > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColorYellow, $"({missing} missing)");
                }
                else if (totalUnlocked > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColorGreen, "(all recorded)");
                }
            }
            else
            {
                ImGui.Text($"  Aetherytes: {aethDb.Count} positions stored");
            }

            var userOnly = db.UserOnlyResolved;
            if (userOnly > 0)
            {
                ImGui.TextColored(ColorCyan, $"  ★ You have {userOnly} location(s) not in community DB - consider sharing!");
                ImGui.SameLine();
                if (ImGui.SmallButton("Open Data Folder"))
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
                    ImGui.TextColored(ColorCyan, $"  {sm.StateDetail}");

                    // XYZ diff display during cycling
                    if (sm.State == BotState.CyclingMapLocations && sm.CurrentLocation != null)
                    {
                        var playerPos = Plugin.ObjectTable.LocalPlayer?.Position ?? System.Numerics.Vector3.Zero;
                        var dx = playerPos.X - sm.CurrentLocation.X;
                        var dy = playerPos.Y - sm.CurrentLocation.Y;
                        var dz = playerPos.Z - sm.CurrentLocation.Z;
                        ImGui.TextColored(ColorGrey, $"  Diff: X={dx:F1} Y={dy:F1} Z={dz:F1}  Dist={Math.Sqrt(dx*dx+dz*dz):F0}y");
                    }

                    if (ImGui.Button("Stop Cycling"))
                    {
                        sm.Stop("main-window:stop-cycling");
                    }

                    // Manual control buttons during XYZ cycling
                    if (sm.State == BotState.CyclingMapLocations)
                    {
                        ImGui.SameLine();
                        if (sm.CycleManualControl)
                        {
                            if (ImGui.Button("Mark This Spot"))
                            {
                                sm.CycleMarkThisSpot();
                            }
                        }
                        else
                        {
                            if (ImGui.Button("Take Control"))
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

                        if (ImGui.Button("Cycle Missing Aetherytes"))
                        {
                            sm.StartCyclingAetherytes();
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("Cycle Missing XYZ"))
                        {
                            sm.StartCyclingMapLocations();
                        }

                        // Aetheryte management buttons
                        ImGui.Spacing();
                        if (ImGui.Button("Reset All Aetherytes"))
                        {
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip("Clear user positions - restore community defaults");
                            // TODO: Add confirmation dialog
                            plugin.AetherytePositionDatabase.ClearAllPositions();
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("Fresh Scan"))
                        {
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip("Clear ALL positions for fresh scanning (dev use)");
                            // TODO: Add confirmation dialog
                            plugin.AetherytePositionDatabase.ClearAllPositionsForFreshScan();
                        }
                        ImGui.SameLine();
                        if (ImGui.Button("Open Config Folder"))
                        {
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip("Open the folder containing AetherytePositions.json for sharing");
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
                ImGui.TextColored(ColorYellow, "  Downloading...");
            }
            else
            {
                if (ImGui.Button("Download Updated Locs"))
                {
                    _ = plugin.DownloadCommunityLocationsForCurrentVersionAsync();
                }
                if (!string.IsNullOrEmpty(db.LastDownloadResult))
                {
                    ImGui.SameLine();
                    var dlColor = db.LastDownloadResult.StartsWith("OK") ? ColorGreen :
                                  db.LastDownloadResult.StartsWith("Error") ? ColorRed : ColorGrey;
                    ImGui.TextColored(dlColor, db.LastDownloadResult);
                }
            }

            ImGui.Spacing();

            // === Map Type Details (grouped by expansion) ===
            var grouped = maps.GroupBy(m => m.Expansion).ToList();
            foreach (var group in grouped)
            {
                if (ImGui.TreeNode($"{group.Key} ({group.Count(m => m.Status == ImplementationStatus.Implemented)}/{group.Count()})##exp_{group.Key}"))
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
                        ImGui.TextColored(statusColor, statusIcon);
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
                        ImGui.Text(displayName);
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
                        ImGui.TextColored(catColor, catLabel);

                        // Second line: Tier, Level, Territory
                        ImGui.Text($"      {map.Tier} | Lvl {map.MinLevel}");
                        if (map.DungeonTerritoryId > 0)
                        {
                            ImGui.SameLine();
                            if (map.SecondTerritoryId > 0)
                                ImGui.TextColored(ColorGrey, $" | Territory {map.DungeonTerritoryId} / {map.SecondTerritoryId}");
                            else
                                ImGui.TextColored(ColorGrey, $" | Territory {map.DungeonTerritoryId}");
                        }
                    }
                    ImGui.TreePop();
                }
            }

            // === Zone Location Stats ===
            if (ImGui.TreeNode("Location Data by Zone##zonestats"))
            {
                var zoneStats = db.GetZoneStats();
                foreach (var kvp in zoneStats.OrderBy(z => z.Key))
                {
                    var zone = kvp.Key;
                    var (total, resolved, zoneUserOnly) = kvp.Value;
                    var pct = total > 0 ? (int)(100.0 * resolved / total) : 0;

                    var zoneColor = pct >= 100 ? ColorGreen : pct > 0 ? ColorYellow : ColorGrey;
                    ImGui.TextColored(zoneColor, $"  {zone}: {resolved}/{total} ({pct}%)");
                    if (zoneUserOnly > 0)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(ColorCyan, $" [+{zoneUserOnly} yours]");
                    }
                }
                ImGui.TreePop();
            }
        }
    }

    private void DrawBotControlSection()
    {
        if (ImGui.CollapsingHeader("Quick Actions", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var sm = plugin.StateManager;
            var loggedIn = Plugin.ClientState.IsLoggedIn;
            const float buttonWidth = 110f;

            var canStart = loggedIn && (sm.State == BotState.Idle || sm.State == BotState.Error);
            if (!canStart)
                ImGui.BeginDisabled();
            if (ImGui.Button("Start", new Vector2(buttonWidth, 0)))
            {
                plugin.SetBotEnabled(true, "main-window:start");
                sm.Start();
            }
            if (!canStart)
                ImGui.EndDisabled();

            ImGui.SameLine();

            if (sm.IsPaused)
            {
                var canResume = loggedIn;
                if (!canResume)
                    ImGui.BeginDisabled();
                if (ImGui.Button("Resume", new Vector2(buttonWidth, 0)))
                    sm.Resume("main-window:resume");
                if (!canResume)
                    ImGui.EndDisabled();
            }
            else
            {
                var canPause = loggedIn && sm.State != BotState.Idle && sm.State != BotState.Error;
                if (!canPause)
                    ImGui.BeginDisabled();
                if (ImGui.Button("Pause", new Vector2(buttonWidth, 0)))
                    sm.Pause("main-window:pause");
                if (!canPause)
                    ImGui.EndDisabled();
            }

            ImGui.SameLine();

            var canStop = loggedIn && sm.State != BotState.Idle && sm.State != BotState.Error;
            if (!canStop)
                ImGui.BeginDisabled();
            if (ImGui.Button("Stop", new Vector2(buttonWidth, 0)))
            {
                if (!sm.IsPaused)
                    plugin.SetBotEnabled(false, "main-window:stop");

                sm.Stop("main-window:stop");
            }
            if (!canStop)
                ImGui.EndDisabled();

            ImGui.Spacing();
            if (ImGui.Button("Alexandrite", new Vector2(buttonWidth, 0)))
            {
                plugin.AlexandriteMapWindow.IsOpen = !plugin.AlexandriteMapWindow.IsOpen;
            }

            ImGui.SameLine();
            if (ImGui.Button("Settings", new Vector2(buttonWidth, 0)))
            {
                plugin.ToggleConfigUi();
            }

            ImGui.SameLine();
            if (ImGui.Button("Report Issue", new Vector2(buttonWidth, 0)))
            {
                ReportIssue();
            }
        }
    }

    private void DrawCurrentRunSection()
    {
        if (ImGui.CollapsingHeader("Current Run", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (!Plugin.ClientState.IsLoggedIn)
            {
                ImGui.TextColored(ColorGrey, "  Log in to view current run.");
                return;
            }

            var sm = plugin.StateManager;

            ImGui.Text("  State: ");
            ImGui.SameLine();
            var stateColor = sm.State == BotState.Error ? ColorRed :
                             sm.State == BotState.Idle ? ColorGrey :
                             sm.State == BotState.Completed ? ColorGreen : ColorCyan;
            ImGui.TextColored(stateColor, sm.State.ToString());

            if (sm.IsPaused)
            {
                ImGui.SameLine();
                ImGui.TextColored(ColorYellow, " [PAUSED]");
            }

            if (!string.IsNullOrEmpty(sm.StateDetail))
            {
                ImGui.Text("  ");
                ImGui.SameLine();
                ImGui.TextColored(ColorGrey, sm.StateDetail);
            }

            if (sm.RetryCount > 0)
            {
                ImGui.Text("  ");
                ImGui.SameLine();
                ImGui.TextColored(ColorYellow, $"Errors: {sm.RetryCount}");
            }

            if (sm.SelectedMapItemId > 0)
            {
                ImGui.Spacing();
                var item = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRow(sm.SelectedMapItemId);
                var mapName = item?.Name.ToString() ?? $"ID {sm.SelectedMapItemId}";
                ImGui.Text("  Map: ");
                ImGui.SameLine();
                ImGui.TextColored(ColorCyan, mapName);
            }

            var retainer = plugin.RetainerMapRetrievalService;
            if (retainer.IsRunning || !string.IsNullOrWhiteSpace(retainer.LastError))
            {
                ImGui.Text("  Retainer: ");
                ImGui.SameLine();
                var color = string.IsNullOrWhiteSpace(retainer.LastError) ? ColorCyan : ColorRed;
                ImGui.TextColored(color, retainer.StatusText);
            }

            // Location info
            if (sm.CurrentLocation != null)
            {
                ImGui.Text("  Zone: ");
                ImGui.SameLine();
                ImGui.TextColored(ColorCyan, sm.CurrentLocation.ZoneName);
            }

            var nav = plugin.NavigationService;
            ImGui.Text("  Nav: ");
            ImGui.SameLine();
            var navColor = nav.State == NavigationState.Error ? ColorRed :
                           nav.State == NavigationState.Idle ? ColorGrey : ColorCyan;
            ImGui.TextColored(navColor, nav.State.ToString());
            if (!string.IsNullOrEmpty(nav.StateDetail))
            {
                ImGui.SameLine();
                ImGui.TextColored(ColorGrey, $"  {nav.StateDetail}");
            }

            ImGui.Text("  ");
            ImGui.SameLine();
            ImGui.TextColored(nav.IsMounted() ? ColorGreen : ColorGrey, nav.IsMounted() ? "[Mounted]" : "[On Foot]");
            ImGui.SameLine();
            ImGui.TextColored(nav.IsFlying() ? ColorCyan : ColorGrey, nav.IsFlying() ? "[Flying]" : "[Grounded]");
            ImGui.SameLine();
            ImGui.TextColored(nav.IsInCombat() ? ColorRed : ColorGrey, nav.IsInCombat() ? "[In Combat]" : "[No Combat]");
        }
    }

    private void DrawNavigationSection()
    {
        if (ImGui.CollapsingHeader("Navigation", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (!Plugin.ClientState.IsLoggedIn)
            {
                ImGui.TextColored(ColorGrey, "  Log in to use navigation.");
                return;
            }

            var nav = plugin.NavigationService;
            var vnav = plugin.VNavIPC;

            // State display
            ImGui.Text("  State: ");
            ImGui.SameLine();
            var stateColor = nav.State == NavigationState.Error ? ColorRed :
                             nav.State == NavigationState.Idle ? ColorGrey : ColorCyan;
            ImGui.TextColored(stateColor, nav.State.ToString());
            if (!string.IsNullOrEmpty(nav.StateDetail))
            {
                ImGui.SameLine();
                ImGui.TextColored(ColorGrey, $"  {nav.StateDetail}");
            }

            // Condition indicators
            ImGui.Text("  ");
            ImGui.SameLine();
            ImGui.TextColored(nav.IsMounted() ? ColorGreen : ColorGrey, nav.IsMounted() ? "[Mounted]" : "[On Foot]");
            ImGui.SameLine();
            ImGui.TextColored(nav.IsFlying() ? ColorCyan : ColorGrey, nav.IsFlying() ? "[Flying]" : "[Grounded]");
            ImGui.SameLine();
            ImGui.TextColored(nav.IsInCombat() ? ColorRed : ColorGrey, nav.IsInCombat() ? "[In Combat]" : "[No Combat]");

            if (!vnav.IsAvailable)
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorRed, "  vnavmesh required for navigation.");
            }
        }
    }

    private void DrawPartySection()
    {
        if (ImGui.CollapsingHeader("Party Status"))
        {
            if (!Plugin.ClientState.IsLoggedIn)
            {
                ImGui.TextColored(ColorGrey, "  Log in to check party status.");
                return;
            }

            var party = plugin.PartyService;
            party.UpdatePartyStatus();

            var memberCount = party.PartyMembers.Count;
            ImGui.Text($"  Members: {memberCount}");
            if (memberCount > 1)
            {
                ImGui.SameLine();
                var mountedCount = party.PartyMembers.Count(m => m.IsMounted);
                ImGui.TextColored(ColorGreen, $" ({mountedCount}/{memberCount} mounted)");
            }

            if (party.PartyMembers.Count > 1)
            {
                ImGui.Spacing();
                var localPlayer = Plugin.ObjectTable.LocalPlayer;
                var localPos = localPlayer?.Position ?? Vector3.Zero;
                
                foreach (var member in party.PartyMembers)
                {
                    var krangled = plugin.Configuration.KrangleNames ? KrangleService.KrangleName(member.Name) : member.Name;
                    ImGui.Text($"    {krangled}");
                    ImGui.SameLine();

                    var dx = localPos.X - member.Position.X;
                    var dz = localPos.Z - member.Position.Z;
                    var xzDistance = Math.Sqrt(dx * dx + dz * dz);
                    var distText = member.IsInSameTerritory && member.HasPosition
                        ? $"{xzDistance:F0}y XZ"
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
                    ImGui.TextColored(
                        statusColor,
                        $"[{mountText}] [{territoryText}, {loadText}, {positionText}] {distText}");

                    if (member.IsFlying)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(ColorCyan, "[Flying]");
                    }

                    ImGui.SameLine();
                    var xyz = member.HasPosition
                        ? $"({member.Position.X:F0}, {member.Position.Y:F0}, {member.Position.Z:F0})"
                        : "(No Position)";
                    ImGui.TextColored(ColorGrey, xyz);
                }
            }

            ImGui.Spacing();
            ImGui.Text("  Mount wait: ");
            ImGui.SameLine();
            ImGui.TextColored(plugin.Configuration.WaitForParty ? ColorGreen : ColorGrey,
                plugin.Configuration.WaitForParty ? "enabled" : "off");
            ImGui.SameLine();
            ImGui.TextColored(plugin.Configuration.RequireAllMounted ? ColorGreen : ColorGrey,
                plugin.Configuration.RequireAllMounted ? " | all mounted" : " | any mounted");

            ImGui.Text("  Dismount wait: ");
            ImGui.SameLine();
            ImGui.TextColored(plugin.Configuration.PartyWaitBeforeDismount ? ColorGreen : ColorGrey,
                plugin.Configuration.PartyWaitBeforeDismount ? "enabled" : "off");
            if (plugin.Configuration.PartyWaitBeforeDismount &&
                plugin.Configuration.PartyWaitBeforeDismountUseCountThreshold)
            {
                ImGui.SameLine();
                var requiredOthers = Math.Clamp(plugin.Configuration.PartyWaitBeforeDismountRequiredOthers, 1, 7);
                ImGui.TextColored(ColorGrey, $" | wait for {requiredOthers} other player(s)");
            }
        }
    }

private void DrawDependencySection()
    {
        if (ImGui.CollapsingHeader("Dependencies"))
        {
            // Required
            ImGui.Text("Required:");
            ImGui.Spacing();

            DrawPluginStatus("  vnavmesh", plugin.VNavIPC.IsAvailable, true);
            DrawPluginStatus("  Lifestream", plugin.IsLifestreamAvailable, true);
            DrawPluginStatus("  Map Flag Reader", plugin.MapFlagService.IsAvailable, false);
            DrawPluginStatus("  TextAdvance", plugin.IsTextAdvanceAvailable, false);
            DrawPluginStatus("  ADS", plugin.IsAdsAvailable, plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver);

            if (!plugin.IsLifestreamAvailable)
            {
                ImGui.TextColored(ColorRed, "  Lifestream missing. LootGoblin cannot issue /li travel without it.");
            }

            if (plugin.Configuration.UseAdsInsteadOfLegacyDungeonSolver && !plugin.IsAdsAvailable)
            {
                ImGui.TextColored(ColorRed, "  ADS dungeon handoff is enabled. Install ADS or disable it in settings.");
            }

            ImGui.Spacing();
            ImGui.Text("Optional (Retainer/Saddlebag Retrieval):");
            ImGui.Spacing();

            DrawPluginStatus("  xadb", plugin.IsXaDatabaseAvailable, false);
            ImGui.SameLine();
            ImGui.TextColored(ColorGrey, "needed for retainer map lookup");
            DrawPluginStatus("  xaslave", plugin.IsXaSlaveAvailable, false);
            ImGui.SameLine();
            ImGui.TextColored(ColorGrey, "needed for assisted retainer/saddlebag retrieval");

            ImGui.Spacing();
            ImGui.Text("Optional (Map Gathering):");
            ImGui.Spacing();

            DrawPluginStatus("  GatherBuddy Reborn", plugin.GatherBuddyRebornService.IsAvailable, false);
            ImGui.SameLine();
            ImGui.TextColored(ColorGrey, plugin.GatherBuddyRebornService.StatusText);

            ImGui.Spacing();
            ImGui.Text("Optional (Treasure Map Statistics):");
            ImGui.Spacing();

            DrawPluginStatus("  MapPartyAssist", plugin.IsMapPartyAssistAvailable, false);
            ImGui.SameLine();
            ImGui.TextColored(ColorGrey, "by SaMo; used for treasure map statistics");

            ImGui.Spacing();
            ImGui.Text("Optional (Combat/Rotation):");
            ImGui.Spacing();

            foreach (var rp in plugin.RotationPluginIPC.RotationPlugins)
            {
                DrawPluginStatus($"  {rp.DisplayName}", rp.IsAvailable, false);
                if (rp.IsAvailable && rp.HasTreasureMapSupport)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColorGreen, " [Map AI]");
                }
            }

            ImGui.Spacing();
            if (ImGui.Button("Refresh Dependencies"))
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
        ImGui.Text($"{label}: ");
        ImGui.SameLine();
        if (available)
        {
            ImGui.TextColored(ColorGreen, "Available");
        }
        else
        {
            ImGui.TextColored(required ? ColorRed : ColorYellow, required ? "MISSING" : "Not found");
        }
    }

    private void DrawCommandsSection()
    {
        if (ImGui.CollapsingHeader("Commands"))
        {
            ImGui.Text("/lootgoblin or /lg");
            ImGui.Text("  (no args) - Toggle this window");
            ImGui.Text("  config    - Open settings");
            ImGui.Text("  on        - Enable bot");
            ImGui.Text("  off       - Disable bot");
            ImGui.Text("  status    - Print current status");
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
        if (ImGui.CollapsingHeader("Debug Log", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var logHeight = ImGui.GetContentRegionAvail().Y - 5;
            if (logHeight < 100) logHeight = 100;

            if (ImGui.BeginChild("DebugLogScroll", new Vector2(0, logHeight), true))
            {
                foreach (var line in plugin.DebugLog)
                {
                    ImGui.TextWrapped(line);
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
