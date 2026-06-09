using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using LootGoblin.Models;

namespace LootGoblin.Services;

public sealed class TreasureMapLocationService : IDisposable
{
    private const uint TreasureMapsActorControlCategory = 0x54;
    private const string ActorControlSelfSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 83 EC 30 33 FF 48 8B D9";
    private const string ShowTreasureMapSignature = "E8 ?? ?? ?? ?? 40 84 F6 0F 85 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ??";
    private static readonly TimeSpan CapturedLocationLifetime = TimeSpan.FromMinutes(5);

    private readonly Plugin plugin;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly Dictionary<uint, TreasureRankInfo> rankByRowId = new();
    private readonly Dictionary<uint, uint> eventItemToRankRowId = new();
    private Hook<HandleActorControlSelfDelegate>? actorControlSelfHook;
    private Hook<ShowTreasureMapDelegate>? showTreasureMapHook;

    private delegate char HandleActorControlSelfDelegate(long a1, long a2, nint dataPtr);
    private delegate nint ShowTreasureMapDelegate(nint manager, ushort rowId, ushort subRowId, byte unknown);

    public TreasureMapLocationService(
        Plugin plugin,
        IDataManager dataManager,
        ISigScanner sigScanner,
        IGameInteropProvider gameInteropProvider,
        IPluginLog log)
    {
        this.plugin = plugin;
        this.dataManager = dataManager;
        this.sigScanner = sigScanner;
        this.gameInteropProvider = gameInteropProvider;
        this.log = log;

        BuildRankLookup();
        InstallHooks();
    }

    public bool IsAvailable { get; private set; }
    public TreasureMapResolvedLocation? LastResolvedLocation { get; private set; }

    public void Dispose()
    {
        actorControlSelfHook?.Dispose();
        actorControlSelfHook = null;
        showTreasureMapHook?.Dispose();
        showTreasureMapHook = null;
        IsAvailable = false;
    }

    public void ClearCapturedLocation()
    {
        LastResolvedLocation = null;
    }

    public void CheckAvailability(bool logStatus = true)
    {
        if (logStatus)
            plugin.AddDebugLog(
                $"Treasure map capture: {(IsAvailable ? "Available" : "Unavailable")} " +
                $"(actor packet={(actorControlSelfHook != null ? "yes" : "no")}, show hook={(showTreasureMapHook != null ? "yes" : "no")}, {rankByRowId.Count} rank rows)");
    }

    public bool TryGetLatestLocation(uint expectedMapItemId, uint expectedEventItemId, out MapLocation location)
    {
        location = new MapLocation();

        var resolved = LastResolvedLocation;
        if (resolved == null)
            return false;

        if (DateTime.Now - resolved.CapturedAt > CapturedLocationLifetime)
            return false;

        if (expectedMapItemId != 0 && resolved.MapItemId != 0 && resolved.MapItemId != expectedMapItemId)
            return false;

        if (expectedEventItemId != 0 && resolved.EventItemId != 0 && resolved.EventItemId != expectedEventItemId)
            return false;

        location = resolved.ToMapLocation();
        return true;
    }

    public bool TryGetLatestLocationForKeyItem(uint eventItemId, out MapLocation location)
        => TryGetLatestLocation(0, eventItemId, out location);

    private void BuildRankLookup()
    {
        try
        {
            var rankSheet = dataManager.GetExcelSheet<TreasureHuntRank>();
            if (rankSheet == null)
                return;

            foreach (var rank in rankSheet)
            {
                var mapItem = rank.ItemName.ValueNullable;
                var eventItem = rank.KeyItemName.ValueNullable;
                if (mapItem == null || mapItem.Value.RowId == 0 || eventItem == null || eventItem.Value.RowId == 0)
                    continue;

                var info = new TreasureRankInfo(
                    rank.RowId,
                    mapItem.Value.RowId,
                    eventItem.Value.RowId,
                    mapItem.Value.Name.ToString());

                rankByRowId[rank.RowId] = info;
                eventItemToRankRowId[eventItem.Value.RowId] = rank.RowId;
            }

            plugin.AddDebugLog($"[TreasureMapCapture] Loaded {rankByRowId.Count} treasure-map rank rows.");
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[TreasureMapCapture] Failed to build rank lookup: {ex}");
        }
    }

