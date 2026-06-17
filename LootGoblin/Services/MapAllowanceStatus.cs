using System;

namespace LootGoblin.Services;

public readonly record struct MapAllowanceStatus(bool IsReady, TimeSpan Remaining, DateTimeOffset? NextAllowanceAtUtc, string Error)
{
    public bool IsAvailable => string.IsNullOrWhiteSpace(Error);

    public string CompactText
    {
        get
        {
            if (!IsAvailable)
                return "unknown";

            if (IsReady)
                return "ready";

            var remaining = Remaining < TimeSpan.Zero ? TimeSpan.Zero : Remaining;
            var hours = (int)Math.Floor(remaining.TotalHours);
            var minutes = Math.Max(0, remaining.Minutes);
            return hours > 0 ? $"{hours}h {minutes:D2}m" : $"{minutes}m";
        }
    }

    public MapAllowanceStatus WithLiveRemaining(DateTimeOffset now)
    {
        if (!IsAvailable || IsReady || NextAllowanceAtUtc is not { } nextAllowanceAtUtc)
            return this;

        var remaining = nextAllowanceAtUtc - now;
        if (remaining <= TimeSpan.Zero)
            return this with { IsReady = true, Remaining = TimeSpan.Zero };

        return this with { Remaining = remaining };
    }
}
