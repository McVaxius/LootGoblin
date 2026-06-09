using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using LootGoblin.Models;
using Lumina.Excel.Sheets;

namespace LootGoblin.Services;

public class MapSourceCount
{
    public int Inventory { get; set; }
    public int Saddlebag { get; set; }
    public int PremiumSaddlebag { get; set; }
    public int Retainer { get; set; }
    public int Total => Inventory + Saddlebag + PremiumSaddlebag + Retainer;
    public int LoadedTotal => Inventory + Saddlebag + PremiumSaddlebag;
    public int SaddlebagTotal => Saddlebag + PremiumSaddlebag;
}

public sealed record SaddlebagMovePlan(
    uint ItemId,
    InventoryType SourceType,
    int SourceSlot,
    InventoryType DestinationType,
    int DestinationSlot,
    int SourceQuantity);

public sealed record RetainerMapMovePlan(
    uint ItemId,
    InventoryType SourceType,
    int SourceSlot,
    InventoryType DestinationType,
    int DestinationSlot,
    int SourceQuantity);

public sealed record TreasureMapKeyItem(
    uint ItemId,
    uint KnownMapItemId,
    int Slot,
    string Name,
    string Description)
{
    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name : $"Key item {ItemId}";
}

public class InventoryService : IDisposable
{
    private readonly IPluginLog _log;
    private readonly Plugin _plugin;
    private readonly IDataManager _dataManager;
    private Lumina.Excel.ExcelSheet<Item>? itemSheet;
    private Lumina.Excel.ExcelSheet<EventItem>? eventItemSheet;
    private Lumina.Excel.ExcelSheet<EventItemHelp>? eventItemHelpSheet;
    private static int scanCounter = 0; // Static counter for reducing log spam across all instances
    private const InventoryType KeyItemsInventoryType = (InventoryType)2004;
    private static readonly IReadOnlyList<ContainerSpec> InventoryContainerSpecs =
    [
        new ContainerSpec(InventoryType.Inventory1, (count, quantity) => count.Inventory += quantity),
        new ContainerSpec(InventoryType.Inventory2, (count, quantity) => count.Inventory += quantity),
        new ContainerSpec(InventoryType.Inventory3, (count, quantity) => count.Inventory += quantity),
        new ContainerSpec(InventoryType.Inventory4, (count, quantity) => count.Inventory += quantity),
    ];

    private static readonly IReadOnlyList<ContainerSpec> SaddlebagContainerSpecs =
    [
        new ContainerSpec(InventoryType.SaddleBag1, (count, quantity) => count.Saddlebag += quantity),
        new ContainerSpec(InventoryType.SaddleBag2, (count, quantity) => count.Saddlebag += quantity),
        new ContainerSpec(InventoryType.PremiumSaddleBag1, (count, quantity) => count.PremiumSaddlebag += quantity),
        new ContainerSpec(InventoryType.PremiumSaddleBag2, (count, quantity) => count.PremiumSaddlebag += quantity),
    ];

    private static readonly IReadOnlyList<ContainerSpec> InventoryAndSaddlebagContainerSpecs =
        InventoryContainerSpecs.Concat(SaddlebagContainerSpecs).ToArray();

