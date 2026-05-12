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

[Serializable]
public class Configuration : IPluginConfiguration
{
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
    public int RepairThresholdPercent { get; set; } = 25;
    public RepairMode RepairMode { get; set; } = RepairMode.NpcNoInn;
    public bool ReturnWhenDoneEnabled { get; set; } = false;
    public ReturnWhenDoneDestination ReturnWhenDoneDestination { get; set; } = ReturnWhenDoneDestination.FC;

    // Phase 6: Map Selection + Chest Interaction
    public bool UseMapTypeFilter { get; set; } = false;
    public List<uint> EnabledMapTypes { get; set; } = new();
    public float ChestInteractionRange { get; set; } = 5f;
    public bool AutoLootChest { get; set; } = true;
    public int ChestOpenTimeout { get; set; } = 10;

    // Automation
    public bool EnableAutoDiscard { get; set; } = false;
    public bool AutoSyncFate { get; set; } = true;
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
    {
        if (itemId == 0)
            return false;

        return !UseMapTypeFilter || EnabledMapTypes.Any(enabledId => enabledId == itemId);
    }

    public IReadOnlyList<uint> GetEnabledMapIdsOrAll(IEnumerable<uint> allKnownMapIds)
    {
        return NormalizeMapIds(UseMapTypeFilter ? EnabledMapTypes : allKnownMapIds);
    }

    public void SetMapTypeEnabled(uint itemId, bool enabled, IEnumerable<uint> allKnownMapIds)
    {
        if (itemId == 0)
            return;

        EnabledMapTypes ??= new List<uint>();

        if (!UseMapTypeFilter)
        {
            UseMapTypeFilter = true;
            EnabledMapTypes = NormalizeMapIds(allKnownMapIds);
        }

        if (enabled)
        {
            if (!EnabledMapTypes.Any(enabledId => enabledId == itemId))
                EnabledMapTypes.Add(itemId);
        }
        else
        {
            EnabledMapTypes.RemoveAll(enabledId => enabledId == itemId);
        }

        EnabledMapTypes = NormalizeMapIds(EnabledMapTypes);
    }

    private static List<uint> NormalizeMapIds(IEnumerable<uint> mapIds)
    {
        return (mapIds ?? Array.Empty<uint>())
            .Where(itemId => itemId != 0)
            .Distinct()
            .ToList();
    }
}
