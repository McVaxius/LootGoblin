using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class MapAllowanceStatusTests
{
    [Fact]
    public void FutureNextAllowanceRecomputesRemainingFromNow()
    {
        var now = DateTimeOffset.Parse("2026-06-16T10:00:00Z");
        var status = new MapAllowanceStatus(
            false,
            TimeSpan.FromHours(12),
            now.AddHours(17).AddMinutes(5),
            string.Empty);

        var live = status.WithLiveRemaining(now);

        Assert.False(live.IsReady);
        Assert.Equal(TimeSpan.FromHours(17).Add(TimeSpan.FromMinutes(5)), live.Remaining);
        Assert.Equal("17h 05m", live.CompactText);
    }

    [Fact]
    public void ExpiredNextAllowanceBecomesReady()
    {
        var now = DateTimeOffset.Parse("2026-06-16T10:00:00Z");
        var status = new MapAllowanceStatus(
            false,
            TimeSpan.FromMinutes(1),
            now.AddSeconds(-1),
            string.Empty);

        var live = status.WithLiveRemaining(now);

        Assert.True(live.IsReady);
        Assert.Equal(TimeSpan.Zero, live.Remaining);
        Assert.Equal("ready", live.CompactText);
    }

    [Fact]
    public void UnavailableStatusStaysUnchanged()
    {
        var now = DateTimeOffset.Parse("2026-06-16T10:00:00Z");
        var status = new MapAllowanceStatus(
            false,
            TimeSpan.Zero,
            now.AddHours(1),
            "map allowance timer not loaded");

        var live = status.WithLiveRemaining(now);

        Assert.Equal(status, live);
        Assert.Equal("unknown", live.CompactText);
    }

    [Fact]
    public void ReadyStatusStaysReady()
    {
        var now = DateTimeOffset.Parse("2026-06-16T10:00:00Z");
        var status = new MapAllowanceStatus(
            true,
            TimeSpan.Zero,
            now.AddHours(1),
            string.Empty);

        var live = status.WithLiveRemaining(now);

        Assert.Equal(status, live);
        Assert.Equal("ready", live.CompactText);
    }
}
