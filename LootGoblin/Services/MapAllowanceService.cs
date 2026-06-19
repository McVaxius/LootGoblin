using System;
using System.Collections.Generic;
using Dalamud.Memory;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace LootGoblin.Services;

public sealed class MapAllowanceService : IDisposable
{
    private static readonly TimeSpan AutoOpenPollTtl = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AutoOpenTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AutoOpenRetryInterval = TimeSpan.FromSeconds(30);

    private readonly Plugin plugin;
    private readonly IPluginLog log;
    private readonly MapAllowanceVerificationCache verificationCache = new();
    private MapAllowanceStatus pendingStatus = MapAllowanceVerificationCache.UnverifiedStatus;
    private DateTimeOffset pendingStatusAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastAutoOpenAttemptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset autoOpenStartedAtUtc = DateTimeOffset.MinValue;
    private ulong activeContentId;
    private bool autoOpenPending;
    private bool autoOpenedContentsInfo;
    private bool contentsInfoDumpedForCurrentWindow;

    public MapAllowanceService(Plugin plugin, IPluginLog log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public void Dispose()
    {
    }

    public void OnActiveCharacterChanged(ulong contentId)
    {
        if (contentId == 0)
        {
            ClearActiveCharacterState();
            return;
        }

        ActivateContentId(contentId, DateTimeOffset.UtcNow, force: true);
    }

    public void ClearActiveCharacterState()
    {
        activeContentId = 0;
        pendingStatus = MapAllowanceVerificationCache.UnverifiedStatus;
        pendingStatusAtUtc = DateTimeOffset.MinValue;
        ResetAutoOpen();
    }

    public MapAllowanceStatus GetStatus(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!TryResolveCurrentContentId(out var contentId))
        {
            pendingStatus = MapAllowanceVerificationCache.UnverifiedStatus;
            pendingStatusAtUtc = now;
            return pendingStatus;
        }

        if (verificationCache.TryGet(contentId, now, out var verifiedStatus))
        {
            pendingStatus = verifiedStatus;
            pendingStatusAtUtc = now;
            return verifiedStatus;
        }

        if (!force && autoOpenPending && now - pendingStatusAtUtc < AutoOpenPollTtl)
            return pendingStatus;

        pendingStatus = ReadStatus(contentId, now);
        pendingStatusAtUtc = now;
        return pendingStatus;
    }

    public bool IsAllowanceReady(out string detail)
    {
        var status = GetStatus(force: true);
        if (!status.IsAvailable)
        {
            detail = $"Map allowance status unavailable: {status.Error}";
            return false;
        }

        if (status.IsReady)
        {
            detail = "ready";
            return true;
        }

        detail = $"Map allowance locked for {status.CompactText}.";
        return false;
    }

    public void MarkAllowanceConsumedByGather()
    {
        var now = DateTimeOffset.UtcNow;
        if (!TryResolveCurrentContentId(out var contentId))
            return;

        pendingStatus = verificationCache.MarkConsumed(contentId, now);
        pendingStatusAtUtc = now;
        plugin.SetActiveMapAllowanceStatus(contentId, pendingStatus);
        FinishAutoOpen(closeContentsInfo: true);
        log.Debug($"[MapAllowance] Marked map allowance consumed; next allowance at {pendingStatus.NextAllowanceAtUtc:O}.");
    }

    private unsafe MapAllowanceStatus ReadStatus(ulong contentId, DateTimeOffset now)
    {
        if (contentId == 0)
            return MapAllowanceVerificationCache.UnverifiedStatus;

        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
            return new MapAllowanceStatus(false, TimeSpan.Zero, null, "loading");

        try
        {
            if (TryReadVisibleContentsInfo(now, out var contentsInfoStatus, out var contentsInfoSource, out var diagnostic))
            {
                if (contentsInfoStatus.IsAvailable)
                {
                    var verifiedStatus = StoreVerifiedStatus(contentId, contentsInfoStatus, now);
                    FinishAutoOpen(closeContentsInfo: true);
                    return verifiedStatus;
                }

                if (autoOpenPending && now - autoOpenStartedAtUtc >= AutoOpenTimeout)
                    FinishAutoOpen(closeContentsInfo: true);

                DumpContentsInfoFailure(diagnostic, contentsInfoSource, contentsInfoStatus);
                return contentsInfoStatus;
            }

            if (autoOpenPending)
            {
                if (now - autoOpenStartedAtUtc >= AutoOpenTimeout)
                    FinishAutoOpen(closeContentsInfo: false);
                else
                    return MapAllowanceVerificationCache.UnverifiedStatus;
            }

            TryStartAutoOpen(now);
            return MapAllowanceVerificationCache.UnverifiedStatus;
        }
        catch (Exception ex)
        {
            log.Debug($"[MapAllowance] Failed to read map allowance status: {ex.Message}");
            return new MapAllowanceStatus(false, TimeSpan.Zero, null, ex.Message);
        }
    }

