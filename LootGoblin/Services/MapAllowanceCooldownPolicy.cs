using System;

namespace LootGoblin.Services;

internal enum MapAllowanceCooldownDecision
{
    Ready,
    Wait,
    Stop,
    Unavailable,
}

internal static class MapAllowanceCooldownPolicy
{
    public static MapAllowanceCooldownDecision Evaluate(MapAllowanceStatus status, int maxWaitMinutes)
    {
        if (!status.IsAvailable)
            return MapAllowanceCooldownDecision.Unavailable;

        if (status.IsReady)
            return MapAllowanceCooldownDecision.Ready;

        var remaining = status.Remaining < TimeSpan.Zero ? TimeSpan.Zero : status.Remaining;
        var maxWait = TimeSpan.FromMinutes(Math.Max(0, maxWaitMinutes));
        return remaining <= maxWait
            ? MapAllowanceCooldownDecision.Wait
            : MapAllowanceCooldownDecision.Stop;
    }
}
