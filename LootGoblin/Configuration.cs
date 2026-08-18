using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using LootGoblin.Models;

namespace LootGoblin;

public enum RepairMode
{
    Self,
    NpcNoInn,
    NpcNoInnNoTeleport,
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

public enum RsrTargetHostileType : byte
{
    AllTargetsCanAttack,
    TargetsHaveTarget,
    AllTargetsWhenSoloInDuty,
    AllTargetsWhenSolo,
    SoloDeepDungeonSmart,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const int MapRunCountMax = int.MaxValue;
    public const int DefaultCommandTriggerRowCount = 10;
    public const RsrTargetHostileType DefaultRsrTargetHostileType = RsrTargetHostileType.TargetsHaveTarget;

    private static readonly string[] LandingOrDutyCommandTriggerDefaultValues =
    {
        "/rotation Auto",
        "/bmrai on",
        "/vbmai on",
        "/fr off",
        "/cbt disable AutoFollow",
        "/bmrai followoutofcombat off",
        "/bmrai followcombat off",
        "/vbmai follow Slot1",
        string.Empty,
        string.Empty,
    };

    private static readonly string[] FinishCommandTriggerDefaultValues =
    {
        "/rotation cancel",
        "/bmrai off",
        "/vbmai off",
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
    };

    public static IReadOnlyList<string> LandingOrDutyCommandTriggerDefaults => LandingOrDutyCommandTriggerDefaultValues;
    public static IReadOnlyList<string> FinishCommandTriggerDefaults => FinishCommandTriggerDefaultValues;
    public static List<string> CreateDefaultLandingOrDutyCommandTriggers() => new(LandingOrDutyCommandTriggerDefaultValues);
    public static List<string> CreateDefaultFinishCommandTriggers() => new(FinishCommandTriggerDefaultValues);

    private int partyTeleportDelaySeconds = 0;

    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool Enabled { get; set; } = false;
    public bool ShowMainWindow { get; set; } = true;
    public bool DebugMode { get; set; } = false;
    public bool EnableDedicatedDiagnosticLog { get; set; } = false;
    public bool KrangleNames { get; set; } = false;

    // Phase 3: Navigation
    public bool AutoTeleport { get; set; } = true;
    public bool RequireVNav { get; set; } = true;
    public float NavigationTimeout { get; set; } = 300f;

    // Phase 4: Party Coordination
    public bool WaitForParty { get; set; } = true;
    public bool WaitForPartyForThiefMapsUnderwater { get; set; } = true;
    public bool RequireAllMounted { get; set; } = true;
    public int PartyWaitTimeout { get; set; } = 60;
    public int PartyTeleportDelaySeconds
    {
        get => partyTeleportDelaySeconds;
        set => partyTeleportDelaySeconds = Math.Clamp(value, 0, 300);
    }
    public bool PartyWaitBeforeDismount { get; set; } = false;
    public bool PartyWaitBeforeDismountUseCountThreshold { get; set; } = false;
    public int PartyWaitBeforeDismountRequiredOthers { get; set; } = 7;
    public bool AvoidTamamizuAetheryte { get; set; } = true;

    // Phase 5: State Machine
    public bool AutoStartNextMap { get; set; } = true;
    public bool EnableStateLogging { get; set; } = true;
    public bool UseAdsInsteadOfLegacyDungeonSolver { get; set; } = true;
    public bool EnableRetainerMapRetrieval { get; set; } = true;
    public bool EnableSaddlebagMapRetrieval { get; set; } = true;
    public int RepairThresholdPercent { get; set; } = 75;
    public RepairMode RepairMode { get; set; } = RepairMode.NpcNoInn;
    public bool ReturnWhenDoneEnabled { get; set; } = false;
    public ReturnWhenDoneDestination ReturnWhenDoneDestination { get; set; } = ReturnWhenDoneDestination.FC;
    public uint SelectedCombatJobId { get; set; } = 0;
    public uint SelectedGatherJobId { get; set; } = 0;
    public int MaxMapAllowanceWaitMinutes { get; set; } = 10;
    public MapGatherCharacterConfigStore MapGatherCharacterConfigs { get; set; } = new();

    // Phase 6: Map Selection + Chest Interaction
    public bool UseMapTypeFilter { get; set; } = true;
    public List<uint> EnabledMapTypes { get; set; } = new();
    public Dictionary<uint, int> MapRunCounts { get; set; } = new();
    public List<uint> GatherEnabledMapTypes { get; set; } = new();
    public bool ShowAllKnownMapTypes { get; set; } = false;
    public float ChestInteractionRange { get; set; } = 5f;
    public bool AutoLootChest { get; set; } = true;
    public int ChestOpenTimeout { get; set; } = 10;
    public TreasureHighLowMode TreasureHighLowMode { get; set; } = TreasureHighLowMode.SolveExpectedValue;

    // Automation
    public bool EnableAutoDiscard { get; set; } = false;
    public bool AutoSyncFate { get; set; } = true;
    public RsrTargetHostileType RsrTargetHostileType { get; set; } = DefaultRsrTargetHostileType;
    public bool BmrReduceActivationRangeForOutdoorAreas { get; set; } = true;
    public bool BmrDisableHuntModules { get; set; } = true;
    public int FeedMeItemId { get; set; } = 4650;
    public string FeedMeItem { get; set; } = "Boiled Egg";
    public bool FeedMeUseHighQuality { get; set; } = false;
    public bool FeedMeSearch { get; set; } = true;
    public bool SummonChocobo { get; set; } = false;
    public string CompanionStance { get; set; } = "Free Stance";
    public List<string> LandingOrDutyCommandTriggers { get; set; } = null!;
    public List<string> FinishCommandTriggers { get; set; } = null!;

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
        PartyTeleportDelaySeconds = Math.Clamp(PartyTeleportDelaySeconds, 0, 300);
        MaxMapAllowanceWaitMinutes = Math.Clamp(MaxMapAllowanceWaitMinutes, 0, 1440);
        if (!Enum.IsDefined(typeof(RsrTargetHostileType), RsrTargetHostileType))
            RsrTargetHostileType = DefaultRsrTargetHostileType;
        NormalizeConfiguredJobAndGatherMaps();
        MapGatherCharacterConfigs ??= new MapGatherCharacterConfigStore();
        MapGatherCharacterConfigs.Normalize();
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

        NormalizeConfiguredJobAndGatherMaps();
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

    public void NormalizeConfiguredJobAndGatherMaps()
    {
        if (!ClassJobOptions.IsCombatJob(SelectedCombatJobId))
            SelectedCombatJobId = 0;

        if (!ClassJobOptions.IsGatherJob(SelectedGatherJobId))
            SelectedGatherJobId = 0;

        GatherEnabledMapTypes = NormalizeMapIds(GatherEnabledMapTypes)
            .Where(itemId =>
                TreasureMapData.KnownMaps.TryGetValue(itemId, out var mapInfo) &&
                mapInfo.IsGatherable)
            .ToList();
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
