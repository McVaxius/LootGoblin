using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Game.Gui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Internal;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.UIHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI;
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
	private static readonly Vector4 ColorOrange = new(1f, 0.6f, 0f, 1f);

    private readonly Plugin plugin;
    private Dictionary<uint, int> cachedMaps = new();
    private DateTime lastScanTime = DateTime.MinValue;
    private const double ScanCooldownSeconds = 2.0;

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

    public override void Draw()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        ImGui.Text($"Loot Goblin v{version}");
        
        // Ko-fi donation button in upper right
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
        {
            ImGui.SetTooltip("Support development on Ko-fi");
        }
        
        ImGui.Separator();
        ImGui.Spacing();

        DrawStatusSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawControlsSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawBotControlSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawMapInventorySection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawMapCompletionSection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawNavigationSection();
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

        if (plugin.Configuration.DebugMode)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            DrawDebugLogSection();
        }
    }

    private void DrawStatusSection()
    {
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
    }

    private void DrawControlsSection()
    {
        if (ImGui.Button("Settings", new Vector2(120, 0)))
        {
            plugin.ToggleConfigUi();
        }

        ImGui.SameLine();
        var krangleEnabled = plugin.Configuration.KrangleNames;
        var krangleText = krangleEnabled ? "Un-Krangle" : "Krangle Names";
        if (ImGui.Button(krangleText, new Vector2(120, 0)))
        {
            plugin.Configuration.KrangleNames = !krangleEnabled;
            plugin.Configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset", new Vector2(120, 0)))
        {
            plugin.StateManager.ResetAll();
        }
    }

    private void DrawMapInventorySection()
    {
        if (ImGui.CollapsingHeader("Treasure Maps in Inventory", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (Plugin.ClientState.IsLoggedIn)
            {
                var now = DateTime.Now;
                if ((now - lastScanTime).TotalSeconds >= ScanCooldownSeconds)
                {
                    cachedMaps = plugin.InventoryService.ScanForMaps();
                    lastScanTime = now;
                }

                if (cachedMaps.Count == 0)
                {
                    ImGui.TextColored(ColorGrey, "  No treasure maps found in inventory.");
                }
                else
                {
                    var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                    var enabledTypes = plugin.Configuration.EnabledMapTypes;

                    // Show warning if multiple map types detected
                    if (cachedMaps.Count > 1)
                    {
                        ImGui.TextColored(ColorGrey, "  Multiple map types detected - use checkboxes to select which to run");
                        ImGui.Spacing();
                    }

                    // Sort entries lowest MinLevel first (matches StateManager selection order)
                    var sortedMaps = cachedMaps
                        .OrderBy(kvp => LootGoblin.Models.TreasureMapData.KnownMaps.TryGetValue(kvp.Key, out var i) ? i.MinLevel : 999)
                        .ToList();

                    ImGui.TextColored(ColorGrey, "  [x] = include in bot run (lowest tier runs first)");
                    ImGui.Spacing();

                    foreach (var kvp in sortedMaps)
                    {
                        var itemId = kvp.Key;
                        var quantity = kvp.Value;
                        
                        var item = itemSheet?.GetRow(itemId);
                        var itemName = item?.Name.ToString();
                        if (string.IsNullOrEmpty(itemName))
                            itemName = $"Unknown Map (ID: {itemId})";
                        
                        var desc = item?.Description.ToString() ?? "";
                        var (mapTier, mapLevel) = ParseMapTierAndLevel(desc);

                        // Checkbox per map type
                        var isEnabled = enabledTypes.Contains(itemId);
                        if (ImGui.Checkbox($"##map_{itemId}", ref isEnabled))
                        {
                            if (isEnabled) enabledTypes.Add(itemId);
                            else enabledTypes.Remove(itemId);
                            plugin.Configuration.Save();
                        }
                        ImGui.SameLine();
                        ImGui.Text($"{itemName} x{quantity}");
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
                if (ImGui.Button("Refresh Maps"))
                {
                    cachedMaps = plugin.InventoryService.ScanForMaps();
                    lastScanTime = DateTime.Now;
                    plugin.AddDebugLog("Manual map inventory refresh.");
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

    private void DrawMapCompletionSection()
    {
        if (ImGui.CollapsingHeader("Map Completion"))
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
                        sm.Stop();
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
                        // Ground-only mode checkbox
                        var groundOnly = plugin.Configuration.CycleGroundOnly;
                        if (ImGui.Checkbox("Ground-only (no flying)", ref groundOnly))
                        {
                            plugin.Configuration.CycleGroundOnly = groundOnly;
                            plugin.Configuration.Save();
                        }

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
                    _ = db.DownloadCommunityDataAsync();
                }
                if (!string.IsNullOrEmpty(db.LastDownloadResult))
                {
                    ImGui.SameLine();
                    var dlColor = db.LastDownloadResult.StartsWith("OK") ? ColorGreen :
                                  db.LastDownloadResult.StartsWith("Error") ? ColorRed : ColorGrey;
                    ImGui.TextColored(dlColor, db.LastDownloadResult);
                }
            }

            var autoUpdate = plugin.Configuration.AutoUpdateLocOnLogin;
            if (ImGui.Checkbox("Auto-update locations on login", ref autoUpdate))
            {
                plugin.Configuration.AutoUpdateLocOnLogin = autoUpdate;
                plugin.Configuration.Save();
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
        if (ImGui.CollapsingHeader("Bot Control", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (!Plugin.ClientState.IsLoggedIn)
            {
                ImGui.TextColored(ColorGrey, "  Log in to control the bot.");
                return;
            }

            var sm = plugin.StateManager;

            // State display
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

            ImGui.Spacing();

            // Control buttons
            if (sm.State == BotState.Idle || sm.State == BotState.Error)
            {
                if (ImGui.Button("Start Bot", new Vector2(120, 0)))
                {
                    plugin.Configuration.Enabled = true;
                    plugin.Configuration.Save();
                    sm.Start();
                }
            }
            else if (sm.IsPaused)
            {
                if (ImGui.Button("Resume", new Vector2(120, 0)))
                    sm.Resume();
                ImGui.SameLine();
                if (ImGui.Button("Stop", new Vector2(120, 0)))
                    sm.Stop();
            }
            else
            {
                if (ImGui.Button("Pause", new Vector2(120, 0)))
                    sm.Pause();
                ImGui.SameLine();
                if (ImGui.Button("Stop", new Vector2(120, 0)))
                {
                    // Stop button should act like /lg off - fully disable the bot
                    plugin.Configuration.Enabled = false;
                    plugin.Configuration.Save();
                    sm.Stop();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Alexandrite", new Vector2(100, 0)))
            {
                plugin.AlexandriteMapWindow.IsOpen = !plugin.AlexandriteMapWindow.IsOpen;
            }

            ImGui.SameLine();
            if (ImGui.Button("Report Issue", new Vector2(100, 0)))
            {
                ReportIssue();
            }

            // Current map info
            if (sm.SelectedMapItemId > 0)
            {
                ImGui.Spacing();
                var item = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRow(sm.SelectedMapItemId);
                var mapName = item?.Name.ToString() ?? $"ID {sm.SelectedMapItemId}";
                ImGui.Text("  Map: ");
                ImGui.SameLine();
                ImGui.TextColored(ColorCyan, mapName);
            }

            // Location info
            if (sm.CurrentLocation != null)
            {
                ImGui.Text("  Zone: ");
                ImGui.SameLine();
                ImGui.TextColored(ColorCyan, sm.CurrentLocation.ZoneName);
            }
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
        if (ImGui.CollapsingHeader("Party Coordination"))
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
                    
                    // Calculate distance from local player
                    var distance = Vector3.Distance(localPos, member.Position);
                    var distText = member.IsInSameZone ? $"{distance:F0}y" : "N/A";
                    
                    if (member.IsMounted)
                    {
                        ImGui.TextColored(ColorGreen, $"[Mounted] {distText}");
                        if (member.IsFlying)
                        {
                            ImGui.SameLine();
                            ImGui.TextColored(ColorCyan, "[Flying]");
                        }
                    }
                    else
                    {
                        ImGui.TextColored(ColorGrey, $"[On Foot] {distText}");
                    }
                    
                    // Show XYZ coordinates
                    ImGui.SameLine();
                    var xyz = member.IsInSameZone 
                        ? $"({member.Position.X:F0}, {member.Position.Y:F0}, {member.Position.Z:F0})"
                        : "(Different Zone)";
                    ImGui.TextColored(ColorGrey, xyz);
                }
            }

            ImGui.Spacing();
            var partyWait = plugin.Configuration.PartyWaitBeforeDismount;
            if (ImGui.Checkbox("Wait for party before dismounting", ref partyWait))
            {
                plugin.Configuration.PartyWaitBeforeDismount = partyWait;
                plugin.Configuration.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(ColorGrey, "(?)");
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.Text("When enabled, the bot will wait at the destination\n" +
                           "until all party members are within 10 yalms (XZ distance)\n" +
                           "before dismounting. This prevents the bot from dismounting\n" +
                           "alone in dangerous zones while party members are still\n" +
                           "traveling. Works in both flying and ground-only modes.");
                ImGui.EndTooltip();
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
            DrawPluginStatus("  GlobeTrotter", plugin.GlobeTrotterIPC.IsAvailable, false);
            DrawPluginStatus("  TextAdvance", plugin.IsTextAdvanceAvailable, false);

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
                plugin.GlobeTrotterIPC.CheckAvailability();
                plugin.RotationPluginIPC.CheckAvailability();
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
                    Plugin.Log.Error("[READ INDICES] SelectIconString addon not ready after 2 seconds");
                    return;
                }

                // Get enabled maps from main window for comparison
                var enabledTypes = plugin.Configuration.EnabledMapTypes;
                var cachedMaps = plugin.InventoryService.ScanForMaps();
                var itemSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();

                Plugin.Log.Information($"[READ INDICES] === SELECTICONSTRING MENU ANALYSIS ===");
                Plugin.Log.Information($"[READ INDICES] Total entries in menu: {entryCount}");
                Plugin.Log.Information($"[READ INDICES] Enabled maps in inventory: {enabledTypes.Count}");
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
                        Plugin.Log.Error($"[READ INDICES] Error reading entry {i}: {ex.Message}");
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
                Plugin.Log.Error($"[READ INDICES] ReadSelectIconStringEntries failed: {ex.Message}\n{ex.StackTrace}");
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
                reportInfo.AppendLine($"Name: {player.Name}");
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
            reportInfo.AppendLine("Configuration:");
            reportInfo.AppendLine($"Enabled Map Types: {string.Join(", ", plugin.Configuration.EnabledMapTypes)}");
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
            
            // Open GitHub issues page asynchronously
            var issueUrl = "https://github.com/McVaxius/LootGoblin/issues/new";
            
            await Task.Run(() => {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = issueUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    plugin.AddDebugLog($"[ReportIssue] Could not open browser: {ex.Message}");
                }
            });
            
            plugin.AddDebugLog("[ReportIssue] GitHub issues page opened - check debug log for full report");
        }
        catch (Exception ex)
        {
            plugin.AddDebugLog($"[ReportIssue] Error generating report: {ex.Message}");
        }
    }
}
