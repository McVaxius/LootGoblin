using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class MapAllowanceHeaderPolicyTests
{
    [Fact]
    public void CooldownShowsTimerAndLegend()
    {
        var state = MapAllowanceHeaderPolicy.Evaluate(
            new MapAllowanceStatus(false, TimeSpan.FromHours(17).Add(TimeSpan.FromMinutes(14)), null, string.Empty),
            hasGatherJob: true);

        Assert.Equal(MapAllowanceHeaderKind.Cooldown, state.Kind);
        Assert.Equal("Map allowance cooldown: 17h 14m", state.PrimaryText);
        Assert.True(state.ShowLegend);
    }

    [Fact]
    public void ReadyWithGatherJobShowsReadyText()
    {
        var state = MapAllowanceHeaderPolicy.Evaluate(
            new MapAllowanceStatus(true, TimeSpan.Zero, null, string.Empty),
            hasGatherJob: true);

        Assert.Equal(MapAllowanceHeaderKind.Ready, state.Kind);
        Assert.Equal("[map can be gathered]", state.PrimaryText);
        Assert.False(state.ShowLegend);
    }

    [Fact]
    public void ReadyWithoutGatherJobShowsNothing()
    {
        var state = MapAllowanceHeaderPolicy.Evaluate(
            new MapAllowanceStatus(true, TimeSpan.Zero, null, string.Empty),
            hasGatherJob: false);

        Assert.Equal(MapAllowanceHeaderKind.None, state.Kind);
        Assert.Equal(string.Empty, state.PrimaryText);
        Assert.False(state.ShowLegend);
    }

    [Fact]
    public void UnavailableStatusShowsNothing()
    {
        var state = MapAllowanceHeaderPolicy.Evaluate(
            new MapAllowanceStatus(false, TimeSpan.Zero, null, "map allowance timer not loaded"),
            hasGatherJob: true);

        Assert.Equal(MapAllowanceHeaderKind.None, state.Kind);
        Assert.Equal(string.Empty, state.PrimaryText);
        Assert.False(state.ShowLegend);
    }

    [Fact]
    public void LoadingStatusShowsNothing()
    {
        var state = MapAllowanceHeaderPolicy.Evaluate(
            new MapAllowanceStatus(false, TimeSpan.Zero, null, "loading"),
            hasGatherJob: true);

        Assert.Equal(MapAllowanceHeaderKind.None, state.Kind);
        Assert.Equal(string.Empty, state.PrimaryText);
        Assert.False(state.ShowLegend);
    }

    [Fact]
    public void DebugUnavailableStatusShowsReason()
    {
        var state = MapAllowanceHeaderPolicy.Evaluate(
            new MapAllowanceStatus(false, TimeSpan.Zero, null, "map allowance timer not loaded"),
            hasGatherJob: true,
            showUnavailableReason: true);

        Assert.Equal(MapAllowanceHeaderKind.Unavailable, state.Kind);
        Assert.Equal("Map allowance: waiting for /timers data", state.PrimaryText);
        Assert.False(state.ShowLegend);
    }

    [Fact]
    public void DebugLoadingStatusNeverShowsReady()
    {
        var state = MapAllowanceHeaderPolicy.Evaluate(
            new MapAllowanceStatus(false, TimeSpan.Zero, null, "loading"),
            hasGatherJob: true,
            showUnavailableReason: true);

        Assert.Equal(MapAllowanceHeaderKind.Unavailable, state.Kind);
        Assert.NotEqual(MapAllowanceHeaderKind.Ready, state.Kind);
        Assert.Equal("Map allowance: waiting for /timers data", state.PrimaryText);
    }
}
