using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
        OnlyOtherCharacterRows,
        CurrentCharacterNoRetainerRows,
        NoParseableRows,
        NoRows,
    }

    private sealed record RetainerMapCandidate(uint ItemId, string ItemName, string RetainerName, int RetainerIndex, int Quantity);
    private sealed record RetainerMapCandidateScan(
        RetainerMapCandidateScanState State,
        RetainerMapCandidate? Candidate,
        int ResponseCount,
        int ParseableRowCount,
        int CurrentCharacterRowCount,
        int RetainerRowCount,
        int OtherCharacterRowCount);
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
    private static readonly TimeSpan RetainerCloseRetryInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan RetainerCloseSignatureLogInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] RetainerCloseAddonPriority =
    {
        "SelectYesno",
        "RetainerItemTransferList",
        "InventoryRetainerLarge",
        "InventoryRetainer",
        "SelectString",
        "RetainerList",
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
        var visibleAddons = GetVisibleRetainerCloseAddons();
        if (visibleAddons.Count == 0)
        {
            UnsuppressAutoRetainer();
            EnterStep(RetrievalStep.Complete, "Retainer map retrieved.");
            return;
        }

        var now = DateTime.Now;
        LogVisibleRetainerCloseAddons(visibleAddons, now);
        StatusText = $"Closing retainer UI... ({FormatRetainerCloseAddons(visibleAddons)})";

        if (retainerCloseAttemptCount > 0 && now - lastRetainerCloseAttemptAt < RetainerCloseRetryInterval)
        {
            nextActionAt = lastRetainerCloseAttemptAt.Add(RetainerCloseRetryInterval);
            return;
        }

        var addonToClose = visibleAddons[0];
        var useCallback = retainerCloseAttemptCount % 2 == 0;
        var actionDescription = useCallback
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

        retainerCloseAttemptCount++;
        lastRetainerCloseAttemptAt = now;
        _plugin.AddDebugLog(
            $"[RetainerMap] Retainer close attempt {retainerCloseAttemptCount}: {actionDescription}. Visible: {FormatRetainerCloseAddons(visibleAddons)}.");
        nextActionAt = now.Add(RetainerCloseRetryInterval);
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

        var allowed = enabledMapIds.Count > 0 ? enabledMapIds.ToHashSet() : GetConfiguredMapIds().ToHashSet();
        var itemSheet = Plugin.DataManager.GetExcelSheet<Item>();
        if (itemSheet == null)
        {
            LastError = "Item sheet unavailable.";
            StatusText = LastError;
            return EmptyCandidateScan(RetainerMapCandidateScanState.NoRows);
        }

        var responseCount = 0;
        var parseableRowCount = 0;
        var currentCharacterRowCount = 0;
        var retainerRowCount = 0;
        var otherCharacterRowCount = 0;

        foreach (var itemId in allowed)
        {
            var item = itemSheet.GetRow(itemId);
            var itemName = item.Name.ToString();
            if (string.IsNullOrWhiteSpace(itemName))
                continue;

            foreach (var response in SearchXaDatabase(itemName))
            {
                responseCount++;
                var candidate = TryParseCandidate(
                    response,
                    itemId,
                    itemName,
                    emitDebug,
                    ref parseableRowCount,
                    ref currentCharacterRowCount,
                    ref retainerRowCount,
                    ref otherCharacterRowCount);
                if (candidate != null)
                {
                    LastError = string.Empty;
                    return new RetainerMapCandidateScan(
                        RetainerMapCandidateScanState.CandidateFound,
                        candidate,
                        responseCount,
                        parseableRowCount,
                        currentCharacterRowCount,
                        retainerRowCount,
                        otherCharacterRowCount);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(LastError))
            StatusText = LastError;

        var state = ClassifyCandidateScan(responseCount, parseableRowCount, currentCharacterRowCount, retainerRowCount);
        if (string.IsNullOrWhiteSpace(LastError))
            SetCandidateScanStatus(state, responseCount, parseableRowCount, currentCharacterRowCount, retainerRowCount, otherCharacterRowCount, emitDebug);

        return new RetainerMapCandidateScan(
            state,
            null,
            responseCount,
            parseableRowCount,
            currentCharacterRowCount,
            retainerRowCount,
            otherCharacterRowCount);
    }

    private static RetainerMapCandidateScan EmptyCandidateScan(RetainerMapCandidateScanState state)
        => new(state, null, 0, 0, 0, 0, 0);

    private static RetainerMapCandidateScanState ClassifyCandidateScan(
        int responseCount,
        int parseableRowCount,
        int currentCharacterRowCount,
        int retainerRowCount)
    {
        if (parseableRowCount == 0)
            return responseCount > 0
                ? RetainerMapCandidateScanState.NoParseableRows
                : RetainerMapCandidateScanState.NoRows;

        if (currentCharacterRowCount == 0)
            return RetainerMapCandidateScanState.OnlyOtherCharacterRows;

        return RetainerMapCandidateScanState.CurrentCharacterNoRetainerRows;
    }

    private void SetCandidateScanStatus(
        RetainerMapCandidateScanState state,
        int responseCount,
        int parseableRowCount,
        int currentCharacterRowCount,
        int retainerRowCount,
        int otherCharacterRowCount,
        bool emitDebug)
    {
        switch (state)
        {
            case RetainerMapCandidateScanState.OnlyOtherCharacterRows:
                StatusText = "XA Database SearchItems is global; matching enabled map rows are on another character/world, not this client.";
                if (emitDebug)
                    _plugin.AddDebugLog(
                        $"[RetainerMap] XADB SearchItems is global. Parsed {parseableRowCount} enabled map row(s), " +
                        $"but all usable matches were for other characters/worlds (other={otherCharacterRowCount}).");
                break;

            case RetainerMapCandidateScanState.CurrentCharacterNoRetainerRows:
                if (retainerRowCount == 0)
                {
                    StatusText = "No enabled current-character retainer map found; XA Database matches are not retainer containers.";
                    if (emitDebug)
                        _plugin.AddDebugLog(
                            $"[RetainerMap] Parsed {parseableRowCount} XADB row(s); current-character rows={currentCharacterRowCount}, retainer rows=0.");
                }
                else
                {
                    StatusText = "No enabled current-character retainer map found; current-character retainer rows had no available quantity.";
                    if (emitDebug)
                        _plugin.AddDebugLog(
                            $"[RetainerMap] Parsed {parseableRowCount} XADB row(s); current-character retainer rows={retainerRowCount}, " +
                            "but none matched an enabled item id with quantity.");
                }

                break;

            case RetainerMapCandidateScanState.NoParseableRows:
                StatusText = "XA Database returned enabled map search results, but no parseable SearchItems rows.";
                if (emitDebug)
                    _plugin.AddDebugLog(
                        $"[RetainerMap] XADB SearchItems returned {responseCount} response(s), but no parseable pipe rows.");
                break;

            case RetainerMapCandidateScanState.NoRows:
                StatusText = "No enabled maps found on retainers in XA Database.";
                if (emitDebug)
                    _plugin.AddDebugLog("[RetainerMap] XADB SearchItems returned no rows for enabled map names.");
                break;
        }
    }

    private IReadOnlyCollection<uint> GetConfiguredMapIds()
    {
        if (_plugin.Configuration.EnabledMapTypes.Count > 0)
            return _plugin.Configuration.EnabledMapTypes;

        return TreasureMapData.KnownMaps.Keys.ToList();
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
            _log.Warning(LastError);
            return false;
        }
    }

    private IEnumerable<string> SearchXaDatabase(string itemName)
    {
        var results = new List<string>();

        if (!_plugin.IsXaDatabaseAvailable)
            return results;

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
            _log.Warning(LastError);
            return false;
        }
    }

    private RetainerMapCandidate? TryParseCandidate(
        string response,
        uint itemId,
        string itemName,
        bool emitDebug,
        ref int parseableRowCount,
        ref int currentCharacterRowCount,
        ref int retainerRowCount,
        ref int otherCharacterRowCount)
    {
        foreach (var row in ParseXaSearchRows(response))
        {
            parseableRowCount++;
            if (!IsCurrentCharacterRow(row))
            {
                otherCharacterRowCount++;
                if (emitDebug)
                    _plugin.AddDebugLog(
                        $"[RetainerMap] Ignored global XADB row for other character/world: {row.Character}@{row.World} {row.ItemName} {row.ContainerName}");
                continue;
            }

            currentCharacterRowCount++;
            if (!IsRetainerContainerName(row.ContainerName))
            {
                if (emitDebug)
                    _plugin.AddDebugLog($"[RetainerMap] Ignored current-character XADB row with non-retainer container: {row.ContainerName} ({row.ItemName}).");
                continue;
            }

            retainerRowCount++;
            if (row.ItemId != itemId || row.Quantity <= 0)
            {
                if (emitDebug)
                    _plugin.AddDebugLog(
                        $"[RetainerMap] Ignored current-character retainer row without enabled quantity: item={row.ItemId}, quantity={row.Quantity}, expected={itemId}.");
                continue;
            }

            var retainerName = ExtractRetainerName(row.ContainerName);
            if (emitDebug)
                _plugin.AddDebugLog(
                    $"[RetainerMap] XA row matched: {row.ItemName} x{row.Quantity} on {retainerName} ({row.ContainerName}).");
            return new RetainerMapCandidate(itemId, itemName, retainerName, -1, row.Quantity);
        }

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
        retainerCloseAttemptCount = 0;
        lastRetainerCloseAttemptAt = DateTime.MinValue;
        lastRetainerCloseSignatureLoggedAt = DateTime.MinValue;
        retainerCloseVisibleAddonSignature = string.Empty;
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
