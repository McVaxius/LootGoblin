using System;
using System.Collections.Generic;
using System.Linq;
using LootGoblin.Models;

namespace LootGoblin.Services;

public sealed class MapGatherParseResult
{
    public bool Success { get; init; }
    public uint ItemId { get; init; }
    public TreasureMapInfo? Map { get; init; }
    public bool RunAfterGather { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public IReadOnlyList<TreasureMapInfo> Matches { get; init; } = Array.Empty<TreasureMapInfo>();
}

public static class MapGatherRequestParser
{
    public static MapGatherParseResult ParseCommand(string args)
    {
        var text = (args ?? string.Empty).Trim();
        var runAfterGather = false;

        if (text.StartsWith("--run", StringComparison.OrdinalIgnoreCase))
        {
            runAfterGather = true;
            text = text[5..].Trim();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new MapGatherParseResult
            {
                Success = false,
                RunAfterGather = runAfterGather,
                ErrorMessage = "Map name or item ID required.",
            };
        }

        var resolved = ResolveMap(text);
        if (!resolved.Success)
            return WithRunAfter(resolved, runAfterGather);

        return new MapGatherParseResult
        {
            Success = true,
            ItemId = resolved.ItemId,
            Map = resolved.Map,
            RunAfterGather = runAfterGather,
            Matches = resolved.Matches,
        };
    }

    public static MapGatherParseResult ResolveMap(string target)
    {
        target = (target ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            return new MapGatherParseResult
            {
                ErrorMessage = "Map name or item ID required.",
            };
        }

        if (uint.TryParse(target, out var itemId))
        {
            if (!TreasureMapData.KnownMaps.TryGetValue(itemId, out var mapById))
            {
                return new MapGatherParseResult
                {
                    ItemId = itemId,
                    ErrorMessage = $"Unknown treasure map item ID {itemId}.",
                };
            }

            return ValidateGatherable(mapById);
        }

        var exactMatches = TreasureMapData.KnownMaps.Values
            .Where(map => string.Equals(map.Name, target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exactMatches.Count == 1)
            return ValidateGatherable(exactMatches[0]);

        var partialMatches = TreasureMapData.KnownMaps.Values
            .Where(map => map.Name.Contains(target, StringComparison.OrdinalIgnoreCase))
            .OrderBy(map => map.MinLevel)
            .ThenBy(map => map.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (partialMatches.Count == 0)
        {
            return new MapGatherParseResult
            {
                ErrorMessage = $"No treasure map matched '{target}'.",
            };
        }

        if (partialMatches.Count > 1)
        {
            return new MapGatherParseResult
            {
                Matches = partialMatches,
                ErrorMessage = $"Map name '{target}' is ambiguous: {string.Join(", ", partialMatches.Select(map => map.Name))}.",
            };
        }

        return ValidateGatherable(partialMatches[0]);
    }

    private static MapGatherParseResult ValidateGatherable(TreasureMapInfo map)
    {
        if (!map.IsGatherable)
        {
            return new MapGatherParseResult
            {
                ItemId = map.ItemId,
                Map = map,
                ErrorMessage = $"{map.Name} cannot be gathered.",
            };
        }

        return new MapGatherParseResult
        {
            Success = true,
            ItemId = map.ItemId,
            Map = map,
            Matches = [map],
        };
    }

    private static MapGatherParseResult WithRunAfter(MapGatherParseResult result, bool runAfterGather)
        => new()
        {
            Success = result.Success,
            ItemId = result.ItemId,
            Map = result.Map,
            RunAfterGather = runAfterGather,
            ErrorMessage = result.ErrorMessage,
            Matches = result.Matches,
        };
}
