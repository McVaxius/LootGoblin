using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace LootGoblin;

public enum TargetingMethod
{
    Method1_Current,
    Method2_IsTargetable,
    Method3_ChatValidation
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
    public bool AllowPillionRiders { get; set; } = true;
    public int PartyWaitTimeout { get; set; } = 60;

    // Phase 5: State Machine
    public bool AutoStartNextMap { get; set; } = false;
    public bool EnableStateLogging { get; set; } = true;

    // Phase 6: Map Selection + Chest Interaction
    public List<uint> EnabledMapTypes { get; set; } = new();
    public float ChestInteractionRange { get; set; } = 5f;
    public bool AutoLootChest { get; set; } = true;
    public int ChestOpenTimeout { get; set; } = 10;

    // Mount Settings
    public string SelectedMount { get; set; } = "Company Chocobo";

    // Targeting Methods
    public TargetingMethod SelectedTargetingMethod { get; set; } = TargetingMethod.Method1_Current;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
