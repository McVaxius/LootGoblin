using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

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
}

public sealed class MapAllowanceService : IDisposable
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly Plugin plugin;
    private readonly IPluginLog log;
    private MapAllowanceStatus cachedStatus = new(true, TimeSpan.Zero, null, string.Empty);
    private DateTime cachedAtUtc = DateTime.MinValue;

    public MapAllowanceService(Plugin plugin, IPluginLog log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public void Dispose()
    {
    }

    public MapAllowanceStatus GetStatus(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now - cachedAtUtc < CacheTtl)
            return cachedStatus;

        cachedAtUtc = now;
        cachedStatus = ReadStatus();
        return cachedStatus;
    }

    public bool IsAllowanceReady(out string detail)
    {
        var status = GetStatus(force: true);
        if (!status.IsAvailable)
        {
            detail = $"Map allowance status unavailable: {status.Error}";
            return false;
        }

        if (status.IsReady)
        {
            detail = "ready";
            return true;
        }

        detail = $"Map allowance locked for {status.CompactText}.";
        return false;
    }

    private unsafe MapAllowanceStatus ReadStatus()
    {
        if (!Plugin.ClientState.IsLoggedIn)
            return new MapAllowanceStatus(true, TimeSpan.Zero, null, string.Empty);

        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
            return new MapAllowanceStatus(false, TimeSpan.Zero, null, "loading");

        try
        {
            var uiState = UIState.Instance();
            if (uiState == null)
                return new MapAllowanceStatus(false, TimeSpan.Zero, null, "UIState unavailable");

            uiState->RequestResetTimestamps();

            var timestamp = uiState->NextMapAllowanceTimestamp;
            if (timestamp == 0)
                return new MapAllowanceStatus(true, TimeSpan.Zero, null, string.Empty);

            var next = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            var remaining = next - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return new MapAllowanceStatus(true, TimeSpan.Zero, next, string.Empty);

            return new MapAllowanceStatus(false, remaining, next, string.Empty);
        }
        catch (Exception ex)
        {
            log.Debug($"[MapAllowance] Failed to read map allowance timestamp: {ex.Message}");
            return new MapAllowanceStatus(false, TimeSpan.Zero, null, ex.Message);
        }
    }
}
