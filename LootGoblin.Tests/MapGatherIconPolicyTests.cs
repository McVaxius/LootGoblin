using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class MapGatherIconPolicyTests
{
    [Fact]
    public void UnknownMapUsesDisabledBlueXAndSuppressesCooldown()
    {
        var state = Evaluate(
            isKnownMap: false,
            isGatherable: true,
            hasGatherJob: true,
            gatherEnabled: true,
            allowanceAvailable: true,
            allowanceReady: false);

        Assert.Equal(MapGatherIconKind.Unavailable, state.Icon);
        Assert.False(state.GatherEnabled);
        Assert.False(state.IsInteractive);
        Assert.False(state.ShowCooldownOverlay);
        Assert.Equal("This map is unknown to LootGoblin and cannot be gathered.", state.Tooltip);
    }

    [Fact]
    public void NonGatherableMapBlueXOverridesMissingJobAndCooldown()
    {
        var state = Evaluate(
            isKnownMap: true,
            isGatherable: false,
            hasGatherJob: false,
            gatherEnabled: true,
            allowanceAvailable: true,
            allowanceReady: false);

        Assert.Equal(MapGatherIconKind.Unavailable, state.Icon);
        Assert.False(state.GatherEnabled);
        Assert.False(state.IsInteractive);
        Assert.False(state.ShowCooldownOverlay);
        Assert.Equal("This map is unavailable through gathering.", state.Tooltip);
    }

    [Fact]
    public void MissingGatherJobKeepsSeedlingDisabled()
    {
        var state = Evaluate(
            isKnownMap: true,
            isGatherable: true,
            hasGatherJob: false,
            gatherEnabled: true,
            allowanceAvailable: true,
            allowanceReady: false);

        Assert.Equal(MapGatherIconKind.Seedling, state.Icon);
        Assert.True(state.GatherEnabled);
        Assert.False(state.IsInteractive);
        Assert.True(state.ShowCooldownOverlay);
        Assert.Equal("Configure Settings -> Run -> Gather Job to gather missing maps.", state.Tooltip);
    }

    [Theory]
    [InlineData(false, "Gathering disabled. Click the seedling to change the missing-map gather selection while allowance is ready.")]
    [InlineData(true, "Gathering enabled. Click the seedling to change the missing-map gather selection while allowance is ready.")]
    public void ReadyTooltipReportsSelection(bool gatherEnabled, string expectedTooltip)
    {
        var state = Evaluate(gatherEnabled: gatherEnabled, allowanceAvailable: true, allowanceReady: true);

        Assert.Equal(MapGatherIconKind.Seedling, state.Icon);
        Assert.True(state.IsInteractive);
        Assert.False(state.ShowCooldownOverlay);
        Assert.Equal(expectedTooltip, state.Tooltip);
    }

    [Theory]
    [InlineData(false, "Gathering disabled. Allowance cooldown: 17h 05m remaining. Selection is saved for the next allowance.")]
    [InlineData(true, "Gathering enabled. Allowance cooldown: 17h 05m remaining. Selection is saved for the next allowance.")]
    public void CooldownTooltipReportsSelectionAndShowsOverlay(bool gatherEnabled, string expectedTooltip)
    {
        var state = Evaluate(gatherEnabled: gatherEnabled, allowanceAvailable: true, allowanceReady: false);

        Assert.True(state.IsInteractive);
        Assert.True(state.ShowCooldownOverlay);
        Assert.Equal(expectedTooltip, state.Tooltip);
    }

    [Theory]
    [InlineData(false, "Gathering disabled. Map allowance status is unavailable.")]
    [InlineData(true, "Gathering enabled. Map allowance status is unavailable.")]
    public void UnavailableAllowanceTooltipReportsSelectionWithoutOverlay(bool gatherEnabled, string expectedTooltip)
    {
        var state = Evaluate(gatherEnabled: gatherEnabled, allowanceAvailable: false, allowanceReady: false);

        Assert.True(state.IsInteractive);
        Assert.False(state.ShowCooldownOverlay);
        Assert.Equal(expectedTooltip, state.Tooltip);
    }

    private static MapGatherIconState Evaluate(
        bool isKnownMap = true,
        bool isGatherable = true,
        bool hasGatherJob = true,
        bool gatherEnabled = false,
        bool allowanceAvailable = true,
        bool allowanceReady = true)
        => MapGatherIconPolicy.Evaluate(new MapGatherIconInput(
            isKnownMap,
            isGatherable,
            hasGatherJob,
            gatherEnabled,
            allowanceAvailable,
            allowanceReady,
            "17h 05m"));
}
