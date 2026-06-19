using System;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using LootGoblin.Models;

namespace LootGoblin.Services;

public sealed class LootGoblinMapGatherIpcService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Plugin plugin;
    private readonly IPluginLog log;
    private readonly ICallGateProvider<bool> isReadyProvider;
    private readonly ICallGateProvider<string> getGatherableMapsProvider;
    private readonly ICallGateProvider<string, string> startMapGatherProvider;
    private readonly ICallGateProvider<string, string> getMapGatherStatusProvider;
    private readonly ICallGateProvider<string, string> cancelMapGatherProvider;

    public LootGoblinMapGatherIpcService(Plugin plugin, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.plugin = plugin;
        this.log = log;

        isReadyProvider = pluginInterface.GetIpcProvider<bool>("LootGoblin.IsReady");
        getGatherableMapsProvider = pluginInterface.GetIpcProvider<string>("LootGoblin.GetGatherableMapsJson");
        startMapGatherProvider = pluginInterface.GetIpcProvider<string, string>("LootGoblin.StartMapGatherJson");
        getMapGatherStatusProvider = pluginInterface.GetIpcProvider<string, string>("LootGoblin.GetMapGatherStatusJson");
        cancelMapGatherProvider = pluginInterface.GetIpcProvider<string, string>("LootGoblin.CancelMapGatherJson");

        isReadyProvider.RegisterFunc(IsReady);
        getGatherableMapsProvider.RegisterFunc(GetGatherableMapsJson);
        startMapGatherProvider.RegisterFunc(StartMapGatherJson);
        getMapGatherStatusProvider.RegisterFunc(GetMapGatherStatusJson);
        cancelMapGatherProvider.RegisterFunc(CancelMapGatherJson);
    }

    public void Dispose()
    {
        isReadyProvider.UnregisterFunc();
        getGatherableMapsProvider.UnregisterFunc();
        startMapGatherProvider.UnregisterFunc();
        getMapGatherStatusProvider.UnregisterFunc();
        cancelMapGatherProvider.UnregisterFunc();
    }

    private bool IsReady()
        => plugin.StateManager.CanAcceptMapGatherRequest;

    private static string GetGatherableMapsJson()
        => JsonSerializer.Serialize(MapGatherCatalog.GetGatherableMaps(), JsonOptions);

    private string StartMapGatherJson(string requestJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<MapGatherStartRequest>(requestJson, JsonOptions) ?? new MapGatherStartRequest();
            if (string.IsNullOrWhiteSpace(request.RequestId))
                request.RequestId = Guid.NewGuid().ToString("N");

            var response = plugin.StateManager.StartMapGatherRequest(request);
            return JsonSerializer.Serialize(response, JsonOptions);
        }
        catch (Exception ex)
        {
            log.Warning($"[MapGatherIPC] StartMapGatherJson failed: {ex.Message}");
            var response = MapGatherStatusResponse.Rejected(string.Empty, 0, string.Empty, false, $"Invalid request JSON: {ex.Message}");
            return JsonSerializer.Serialize(response, JsonOptions);
        }
    }

    private string GetMapGatherStatusJson(string requestId)
    {
        try
        {
            var response = plugin.StateManager.GetMapGatherRequestStatus(requestId);
            return JsonSerializer.Serialize(response, JsonOptions);
        }
        catch (Exception ex)
        {
            log.Warning($"[MapGatherIPC] GetMapGatherStatusJson failed: {ex.Message}");
            var response = MapGatherStatusResponse.Rejected(requestId, 0, string.Empty, false, $"Status failed: {ex.Message}");
            return JsonSerializer.Serialize(response, JsonOptions);
        }
    }

    private string CancelMapGatherJson(string requestId)
    {
        try
        {
            var response = plugin.StateManager.CancelMapGatherRequest(requestId);
            return JsonSerializer.Serialize(response, JsonOptions);
        }
        catch (Exception ex)
        {
            log.Warning($"[MapGatherIPC] CancelMapGatherJson failed: {ex.Message}");
            var response = MapGatherStatusResponse.Rejected(requestId, 0, string.Empty, false, $"Cancel failed: {ex.Message}");
            return JsonSerializer.Serialize(response, JsonOptions);
        }
    }
}
