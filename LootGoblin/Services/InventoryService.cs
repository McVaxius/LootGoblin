using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace LootGoblin.Services;

public class InventoryService : IDisposable
{
    private readonly IPluginLog _log;
    private readonly Plugin _plugin;
    private readonly IDataManager _dataManager;
    private static int scanCounter = 0; // Static counter for reducing log spam across all instances

    public InventoryService(Plugin plugin, IDataManager dataManager, IPluginLog log)
    {
        _plugin = plugin;
        _dataManager = dataManager;
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

            var itemSheet = _dataManager.GetExcelSheet<Item>();
            if (itemSheet == null)
            {
                _plugin.AddDebugLog("Item sheet is null.");
                return results;
            }

            var containers = new[]
            {
                InventoryType.Inventory1,
                InventoryType.Inventory2,
                InventoryType.Inventory3,
                InventoryType.Inventory4,
            };

            foreach (var containerType in containers)
            {
                var container = manager->GetInventoryContainer(containerType);
                if (container == null) continue;

                for (int i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0) continue;

                    var item = itemSheet.GetRow(slot->ItemId);
                    var itemName = item.Name.ToString();
                    if (string.IsNullOrEmpty(itemName)) continue;

                    // Pattern: "Timeworn * Map", "* Special Timeworn Map", or "Mysterious Map"
                    if ((itemName.Contains("Timeworn") && itemName.Contains("Map")) || itemName == "Mysterious Map")
                    {
                        var itemId = slot->ItemId;
                        var quantity = (int)slot->Quantity;

                        if (results.ContainsKey(itemId))
                            results[itemId] += quantity;
                        else
                            results[itemId] = quantity;
                    }
                }
            }

            if (results.Count == 0)
            {
                scanCounter++;
                if (scanCounter % 5 == 1)
                {
                    _plugin.AddDebugLog("No treasure maps found in inventory.");
                }
            }
            else
            {
                scanCounter++;
                // Only log details every 5 scans to reduce spam
                if (scanCounter % 5 == 1)
                {
                    _plugin.AddDebugLog($"Found {results.Count} map types (scan #{scanCounter}):");
                    foreach (var kvp in results)
                    {
                        var item = itemSheet.GetRow(kvp.Key);
                        var name = item.Name.ToString();
                        if (string.IsNullOrEmpty(name)) name = "Unknown";
                        _plugin.AddDebugLog($"  Found {kvp.Value}x {name} (ID: {kvp.Key})");
                    }
                }
            }
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
