using System;
using System.Collections.Generic;
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

    public List<RotationPluginInfo> RotationPlugins { get; } = new()
    {
        new RotationPluginInfo
        {
            InternalName = "RotationSolverReborn",
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

    public RotationPluginIPC(Plugin plugin, IDalamudPluginInterface pluginInterface, IPluginLog log)
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

            foreach (var rp in RotationPlugins)
            {
                rp.IsAvailable = false;
                foreach (var p in installedPlugins)
                {
                    if (string.Equals(p.InternalName, rp.InternalName, StringComparison.OrdinalIgnoreCase) && p.IsLoaded)
                    {
                        rp.IsAvailable = true;
                        break;
                    }
                }
            }

            foreach (var rp in RotationPlugins)
            {
                if (rp.IsAvailable)
                    _plugin.AddDebugLog($"{rp.DisplayName}: Available");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Error checking rotation plugins: {ex.Message}");
        }
    }
}