    private bool TryResolveCurrentContentId(out ulong contentId)
    {
        contentId = 0;
        if (!Plugin.ClientState.IsLoggedIn)
        {
            ClearActiveCharacterState();
            return false;
        }

        if (!Plugin.PlayerState.IsLoaded)
        {
            return false;
        }

        contentId = Plugin.PlayerState.ContentId;
        if (contentId == 0)
        {
            return false;
        }

        if (activeContentId != contentId)
            ActivateContentId(contentId, DateTimeOffset.UtcNow, force: true);

        return true;
    }

    private void ActivateContentId(ulong contentId, DateTimeOffset now, bool force)
    {
        if (!force && activeContentId == contentId)
            return;

        activeContentId = contentId;
        ResetAutoOpen();
        pendingStatus = MapAllowanceVerificationCache.UnverifiedStatus;
        pendingStatusAtUtc = DateTimeOffset.MinValue;

        if (plugin.ActiveMapGatherContentId == contentId &&
            plugin.ActiveMapGatherConfig.TryGetMapAllowanceSnapshot(now, out var snapshot))
        {
            pendingStatus = verificationCache.Store(contentId, snapshot, now);
            pendingStatusAtUtc = now;
        }
    }

    private MapAllowanceStatus StoreVerifiedStatus(ulong contentId, MapAllowanceStatus status, DateTimeOffset now)
    {
        var verifiedStatus = verificationCache.Store(contentId, status, now);
        if (verifiedStatus.IsAvailable)
            plugin.SetActiveMapAllowanceStatus(contentId, verifiedStatus);

        return verifiedStatus;
    }

    private unsafe bool TryReadVisibleContentsInfo(
        DateTimeOffset now,
        out MapAllowanceStatus status,
        out MapAllowanceParseSource source,
        out ContentsInfoDiagnostic diagnostic)
    {
        status = default;
        source = MapAllowanceParseSource.None;
        diagnostic = ContentsInfoDiagnostic.Empty;

        var addon = GetContentsInfoAddon(out var addonFound, out var addonVisible);
        if (addon == null || !addonVisible)
        {
            contentsInfoDumpedForCurrentWindow = false;
            return false;
        }

        var (values, atkValueDiagnostics) = ReadAtkValues(addon);
        diagnostic = new ContentsInfoDiagnostic(addonFound, addonVisible, values.Length, atkValueDiagnostics, Array.Empty<string>());

        if (MapAllowanceContentsInfoParser.TryParse(values, now, out status, out source))
            return true;

        var atkStatus = status;
        var visibleTexts = CollectVisibleTextNodes(addon);
        diagnostic = new ContentsInfoDiagnostic(addonFound, addonVisible, values.Length, atkValueDiagnostics, visibleTexts);
        if (MapAllowanceContentsInfoParser.TryParseVisibleTextNodes(visibleTexts, now, out status, out source))
            return true;

        status = atkStatus.IsAvailable ? status : atkStatus;
        return true;
    }

    private static unsafe (object?[] Values, IReadOnlyList<AtkValueDiagnostic> Diagnostics) ReadAtkValues(AtkUnitBase* addon)
    {
        var valueCount = Math.Max(0, (int)addon->AtkValuesCount);
        var values = new object?[valueCount];
        var diagnostics = new AtkValueDiagnostic[valueCount];

        for (var i = 0; i < values.Length; i++)
        {
            var value = &addon->AtkValues[i];
            var text = TryReadAtkString(value);
            values[i] = text;
            diagnostics[i] = new AtkValueDiagnostic(i, value->Type.ToString(), text);
        }

        return (values, diagnostics);
    }

    private static unsafe AtkUnitBase* GetContentsInfoAddon(out bool found, out bool visible)
    {
        found = false;
        visible = false;

        try
        {
            var manager = RaptureAtkUnitManager.Instance();
            var addon = manager == null ? null : manager->GetAddonByName("ContentsInfo");
            found = addon != null;
            visible = addon != null && addon->IsVisible;
            return addon;
        }
        catch
        {
            return null;
        }
    }

