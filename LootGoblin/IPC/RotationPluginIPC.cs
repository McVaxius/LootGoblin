using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LootGoblin.IPC;

public class RotationPluginInfo
{
    public string InternalName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsAvailable { get; set; }
    public bool HasTreasureMapSupport { get; init; }
    public string Notes { get; init; } = string.Empty;
}

public class RotationPluginIPC : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;
    private readonly Plugin _plugin;
    private DateTime _lastBossModDangerRefreshUtc = DateTime.MinValue;

    public List<RotationPluginInfo> RotationPlugins { get; } = new()
    {
        new RotationPluginInfo
        {
            InternalName = "RotationSolver",
            DisplayName = "RSR (RotationSolver Reborn)",
            HasTreasureMapSupport = false,
            Notes = "General combat rotation",
        },
        new RotationPluginInfo
        {
            InternalName = "BossModReborn",
            DisplayName = "BMR (BossMod Reborn)",
            HasTreasureMapSupport = true,
            Notes = "Has AI modules for treasure map dungeons",
        },
        new RotationPluginInfo
        {
            InternalName = "vbm",
            DisplayName = "VBM",
            HasTreasureMapSupport = false,
            Notes = "Combat rotation (no treasure map modules)",
        },
        new RotationPluginInfo
        {
            InternalName = "WrathCombo",
            DisplayName = "Wrath",
            HasTreasureMapSupport = false,
            Notes = "Combat rotation",
        },
    };

    public bool BmrHasActiveModule { get; private set; }
    public string BmrActiveModuleName { get; private set; } = string.Empty;
    public int VbmForbiddenZonesCount { get; private set; }
    public bool IsBossModRebornAvailable => IsRotationPluginAvailable("BossModReborn");
    public bool IsVbmAvailable => IsRotationPluginAvailable("vbm");

    public bool BossModDangerDetected => BmrHasActiveModule || VbmForbiddenZonesCount > 0;

    public string BossModDangerReason
    {
        get
        {
            if (BmrHasActiveModule)
            {
                return string.IsNullOrWhiteSpace(BmrActiveModuleName)
                    ? "BMR active module"
                    : $"BMR active module {BmrActiveModuleName}";
            }

            return VbmForbiddenZonesCount > 0
                ? $"VBM forbidden zones {VbmForbiddenZonesCount}"
                : "No BossMod danger signal";
        }
    }

    public RotationPluginIPC(Plugin plugin, IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _plugin = plugin;
        _pluginInterface = pluginInterface;
        _log = log;

        CheckAvailability();
    }

    public void Dispose() { }

    public void CheckAvailability(bool logStatus = true)
    {
        try
        {
            var installedPlugins = _pluginInterface.InstalledPlugins;

            // Debug: Log all installed plugin InternalNames
            if (logStatus && _plugin.Configuration.DebugMode)
            {
                _plugin.AddDebugLog("=== Installed Plugins ===");
                foreach (var p in installedPlugins)
                {
                    if (p.IsLoaded)
                        _plugin.AddDebugLog($"  {p.InternalName} (loaded)");
                }
            }

            foreach (var rp in RotationPlugins)
            {
                rp.IsAvailable = false;
                foreach (var p in installedPlugins)
                {
                    if (string.Equals(p.InternalName, rp.InternalName, StringComparison.OrdinalIgnoreCase) && p.IsLoaded)
                    {
                        rp.IsAvailable = true;
                        if (logStatus)
                            _plugin.AddDebugLog($"{rp.DisplayName}: Available (matched '{p.InternalName}')");
                        break;
                    }
                }

                if (!rp.IsAvailable && logStatus && _plugin.Configuration.DebugMode)
                {
                    _plugin.AddDebugLog($"{rp.DisplayName}: Not found (looking for '{rp.InternalName}')");
                }
            }

            RefreshBossModDangerStatus(force: true);
        }
        catch (Exception ex)
        {
            _log.Error($"Error checking rotation plugins: {ex.Message}");
        }
    }

    public void RefreshBossModDangerStatus(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastBossModDangerRefreshUtc).TotalSeconds < 0.5)
            return;

        _lastBossModDangerRefreshUtc = now;
        BmrHasActiveModule = false;
        BmrActiveModuleName = string.Empty;
        VbmForbiddenZonesCount = 0;

        if (IsRotationPluginAvailable("BossModReborn"))
        {
            try
            {
                BmrHasActiveModule = _pluginInterface
                    .GetIpcSubscriber<bool>("BossMod.HasActiveModule")
                    .InvokeFunc();
            }
            catch (Exception ex)
            {
                _log.Debug($"[BossModDanger] BossMod.HasActiveModule IPC unavailable: {ex.Message}");
            }

            try
            {
                BmrActiveModuleName = _pluginInterface
                    .GetIpcSubscriber<string>("BossMod.ActiveModuleName")
                    .InvokeFunc() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _log.Debug($"[BossModDanger] BossMod.ActiveModuleName IPC unavailable: {ex.Message}");
            }
        }

        if (IsRotationPluginAvailable("vbm"))
        {
            try
            {
                VbmForbiddenZonesCount = Math.Max(
                    0,
                    _pluginInterface
                        .GetIpcSubscriber<int>("BossMod.ForbiddenZonesCount")
                        .InvokeFunc());
            }
            catch (Exception ex)
            {
                _log.Debug($"[BossModDanger] BossMod.ForbiddenZonesCount IPC unavailable: {ex.Message}");
            }
        }
    }

    private bool IsRotationPluginAvailable(string internalName)
        => RotationPlugins.Any(plugin =>
            string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase)
            && plugin.IsAvailable);
}
