using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace LootGoblin.Services;

public class MapSourceCount
{
    public int Inventory { get; set; }
    public int Saddlebag { get; set; }
    public int PremiumSaddlebag { get; set; }
    public int Total => Inventory + Saddlebag + PremiumSaddlebag;
    public int SaddlebagTotal => Saddlebag + PremiumSaddlebag;
}

public class InventoryService : IDisposable
{
    private readonly IPluginLog _log;
    private readonly Plugin _plugin;
    private readonly IDataManager _dataManager;
    private static int scanCounter = 0; // Static counter for reducing log spam across all instances

    private readonly struct ContainerSpec
    {
        public ContainerSpec(InventoryType type, Action<MapSourceCount, int> addQuantity)
        {
            Type = type;
            AddQuantity = addQuantity;
        }

        public InventoryType Type { get; }
        public Action<MapSourceCount, int> AddQuantity { get; }
    }

    public InventoryService(Plugin plugin, IDataManager dataManager, IPluginLog log)
    {
        _plugin = plugin;
        _dataManager = dataManager;
        _log = log;
    }

    public void Dispose() { }

    public Dictionary<uint, int> ScanForMaps()
    {
        return ScanForMapSources(includeSaddlebags: false)
            .Where(kvp => kvp.Value.Inventory > 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Inventory);
    }

    public unsafe Dictionary<uint, MapSourceCount> ScanForMapSources(bool includeSaddlebags = true)
    {
        var results = new Dictionary<uint, MapSourceCount>();

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

            var containers = GetContainerSpecs(includeSaddlebags);

            foreach (var spec in containers)
            {
                var container = manager->GetInventoryContainer(spec.Type);
                if (container == null) continue;
                if (!container->IsLoaded) continue;

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

                        if (!results.TryGetValue(itemId, out var sourceCount))
                        {
                            sourceCount = new MapSourceCount();
                            results[itemId] = sourceCount;
                        }

                        spec.AddQuantity(sourceCount, quantity);
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
                        var counts = kvp.Value;
                        _plugin.AddDebugLog(
                            $"  Found {counts.Total}x {name} (ID: {kvp.Key}) " +
                            $"[inventory={counts.Inventory}, saddlebag={counts.Saddlebag}, premium={counts.PremiumSaddlebag}]");
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
            var total = 0;
            foreach (var spec in GetInventoryContainerSpecs())
            {
                var container = manager->GetInventoryContainer(spec.Type);
                if (container == null || !container->IsLoaded) continue;

                for (int i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot != null && slot->ItemId == itemId)
                        total += (int)slot->Quantity;
                }
            }

            return total;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to get map count for {itemId}: {ex.Message}");
            return 0;
        }
    }

    public unsafe bool TryMoveMapFromSaddlebagsToInventory(uint itemId, out string detail)
    {
        detail = "";

        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
            {
                detail = "InventoryManager is null";
                return false;
            }

            InventoryType sourceType = default;
            int sourceSlot = -1;
            foreach (var spec in GetSaddlebagContainerSpecs())
            {
                var container = manager->GetInventoryContainer(spec.Type);
                if (container == null || !container->IsLoaded) continue;

                for (int i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId != itemId) continue;

                    sourceType = spec.Type;
                    sourceSlot = i;
                    break;
                }

                if (sourceSlot >= 0)
                    break;
            }

            if (sourceSlot < 0)
            {
                detail = "Selected map was not found in loaded saddlebags. Open saddlebags once this session and retry.";
                return false;
            }

            InventoryType destinationType = default;
            int destinationSlot = -1;
            foreach (var spec in GetInventoryContainerSpecs())
            {
                var container = manager->GetInventoryContainer(spec.Type);
                if (container == null || !container->IsLoaded) continue;

                for (int i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId != 0) continue;

                    destinationType = spec.Type;
                    destinationSlot = i;
                    break;
                }

                if (destinationSlot >= 0)
                    break;
            }

            if (destinationSlot < 0)
            {
                detail = "No empty inventory slot is available for saddlebag retrieval.";
                return false;
            }

            manager->MoveItemSlot(sourceType, (ushort)sourceSlot, destinationType, (ushort)destinationSlot, true);
            detail = $"Moved map {itemId} from {sourceType}[{sourceSlot}] to {destinationType}[{destinationSlot}]";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            _log.Error($"Failed to move map {itemId} from saddlebags: {ex.Message}");
            return false;
        }
    }

    private static IReadOnlyList<ContainerSpec> GetContainerSpecs(bool includeSaddlebags)
    {
        return includeSaddlebags
            ? GetInventoryContainerSpecs().Concat(GetSaddlebagContainerSpecs()).ToList()
            : GetInventoryContainerSpecs();
    }

    private static IReadOnlyList<ContainerSpec> GetInventoryContainerSpecs()
    {
        return new[]
        {
            new ContainerSpec(InventoryType.Inventory1, (count, quantity) => count.Inventory += quantity),
            new ContainerSpec(InventoryType.Inventory2, (count, quantity) => count.Inventory += quantity),
            new ContainerSpec(InventoryType.Inventory3, (count, quantity) => count.Inventory += quantity),
            new ContainerSpec(InventoryType.Inventory4, (count, quantity) => count.Inventory += quantity),
        };
    }

    private static IReadOnlyList<ContainerSpec> GetSaddlebagContainerSpecs()
    {
        return new[]
        {
            new ContainerSpec(InventoryType.SaddleBag1, (count, quantity) => count.Saddlebag += quantity),
            new ContainerSpec(InventoryType.SaddleBag2, (count, quantity) => count.Saddlebag += quantity),
            new ContainerSpec(InventoryType.PremiumSaddleBag1, (count, quantity) => count.PremiumSaddlebag += quantity),
            new ContainerSpec(InventoryType.PremiumSaddleBag2, (count, quantity) => count.PremiumSaddlebag += quantity),
        };
    }
}
