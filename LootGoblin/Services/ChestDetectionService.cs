using System;
using System.Linq;
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
    /// Uses FrenRider-style simple targeting: look for exact name "Treasure Coffer".
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
            // FrenRider-style: simple exact name match
            foreach (var chestObj in Plugin.ObjectTable.Where(obj =>
                         obj != null && obj.Name.ToString() == "Treasure Coffer"))
            {
                var dist = Vector3.Distance(player.Position, chestObj.Position);
                if (dist > maxRange || dist >= nearestDist)
                    continue;

                nearest = chestObj;
                nearestDist = dist;
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
        else
        {
            // Fallback: try /target command approach
            _plugin.AddDebugLog("No chest found via ObjectTable - trying /target command...");
            // Note: We could send CommandHelper.SendCommand("/target \"Treasure Coffer\"") here
            // but that would make the player target it, not return the object reference
        }

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