    private static unsafe string? TryReadAtkString(AtkValue* value)
    {
        if (value->Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString or AtkValueType.WideString))
            return null;

        return value->String.Value == null
            ? string.Empty
            : MemoryHelper.ReadStringNullTerminated((nint)value->String.Value);
    }

    private static unsafe IReadOnlyList<string> CollectVisibleTextNodes(AtkUnitBase* addon)
    {
        var visibleTexts = new List<string>();

        for (var id = 1u; id <= 300u; id++)
            TryCollectTextNode(addon, id, visibleTexts);

        for (var id = 50000u; id <= 51200u; id++)
            TryCollectTextNode(addon, id, visibleTexts);

        return visibleTexts;
    }

    private static unsafe void TryCollectTextNode(AtkUnitBase* addon, uint nodeId, List<string> visibleTexts)
    {
        var node = addon->GetNodeById(nodeId);
        if (node == null ||
            node->Type != NodeType.Text ||
            !node->IsVisible())
        {
            return;
        }

        var textNode = (AtkTextNode*)node;
        var text = MapAllowanceContentsInfoParser.Normalize(textNode->NodeText.ToString());
        if (!string.IsNullOrWhiteSpace(text) && !visibleTexts.Contains(text))
            visibleTexts.Add(text);
    }

    private void DumpContentsInfoFailure(
        ContentsInfoDiagnostic diagnostic,
        MapAllowanceParseSource selectedSource,
        MapAllowanceStatus contentsInfoStatus)
    {
        if (!plugin.Configuration.DebugMode || contentsInfoDumpedForCurrentWindow)
            return;

        contentsInfoDumpedForCurrentWindow = true;
        plugin.AddDebugLog(
            $"[MapAllowance] ContentsInfo parse failed. found={diagnostic.AddonFound} visible={diagnostic.AddonVisible} AtkValuesCount={diagnostic.AtkValuesCount} selectedSource={selectedSource} contentsError='{contentsInfoStatus.Error}'");

        foreach (var value in diagnostic.AtkValues)
        {
            plugin.AddDebugLog(
                $"[MapAllowance] AtkValue[{value.Index}] type={value.Type} string='{FormatDiagnosticString(value.Text)}'");
        }

        for (var i = 0; i < diagnostic.VisibleTexts.Count; i++)
            plugin.AddDebugLog($"[MapAllowance] TextNode[{i}] string='{FormatDiagnosticString(diagnostic.VisibleTexts[i])}'");
    }

    private static string FormatDiagnosticString(string? value)
        => value == null
            ? "<null>"
            : value
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal);

    private void TryStartAutoOpen(DateTimeOffset now)
    {
        if (autoOpenPending || now - lastAutoOpenAttemptUtc < AutoOpenRetryInterval)
            return;

        lastAutoOpenAttemptUtc = now;
        if (!CommandHelper.TrySendCommand("/timers"))
            return;

        autoOpenPending = true;
        autoOpenedContentsInfo = true;
        autoOpenStartedAtUtc = now;
        contentsInfoDumpedForCurrentWindow = false;
    }

    private void FinishAutoOpen(bool closeContentsInfo)
    {
        if (!autoOpenPending)
            return;

        var shouldClose = closeContentsInfo && autoOpenedContentsInfo;
        autoOpenPending = false;
        autoOpenedContentsInfo = false;
        autoOpenStartedAtUtc = DateTimeOffset.MinValue;

        if (shouldClose)
            GameHelpers.TryCloseAddonByCallback("ContentsInfo");
    }

    private void ResetAutoOpen()
    {
        lastAutoOpenAttemptUtc = DateTimeOffset.MinValue;
        autoOpenStartedAtUtc = DateTimeOffset.MinValue;
        autoOpenPending = false;
        autoOpenedContentsInfo = false;
        contentsInfoDumpedForCurrentWindow = false;
    }

    private readonly record struct AtkValueDiagnostic(int Index, string Type, string? Text);

    private readonly record struct ContentsInfoDiagnostic(
        bool AddonFound,
        bool AddonVisible,
        int AtkValuesCount,
        IReadOnlyList<AtkValueDiagnostic> AtkValues,
        IReadOnlyList<string> VisibleTexts)
    {
        public static ContentsInfoDiagnostic Empty { get; } = new(
            false,
            false,
            0,
            Array.Empty<AtkValueDiagnostic>(),
            Array.Empty<string>());
    }
}
