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

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
