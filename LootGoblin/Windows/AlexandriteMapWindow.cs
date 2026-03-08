using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using LootGoblin.Models;
using LootGoblin.Services;

namespace LootGoblin.Windows;

public class AlexandriteMapWindow : Window, IDisposable
{
    private static readonly Vector4 ColorGreen = new(0.3f, 1f, 0.3f, 1f);
    private static readonly Vector4 ColorRed = new(1f, 0.3f, 0.3f, 1f);
    private static readonly Vector4 ColorYellow = new(1f, 1f, 0.3f, 1f);
    private static readonly Vector4 ColorGrey = new(0.5f, 0.5f, 0.5f, 1f);
    private static readonly Vector4 ColorCyan = new(0.3f, 1f, 1f, 1f);

    private readonly Plugin plugin;
    private int runCount = 1;

    public AlexandriteMapWindow(Plugin plugin)
        : base("Alexandrite Maps##AlexandriteMapWindow")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 200),
            MaximumSize = new Vector2(500, 400),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var sm = plugin.StateManager;
        var isRunning = sm.State == BotState.AlexandriteFarming;
        var isBusy = sm.State != BotState.Idle && sm.State != BotState.Error && sm.State != BotState.Completed;

        // Poetics display
        if (Plugin.ClientState.IsLoggedIn)
        {
            var poetics = GameHelpers.GetCurrentPoetics();
            ImGui.Text("Poetics: ");
            ImGui.SameLine();
            var poeticsColor = poetics >= 75 ? ColorGreen : ColorRed;
            ImGui.TextColored(poeticsColor, $"{poetics}/2000");
            ImGui.SameLine();
            ImGui.TextColored(ColorGrey, $"  (75 per map)");
        }
        else
        {
            ImGui.TextColored(ColorGrey, "Log in to see Poetics.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Run count
        if (!isRunning)
        {
            ImGui.Text("Runs: ");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.InputInt("##runcount", ref runCount);
            if (runCount < 1) runCount = 1;
            if (runCount > 100) runCount = 100;
        }
        else
        {
            ImGui.Text("Runs: ");
            ImGui.SameLine();
            ImGui.TextColored(ColorCyan, $"{sm.AlexandriteRunsCompleted} done, {sm.AlexandriteRunsRemaining} remaining");
        }

        ImGui.Spacing();

        // Start / Stop
        if (isRunning)
        {
            if (ImGui.Button("Stop##alexstop", new Vector2(120, 0)))
            {
                sm.Stop();
            }
        }
        else
        {
            if (isBusy)
                ImGui.BeginDisabled();

            if (ImGui.Button("Start##alexstart", new Vector2(120, 0)))
            {
                plugin.Configuration.AlexandriteRunCount = runCount;
                plugin.Configuration.Save();
                sm.StartAlexandriteFarming(runCount);
            }

            if (isBusy)
                ImGui.EndDisabled();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Status
        ImGui.Text("Status: ");
        ImGui.SameLine();
        if (isRunning)
        {
            ImGui.TextColored(ColorCyan, sm.StateDetail);
        }
        else if (sm.State == BotState.Error)
        {
            ImGui.TextColored(ColorRed, sm.StateDetail);
        }
        else
        {
            ImGui.TextColored(ColorGrey, "Idle");
        }

        // Mysterious Map count
        if (Plugin.ClientState.IsLoggedIn)
        {
            var mapCount = GameHelpers.GetInventoryItemCount(7885);
            if (mapCount > 0)
            {
                ImGui.Text("Maps in inventory: ");
                ImGui.SameLine();
                ImGui.TextColored(ColorGreen, $"{mapCount}");
            }
        }

        ImGui.Spacing();
        ImGui.TextColored(ColorGrey, "Buys Mysterious Maps from Auriana in");
        ImGui.TextColored(ColorGrey, "Revenant's Toll (75 Poetics each), then");
        ImGui.TextColored(ColorGrey, "runs each map automatically.");
    }
}
