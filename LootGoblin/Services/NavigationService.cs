using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace LootGoblin.Services;

public enum NavigationState
{
    Idle,
    Teleporting,
    WaitingForTeleport,
    Mounting,
    Flying,
    Arrived,
    Error,
}

public class NavigationService : IDisposable
{
    private readonly Plugin _plugin;
    private readonly IPluginLog _log;
    private readonly ICondition _condition;
    private readonly IClientState _clientState;
    private readonly IDataManager _dataManager;

    public NavigationState State { get; private set; } = NavigationState.Idle;
    public string StateDetail { get; private set; } = "";
    public Vector3 TargetPosition { get; private set; }
    public uint TargetTerritoryId { get; private set; }

    private DateTime stateStartTime;
    private float timeoutSeconds = 30f;

    public NavigationService(Plugin plugin, ICondition condition, IClientState clientState, IDataManager dataManager, IPluginLog log)
    {
        _plugin = plugin;
        _condition = condition;
        _clientState = clientState;
        _dataManager = dataManager;
        _log = log;
    }

    public void Dispose() { }

    public void TeleportToAetheryte(uint aetheryteId)
    {
        if (!_clientState.IsLoggedIn)
        {
            SetState(NavigationState.Error, "Not logged in.");
            return;
        }

        if (_condition[ConditionFlag.InCombat])
        {
            SetState(NavigationState.Error, "Cannot teleport while in combat.");
            return;
        }

        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
        {
            SetState(NavigationState.Error, "Already between areas.");
            return;
        }

        var aetheryteSheet = _dataManager.GetExcelSheet<Aetheryte>();
        var aetheryte = aetheryteSheet?.GetRow(aetheryteId);
        var name = aetheryte?.PlaceName.ValueNullable?.Name.ToString() ?? $"Aetheryte {aetheryteId}";

        _plugin.AddDebugLog($"Teleporting to {name} (ID: {aetheryteId})...");
        CommandHelper.SendCommand($"/tp {name}");

        SetState(NavigationState.Teleporting, $"Teleporting to {name}...");
    }

    public void FlyToPosition(Vector3 position)
    {
        // Re-check availability in case vnavmesh loaded after LootGoblin startup
        _plugin.VNavIPC.CheckAvailability();
        
        if (!_plugin.VNavIPC.IsAvailable)
        {
            SetState(NavigationState.Error, "vnavmesh not available.");
            return;
        }

        TargetPosition = position;
        _plugin.VNavIPC.FlyTo(position);
        SetState(NavigationState.Flying, $"Flying to {CommandHelper.FormatVector(position)}...");
    }

    public void MoveToPosition(Vector3 position)
    {
        // Re-check availability in case vnavmesh loaded after LootGoblin startup
        _plugin.VNavIPC.CheckAvailability();
        
        if (!_plugin.VNavIPC.IsAvailable)
        {
            SetState(NavigationState.Error, "vnavmesh not available.");
            return;
        }

        TargetPosition = position;
        _plugin.VNavIPC.MoveTo(position);
        SetState(NavigationState.Flying, $"Moving to {CommandHelper.FormatVector(position)}...");
    }

    public void StopNavigation()
    {
        _plugin.VNavIPC.Stop();
        // Clear any active flag to prevent pathing to old flags
        CommandHelper.SendCommand("/vnav clearflag");
        SetState(NavigationState.Idle, "Navigation stopped.");
    }

    public void MountUp()
    {
        if (_condition[ConditionFlag.Mounted])
        {
            _plugin.AddDebugLog("Already mounted.");
            return;
        }

        var selectedMount = _plugin.Configuration.SelectedMount ?? "Company Chocobo";
        string mountCommand;
        if (selectedMount == "Mount Roulette")
            mountCommand = "/generalaction \"Mount Roulette\"";
        else if (string.IsNullOrEmpty(selectedMount))
            mountCommand = "/mount \"Company Chocobo\"";
        else
            mountCommand = $"/mount \"{selectedMount}\"";
        
        _plugin.AddDebugLog($"Using mount command: {mountCommand}");
        CommandHelper.SendCommand(mountCommand);
        SetState(NavigationState.Mounting, $"Mounting {selectedMount}...");
    }

