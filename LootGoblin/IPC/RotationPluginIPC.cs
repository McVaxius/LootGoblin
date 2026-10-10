using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using LootGoblin.Services;

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
    private enum RsrOtherCommandType : byte
    {
        Settings,
        Rotations,
        DutyRotations,
        DoActions,
        ToggleActions,
        NextAction,
    }

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;
    private readonly Plugin _plugin;
    private readonly BossModPresetService _presets;
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
        _presets = new BossModPresetService(
            Path.Combine(pluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory, "data", "bm"),
            new BossModPresetIpc(pluginInterface));
        _pluginInterface = pluginInterface;
        _log = log;

        CheckAvailability();
    }

    public bool TrySetRsrHostileType(RsrTargetHostileType hostileType)
    {
        try
        {
            var subscriber = _pluginInterface.GetIpcSubscriber<RsrOtherCommandType, string, object>("RotationSolverReborn.OtherCommand");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HostileType {hostileType}");
            _log.Debug($"[RSR] Applied hostile targeting via IPC: {hostileType}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning($"[RSR] Failed to apply hostile targeting {hostileType}; continuing with the rotation command: {ex.Message}");
            return false;
        }
    }

    internal void RestoreRsrHealing()
    {
        try
        {
            var subscriber = _pluginInterface.GetIpcSubscriber<RsrOtherCommandType, string, object>("RotationSolverReborn.OtherCommand");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "AutoHeal true");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "UseGroundBeneficialAbility true");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealWhenNothingTodo true");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthAreaAbilityHot 0.70");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthAreaSpellHot 0.70");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthAreaAbility 0.90");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthAreaSpell 0.80");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthSingleAbilityHot 0.80");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthSingleSpellHot 0.70");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthSingleAbility 0.85");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, "HealthSingleSpell 0.80");
        }
        catch (Exception ex)
        {
            _log.Warning($"[RSR] Healing settings dispatch failed; continuing Start: {ex.Message}");
        }
    }

    public void Dispose() { }

    internal bool RefreshPackagedPresets(out string detail)
    {
        if (!CanUseBmrPresetIpc(out detail))
            return false;
        var refreshed = _presets.Refresh(out detail);
        _plugin.AddDebugLog($"[BossModPresets] {detail}");
        return refreshed;
    }

    internal bool PreparePassivePreset(out string detail)
    {
        if (!CanUseBmrPresetIpc(out detail))
            return false;
        var jobId = Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
        var prepared = _presets.PreparePassive(jobId, out detail);
        _plugin.AddDebugLog($"[BossModPresets] {detail}");
        return prepared;
    }

    private bool CanUseBmrPresetIpc(out string detail)
    {
        var loaded = _pluginInterface.InstalledPlugins.Where(plugin => plugin.IsLoaded
            && (string.Equals(plugin.InternalName, "BossModReborn", StringComparison.OrdinalIgnoreCase)
                || string.Equals(plugin.InternalName, "BossMod", StringComparison.OrdinalIgnoreCase)
                || string.Equals(plugin.InternalName, "vbm", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (loaded.Length != 1 || !string.Equals(loaded[0].InternalName, "BossModReborn", StringComparison.OrdinalIgnoreCase))
        {
            detail = loaded.Length > 1 ? "Both BossMod providers are loaded; preset IPC is ambiguous."
                : "BossModReborn is not loaded.";
            return false;
        }
        detail = string.Empty;
        return true;
    }

    private sealed class BossModPresetIpc(IDalamudPluginInterface pluginInterface) : IBossModPresetIpc
    {
        public string? GetPreset(string name)
            => pluginInterface.GetIpcSubscriber<string, string>("BossMod.Presets.Get").InvokeFunc(name);
        public bool DeletePreset(string name)
            => pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.Delete").InvokeFunc(name);
        public bool CreatePreset(string json)
            => pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create").InvokeFunc(json, true);
        public string? GetActivePreset()
            => pluginInterface.GetIpcSubscriber<string>("BossMod.Presets.GetActive").InvokeFunc();
        public bool SetActivePreset(string name)
            => pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive").InvokeFunc(name);
        public string GetAiPreset()
            => pluginInterface.GetIpcSubscriber<string>("BossMod.AI.GetPreset").InvokeFunc() ?? string.Empty;
        public void SetAiPreset(string name)
            => pluginInterface.GetIpcSubscriber<string, object>("BossMod.AI.SetPreset").InvokeAction(name);
    }

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
            Plugin.LogError($"Error checking rotation plugins: {ex.Message}");
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
