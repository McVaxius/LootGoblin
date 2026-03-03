using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using LootGoblin.Models;

namespace LootGoblin.Services;

public class InventoryService : IDisposable
{
    private readonly IPluginLog _log;
    private readonly Plugin _plugin;

    public InventoryService(Plugin plugin, IPluginLog log)
    {
        _plugin = plugin;
        _log = log;
    }

    public void Dispose() { }

    public unsafe Dictionary<uint, int> ScanForMaps()
    {
        var results = new Dictionary<uint, int>();

        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
            {
                _plugin.AddDebugLog("InventoryManager is null.");
                return results;
            }

            foreach (var itemId in TreasureMapData.AllMapItemIds)
            {
                var count = manager->GetInventoryItemCount(itemId);
                if (count > 0)
                {
                    results[itemId] = count;

                    if (TreasureMapData.KnownMaps.TryGetValue(itemId, out var info))
                        _plugin.AddDebugLog($"Found {count}x {info.Name} (ID: {itemId})");
                }
            }

            if (results.Count == 0)
                _plugin.AddDebugLog("No treasure maps found in inventory.");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to scan inventory for maps: {ex.Message}");
            _plugin.AddDebugLog($"Inventory scan error: {ex.Message}");
        }

        return results;
    }

    public unsafe int GetMapCount(uint itemId)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return 0;
            return manager->GetInventoryItemCount(itemId);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to get map count for {itemId}: {ex.Message}");
            return 0;
        }
    }
}
