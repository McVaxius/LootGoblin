using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace LootGoblin.Services;

/// <summary>
/// Scans the ObjectTable for treasure coffers (chests spawned by opening treasure maps).
/// In FFXIV, treasure chests appear as EventObj-type game objects named "Treasure Coffer".
/// </summary>
public class ChestDetectionService : IDisposable
{
    private readonly Plugin _plugin;
    private readonly IPluginLog _log;

    public IGameObject? NearestCoffer { get; private set; }
    public float NearestCofferDistance { get; private set; } = float.MaxValue;

    public ChestDetectionService(Plugin plugin, IPluginLog log)
    {
        _plugin = plugin;
        _log = log;
    }

    public void Dispose() { }

    /// <summary>
    /// Scan the ObjectTable for the nearest treasure coffer.
    /// Searches for EventObj objects with names containing "Treasure" or "Coffer".
    /// Returns the nearest one within maxRange (default 100 yalms).
    /// </summary>
    public IGameObject? FindNearestCoffer(float maxRange = 100f)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            NearestCoffer = null;
            NearestCofferDistance = float.MaxValue;
            return null;
        }

        IGameObject? nearest = null;
        var nearestDist = float.MaxValue;

        try
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj == null) continue;
                if (obj.ObjectKind != ObjectKind.EventObj) continue;

                var name = obj.Name.TextValue;
                if (string.IsNullOrEmpty(name)) continue;

                if (!name.Contains("Treasure", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Coffer", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dist = Vector3.Distance(player.Position, obj.Position);
                if (dist < nearestDist && dist <= maxRange)
                {
                    nearestDist = dist;
                    nearest = obj;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"ChestDetectionService.FindNearestCoffer failed: {ex.Message}");
        }

        NearestCoffer = nearest;
        NearestCofferDistance = nearestDist;

        if (nearest != null)
            _plugin.AddDebugLog($"Coffer found: '{nearest.Name.TextValue}' at {nearest.Position} ({nearestDist:F1}y away)");

        return nearest;
    }

    /// <summary>
    /// Returns true if a coffer is within interaction range.
    /// </summary>
    public bool IsCofferInRange(float interactionRange = 5f)
    {
        return NearestCoffer != null && NearestCofferDistance <= interactionRange;
    }
}