    public void FlyToFlag()
    {
        // Re-check availability in case vnavmesh loaded after LootGoblin startup
        _plugin.VNavIPC.CheckAvailability();
        
        if (!_plugin.VNavIPC.IsAvailable)
        {
            SetState(NavigationState.Error, "vnavmesh not available.");
            return;
        }

        CommandHelper.SendCommand("/vnav flyflag");
        SetState(NavigationState.Flying, "Flying to map flag...");
        _plugin.AddDebugLog("Flying to flag via vnavmesh.");
    }

    /// <summary>
    /// Find the nearest unlocked aetheryte in the given territory to the target position.
    /// Uses XYZ distance when community RealXYZ is available for the destination, XZ-only otherwise.
    /// </summary>
    /// <param name="bestAetheryteDistance">Output: distance from the best aetheryte to the destination (for comparison with player distance)</param>
    /// <param name="usedXyzComparison">Output: whether full XYZ comparison was used (true) or XZ-only (false)</param>
    public unsafe uint FindNearestAetheryte(uint territoryId, Vector3 targetPosition, out double bestAetheryteDistance, out bool usedXyzComparison)
    {
        bestAetheryteDistance = double.MaxValue;
        usedXyzComparison = false;

        try
        {
            var telepo = Telepo.Instance();
            if (telepo == null) return 0;

            telepo->UpdateAetheryteList();
            var count = telepo->TeleportList.Count;
            if (count == 0) return 0;

            var aetheryteSheet = _dataManager.GetExcelSheet<Aetheryte>();
            if (aetheryteSheet == null) return 0;

            _plugin.AddDebugLog($"[Aetheryte] Searching territory {territoryId}, target=({targetPosition.X:F1}, {targetPosition.Y:F1}, {targetPosition.Z:F1}), teleport list count={count}");

            // Get Map data for coordinate conversion
            float sizeFactor = 100f;
            float offsetX = 0f, offsetY = 0f;
            uint mapId = 0;
            try
            {
                var territoryTypeSheet = _dataManager.GetExcelSheet<TerritoryType>();
                if (territoryTypeSheet != null)
                {
                    var territory = territoryTypeSheet.GetRow(territoryId);
                    var mapRow = territory.Map.Value;
                    mapId = territory.Map.RowId;
                    sizeFactor = mapRow.SizeFactor;
                    offsetX = mapRow.OffsetX;
                    offsetY = mapRow.OffsetY;
                    _plugin.AddDebugLog($"[Aetheryte] Map: ID={mapId} SizeFactor={sizeFactor} Offset=({offsetX},{offsetY})");
                }
            }
            catch (Exception ex)
            {
                _plugin.AddDebugLog($"[Aetheryte] Map lookup failed: {ex.GetType().Name}: {ex.Message}");
            }

            // Get Level sheet for direct lookup
            var levelSheet = _dataManager.GetExcelSheet<Level>();

            // Collect all candidate aetherytes in the target territory
            var candidates = new System.Collections.Generic.List<(uint Id, string Name, uint Cost, Vector3 WorldPos)>();

            for (int i = 0; i < count; i++)
            {
                var entry = telepo->TeleportList[i];
                if (entry.AetheryteId == 0) continue;

                var aetheryte = aetheryteSheet.GetRow(entry.AetheryteId);

                if (aetheryte.Territory.RowId != territoryId) continue;

                var name = aetheryte.PlaceName.ValueNullable?.Name.ToString() ?? $"ID {entry.AetheryteId}";

                // Method 1a: Level via RowRef ValueNullable
                var worldPos = Vector3.Zero;
                try
                {
                    int levelIdx = 0;
                    foreach (var lvl in aetheryte.Level)
                    {
                        var levelRowId = lvl.RowId;
                        _plugin.AddDebugLog($"  [Level] {name}: [{levelIdx}] RowId={levelRowId}");

                        // Try ValueNullable first
                        var levelRow = lvl.ValueNullable;
                        if (levelRow != null)
                        {
                            var lx = levelRow.Value.X;
                            var ly = levelRow.Value.Y;
                            var lz = levelRow.Value.Z;
                            if (lx != 0 || lz != 0)
                            {
                                worldPos = new Vector3(lx, ly, lz);
                                _plugin.AddDebugLog($"  [Level] {name}: ValueNullable OK ({lx:F1}, {ly:F1}, {lz:F1})");
                                break;
                            }
                            else
                            {
                                _plugin.AddDebugLog($"  [Level] {name}: ValueNullable returned zero coords");
                            }
                        }
                        else
                        {
                            _plugin.AddDebugLog($"  [Level] {name}: ValueNullable returned null");

                            // Method 1b: Direct Level sheet lookup by RowId
                            if (levelSheet != null && levelRowId > 0)
                            {
                                try
                                {
                                    var directLevel = levelSheet.GetRow(levelRowId);
                                    var dlx = directLevel.X;
                                    var dly = directLevel.Y;
                                    var dlz = directLevel.Z;
                                    if (dlx != 0 || dlz != 0)
                                    {
                                        worldPos = new Vector3(dlx, dly, dlz);
                                        _plugin.AddDebugLog($"  [Level] {name}: Direct lookup OK ({dlx:F1}, {dly:F1}, {dlz:F1})");
                                        break;
                                    }
                                    else
                                    {
                                        _plugin.AddDebugLog($"  [Level] {name}: Direct lookup returned zero coords");
                                    }
                                }
                                catch (Exception dex)
                                {
                                    _plugin.AddDebugLog($"  [Level] {name}: Direct lookup EXCEPTION: {dex.GetType().Name}: {dex.Message}");
                                }
                            }
                        }
                        levelIdx++;
                    }
                    if (levelIdx == 0)
                        _plugin.AddDebugLog($"  [Level] {name}: Level collection EMPTY (0 entries)");
                }
                catch (Exception ex)
                {
                    _plugin.AddDebugLog($"  [Level] {name}: ITERATION EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                }

                candidates.Add((entry.AetheryteId, name, entry.GilCost, worldPos));
            }

            if (candidates.Count == 0)
            {
                _plugin.AddDebugLog($"[Aetheryte] No unlocked aetheryte found for territory {territoryId}.");
                return 0;
            }

            // Method 1c: AetherytePositionDatabase - stored positions from previous teleport arrivals
            if (_plugin.AetherytePositionDatabase != null && candidates.Any(c => c.WorldPos == Vector3.Zero))
            {
                int dbHits = 0;
                for (int ci = 0; ci < candidates.Count; ci++)
                {
                    if (candidates[ci].WorldPos != Vector3.Zero) continue;
                    var storedPos = _plugin.AetherytePositionDatabase.GetPosition(candidates[ci].Id);
                    if (storedPos != null)
                    {
                        candidates[ci] = (candidates[ci].Id, candidates[ci].Name, candidates[ci].Cost,
                            new Vector3(storedPos.X, storedPos.Y, storedPos.Z));
                        _plugin.AddDebugLog($"  [AetheryteDB] {candidates[ci].Name}: stored pos ({storedPos.X:F1}, {storedPos.Y:F1}, {storedPos.Z:F1})");
                        dbHits++;
                    }
                }
                if (dbHits > 0)
                    _plugin.AddDebugLog($"[Aetheryte] AetheryteDB resolved {dbHits}/{candidates.Count(c => c.WorldPos == Vector3.Zero) + dbHits} missing positions");
            }

            // Method 2: MapMarker fallback for candidates with no position
            // MapMarker DataKey does NOT match Aetheryte RowId — match by name or by collecting
            // all aetheryte-type markers and assigning to nearest candidate
            if (mapId > 0 && candidates.Any(c => c.WorldPos == Vector3.Zero))
            {
                try
                {
                    var mapMarkerSheet = _dataManager.GetSubrowExcelSheet<MapMarker>();

                    // Collect ALL aetheryte-type markers (DataType 3=aetheryte, 4=aethernet)
                    var aetheryteMarkers = new System.Collections.Generic.List<(int X, int Y, byte DataType, uint DataKeyId)>();
                    int totalMarkers = 0;

                    for (ushort subIdx = 0; subIdx < 500; subIdx++)
                    {
                        var marker = mapMarkerSheet.GetSubrowOrDefault(mapId, subIdx);
                        if (marker == null) break;
                        totalMarkers++;

                        var mDataType = marker.Value.DataType;
                        var mDataKey = marker.Value.DataKey.RowId;
                        var mX = marker.Value.X;
                        var mY = marker.Value.Y;

                        // Log first 10 markers for diagnostic purposes
                        if (subIdx < 10)
                            _plugin.AddDebugLog($"  [MapMarker] [{subIdx}] DataType={mDataType} DataKey={mDataKey} X={mX} Y={mY}");

                        // Collect aetheryte markers (DataType 3 or 4)
                        if (mDataType == 3 || mDataType == 4)
                        {
                            aetheryteMarkers.Add((mX, mY, mDataType, mDataKey));
                        }
                    }

                    _plugin.AddDebugLog($"[Aetheryte] MapMarker: {totalMarkers} total markers, {aetheryteMarkers.Count} aetheryte-type markers for map {mapId}");

                    if (aetheryteMarkers.Count > 0)
                    {
                        float scaleFactor = sizeFactor / 100.0f;

                        // Convert all aetheryte markers to world positions
                        var markerWorldPositions = new System.Collections.Generic.List<Vector3>();
                        foreach (var m in aetheryteMarkers)
                        {
                            float worldX = ((float)m.X / scaleFactor - 1024.0f) / scaleFactor + offsetX;
                            float worldZ = ((float)m.Y / scaleFactor - 1024.0f) / scaleFactor + offsetY;
                            markerWorldPositions.Add(new Vector3(worldX, 0, worldZ));
                            _plugin.AddDebugLog($"  [MapMarker] Aetheryte marker: raw=({m.X},{m.Y}) DataType={m.DataType} DataKey={m.DataKeyId} → world=({worldX:F1}, 0, {worldZ:F1})");
                        }

                        // Build a DataKey→CandidateIndex lookup using both AetheryteId and PlaceName.RowId
                        var dataKeyToCandidateIdx = new System.Collections.Generic.Dictionary<uint, int>();
                        for (int ci = 0; ci < candidates.Count; ci++)
                        {
                            if (candidates[ci].WorldPos != Vector3.Zero) continue;
                            // Match by AetheryteId
                            dataKeyToCandidateIdx[candidates[ci].Id] = ci;
                            // Match by PlaceName.RowId (MapMarker DataKey may reference PlaceName, not Aetheryte)
                            try
                            {
                                var aethRow = aetheryteSheet.GetRow(candidates[ci].Id);
                                var placeNameRowId = aethRow.PlaceName.RowId;
                                if (placeNameRowId > 0 && !dataKeyToCandidateIdx.ContainsKey(placeNameRowId))
                                    dataKeyToCandidateIdx[placeNameRowId] = ci;
                                _plugin.AddDebugLog($"  [MapMarker] Candidate {candidates[ci].Name}: AetheryteId={candidates[ci].Id}, PlaceNameRowId={placeNameRowId}");
                            }
                            catch { }
                        }

                        // Try ID-based matching first (DataKey → candidate)
                        int matchedCount = 0;
                        for (int mi = 0; mi < aetheryteMarkers.Count; mi++)
                        {
                            var dataKeyId = aetheryteMarkers[mi].DataKeyId;
                            if (dataKeyToCandidateIdx.TryGetValue(dataKeyId, out var ci) && candidates[ci].WorldPos == Vector3.Zero)
                            {
                                candidates[ci] = (candidates[ci].Id, candidates[ci].Name, candidates[ci].Cost, markerWorldPositions[mi]);
                                _plugin.AddDebugLog($"  [MapMarker] ID-matched marker [{mi}] (DataKey={dataKeyId}) → {candidates[ci].Name} at ({markerWorldPositions[mi].X:F1}, 0, {markerWorldPositions[mi].Z:F1})");
                                matchedCount++;
                            }
                        }

                        // If ID matching failed, find the closest marker to target and short-circuit
                        if (matchedCount == 0 && targetPosition != default)
                        {
                            _plugin.AddDebugLog($"[Aetheryte] MapMarker: ID matching failed for all {aetheryteMarkers.Count} markers. Using closest-marker direct selection.");
                            int closestMarkerIdx = 0;
                            double closestDist = double.MaxValue;
                            for (int mi = 0; mi < markerWorldPositions.Count; mi++)
                            {
                                var dx = markerWorldPositions[mi].X - targetPosition.X;
                                var dz = markerWorldPositions[mi].Z - targetPosition.Z;
                                var dist = dx * dx + dz * dz;
                                if (dist < closestDist) { closestDist = dist; closestMarkerIdx = mi; }
                            }

                            // Find the closest marker to target, then find the farthest marker from it
                            // The candidate at the FARTHEST marker from the closest marker is NOT the one we want
                            // The candidate at the CLOSEST marker IS what we want
                            // Since we can't match marker→candidate by ID, pick candidate whose name 
                            // appears later in the aetheryte list (further from starting city = likely farther aetheryte)
                            // BUT actually, we can try a different approach: assign markers in sheet order
                            // and candidates in teleport list order (which tends to match geographic order)

                            // Fallback: just assign markers sequentially to candidates
                            var unassignedCandidates = new System.Collections.Generic.List<int>();
                            for (int ci = 0; ci < candidates.Count; ci++)
                                if (candidates[ci].WorldPos == Vector3.Zero) unassignedCandidates.Add(ci);

                            for (int mi = 0; mi < markerWorldPositions.Count && mi < unassignedCandidates.Count; mi++)
                            {
                                var ci = unassignedCandidates[mi];
                                candidates[ci] = (candidates[ci].Id, candidates[ci].Name, candidates[ci].Cost, markerWorldPositions[mi]);
                                _plugin.AddDebugLog($"  [MapMarker] Sequential assign marker [{mi}] → {candidates[ci].Name} at ({markerWorldPositions[mi].X:F1}, 0, {markerWorldPositions[mi].Z:F1})");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _plugin.AddDebugLog($"[Aetheryte] MapMarker fallback EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Method 3: Check MapLocationDatabase for aetheryte name override
            if (_plugin.MapLocationDatabase != null && targetPosition != default)
            {
                var dbEntry = _plugin.MapLocationDatabase.FindEntry(territoryId, targetPosition.X, targetPosition.Z);
                if (dbEntry != null && !string.IsNullOrEmpty(dbEntry.AetheryteName))
                {
                    var overrideCandidate = candidates.FirstOrDefault(c =>
                        string.Equals(c.Name, dbEntry.AetheryteName, StringComparison.OrdinalIgnoreCase));
                    if (overrideCandidate.Id != 0)
                    {
                        _plugin.AddDebugLog($"[Aetheryte] DB override: using {dbEntry.AetheryteName} (ID: {overrideCandidate.Id})");
                        return overrideCandidate.Id;
                    }
                }
            }

            // Check MapLocationDatabase for community RealXYZ of the destination
            Vector3 realDestination = default;
            bool hasRealDestY = false;
            if (_plugin.MapLocationDatabase != null && targetPosition != default)
            {
                var dbEntry = _plugin.MapLocationDatabase.FindEntry(territoryId, targetPosition.X, targetPosition.Z);
                if (dbEntry != null && dbEntry.HasRealXYZ)
                {
                    realDestination = new Vector3(dbEntry.RealX, dbEntry.RealY, dbEntry.RealZ);
                    hasRealDestY = true;
                    _plugin.AddDebugLog($"[Aetheryte] Community RealXYZ found for destination: ({dbEntry.RealX:F1}, {dbEntry.RealY:F1}, {dbEntry.RealZ:F1}) - using XYZ comparison");
                }
                else
                {
                    _plugin.AddDebugLog($"[Aetheryte] No community RealXYZ for destination - using XZ-only comparison");
                }
            }

            usedXyzComparison = hasRealDestY;

            // The comparison target: use RealXYZ if available, otherwise use flag XZ
            var compTarget = hasRealDestY ? realDestination : targetPosition;

            // Log all candidates with final positions
            foreach (var c in candidates)
            {
                var posStr = c.WorldPos != Vector3.Zero ? $"({c.WorldPos.X:F1}, {c.WorldPos.Y:F1}, {c.WorldPos.Z:F1})" : "NO_POS";
                // Determine if this aetheryte has a real Y value (from AetheryteDB, not MapMarker which sets Y=0)
                var hasRealAethY = c.WorldPos != Vector3.Zero && _plugin.AetherytePositionDatabase != null && _plugin.AetherytePositionDatabase.HasPosition(c.Id);
                var distMode = (hasRealDestY && hasRealAethY) ? "XYZ" : "XZ";
                double dist;
                if (hasRealDestY && hasRealAethY)
                {
                    var dx = c.WorldPos.X - compTarget.X;
                    var dy = c.WorldPos.Y - compTarget.Y;
                    var dz = c.WorldPos.Z - compTarget.Z;
                    dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                }
                else if (c.WorldPos != Vector3.Zero)
                {
                    var dx = c.WorldPos.X - compTarget.X;
                    var dz = c.WorldPos.Z - compTarget.Z;
                    dist = Math.Sqrt(dx * dx + dz * dz);
                }
                else
                {
                    dist = double.MaxValue;
                }
                _plugin.AddDebugLog($"  [Candidate] {c.Name} (ID: {c.Id}, Cost: {c.Cost}g, Pos: {posStr}, {distMode} dist: {dist:F0}y)");
            }

            uint bestId;
            string bestName;

            // Pick closest to target if we have positions and a target
            if (targetPosition != default && candidates.Any(c => c.WorldPos != Vector3.Zero))
            {
                var closest = candidates
                    .Where(c => c.WorldPos != Vector3.Zero)
                    .OrderBy(c => {
                        // Use full XYZ if we have real Y for BOTH aetheryte and destination
                        var hasRealAethY = _plugin.AetherytePositionDatabase != null && _plugin.AetherytePositionDatabase.HasPosition(c.Id);
                        if (hasRealDestY && hasRealAethY)
                        {
                            var dx = c.WorldPos.X - compTarget.X;
                            var dy = c.WorldPos.Y - compTarget.Y;
                            var dz = c.WorldPos.Z - compTarget.Z;
                            return dx * dx + dy * dy + dz * dz;
                        }
                        else
                        {
                            var dx = c.WorldPos.X - compTarget.X;
                            var dz = c.WorldPos.Z - compTarget.Z;
                            return dx * dx + dz * dz;
                        }
                    })
                    .First();
                bestId = closest.Id;
                bestName = closest.Name;

                // Compute distance for the winner using same rules
                var bestHasRealY = _plugin.AetherytePositionDatabase != null && _plugin.AetherytePositionDatabase.HasPosition(closest.Id);
                if (hasRealDestY && bestHasRealY)
                {
                    var dx = closest.WorldPos.X - compTarget.X;
                    var dy = closest.WorldPos.Y - compTarget.Y;
                    var dz = closest.WorldPos.Z - compTarget.Z;
                    bestAetheryteDistance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    _plugin.AddDebugLog($"[Aetheryte] Selected closest (XYZ): {bestName} (ID: {bestId}, dist: {bestAetheryteDistance:F0}y)");
                }
                else
                {
                    var dx = closest.WorldPos.X - compTarget.X;
                    var dz = closest.WorldPos.Z - compTarget.Z;
                    bestAetheryteDistance = Math.Sqrt(dx * dx + dz * dz);
                    _plugin.AddDebugLog($"[Aetheryte] Selected closest (XZ): {bestName} (ID: {bestId}, dist: {bestAetheryteDistance:F0}y)");
                }
            }
            else
            {
                // Fallback: cheapest cost
                var cheapest = candidates.OrderBy(c => c.Cost).First();
                bestId = cheapest.Id;
                bestName = cheapest.Name;
                bestAetheryteDistance = double.MaxValue;
                _plugin.AddDebugLog($"[Aetheryte] FALLBACK cheapest: {bestName} (ID: {bestId}, Cost: {cheapest.Cost}g) [no position data]");
            }

            return bestId;
        }
        catch (Exception ex)
        {
            _log.Error($"Error finding nearest aetheryte: {ex.Message}");
            _plugin.AddDebugLog($"[Aetheryte] FATAL EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            bestAetheryteDistance = double.MaxValue;
            usedXyzComparison = false;
            return 0;
        }
    }

    public bool IsTeleporting()
    {
        return _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];
    }

    public bool IsMounted()
    {
        return _condition[ConditionFlag.Mounted];
    }

    public bool IsInCombat()
    {
        return _condition[ConditionFlag.InCombat];
    }

    public bool IsFlying()
    {
        return _condition[ConditionFlag.InFlight] || _condition[ConditionFlag.Diving];
    }

    /// <summary>Get estimated aetheryte position from Level sheet or MapMarker (no user data).</summary>
    public unsafe Vector3 GetEstimatedAetherytePosition(uint aetheryteId)
    {
        try
        {
            var aetheryteSheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
            if (aetheryteSheet == null) return Vector3.Zero;

            var aetheryte = aetheryteSheet.GetRow(aetheryteId);
            var name = aetheryte.PlaceName.ValueNullable?.Name.ToString() ?? $"Aetheryte {aetheryteId}";

            // Get position from Level sheet (X,Z coordinates)
            int levelIdx = 0;
            foreach (var lvl in aetheryte.Level)
            {
                var levelRowId = lvl.RowId;
                _plugin.AddDebugLog($"[Aetheryte] {name}: Level[{levelIdx}] RowId={levelRowId}");

                // Try direct access instead of ValueNullable
                try
                {
                    var levelRow = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Level>()?.GetRow(lvl.RowId);
                    if (levelRow != null)
                {
                        var lx = levelRow.Value.X;
                        var ly = levelRow.Value.Y;
                        var lz = levelRow.Value.Z;
                        
                        _plugin.AddDebugLog($"[Aetheryte] {name}: Level coords X={lx}, Y={ly}, Z={lz}");
                        
                        // Use Level sheet X,Z with Y=0 for distance check
                        if (lx != 0 || lz != 0)
                        {
                            _plugin.AddDebugLog($"[Aetheryte] {name} Level pos: X={lx}, Z={lz}");
                            return new Vector3(lx, 0f, lz);
                        }
                        else
                        {
                            _plugin.AddDebugLog($"[Aetheryte] {name}: Level returned zero coords");
                        }
                    }
                    else
                    {
                        _plugin.AddDebugLog($"[Aetheryte] {name}: Direct Level access returned null");
                    }
                }
                catch (Exception ex)
                {
                    _plugin.AddDebugLog($"[Aetheryte] {name}: Direct Level access failed: {ex.Message}");
                }
                levelIdx++;
            }

            // Fallback: use zero position - this will never trigger recording but prevents crashes
            _plugin.AddDebugLog($"[Aetheryte] {name} - no position data available");
            return Vector3.Zero;
        }
        catch (Exception ex)
        {
            _plugin.AddDebugLog($"[Aetheryte] GetEstimatedPosition failed for ID {aetheryteId}: {ex.Message}");
        }

        return Vector3.Zero;
    }

    private void SetState(NavigationState state, string detail)
    {
        State = state;
        StateDetail = detail;
        stateStartTime = DateTime.Now;
        _plugin.AddDebugLog($"Nav state: {state} - {detail}");
    }
}
