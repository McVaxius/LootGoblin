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
    private DateTime lastCofferLogTime = DateTime.MinValue;
    private uint lastLoggedCofferEntityId;
    private bool lastLoggedCofferTargetable;

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
    /// Prefer exact "Treasure Coffer", then safe coffer/chest name tokens.
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
            var candidate = Plugin.ObjectTable
                .Where(IsCofferObject)
                .Select(obj => new
                {
                    Object = obj,
                    Distance = Vector3.Distance(player.Position, obj.Position),
                    NameRank = IsExactTreasureCofferName(obj.Name.TextValue) ? 0 : 1,
                    TargetRank = obj.IsTargetable ? 0 : 1,
                })
                .Where(candidate => candidate.Distance <= maxRange)
                .OrderBy(candidate => candidate.NameRank)
                .ThenBy(candidate => candidate.TargetRank)
                .ThenBy(candidate => candidate.Distance)
                .FirstOrDefault();

            if (candidate != null)
            {
                nearest = candidate.Object;
                nearestDist = candidate.Distance;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"ChestDetectionService.FindNearestCoffer failed: {ex.Message}");
        }

        NearestCoffer = nearest;
        NearestCofferDistance = nearestDist;

        if (nearest != null && ShouldLogCoffer(nearest))
        {
            _plugin.AddDebugLog(
                $"Coffer found: '{nearest.Name.TextValue}' kind={nearest.ObjectKind} targetable={nearest.IsTargetable} " +
                $"at {nearest.Position} ({nearestDist:F1}y away)");
        }

        return nearest;
    }

    public static bool IsCofferObject(IGameObject? obj)
    {
        if (obj == null)
            return false;

        if (obj.ObjectKind != ObjectKind.EventObj && obj.ObjectKind != ObjectKind.Treasure)
            return false;

        return IsSafeCofferName(obj.Name.TextValue);
    }

    public static bool IsExactTreasureCofferName(string name)
    {
        return string.Equals(name.Trim(), "Treasure Coffer", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeCofferName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return false;

        if (IsExactTreasureCofferName(trimmed))
            return true;

        return trimmed.Contains("coffer", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Contains("chest", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldLogCoffer(IGameObject coffer)
    {
        var now = DateTime.Now;
        var shouldLog = coffer.EntityId != lastLoggedCofferEntityId ||
                        coffer.IsTargetable != lastLoggedCofferTargetable ||
                        now - lastCofferLogTime >= TimeSpan.FromSeconds(5.0);
        if (!shouldLog)
            return false;

        lastLoggedCofferEntityId = coffer.EntityId;
        lastLoggedCofferTargetable = coffer.IsTargetable;
        lastCofferLogTime = now;
        return true;
    }

    /// <summary>
    /// Returns true if a coffer is within interaction range.
    /// </summary>
    public bool IsCofferInRange(float interactionRange = 5f)
    {
        return NearestCoffer != null && NearestCofferDistance <= interactionRange;
    }
}
