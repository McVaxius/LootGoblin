using System;
using System.Linq;
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
            var installedPlugins = _pluginInterface.InstalledPlugins.ToList();
            _plugin.AddDebugLog($"[VNavIPC] Checking availability - found {installedPlugins.Count} total plugins");
            IsAvailable = false;
            
            foreach (var p in installedPlugins)
            {
                _plugin.AddDebugLog($"[VNavIPC] Plugin: '{p.InternalName}' (Loaded: {p.IsLoaded}, Version: {p.Version})");
                
                if (string.Equals(p.InternalName, "vnavmesh", StringComparison.OrdinalIgnoreCase))
                {
                    _plugin.AddDebugLog($"[VNavIPC] Found vnavmesh plugin - IsLoaded: {p.IsLoaded}");
                    
                    if (p.IsLoaded)
                    {
                        IsAvailable = true;
                        _plugin.AddDebugLog($"[VNavIPC] vnavmesh: Available (matched '{p.InternalName}')");
                        break;
                    }
                    else
                    {
                        _plugin.AddDebugLog($"[VNavIPC] vnavmesh found but not loaded yet");
                    }
                }
            }

            if (!IsAvailable)
            {
                _plugin.AddDebugLog($"[VNavIPC] vnavmesh: Not available after checking {installedPlugins.Count} plugins");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Error checking vnavmesh: {ex.Message}");
            _plugin.AddDebugLog($"[VNavIPC] Exception during availability check: {ex.Message}");
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
