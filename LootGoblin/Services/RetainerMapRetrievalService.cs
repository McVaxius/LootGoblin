using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LootGoblin.Models;
using Lumina.Excel.Sheets;

namespace LootGoblin.Services;

public enum RetainerMapRetrievalResult
{
    Idle,
    Running,
    Retrieved,
    NotAvailable,
    Error,
}

public sealed class RetainerMapRetrievalService : IDisposable
{
    private enum RetrievalStep
    {
        Idle,
        MovingToBell,
        TravelingToBell,
        InteractingBell,
        SelectingRetainer,
        OpeningRetainerInventory,
        RetrievingMap,
        ClosingRetainer,
        Complete,
        Error,
    }

    private sealed record RetainerMapCandidate(uint ItemId, string ItemName, string RetainerName, int RetainerIndex, int Quantity);
    private sealed record RetainerListEntry(int Index, string Name);
    private sealed record XaItemRow(
        string Character,
        string World,
        string ContainerName,
        string ItemName,
        uint ItemId,
        int Quantity,
        bool IsHq);

    private const uint RevenantsTollTerritoryId = 156;
    private static readonly Vector3 RevenantsTollBellApproachPosition = new(12.188f, 29.000f, -735.430f);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongStepTimeout = TimeSpan.FromSeconds(90);

    private readonly Plugin _plugin;
    private readonly IPluginLog _log;

    private RetrievalStep step = RetrievalStep.Idle;
    private DateTime stepStartedAt = DateTime.MinValue;
    private DateTime nextActionAt = DateTime.MinValue;
    private RetainerMapCandidate? target;
    private bool autoRetainerSuppressed;
    private bool bellInteracted;
    private bool retainerSelected;
    private bool inventoryOpened;
    private bool retainerMoveIssued;
    private bool closeIssued;

    public string StatusText { get; private set; } = "Idle.";
    public string LastError { get; private set; } = string.Empty;
    public uint TargetItemId => target?.ItemId ?? 0;
    public string TargetMapName => target?.ItemName ?? string.Empty;
    public string TargetRetainerName => target?.RetainerName ?? string.Empty;
    public bool IsRunning => step != RetrievalStep.Idle && step != RetrievalStep.Complete && step != RetrievalStep.Error;

    public RetainerMapRetrievalService(Plugin plugin, IPluginLog log)
    {
        _plugin = plugin;
        _log = log;
    }

    public void Dispose()
    {
        UnsuppressAutoRetainer();
    }

    public void Reset()
    {
        UnsuppressAutoRetainer();
        step = RetrievalStep.Idle;
        target = null;
        StatusText = "Idle.";
        LastError = string.Empty;
        ResetStepFlags();
    }

    public RetainerMapRetrievalResult StartOrTick(IReadOnlyCollection<uint> enabledMapIds)
    {
        if (step == RetrievalStep.Complete)
        {
            Reset();
            return RetainerMapRetrievalResult.Retrieved;
        }

        if (step == RetrievalStep.Error)
            return RetainerMapRetrievalResult.Error;

        if (step == RetrievalStep.Idle)
        {
            var candidate = FindRetainerMapCandidate(enabledMapIds);
            if (candidate == null)
                return string.IsNullOrEmpty(LastError) ? RetainerMapRetrievalResult.NotAvailable : RetainerMapRetrievalResult.Error;

            target = candidate;
            _plugin.AddDebugLog($"[RetainerMap] Found {candidate.ItemName} on retainer {candidate.RetainerName} via XA Database.");
            SuppressAutoRetainer();

            if (TryFindNearestBell(out _))
                EnterStep(RetrievalStep.MovingToBell, $"Moving to retainer bell for {candidate.ItemName}...");
            else
                EnterStep(RetrievalStep.TravelingToBell, "No nearby retainer bell found. Traveling to Revenant's Toll...");
        }

        TickActiveStep();
        return step == RetrievalStep.Complete ? RetainerMapRetrievalResult.Retrieved : RetainerMapRetrievalResult.Running;
    }

    public bool StartManualFetch()
    {
        Reset();
        var enabled = GetConfiguredMapIds();
        var result = StartOrTick(enabled);
        return result == RetainerMapRetrievalResult.Running || result == RetainerMapRetrievalResult.Retrieved;
    }

