using System;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LootGoblin.Services;

namespace LootGoblin.IPC;

public class VNavIPC : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;
    private readonly Plugin _plugin;

    public bool IsAvailable { get; private set; }
    public bool IsNavigating { get; private set; }

    public VNavIPC(Plugin plugin, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _plugin = plugin;
        _pluginInterface = pluginInterface;
        _log = log;

        CheckAvailability();
    }

    public void Dispose() { }

    public void CheckAvailability()
    {
        try
        {
            var installedPlugins = _pluginInterface.InstalledPlugins;
            IsAvailable = false;
            
            foreach (var p in installedPlugins)
            {
                if (string.Equals(p.InternalName, "vnavmesh", StringComparison.OrdinalIgnoreCase) && p.IsLoaded)
                {
                    IsAvailable = true;
                    _plugin.AddDebugLog($"vnavmesh: Available (matched '{p.InternalName}')");
                    break;
                }
            }

            if (!IsAvailable)
            {
                if (_plugin.Configuration.DebugMode)
                    _plugin.AddDebugLog("vnavmesh: Not found (looking for 'vnavmesh')");
                else
                    _plugin.AddDebugLog("vnavmesh: Not found");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Error checking vnavmesh: {ex.Message}");
            IsAvailable = false;
        }
    }

    public void FlyTo(Vector3 target)
    {
        if (!IsAvailable)
        {
            _plugin.AddDebugLog("Cannot fly: vnavmesh not available.");
            return;
        }

        var coords = CommandHelper.FormatVector(target);
        var cmd = $"/vnav flyto {coords}";
        CommandHelper.SendCommand(cmd);
        IsNavigating = true;
        _plugin.AddDebugLog($"Flying to {coords}");
    }

    public void MoveTo(Vector3 target)
    {
        if (!IsAvailable)
        {
            _plugin.AddDebugLog("Cannot move: vnavmesh not available.");
            return;
        }

        var coords = CommandHelper.FormatVector(target);
        var cmd = $"/vnav moveto {coords}";
        CommandHelper.SendCommand(cmd);
        IsNavigating = true;
        _plugin.AddDebugLog($"Moving to {coords}");
    }

    public void Stop()
    {
        if (!IsAvailable) return;

        CommandHelper.SendCommand("/vnavmesh stop");
        IsNavigating = false;
        _plugin.AddDebugLog("Navigation stopped.");
    }
}
