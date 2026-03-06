using System.Collections.Generic;

namespace LootGoblin.Models;

public enum MapTier
{
    Unknown,
    Solo,
    Party,
    Raid,
}

public enum MapCategory
{
    Outdoor,           // No portal, overworld chest + combat only
    Dungeon,           // Portal leads to a dungeon with room progression
    Roulette,          // Portal leads to dungeon with Atomos roulette (High/Low)
    GuaranteedPortal,  // Always spawns portal, enters highest-tier dungeon for expac
}

public enum ImplementationStatus
{
    NotStarted,
    WIP,
    Implemented,
}

public class TreasureMapInfo
{
    public uint ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public MapTier Tier { get; init; }
    public int MinLevel { get; init; }
    public string Expansion { get; init; } = string.Empty;
    public bool HasDungeon { get; init; }
    public MapCategory Category { get; init; } = MapCategory.Outdoor;
    public ImplementationStatus Status { get; init; } = ImplementationStatus.NotStarted;
    public uint DungeonTerritoryId { get; init; } // Territory ID of the dungeon this map leads to (0 = unknown/none)
}

public static class TreasureMapData
{
    public static readonly Dictionary<uint, TreasureMapInfo> KnownMaps = new()
    {
        // A Realm Reborn
        { 6688, new TreasureMapInfo { ItemId = 6688, Name = "Timeworn Leather Map", Tier = MapTier.Solo, MinLevel = 40, Expansion = "ARR", HasDungeon = false, Category = MapCategory.Outdoor, Status = ImplementationStatus.Implemented } },
        { 6689, new TreasureMapInfo { ItemId = 6689, Name = "Timeworn Goatskin Map", Tier = MapTier.Solo, MinLevel = 45, Expansion = "ARR", HasDungeon = false, Category = MapCategory.Outdoor, Status = ImplementationStatus.Implemented } },
        { 6690, new TreasureMapInfo { ItemId = 6690, Name = "Timeworn Toadskin Map", Tier = MapTier.Solo, MinLevel = 50, Expansion = "ARR", HasDungeon = false, Category = MapCategory.Outdoor, Status = ImplementationStatus.Implemented } },
        { 6691, new TreasureMapInfo { ItemId = 6691, Name = "Timeworn Boarskin Map", Tier = MapTier.Party, MinLevel = 50, Expansion = "ARR", HasDungeon = false, Category = MapCategory.Outdoor, Status = ImplementationStatus.Implemented } },
        { 6692, new TreasureMapInfo { ItemId = 6692, Name = "Timeworn Peisteskin Map", Tier = MapTier.Party, MinLevel = 50, Expansion = "ARR", HasDungeon = false, Category = MapCategory.Outdoor, Status = ImplementationStatus.Implemented } },

        // Heavensward
        { 12241, new TreasureMapInfo { ItemId = 12241, Name = "Timeworn Archaeoskin Map", Tier = MapTier.Solo, MinLevel = 55, Expansion = "HW", HasDungeon = false, Category = MapCategory.Outdoor, Status = ImplementationStatus.Implemented } },
        { 12242, new TreasureMapInfo { ItemId = 12242, Name = "Timeworn Wyvernskin Map", Tier = MapTier.Party, MinLevel = 60, Expansion = "HW", HasDungeon = false, Category = MapCategory.Outdoor, Status = ImplementationStatus.Implemented } },
        { 12243, new TreasureMapInfo { ItemId = 12243, Name = "Timeworn Dragonskin Map", Tier = MapTier.Party, MinLevel = 60, Expansion = "HW", HasDungeon = true, Category = MapCategory.Dungeon, Status = ImplementationStatus.NotStarted, DungeonTerritoryId = 0 } }, // Aquapolis - TBD territory ID

        // Stormblood
        { 17835, new TreasureMapInfo { ItemId = 17835, Name = "Timeworn Gaganaskin Map", Tier = MapTier.Solo, MinLevel = 65, Expansion = "SB", HasDungeon = false, Category = MapCategory.Outdoor, Status = ImplementationStatus.Implemented } },
        { 17836, new TreasureMapInfo { ItemId = 17836, Name = "Timeworn Gazelleskin Map", Tier = MapTier.Party, MinLevel = 70, Expansion = "SB", HasDungeon = true, Category = MapCategory.Roulette, Status = ImplementationStatus.Implemented, DungeonTerritoryId = 712 } }, // Lost Canals of Uznair
        { 24794, new TreasureMapInfo { ItemId = 24794, Name = "Seemingly Special Timeworn Map", Tier = MapTier.Party, MinLevel = 70, Expansion = "SB", HasDungeon = true, Category = MapCategory.GuaranteedPortal, Status = ImplementationStatus.Implemented, DungeonTerritoryId = 712 } }, // Lost Canals (guaranteed)

        // Shadowbringers
        { 26744, new TreasureMapInfo { ItemId = 26744, Name = "Timeworn Gliderskin Map", Tier = MapTier.Solo, MinLevel = 75, Expansion = "ShB", HasDungeon = false, Category = MapCategory.Outdoor, Status = ImplementationStatus.Implemented } },
        { 26745, new TreasureMapInfo { ItemId = 26745, Name = "Timeworn Zonureskin Map", Tier = MapTier.Party, MinLevel = 80, Expansion = "ShB", HasDungeon = true, Category = MapCategory.Roulette, Status = ImplementationStatus.WIP, DungeonTerritoryId = 0 } }, // Lyhe Ghiah - TBD territory ID + XYZ
        { 33328, new TreasureMapInfo { ItemId = 33328, Name = "Ostensibly Special Timeworn Map", Tier = MapTier.Party, MinLevel = 80, Expansion = "ShB", HasDungeon = true, Category = MapCategory.GuaranteedPortal, Status = ImplementationStatus.WIP, DungeonTerritoryId = 0 } }, // Guaranteed portal - TBD territory ID + XYZ

        // Endwalker
        { 36611, new TreasureMapInfo { ItemId = 36611, Name = "Timeworn Saigaskin Map", Tier = MapTier.Solo, MinLevel = 85, Expansion = "EW", HasDungeon = false, Category = MapCategory.Outdoor, Status = ImplementationStatus.Implemented } },
        { 36612, new TreasureMapInfo { ItemId = 36612, Name = "Timeworn Kumbhiraskin Map", Tier = MapTier.Party, MinLevel = 90, Expansion = "EW", HasDungeon = true, Category = MapCategory.Roulette, Status = ImplementationStatus.WIP, DungeonTerritoryId = 0 } }, // Excitatron 6000 - TBD territory ID + XYZ
        { 39593, new TreasureMapInfo { ItemId = 39593, Name = "Potentially Special Timeworn Map", Tier = MapTier.Party, MinLevel = 90, Expansion = "EW", HasDungeon = true, Category = MapCategory.GuaranteedPortal, Status = ImplementationStatus.WIP, DungeonTerritoryId = 0 } }, // Guaranteed portal - TBD territory ID + XYZ
        { 39918, new TreasureMapInfo { ItemId = 39918, Name = "Conceivably Special Timeworn Map", Tier = MapTier.Party, MinLevel = 90, Expansion = "EW", HasDungeon = true, Category = MapCategory.GuaranteedPortal, Status = ImplementationStatus.WIP, DungeonTerritoryId = 0 } }, // Guaranteed portal - TBD territory ID + XYZ

        // Dawntrail
        { 39591, new TreasureMapInfo { ItemId = 39591, Name = "Timeworn Loboskin Map", Tier = MapTier.Solo, MinLevel = 95, Expansion = "DT", HasDungeon = false, Category = MapCategory.Outdoor, Status = ImplementationStatus.Implemented } },
        { 39592, new TreasureMapInfo { ItemId = 39592, Name = "Timeworn Br'aaxskin Map", Tier = MapTier.Party, MinLevel = 100, Expansion = "DT", HasDungeon = true, Category = MapCategory.Roulette, Status = ImplementationStatus.WIP, DungeonTerritoryId = 0 } }, // TBD territory ID + XYZ
        { 43557, new TreasureMapInfo { ItemId = 43557, Name = "Timeworn Br'aaxskin Map", Tier = MapTier.Party, MinLevel = 100, Expansion = "DT", HasDungeon = true, Category = MapCategory.Roulette, Status = ImplementationStatus.WIP, DungeonTerritoryId = 0 } }, // TBD territory ID + XYZ
        { 46185, new TreasureMapInfo { ItemId = 46185, Name = "Timeworn Gargantuaskin Map", Tier = MapTier.Party, MinLevel = 100, Expansion = "DT", HasDungeon = true, Category = MapCategory.Roulette, Status = ImplementationStatus.WIP, DungeonTerritoryId = 0 } }, // TBD territory ID + XYZ
    };

    public static IEnumerable<uint> AllMapItemIds => KnownMaps.Keys;
}
