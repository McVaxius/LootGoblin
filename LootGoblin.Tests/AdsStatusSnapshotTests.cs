using System.Text.Json;
using System.Text.Json.Nodes;
using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class AdsStatusSnapshotTests
{
    private const string Complete = """
        {"ownershipMode":"Observing","executionPhase":"ObservingOnly","inInstancedDuty":true,
         "unsafeTransition":false,"territoryTypeId":1044,"contentFinderConditionId":831,
         "completionTreasureSweep":{"territoryTypeId":1044,"contentFinderConditionId":831,"withoutExit":true,"completed":true}}
        """;

    [Fact]
    public void MatchingNormalSweepReleaseIsTheOnlyPositiveResult()
    {
        var captured = DateTime.UtcNow;
        var status = AdsStatusSnapshot.Parse(Complete, captured);
        Assert.True(status.HasCompletedTreasureSweepFor(1044, 831));
        Assert.Equal(captured, status.CapturedAtUtc);
        Assert.False(status.IsOwned);
        Assert.False(status.HasCompletedTreasureSweepFor(1048, 831));
        Assert.False(status.HasCompletedTreasureSweepFor(1044, 999));
        Assert.False(status.HasCompletedTreasureSweepFor(0, 0));
        Assert.False(AdsStatusSnapshot.Empty.HasCompletedTreasureSweepFor(1044, 831));
    }

    [Theory]
    [InlineData("Leaving")]
    [InlineData("OwnedStartInside")]
    [InlineData("Failed")]
    [InlineData("Idle")]
    [InlineData("")]
    public void OwnershipOrFailureNeverStandsInForSweepCompletion(string ownership)
    {
        var root = JsonNode.Parse(Complete)!;
        root["ownershipMode"] = ownership;
        Assert.False(Parse(root).HasCompletedTreasureSweepFor(1044, 831));
    }

    [Theory]
    [InlineData("territoryTypeId", "0")]
    [InlineData("territoryTypeId", "1048")]
    [InlineData("territoryTypeId", "\"1044\"")]
    [InlineData("territoryTypeId", "-1")]
    [InlineData("contentFinderConditionId", "999")]
    [InlineData("contentFinderConditionId", "null")]
    [InlineData("withoutExit", "false")]
    [InlineData("withoutExit", "\"true\"")]
    [InlineData("completed", "false")]
    [InlineData("completed", "1")]
    [InlineData("completed", "null")]
    public void MissingMalformedPendingOrDifferentDutySweepHolds(string field, string value)
    {
        var root = JsonNode.Parse(Complete)!;
        root["completionTreasureSweep"]![field] = JsonNode.Parse(value);
        Assert.False(Parse(root).HasCompletedTreasureSweepFor(1044, 831));
        root["completionTreasureSweep"]!.AsObject().Remove(field);
        Assert.False(Parse(root).HasCompletedTreasureSweepFor(1044, 831));
    }

    [Theory]
    [InlineData("unsafeTransition", "true")]
    [InlineData("unsafeTransition", "\"false\"")]
    [InlineData("inInstancedDuty", "false")]
    [InlineData("inInstancedDuty", "\"true\"")]
    [InlineData("territoryTypeId", "1048")]
    [InlineData("contentFinderConditionId", "999")]
    public void CurrentDutySafetyTruthMustAlsoMatch(string field, string value)
    {
        var root = JsonNode.Parse(Complete)!;
        root[field] = JsonNode.Parse(value);
        Assert.False(Parse(root).HasCompletedTreasureSweepFor(1044, 831));
        root.AsObject().Remove(field);
        Assert.False(Parse(root).HasCompletedTreasureSweepFor(1044, 831));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    public void MissingOrUnsupportedSweepResultHolds(string result)
    {
        var root = JsonNode.Parse(Complete)!;
        root["completionTreasureSweep"] = JsonNode.Parse(result);
        Assert.False(Parse(root).HasCompletedTreasureSweepFor(1044, 831));
        root.AsObject().Remove("completionTreasureSweep");
        Assert.False(Parse(root).HasCompletedTreasureSweepFor(1044, 831));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("malformed")]
    public void InvalidStatusIsRejected(string json)
        => Assert.ThrowsAny<JsonException>(() => AdsStatusSnapshot.Parse(json, DateTime.UtcNow));

    private static AdsStatusSnapshot Parse(JsonNode root)
        => AdsStatusSnapshot.Parse(root.ToJsonString(), DateTime.UtcNow);
}
