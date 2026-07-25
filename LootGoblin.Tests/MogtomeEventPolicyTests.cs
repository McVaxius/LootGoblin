using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class MogtomeEventPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 31, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Moogle Treasure Trove")]
    [InlineData("The Moogle Treasure Trove")]
    [InlineData("Moogle Treasure Trove - The Hunt for Goetia")]
    [InlineData("MOOGLE TREASURE TROVE")]
    public void IsActive_MatchesKnownNameVariants(string name)
    {
        var events = new[]
        {
            new MogtomeEvent(name, Now.AddHours(-1), Now.AddHours(1)),
        };

        Assert.True(MogtomeEventPolicy.IsActive(events, Now));
    }

    [Fact]
    public void IsActive_UsesInclusiveBeginAndExclusiveEnd()
    {
        var activeEvent = new MogtomeEvent("Moogle Treasure Trove", Now, Now.AddHours(1));

        Assert.True(MogtomeEventPolicy.IsActive(new[] { activeEvent }, Now));
        Assert.True(MogtomeEventPolicy.IsActive(new[] { activeEvent }, Now.AddHours(1).AddTicks(-1)));
        Assert.False(MogtomeEventPolicy.IsActive(new[] { activeEvent }, Now.AddHours(1)));
    }

    [Fact]
    public void IsActive_IgnoresUnrelatedEvents()
    {
        var events = new[]
        {
            new MogtomeEvent("The Rising", Now.AddHours(-1), Now.AddHours(1)),
        };

        Assert.False(MogtomeEventPolicy.IsActive(events, Now));
    }

    [Fact]
    public void TryParseFeed_ParsesUtcRows()
    {
        const string json =
            """[{"name":"The Moogle Treasure Trove","begin":"2026-03-31T00:00:00Z","end":"2026-04-28T00:00:00Z"}]""";

        var parsed = MogtomeEventPolicy.TryParseFeed(json, out var events);

        Assert.True(parsed);
        var activeEvent = Assert.Single(events);
        Assert.Equal(TimeSpan.Zero, activeEvent.Begin.Offset);
        Assert.Equal(TimeSpan.Zero, activeEvent.End.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""[{"name":"Moogle Treasure Trove","begin":"bad","end":"2026-04-28T00:00:00Z"}]""")]
    [InlineData("""[{"name":"Moogle Treasure Trove","begin":"2026-04-28T00:00:00Z","end":"2026-03-31T00:00:00Z"}]""")]
    public void TryParseFeed_FailsClosedForMalformedInput(string json)
    {
        Assert.False(MogtomeEventPolicy.TryParseFeed(json, out var events));
        Assert.Empty(events);
    }
}
