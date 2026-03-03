using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LootGoblin.IPC;

public class VNavIPC : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;
    private readonly Plugin _plugin;

    public bool IsAvailable { get; private set; }

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
                if (p.InternalName == "vnavmesh" && p.IsLoaded)
                {
                    IsAvailable = true;
                    break;
                }
            }

            _plugin.AddDebugLog($"vnavmesh: {(IsAvailable ? "Available" : "Not found")}");
        }
        catch (Exception ex)
        {
            _log.Error($"Error checking vnavmesh: {ex.Message}");
            IsAvailable = false;
        }
    }
}
