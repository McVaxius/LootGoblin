using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LootGoblin.Services;

internal enum MapAllowanceParseSource
{
    None,
    AtkValues,
    FixedAtkValues,
    NodeTexts,
}

internal static class MapAllowanceContentsInfoParser
{
    private const string MapAllowanceLabel = "Next Map Allowance";
    private const string AvailableNowText = "Available Now";
    private static readonly Regex RemainingPattern = new(@"(?<hours>\d+):(?<minutes>\d{2})\s+Remaining", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(IReadOnlyList<object?> values, DateTimeOffset now, out MapAllowanceStatus status)
        => TryParse(values, now, out status, out _);

    public static bool TryParse(IReadOnlyList<object?> values, DateTimeOffset now, out MapAllowanceStatus status, out MapAllowanceParseSource source)
    {
        status = new MapAllowanceStatus(false, TimeSpan.Zero, null, "map allowance timer not loaded");
        source = MapAllowanceParseSource.None;

        var foundLabel = false;
        foreach (var labelIndex in FindLabelIndexes(values))
        {
            foundLabel = true;
            if (TryReadNextString(values, labelIndex + 1, out var detail) &&
                TryParseDetail(detail, now, out status))
            {
                source = MapAllowanceParseSource.AtkValues;
                return true;
            }

            if (string.IsNullOrWhiteSpace(status.Error))
                status = new MapAllowanceStatus(false, TimeSpan.Zero, null, "map allowance detail not loaded");
        }

        if (TryReadFixedIndexDetail(values, out var fixedDetail))
        {
            if (TryParseDetail(fixedDetail, now, out status))
            {
                source = MapAllowanceParseSource.FixedAtkValues;
                return true;
            }
        }

        if (foundLabel && string.Equals(status.Error, "map allowance timer not loaded", StringComparison.Ordinal))
            status = new MapAllowanceStatus(false, TimeSpan.Zero, null, "map allowance detail not loaded");

        return false;
    }

    public static bool TryParseVisibleTextNodes(IReadOnlyList<string> visibleTexts, DateTimeOffset now, out MapAllowanceStatus status)
        => TryParseVisibleTextNodes(visibleTexts, now, out status, out _);

    public static bool TryParseVisibleTextNodes(IReadOnlyList<string> visibleTexts, DateTimeOffset now, out MapAllowanceStatus status, out MapAllowanceParseSource source)
    {
        status = new MapAllowanceStatus(false, TimeSpan.Zero, null, "map allowance timer not loaded");
        source = MapAllowanceParseSource.None;

        for (var i = 0; i < visibleTexts.Count; i++)
        {
            if (!IsMapAllowanceLabel(visibleTexts[i]))
                continue;

            if (!TryReadNextText(visibleTexts, i + 1, out var detail))
            {
                status = new MapAllowanceStatus(false, TimeSpan.Zero, null, "map allowance detail not loaded");
                return false;
            }

            if (TryParseDetail(detail, now, out status))
            {
                source = MapAllowanceParseSource.NodeTexts;
                return true;
            }

            return false;
        }

        return false;
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (char.IsControl(c) ||
                category is UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate)
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    private static bool TryParseDetail(string rawDetail, DateTimeOffset now, out MapAllowanceStatus status)
    {
        var detail = Normalize(rawDetail);
        if (string.IsNullOrWhiteSpace(detail))
        {
            status = new MapAllowanceStatus(false, TimeSpan.Zero, null, "map allowance detail not loaded");
            return false;
        }

        if (IsLoadingOrUnavailable(detail))
        {
            status = new MapAllowanceStatus(false, TimeSpan.Zero, null, detail);
            return false;
        }

        if (detail.Contains(AvailableNowText, StringComparison.OrdinalIgnoreCase))
        {
            status = new MapAllowanceStatus(true, TimeSpan.Zero, null, string.Empty);
            return true;
        }

        var match = RemainingPattern.Match(detail);
        if (!match.Success ||
            !int.TryParse(match.Groups["hours"].Value, out var hours) ||
            !int.TryParse(match.Groups["minutes"].Value, out var minutes) ||
            minutes > 59)
        {
            status = new MapAllowanceStatus(false, TimeSpan.Zero, null, "map allowance detail not recognized");
            return false;
        }

        var remaining = TimeSpan.FromHours(hours).Add(TimeSpan.FromMinutes(minutes));
        status = new MapAllowanceStatus(false, remaining, now.Add(remaining), string.Empty);
        return true;
    }

    private static IEnumerable<int> FindLabelIndexes(IReadOnlyList<object?> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is string text && IsMapAllowanceLabel(text))
                yield return i;
        }
    }

    private static bool TryReadNextString(IReadOnlyList<object?> values, int startIndex, out string detail)
    {
        for (var i = startIndex; i < values.Count; i++)
        {
            if (values[i] is not string text || string.IsNullOrWhiteSpace(text))
                continue;

            detail = text.Trim();
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static bool TryReadNextText(IReadOnlyList<string> values, int startIndex, out string detail)
    {
        for (var i = startIndex; i < values.Count; i++)
        {
            var normalized = Normalize(values[i]);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            detail = normalized;
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static bool TryReadFixedIndexDetail(IReadOnlyList<object?> values, out string detail)
    {
        detail = string.Empty;
        const int labelIndex = 12;
        const int detailIndex = 14;

        if (values.Count <= detailIndex ||
            values[labelIndex] is not string label ||
            !IsMapAllowanceLabel(label) ||
            values[detailIndex] is not string detailValue)
        {
            return false;
        }

        detail = detailValue;
        return true;
    }

    private static bool IsMapAllowanceLabel(string text)
        => Normalize(text).Contains(MapAllowanceLabel, StringComparison.OrdinalIgnoreCase);

    private static bool IsLoadingOrUnavailable(string detail)
        => detail.Contains("Retrieving information", StringComparison.OrdinalIgnoreCase) ||
           detail.Contains("loading", StringComparison.OrdinalIgnoreCase) ||
           detail.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
           detail.Contains("not available", StringComparison.OrdinalIgnoreCase) ||
           (detail.Contains("duty", StringComparison.OrdinalIgnoreCase) &&
            (detail.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
             detail.Contains("unable", StringComparison.OrdinalIgnoreCase)));
}
