using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LootGoblin.Services;

namespace LootGoblin.IPC;

public class FrenRiderIPC : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;
    private readonly Plugin _plugin;

    public bool IsAvailable { get; private set; }
    public bool IsFollowing { get; private set; }

    public FrenRiderIPC(Plugin plugin, IDalamudPluginInterface pluginInterface, IPluginLog log)
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
                if (string.Equals(p.InternalName, "FrenRider", StringComparison.OrdinalIgnoreCase) && p.IsLoaded)
                {
                    IsAvailable = true;
                    _plugin.AddDebugLog($"FrenRider: Available (matched '{p.InternalName}')");
                    break;
                }
            }

            if (!IsAvailable)
            {
                if (_plugin.Configuration.DebugMode)
                    _plugin.AddDebugLog("FrenRider: Not found (looking for 'FrenRider')");
                else
                    _plugin.AddDebugLog("FrenRider: Not found");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Error checking FrenRider: {ex.Message}");
            IsAvailable = false;
        }
    }

    public void StopFollowing()
    {
        if (!IsAvailable)
        {
            _plugin.AddDebugLog("Cannot stop FrenRider: FrenRider not available.");
            return;
        }

        // Send FrenRider stop command
        CommandHelper.SendCommand("/frenrider stop");
        _plugin.AddDebugLog("Sent FrenRider stop command.");
    }

    public void PauseFollowing()
    {
        if (!IsAvailable)
        {
            _plugin.AddDebugLog("Cannot pause FrenRider: FrenRider not available.");
            return;
        }

        CommandHelper.SendCommand("/frenrider pause");
        _plugin.AddDebugLog("Sent FrenRider pause command.");
    }

    public void ResumeFollowing()
    {
        if (!IsAvailable)
        {
            _plugin.AddDebugLog("Cannot resume FrenRider: FrenRider not available.");
            return;
        }

        CommandHelper.SendCommand("/frenrider resume");
        _plugin.AddDebugLog("Sent FrenRider resume command.");
    }
}
