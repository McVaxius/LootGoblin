using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class MapAllowanceContentsInfoParserTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-16T10:00:00Z");

    [Fact]
    public void RemainingDetailParsesCooldown()
    {
        var parsed = MapAllowanceContentsInfoParser.TryParse(
            new object?[] { "Next Map Allowance", 1, "\uE031 17:14 Remaining  (6/17 14:37)" },
            Now,
            out var status,
            out var source);

        Assert.True(parsed);
        Assert.Equal(MapAllowanceParseSource.AtkValues, source);
        Assert.False(status.IsReady);
        Assert.True(status.IsAvailable);
        Assert.Equal(TimeSpan.FromHours(17).Add(TimeSpan.FromMinutes(14)), status.Remaining);
        Assert.Equal(Now.AddHours(17).AddMinutes(14), status.NextAllowanceAtUtc);
    }

    [Fact]
    public void NormalizedIconAndControlCharsParseCooldown()
    {
        var parsed = MapAllowanceContentsInfoParser.TryParse(
            new object?[] { "\u0001\uE031 Next Map Allowance \u0002", 1, "\uE031\u000217:14 Remaining  (6/17 14:37)" },
            Now,
            out var status,
            out var source);

        Assert.True(parsed);
        Assert.Equal(MapAllowanceParseSource.AtkValues, source);
        Assert.False(status.IsReady);
        Assert.True(status.IsAvailable);
        Assert.Equal(TimeSpan.FromHours(17).Add(TimeSpan.FromMinutes(14)), status.Remaining);
    }

    [Fact]
    public void FixedIndexFallbackParsesCooldown()
    {
        var values = new object?[15];
        values[12] = "Next Map Allowance";
        values[13] = "Retrieving information...";
        values[14] = "\uE031 17:14 Remaining  (6/17 14:37)";

        var parsed = MapAllowanceContentsInfoParser.TryParse(
            values,
            Now,
            out var status,
            out var source);

        Assert.True(parsed);
        Assert.Equal(MapAllowanceParseSource.FixedAtkValues, source);
        Assert.False(status.IsReady);
        Assert.True(status.IsAvailable);
        Assert.Equal(TimeSpan.FromHours(17).Add(TimeSpan.FromMinutes(14)), status.Remaining);
    }

    [Fact]
    public void VisibleTextNodesParseCooldown()
    {
        var parsed = MapAllowanceContentsInfoParser.TryParseVisibleTextNodes(
            new[] { "Timers", "Next Map Allowance", "\uE031 17:14 Remaining  (6/17 14:37)" },
            Now,
            out var status,
            out var source);

        Assert.True(parsed);
        Assert.Equal(MapAllowanceParseSource.NodeTexts, source);
        Assert.False(status.IsReady);
        Assert.True(status.IsAvailable);
        Assert.Equal(TimeSpan.FromHours(17).Add(TimeSpan.FromMinutes(14)), status.Remaining);
    }

    [Fact]
    public void AvailableNowParsesReady()
    {
        var parsed = MapAllowanceContentsInfoParser.TryParse(
            new object?[] { "Next Map Allowance", 1, "Available Now" },
            Now,
            out var status,
            out var source);

        Assert.True(parsed);
        Assert.Equal(MapAllowanceParseSource.AtkValues, source);
        Assert.True(status.IsReady);
        Assert.True(status.IsAvailable);
        Assert.Equal(TimeSpan.Zero, status.Remaining);
    }

    [Fact]
    public void MissingMapLabelIsUnavailable()
    {
        var parsed = MapAllowanceContentsInfoParser.TryParse(
            new object?[] { "Other Timer", "Available Now" },
            Now,
            out var status);

        Assert.False(parsed);
        Assert.False(status.IsReady);
        Assert.False(status.IsAvailable);
    }

    [Fact]
    public void RetrievingInformationIsUnavailable()
    {
        var parsed = MapAllowanceContentsInfoParser.TryParse(
            new object?[] { "Next Map Allowance", "Retrieving information..." },
            Now,
            out var status);

        Assert.False(parsed);
        Assert.False(status.IsReady);
        Assert.False(status.IsAvailable);
    }

    [Fact]
    public void MalformedDetailIsUnavailable()
    {
        var parsed = MapAllowanceContentsInfoParser.TryParse(
            new object?[] { "Next Map Allowance", "soon" },
            Now,
            out var status);

        Assert.False(parsed);
        Assert.False(status.IsReady);
        Assert.False(status.IsAvailable);
    }
}
