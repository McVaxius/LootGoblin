using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
    private enum RsrStateCommandType : byte
    {
        Auto = 1,
        Manual = 3,
    }

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
    private object? _bossModInstance;
    private Assembly? _bossModAssembly;
    private string? _bossModInternalName;

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
            InternalName = "GodMod",
            DisplayName = "GodMod",
            HasTreasureMapSupport = true,
            Notes = "Combat rotation and treasure map AI",
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
    public int GodModForbiddenZonesCount { get; private set; }
    public bool IsBossModRebornAvailable => IsRotationPluginAvailable("BossModReborn");
    public bool IsGodModAvailable => IsRotationPluginAvailable("GodMod");
    public bool IsVbmAvailable => IsRotationPluginAvailable("vbm");
    internal bool HasPinnedBossModHost => _bossModInstance != null;
    internal string? BossModAiCommand => _bossModInternalName switch
    {
        "GodMod" => "/gmai",
        "BossModReborn" => "/bmrai",
        _ => null,
    };
    public string BossModHostName => _bossModInternalName == "GodMod" || (_bossModInstance == null && IsGodModAvailable) ? "GodMod" : "BMR";

    public bool BossModDangerDetected => BmrHasActiveModule || GodModForbiddenZonesCount > 0 || VbmForbiddenZonesCount > 0;

    public string BossModDangerReason
    {
        get
        {
            if (BmrHasActiveModule)
            {
                return string.IsNullOrWhiteSpace(BmrActiveModuleName)
                    ? $"{BossModHostName} active module"
                    : $"{BossModHostName} active module {BmrActiveModuleName}";
            }

            if (GodModForbiddenZonesCount > 0)
                return $"GodMod forbidden zones {GodModForbiddenZonesCount}";

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
            new BossModPresetIpc(this, pluginInterface));
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
            _log.Warning($"[RSR] Failed to apply hostile targeting {hostileType}; continuing with mode activation: {ex.Message}");
            return false;
        }
    }

    public bool TrySetRsrMode(bool manual)
    {
        var mode = manual ? RsrStateCommandType.Manual : RsrStateCommandType.Auto;
        try
        {
            _pluginInterface.GetIpcSubscriber<RsrStateCommandType, object>("RotationSolverReborn.ChangeOperatingMode")
                .InvokeAction(mode);
            _log.Debug($"[RSR] Applied operating mode via IPC: {mode}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning($"[RSR] Failed to apply operating mode {mode}: {ex.Message}");
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
            // RSR Settings parses numeric strings with the current culture.
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthAreaAbilityHot {0.70f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthAreaSpellHot {0.70f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthAreaAbility {0.90f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthAreaSpell {0.80f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthSingleAbilityHot {0.80f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthSingleSpellHot {0.70f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthSingleAbility {0.85f.ToString("R", CultureInfo.CurrentCulture)}");
            subscriber.InvokeAction(RsrOtherCommandType.Settings, $"HealthSingleSpell {0.80f.ToString("R", CultureInfo.CurrentCulture)}");
        }
        catch (Exception ex)
        {
            _log.Warning($"[RSR] Healing settings dispatch failed; continuing Start: {ex.Message}");
        }
    }

    public void Dispose() { }

    internal bool RefreshPackagedPresets(out string detail)
    {
        // A new accepted run may choose a host; control within that run never changes hosts.
        _bossModInstance = null;
        _bossModAssembly = null;
        _bossModInternalName = null;
        _presets.Refresh(out _, install: false);
        if (!IsBossModRebornAvailable && !IsGodModAvailable)
        {
            detail = string.Empty;
            return true;
        }
        if (!CanUseBossModPresetIpc(out detail))
            return false;
        var refreshed = _presets.Refresh(out detail);
        _plugin.AddDebugLog($"[BossModPresets] {detail}");
        return refreshed;
    }

    internal bool PrepareCombatPreset(bool useBmrRotation, out string detail)
    {
        if (!CanUseBossModPresetIpc(out detail))
            return false;
        var jobId = Plugin.ObjectTable.LocalPlayer?.ClassJob.RowId ?? 0;
        var prepared = _presets.Prepare(jobId, useBmrRotation, out detail);
        _plugin.AddDebugLog($"[BossModPresets] {detail}");
        return prepared;
    }

    internal bool CanUseBossModPresetIpc(out string detail, bool pin = true)
    {
        try
        {
            var loaded = _pluginInterface.InstalledPlugins.Where(plugin => plugin.IsLoaded
                && (string.Equals(plugin.InternalName, "BossModReborn", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(plugin.InternalName, "GodMod", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(plugin.InternalName, "BossMod", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(plugin.InternalName, "vbm", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (loaded.Length != 1 || (!string.Equals(loaded[0].InternalName, "BossModReborn", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(loaded[0].InternalName, "GodMod", StringComparison.OrdinalIgnoreCase)))
            {
                detail = loaded.Length > 1 ? "Multiple BossMod providers are loaded; their shared IPC is ambiguous."
                    : "A compatible BossModReborn or GodMod host is not loaded.";
                return false;
            }

            var name = string.Equals(loaded[0].InternalName, "GodMod", StringComparison.OrdinalIgnoreCase) ? "GodMod" : "BossModReborn";
            var live = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == name)
                .Select(assembly => new
                {
                    Assembly = assembly,
                    Instance = assembly.GetType("BossMod.AI.AIManager")?.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null),
                })
                .Where(host => host.Instance != null && ReferenceEquals(host.Instance.GetType().Assembly, host.Assembly)).ToArray();
            if (live.Length != 1)
            {
                detail = $"{name}'s live AI host is unavailable or ambiguous.";
                return false;
            }
            if (_bossModInstance != null && (_bossModInternalName != name
                || !ReferenceEquals(_bossModInstance, live[0].Instance) || !ReferenceEquals(_bossModAssembly, live[0].Assembly)))
            {
                detail = "The selected BossMod host changed; start a new run before using its preset or AI controls.";
                return false;
            }
            if (pin)
            {
                _bossModInstance = live[0].Instance;
                _bossModAssembly = live[0].Assembly;
                _bossModInternalName = name;
            }
            detail = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            detail = $"BossMod host qualification failed: {ex.GetBaseException().Message}";
            return false;
        }
    }

    private sealed class BossModPresetIpc(RotationPluginIPC owner, IDalamudPluginInterface pluginInterface) : IBossModPresetIpc
    {
        public bool IsRotationModuleAvailable(string moduleName)
        {
            try
            {
                if (!owner.CanUseBossModPresetIpc(out _))
                    return false;
                var assembly = owner._bossModAssembly;
                var registry = assembly?.GetType("BossMod.Autorotation.RotationModuleRegistry");
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
                if (registry?.GetMethod("FindType", flags)?.Invoke(null, [moduleName]) is not Type type
                    || type.FullName != moduleName
                    || registry.GetField("Modules", flags)?.GetValue(null) is not System.Collections.IDictionary modules
                    || !modules.Contains(type) || modules[type] is not { } entry)
                    return false;
                const System.Reflection.BindingFlags memberFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
                return (entry.GetType().GetProperty("Definition", memberFlags)?.GetValue(entry)
                    ?? entry.GetType().GetField("Definition", memberFlags)?.GetValue(entry)) != null;
            }
            catch { return false; }
        }

        public string? GetPreset(string name)
        {
            if (!owner.CanUseBossModPresetIpc(out var detail)) throw new InvalidOperationException(detail);
            return pluginInterface.GetIpcSubscriber<string, string>("BossMod.Presets.Get").InvokeFunc(name);
        }
        public bool CreatePreset(string json)
        {
            if (!owner.CanUseBossModPresetIpc(out var detail)) throw new InvalidOperationException(detail);
            return pluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create").InvokeFunc(json, true);
        }
        public string? GetActivePreset()
        {
            if (!owner.CanUseBossModPresetIpc(out var detail)) throw new InvalidOperationException(detail);
            return pluginInterface.GetIpcSubscriber<string>("BossMod.Presets.GetActive").InvokeFunc();
        }
        public bool SetActivePreset(string name)
        {
            if (!owner.CanUseBossModPresetIpc(out var detail)) throw new InvalidOperationException(detail);
            return pluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive").InvokeFunc(name);
        }
        public string GetAiPreset()
        {
            if (!owner.CanUseBossModPresetIpc(out var detail)) throw new InvalidOperationException(detail);
            return pluginInterface.GetIpcSubscriber<string>("BossMod.AI.GetPreset").InvokeFunc() ?? string.Empty;
        }
        public void SetAiPreset(string name)
        {
            if (!owner.CanUseBossModPresetIpc(out var detail)) throw new InvalidOperationException(detail);
            pluginInterface.GetIpcSubscriber<string, object>("BossMod.AI.SetPreset").InvokeAction(name);
        }
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
        GodModForbiddenZonesCount = 0;

        if ((IsBossModRebornAvailable || IsGodModAvailable) && CanUseBossModPresetIpc(out _, pin: false))
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

            if (IsGodModAvailable && CanUseBossModPresetIpc(out _, pin: false))
            {
                try
                {
                    GodModForbiddenZonesCount = Math.Max(0, _pluginInterface
                        .GetIpcSubscriber<int>("BossMod.Hints.ForbiddenZonesCount").InvokeFunc());
                }
                catch (Exception ex)
                {
                    _log.Debug($"[BossModDanger] BossMod.Hints.ForbiddenZonesCount IPC unavailable: {ex.Message}");
                }
            }
        }

        if (IsVbmAvailable && !IsBossModRebornAvailable && !IsGodModAvailable)
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
