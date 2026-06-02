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
    private bool isNavReadyIpcFailureLogged;
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

    public bool? TryIsNavReady()
    {
        if (!IsAvailable)
            return null;

        try
        {
            return _pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc();
        }
        catch (Exception ex)
        {
            LogIpcReadFailureOnce(
                ref isNavReadyIpcFailureLogged,
                "navmesh ready",
                ex);
            return null;
        }
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
        IsAvailable = false;

        var foundInstalledButNotLoaded = false;
        var loadedMatchDetail = string.Empty;
        var metadataScanError = string.Empty;

        try
        {
            foreach (var p in _pluginInterface.InstalledPlugins)
            {
                if (!IsVNavPluginIdentity(p.InternalName) &&
                    !IsVNavPluginIdentity(p.Name))
                {
                    continue;
                }

                if (p.IsLoaded)
                {
                    IsAvailable = true;
                    loadedMatchDetail = $"InternalName='{p.InternalName}', Name='{p.Name}'";
                    break;
                }

                foundInstalledButNotLoaded = true;
            }
        }
        catch (Exception ex)
        {
            metadataScanError = $"{ex.GetType().Name}: {ex.Message}";
            _log.Debug($"[VNavIPC] Plugin metadata scan failed while checking vnavmesh: {metadataScanError}");
        }

        if (IsAvailable)
        {
            if (logStatus)
                LogAvailabilityStatus($"vnavmesh available via plugin metadata ({loadedMatchDetail})");
            return;
        }

        if (TryProbeVNavIpc(out var probeName, out var probeFailureDetail))
        {
            IsAvailable = true;
            if (logStatus)
                LogAvailabilityStatus($"vnavmesh available via IPC probe ({probeName})");
            return;
        }

        if (!logStatus)
            return;

        if (foundInstalledButNotLoaded)
        {
            LogAvailabilityStatus("vnavmesh found but not loaded; IPC probe failed");
            return;
        }

        var scanDetail = string.IsNullOrWhiteSpace(metadataScanError)
            ? "not found in plugin list"
            : $"plugin list unavailable ({metadataScanError})";
        var probeDetail = string.IsNullOrWhiteSpace(probeFailureDetail)
            ? "no IPC responder"
            : probeFailureDetail;
        LogAvailabilityStatus($"vnavmesh not available: {scanDetail}; {probeDetail}");
    }

    private static bool IsVNavPluginIdentity(string? value)
        => string.Equals(NormalizePluginIdentity(value), "vnavmesh", StringComparison.Ordinal);

    private static string NormalizePluginIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private bool TryProbeVNavIpc(out string probeName, out string failureDetail)
    {
        probeName = string.Empty;
        failureDetail = string.Empty;

        foreach (var ipcName in new[] { "vnavmesh.Nav.IsReady", "vnavmesh.Path.IsRunning", "NavmeshManager.IsRunning" })
        {
            try
            {
                _pluginInterface.GetIpcSubscriber<bool>(ipcName).InvokeFunc();
                probeName = ipcName;
                return true;
            }
            catch (Exception ex)
            {
                failureDetail = $"{ipcName}: {ex.GetType().Name}: {ex.Message}";
            }
        }

        return false;
    }

    private void LogAvailabilityStatus(string message)
    {
        var formatted = $"[VNavIPC] {message}";
        _plugin.AddDebugLog(formatted);
        _log.Debug(formatted);
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
        CommandHelper.SendChatCommand("/vnav flyflag");
        CommandHelper.SendChatCommand(cmd);
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
