using System;
using System.Collections.Generic;
using System.Linq;

namespace LootGoblin.Services;

internal static class LootGoblinActionTrace
{
    private const int MaxRecentActions = 32;
    private const int MaxActionTextLength = 240;
    private static readonly object SyncRoot = new();
    private static readonly Queue<string> RecentActions = new();

    public static void Record(string category, string detail)
    {
        try
        {
            var entry = $"{DateTime.Now:HH:mm:ss.fff} {Sanitize(category)} {Sanitize(detail)}";
            lock (SyncRoot)
            {
                RecentActions.Enqueue(entry);
                while (RecentActions.Count > MaxRecentActions)
                    RecentActions.Dequeue();
            }
        }
        catch
        {
            // Diagnostics must never affect automation.
        }
    }

    public static string FormatRecent(int maxActions = 12)
    {
        try
        {
            lock (SyncRoot)
            {
                if (RecentActions.Count == 0)
                    return "none";

                var skip = Math.Max(0, RecentActions.Count - Math.Max(1, maxActions));
                return string.Join(" | ", RecentActions.Skip(skip));
            }
        }
        catch
        {
            return "unavailable";
        }
    }

    private static string Sanitize(string value)
    {
        var sanitized = (value ?? string.Empty)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Trim();

        return sanitized.Length <= MaxActionTextLength
            ? sanitized
            : sanitized[..MaxActionTextLength] + "...";
    }
}