    private void InstallHooks()
    {
        InstallActorControlHook();
        InstallShowMapHook();
        IsAvailable = actorControlSelfHook != null || showTreasureMapHook != null;
    }

    private void InstallActorControlHook()
    {
        try
        {
            var actorControlPtr = sigScanner.ScanText(ActorControlSelfSignature);
            actorControlSelfHook = gameInteropProvider.HookFromAddress<HandleActorControlSelfDelegate>(actorControlPtr, OnActorControlSelf);
            actorControlSelfHook.Enable();
            plugin.AddDebugLog("[TreasureMapCapture] ActorControlSelf treasure-map hook installed.");
        }
        catch (Exception ex)
        {
            actorControlSelfHook = null;
            Plugin.LogError($"[TreasureMapCapture] Failed to install ActorControlSelf hook: {ex}");
            plugin.AddDebugLog($"[TreasureMapCapture] ActorControlSelf hook unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void InstallShowMapHook()
    {
        try
        {
            var showMapPtr = sigScanner.ScanText(ShowTreasureMapSignature);
            showTreasureMapHook = gameInteropProvider.HookFromAddress<ShowTreasureMapDelegate>(showMapPtr, OnShowTreasureMap);
            showTreasureMapHook.Enable();
            plugin.AddDebugLog("[TreasureMapCapture] Treasure map show hook installed.");
        }
        catch (Exception ex)
        {
            showTreasureMapHook = null;
            Plugin.LogError($"[TreasureMapCapture] Failed to install treasure map show hook: {ex}");
            plugin.AddDebugLog($"[TreasureMapCapture] Hook unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private char OnActorControlSelf(long a1, long a2, nint dataPtr)
    {
        try
        {
            var packet = ParseTreasureMapPacket(dataPtr);
            if (packet != null)
                CaptureLocationForEventItem(packet.EventItemId, packet.SubRowId, packet.JustOpened ? "actor-control/opened" : "actor-control");
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[TreasureMapCapture] ActorControlSelf detour failed: {ex}");
        }

        return actorControlSelfHook?.Original(a1, a2, dataPtr) ?? '\0';
    }

    private nint OnShowTreasureMap(nint manager, ushort rowId, ushort subRowId, byte unknown)
    {
        try
        {
            CaptureLocation(rowId, subRowId, "show-map");
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[TreasureMapCapture] Show-map detour failed: {ex}");
        }

        return showTreasureMapHook?.Original(manager, rowId, subRowId, unknown) ?? nint.Zero;
    }

    private static TreasureMapPacket? ParseTreasureMapPacket(nint dataPtr)
    {
        if (dataPtr == nint.Zero)
            return null;

        var category = (uint)Marshal.ReadByte(dataPtr);
        if (category != TreasureMapsActorControlCategory)
            return null;

        var eventItemId = (uint)Marshal.ReadInt32(dataPtr, 4);
        var subRowId = (uint)Marshal.ReadInt32(dataPtr, 8);
        var justOpened = Marshal.ReadInt32(dataPtr, 12) == 1;

        if (eventItemId == 0)
            return null;

        return new TreasureMapPacket(eventItemId, (ushort)subRowId, justOpened);
    }

    private bool CaptureLocationForEventItem(uint eventItemId, ushort subRowId, string source)
    {
        if (!eventItemToRankRowId.TryGetValue(eventItemId, out var rowId))
        {
            plugin.AddDebugLog($"[TreasureMapCapture] Unknown treasure event item {eventItemId} from {source}.");
            return false;
        }

        return CaptureLocation(rowId, subRowId, source);
    }

    private bool CaptureLocation(uint rowId, ushort subRowId, string source)
    {
        if (!TryResolveLocation(rowId, subRowId, out var resolved))
            return false;

        var previous = LastResolvedLocation;
        LastResolvedLocation = resolved;

        var duplicate = previous != null
            && previous.RankRowId == resolved.RankRowId
            && previous.SubRowId == resolved.SubRowId
            && DateTime.Now - previous.CapturedAt < TimeSpan.FromSeconds(2);

        if (!duplicate)
        {
            plugin.AddDebugLog(
                $"[TreasureMapCapture] Captured {resolved.MapName} via {source} row={resolved.RankRowId}/{resolved.SubRowId}: " +
                $"{resolved.Location.ZoneName} ({resolved.Location.X:F1}, {resolved.Location.Y:F1}, {resolved.Location.Z:F1})");
        }

        if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] ||
            Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51])
        {
            return true;
        }

        GameHelpers.SetMapFlag(resolved.Location.TerritoryId, resolved.MapId, resolved.Location.X, resolved.Location.Z);
        return true;
    }

    private bool TryResolveLocation(uint rowId, ushort subRowId, out TreasureMapResolvedLocation resolved)
    {
        resolved = TreasureMapResolvedLocation.Empty;

        if (!rankByRowId.TryGetValue(rowId, out var rankInfo))
        {
            plugin.AddDebugLog($"[TreasureMapCapture] Unknown treasure rank row {rowId}.");
            return false;
        }

        var treasureSpotSheet = dataManager.GetSubrowExcelSheet<TreasureSpot>();
        if (treasureSpotSheet == null)
            return false;

        var spot = treasureSpotSheet.GetSubrowOrDefault(rowId, subRowId);
        if (spot == null)
            return false;

        var loc = spot.Value.Location.ValueNullable;
        if (loc == null)
            return false;

        var map = loc.Value.Map.ValueNullable;
        var territory = map?.TerritoryType.ValueNullable;
        if (map == null || territory == null || territory.Value.RowId == 0)
            return false;

        var zoneName = territory.Value.PlaceName.ValueNullable?.Name.ToString() ?? $"Territory {territory.Value.RowId}";
        var mapLocation = new MapLocation
        {
            TerritoryId = territory.Value.RowId,
            ZoneName = zoneName,
            X = loc.Value.X,
            Y = loc.Value.Y,
            Z = loc.Value.Z,
            IsResolved = true,
        };

        resolved = new TreasureMapResolvedLocation(
            rankInfo.MapItemId,
            rankInfo.EventItemId,
            rankInfo.RankRowId,
            subRowId,
            map.Value.RowId,
            string.IsNullOrWhiteSpace(rankInfo.MapName) ? $"Treasure map row {rowId}" : rankInfo.MapName,
            mapLocation,
            DateTime.Now);
        return true;
    }

    private sealed record TreasureRankInfo(uint RankRowId, uint MapItemId, uint EventItemId, string MapName);
    private sealed record TreasureMapPacket(uint EventItemId, ushort SubRowId, bool JustOpened);
}

public sealed record TreasureMapResolvedLocation(
    uint MapItemId,
    uint EventItemId,
    uint RankRowId,
    ushort SubRowId,
    uint MapId,
    string MapName,
    MapLocation Location,
    DateTime CapturedAt)
{
    public static TreasureMapResolvedLocation Empty { get; } = new(
        0,
        0,
        0,
        0,
        0,
        string.Empty,
        new MapLocation(),
        DateTime.MinValue);

    public MapLocation ToMapLocation()
    {
        return new MapLocation
        {
            TerritoryId = Location.TerritoryId,
            ZoneName = Location.ZoneName,
            X = Location.X,
            Y = Location.Y,
            Z = Location.Z,
            NearestAetheryteId = Location.NearestAetheryteId,
            NearestAetheryteName = Location.NearestAetheryteName,
            IsResolved = Location.IsResolved,
        };
    }
}
