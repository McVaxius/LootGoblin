using System;

namespace LootGoblin.Services;

internal enum MapAllowanceHeaderKind
{
    None,
    Cooldown,
    Ready,
    Unavailable,
}

internal readonly record struct MapAllowanceHeaderState(
    MapAllowanceHeaderKind Kind,
    string PrimaryText,
    bool ShowLegend)
{
    public static MapAllowanceHeaderState None { get; } = new(MapAllowanceHeaderKind.None, string.Empty, false);
}

internal static class MapAllowanceHeaderPolicy
{
    public const string ReadyText = "[map can be gathered]";
    public const string CooldownPrefix = "Map allowance cooldown:";
    public const string UnavailablePrefix = "Map allowance:";
    public const string WaitingForTimersText = "Map allowance: waiting for /timers data";
    public const string LegendLineOne = "Seedling: toggle missing-map gathering | Blue X: unavailable through gathering";
    public const string LegendLineTwo = "Red X overlay: allowance cooldown";

    public static MapAllowanceHeaderState Evaluate(MapAllowanceStatus status, bool hasGatherJob, bool showUnavailableReason = false)
    {
        if (!status.IsAvailable)
            return showUnavailableReason
                ? new MapAllowanceHeaderState(MapAllowanceHeaderKind.Unavailable, FormatUnavailableReason(status.Error), false)
                : MapAllowanceHeaderState.None;

        if (!status.IsReady)
        {
            return new MapAllowanceHeaderState(
                MapAllowanceHeaderKind.Cooldown,
                $"{CooldownPrefix} {status.CompactText}",
                true);
        }

        if (!hasGatherJob)
            return MapAllowanceHeaderState.None;

        return new MapAllowanceHeaderState(
            MapAllowanceHeaderKind.Ready,
            ReadyText,
            false);
    }

    private static string FormatUnavailableReason(string error)
    {
        if (string.IsNullOrWhiteSpace(error) ||
            error.Contains("loading", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("not loaded", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("Retrieving information", StringComparison.OrdinalIgnoreCase))
        {
            return WaitingForTimersText;
        }

        return $"{UnavailablePrefix} {error}";
    }
}
