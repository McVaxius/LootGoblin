using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using LootGoblin.Models;

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
        ImGui.TextColored(ColorYellow, "Idle");

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
                    foreach (var kvp in cachedMaps)
                    {
                        if (TreasureMapData.KnownMaps.TryGetValue(kvp.Key, out var info))
                        {
                            var tierColor = info.Tier == MapTier.Party ? ColorCyan : ColorYellow;
                            ImGui.TextColored(tierColor, $"  {info.Name}");
                            ImGui.SameLine();
                            ImGui.Text($" x{kvp.Value}");
                            ImGui.SameLine();
                            ImGui.TextColored(ColorGrey, $"  [{info.Expansion}] {info.Tier}");
                            if (info.HasDungeon)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(ColorGreen, " (Dungeon)");
                            }
                        }
                        else
                        {
                            ImGui.Text($"  Unknown Map (ID: {kvp.Key}) x{kvp.Value}");
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
            }
            else
            {
                ImGui.TextColored(ColorGrey, "  Log in to scan inventory.");
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
