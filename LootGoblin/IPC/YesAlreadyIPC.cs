using System;
using System.Collections.Generic;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LootGoblin.IPC;

/// <summary>
/// Manages YesAlready plugin pause/unpause via ECommons shared data.
/// Pattern from PunishXIV/YesAlready BlockListHandler:
/// YesAlready checks a shared HashSet&lt;string&gt; named "YesAlready.StopRequests".
/// If any entries exist, YesAlready is "Locked" (paused).
/// We add "LootGoblin" to pause, remove to unpause.
/// </summary>
public class YesAlreadyIPC : IDisposable
{
    private const string StopRequestsKey = "YesAlready.StopRequests";
    private const string LockName = "LootGoblin";

    private readonly Plugin _plugin;
    private readonly IPluginLog _log;
    private bool _isPaused;

    public bool IsPaused => _isPaused;

    public YesAlreadyIPC(Plugin plugin, IPluginLog log)
    {
        _plugin = plugin;
        _log = log;
    }

    /// <summary>
    /// Pause YesAlready by adding our name to the shared stop requests set.
    /// </summary>
    public void Pause()
    {
        if (_isPaused) return;

        try
        {
            var stopRequests = Plugin.PluginInterface.GetOrCreateData<HashSet<string>>(StopRequestsKey, () => []);
            stopRequests.Add(LockName);
            _isPaused = true;
            _plugin.AddDebugLog("[YesAlready] Paused (added LootGoblin to StopRequests)");
        }
        catch (Exception ex)
        {
            _log.Warning($"[YesAlready] Failed to pause: {ex.Message}");
        }
    }

    /// <summary>
    /// Unpause YesAlready by removing our name from the shared stop requests set.
    /// </summary>
    public void Unpause()
    {
        if (!_isPaused) return;

        try
        {
            var stopRequests = Plugin.PluginInterface.GetOrCreateData<HashSet<string>>(StopRequestsKey, () => []);
            stopRequests.Remove(LockName);
            _isPaused = false;
            _plugin.AddDebugLog("[YesAlready] Unpaused (removed LootGoblin from StopRequests)");
        }
        catch (Exception ex)
        {
            _log.Warning($"[YesAlready] Failed to unpause: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // Always unpause on dispose to avoid leaving YesAlready locked
        if (_isPaused)
        {
            try
            {
                var stopRequests = Plugin.PluginInterface.GetOrCreateData<HashSet<string>>(StopRequestsKey, () => []);
                stopRequests.Remove(LockName);
                _isPaused = false;
                _log.Information("[YesAlready] Unpaused on dispose");
            }
            catch (Exception ex)
            {
                _log.Warning($"[YesAlready] Failed to unpause on dispose: {ex.Message}");
            }
        }
    }
}