    public bool HasRetainerMapCandidate(IReadOnlyCollection<uint> enabledMapIds)
    {
        var previousStatus = StatusText;
        var previousError = LastError;
        var candidate = FindRetainerMapCandidate(enabledMapIds);
        StatusText = previousStatus;
        LastError = previousError;
        return candidate != null;
    }

    public Dictionary<uint, int> GetRetainerMapCounts(IReadOnlyCollection<uint> mapIds, bool refreshFirst)
    {
        var counts = new Dictionary<uint, int>();
        LastError = string.Empty;

        if (!IsXaDatabaseReady())
            return counts;

        if (refreshFirst)
            TryRefreshXaDatabase();

        var allowed = mapIds.Count > 0 ? mapIds.ToHashSet() : GetConfiguredMapIds().ToHashSet();
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (itemSheet == null)
        {
            LastError = "Item sheet unavailable.";
            StatusText = LastError;
            return counts;
        }

        var totalRows = 0;
        var currentCharacterRows = 0;
        var retainerRows = 0;

        foreach (var itemId in allowed)
        {
            var item = itemSheet.GetRow(itemId);
            var itemName = item.Name.ToString();
            if (string.IsNullOrWhiteSpace(itemName))
                continue;

            foreach (var response in SearchXaDatabase(itemName))
            {
                foreach (var row in ParseXaSearchRows(response))
                {
                    totalRows++;
                    if (!IsCurrentCharacterRow(row))
                    {
                        _plugin.AddDebugLog($"[RetainerMap] Rejected XA row for other character/world: {row.Character}@{row.World} {row.ItemName} {row.ContainerName}");
                        continue;
                    }

                    currentCharacterRows++;
                    if (!IsRetainerContainerName(row.ContainerName))
                    {
                        _plugin.AddDebugLog($"[RetainerMap] Rejected XA row with non-retainer container: {row.ContainerName} ({row.ItemName}).");
                        continue;
                    }

                    retainerRows++;
                    if (row.ItemId != itemId || row.Quantity <= 0)
                        continue;

                    counts[itemId] = counts.TryGetValue(itemId, out var existing)
                        ? existing + row.Quantity
                        : row.Quantity;
                }
            }
        }

        _plugin.AddDebugLog(
            $"[RetainerMap] XA count refresh: rows={totalRows}, current-character={currentCharacterRows}, retainer={retainerRows}, map-types={counts.Count}.");
        if (totalRows > 0 && currentCharacterRows == 0)
            LastError = "XADB returned rows, but none matched current character/world.";
        else if (currentCharacterRows > 0 && retainerRows == 0)
            LastError = "XADB returned current-character rows, but none were retainer containers.";

        return counts;
    }

    private void TickActiveStep()
    {
        if (DateTime.Now < nextActionAt)
            return;

        if (step != RetrievalStep.TravelingToBell && DateTime.Now - stepStartedAt > StepTimeout)
        {
            Fail($"Timed out during {step}.");
            return;
        }

        if (step == RetrievalStep.TravelingToBell && DateTime.Now - stepStartedAt > LongStepTimeout)
        {
            Fail("Timed out traveling to Revenant's Toll retainer bell.");
            return;
        }

        switch (step)
        {
            case RetrievalStep.TravelingToBell:
                TickTravelingToBell();
                break;
            case RetrievalStep.MovingToBell:
                TickMovingToBell();
                break;
            case RetrievalStep.InteractingBell:
                TickInteractingBell();
                break;
            case RetrievalStep.SelectingRetainer:
                TickSelectingRetainer();
                break;
            case RetrievalStep.OpeningRetainerInventory:
                TickOpeningRetainerInventory();
                break;
            case RetrievalStep.RetrievingMap:
                TickRetrievingMap();
                break;
            case RetrievalStep.ClosingRetainer:
                TickClosingRetainer();
                break;
        }
    }

