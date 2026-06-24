using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class MapAllowanceCooldownPolicyTests
{
    [Fact]
    public void ReadyAllowanceStartsGather()
    {
        var status = new MapAllowanceStatus(true, TimeSpan.Zero, null, string.Empty);

        var decision = MapAllowanceCooldownPolicy.Evaluate(status, 10);

        Assert.Equal(MapAllowanceCooldownDecision.Ready, decision);
    }

    [Fact]
    public void ShortCooldownWaits()
    {
        var status = new MapAllowanceStatus(false, TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow.AddMinutes(10), string.Empty);

        var decision = MapAllowanceCooldownPolicy.Evaluate(status, 10);

        Assert.Equal(MapAllowanceCooldownDecision.Wait, decision);
    }

    [Fact]
    public void LongCooldownStops()
    {
        var status = new MapAllowanceStatus(false, TimeSpan.FromMinutes(11), DateTimeOffset.UtcNow.AddMinutes(11), string.Empty);

        var decision = MapAllowanceCooldownPolicy.Evaluate(status, 10);

        Assert.Equal(MapAllowanceCooldownDecision.Stop, decision);
    }

    [Fact]
    public void UnknownStatusKeepsExistingUnavailableBehavior()
    {
        var status = new MapAllowanceStatus(false, TimeSpan.Zero, null, "map allowance timer not loaded");

        var decision = MapAllowanceCooldownPolicy.Evaluate(status, 10);

        Assert.Equal(MapAllowanceCooldownDecision.Unavailable, decision);
    }
}
