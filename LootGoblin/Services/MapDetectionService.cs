using System;
using Dalamud.Plugin.Services;
using LootGoblin.Models;

namespace LootGoblin.Services;

public class MapDetectionService : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IGameGui _gameGui;
    private readonly Plugin _plugin;

    public MapLocation? CurrentLocation { get; private set; }
    public bool IsMapOpen { get; private set; }

    public MapDetectionService(Plugin plugin, IGameGui gameGui, IPluginLog log)
    {
        _plugin = plugin;
        _gameGui = gameGui;
        _log = log;
    }

    public void Dispose() { }

    public void CheckForOpenMap()
    {
        try
        {
            var addonPtr = _gameGui.GetAddonByName("AreaMap");
            var wasOpen = IsMapOpen;
            IsMapOpen = addonPtr != nint.Zero;

            if (IsMapOpen && !wasOpen)
            {
                _plugin.AddDebugLog("Map addon detected as open.");
            }
            else if (!IsMapOpen && wasOpen)
            {
                _plugin.AddDebugLog("Map addon closed.");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Error checking map state: {ex.Message}");
        }
    }

    public void SetLocation(MapLocation location)
    {
        CurrentLocation = location;
        _plugin.AddDebugLog($"Location set: {location.ZoneName} ({location.X:F1}, {location.Y:F1}, {location.Z:F1})");
    }

    public void ClearLocation()
    {
        CurrentLocation = null;
        IsMapOpen = false;
    }
}
