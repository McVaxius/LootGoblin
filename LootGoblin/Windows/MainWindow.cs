using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
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
        ImGui.Text("Loot Goblin - Treasure Map Automation");
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

        DrawMapInventorySection();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawNavigationSection();
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
                ImGui.SameLine();
                ImGui.Text($"  |  {player.Name} @ {player.HomeWorld.Value.Name}");
            }
        }

        var partyCount = Plugin.PartyList.Length;
        ImGui.Text("Party: ");
        ImGui.SameLine();
        ImGui.Text(partyCount > 0 ? $"{partyCount} members" : "Solo");
    }

    private void DrawControlsSection()
    {
        var enabled = plugin.Configuration.Enabled;

        if (enabled)
        {
            if (ImGui.Button("Disable Bot", new Vector2(120, 0)))
            {
                plugin.Configuration.Enabled = false;
                plugin.Configuration.Save();
                plugin.AddDebugLog("Bot disabled via UI.");
            }
        }
        else
        {
            if (ImGui.Button("Enable Bot", new Vector2(120, 0)))
            {
                plugin.Configuration.Enabled = true;
                plugin.Configuration.Save();
                plugin.AddDebugLog("Bot enabled via UI.");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Settings", new Vector2(120, 0)))
        {
            plugin.ToggleConfigUi();
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
                    
                    foreach (var kvp in cachedMaps)
                    {
                        var itemId = kvp.Key;
                        var quantity = kvp.Value;
                        
                        var item = itemSheet?.GetRow(itemId);
                        var itemName = item?.Name.ToString();
                        if (string.IsNullOrEmpty(itemName))
                            itemName = $"Unknown Map (ID: {itemId})";
                        
                        // Determine tier from item description
                        var desc = item?.Description.ToString() ?? "";
                        var isParty = desc.Contains("8 player", StringComparison.OrdinalIgnoreCase);
                        var tierColor = isParty ? ColorCyan : ColorYellow;
                        var tierTag = isParty ? "[Party]" : "[Solo]";
                        
                        ImGui.TextColored(tierColor, $"  {itemName}");
                        ImGui.SameLine();
                        ImGui.Text($" x{quantity}");
                        ImGui.SameLine();
                        ImGui.TextColored(ColorGrey, $"  {tierTag}");
                    }
                }

                ImGui.Spacing();
                if (ImGui.Button("Refresh Maps"))
                {
                    cachedMaps = plugin.InventoryService.ScanForMaps();
                    lastScanTime = DateTime.Now;
                    plugin.AddDebugLog("Manual map inventory refresh.");
                }
            }
            else
            {
                ImGui.TextColored(ColorGrey, "  Log in to scan inventory.");
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

            ImGui.Spacing();

            // Navigation buttons
            if (!vnav.IsAvailable)
            {
                ImGui.TextColored(ColorRed, "  vnavmesh required for navigation.");
            }
            else
            {
                if (ImGui.Button("Fly to Flag", new Vector2(120, 0)))
                {
                    nav.FlyToFlag();
                }

                ImGui.SameLine();
                if (ImGui.Button("Mount Up", new Vector2(120, 0)))
                {
                    nav.MountUp();
                }

                ImGui.SameLine();
                if (ImGui.Button("Stop Nav", new Vector2(120, 0)))
                {
                    nav.StopNavigation();
                }
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
}