    private void TickTravelingToBell()
    {
        if (IsLoading())
        {
            StatusText = "Traveling to retainer bell...";
            nextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (!bellInteracted)
        {
            bellInteracted = true;
            CommandHelper.SendCommand("/li Revenant's Toll");
            nextActionAt = DateTime.Now.AddSeconds(5);
            return;
        }

        if (Plugin.ClientState.TerritoryType != RevenantsTollTerritoryId)
        {
            StatusText = "Waiting for Revenant's Toll arrival...";
            nextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            StatusText = "Waiting for player before moving to Revenant's Toll bell approach...";
            nextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (!IsNearBellApproach(player.Position))
        {
            EnterStep(RetrievalStep.MovingToBell, "Moving to Revenant's Toll retainer bell approach...");
            return;
        }

        if (!TryFindNearestBell(out _))
        {
            StatusText = "Arrived. Waiting for retainer bell object...";
            nextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        EnterStep(RetrievalStep.MovingToBell, "Moving to Revenant's Toll retainer bell...");
    }

    private void TickMovingToBell()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            StatusText = "Waiting for player before moving to retainer bell...";
            nextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (Plugin.ClientState.TerritoryType != RevenantsTollTerritoryId)
        {
            EnterStep(RetrievalStep.TravelingToBell, "Traveling to Revenant's Toll retainer bell...");
            return;
        }

        var approachDistance = Vector3.Distance(player.Position, RevenantsTollBellApproachPosition);
        if (approachDistance > 3f)
        {
            _plugin.NavigationService.MoveToPosition(RevenantsTollBellApproachPosition);
            StatusText = $"Moving to Revenant's Toll bell approach... ({approachDistance:F1}y)";
            nextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (!TryFindNearestBell(out var bell))
        {
            StatusText = "At bell approach. Waiting for Summoning Bell object...";
            nextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        var distance = Vector3.Distance(player.Position, bell.Position);
        if (distance > 4f)
        {
            _plugin.NavigationService.MoveToPosition(bell.Position);
            StatusText = $"Moving to retainer bell... ({distance:F1}y)";
            nextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        _plugin.NavigationService.StopNavigation();
        EnterStep(RetrievalStep.InteractingBell, "Opening retainer bell...");
    }

    private void TickInteractingBell()
    {
        if (GameHelpers.IsAddonVisible("RetainerList") || GameHelpers.IsAddonVisible("SelectString"))
        {
            EnterStep(RetrievalStep.SelectingRetainer, "Selecting retainer...");
            return;
        }

        if (!bellInteracted)
        {
            if (!TryFindNearestBell(out var bell))
            {
                Fail("Retainer bell disappeared before interaction.");
                return;
            }

            Plugin.TargetManager.Target = bell;
            GameHelpers.InteractWithObject(bell);
            bellInteracted = true;
            nextActionAt = DateTime.Now.AddSeconds(2);
            return;
        }

        StatusText = "Waiting for retainer list...";
        nextActionAt = DateTime.Now.AddSeconds(1);
    }

    private void TickSelectingRetainer()
    {
        if (GameHelpers.IsAddonVisible("SelectString"))
        {
            var selectIndex = FindSelectStringIndex(target?.RetainerName ?? string.Empty);
            if (selectIndex >= 0)
            {
                GameHelpers.FireAddonCallback("SelectString", true, selectIndex);
                EnterStep(RetrievalStep.OpeningRetainerInventory, "Waiting for retainer menu...");
                return;
            }
        }

        if (GameHelpers.IsAddonVisible("RetainerList") && !retainerSelected)
        {
            var targetName = target?.RetainerName ?? string.Empty;
            if (!TryFindRetainerListIndex(targetName, out var index, out var visibleNames))
            {
                var visible = visibleNames.Count == 0 ? "none parsed" : string.Join(", ", visibleNames);
                Fail($"Retainer list is visible but target retainer '{targetName}' was not found by confirmed name. Visible retainers/text: {visible}.");
                return;
            }

            _plugin.AddDebugLog($"[RetainerMap] RetainerList target '{targetName}' matched row {index}.");
            GameHelpers.FireAddonCallback("RetainerList", true, 2, index, 0, 0);
            retainerSelected = true;
            nextActionAt = DateTime.Now.AddSeconds(2);
            return;
        }

        if (GameHelpers.IsAddonVisible("SelectString") && retainerSelected)
        {
            EnterStep(RetrievalStep.OpeningRetainerInventory, "Opening retainer inventory...");
            return;
        }

        StatusText = $"Waiting to select retainer {target?.RetainerName ?? "unknown"}...";
        nextActionAt = DateTime.Now.AddSeconds(1);
    }

    private void TickOpeningRetainerInventory()
    {
        if (TryGetActiveRetainerInventoryAddonName(out var addonName))
        {
            _plugin.AddDebugLog($"[RetainerMap] {addonName} detected as active retainer inventory.");
            EnterStep(RetrievalStep.RetrievingMap, $"Retrieving {target?.ItemName}...");
            return;
        }

        if (GameHelpers.IsAddonVisible("SelectString") && !inventoryOpened)
        {
            var index = FindSelectStringIndex("Entrust", "withdraw", "Withdraw");
            if (index < 0)
                index = 0;

            GameHelpers.FireAddonCallback("SelectString", true, index);
            inventoryOpened = true;
            nextActionAt = DateTime.Now.AddSeconds(2);
            return;
        }

        StatusText = "Waiting for retainer inventory menu...";
        nextActionAt = DateTime.Now.AddSeconds(1);
    }

    private void TickRetrievingMap()
    {
        if (target == null)
        {
            Fail("No retainer map target.");
            return;
        }

        if (_plugin.InventoryService.GetMapCount(target.ItemId) > 0)
        {
            EnterStep(RetrievalStep.ClosingRetainer, $"Retrieved {target.ItemName}. Closing retainer...");
            return;
        }

        if (GameHelpers.IsAddonVisible("SelectYesno"))
        {
            GameHelpers.ClickYesIfVisible();
            nextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (!TryGetActiveRetainerInventoryAddonName(out var addonName))
        {
            StatusText = "Waiting for active retainer inventory...";
            nextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (!retainerMoveIssued)
        {
            if (!_plugin.InventoryService.TryPlanRetainerMapMove(target.ItemId, out var plan, out var planDetail))
            {
                Fail($"Could not plan retainer map retrieval from {addonName}: {planDetail}");
                return;
            }

            _plugin.AddDebugLog($"[RetainerMap] {planDetail}.");
            if (!_plugin.InventoryService.TryMovePlannedRetainerMap(plan, out var moveDetail))
            {
                Fail($"Could not move retainer map from {addonName}: {moveDetail}");
                return;
            }

            _plugin.AddDebugLog($"[RetainerMap] {moveDetail}.");
            retainerMoveIssued = true;
            nextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        StatusText = $"Waiting for {target.ItemName} retrieval...";
        nextActionAt = DateTime.Now.AddSeconds(1);
    }

    private void TickClosingRetainer()
    {
        if (!closeIssued)
        {
            GameHelpers.CloseCurrentAddon();
            closeIssued = true;
            nextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (!IsRetainerInventoryVisible() && !GameHelpers.IsAddonVisible("SelectString") && !GameHelpers.IsAddonVisible("RetainerList"))
        {
            UnsuppressAutoRetainer();
            EnterStep(RetrievalStep.Complete, "Retainer map retrieved.");
            return;
        }

        GameHelpers.CloseCurrentAddon();
        nextActionAt = DateTime.Now.AddSeconds(1);
    }

    private RetainerMapCandidate? FindRetainerMapCandidate(IReadOnlyCollection<uint> enabledMapIds)
    {
        LastError = string.Empty;

        if (!_plugin.Configuration.EnableRetainerMapRetrieval)
        {
            StatusText = "Retainer map retrieval disabled.";
            return null;
        }

        if (!IsXaDatabaseReady())
        {
            LastError = "XA Database is not ready.";
            StatusText = LastError;
            return null;
        }

        var allowed = enabledMapIds.Count > 0 ? enabledMapIds.ToHashSet() : GetConfiguredMapIds().ToHashSet();
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (itemSheet == null)
        {
            LastError = "Item sheet unavailable.";
            StatusText = LastError;
            return null;
        }

        foreach (var itemId in allowed)
        {
            var item = itemSheet.GetRow(itemId);
            var itemName = item.Name.ToString();
            if (string.IsNullOrWhiteSpace(itemName))
                continue;

            foreach (var response in SearchXaDatabase(itemName))
            {
                var candidate = TryParseCandidate(response, itemId, itemName);
                if (candidate != null)
                    return candidate;
            }
        }

        StatusText = "No enabled maps found on retainers in XA Database.";
        return null;
    }

    private IReadOnlyCollection<uint> GetConfiguredMapIds()
    {
        if (_plugin.Configuration.EnabledMapTypes.Count > 0)
            return _plugin.Configuration.EnabledMapTypes;

        return TreasureMapData.KnownMaps.Keys.ToList();
    }

    private bool IsXaDatabaseReady()
    {
        try
        {
            var subscriber = Plugin.PluginInterface.GetIpcSubscriber<bool>("XA.Database.IsReady");
            return subscriber.InvokeFunc();
        }
        catch (Exception ex)
        {
            LastError = $"XA Database readiness IPC failed: {ex.Message}";
            _log.Warning(LastError);
            return false;
        }
    }

    private IEnumerable<string> SearchXaDatabase(string itemName)
    {
        var results = new List<string>();

        try
        {
            var subscriber = Plugin.PluginInterface.GetIpcSubscriber<string, string>("XA.Database.SearchItems");
            var response = subscriber.InvokeFunc(itemName);
            if (!string.IsNullOrWhiteSpace(response))
                results.Add(response);
        }
        catch (Exception ex)
        {
            LastError = $"XA Database SearchItems IPC failed for {itemName}: {ex.Message}";
            _log.Warning(LastError);
        }

        return results;
    }

    private bool TryRefreshXaDatabase()
    {
        try
        {
            var subscriber = Plugin.PluginInterface.GetIpcSubscriber<object>("XA.Database.Refresh");
            subscriber.InvokeAction();
            _plugin.AddDebugLog("[RetainerMap] Requested XA Database refresh before retainer count scan.");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"XA Database Refresh IPC failed: {ex.Message}";
            _log.Warning(LastError);
            return false;
        }
    }

    private RetainerMapCandidate? TryParseCandidate(string response, uint itemId, string itemName)
    {
        var sawRows = false;
        var sawCurrentCharacterRows = false;
        var sawRetainerRows = false;

        foreach (var row in ParseXaSearchRows(response))
        {
            sawRows = true;
            if (!IsCurrentCharacterRow(row))
                continue;

            sawCurrentCharacterRows = true;
            if (!IsRetainerContainerName(row.ContainerName))
                continue;

            sawRetainerRows = true;
            if (row.ItemId != itemId || row.Quantity <= 0)
                continue;

            var retainerName = ExtractRetainerName(row.ContainerName);
            _plugin.AddDebugLog(
                $"[RetainerMap] XA row matched: {row.ItemName} x{row.Quantity} on {retainerName} ({row.ContainerName}).");
            return new RetainerMapCandidate(itemId, itemName, retainerName, -1, row.Quantity);
        }

        if (!sawRows)
            LastError = $"XA Database SearchItems response for {itemName} had no parseable pipe rows.";
        else if (!sawCurrentCharacterRows)
            LastError = $"XA Database SearchItems found {itemName}, but not for current character/world.";
        else if (!sawRetainerRows)
            LastError = $"XA Database SearchItems found {itemName}, but not in retainer containers.";

        return null;
    }

    private IEnumerable<XaItemRow> ParseXaSearchRows(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            yield break;

        foreach (var rawLine in response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var parts = line.Split('|');
            if (parts.Length < 7)
            {
                _plugin.AddDebugLog($"[RetainerMap] Skipping XA row with {parts.Length} columns: {line}");
                continue;
            }

            if (!uint.TryParse(parts[4].Trim(), out var parsedItemId))
            {
                if (!parts[4].Trim().Equals("ItemId", StringComparison.OrdinalIgnoreCase))
                    _plugin.AddDebugLog($"[RetainerMap] Skipping XA row with invalid ItemId: {line}");
                continue;
            }

            var quantity = 0;
            if (!int.TryParse(parts[5].Trim(), out quantity))
                quantity = 0;

            var isHq = bool.TryParse(parts[6].Trim(), out var parsedIsHq) && parsedIsHq;
            yield return new XaItemRow(
                parts[0].Trim(),
                parts[1].Trim(),
                parts[2].Trim(),
                parts[3].Trim(),
                parsedItemId,
                quantity,
                isHq);
        }
    }

    private bool IsCurrentCharacterRow(XaItemRow row)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var currentCharacter = player?.Name.TextValue ?? string.Empty;
        var currentWorld = player?.HomeWorld.Value.Name.ToString() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(currentCharacter) &&
            !string.Equals(row.Character, currentCharacter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(currentWorld) &&
            !string.Equals(row.World, currentWorld, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool IsRetainerContainerName(string containerName)
    {
        return !string.IsNullOrWhiteSpace(containerName) &&
               containerName.Contains("retainer", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractRetainerName(string containerName)
    {
        var cleaned = containerName.Trim();
        foreach (var prefix in new[] { "Retainer:", "Retainer -", "Retainer", "Retainer Inventory:" })
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return cleaned[prefix.Length..].Trim(' ', '-', ':');
        }

        return cleaned;
    }

    private static IEnumerable<JsonElement> EnumerateObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            foreach (var property in element.EnumerateObject())
            {
                foreach (var child in EnumerateObjects(property.Value))
                    yield return child;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var child in EnumerateObjects(item))
                    yield return child;
            }
        }
    }

    private bool IsCurrentCharacterResult(JsonElement element)
    {
        var currentContentId = 0UL;
        var resultContentId = GetUlong(element, "CharacterContentId", "OwnerContentId", "ContentId", "CID");
        if (resultContentId.HasValue && currentContentId != 0 && resultContentId.Value != currentContentId)
            return false;

        var player = Plugin.ObjectTable.LocalPlayer;
        var currentWorld = player?.HomeWorld.RowId ?? 0;
        var worldId = GetUint(element, "WorldId", "HomeWorldId", "OwnerWorldId");
        if (worldId.HasValue && currentWorld != 0 && worldId.Value != currentWorld)
            return false;

        return true;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                    return value.GetString();
                if (value.ValueKind == JsonValueKind.Number)
                    return value.GetRawText();
            }
        }

        return null;
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                    return number;
                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
                    return number;
            }
        }

        return null;
    }

    private static uint? GetUint(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number))
                    return number;
                if (value.ValueKind == JsonValueKind.String && uint.TryParse(value.GetString(), out number))
                    return number;
            }
        }

        return null;
    }

    private static ulong? GetUlong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetPropertyIgnoreCase(element, name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number))
                    return number;
                if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), out number))
                    return number;
            }
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private bool TryFindNearestBell(out IGameObject bell)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        bell = null!;
        if (player == null)
            return false;

        bell = Plugin.ObjectTable
            .Where(obj => obj != null && obj.ObjectKind == ObjectKind.EventObj)
            .Where(obj => obj.Name.TextValue.Contains("Summoning Bell", StringComparison.OrdinalIgnoreCase))
            .OrderBy(obj => Vector3.Distance(player.Position, obj.Position))
            .FirstOrDefault()!;

        return bell != null;
    }

    private static bool IsNearBellApproach(Vector3 position)
        => Vector3.Distance(position, RevenantsTollBellApproachPosition) <= 3f;

    private static bool IsLoading()
    {
        return Plugin.Condition[ConditionFlag.BetweenAreas] ||
               Plugin.Condition[ConditionFlag.BetweenAreas51];
    }

    private static bool IsRetainerInventoryVisible()
    {
        return TryGetActiveRetainerInventoryAddonName(out _, includeTransferList: true);
    }

    private static bool TryGetActiveRetainerInventoryAddonName(out string addonName, bool includeTransferList = false)
    {
        if (GameHelpers.IsAddonVisible("InventoryRetainerLarge"))
        {
            addonName = "InventoryRetainerLarge";
            return true;
        }

        if (GameHelpers.IsAddonVisible("InventoryRetainer"))
        {
            addonName = "InventoryRetainer";
            return true;
        }

        if (includeTransferList && GameHelpers.IsAddonVisible("RetainerItemTransferList"))
        {
            addonName = "RetainerItemTransferList";
            return true;
        }

        addonName = string.Empty;
        return false;
    }

    private static unsafe int FindSelectStringIndex(params string[] needles)
    {
        try
        {
            nint addonPtr = Plugin.GameGui.GetAddonByName("SelectString", 1);
            if (addonPtr == 0)
                return -1;

            var addon = (AddonSelectString*)addonPtr;
            if (!addon->AtkUnitBase.IsVisible)
                return -1;

            var master = new AddonMaster.SelectString(&addon->AtkUnitBase);
            for (var i = 0; i < master.EntryCount; i++)
            {
                var text = master.Entries[i].Text;
                if (needles.Any(needle => !string.IsNullOrWhiteSpace(needle) &&
                                          text.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                    return i;
            }
        }
        catch
        {
        }

        return -1;
    }

    private static unsafe bool TryFindRetainerListIndex(string targetName, out int index, out List<string> visibleNames)
    {
        index = -1;
        visibleNames = new List<string>();

        if (string.IsNullOrWhiteSpace(targetName))
            return false;

        try
        {
            nint addonPtr = Plugin.GameGui.GetAddonByName("RetainerList", 1);
            if (addonPtr == 0)
                return false;

            var addon = (AtkUnitBase*)addonPtr;
            if (!addon->IsVisible)
                return false;

            var entries = ReadRetainerListEntries(addon);
            visibleNames = entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
                .Select(entry => entry.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var normalizedTarget = NormalizeRetainerName(targetName);
            var match = entries.FirstOrDefault(entry => NormalizeRetainerName(entry.Name) == normalizedTarget);
            if (match == null)
                return false;

            index = match.Index;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static unsafe List<RetainerListEntry> ReadRetainerListEntries(AtkUnitBase* addon)
    {
        var reader = new RetainerListReader(addon);
        return reader.Retainers
            .Where(entry => entry.IsActive && IsPlausibleRetainerName(entry.Name))
            .Select(entry => new RetainerListEntry(entry.Index, entry.Name.Trim()))
            .ToList();
    }

    private sealed unsafe class RetainerListReader(AtkUnitBase* addon)
    {
        public List<RetainerEntryReader> Retainers => Loop(3, 10, 10);

        private List<RetainerEntryReader> Loop(int offset, int size, int maxLength)
        {
            var entries = new List<RetainerEntryReader>();
            for (var i = 0; i < maxLength; i++)
            {
                var entry = new RetainerEntryReader(addon, offset + i * size, i);
                if (entry.IsNull)
                    break;

                entries.Add(entry);
            }

            return entries;
        }
    }

    private sealed unsafe class RetainerEntryReader(AtkUnitBase* addon, int beginOffset, int index)
    {
        public int Index => index;
        public bool IsNull => addon->AtkValuesCount == 0 || ReadValue(0)->Type == 0;
        public string Name => ReadString(0);
        public bool IsActive => ReadBool(8) ?? false;

        private AtkValue* ReadValue(int offset)
        {
            var valueIndex = beginOffset + offset;
            if (valueIndex < 0 || valueIndex >= addon->AtkValuesCount)
                throw new ArgumentOutOfRangeException(nameof(offset));

            return &addon->AtkValues[valueIndex];
        }

        private string ReadString(int offset)
        {
            var value = ReadValue(offset);
            if (value->Type == 0)
                return string.Empty;

            if (value->Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8 or AtkValueType.WideString))
                return string.Empty;

            return value->String.Value == null
                ? string.Empty
                : MemoryHelper.ReadStringNullTerminated((nint)value->String.Value);
        }

        private bool? ReadBool(int offset)
        {
            var value = ReadValue(offset);
            if (value->Type == 0)
                return null;

            if (value->Type != AtkValueType.Bool)
                return null;

            return value->Byte != 0;
        }
    }

    private static bool IsPlausibleRetainerName(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length is < 2 or > 24)
            return false;

        if (text.Contains(' ') || text.Any(char.IsDigit))
            return false;

        return text.All(c => char.IsLetter(c) || c == '\'' || c == '-');
    }

    private static string NormalizeRetainerName(string name)
        => new(name.Where(c => char.IsLetterOrDigit(c) || c == '\'' || c == '-').Select(char.ToLowerInvariant).ToArray());

    private void SuppressAutoRetainer()
    {
        if (autoRetainerSuppressed)
            return;

        CommandHelper.SendCommand("/ays pause");
        autoRetainerSuppressed = true;
        _plugin.AddDebugLog("[RetainerMap] AutoRetainer suppression requested.");
    }

    private void UnsuppressAutoRetainer()
    {
        if (!autoRetainerSuppressed)
            return;

        CommandHelper.SendCommand("/ays resume");
        autoRetainerSuppressed = false;
        _plugin.AddDebugLog("[RetainerMap] AutoRetainer unsuppress requested.");
    }

    private void EnterStep(RetrievalStep nextStep, string status)
    {
        step = nextStep;
        stepStartedAt = DateTime.Now;
        nextActionAt = DateTime.Now;
        StatusText = status;
        ResetStepFlags();
        _plugin.AddDebugLog($"[RetainerMap] {status}");
    }

    private void ResetStepFlags()
    {
        bellInteracted = false;
        retainerSelected = false;
        inventoryOpened = false;
        retainerMoveIssued = false;
        closeIssued = false;
    }

    private void Fail(string message)
    {
        LastError = message;
        StatusText = message;
        UnsuppressAutoRetainer();
        step = RetrievalStep.Error;
        _plugin.AddDebugLog($"[RetainerMap] ERROR: {message}");
    }
}
