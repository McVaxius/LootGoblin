using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class AlexandritePolicyTests
{
    [Fact]
    public void PoeticsOnlyMaxRunsUsesSeventyFivePoeticsPerMap()
    {
        var state = AlexandritePolicy.EvaluateRunLimit(
            requestedRuns: 99,
            inventoryMapCount: 0,
            hasActiveMysteriousMap: false,
            currentPoetics: 225);

        Assert.Equal(3, state.MaxRunnableRuns);
        Assert.Equal(3, state.PurchasableMapCount);
        Assert.Equal(3, state.RequestedRuns);
        Assert.True(state.CanStart);
    }

    [Fact]
    public void InventoryAndActiveMapAddToMaxRuns()
    {
        var state = AlexandritePolicy.EvaluateRunLimit(
            requestedRuns: 5,
            inventoryMapCount: 2,
            hasActiveMysteriousMap: true,
            currentPoetics: 150);

        Assert.Equal(5, state.MaxRunnableRuns);
        Assert.Equal(2, state.InventoryMapCount);
        Assert.Equal(1, state.ActiveMapCount);
        Assert.Equal(2, state.PurchasableMapCount);
        Assert.Equal(5, state.RequestedRuns);
    }

    [Fact]
    public void InputClampsToMaxRunnable()
    {
        var state = AlexandritePolicy.EvaluateRunLimit(
            requestedRuns: 10,
            inventoryMapCount: 1,
            hasActiveMysteriousMap: false,
            currentPoetics: 150);

        Assert.Equal(3, state.MaxRunnableRuns);
        Assert.Equal(3, state.RequestedRuns);
    }

    [Fact]
    public void PositiveMaxKeepsRequestedRunsAtLeastOne()
    {
        var state = AlexandritePolicy.EvaluateRunLimit(
            requestedRuns: 0,
            inventoryMapCount: 1,
            hasActiveMysteriousMap: false,
            currentPoetics: 0);

        Assert.Equal(1, state.MaxRunnableRuns);
        Assert.Equal(1, state.RequestedRuns);
        Assert.True(state.CanStart);
    }

    [Fact]
    public void ZeroRunnableDisablesStart()
    {
        var state = AlexandritePolicy.EvaluateRunLimit(
            requestedRuns: 10,
            inventoryMapCount: 0,
            hasActiveMysteriousMap: false,
            currentPoetics: 0);

        Assert.Equal(0, state.MaxRunnableRuns);
        Assert.Equal(0, state.RequestedRuns);
        Assert.False(state.CanStart);
    }
}
