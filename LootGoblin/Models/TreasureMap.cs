using System.Collections.Generic;

namespace LootGoblin.Models;

public enum MapTier
{
    Unknown,
    Solo,
    Party,
    Raid,
}

public class TreasureMapInfo
{
    public uint ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public MapTier Tier { get; init; }
    public int MinLevel { get; init; }
    public string Expansion { get; init; } = string.Empty;
    public bool HasDungeon { get; init; }
}

public static class TreasureMapData
{
    public static readonly Dictionary<uint, TreasureMapInfo> KnownMaps = new()
    {
        // A Realm Reborn
        { 6688, new TreasureMapInfo { ItemId = 6688, Name = "Timeworn Leather Map", Tier = MapTier.Solo, MinLevel = 40, Expansion = "ARR", HasDungeon = false } },
        { 6689, new TreasureMapInfo { ItemId = 6689, Name = "Timeworn Goatskin Map", Tier = MapTier.Solo, MinLevel = 45, Expansion = "ARR", HasDungeon = false } },
        { 6690, new TreasureMapInfo { ItemId = 6690, Name = "Timeworn Toadskin Map", Tier = MapTier.Solo, MinLevel = 50, Expansion = "ARR", HasDungeon = false } },
        { 6691, new TreasureMapInfo { ItemId = 6691, Name = "Timeworn Boarskin Map", Tier = MapTier.Party, MinLevel = 50, Expansion = "ARR", HasDungeon = false } },
        { 6692, new TreasureMapInfo { ItemId = 6692, Name = "Timeworn Peisteskin Map", Tier = MapTier.Party, MinLevel = 50, Expansion = "ARR", HasDungeon = false } },

        // Heavensward
        { 12241, new TreasureMapInfo { ItemId = 12241, Name = "Timeworn Archaeoskin Map", Tier = MapTier.Solo, MinLevel = 55, Expansion = "HW", HasDungeon = false } },
        { 12242, new TreasureMapInfo { ItemId = 12242, Name = "Timeworn Wyvernskin Map", Tier = MapTier.Party, MinLevel = 60, Expansion = "HW", HasDungeon = false } },
        { 12243, new TreasureMapInfo { ItemId = 12243, Name = "Timeworn Dragonskin Map", Tier = MapTier.Party, MinLevel = 60, Expansion = "HW", HasDungeon = true } },

        // Stormblood
        { 17835, new TreasureMapInfo { ItemId = 17835, Name = "Timeworn Gaganaskin Map", Tier = MapTier.Solo, MinLevel = 65, Expansion = "SB", HasDungeon = false } },
        { 17836, new TreasureMapInfo { ItemId = 17836, Name = "Timeworn Gazelleskin Map", Tier = MapTier.Party, MinLevel = 70, Expansion = "SB", HasDungeon = true } },
        { 24794, new TreasureMapInfo { ItemId = 24794, Name = "Seemingly Special Timeworn Map", Tier = MapTier.Party, MinLevel = 70, Expansion = "SB", HasDungeon = true } },

        // Shadowbringers
        { 26744, new TreasureMapInfo { ItemId = 26744, Name = "Timeworn Gliderskin Map", Tier = MapTier.Solo, MinLevel = 75, Expansion = "ShB", HasDungeon = false } },
        { 26745, new TreasureMapInfo { ItemId = 26745, Name = "Timeworn Zonureskin Map", Tier = MapTier.Party, MinLevel = 80, Expansion = "ShB", HasDungeon = true } },
        { 33328, new TreasureMapInfo { ItemId = 33328, Name = "Ostensibly Special Timeworn Map", Tier = MapTier.Party, MinLevel = 80, Expansion = "ShB", HasDungeon = true } },

        // Endwalker
        { 36611, new TreasureMapInfo { ItemId = 36611, Name = "Timeworn Saigaskin Map", Tier = MapTier.Solo, MinLevel = 85, Expansion = "EW", HasDungeon = false } },
        { 36612, new TreasureMapInfo { ItemId = 36612, Name = "Timeworn Kumbhiraskin Map", Tier = MapTier.Party, MinLevel = 90, Expansion = "EW", HasDungeon = true } },
        { 39593, new TreasureMapInfo { ItemId = 39593, Name = "Potentially Special Timeworn Map", Tier = MapTier.Party, MinLevel = 90, Expansion = "EW", HasDungeon = true } },
        { 39918, new TreasureMapInfo { ItemId = 39918, Name = "Conceivably Special Timeworn Map", Tier = MapTier.Party, MinLevel = 90, Expansion = "EW", HasDungeon = true } },

        // Dawntrail
        { 39591, new TreasureMapInfo { ItemId = 39591, Name = "Timeworn Loboskin Map", Tier = MapTier.Solo, MinLevel = 95, Expansion = "DT", HasDungeon = false } },
        { 39592, new TreasureMapInfo { ItemId = 39592, Name = "Timeworn Br'aaxskin Map", Tier = MapTier.Party, MinLevel = 100, Expansion = "DT", HasDungeon = true } },
        { 43557, new TreasureMapInfo { ItemId = 43557, Name = "Timeworn Br'aaxskin Map", Tier = MapTier.Party, MinLevel = 100, Expansion = "DT", HasDungeon = true } },
        { 46185, new TreasureMapInfo { ItemId = 46185, Name = "Timeworn Gargantuaskin Map", Tier = MapTier.Party, MinLevel = 100, Expansion = "DT", HasDungeon = true } },
    };

    public static IEnumerable<uint> AllMapItemIds => KnownMaps.Keys;
}
