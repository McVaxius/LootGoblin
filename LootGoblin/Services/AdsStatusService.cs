using System;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LootGoblin.Services;

public sealed class AdsStatusService : IDisposable
{
    private readonly Plugin _plugin;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    public AdsStatusSnapshot Current { get; private set; } = AdsStatusSnapshot.Empty;

    public AdsStatusService(Plugin plugin, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _plugin = plugin;
        _pluginInterface = pluginInterface;
        _log = log;
    }

    public void Dispose()
    {
    }

    public void Reset()
    {
        Current = AdsStatusSnapshot.Empty;
        _lastRefreshUtc = DateTime.MinValue;
    }

    public AdsStatusSnapshot Refresh(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastRefreshUtc).TotalSeconds < 1.0)
            return Current;

        _lastRefreshUtc = now;
        if (!_plugin.IsAdsAvailable)
        {
            Current = AdsStatusSnapshot.Empty;
            return Current;
        }

        try
        {
            var subscriber = _pluginInterface.GetIpcSubscriber<string>("ADS.GetStatusJson");
            var json = subscriber.InvokeFunc();
            if (string.IsNullOrWhiteSpace(json))
            {
                Current = new AdsStatusSnapshot
                {
                    IsAvailable = true,
                    StatusReadable = false,
                    CapturedAtUtc = now,
                };
                return Current;
            }

            Current = AdsStatusSnapshot.Parse(json, now);
            return Current;
        }
        catch (Exception ex)
        {
            _log.Debug($"[ADS] Failed to read ADS status JSON: {ex.Message}");
            Current = new AdsStatusSnapshot
            {
                IsAvailable = true,
                StatusReadable = false,
                CapturedAtUtc = now,
            };
            return Current;
        }
    }

    public bool StartRepair(string mode)
    {
        if (!_plugin.IsAdsAvailable)
            return false;

        try
        {
            var subscriber = _pluginInterface.GetIpcSubscriber<string, bool>("ADS.StartRepair");
            return subscriber.InvokeFunc(mode);
        }
        catch (Exception ex)
        {
            _log.Debug($"[ADS] Failed to start ADS repair via IPC: {ex.Message}");
            return false;
        }
    }

    public bool StartDutyInsideWithoutExit(out uint territoryId, out uint contentId)
    {
        territoryId = contentId = 0;
        if (!_plugin.IsAdsAvailable)
            return false;

        try
        {
            var capabilities = _pluginInterface.GetIpcSubscriber<string>("ADS.GetCapabilitiesJson").InvokeFunc();
            using var capabilityDocument = JsonDocument.Parse(capabilities);
            if (!capabilityDocument.RootElement.TryGetProperty("dutyCompletionSweepWithoutExit", out var capability)
                || capability.ValueKind != JsonValueKind.Number || !capability.TryGetInt32(out var version) || version < 1)
                return false;

            var response = _pluginInterface.GetIpcSubscriber<string, string, string>("ADS.Invoke")
                .InvokeFunc("duty.start-inside", "{\"sweepWithoutExit\":true}");
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("sweepWithoutExit", out var choice) || choice.ValueKind != JsonValueKind.True
                || !data.TryGetProperty("territoryTypeId", out var territory) || !territory.TryGetUInt32(out territoryId)
                || !data.TryGetProperty("contentFinderConditionId", out var content) || !content.TryGetUInt32(out contentId))
                return false;

            return territoryId != 0 && contentId != 0;
        }
        catch (Exception ex)
        {
            _log.Debug($"[ADS] Failed to select duty-completion sweep without exit via IPC: {ex.Message}");
            territoryId = contentId = 0;
            return false;
        }
    }
}
