using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using LootGoblin.Models;
using LootGoblin.Services;
using AethertekUI;

namespace LootGoblin.Windows;

public class AlexandriteMapWindow : Window, IDisposable
{
    private readonly AethertekUI.Dalamud.MaterialWindowMotion windowMotion = new();
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
            MinimumSize = new Vector2(340, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(440, 440);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags |= ImGuiWindowFlags.HorizontalScrollbar;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
        => windowMotion.Restore(this);

    public override void Draw()
    {
        windowMotion.DrawChrome();
        UiGui.Title("Alexandrite Maps", UiText.T("Alexandrite Maps"), MaterialIcon.Crystal);
        using var controls = MaterialControls.Push(LootGoblinPresentation.Controls(plugin.Configuration.UiCompact ? 30 : 36));
        var valueColumn = LootGoblinPresentation.DetailColumn("Poetics: ", "Runs: ", "Runnable: ", "Status: ", "Maps in inventory: ", "Active map: ");
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
            LootGoblinPresentation.DetailLabel("Poetics: ", valueColumn);
            var poeticsColor = poetics >= AlexandritePolicy.PoeticsPerMysteriousMap ? ColorGreen : ColorRed;
            UiGui.TextColored(poeticsColor, UiText.F($"{poetics}/2000"));
            var note = UiText.F($"  ({AlexandritePolicy.PoeticsPerMysteriousMap} per map)");
            if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + MaterialText.Measure(note).X
                <= ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X) ImGui.SameLine();
            UiGui.TextColored(ColorGrey, note);
        }
        else
        {
            UiGui.TextColored(ColorGrey, "Log in to see Poetics.");
        }

        // Run count
        if (!isRunning)
        {
            LootGoblinPresentation.DetailLabel("Runs: ", valueColumn);
            ImGui.SetNextItemWidth(Math.Min(100 * MaterialTheme.Metrics.Scale, ImGui.GetContentRegionAvail().X));
            UiGui.InputInt("##runcount", ref runCount);
            runLimit = AlexandritePolicy.EvaluateRunLimit(
                runCount,
                inventoryMapCount,
                hasActiveMysteriousMap,
                poetics);
            runCount = runLimit.RequestedRuns;
        }
        else
        {
            LootGoblinPresentation.DetailLabel("Runs: ", valueColumn);
            UiGui.TextColored(ColorCyan, UiText.F($"{sm.AlexandriteRunsCompleted} done, {sm.AlexandriteRunsRemaining} remaining"));
        }

        LootGoblinPresentation.DetailLabel("Runnable: ", valueColumn);
        UiGui.TextColored(runLimit.CanStart ? ColorGreen : ColorRed, UiText.F($"{runLimit.MaxRunnableRuns}"));
        if (isLoggedIn)
        {
            UiGui.TextColored(
                ColorGrey,
                UiText.F($"{runLimit.InventoryMapCount} inventory + {runLimit.ActiveMapCount} active + {runLimit.PurchasableMapCount} from Poetics"));
        }

        ImGui.Spacing();

        // Start / Stop
        if (isRunning)
        {
            if (UiGui.Button("Stop##alexstop", new Vector2(-1, (plugin.Configuration.UiCompact ? 36 : 40) * MaterialTheme.Metrics.Scale), MaterialIcon.Stop))
            {
                sm.Stop("alexandrite-window:stop");
            }
        }
        else
        {
            var startDisabled = isBusy || !runLimit.CanStart;
            if (startDisabled)
                ImGui.BeginDisabled();

            ImGui.PushStyleColor(ImGuiCol.Button, MaterialTheme.Current.Colors.PrimaryContainer);
            if (UiGui.Button("Start##alexstart", new Vector2(-1, (plugin.Configuration.UiCompact ? 36 : 40) * MaterialTheme.Metrics.Scale), MaterialIcon.Play))
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
            ImGui.PopStyleColor();

            if (startDisabled)
                ImGui.EndDisabled();
        }

        ImGui.Spacing();
        // Status
        LootGoblinPresentation.DetailLabel("Status: ", valueColumn);
        if (isRunning)
        {
            UiGui.TextColored(ColorCyan, sm.StateDetail);
        }
        else if (sm.State == BotState.Error)
        {
            UiGui.TextColored(ColorRed, sm.StateDetail);
        }
        else
        {
            UiGui.TextColored(ColorGrey, "Idle");
        }

        // Mysterious Map count
        if (isLoggedIn)
        {
            LootGoblinPresentation.DetailLabel("Maps in inventory: ", valueColumn);
            UiGui.TextColored(inventoryMapCount > 0 ? ColorGreen : ColorGrey, UiText.F($"{inventoryMapCount}"));

            LootGoblinPresentation.DetailLabel("Active map: ", valueColumn);
            UiGui.TextColored(hasActiveMysteriousMap ? ColorGreen : ColorGrey, hasActiveMysteriousMap ? "yes" : "no");
        }

        ImGui.Spacing();
        LootGoblinPresentation.AccentRule();
        UiGui.TextWrapped("Buys Mysterious Maps from Auriana in Revenant's Toll (75 Poetics each), then runs each map automatically.");
    }

    private bool HasActiveMysteriousMap()
        => plugin.InventoryService.TryFindTreasureMapKeyItem(out var keyItem) &&
           keyItem.KnownMapItemId == MysteriousMapItemId;
}
