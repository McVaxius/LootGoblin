using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LootGoblin;

public enum RepairMode
{
    Self,
    NpcNoInn,
}

public enum ReturnWhenDoneDestination
{
    FC,
    Personal,
    Inn,
}

public enum TreasureHighLowMode
{
    Skip,
    SolveExpectedValue,
    ObserveOnly,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int MapRunCountMax = int.MaxValue;

    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool Enabled { get; set; } = false;
    public bool ShowMainWindow { get; set; } = true;
    public bool DebugMode { get; set; } = false;
    public bool KrangleNames { get; set; } = false;

    // Phase 3: Navigation
    public bool AutoTeleport { get; set; } = true;
    public bool RequireVNav { get; set; } = true;
    public float NavigationTimeout { get; set; } = 300f;

    // Phase 4: Party Coordination
    public bool WaitForParty { get; set; } = true;
    public bool RequireAllMounted { get; set; } = true;
    public int PartyWaitTimeout { get; set; } = 60;
    public bool PartyWaitBeforeDismount { get; set; } = false;
    public bool PartyWaitBeforeDismountUseCountThreshold { get; set; } = false;
    public int PartyWaitBeforeDismountRequiredOthers { get; set; } = 7;

    // Phase 5: State Machine
    public bool AutoStartNextMap { get; set; } = false;
    public bool EnableStateLogging { get; set; } = true;
    public bool UseAdsInsteadOfLegacyDungeonSolver { get; set; } = true;
    public bool EnableRetainerMapRetrieval { get; set; } = true;
    public bool EnableSaddlebagMapRetrieval { get; set; } = true;
    public int RepairThresholdPercent { get; set; } = 25;
    public RepairMode RepairMode { get; set; } = RepairMode.NpcNoInn;
    public bool ReturnWhenDoneEnabled { get; set; } = false;
    public ReturnWhenDoneDestination ReturnWhenDoneDestination { get; set; } = ReturnWhenDoneDestination.FC;

    // Phase 6: Map Selection + Chest Interaction
    public bool UseMapTypeFilter { get; set; } = true;
    public List<uint> EnabledMapTypes { get; set; } = new();
    public Dictionary<uint, int> MapRunCounts { get; set; } = new();
    public bool ShowAllKnownMapTypes { get; set; } = false;
    public float ChestInteractionRange { get; set; } = 5f;
    public bool AutoLootChest { get; set; } = true;
    public int ChestOpenTimeout { get; set; } = 10;
    public TreasureHighLowMode TreasureHighLowMode { get; set; } = TreasureHighLowMode.Skip;

    // Automation
    public bool EnableAutoDiscard { get; set; } = false;
    public bool AutoSyncFate { get; set; } = true;
    public bool BmrReduceActivationRangeForOutdoorAreas { get; set; } = true;
    public bool BmrDisableHuntModules { get; set; } = true;
    public int FeedMeItemId { get; set; } = 4650;
    public string FeedMeItem { get; set; } = "Boiled Egg";
    public bool FeedMeUseHighQuality { get; set; } = false;
    public bool FeedMeSearch { get; set; } = true;
    public bool SummonChocobo { get; set; } = false;
    public string CompanionStance { get; set; } = "Free Stance";
    public List<string> LandingOrDutyCommandTriggers { get; set; } = new()
    {
        "/rotation Auto",
        "/bmrai on",
        "/vbmai on",
        "/echo wheee",
        string.Empty,
    };

    public List<string> FinishCommandTriggers { get; set; } = new()
    {
        "/rotation cancel",
        "/bmrai off",
        "/vbmai off",
        string.Empty,
        string.Empty,
    };

    // Mount Settings
    public string SelectedMount { get; set; } = "Company Chocobo";

    // Map Location Database
    public bool AutoUpdateLocOnLogin { get; set; } = true;
    public string LastCommunityLocationsRefreshPluginVersion { get; set; } = "";

