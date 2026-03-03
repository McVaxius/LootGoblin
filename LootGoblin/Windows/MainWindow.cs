using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace LootGoblin.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("Loot Goblin##MainWindow")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
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

        ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "Plugin loaded successfully!");
        ImGui.Spacing();

        ImGui.Text("Status: Idle");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped("Phase 1 complete. Map detection, navigation, and party coordination coming in future phases.");
        ImGui.Spacing();

        if (ImGui.Button("Settings"))
        {
            plugin.ToggleConfigUi();
        }
    }
}
