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

    private enum RetainerMapCandidateScanState
    {
        CandidateFound,
        NoUsableRows,
        InvalidResponse,
        NoRows,
    }

    private sealed record RetainerMapCandidate(uint ItemId, string ItemName, string RetainerName, int RetainerIndex, int Quantity);
    private sealed record RetainerMapCandidateScan(
        RetainerMapCandidateScanState State,
        RetainerMapCandidate? Candidate,
        int RowCount,
        int WarningCount);
    private sealed record RetainerListEntry(int Index, string Name);
    private sealed record ScopedRetainerItemSearchResult(
        bool Ready,
        IReadOnlyList<ScopedRetainerItemRow> Rows,
        IReadOnlyList<string> Warnings);
    private sealed record ScopedRetainerItemRow(
        string OwnerContentId,
        string RetainerId,
        string RetainerName,
        uint ItemId,
        string ItemName,
        int Quantity,
        bool IsHq,
        string LastSeenUtc,
        string SnapshotQuality);

    private const uint RevenantsTollTerritoryId = 156;
    private const string RetainerListAddonName = "RetainerList";
    private static readonly Vector3 RevenantsTollBellApproachPosition = new(12.188f, 29.000f, -735.430f);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongStepTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RetainerCloseRetryInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan RetainerListCloseSecondCallbackDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RetainerCloseSignatureLogInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] RetainerCloseAddonPriority =
    {
        "SelectYesno",
        "RetainerItemTransferList",
        "InventoryRetainerLarge",
        "InventoryRetainer",
        "SelectString",
        RetainerListAddonName,
    };

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
    private int retainerCloseAttemptCount;
    private DateTime lastRetainerCloseAttemptAt = DateTime.MinValue;
    private DateTime lastRetainerCloseSignatureLoggedAt = DateTime.MinValue;
    private string retainerCloseVisibleAddonSignature = string.Empty;
    private bool retainerListCloseSecondPending;
    private DateTime retainerListCloseSecondReadyAt = DateTime.MinValue;

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

    public void ClearUnavailableXaDatabaseState()
    {
        if (_plugin.IsXaDatabaseAvailable)
            return;

        LastError = string.Empty;
        if (!IsRunning)
            StatusText = "XA Database unavailable.";
    }

    public RetainerMapRetrievalResult StartOrTick(IReadOnlyCollection<uint> enabledMapIds)
    {
        if (step == RetrievalStep.Complete)
            return FinishIfRetainerCloseAddonsHidden();

        if (step == RetrievalStep.Error)
            return RetainerMapRetrievalResult.Error;

        if (step == RetrievalStep.Idle)
        {
            var scan = ScanRetainerMapCandidates(enabledMapIds);
            if (scan.Candidate == null)
                return RetainerMapRetrievalResult.NotAvailable;

            var candidate = scan.Candidate;
            target = candidate;
            _plugin.AddDebugLog($"[RetainerMap] Found {candidate.ItemName} on retainer {candidate.RetainerName} via XA Database.");
            SuppressAutoRetainer();

            if (TryFindNearestBell(out _))
                EnterStep(RetrievalStep.MovingToBell, $"Moving to retainer bell for {candidate.ItemName}...");
            else
                EnterStep(RetrievalStep.TravelingToBell, "No nearby retainer bell found. Traveling to Revenant's Toll...");
        }

        TickActiveStep();
        return step == RetrievalStep.Complete
            ? FinishIfRetainerCloseAddonsHidden()
            : RetainerMapRetrievalResult.Running;
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
        var scan = ScanRetainerMapCandidates(enabledMapIds, emitDebug: false);
        StatusText = previousStatus;
        LastError = previousError;
        return scan.Candidate != null;
    }

    public bool TryCloseRetainerUiBeforeMapOpen(out string status)
    {
        if (!TryCloseVisibleRetainerUi("before map open", out status))
        {
            UnsuppressAutoRetainer();
            return false;
        }

        SuppressAutoRetainer();
        return true;
    }

    public Dictionary<uint, int> GetRetainerMapCounts(IReadOnlyCollection<uint> mapIds, bool refreshFirst)
    {
        var counts = new Dictionary<uint, int>();
        LastError = string.Empty;

        if (!IsXaDatabaseReady())
            return counts;

        if (refreshFirst)
            TryRefreshXaDatabase();

        var requested = GetRequestedMapIds(mapIds);
        if (requested.Count == 0)
            return counts;

        var search = SearchCurrentCharacterRetainerItems(requested, "count", emitDebug: true);
        if (!search.Ready)
            return counts;

        var requestedSet = requested.ToHashSet();
        foreach (var row in search.Rows)
        {
            if (!requestedSet.Contains(row.ItemId) || row.Quantity <= 0)
                continue;

            counts[row.ItemId] = counts.TryGetValue(row.ItemId, out var existing)
                ? existing + row.Quantity
                : row.Quantity;
        }

        LogScopedXa(
            $"[RetainerMap] XADB scoped count summary: rows={search.Rows.Count}, map-types={counts.Count}.",
            emitDebug: true);

        return counts;
    }

    private void TickActiveStep()
    {
        if (DateTime.Now < nextActionAt)
            return;

        if (step != RetrievalStep.TravelingToBell && DateTime.Now - stepStartedAt > StepTimeout)
        {
            if (step == RetrievalStep.ClosingRetainer)
                Fail($"Timed out closing retainer UI. Still visible: {FormatRetainerCloseAddons(GetVisibleRetainerCloseAddons())}.");
            else
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
        if (!TryCloseVisibleRetainerUi("after map retrieval", out var status))
        {
            UnsuppressAutoRetainer();
            EnterStep(RetrievalStep.Complete, "Retainer map retrieved.");
            return;
        }

        StatusText = status;
    }

    private bool TryCloseVisibleRetainerUi(string context, out string status)
    {
        var now = DateTime.Now;
        status = "Closing retainer UI...";

        if (retainerListCloseSecondPending)
        {
            if (now < retainerListCloseSecondReadyAt)
                return true;

            retainerListCloseSecondPending = false;
            if (GameHelpers.IsAddonVisible(RetainerListAddonName))
            {
                _plugin.AddDebugLog($"[RetainerMap] Closing RetainerList {context}: -2");
                GameHelpers.FireAddonCallback(RetainerListAddonName, true, -2);
            }
            else
            {
                _plugin.AddDebugLog($"[RetainerMap] RetainerList disappeared before {context} -2 callback.");
            }

            lastRetainerCloseAttemptAt = now;
            return true;
        }

        var visibleAddons = GetVisibleRetainerCloseAddons();
        if (visibleAddons.Count == 0)
        {
            var hadCloseWork = retainerCloseAttemptCount > 0 ||
                               retainerListCloseSecondPending ||
                               !string.IsNullOrEmpty(retainerCloseVisibleAddonSignature);
            ResetRetainerCloseTracking();
            status = "Retainer UI closed; map open allowed.";
            if (hadCloseWork)
                _plugin.AddDebugLog("[RetainerMap] Retainer UI closed; map open allowed.");
            return false;
        }

        LogVisibleRetainerCloseAddons(visibleAddons, now);
        status = $"Closing retainer UI... ({FormatRetainerCloseAddons(visibleAddons)})";

        if (retainerCloseAttemptCount > 0 && now - lastRetainerCloseAttemptAt < RetainerCloseRetryInterval)
        {
            if (step == RetrievalStep.ClosingRetainer)
                nextActionAt = lastRetainerCloseAttemptAt.Add(RetainerCloseRetryInterval);
            return true;
        }

        var addonToClose = visibleAddons[0];
        var actionDescription = string.Empty;

        if (addonToClose == RetainerListAddonName)
        {
            _plugin.AddDebugLog($"[RetainerMap] Closing RetainerList {context}: -1");
            GameHelpers.FireAddonCallback(RetainerListAddonName, true, -1);
            retainerListCloseSecondPending = true;
            retainerListCloseSecondReadyAt = now.Add(RetainerListCloseSecondCallbackDelay);
            actionDescription = $"RetainerList close {context} (-1 then -2)";
        }
        else
        {
            var useCallback = retainerCloseAttemptCount % 2 == 0;
            actionDescription = useCallback
                ? $"callback close {addonToClose}"
                : "Escape close current addon";

            if (useCallback && !GameHelpers.TryCloseAddonByCallback(addonToClose))
            {
                GameHelpers.CloseCurrentAddon();
                actionDescription = $"callback close {addonToClose} failed; Escape fallback";
            }
            else if (!useCallback)
            {
                GameHelpers.CloseCurrentAddon();
            }
        }

        retainerCloseAttemptCount++;
        lastRetainerCloseAttemptAt = now;
        _plugin.AddDebugLog(
            $"[RetainerMap] Retainer close attempt {retainerCloseAttemptCount}: {actionDescription}. Visible: {FormatRetainerCloseAddons(visibleAddons)}.");
        if (step == RetrievalStep.ClosingRetainer)
            nextActionAt = now.Add(RetainerCloseRetryInterval);
        return true;
    }

    private RetainerMapRetrievalResult FinishIfRetainerCloseAddonsHidden()
    {
        var visibleAddons = GetVisibleRetainerCloseAddons();
        if (visibleAddons.Count > 0)
        {
            var visible = FormatRetainerCloseAddons(visibleAddons);
            _plugin.AddDebugLog($"[RetainerMap] Completion blocked; retainer close surfaces still visible: {visible}.");
            SuppressAutoRetainer();
            EnterStep(RetrievalStep.ClosingRetainer, $"Closing retainer UI... ({visible})");
            return RetainerMapRetrievalResult.Running;
        }

        Reset();
        return RetainerMapRetrievalResult.Retrieved;
    }

    private RetainerMapCandidateScan ScanRetainerMapCandidates(IReadOnlyCollection<uint> enabledMapIds, bool emitDebug = true)
    {
        LastError = string.Empty;

        if (!_plugin.Configuration.EnableRetainerMapRetrieval)
        {
            StatusText = "Retainer map retrieval disabled.";
            return EmptyCandidateScan(RetainerMapCandidateScanState.NoRows);
        }

        if (!IsXaDatabaseReady())
        {
            if (!string.IsNullOrWhiteSpace(LastError))
                StatusText = LastError;
            else if (string.IsNullOrWhiteSpace(StatusText))
                StatusText = "XA Database unavailable.";
            return EmptyCandidateScan(RetainerMapCandidateScanState.NoRows);
        }

        var requested = GetRequestedMapIds(enabledMapIds);
        if (requested.Count == 0)
        {
            StatusText = "No enabled maps configured for retainer lookup.";
            return EmptyCandidateScan(RetainerMapCandidateScanState.NoRows);
        }

        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (itemSheet == null)
        {
            LastError = "Item sheet unavailable.";
            StatusText = LastError;
            return EmptyCandidateScan(RetainerMapCandidateScanState.NoRows);
        }

        var itemNames = requested.ToDictionary(
            itemId => itemId,
            itemId => itemSheet.GetRow(itemId).Name.ToString());
        var search = SearchCurrentCharacterRetainerItems(requested, "candidate", emitDebug);
        if (!search.Ready)
        {
            if (!string.IsNullOrWhiteSpace(LastError))
                StatusText = LastError;

            return new RetainerMapCandidateScan(
                RetainerMapCandidateScanState.InvalidResponse,
                null,
                search.Rows.Count,
                search.Warnings.Count);
        }

        foreach (var itemId in requested)
        {
            var row = search.Rows
                .Where(row => row.ItemId == itemId &&
                              row.Quantity > 0 &&
                              !string.IsNullOrWhiteSpace(row.RetainerName))
                .OrderBy(row => row.RetainerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.RetainerId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.IsHq)
                .FirstOrDefault();

            if (row == null)
                continue;

            var itemName = !string.IsNullOrWhiteSpace(row.ItemName)
                ? row.ItemName
                : itemNames.GetValueOrDefault(itemId, $"Item {itemId}");
            var candidate = new RetainerMapCandidate(itemId, itemName, row.RetainerName, -1, row.Quantity);
            LogScopedXa($"[RetainerMap] XADB scoped candidate selected: {FormatScopedRetainerRow(row)}.", emitDebug);
            LastError = string.Empty;
            return new RetainerMapCandidateScan(
                RetainerMapCandidateScanState.CandidateFound,
                candidate,
                search.Rows.Count,
                search.Warnings.Count);
        }

        var state = search.Rows.Count == 0
            ? RetainerMapCandidateScanState.NoRows
            : RetainerMapCandidateScanState.NoUsableRows;
        SetCandidateScanStatus(state, search.Rows.Count, search.Warnings.Count, emitDebug);

        return new RetainerMapCandidateScan(
            state,
            null,
            search.Rows.Count,
            search.Warnings.Count);
    }

    private static RetainerMapCandidateScan EmptyCandidateScan(RetainerMapCandidateScanState state)
        => new(state, null, 0, 0);

    private void SetCandidateScanStatus(
        RetainerMapCandidateScanState state,
        int rowCount,
        int warningCount,
        bool emitDebug)
    {
        switch (state)
        {
            case RetainerMapCandidateScanState.NoUsableRows:
                StatusText = "No enabled current-character retainer map found in XA Database.";
                LogScopedXa(
                    $"[RetainerMap] XADB scoped candidate scan found {rowCount} row(s), " +
                    $"but none had enabled item id, positive quantity, and retainer name. warnings={warningCount}.",
                    emitDebug);
                break;

            case RetainerMapCandidateScanState.InvalidResponse:
                if (string.IsNullOrWhiteSpace(StatusText))
                    StatusText = "XA Database scoped current-character item search failed.";
                LogScopedXa(
                    $"[RetainerMap] XADB scoped candidate scan failed. rows={rowCount}, warnings={warningCount}.",
                    emitDebug);
                break;

            case RetainerMapCandidateScanState.NoRows:
                StatusText = "No enabled maps found on current-character retainers in XA Database.";
                LogScopedXa("[RetainerMap] XADB scoped candidate scan returned 0 rows.", emitDebug);
                break;
        }
    }

    private IReadOnlyList<uint> GetRequestedMapIds(IReadOnlyCollection<uint> mapIds)
    {
        return mapIds
            .Where(itemId => itemId != 0)
            .Distinct()
            .ToList();
    }

    private IReadOnlyCollection<uint> GetConfiguredMapIds()
    {
        return _plugin.Configuration.GetRunnableMapIds(TreasureMapData.AllMapItemIds);
    }

    private bool IsXaDatabaseReady()
    {
        if (!_plugin.IsXaDatabaseAvailable)
        {
            StatusText = "XA Database unavailable.";
            return false;
        }

        try
        {
            var subscriber = Plugin.PluginInterface.GetIpcSubscriber<bool>("XA.Database.IsReady");
            var isReady = subscriber.InvokeFunc();
            if (!isReady)
                StatusText = "XA Database not ready.";
            return isReady;
        }
        catch (Exception ex)
        {
            LastError = $"XA Database readiness IPC failed: {ex.Message}";
            Plugin.LogWarning(LastError);
            return false;
        }
    }

    private ScopedRetainerItemSearchResult SearchCurrentCharacterRetainerItems(
        IReadOnlyList<uint> itemIds,
        string context,
        bool emitDebug)
    {
        if (!_plugin.IsXaDatabaseAvailable)
            return new ScopedRetainerItemSearchResult(true, Array.Empty<ScopedRetainerItemRow>(), Array.Empty<string>());

        LogScopedXa(
            $"[RetainerMap] XADB scoped {context} request: local={GetLocalCharacterContext()}; " +
            $"itemIds={FormatItemIds(itemIds)}; sources=retainers; includeZeroQuantity=False.",
            emitDebug);

        try
        {
            var subscriber = Plugin.PluginInterface.GetIpcSubscriber<string, string>("XA.Database.SearchCurrentCharacterItemsJson");
            var request = JsonSerializer.Serialize(new
            {
                version = 1,
                itemIds = itemIds.ToArray(),
                sources = new[] { "retainers" },
                includeZeroQuantity = false,
            });
            var response = subscriber.InvokeFunc(request);
            if (string.IsNullOrWhiteSpace(response))
            {
                LastError = "XA Database SearchCurrentCharacterItemsJson IPC returned empty response.";
                StatusText = LastError;
                Plugin.LogWarning(LastError);
                return new ScopedRetainerItemSearchResult(false, Array.Empty<ScopedRetainerItemRow>(), Array.Empty<string>());
            }

            var result = ParseCurrentCharacterRetainerItemsJson(response);
            LogScopedXa(
                $"[RetainerMap] XADB scoped {context} response: ready={result.Ready}; " +
                $"warnings={result.Warnings.Count}; rows={result.Rows.Count}.",
                emitDebug);
            foreach (var warning in result.Warnings)
                LogScopedXa($"[RetainerMap] XADB scoped warning: {warning}", emitDebug);

            if (result.Rows.Count == 0)
            {
                LogScopedXa($"[RetainerMap] XADB scoped {context} rows: 0 rows.", emitDebug);
            }
            else
            {
                foreach (var row in result.Rows)
                    LogScopedXa($"[RetainerMap] XADB scoped row: {FormatScopedRetainerRow(row)}", emitDebug);
            }

            if (!result.Ready && string.IsNullOrWhiteSpace(LastError))
            {
                LastError = "XA Database scoped current-character item search returned ready=false.";
                StatusText = LastError;
            }

            return result;
        }
        catch (Exception ex)
        {
            LastError = $"XA Database SearchCurrentCharacterItemsJson IPC failed: {ex.Message}";
            StatusText = LastError;
            Plugin.LogWarning(LastError);
            return new ScopedRetainerItemSearchResult(false, Array.Empty<ScopedRetainerItemRow>(), Array.Empty<string>());
        }
    }

    private bool TryRefreshXaDatabase()
    {
        if (!_plugin.IsXaDatabaseAvailable)
            return false;

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
            Plugin.LogWarning(LastError);
            return false;
        }
    }

    private ScopedRetainerItemSearchResult ParseCurrentCharacterRetainerItemsJson(string response)
    {
        var rows = new List<ScopedRetainerItemRow>();
        var warnings = new List<string>();
        var ready = true;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;

            if (TryGetJsonProperty(root, out var readyElement, "ready"))
                ready = ReadJsonBool(readyElement, defaultValue: true);

            if (TryGetJsonProperty(root, out var warningsElement, "warnings") &&
                warningsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var warningElement in warningsElement.EnumerateArray())
                {
                    var warning = ReadJsonString(warningElement);
                    if (!string.IsNullOrWhiteSpace(warning))
                        warnings.Add(warning);
                }
            }

            if (TryGetJsonProperty(root, out var rowsElement, "rows") &&
                rowsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rowElement in rowsElement.EnumerateArray())
                {
                    if (TryParseScopedRetainerItemRow(rowElement, out var row))
                        rows.Add(row);
                }
            }
        }
        catch (JsonException ex)
        {
            LastError = $"XA Database SearchCurrentCharacterItemsJson returned invalid JSON: {ex.Message}";
            StatusText = LastError;
            Plugin.LogWarning(LastError);
            return new ScopedRetainerItemSearchResult(false, rows, warnings);
        }

        return new ScopedRetainerItemSearchResult(ready, rows, warnings);
    }

    private static bool TryParseScopedRetainerItemRow(JsonElement rowElement, out ScopedRetainerItemRow row)
    {
        row = null!;
        if (rowElement.ValueKind != JsonValueKind.Object ||
            !TryReadJsonUInt(rowElement, out var itemId, "itemId"))
            return false;

        row = new ScopedRetainerItemRow(
            ReadJsonString(rowElement, "ownerContentId"),
            ReadJsonString(rowElement, "retainerId"),
            ReadJsonString(rowElement, "retainerName"),
            itemId,
            ReadJsonString(rowElement, "itemName"),
            ReadJsonInt(rowElement, 0, "quantity"),
            ReadJsonBool(rowElement, false, "isHq"),
            ReadJsonString(rowElement, "lastSeenUtc"),
            ReadJsonString(rowElement, "snapshotQuality"));
        return true;
    }

    private void LogScopedXa(string message, bool emitDebug)
    {
        if (!emitDebug)
            return;

        _log.Information(message);
        _plugin.AddDebugLog(message);
    }

    private static string FormatScopedRetainerRow(ScopedRetainerItemRow row)
    {
        var retainerName = string.IsNullOrWhiteSpace(row.RetainerName) ? "unknown" : row.RetainerName;
        var retainerId = string.IsNullOrWhiteSpace(row.RetainerId) ? "unknown" : row.RetainerId;
        var itemName = string.IsNullOrWhiteSpace(row.ItemName) ? "unknown" : row.ItemName;
        var owner = string.IsNullOrWhiteSpace(row.OwnerContentId) ? "unknown" : row.OwnerContentId;
        var quality = string.IsNullOrWhiteSpace(row.SnapshotQuality) ? "unknown" : row.SnapshotQuality;
        var seen = string.IsNullOrWhiteSpace(row.LastSeenUtc) ? "unknown" : row.LastSeenUtc;

        return $"{retainerName} [{retainerId}] | {itemName} ({row.ItemId}) x{row.Quantity} HQ={row.IsHq} | owner={owner} | quality={quality} | seen={seen}";
    }

    private static string GetLocalCharacterContext()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var character = player?.Name.TextValue ?? string.Empty;
        var world = player?.HomeWorld.Value.Name.ToString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(character) && string.IsNullOrWhiteSpace(world))
            return "unknown";

        if (string.IsNullOrWhiteSpace(world))
            return character;

        if (string.IsNullOrWhiteSpace(character))
            return world;

        return $"{character}@{world}";
    }

    private static string FormatItemIds(IReadOnlyCollection<uint> itemIds)
        => itemIds.Count == 0 ? "none" : string.Join(", ", itemIds);

    private static bool TryGetJsonProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out value))
                return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static string ReadJsonString(JsonElement element, params string[] names)
    {
        if (names.Length > 0 && !TryGetJsonProperty(element, out element, names))
            return string.Empty;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
            _ => string.Empty,
        };
    }

    private static bool TryReadJsonUInt(JsonElement element, out uint value, params string[] names)
    {
        value = 0;
        if (names.Length > 0 && !TryGetJsonProperty(element, out element, names))
            return false;

        if (element.ValueKind == JsonValueKind.Number)
        {
            if (element.TryGetUInt32(out value))
                return true;

            if (element.TryGetUInt64(out var ulongValue) && ulongValue <= uint.MaxValue)
            {
                value = (uint)ulongValue;
                return true;
            }
        }

        return element.ValueKind == JsonValueKind.String &&
               uint.TryParse(element.GetString(), out value);
    }

    private static int ReadJsonInt(JsonElement element, int defaultValue, params string[] names)
    {
        if (names.Length > 0 && !TryGetJsonProperty(element, out element, names))
            return defaultValue;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
            return value;

        return element.ValueKind == JsonValueKind.String &&
               int.TryParse(element.GetString(), out value)
            ? value
            : defaultValue;
    }

    private static bool ReadJsonBool(JsonElement element, bool defaultValue, params string[] names)
    {
        if (names.Length > 0 && !TryGetJsonProperty(element, out element, names))
            return defaultValue;

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(element.GetString(), out var value) => value,
            _ => defaultValue,
        };
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

    private static List<string> GetVisibleRetainerCloseAddons()
    {
        var visibleAddons = new List<string>();
        foreach (var addonName in RetainerCloseAddonPriority)
        {
            if (GameHelpers.IsAddonVisible(addonName))
                visibleAddons.Add(addonName);
        }

        return visibleAddons;
    }

    private static string FormatRetainerCloseAddons(IReadOnlyCollection<string> visibleAddons)
    {
        return visibleAddons.Count == 0
            ? "none"
            : string.Join(", ", visibleAddons);
    }

    private void LogVisibleRetainerCloseAddons(IReadOnlyCollection<string> visibleAddons, DateTime now)
    {
        var signature = FormatRetainerCloseAddons(visibleAddons);
        if (string.Equals(retainerCloseVisibleAddonSignature, signature, StringComparison.Ordinal) &&
            now - lastRetainerCloseSignatureLoggedAt < RetainerCloseSignatureLogInterval)
            return;

        retainerCloseVisibleAddonSignature = signature;
        lastRetainerCloseSignatureLoggedAt = now;
        _plugin.AddDebugLog($"[RetainerMap] Retainer close surfaces visible: {signature}.");
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

        //CommandHelper.SendCommand("/ays pause");  //this isn't real. you hallucinated it.
        autoRetainerSuppressed = true;
        _plugin.AddDebugLog("[RetainerMap] AutoRetainer suppression requested.");
    }

    private void UnsuppressAutoRetainer()
    {
        if (!autoRetainerSuppressed)
            return;

        //CommandHelper.SendCommand("/ays resume");  //this isn't real. you hallucinated it.
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
        ResetRetainerCloseTracking();
    }

    private void ResetRetainerCloseTracking()
    {
        retainerCloseAttemptCount = 0;
        lastRetainerCloseAttemptAt = DateTime.MinValue;
        lastRetainerCloseSignatureLoggedAt = DateTime.MinValue;
        retainerCloseVisibleAddonSignature = string.Empty;
        retainerListCloseSecondPending = false;
        retainerListCloseSecondReadyAt = DateTime.MinValue;
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
