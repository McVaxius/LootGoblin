using System;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LootGoblin.Services;

public sealed class AdsStatusSnapshot
{
    public static AdsStatusSnapshot Empty { get; } = new();

    public bool IsAvailable { get; init; }
    public bool StatusReadable { get; init; }
    public string OwnershipMode { get; init; } = string.Empty;
    public string ExecutionPhase { get; init; } = string.Empty;
    public string ExecutionStatus { get; init; } = string.Empty;
    public bool InDuty { get; init; }
    public bool SupportedDuty { get; init; }
    public DateTime CapturedAtUtc { get; init; }

    public bool IsOwned
        => OwnershipMode is "OwnedStartOutside" or "OwnedStartInside" or "OwnedResumeInside" or "Leaving";
}

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

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Current = new AdsStatusSnapshot
            {
                IsAvailable = true,
                StatusReadable = true,
                OwnershipMode = GetString(root, "ownershipMode"),
                ExecutionPhase = GetString(root, "executionPhase"),
                ExecutionStatus = GetString(root, "executionStatus"),
                InDuty = GetBool(root, "inDuty"),
                SupportedDuty = GetBool(root, "supportedDuty"),
                CapturedAtUtc = now,
            };
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

    private static string GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBool(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            && property.GetBoolean();
}
