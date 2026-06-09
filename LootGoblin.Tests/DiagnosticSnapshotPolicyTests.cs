using System;
using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class DiagnosticSnapshotPolicyTests
{
    [Fact]
    public void WritesOnlyAfterActiveInterval()
    {
        var policy = new DiagnosticSnapshotPolicy(TimeSpan.FromSeconds(60));
        var start = new DateTime(2026, 6, 9, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(policy.ShouldWritePeriodic(start, activelyRunning: true));
        Assert.False(policy.ShouldWritePeriodic(start.AddSeconds(59), activelyRunning: true));
        Assert.True(policy.ShouldWritePeriodic(start.AddSeconds(60), activelyRunning: true));
        Assert.False(policy.ShouldWritePeriodic(start.AddSeconds(61), activelyRunning: true));
    }

    [Fact]
    public void InactiveStateStopsHeartbeatAndRestartsInterval()
    {
        var policy = new DiagnosticSnapshotPolicy(TimeSpan.FromSeconds(60));
        var start = new DateTime(2026, 6, 9, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(policy.ShouldWritePeriodic(start, activelyRunning: true));
        Assert.False(policy.ShouldWritePeriodic(start.AddSeconds(30), activelyRunning: false));
        Assert.False(policy.ShouldWritePeriodic(start.AddSeconds(90), activelyRunning: false));
        Assert.False(policy.ShouldWritePeriodic(start.AddSeconds(100), activelyRunning: true));
        Assert.True(policy.ShouldWritePeriodic(start.AddSeconds(160), activelyRunning: true));
    }
}
