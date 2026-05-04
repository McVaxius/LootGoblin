using System;
using Dalamud.Plugin.Services;
using LootGoblin.Models;

namespace LootGoblin.Services;

public sealed class MapFlagService : IDisposable
{
    private readonly Plugin plugin;
    private readonly IPluginLog log;
    private readonly MapFlagReader flagReader;

    public MapFlagService(Plugin plugin, IPluginLog log)
    {
        this.plugin = plugin;
        this.log = log;
        flagReader = new MapFlagReader(plugin, log);
        CheckAvailability(logStatus: false);
    }

    public bool IsAvailable { get; private set; }

    public void Dispose()
    {
        flagReader.Dispose();
    }

    public MapLocation? TryGetMapLocation()
    {
        var location = TryReadFlag();
        if (location != null)
            plugin.AddDebugLog($"Map location from local flag: {location.ZoneName} ({location.X:F1}, {location.Z:F1})");
        else
            plugin.AddDebugLog("No local map flag found yet.");

        return location;
    }

    public MapLocation? TryReadFlag()
    {
        return flagReader.TryReadFlag();
    }

    public void CheckAvailability(bool logStatus = true)
    {
        try
        {
            IsAvailable = true;
            if (logStatus)
                plugin.AddDebugLog("Map flag reader: Available (local AgentMap reader)");
        }
        catch (Exception ex)
        {
            log.Error($"Error checking local map flag reader: {ex.Message}");
            IsAvailable = false;
        }
    }
}
