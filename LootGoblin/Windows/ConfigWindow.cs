using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace LootGoblin.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Configuration configuration;

    public ConfigWindow(Plugin plugin) : base("Loot Goblin Settings###LootGoblinConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse;

        Size = new Vector2(350, 220);
        SizeCondition = ImGuiCond.FirstUseEver;

        configuration = plugin.Configuration;
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
    }
}
