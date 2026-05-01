using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
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

    private const uint RevenantsTollTerritoryId = 156;
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
    private bool retrieveClicked;
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

            if (TryFindNearestBell(out var bell))
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
        if (!TryFindNearestBell(out var bell))
        {
            EnterStep(RetrievalStep.TravelingToBell, "No reachable retainer bell nearby. Traveling to Revenant's Toll...");
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            StatusText = "Waiting for player before moving to retainer bell...";
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
            var index = Math.Max(target?.RetainerIndex ?? 0, 0);
            GameHelpers.FireAddonCallback("RetainerList", true, 2, index);
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
        if (IsRetainerInventoryVisible())
        {
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

        if (!retrieveClicked && TryOpenRetainerItemContext(target.ItemName))
        {
            retrieveClicked = true;
            nextActionAt = DateTime.Now.AddSeconds(1);
            return;
        }

        if (retrieveClicked && GameHelpers.IsAddonVisible("ContextMenu"))
        {
            GameHelpers.FireAddonCallback("ContextMenu", true, 0, 0, 0);
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

            foreach (var json in SearchXaDatabase(itemName))
            {
                var candidate = TryParseCandidate(json, itemId, itemName);
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

    private RetainerMapCandidate? TryParseCandidate(string json, uint itemId, string itemName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var element in EnumerateObjects(doc.RootElement))
            {
                if (!IsCurrentCharacterResult(element))
                    continue;

                var location = GetString(element, "Location", "LocationType", "Container", "InventoryType", "Source");
                if (!string.IsNullOrWhiteSpace(location) &&
                    !location.Contains("retainer", StringComparison.OrdinalIgnoreCase))
                    continue;

                var retainerName = GetString(element, "RetainerName", "OwnerName", "Name") ?? "unknown retainer";
                var quantity = GetInt(element, "Quantity", "Count", "ItemCount") ?? 1;
                var index = GetInt(element, "RetainerIndex", "RetainerSlot", "Index") ?? 0;
                return new RetainerMapCandidate(itemId, itemName, retainerName, index, quantity);
            }
        }
        catch (Exception ex)
        {
            LastError = $"Could not parse XA Database SearchItems response: {ex.Message}";
            _log.Warning(LastError);
        }

        return null;
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

    private static bool IsLoading()
    {
        return Plugin.Condition[ConditionFlag.BetweenAreas] ||
               Plugin.Condition[ConditionFlag.BetweenAreas51];
    }

    private static bool IsRetainerInventoryVisible()
    {
        return GameHelpers.IsAddonVisible("InventoryRetainer") ||
               GameHelpers.IsAddonVisible("RetainerItemTransferList");
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

    private static bool TryOpenRetainerItemContext(string itemName)
    {
        // Retainer inventory row discovery is patch-sensitive. Prefer a known addon callback,
        // then let the following ContextMenu/YesNo steps finish the withdrawal if the game opens it.
        if (!IsRetainerInventoryVisible())
            return false;

        GameHelpers.FireAddonCallback("InventoryRetainer", true, 0, 0, 0);
        return true;
    }

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
        retrieveClicked = false;
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
