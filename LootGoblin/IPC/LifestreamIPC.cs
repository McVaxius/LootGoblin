using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LootGoblin.IPC;

public sealed class LifestreamIPC : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private DateTime nextStatusRefreshUtc = DateTime.MinValue;

    public bool IsAvailable { get; private set; }
    public string StatusText { get; private set; } = "Lifestream IPC has not been checked.";

    public LifestreamIPC(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        RefreshStatus(force: true);
    }

    public void Dispose()
    {
    }

    public void RefreshStatus(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now < nextStatusRefreshUtc)
            return;

        nextStatusRefreshUtc = now + TimeSpan.FromSeconds(5);
        try
        {
            _ = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
            IsAvailable = true;
            StatusText = "Lifestream IPC is available.";
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            StatusText = "Lifestream is not installed, loaded, or exposing supported IPC.";
            log.Debug($"[Lifestream] IsBusy unavailable: {ex.Message}");
        }
    }

    public bool TryIsBusy(out bool isBusy)
    {
        try
        {
            isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
            IsAvailable = true;
            return true;
        }
        catch
        {
            isBusy = false;
            IsAvailable = false;
            return false;
        }
    }

    public bool TryChangeWorld(uint worldId, out string error)
    {
        error = string.Empty;
        if (worldId == 0)
        {
            error = "The destination world ID is invalid.";
            return false;
        }

        RefreshStatus(force: true);
        if (!IsAvailable)
        {
            error = StatusText;
            return false;
        }

        try
        {
            if (pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc())
            {
                error = "Lifestream is already busy.";
                return false;
            }

            var accepted = pluginInterface
                .GetIpcSubscriber<uint, bool>("Lifestream.ChangeWorldById")
                .InvokeFunc(worldId);
            if (!accepted)
                error = $"Lifestream did not accept travel to world {worldId}.";
            return accepted;
        }
        catch (Exception ex)
        {
            error = $"Lifestream world travel failed: {ex.Message}";
            return false;
        }
    }

    public bool TryTeleport(uint aetheryteId, out string error)
    {
        error = string.Empty;
        try
        {
            if (pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc())
            {
                error = "Lifestream is already busy.";
                return false;
            }

            var accepted = pluginInterface
                .GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport")
                .InvokeFunc(aetheryteId, 0);
            if (!accepted)
                error = $"Aetheryte {aetheryteId} is not available for Lifestream teleport.";
            return accepted;
        }
        catch (Exception ex)
        {
            error = $"Lifestream teleport failed: {ex.Message}";
            return false;
        }
    }

    public bool TryAbort(out string error)
    {
        error = string.Empty;
        try
        {
            pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not abort LootGoblin-owned Lifestream travel: {ex.Message}";
            return false;
        }
    }
}
