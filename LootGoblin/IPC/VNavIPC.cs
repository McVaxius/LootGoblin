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
    private bool isRunningIpcFailureLogged;
    private bool isPathRunningIpcFailureLogged;
    private bool isPathfindInProgressIpcFailureLogged;

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

    public bool? TryIsRunning()
    {
        if (!IsAvailable)
            return null;

        Exception? lastException = null;
        foreach (var ipcName in new[] { "NavmeshManager.IsRunning", "vnavmesh.Path.IsRunning" })
        {
            try
            {
                return _pluginInterface.GetIpcSubscriber<bool>(ipcName).InvokeFunc();
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        if (!isRunningIpcFailureLogged)
        {
            isRunningIpcFailureLogged = true;
            var detail = lastException == null
                ? "unknown error"
                : $"{lastException.GetType().Name}: {lastException.Message}";
            _plugin.AddDebugLog($"[VNavIPC] Could not read vnavmesh running state via IPC: {detail}");
            _log.Debug($"[VNavIPC] Could not read vnavmesh running state via IPC: {detail}");
        }

        return null;
    }

    public bool? TryIsPathRunning()
    {
        if (!IsAvailable)
            return null;

        try
        {
            return _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc();
        }
        catch (Exception ex)
        {
            LogIpcReadFailureOnce(
                ref isPathRunningIpcFailureLogged,
                "path running",
                ex);
            return null;
        }
    }

    public bool? TryIsPathfindInProgress()
    {
        if (!IsAvailable)
            return null;

        Exception? lastException = null;
        var anySucceeded = false;
        var inProgress = false;
        foreach (var ipcName in new[] { "vnavmesh.Nav.PathfindInProgress", "vnavmesh.SimpleMove.PathfindInProgress" })
        {
            try
            {
                anySucceeded = true;
                inProgress |= _pluginInterface.GetIpcSubscriber<bool>(ipcName).InvokeFunc();
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        if (anySucceeded)
            return inProgress;

        LogIpcReadFailureOnce(
            ref isPathfindInProgressIpcFailureLogged,
            "pathfind in-progress",
            lastException);
        return null;
    }

    private void LogIpcReadFailureOnce(ref bool failureLogged, string stateName, Exception? exception)
    {
        if (failureLogged)
            return;

        failureLogged = true;
        var detail = exception == null
            ? "unknown error"
            : $"{exception.GetType().Name}: {exception.Message}";
        _plugin.AddDebugLog($"[VNavIPC] Could not read vnavmesh {stateName} state via IPC: {detail}");
        _log.Debug($"[VNavIPC] Could not read vnavmesh {stateName} state via IPC: {detail}");
    }

    public void CheckAvailability(bool logStatus = true)
    {
        try
        {
            IsAvailable = false;
            
            foreach (var p in _pluginInterface.InstalledPlugins)
            {
                if (string.Equals(p.InternalName, "vnavmesh", StringComparison.OrdinalIgnoreCase))
                {
                    if (p.IsLoaded)
                    {
                        IsAvailable = true;
                    }
                    else
                    {
                        if (logStatus)
                            _plugin.AddDebugLog($"[VNavIPC] vnavmesh found but not loaded");
                    }
                    break;
                }
            }

            if (!IsAvailable && logStatus)
            {
                _plugin.AddDebugLog($"[VNavIPC] vnavmesh not available");
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
