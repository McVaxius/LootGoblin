using System;
using System.Collections.Generic;
using System.Linq;
using LootGoblin.Models;
using LootGoblin.Services;

namespace LootGoblin;

[Serializable]
public sealed class MapGatherCharacterConfig
{
    public uint SelectedGatherJobId { get; set; } = 0;
    public List<uint> GatherEnabledMapTypes { get; set; } = new();
    public MapAllowanceStatus? MapAllowanceStatusSnapshot { get; set; }

    public bool HasGatherJob => SelectedGatherJobId != 0;

    public bool IsMapGatherEnabled(uint itemId)
        => itemId != 0 && GatherEnabledMapTypes?.Any(enabledId => enabledId == itemId) == true;

    public void SetMapGatherEnabled(uint itemId, bool enabled)
    {
        if (itemId == 0)
            return;

        GatherEnabledMapTypes ??= new List<uint>();

        if (enabled)
        {
            if (!GatherEnabledMapTypes.Any(enabledId => enabledId == itemId))
                GatherEnabledMapTypes.Add(itemId);
        }
        else
        {
            GatherEnabledMapTypes.RemoveAll(enabledId => enabledId == itemId);
        }

        Normalize();
    }

    public void CopyLegacyGatherSettings(uint selectedGatherJobId, IEnumerable<uint>? gatherEnabledMapTypes)
    {
        SelectedGatherJobId = selectedGatherJobId;
        GatherEnabledMapTypes = NormalizeMapIds(gatherEnabledMapTypes).ToList();
        Normalize();
    }

    public void SetMapAllowanceSnapshot(MapAllowanceStatus status)
    {
        MapAllowanceStatusSnapshot = status.IsAvailable ? status : null;
    }

    public bool TryGetMapAllowanceSnapshot(DateTimeOffset now, out MapAllowanceStatus status)
    {
        if (MapAllowanceStatusSnapshot is { } snapshot && snapshot.IsAvailable)
        {
            status = snapshot.WithLiveRemaining(now);
            MapAllowanceStatusSnapshot = status;
            return true;
        }

        status = MapAllowanceVerificationCache.UnverifiedStatus;
        return false;
    }

    public void Normalize()
    {
        if (!ClassJobOptions.IsGatherJob(SelectedGatherJobId))
            SelectedGatherJobId = 0;

        GatherEnabledMapTypes = NormalizeMapIds(GatherEnabledMapTypes)
            .Where(itemId =>
                TreasureMapData.KnownMaps.TryGetValue(itemId, out var mapInfo) &&
                mapInfo.IsGatherable)
            .ToList();

        if (MapAllowanceStatusSnapshot is { } snapshot && !snapshot.IsAvailable)
            MapAllowanceStatusSnapshot = null;
    }

    private static IEnumerable<uint> NormalizeMapIds(IEnumerable<uint>? mapIds)
        => (mapIds ?? Array.Empty<uint>())
            .Where(itemId => itemId != 0)
            .Distinct();
}

[Serializable]
public sealed class MapGatherCharacterConfigStore
{
    public Dictionary<string, MapGatherCharacterConfig> Characters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool LegacyGatherSettingsMigrated { get; set; }

    public MapGatherCharacterConfig BindCharacter(
        string characterKey,
        uint legacySelectedGatherJobId,
        IEnumerable<uint>? legacyGatherEnabledMapTypes,
        out bool migratedLegacySettings)
    {
        EnsureInitialized();

        if (!Characters.TryGetValue(characterKey, out var characterConfig) || characterConfig == null)
        {
            characterConfig = new MapGatherCharacterConfig();
            Characters[characterKey] = characterConfig;
        }

        migratedLegacySettings = false;
        if (!LegacyGatherSettingsMigrated)
        {
            characterConfig.CopyLegacyGatherSettings(legacySelectedGatherJobId, legacyGatherEnabledMapTypes);
            LegacyGatherSettingsMigrated = true;
            migratedLegacySettings = true;
        }

        characterConfig.Normalize();
        return characterConfig;
    }

    public void Normalize()
    {
        EnsureInitialized();

        foreach (var characterConfig in Characters.Values)
            characterConfig?.Normalize();
    }

    private void EnsureInitialized()
    {
        Characters ??= new Dictionary<string, MapGatherCharacterConfig>(StringComparer.OrdinalIgnoreCase);
    }
}
