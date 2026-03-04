using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LootGoblin.Models;
using LootGoblin.Services;

namespace LootGoblin.IPC;

public class GlobeTrotterIPC : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;
    private readonly Plugin _plugin;

    public bool IsAvailable { get; private set; }

    private MapFlagReader? _flagReader;

    public GlobeTrotterIPC(Plugin plugin, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _plugin = plugin;
        _pluginInterface = pluginInterface;
        _log = log;

        _flagReader = new MapFlagReader(plugin, log);  // DataManager accessed via Plugin.DataManager static
        CheckAvailability();
    }

    public void Dispose() { }

    /// <summary>
    /// Try to get the current map flag location.
    /// Primary source: AgentMap flag marker (set automatically when map is deciphered).
    /// GlobeTrotter IPC proper: future hookup once their API is confirmed.
    /// </summary>
    public MapLocation? TryGetMapLocation()
    {
        if (_flagReader == null) return null;
        var location = _flagReader.TryReadFlag();
        if (location != null)
            _plugin.AddDebugLog($"Map location from flag: {location.ZoneName} ({location.X:F1}, {location.Z:F1})");
        else
            _plugin.AddDebugLog("No map flag found yet - has the map been deciphered?");
        return location;
    }

    public void CheckAvailability()
    {
        try
        {
            var installedPlugins = _pluginInterface.InstalledPlugins;
            IsAvailable = false;
            
            foreach (var p in installedPlugins)
            {
                if (string.Equals(p.InternalName, "GlobeTrotter", StringComparison.OrdinalIgnoreCase) && p.IsLoaded)
                {
                    IsAvailable = true;
                    _plugin.AddDebugLog($"GlobeTrotter: Available (matched '{p.InternalName}')");
                    break;
                }
            }

            if (!IsAvailable)
            {
                if (_plugin.Configuration.DebugMode)
                    _plugin.AddDebugLog("GlobeTrotter: Not found (looking for 'GlobeTrotter')");
                else
                    _plugin.AddDebugLog("GlobeTrotter: Not found");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Error checking GlobeTrotter: {ex.Message}");
            IsAvailable = false;
        }
    }
}
