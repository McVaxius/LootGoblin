using System;
using System.Collections.Generic;
using System.Linq;

namespace LootGoblin.Models;

public sealed class MapGatherableMapDto
{
    public uint ItemId { get; set; }
    public string MapName { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Expansion { get; set; } = string.Empty;
    public int MinLevel { get; set; }
    public bool HasDungeon { get; set; }
    public bool IsGatherable { get; set; }
    public bool SoloOutdoorSafe { get; set; }
}

public sealed class MapGatherStartRequest
{
    public string RequestId { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string MapName { get; set; } = string.Empty;
    public bool RunAfterGather { get; set; }
}

public sealed class MapGatherStatusResponse
{
    public string RequestId { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public string MapName { get; set; } = string.Empty;
    public bool RunAfterGather { get; set; }
    public bool Accepted { get; set; }
    public bool Terminal { get; set; }
    public bool Success { get; set; }
    public string State { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool HasDungeon { get; set; }
    public bool IsGatherable { get; set; }
    public bool SoloOutdoorSafe { get; set; }

    public static MapGatherStatusResponse Rejected(string requestId, uint itemId, string mapName, bool runAfterGather, string message)
        => new()
        {
            RequestId = requestId,
            ItemId = itemId,
            MapName = mapName,
            RunAfterGather = runAfterGather,
            Accepted = false,
            Terminal = true,
            Success = false,
            State = MapGatherRequestStates.Rejected,
            Message = message,
        };
}

public static class MapGatherRequestStates
{
    public const string Rejected = "Rejected";
    public const string Accepted = "Accepted";
    public const string AlreadyPresent = "AlreadyPresent";
    public const string Gathering = "Gathering";
    public const string RunningMap = "RunningMap";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Unknown = "Unknown";
}

public static class MapGatherCatalog
{
    public static IReadOnlyList<MapGatherableMapDto> GetGatherableMaps()
        => TreasureMapData.KnownMaps.Values
            .Where(map => map.IsGatherable)
            .OrderBy(map => map.MinLevel)
            .ThenBy(map => map.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToList();

    public static MapGatherableMapDto ToDto(TreasureMapInfo map)
        => new()
        {
            ItemId = map.ItemId,
            MapName = map.Name,
            Tier = map.Tier.ToString(),
            Category = map.Category.ToString(),
            Expansion = map.Expansion,
            MinLevel = map.MinLevel,
            HasDungeon = map.HasDungeon,
            IsGatherable = map.IsGatherable,
            SoloOutdoorSafe = IsSoloOutdoorSafe(map),
        };

    public static MapGatherStatusResponse ApplyMapMetadata(MapGatherStatusResponse response, TreasureMapInfo? map)
    {
        if (map == null)
            return response;

        response.ItemId = map.ItemId;
        response.MapName = map.Name;
        response.Tier = map.Tier.ToString();
        response.Category = map.Category.ToString();
        response.HasDungeon = map.HasDungeon;
        response.IsGatherable = map.IsGatherable;
        response.SoloOutdoorSafe = IsSoloOutdoorSafe(map);
        return response;
    }

    public static bool IsSoloOutdoorSafe(TreasureMapInfo map)
        => map.Tier == MapTier.Solo &&
           map.Category == MapCategory.Outdoor &&
           !map.HasDungeon;
}
