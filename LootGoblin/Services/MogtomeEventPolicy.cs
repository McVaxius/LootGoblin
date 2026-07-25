using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LootGoblin.Services;

internal sealed record MogtomeEvent(string Name, DateTimeOffset Begin, DateTimeOffset End);

internal static class MogtomeEventPolicy
{
    private const string EventNameFragment = "Moogle Treasure Trove";

    public static bool TryParseFeed(string json, out IReadOnlyList<MogtomeEvent> events)
    {
        events = Array.Empty<MogtomeEvent>();
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var rows = JsonSerializer.Deserialize<List<EventRow>>(json);
            if (rows == null)
                return false;

            var parsed = new List<MogtomeEvent>(rows.Count);
            foreach (var row in rows)
            {
                if (row == null ||
                    string.IsNullOrWhiteSpace(row.Name) ||
                    !TryParseUtc(row.Begin, out var begin) ||
                    !TryParseUtc(row.End, out var end) ||
                    end <= begin)
                {
                    return false;
                }

                parsed.Add(new MogtomeEvent(row.Name.Trim(), begin, end));
            }

            events = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsActive(IEnumerable<MogtomeEvent> events, DateTimeOffset nowUtc)
    {
        foreach (var activeEvent in events)
        {
            if (activeEvent.Name.Contains(EventNameFragment, StringComparison.OrdinalIgnoreCase) &&
                activeEvent.Begin <= nowUtc &&
                nowUtc < activeEvent.End)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseUtc(string? value, out DateTimeOffset timestamp)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);

    private sealed class EventRow
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("begin")]
        public string? Begin { get; init; }

        [JsonPropertyName("end")]
        public string? End { get; init; }
    }
}
