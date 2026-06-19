using LootGoblin.Services;
using System;
using System.Numerics;
using Xunit;

namespace LootGoblin.Tests;

public sealed class AlexandritePolicyTests
{
    private static readonly Vector3 AurianaPosition = new(62.98f, 31.29f, -737.07f);

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

    [Fact]
    public void BuyStartSkipsLifestreamWhenNearAurianaInMorDhona()
    {
        var position = new Vector3(AurianaPosition.X + 49.9f, AurianaPosition.Y, AurianaPosition.Z);

        var action = AlexandritePolicy.EvaluateBuyStart(
            inventoryMapCount: 0,
            territoryId: AlexandritePolicy.MorDhonaTerritoryId,
            playerPosition: position,
            aurianaPosition: AurianaPosition);

        Assert.Equal(AlexandriteBuyStartAction.SkipLifestream, action);
    }

    [Fact]
    public void BuyStartUsesLifestreamAtFiftyYalmsFromAuriana()
    {
        var position = new Vector3(AurianaPosition.X + 50.0f, AurianaPosition.Y, AurianaPosition.Z);

        var action = AlexandritePolicy.EvaluateBuyStart(
            inventoryMapCount: 0,
            territoryId: AlexandritePolicy.MorDhonaTerritoryId,
            playerPosition: position,
            aurianaPosition: AurianaPosition);

        Assert.Equal(AlexandriteBuyStartAction.UseLifestream, action);
    }

    [Fact]
    public void BuyStartUsesLifestreamOutsideMorDhona()
    {
        var action = AlexandritePolicy.EvaluateBuyStart(
            inventoryMapCount: 0,
            territoryId: 148,
            playerPosition: AurianaPosition,
            aurianaPosition: AurianaPosition);

        Assert.Equal(AlexandriteBuyStartAction.UseLifestream, action);
    }

    [Fact]
    public void BuyStartUsesLifestreamWhenPlayerPositionUnknown()
    {
        var action = AlexandritePolicy.EvaluateBuyStart(
            inventoryMapCount: 0,
            territoryId: AlexandritePolicy.MorDhonaTerritoryId,
            playerPosition: null,
            aurianaPosition: AurianaPosition);

        Assert.Equal(AlexandriteBuyStartAction.UseLifestream, action);
    }

    [Fact]
    public void BuyStartUsesInventoryMapBeforeLifestreamPolicy()
    {
        var action = AlexandritePolicy.EvaluateBuyStart(
            inventoryMapCount: 1,
            territoryId: 148,
            playerPosition: null,
            aurianaPosition: AurianaPosition);

        Assert.Equal(AlexandriteBuyStartAction.UseInventoryMap, action);
    }

    [Fact]
    public void PostPurchaseMapCountWinsWithoutDialogs()
    {
        var action = AlexandritePolicy.EvaluatePostPurchase(
            inventoryMapCount: 1,
            selectYesnoVisible: false,
            elapsed: TimeSpan.FromSeconds(31),
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(AlexandritePostPurchaseAction.HandoffInventoryMap, action);
    }

    [Fact]
    public void PostPurchaseVisibleYesnoWithNoMapClicksAndWaits()
    {
        var action = AlexandritePolicy.EvaluatePostPurchase(
            inventoryMapCount: 0,
            selectYesnoVisible: true,
            elapsed: TimeSpan.FromSeconds(5),
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(AlexandritePostPurchaseAction.ClickPurchaseConfirm, action);
    }

    [Fact]
    public void PostPurchaseNoDialogAndNoMapWaitsBeforeTimeout()
    {
        var action = AlexandritePolicy.EvaluatePostPurchase(
            inventoryMapCount: 0,
            selectYesnoVisible: false,
            elapsed: TimeSpan.FromSeconds(29),
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(AlexandritePostPurchaseAction.Wait, action);
    }

    [Fact]
    public void PostPurchaseNoDialogAndNoMapFailsAfterTimeout()
    {
        var action = AlexandritePolicy.EvaluatePostPurchase(
            inventoryMapCount: 0,
            selectYesnoVisible: false,
            elapsed: TimeSpan.FromSeconds(30),
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(AlexandritePostPurchaseAction.FailTimeout, action);
    }

    [Fact]
    public void LifestreamArrivalWaitsForPlayerReadiness()
    {
        var decision = AlexandritePolicy.EvaluateLifestreamArrival(
            loading: false,
            territoryId: AlexandritePolicy.MorDhonaTerritoryId,
            settleElapsed: TimeSpan.FromSeconds(2),
            requiredSettleDelay: TimeSpan.FromSeconds(1),
            hasPlayer: true,
            playerIsCasting: false,
            conditionCasting: false,
            playerAvailable: false);

        Assert.False(decision.CanAdvance);
        Assert.Equal(AlexandriteLifestreamArrivalWaitReason.PlayerUnavailable, decision.WaitReason);
    }

    [Fact]
    public void ApproachMountsBeforeMovingWhenUnmounted()
    {
        var action = AlexandritePolicy.EvaluateApproach(
            hasPlayerPosition: true,
            distanceToAuriana: 100f,
            mounted: false,
            mounting: false);

        Assert.Equal(AlexandriteApproachAction.Mount, action);
    }

    [Fact]
    public void ApproachWaitsThroughMountingBeforeMoving()
    {
        var action = AlexandritePolicy.EvaluateApproach(
            hasPlayerPosition: true,
            distanceToAuriana: 100f,
            mounted: false,
            mounting: true);

        Assert.Equal(AlexandriteApproachAction.WaitForMounting, action);
    }

    [Fact]
    public void ApproachDismountsAtAurianaBeforeInteract()
    {
        var mountedAction = AlexandritePolicy.EvaluateApproach(
            hasPlayerPosition: true,
            distanceToAuriana: 4.9f,
            mounted: true,
            mounting: false);
        var unmountedAction = AlexandritePolicy.EvaluateApproach(
            hasPlayerPosition: true,
            distanceToAuriana: 4.9f,
            mounted: false,
            mounting: false);

        Assert.Equal(AlexandriteApproachAction.Dismount, mountedAction);
        Assert.Equal(AlexandriteApproachAction.Interact, unmountedAction);
    }

    [Fact]
    public void PendingMysteriousMapBypassesStartMapRefresh()
    {
        var bypass = AlexandritePolicy.ShouldBypassStartMapRefresh(AlexandritePolicy.MysteriousMapItemId);

        Assert.True(bypass);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(6688u)]
    public void NonMysteriousPendingMapUsesNormalStartRefresh(uint pendingMapTargetItemId)
    {
        var bypass = AlexandritePolicy.ShouldBypassStartMapRefresh(pendingMapTargetItemId);

        Assert.False(bypass);
    }
}
