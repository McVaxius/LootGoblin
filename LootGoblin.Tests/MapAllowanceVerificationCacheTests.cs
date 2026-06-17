using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class MapAllowanceVerificationCacheTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-16T10:00:00Z");

    [Fact]
    public void NoVerifiedCacheIsUnavailableAndNotReady()
    {
        var cache = new MapAllowanceVerificationCache();

        var found = cache.TryGet(123, Now, out var status);

        Assert.False(found);
        Assert.False(status.IsAvailable);
        Assert.False(status.IsReady);
        Assert.Equal("map allowance timer not loaded", status.Error);
    }

    [Fact]
    public void VerifiedCooldownStaysCooldownWithoutTimersWindow()
    {
        var cache = new MapAllowanceVerificationCache();
        cache.Store(
            123,
            new MapAllowanceStatus(false, TimeSpan.FromHours(17), Now.AddHours(17), string.Empty),
            Now);

        var found = cache.TryGet(123, Now.AddMinutes(30), out var status);

        Assert.True(found);
        Assert.True(status.IsAvailable);
        Assert.False(status.IsReady);
        Assert.Equal(TimeSpan.FromHours(16).Add(TimeSpan.FromMinutes(30)), status.Remaining);
        Assert.Equal(Now.AddHours(17), status.NextAllowanceAtUtc);
    }

    [Fact]
    public void VerifiedCooldownBecomesReadyAtAllowanceTime()
    {
        var cache = new MapAllowanceVerificationCache();
        cache.Store(
            123,
            new MapAllowanceStatus(false, TimeSpan.FromMinutes(1), Now.AddMinutes(1), string.Empty),
            Now);

        var found = cache.TryGet(123, Now.AddMinutes(1), out var status);

        Assert.True(found);
        Assert.True(status.IsAvailable);
        Assert.True(status.IsReady);
        Assert.Equal(TimeSpan.Zero, status.Remaining);
    }

    [Fact]
    public void ContentIdChangeClearsVerifiedCache()
    {
        var cache = new MapAllowanceVerificationCache();
        cache.Store(
            123,
            new MapAllowanceStatus(true, TimeSpan.Zero, null, string.Empty),
            Now);

        var found = cache.TryGet(456, Now, out var status);

        Assert.False(found);
        Assert.False(status.IsAvailable);
        Assert.False(status.IsReady);
    }

    [Fact]
    public void MarkConsumedSetsExactEighteenHourCooldown()
    {
        var cache = new MapAllowanceVerificationCache();

        var status = cache.MarkConsumed(123, Now);

        Assert.True(status.IsAvailable);
        Assert.False(status.IsReady);
        Assert.Equal(TimeSpan.FromHours(18), status.Remaining);
        Assert.Equal(Now.AddHours(18), status.NextAllowanceAtUtc);
    }
}