    private static readonly IReadOnlyList<ContainerSpec> RetainerContainerSpecs =
    [
        new ContainerSpec(InventoryType.RetainerPage1, (count, quantity) => count.Retainer += quantity),
        new ContainerSpec(InventoryType.RetainerPage2, (count, quantity) => count.Retainer += quantity),
        new ContainerSpec(InventoryType.RetainerPage3, (count, quantity) => count.Retainer += quantity),
        new ContainerSpec(InventoryType.RetainerPage4, (count, quantity) => count.Retainer += quantity),
        new ContainerSpec(InventoryType.RetainerPage5, (count, quantity) => count.Retainer += quantity),
        new ContainerSpec(InventoryType.RetainerPage6, (count, quantity) => count.Retainer += quantity),
        new ContainerSpec(InventoryType.RetainerPage7, (count, quantity) => count.Retainer += quantity),
    ];
    private static readonly Dictionary<string, uint> KeyItemNameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "alexandrite", 7884 },
        { "alexandrite map", 7884 },
        { "leather buried", 8156 },
        { "leather buried treasure", 8156 },
        { "leather buried treasure map", 8156 },
		{ "fabled thief's", 19770 },
    };

    private static readonly Dictionary<uint, uint> KeyItemIdAliases = new()
    {
        { 2001223, 7884 }, // Alexandrite Map -> Mysterious Map
        { 2001352, 8156 }, // Leather Buried Treasure Map -> Unhidden Leather Map
    };

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

    public unsafe bool TryGetLowestEquippedGearConditionPercent(out int lowestConditionPercent)
    {
        lowestConditionPercent = 100;
        var foundEquippedItem = false;

        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;

        var equippedContainer = manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (equippedContainer == null || !equippedContainer->IsLoaded)
            return false;

        for (var i = 0; i < equippedContainer->Size; i++)
        {
            var slot = equippedContainer->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0)
                continue;

            foundEquippedItem = true;
            lowestConditionPercent = Math.Min(lowestConditionPercent, slot->Condition / 300);
        }

        return foundEquippedItem;
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

            var itemSheet = GetItemSheet();
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

                    var itemId = slot->ItemId;
                    var itemName = TreasureMapData.KnownMaps.ContainsKey(itemId)
                        ? string.Empty
                        : ResolveItemName(itemSheet, itemId);

                    if (IsTreasureMapSource(itemId, itemName))
                    {
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
                        var name = GetMapSourceDisplayName(kvp.Key, ResolveItemName(itemSheet, kvp.Key));
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
            Plugin.LogError($"Failed to scan inventory for maps: {ex.Message}");
            _plugin.AddDebugLog($"Inventory scan error: {ex.Message}");
        }

        return results;
    }

    public bool HasTreasureMapKeyItem()
        => TryFindTreasureMapKeyItem(out _);

    public unsafe bool TryFindTreasureMapKeyItem(out TreasureMapKeyItem keyItem)
    {
        keyItem = null!;

        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
                return false;

            var container = manager->GetInventoryContainer(KeyItemsInventoryType);
            if (container == null || !container->IsLoaded)
                return false;

            var itemSheet = GetItemSheet();
            var eventItemSheet = GetEventItemSheet();
            var eventItemHelpSheet = GetEventItemHelpSheet();

            for (int i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    continue;

                var itemId = slot->ItemId;
                var name = string.Empty;
                var description = string.Empty;

                ResolveKeyItemText(itemId, eventItemSheet, eventItemHelpSheet, itemSheet, out name, out description);

                if (!TryMatchTreasureMapKeyItem(itemId, name, description, out var knownMapItemId))
                    continue;

                keyItem = new TreasureMapKeyItem(itemId, knownMapItemId, i, name, description);
                return true;
            }
        }
        catch (Exception ex)
        {
            Plugin.LogError($"Failed to scan key items for treasure map: {ex.Message}");
            _plugin.AddDebugLog($"Key item scan error: {ex.Message}");
        }

        return false;
    }

    public unsafe int GetMapCount(uint itemId)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return 0;
            return GetMapCount(manager, itemId, GetInventoryContainerSpecs());
        }
        catch (Exception ex)
        {
            Plugin.LogError($"Failed to get map count for {itemId}: {ex.Message}");
            return 0;
        }
    }

    public unsafe int GetSaddlebagMapCount(uint itemId)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return 0;
            return GetMapCount(manager, itemId, GetSaddlebagContainerSpecs());
        }
        catch (Exception ex)
        {
            Plugin.LogError($"Failed to get saddlebag map count for {itemId}: {ex.Message}");
            return 0;
        }
    }

    public unsafe bool TryMoveMapFromSaddlebagsToInventory(uint itemId, out string detail)
    {
        detail = "";

        try
        {
            return TryPlanSaddlebagMapMove(itemId, out var plan, out detail) &&
                   TryMovePlannedSaddlebagMap(plan, out detail);
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            Plugin.LogError($"Failed to move map {itemId} from saddlebags: {ex.Message}");
            return false;
        }
    }

    public unsafe bool TryPlanSaddlebagMapMove(uint itemId, out SaddlebagMovePlan plan, out string detail)
    {
        plan = null!;
        detail = "";

        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
            {
                detail = "InventoryManager is null";
                return false;
            }

            if (!TryFindSaddlebagSource(manager, itemId, out var sourceType, out var sourceSlot, out var sourceQuantity))
            {
                detail = "Selected map was not found in loaded saddlebags. Open saddlebags once this session and retry.";
                return false;
            }

            if (!TryFindEmptyInventorySlot(manager, out var destinationType, out var destinationSlot))
            {
                detail = "No empty inventory slot is available for saddlebag retrieval.";
                return false;
            }

            plan = new SaddlebagMovePlan(itemId, sourceType, sourceSlot, destinationType, destinationSlot, sourceQuantity);
            detail = $"Selected {sourceType}[{sourceSlot}] -> {destinationType}[{destinationSlot}]";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            Plugin.LogError($"Failed to plan map {itemId} saddlebag move: {ex.Message}");
            return false;
        }
    }

    public unsafe bool TryMovePlannedSaddlebagMap(SaddlebagMovePlan plan, out string detail)
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

            var sourceContainer = manager->GetInventoryContainer(plan.SourceType);
            if (sourceContainer == null || !sourceContainer->IsLoaded)
            {
                detail = $"Source container {plan.SourceType} is not loaded.";
                return false;
            }

            var destinationContainer = manager->GetInventoryContainer(plan.DestinationType);
            if (destinationContainer == null || !destinationContainer->IsLoaded)
            {
                detail = $"Destination container {plan.DestinationType} is not loaded.";
                return false;
            }

            if (plan.SourceSlot < 0 || plan.SourceSlot >= sourceContainer->Size)
            {
                detail = $"Source slot {plan.SourceSlot} is out of range for {plan.SourceType}.";
                return false;
            }

            if (plan.DestinationSlot < 0 || plan.DestinationSlot >= destinationContainer->Size)
            {
                detail = $"Destination slot {plan.DestinationSlot} is out of range for {plan.DestinationType}.";
                return false;
            }

            var sourceSlot = sourceContainer->GetInventorySlot(plan.SourceSlot);
            if (sourceSlot == null || sourceSlot->ItemId != plan.ItemId)
            {
                detail = $"Source slot changed before move. Expected {plan.ItemId} in {plan.SourceType}[{plan.SourceSlot}].";
                return false;
            }

            var destinationSlot = destinationContainer->GetInventorySlot(plan.DestinationSlot);
            if (destinationSlot == null || destinationSlot->ItemId != 0)
            {
                detail = $"Destination slot changed before move. {plan.DestinationType}[{plan.DestinationSlot}] is no longer empty.";
                return false;
            }

            manager->MoveItemSlot(plan.SourceType, (ushort)plan.SourceSlot, plan.DestinationType, (ushort)plan.DestinationSlot, true);
            detail = $"Moved map {plan.ItemId} from {plan.SourceType}[{plan.SourceSlot}] to {plan.DestinationType}[{plan.DestinationSlot}]";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            Plugin.LogError($"Failed to execute map {plan.ItemId} saddlebag move: {ex.Message}");
            return false;
        }
    }

    public unsafe bool TryMoveMapFromRetainerToInventory(uint itemId, out string detail)
    {
        detail = "";

        try
        {
            return TryPlanRetainerMapMove(itemId, out var plan, out detail) &&
                   TryMovePlannedRetainerMap(plan, out detail);
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            Plugin.LogError($"Failed to move map {itemId} from retainer: {ex.Message}");
            return false;
        }
    }

    public unsafe bool TryPlanRetainerMapMove(uint itemId, out RetainerMapMovePlan plan, out string detail)
    {
        plan = null!;
        detail = "";

        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
            {
                detail = "InventoryManager is null";
                return false;
            }

            var sourceFound = TryScanRetainerSource(
                manager,
                itemId,
                out var sourceType,
                out var sourceSlot,
                out var sourceQuantity,
                out var loadedPages,
                out var unloadedPages,
                out var scannedSlots,
                out var matchedQuantity);

            _plugin.AddDebugLog(
                $"[RetainerMap] Retainer inventory scan for item {itemId}: loaded={loadedPages}; " +
                $"unloaded={unloadedPages}; scannedSlots={scannedSlots}; matchedQuantity={matchedQuantity}.");

            if (unloadedPages != "none")
            {
                detail = $"Retainer inventory containers are not loaded: {unloadedPages}.";
                return false;
            }

            if (!sourceFound)
            {
                detail = $"Selected map {itemId} was not found in loaded retainer inventory. " +
                         $"Scanned {loadedPages} ({scannedSlots} slots, matched quantity {matchedQuantity}).";
                return false;
            }

            if (!TryFindEmptyInventorySlot(manager, out var destinationType, out var destinationSlot))
            {
                detail = "No empty player inventory slot is available for retainer retrieval.";
                return false;
            }

            plan = new RetainerMapMovePlan(itemId, sourceType, sourceSlot, destinationType, destinationSlot, sourceQuantity);
            detail = $"Selected retainer map move {sourceType}[{sourceSlot}] -> {destinationType}[{destinationSlot}]";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            Plugin.LogError($"Failed to plan map {itemId} retainer move: {ex.Message}");
            return false;
        }
    }

    public unsafe bool TryMovePlannedRetainerMap(RetainerMapMovePlan plan, out string detail)
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

            var sourceContainer = manager->GetInventoryContainer(plan.SourceType);
            if (sourceContainer == null || !sourceContainer->IsLoaded)
            {
                detail = $"Source retainer container {plan.SourceType} is not loaded.";
                return false;
            }

            var destinationContainer = manager->GetInventoryContainer(plan.DestinationType);
            if (destinationContainer == null || !destinationContainer->IsLoaded)
            {
                detail = $"Destination container {plan.DestinationType} is not loaded.";
                return false;
            }

            if (plan.SourceSlot < 0 || plan.SourceSlot >= sourceContainer->Size)
            {
                detail = $"Source slot {plan.SourceSlot} is out of range for {plan.SourceType}.";
                return false;
            }

            if (plan.DestinationSlot < 0 || plan.DestinationSlot >= destinationContainer->Size)
            {
                detail = $"Destination slot {plan.DestinationSlot} is out of range for {plan.DestinationType}.";
                return false;
            }

            var sourceSlot = sourceContainer->GetInventorySlot(plan.SourceSlot);
            if (sourceSlot == null || sourceSlot->ItemId != plan.ItemId)
            {
                detail = $"Source slot changed before move. Expected {plan.ItemId} in {plan.SourceType}[{plan.SourceSlot}].";
                return false;
            }

            var destinationSlot = destinationContainer->GetInventorySlot(plan.DestinationSlot);
            if (destinationSlot == null || destinationSlot->ItemId != 0)
            {
                detail = $"Destination slot changed before move. {plan.DestinationType}[{plan.DestinationSlot}] is no longer empty.";
                return false;
            }

            manager->MoveItemSlot(plan.SourceType, (ushort)plan.SourceSlot, plan.DestinationType, (ushort)plan.DestinationSlot, true);
            detail = $"Moved retainer map {plan.ItemId} from {plan.SourceType}[{plan.SourceSlot}] to {plan.DestinationType}[{plan.DestinationSlot}]";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            Plugin.LogError($"Failed to execute map {plan.ItemId} retainer move: {ex.Message}");
            return false;
        }
    }

    private static unsafe int GetMapCount(
        InventoryManager* manager,
        uint itemId,
        IReadOnlyList<ContainerSpec> containerSpecs)
    {
        var total = 0;
        foreach (var spec in containerSpecs)
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

    private static unsafe bool TryFindSaddlebagSource(
        InventoryManager* manager,
        uint itemId,
        out InventoryType sourceType,
        out int sourceSlot,
        out int sourceQuantity)
    {
        sourceType = default;
        sourceSlot = -1;
        sourceQuantity = 0;

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
                sourceQuantity = (int)slot->Quantity;
                return true;
            }
        }

        return false;
    }

    private static unsafe bool TryScanRetainerSource(
        InventoryManager* manager,
        uint itemId,
        out InventoryType sourceType,
        out int sourceSlot,
        out int sourceQuantity,
        out string loadedPages,
        out string unloadedPages,
        out int scannedSlots,
        out int matchedQuantity)
    {
        sourceType = default;
        sourceSlot = -1;
        sourceQuantity = 0;
        scannedSlots = 0;
        matchedQuantity = 0;
        var loaded = new List<string>();
        var unloaded = new List<string>();

        foreach (var spec in GetRetainerContainerSpecs())
        {
            var container = manager->GetInventoryContainer(spec.Type);
            if (container == null || !container->IsLoaded)
            {
                unloaded.Add(spec.Type.ToString());
                continue;
            }

            loaded.Add($"{spec.Type}[{container->Size}]");
            scannedSlots += container->Size;

            for (int i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != itemId) continue;

                matchedQuantity += (int)slot->Quantity;
                if (sourceSlot >= 0) continue;

                sourceType = spec.Type;
                sourceSlot = i;
                sourceQuantity = (int)slot->Quantity;
            }
        }

        loadedPages = loaded.Count == 0 ? "none" : string.Join(", ", loaded);
        unloadedPages = unloaded.Count == 0 ? "none" : string.Join(", ", unloaded);
        return sourceSlot >= 0;
    }

    private static unsafe bool TryFindEmptyInventorySlot(
        InventoryManager* manager,
        out InventoryType destinationType,
        out int destinationSlot)
    {
        destinationType = default;
        destinationSlot = -1;

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
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ContainerSpec> GetContainerSpecs(bool includeSaddlebags)
    {
        return includeSaddlebags
            ? InventoryAndSaddlebagContainerSpecs
            : InventoryContainerSpecs;
    }

    private static IReadOnlyList<ContainerSpec> GetInventoryContainerSpecs()
        => InventoryContainerSpecs;

    private static IReadOnlyList<ContainerSpec> GetSaddlebagContainerSpecs()
        => SaddlebagContainerSpecs;

    private static IReadOnlyList<ContainerSpec> GetRetainerContainerSpecs()
        => RetainerContainerSpecs;

    private Lumina.Excel.ExcelSheet<Item>? GetItemSheet()
        => itemSheet ??= _dataManager.GetExcelSheet<Item>();

    private Lumina.Excel.ExcelSheet<EventItem>? GetEventItemSheet()
        => eventItemSheet ??= _dataManager.GetExcelSheet<EventItem>();

    private Lumina.Excel.ExcelSheet<EventItemHelp>? GetEventItemHelpSheet()
        => eventItemHelpSheet ??= _dataManager.GetExcelSheet<EventItemHelp>();

    private static string ResolveItemName(Lumina.Excel.ExcelSheet<Item>? sheet, uint itemId)
    {
        try
        {
            return sheet != null && sheet.TryGetRow(itemId, out var item)
                ? item.Name.ToString()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsTreasureMapSource(uint itemId, string itemName)
    {
        if (TreasureMapData.KnownMaps.ContainsKey(itemId))
            return true;

        return !string.IsNullOrWhiteSpace(itemName) &&
               ((itemName.Contains("Timeworn", StringComparison.OrdinalIgnoreCase) &&
                 itemName.Contains("Map", StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(itemName, "Mysterious Map", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetMapSourceDisplayName(uint itemId, string itemName)
    {
        if (!string.IsNullOrWhiteSpace(itemName))
            return itemName;

        return TreasureMapData.KnownMaps.TryGetValue(itemId, out var mapInfo) &&
               !string.IsNullOrWhiteSpace(mapInfo.Name)
            ? mapInfo.Name
            : "Unknown";
    }

    private static bool TryMatchTreasureMapKeyItem(
        uint itemId,
        string name,
        string description,
        out uint knownMapItemId)
    {
        if (KeyItemIdAliases.TryGetValue(itemId, out knownMapItemId))
            return true;

        if (TreasureMapData.KnownMaps.ContainsKey(itemId))
        {
            knownMapItemId = itemId;
            return true;
        }

        var normalizedName = NormalizeTreasureMapText(name);
        var normalizedDescription = NormalizeTreasureMapText(description);

        if (TryMatchKeyItemAlias(normalizedName, normalizedDescription, out knownMapItemId))
            return true;

        foreach (var map in TreasureMapData.KnownMaps.Values)
        {
            if (ContainsIgnoreCase(name, map.Name) || ContainsIgnoreCase(description, map.Name))
            {
                knownMapItemId = map.ItemId;
                return true;
            }

            var keyItemStem = NormalizeTreasureMapText(map.Name);
            if ((ContainsIgnoreCase(name, "Treasure Map") || ContainsIgnoreCase(description, "Treasure Map")) &&
                (ContainsNormalizedMapStem(normalizedName, keyItemStem) ||
                 ContainsNormalizedMapStem(normalizedDescription, keyItemStem)))
            {
                knownMapItemId = map.ItemId;
                return true;
            }
        }

        if (ContainsIgnoreCase(name, "Treasure Map") ||
            ContainsIgnoreCase(description, "Treasure Map"))
        {
            knownMapItemId = 0;
            return true;
        }

        knownMapItemId = 0;
        return false;
    }

    private static void ResolveKeyItemText(
        uint itemId,
        Lumina.Excel.ExcelSheet<EventItem>? eventItemSheet,
        Lumina.Excel.ExcelSheet<EventItemHelp>? eventItemHelpSheet,
        Lumina.Excel.ExcelSheet<Item>? itemSheet,
        out string name,
        out string description)
    {
        name = string.Empty;
        description = string.Empty;

        try
        {
            var eventItem = eventItemSheet?.GetRow(itemId);
            if (eventItem != null)
            {
                name = FirstNonEmpty(
                    eventItem.Value.Name.ToString(),
                    eventItem.Value.Singular.ToString(),
                    eventItem.Value.Plural.ToString());
            }
        }
        catch
        {
            // EventItem may not contain every key-item-like row; Item fallback below still applies.
        }

        try
        {
            var eventItemHelp = eventItemHelpSheet?.GetRow(itemId);
            if (eventItemHelp != null)
                description = eventItemHelp.Value.Description.ToString();
        }
        catch
        {
            // EventItemHelp can be absent for rows with no help text.
        }

        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(description))
            return;

        try
        {
            var item = itemSheet?.GetRow(itemId);
            if (item != null)
            {
                if (string.IsNullOrWhiteSpace(name))
                    name = item.Value.Name.ToString();
                if (string.IsNullOrWhiteSpace(description))
                    description = item.Value.Description.ToString();
            }
        }
        catch
        {
            // Some key-item rows do not resolve through Item.
        }
    }

    private static bool TryMatchKeyItemAlias(string normalizedName, string normalizedDescription, out uint knownMapItemId)
    {
        foreach (var alias in KeyItemNameAliases)
        {
            var normalizedAlias = NormalizeTreasureMapText(alias.Key);
            if (ContainsNormalizedMapStem(normalizedName, normalizedAlias) ||
                ContainsNormalizedMapStem(normalizedDescription, normalizedAlias))
            {
                knownMapItemId = alias.Value;
                return true;
            }
        }

        knownMapItemId = 0;
        return false;
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static bool ContainsIgnoreCase(string haystack, string needle)
        => !string.IsNullOrWhiteSpace(haystack) &&
           !string.IsNullOrWhiteSpace(needle) &&
           haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsNormalizedMapStem(string haystack, string needle)
        => !string.IsNullOrWhiteSpace(haystack) &&
           !string.IsNullOrWhiteSpace(needle) &&
           haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTreasureMapText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                builder.Append(char.ToLowerInvariant(c));
            else
                builder.Append(' ');
        }

        var tokens = builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token =>
                !string.Equals(token, "timeworn", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(token, "treasure", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(token, "map", StringComparison.OrdinalIgnoreCase));

        return string.Join(' ', tokens);
    }
}
