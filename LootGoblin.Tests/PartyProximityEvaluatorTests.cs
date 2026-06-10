using System.Numerics;
using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public class PartyProximityEvaluatorTests
{
    [Fact]
    public void SixPersonSameTerritoryPartyWithinRangeProceedsImmediately()
    {
        var members = new List<PartyProximityMember> { Local() };
        members.AddRange(Enumerable.Range(1, 5).Select(index => Other($"Member {index}", (ulong)index, index, index)));

        var result = Evaluate(members);

        Assert.True(result.CanProceed);
        Assert.Equal(5, result.NearbyOthers);
        Assert.Equal(5, result.RequiredOthers);
    }

    [Fact]
    public void PartyListPositionDoesNotRequireLoadedGameObject()
    {
        var result = Evaluate(
            new[]
            {
                Local(),
                Other("Party List Only", 2, 5, 0, loaded: false, source: PartyPositionSource.PartyList),
            });

        Assert.True(result.CanProceed);
        Assert.Equal(1, result.ResolvedSameTerritoryCount);
        Assert.Equal(PartyProximityMemberStatus.Nearby, result.Members[1].Status);
    }

    [Fact]
    public void SameNameEntriesRemainSeparateAndFarMemberBlocks()
    {
        var result = Evaluate(
            new[]
            {
                Local(),
                Other("Same Name", 2, 5, 0),
                Other("Same Name", 3, 25, 0),
            });

        Assert.False(result.CanProceed);
        Assert.Equal(2, result.TotalOthers);
        Assert.Equal(1, result.SameTerritoryFarCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameTerritoryFarMemberBlocksBeforeAndAfterTimeout(bool timedOut)
    {
        var result = Evaluate(
            new[]
            {
                Local(),
                Other("Far", 2, 25, 0),
            },
            timedOut: timedOut);

        Assert.False(result.CanProceed);
        Assert.Equal(1, result.SameTerritoryFarCount);
    }

    [Fact]
    public void UnresolvedAndOutOfTerritoryBlockBeforeTimeoutThenGuardedRecoveryProceeds()
    {
        var members = new[]
        {
            Local(),
            Other("Nearby", 2, 5, 0),
            Other("Unresolved", 3, 0, 0, territory: PartyTerritoryStatus.Unknown, hasPosition: false),
            Other("Elsewhere", 4, 0, 0, territory: PartyTerritoryStatus.Different, hasPosition: false),
        };

        var beforeTimeout = Evaluate(members);
        var afterTimeout = Evaluate(members, timedOut: true);

        Assert.False(beforeTimeout.CanProceed);
        Assert.True(afterTimeout.CanProceed);
        Assert.True(afterTimeout.GuardedRecoveryUsed);
        Assert.Equal(1, afterTimeout.UnresolvedCount);
        Assert.Equal(1, afterTimeout.OutOfTerritoryCount);
    }

    [Fact]
    public void InvalidSnapshotAndZeroResolvedMembersContinueHolding()
    {
        var members = new[]
        {
            Local(),
            Other("Unresolved", 2, 0, 0, territory: PartyTerritoryStatus.Unknown, hasPosition: false),
            Other("Elsewhere", 3, 0, 0, territory: PartyTerritoryStatus.Different, hasPosition: false),
        };

        Assert.False(Evaluate(members, snapshotValid: false, timedOut: true).CanProceed);
        Assert.False(Evaluate(members, timedOut: true).CanProceed);
    }

    [Fact]
    public void ThresholdModeUsesSameXzClassifications()
    {
        var members = new[]
        {
            Local(),
            Other("Nearby", 2, 5, 0, loaded: false, source: PartyPositionSource.PartyList),
            Other("Far", 3, 25, 0),
        };

        var fullParty = Evaluate(members);
        var threshold = Evaluate(members, useThreshold: true, requiredOthers: 1);

        Assert.False(fullParty.CanProceed);
        Assert.True(threshold.CanProceed);
        Assert.Equal(fullParty.NearbyOthers, threshold.NearbyOthers);
        Assert.Equal(fullParty.SameTerritoryFarCount, threshold.SameTerritoryFarCount);
    }

    [Fact]
    public void ThresholdTwoAllowsTwoNearbyDespiteFarOrUnresolvedOthers()
    {
        var members = new[]
        {
            Local(),
            Other("Nearby 1", 2, 5, 0),
            Other("Nearby 2", 3, 7, 0),
            Other("Far 1", 4, 25, 0),
            Other("Far 2", 5, 35, 0),
            Other("Unresolved 1", 6, 0, 0, territory: PartyTerritoryStatus.Unknown, hasPosition: false),
            Other("Unresolved 2", 7, 0, 0, territory: PartyTerritoryStatus.Unknown, hasPosition: false),
            Other("Elsewhere", 8, 0, 0, territory: PartyTerritoryStatus.Different, hasPosition: false),
        };

        var result = Evaluate(members, useThreshold: true, requiredOthers: 2);

        Assert.True(result.CanProceed);
        Assert.Equal(2, result.NearbyOthers);
        Assert.Equal(2, result.RequiredOthers);
        Assert.Equal(7, result.TotalOthers);
    }

    [Fact]
    public void ThresholdTwoHoldsWithOnlyOneNearbyOther()
    {
        var members = new[]
        {
            Local(),
            Other("Nearby", 2, 5, 0),
            Other("Far", 3, 25, 0),
            Other("Unresolved", 4, 0, 0, territory: PartyTerritoryStatus.Unknown, hasPosition: false),
        };

        var result = Evaluate(members, useThreshold: true, requiredOthers: 2);

        Assert.False(result.CanProceed);
        Assert.Equal(1, result.NearbyOthers);
        Assert.Equal(2, result.RequiredOthers);
    }

    [Fact]
    public void TimeoutInThresholdModeStillRequiresConfiguredNearbyOthers()
    {
        var enoughNearby = new[]
        {
            Local(),
            Other("Nearby 1", 2, 5, 0),
            Other("Nearby 2", 3, 7, 0),
            Other("Far", 4, 25, 0),
        };
        var notEnoughNearby = new[]
        {
            Local(),
            Other("Nearby", 2, 5, 0),
            Other("Far", 3, 25, 0),
        };

        var allowed = Evaluate(enoughNearby, timedOut: true, useThreshold: true, requiredOthers: 2);
        var held = Evaluate(notEnoughNearby, timedOut: true, useThreshold: true, requiredOthers: 2);

        Assert.True(allowed.CanProceed);
        Assert.False(allowed.GuardedRecoveryUsed);
        Assert.Equal(2, allowed.RequiredOthers);
        Assert.False(held.CanProceed);
        Assert.Equal(2, held.RequiredOthers);
    }

    [Fact]
    public void FullPartyModeStillRequiresAllNearbyOthersBeforeTimeout()
    {
        var result = Evaluate(
            new[]
            {
                Local(),
                Other("Nearby 1", 2, 5, 0),
                Other("Nearby 2", 3, 7, 0),
                Other("Far", 4, 25, 0),
            });

        Assert.False(result.CanProceed);
        Assert.Equal(2, result.NearbyOthers);
        Assert.Equal(3, result.RequiredOthers);
    }

    [Fact]
    public void MountAndPostDungeonGatesRequireLoadedSameTerritoryMembers()
    {
        Assert.True(PartyGateSemantics.IsLoadedSameTerritory(true, PartyTerritoryStatus.Same));
        Assert.False(PartyGateSemantics.IsLoadedSameTerritory(false, PartyTerritoryStatus.Same));
        Assert.False(PartyGateSemantics.IsLoadedSameTerritory(true, PartyTerritoryStatus.Different));

        Assert.True(PartyGateSemantics.IsLoadedSameTerritoryMounted(true, PartyTerritoryStatus.Same, true));
        Assert.False(PartyGateSemantics.IsLoadedSameTerritoryMounted(false, PartyTerritoryStatus.Same, true));
        Assert.False(PartyGateSemantics.IsLoadedSameTerritoryMounted(true, PartyTerritoryStatus.Same, false));

        Assert.True(PartyGateSemantics.HasRequiredOthers(2, 7, useCountThreshold: true, configuredRequiredOthers: 2));
        Assert.False(PartyGateSemantics.HasRequiredOthers(1, 7, useCountThreshold: true, configuredRequiredOthers: 2));
        Assert.True(PartyGateSemantics.HasRequiredOthers(7, 7, useCountThreshold: false, configuredRequiredOthers: 2));
        Assert.False(PartyGateSemantics.HasRequiredOthers(2, 7, useCountThreshold: false, configuredRequiredOthers: 2));
    }

    private static PartyProximityResult Evaluate(
        IReadOnlyList<PartyProximityMember> members,
        bool snapshotValid = true,
        bool timedOut = false,
        bool useThreshold = false,
        int requiredOthers = 7)
        => PartyProximityEvaluator.Evaluate(
            snapshotValid,
            members,
            10,
            useThreshold,
            requiredOthers,
            timedOut);

    private static PartyProximityMember Local()
        => new(
            "Local",
            1,
            10,
            100,
            true,
            PartyTerritoryStatus.Same,
            true,
            true,
            Vector3.Zero,
            PartyPositionSource.DirectActor);

    private static PartyProximityMember Other(
        string name,
        ulong contentId,
        float x,
        float z,
        PartyTerritoryStatus territory = PartyTerritoryStatus.Same,
        bool loaded = true,
        bool hasPosition = true,
        PartyPositionSource source = PartyPositionSource.DirectActor)
        => new(
            name,
            contentId,
            10,
            (uint)(100 + contentId),
            false,
            territory,
            loaded,
            hasPosition,
            new Vector3(x, 100, z),
            hasPosition ? source : PartyPositionSource.None);
}
