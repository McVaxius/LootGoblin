using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using LootGoblin.Models;
using LootGoblin.Services;

namespace LootGoblin.Windows;

public class AlexandriteMapWindow : Window, IDisposable
{
    private const uint MysteriousMapItemId = AlexandritePolicy.MysteriousMapItemId;

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
        runCount = plugin.Configuration.AlexandriteRunCount;
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
        var isLoggedIn = Plugin.ClientState.IsLoggedIn;
        var poetics = isLoggedIn ? GameHelpers.GetCurrentPoetics() : 0;
        var inventoryMapCount = isLoggedIn ? GameHelpers.GetInventoryItemCount(MysteriousMapItemId) : 0;
        var hasActiveMysteriousMap = isLoggedIn && HasActiveMysteriousMap();
        var runLimit = AlexandritePolicy.EvaluateRunLimit(
            runCount,
            inventoryMapCount,
            hasActiveMysteriousMap,
            poetics);

        // Poetics display
        if (isLoggedIn)
        {
            ImGui.Text("Poetics: ");
            ImGui.SameLine();
            var poeticsColor = poetics >= AlexandritePolicy.PoeticsPerMysteriousMap ? ColorGreen : ColorRed;
            ImGui.TextColored(poeticsColor, $"{poetics}/2000");
            ImGui.SameLine();
            ImGui.TextColored(ColorGrey, $"  ({AlexandritePolicy.PoeticsPerMysteriousMap} per map)");
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
            runLimit = AlexandritePolicy.EvaluateRunLimit(
                runCount,
                inventoryMapCount,
                hasActiveMysteriousMap,
                poetics);
            runCount = runLimit.RequestedRuns;
        }
        else
        {
            ImGui.Text("Runs: ");
            ImGui.SameLine();
            ImGui.TextColored(ColorCyan, $"{sm.AlexandriteRunsCompleted} done, {sm.AlexandriteRunsRemaining} remaining");
        }

        ImGui.Spacing();

        ImGui.Text("Runnable: ");
        ImGui.SameLine();
        ImGui.TextColored(runLimit.CanStart ? ColorGreen : ColorRed, $"{runLimit.MaxRunnableRuns}");
        if (isLoggedIn)
        {
            ImGui.TextColored(
                ColorGrey,
                $"{runLimit.InventoryMapCount} inventory + {runLimit.ActiveMapCount} active + {runLimit.PurchasableMapCount} from Poetics");
        }

        ImGui.Spacing();

        // Start / Stop
        if (isRunning)
        {
            if (ImGui.Button("Stop##alexstop", new Vector2(120, 0)))
            {
                sm.Stop("alexandrite-window:stop");
            }
        }
        else
        {
            var startDisabled = isBusy || !runLimit.CanStart;
            if (startDisabled)
                ImGui.BeginDisabled();

            if (ImGui.Button("Start##alexstart", new Vector2(120, 0)))
            {
                runLimit = AlexandritePolicy.EvaluateRunLimit(
                    runCount,
                    inventoryMapCount,
                    hasActiveMysteriousMap,
                    poetics);
                runCount = runLimit.RequestedRuns;
                plugin.Configuration.AlexandriteRunCount = runLimit.RequestedRuns;
                plugin.Configuration.Save();
                sm.StartAlexandriteFarming(runLimit.RequestedRuns);
            }

            if (startDisabled)
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
        if (isLoggedIn)
        {
            ImGui.Text("Maps in inventory: ");
            ImGui.SameLine();
            ImGui.TextColored(inventoryMapCount > 0 ? ColorGreen : ColorGrey, $"{inventoryMapCount}");

            ImGui.Text("Active map: ");
            ImGui.SameLine();
            ImGui.TextColored(hasActiveMysteriousMap ? ColorGreen : ColorGrey, hasActiveMysteriousMap ? "yes" : "no");
        }

        ImGui.Spacing();
        ImGui.TextColored(ColorGrey, "Buys Mysterious Maps from Auriana in");
        ImGui.TextColored(ColorGrey, "Revenant's Toll (75 Poetics each), then");
        ImGui.TextColored(ColorGrey, "runs each map automatically.");
    }

    private bool HasActiveMysteriousMap()
        => plugin.InventoryService.TryFindTreasureMapKeyItem(out var keyItem) &&
           keyItem.KnownMapItemId == MysteriousMapItemId;
}
