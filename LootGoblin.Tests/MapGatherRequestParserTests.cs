using LootGoblin.Models;
using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class MapGatherRequestParserTests
{
    [Fact]
    public void ResolvesGatherableMapByItemId()
    {
        var result = MapGatherRequestParser.ParseCommand("43556");

        Assert.True(result.Success);
        Assert.Equal(43556u, result.ItemId);
        Assert.Equal("Timeworn Loboskin Map", result.Map?.Name);
        Assert.False(result.RunAfterGather);
    }

    [Fact]
    public void ResolvesGatherableMapByExactName()
    {
        var result = MapGatherRequestParser.ParseCommand("Timeworn Loboskin Map");

        Assert.True(result.Success);
        Assert.Equal(43556u, result.ItemId);
    }

    [Fact]
    public void ResolvesGatherableMapByUniquePartialName()
    {
        var result = MapGatherRequestParser.ParseCommand("--run Loboskin");

        Assert.True(result.Success);
        Assert.Equal(43556u, result.ItemId);
        Assert.True(result.RunAfterGather);
    }

    [Fact]
    public void RejectsAmbiguousPartialName()
    {
        var result = MapGatherRequestParser.ParseCommand("Timeworn");

        Assert.False(result.Success);
        Assert.Contains("ambiguous", result.ErrorMessage);
        Assert.True(result.Matches.Count > 1);
    }

    [Fact]
    public void RejectsNonGatherableMap()
    {
        var result = MapGatherRequestParser.ParseCommand("Mysterious Map");

        Assert.False(result.Success);
        Assert.Contains("cannot be gathered", result.ErrorMessage);
        Assert.Equal(7884u, result.ItemId);
    }

    [Fact]
    public void CatalogExportsOnlyGatherableMapsAndMarksSoloOutdoorSafety()
    {
        var maps = MapGatherCatalog.GetGatherableMaps();

        Assert.All(maps, map => Assert.True(map.IsGatherable));
        var loboskin = Assert.Single(maps, map => map.ItemId == 43556);
        Assert.True(loboskin.SoloOutdoorSafe);

        var braaxskin = Assert.Single(maps, map => map.ItemId == 43557);
        Assert.False(braaxskin.SoloOutdoorSafe);
    }
}
