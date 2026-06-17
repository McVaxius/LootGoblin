using System;

namespace LootGoblin.Services;

internal sealed class MapAllowanceVerificationCache
{
    public static MapAllowanceStatus UnverifiedStatus { get; } = new(false, TimeSpan.Zero, null, "map allowance timer not loaded");

    private ulong verifiedContentId;
    private MapAllowanceStatus verifiedStatus = UnverifiedStatus;
    private bool hasVerifiedStatus;

    public bool TryGet(ulong contentId, DateTimeOffset now, out MapAllowanceStatus status)
    {
        if (contentId == 0)
        {
            Clear();
            status = UnverifiedStatus;
            return false;
        }

        if (!hasVerifiedStatus)
        {
            status = UnverifiedStatus;
            return false;
        }

        if (verifiedContentId != contentId)
        {
            Clear();
            status = UnverifiedStatus;
            return false;
        }

        verifiedStatus = verifiedStatus.WithLiveRemaining(now);
        status = verifiedStatus;
        return true;
    }

    public MapAllowanceStatus Store(ulong contentId, MapAllowanceStatus status, DateTimeOffset now)
    {
        if (contentId == 0)
        {
            Clear();
            return UnverifiedStatus;
        }

        if (!status.IsAvailable)
            return UnverifiedStatus;

        verifiedContentId = contentId;
        verifiedStatus = status.WithLiveRemaining(now);
        hasVerifiedStatus = true;
        return verifiedStatus;
    }

    public MapAllowanceStatus MarkConsumed(ulong contentId, DateTimeOffset now)
    {
        if (contentId == 0)
        {
            Clear();
            return UnverifiedStatus;
        }

        var remaining = TimeSpan.FromHours(18);
        return Store(contentId, new MapAllowanceStatus(false, remaining, now.Add(remaining), string.Empty), now);
    }

    public void Clear()
    {
        verifiedContentId = 0;
        verifiedStatus = UnverifiedStatus;
        hasVerifiedStatus = false;
    }
}
