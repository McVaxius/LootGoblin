using Dalamud.Configuration;
using System;

namespace LootGoblin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool Enabled { get; set; } = false;
    public bool ShowMainWindow { get; set; } = true;
    public bool DebugMode { get; set; } = false;

    // Phase 3: Navigation
    public bool AutoTeleport { get; set; } = true;
    public bool RequireVNav { get; set; } = true;
    public float NavigationTimeout { get; set; } = 300f;

    // Phase 4: Party Coordination
    public bool WaitForParty { get; set; } = true;
    public bool RequireAllMounted { get; set; } = true;
    public bool AllowPillionRiders { get; set; } = true;
    public int PartyWaitTimeout { get; set; } = 60;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