    // XYZ Cycling
    public bool CycleGroundOnly { get; set; } = false;
    public bool ShowDebugMapCompletion { get; set; } = false;

    // Alexandrite Farming
    public int AlexandriteRunCount { get; set; } = 1;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }

    public bool IsMapTypeEnabled(uint itemId)
        => GetMapRunCount(itemId) > 0;

    public bool IsMapRunCountMax(uint itemId)
        => GetMapRunCount(itemId) == MapRunCountMax;

    public int GetMapRunCount(uint itemId)
    {
        if (itemId == 0)
            return 0;

        MapRunCounts ??= new Dictionary<uint, int>();
        if (MapRunCounts.TryGetValue(itemId, out var count))
            return NormalizeMapRunCount(count);

        return EnabledMapTypes?.Any(enabledId => enabledId == itemId) == true
            ? MapRunCountMax
            : 0;
    }

    public IReadOnlyList<uint> GetEnabledMapIdsOrAll(IEnumerable<uint> allKnownMapIds)
        => GetRunnableMapIds(allKnownMapIds);

    public IReadOnlyList<uint> GetRunnableMapIds(IEnumerable<uint> allKnownMapIds)
    {
        return NormalizeMapIds(EnabledMapTypes)
            .Where(itemId => GetMapRunCount(itemId) > 0)
            .ToList();
    }

    public void SetMapTypeEnabled(uint itemId, bool enabled, IEnumerable<uint> allKnownMapIds)
        => SetMapRunCount(itemId, enabled ? MapRunCountMax : 0);

    public void SetMapRunCountToMax(uint itemId)
        => SetMapRunCount(itemId, MapRunCountMax);

    public void SetMapRunCount(uint itemId, int count)
    {
        if (itemId == 0)
            return;

        EnabledMapTypes ??= new List<uint>();
        MapRunCounts ??= new Dictionary<uint, int>();

        UseMapTypeFilter = true;
        count = NormalizeMapRunCount(count);

        if (count > 0)
        {
            MapRunCounts[itemId] = count;
            if (!EnabledMapTypes.Any(enabledId => enabledId == itemId))
                EnabledMapTypes.Add(itemId);
        }
        else
        {
            MapRunCounts.Remove(itemId);
            EnabledMapTypes.RemoveAll(enabledId => enabledId == itemId);
        }

        EnabledMapTypes = NormalizeMapIds(EnabledMapTypes);
    }

    public bool TryDecrementMapRunCount(uint itemId, out int remaining)
    {
        remaining = GetMapRunCount(itemId);
        if (remaining <= 0 || remaining == MapRunCountMax)
            return false;

        remaining = Math.Max(0, remaining - 1);
        SetMapRunCount(itemId, remaining);
        Save();
        return true;
    }

    public void NormalizeConfiguredMapRuns()
    {
        EnabledMapTypes = NormalizeMapIds(EnabledMapTypes);
        MapRunCounts ??= new Dictionary<uint, int>();

        var normalizedCounts = new Dictionary<uint, int>();
        foreach (var kvp in MapRunCounts)
        {
            var itemId = kvp.Key;
            if (itemId == 0)
                continue;

            var count = NormalizeMapRunCount(kvp.Value);
            if (count > 0)
                normalizedCounts[itemId] = count;
        }

        foreach (var itemId in EnabledMapTypes)
        {
            if (!normalizedCounts.ContainsKey(itemId))
                normalizedCounts[itemId] = MapRunCountMax;
        }

        MapRunCounts = normalizedCounts;
        EnabledMapTypes = NormalizeMapIds(MapRunCounts.Keys);
        UseMapTypeFilter = true;
    }

    private static int NormalizeMapRunCount(int count)
        => count <= 0 ? 0 : count;

    private static List<uint> NormalizeMapIds(IEnumerable<uint> mapIds)
    {
        return (mapIds ?? Array.Empty<uint>())
            .Where(itemId => itemId != 0)
            .Distinct()
            .ToList();
    }
}
