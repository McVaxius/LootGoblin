using System;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using LootGoblin.Models;

namespace LootGoblin.Services;

public sealed class LootGoblinMapRunIpcService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly StateManager stateManager;
    private readonly ICallGateProvider<string> supportedMapsProvider;
    private readonly ICallGateProvider<string, string> startProvider;
    private readonly ICallGateProvider<string, string> statusProvider;
    private readonly ICallGateProvider<string, string> cancelProvider;

    public LootGoblinMapRunIpcService(StateManager stateManager, IDalamudPluginInterface pluginInterface)
    {
        this.stateManager = stateManager;
        supportedMapsProvider = pluginInterface.GetIpcProvider<string>("LootGoblin.GetSupportedMapsJson");
        startProvider = pluginInterface.GetIpcProvider<string, string>("LootGoblin.StartMapRunJson");
        statusProvider = pluginInterface.GetIpcProvider<string, string>("LootGoblin.GetMapRunStatusJson");
        cancelProvider = pluginInterface.GetIpcProvider<string, string>("LootGoblin.CancelMapRunJson");
        supportedMapsProvider.RegisterFunc(GetSupportedMapsJson);
        startProvider.RegisterFunc(StartMapRunJson);
        statusProvider.RegisterFunc(id => Serialize(stateManager.MapRunRequests.GetStatus(id)));
        cancelProvider.RegisterFunc(id => Serialize(stateManager.MapRunRequests.RequestCancel(id)));
    }

    private static string GetSupportedMapsJson()
        => JsonSerializer.Serialize(TreasureMapData.KnownMaps.Values
            .Where(map => map.Status == ImplementationStatus.Implemented)
            .OrderBy(map => map.MinLevel)
            .ThenBy(map => map.Name, StringComparer.OrdinalIgnoreCase)
            .Select(MapGatherCatalog.ToDto), JsonOptions);

    private string StartMapRunJson(string requestJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<MapRunStartRequest>(requestJson, JsonOptions);
            return Serialize(stateManager.MapRunRequests.Submit(request!));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return Serialize(MapRunStatusResponse.Rejected(new MapRunStartRequest(), "Invalid map-run request JSON."));
        }
    }

    private static string Serialize(MapRunStatusResponse response)
        => JsonSerializer.Serialize(response, JsonOptions);

    public void Dispose()
    {
        supportedMapsProvider.UnregisterFunc();
        startProvider.UnregisterFunc();
        statusProvider.UnregisterFunc();
        cancelProvider.UnregisterFunc();
    }
}
