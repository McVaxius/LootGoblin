using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace LootGoblin.Services;

/// <summary>
/// Two-file map location database system:
///   [1] CommunityMapLocations.json - Downloaded from GitHub, contains community-contributed RealXYZ + AetheryteName.
///       Updated via "Download Updated Locs" button or auto-update on login.
///   [2] UserMapLocations.json - Generated from the user's own gameplay. Never overwritten by downloads.
///
/// Lookup order: community file first, then user file. First valid RealXYZ wins.
/// Recording: always writes to user file.
///
/// TreasureSpot data (all possible dig locations) is read from game data at startup.
/// This provides the master list of known flag positions. Community/user files augment with RealXYZ.
///
/// MAINTAINING THIS DATABASE:
///   To update the community file with new map locations added to the game:
///   1. The plugin reads TreasureSpot sheet at startup (same data source as GlobeTrotter plugin)
///   2. TreasureSpot contains ALL possible dig locations for every treasure map type
///   3. When new maps are added to FFXIV, TreasureSpot auto-updates with game patches
///   4. Run the plugin after a game update, it will detect new TreasureSpot entries
///   5. Play maps to record RealXYZ, then share UserMapLocations.json entries as PRs
///   6. Merge verified RealXYZ into CommunityMapLocations.json on GitHub
/// </summary>
public class MapLocationDatabase
{
    private readonly Plugin _plugin;
    private readonly IPluginLog _log;
    private readonly string _communityFilePath;
    private readonly string _userFilePath;
    private readonly string _legacyFilePath;

    private List<MapLocationEntry> _communityEntries = new();
    private List<MapLocationEntry> _userEntries = new();
    private List<MapLocationEntry> _treasureSpotEntries = new();

    // GitHub raw URL for community data file
    //public const string CommunityDataUrl = "https://raw.githubusercontent.com/McVaxius/LootGoblin/master/LootGoblin/data/CommunityMapLocations.json";
    public const string CommunityDataUrl = "https://raw.githubusercontent.com/McVaxius/LootGoblin/refs/heads/master/LootGoblin/data/CommunityMapLocations.json";

    public bool IsDownloading { get; private set; }
    public string? LastDownloadResult { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MapLocationDatabase(Plugin plugin, IPluginLog log)
    {
        _plugin = plugin;
        _log = log;
        var configDir = Plugin.PluginInterface.GetPluginConfigDirectory();
        _communityFilePath = Path.Combine(configDir, "CommunityMapLocations.json");
        _userFilePath = Path.Combine(configDir, "UserMapLocations.json");
        _legacyFilePath = Path.Combine(configDir, "MapLocations.json");
        MigrateLegacyFile();
        LoadCommunity();
        AssignIndices();
        LoadUser();
    }

    /// <summary>All community entries (downloaded from GitHub).</summary>
    public IReadOnlyList<MapLocationEntry> CommunityEntries => _communityEntries.AsReadOnly();

    /// <summary>All user-recorded entries (from gameplay).</summary>
    public IReadOnlyList<MapLocationEntry> UserEntries => _userEntries.AsReadOnly();

    /// <summary>All TreasureSpot entries (from game data, no RealXYZ).</summary>
    public IReadOnlyList<MapLocationEntry> TreasureSpotEntries => _treasureSpotEntries.AsReadOnly();

    /// <summary>Total unique locations across all sources.</summary>
    public int TotalLocations => GetAllMerged().Count;

    /// <summary>Locations with valid RealXYZ from any source.</summary>
    public int ResolvedLocations => GetAllMerged().Count(e => e.HasRealXYZ);

    /// <summary>User entries that have RealXYZ not present in community file (shareable).</summary>
    public int UserOnlyResolved => _userEntries.Count(u => u.HasRealXYZ &&
        !_communityEntries.Any(c => c.HasRealXYZ && c.TerritoryId == u.TerritoryId &&
            Math.Sqrt(Math.Pow(c.FlagX - u.FlagX, 2) + Math.Pow(c.FlagZ - u.FlagZ, 2)) <= 10.0));

    /// <summary>
    /// Assign indices to community entries for easy identification.
    /// </summary>
    private void AssignIndices()
    {
        // Only assign indices to community entries (preserves user data)
        for (int i = 0; i < _communityEntries.Count; i++)
        {
            _communityEntries[i].Index = i + 1; // 1-based indexing
        }
        
        _log.Information($"[MapLocDB] Assigned indices to {_communityEntries.Count} community entries");
    }

    /// <summary>
    /// Look up a stored entry for a given territory + flag position.
    /// Checks community first, then user. Returns first match with valid RealXYZ,
    /// or first match without RealXYZ if none have it.
    /// </summary>
    public MapLocationEntry? FindEntry(uint territoryId, float flagX, float flagZ)
    {
        MapLocationEntry? bestNoReal = null;

        // Check community entries first
        foreach (var entry in _communityEntries)
        {
            if (entry.TerritoryId != territoryId) continue;
            var dx = entry.FlagX - flagX;
            var dz = entry.FlagZ - flagZ;
            var xzDist = Math.Sqrt(dx * dx + dz * dz);
            if (xzDist <= 10.0)
            {
                if (entry.HasRealXYZ)
                {
                    _plugin.AddDebugLog($"[MapLocDB] Community hit: {entry.ZoneName} real=({entry.RealX:F1},{entry.RealY:F1},{entry.RealZ:F1}) dist={xzDist:F1}y");
                    return entry;
                }
                bestNoReal ??= entry;
            }
        }

        // Check user entries
        foreach (var entry in _userEntries)
        {
            if (entry.TerritoryId != territoryId) continue;
            var dx = entry.FlagX - flagX;
            var dz = entry.FlagZ - flagZ;
            var xzDist = Math.Sqrt(dx * dx + dz * dz);
            if (xzDist <= 10.0)
            {
                if (entry.HasRealXYZ)
                {
                    _plugin.AddDebugLog($"[MapLocDB] User hit: {entry.ZoneName} real=({entry.RealX:F1},{entry.RealY:F1},{entry.RealZ:F1}) dist={xzDist:F1}y");
                    return entry;
                }
                bestNoReal ??= entry;
            }
        }

        if (bestNoReal != null)
            _plugin.AddDebugLog($"[MapLocDB] Found entry but no RealXYZ: {bestNoReal.ZoneName} flag=({bestNoReal.FlagX:F1},{bestNoReal.FlagZ:F1})");

        return bestNoReal;
    }

    /// <summary>
    /// Record a successful dig location to the USER file.
    /// Updates existing entry if found, otherwise adds new.
    /// </summary>
    public void RecordLocation(uint territoryId, string zoneName, string mapName, float flagX, float flagY, float flagZ, float realX, float realY, float realZ)
    {
        // Check if user file already has a valid entry
        foreach (var entry in _userEntries)
        {
            if (entry.TerritoryId != territoryId) continue;
            var dx = entry.FlagX - flagX;
            var dz = entry.FlagZ - flagZ;
            if (Math.Sqrt(dx * dx + dz * dz) <= 10.0)
            {
                if (entry.HasRealXYZ)
                {
                    _plugin.AddDebugLog($"[MapLocDB] User file already has entry, skipping");
                    return;
                }
                // Update existing entry with RealXYZ
                entry.RealX = (float)Math.Round(realX, 1);
                entry.RealY = (float)Math.Round(realY, 1);
                entry.RealZ = (float)Math.Round(realZ, 1);
                entry.HasRealXYZ = true;
                entry.RecordedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                SaveUser();
                _plugin.AddDebugLog($"[MapLocDB] Updated user entry with RealXYZ: real=({realX:F1},{realY:F1},{realZ:F1})");
                return;
            }
        }

        var newEntry = new MapLocationEntry
        {
            TerritoryId = territoryId,
            ZoneName = zoneName,
            MapName = mapName,
            FlagX = (float)Math.Round(flagX, 1),
            FlagY = (float)Math.Round(flagY, 1),
            FlagZ = (float)Math.Round(flagZ, 1),
            RealX = (float)Math.Round(realX, 1),
            RealY = (float)Math.Round(realY, 1),
            RealZ = (float)Math.Round(realZ, 1),
            HasRealXYZ = true,
            RecordedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        _userEntries.Add(newEntry);
        SaveUser();
        _plugin.AddDebugLog($"[MapLocDB] Recorded user location: {zoneName} T{territoryId} flag=({flagX:F1},{flagZ:F1}) real=({realX:F1},{realY:F1},{realZ:F1}) [user total: {_userEntries.Count}]");
    }

    /// <summary>
    /// Read all possible dig locations from TreasureSpot game data (same source as GlobeTrotter).
    /// Populates _treasureSpotEntries with flag positions for all map types.
    /// </summary>
    public void PopulateFromTreasureSpot(IDataManager dataManager)
    {
        try
        {
            var treasureHuntRankSheet = dataManager.GetExcelSheet<TreasureHuntRank>();
            if (treasureHuntRankSheet == null) return;

            var treasureSpotSheet = dataManager.GetSubrowExcelSheet<TreasureSpot>();
            if (treasureSpotSheet == null) return;

            int totalSpots = 0;
            int newSpots = 0;

            foreach (var rank in treasureHuntRankSheet)
            {
                var unopened = rank.ItemName.ValueNullable;
                if (unopened == null || unopened.Value.RowId == 0) continue;

                var mapItemName = unopened.Value.Name.ToString();
                if (string.IsNullOrEmpty(mapItemName)) continue;

                // Iterate subrows for this rank using GetSubrowOrDefault
                try
                {
                    for (ushort subRowId = 0; subRowId < 200; subRowId++)
                    {
                        var spot = treasureSpotSheet.GetSubrowOrDefault(rank.RowId, subRowId);
                        if (spot == null) break;

                        var loc = spot.Value.Location.ValueNullable;
                        if (loc == null) continue;

                        var map = loc.Value.Map.ValueNullable;
                        if (map == null) continue;

                        var terr = map.Value.TerritoryType.ValueNullable;
                        if (terr == null || terr.Value.RowId == 0) continue;

                        var worldX = loc.Value.X;
                        var worldZ = loc.Value.Z;
                        if (worldX == 0 && worldZ == 0) continue;

                        totalSpots++;

                        var territoryId = terr.Value.RowId;
                        var zoneName = terr.Value.PlaceName.ValueNullable?.Name.ToString() ?? $"Territory {territoryId}";

                        // Check if already in community or user entries
                        bool alreadyKnown = false;
                        foreach (var existing in _communityEntries.Concat(_userEntries))
                        {
                            if (existing.TerritoryId != territoryId) continue;
                            var dx = existing.FlagX - worldX;
                            var dz = existing.FlagZ - worldZ;
                            if (Math.Sqrt(dx * dx + dz * dz) <= 10.0)
                            {
                                alreadyKnown = true;
                                break;
                            }
                        }

                        if (!alreadyKnown)
                        {
                            newSpots++;
                            _treasureSpotEntries.Add(new MapLocationEntry
                            {
                                TerritoryId = territoryId,
                                ZoneName = zoneName,
                                MapName = mapItemName,
                                FlagX = (float)Math.Round(worldX, 1),
                                FlagY = (float)Math.Round(loc.Value.Y, 1),
                                FlagZ = (float)Math.Round(worldZ, 1),
                                HasRealXYZ = false,
                                Source = "TreasureSpot",
                            });
                        }
                    }
                }
                catch { }
            }

            _plugin.AddDebugLog($"[MapLocDB] TreasureSpot: {totalSpots} spots found, {newSpots} new (not in community/user files)");
        }
        catch (Exception ex)
        {
            _log.Error($"TreasureSpot population failed: {ex.Message}");
            _plugin.AddDebugLog($"[MapLocDB] TreasureSpot failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Download community data from GitHub. Only updates entries where local RealXYZ is missing.
    /// </summary>
    public async Task DownloadCommunityDataAsync()
    {
        if (IsDownloading) return;
        IsDownloading = true;
        LastDownloadResult = "Downloading...";

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var json = await client.GetStringAsync(CommunityDataUrl);
            var remoteEntries = JsonSerializer.Deserialize<List<MapLocationEntry>>(json, JsonOptions);

            if (remoteEntries == null || remoteEntries.Count == 0)
            {
                LastDownloadResult = "No data received";
                _plugin.AddDebugLog("[MapLocDB] Download: no data received");
                return;
            }

            int updated = 0;
            int added = 0;

            foreach (var remote in remoteEntries)
            {
                bool found = false;
                for (int i = 0; i < _communityEntries.Count; i++)
                {
                    var local = _communityEntries[i];
                    if (local.TerritoryId != remote.TerritoryId) continue;
                    var dx = local.FlagX - remote.FlagX;
                    var dz = local.FlagZ - remote.FlagZ;
                    if (Math.Sqrt(dx * dx + dz * dz) <= 10.0)
                    {
                        found = true;
                        // Only update if local is missing data that remote has
                        if (!local.HasRealXYZ && remote.HasRealXYZ)
                        {
                            local.RealX = remote.RealX;
                            local.RealY = remote.RealY;
                            local.RealZ = remote.RealZ;
                            local.HasRealXYZ = true;
                            updated++;
                        }
                        if (string.IsNullOrEmpty(local.AetheryteName) && !string.IsNullOrEmpty(remote.AetheryteName))
                        {
                            local.AetheryteName = remote.AetheryteName;
                        }
                        break;
                    }
                }

                if (!found)
                {
                    _communityEntries.Add(remote);
                    added++;
                }
            }

            SaveCommunity();
            LastDownloadResult = $"OK: +{added} new, {updated} updated RealXYZ (total: {_communityEntries.Count})";
            _plugin.AddDebugLog($"[MapLocDB] Download complete: {added} added, {updated} RealXYZ updated, total {_communityEntries.Count}");
        }
        catch (Exception ex)
        {
            LastDownloadResult = $"Error: {ex.Message}";
            _plugin.AddDebugLog($"[MapLocDB] Download failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsDownloading = false;
        }
    }

    /// <summary>Get merged list of all entries (community + user + treasurespot, deduplicated).</summary>
    public List<MapLocationEntry> GetAllMerged()
    {
        var merged = new List<MapLocationEntry>();

        // Add community entries first (highest priority)
        merged.AddRange(_communityEntries);

        // Add user entries not already covered
        foreach (var user in _userEntries)
        {
            bool covered = merged.Any(m => m.TerritoryId == user.TerritoryId &&
                Math.Sqrt(Math.Pow(m.FlagX - user.FlagX, 2) + Math.Pow(m.FlagZ - user.FlagZ, 2)) <= 10.0);
            if (!covered)
                merged.Add(user);
        }

        // Add TreasureSpot entries not already covered
        foreach (var ts in _treasureSpotEntries)
        {
            bool covered = merged.Any(m => m.TerritoryId == ts.TerritoryId &&
                Math.Sqrt(Math.Pow(m.FlagX - ts.FlagX, 2) + Math.Pow(m.FlagZ - ts.FlagZ, 2)) <= 10.0);
            if (!covered)
                merged.Add(ts);
        }

        return merged;
    }

    /// <summary>Get stats grouped by zone name.</summary>
    public Dictionary<string, (int Total, int Resolved, int UserOnly)> GetZoneStats()
    {
        var all = GetAllMerged();
        var stats = new Dictionary<string, (int Total, int Resolved, int UserOnly)>();

        foreach (var group in all.GroupBy(e => e.ZoneName))
        {
            var total = group.Count();
            var resolved = group.Count(e => e.HasRealXYZ);
            var userOnly = group.Count(e => e.HasRealXYZ &&
                _userEntries.Any(u => u.HasRealXYZ && u.TerritoryId == e.TerritoryId &&
                    Math.Sqrt(Math.Pow(u.FlagX - e.FlagX, 2) + Math.Pow(u.FlagZ - e.FlagZ, 2)) <= 10.0) &&
                !_communityEntries.Any(c => c.HasRealXYZ && c.TerritoryId == e.TerritoryId &&
                    Math.Sqrt(Math.Pow(c.FlagX - e.FlagX, 2) + Math.Pow(c.FlagZ - e.FlagZ, 2)) <= 10.0));
            stats[group.Key] = (total, resolved, userOnly);
        }

        return stats;
    }

    private void MigrateLegacyFile()
    {
        // Migrate old MapLocations.json to UserMapLocations.json
        if (File.Exists(_legacyFilePath) && !File.Exists(_userFilePath))
        {
            try
            {
                var json = File.ReadAllText(_legacyFilePath);
                var legacy = JsonSerializer.Deserialize<List<MapLocationEntry>>(json, JsonOptions);
                if (legacy != null && legacy.Count > 0)
                {
                    // Mark all legacy entries as having RealXYZ (they were recorded from gameplay)
                    foreach (var entry in legacy)
                        entry.HasRealXYZ = true;
                    var newJson = JsonSerializer.Serialize(legacy, JsonOptions);
                    File.WriteAllText(_userFilePath, newJson);
                    _plugin.AddDebugLog($"[MapLocDB] Migrated {legacy.Count} entries from legacy MapLocations.json → UserMapLocations.json");
                }
                File.Delete(_legacyFilePath);
            }
            catch (Exception ex)
            {
                _log.Error($"Legacy migration failed: {ex.Message}");
            }
        }
    }

    private void LoadCommunity()
    {
        _communityEntries = LoadFile(_communityFilePath, "Community");
    }

    private void LoadUser()
    {
        _userEntries = LoadFile(_userFilePath, "User");
    }

    private List<MapLocationEntry> LoadFile(string path, string label)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var entries = JsonSerializer.Deserialize<List<MapLocationEntry>>(json, JsonOptions) ?? new();
                _plugin.AddDebugLog($"[MapLocDB] Loaded {entries.Count} {label} entries");
                return entries;
            }
            _plugin.AddDebugLog($"[MapLocDB] No {label} file found");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load {label} MapLocDB: {ex.Message}");
        }
        return new();
    }

    private void SaveCommunity()
    {
        SaveFile(_communityFilePath, _communityEntries);
    }

    private void SaveUser()
    {
        SaveFile(_userFilePath, _userEntries);
    }

    private void SaveFile(string path, List<MapLocationEntry> entries)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save MapLocDB: {ex.Message}");
        }
    }
}

public class MapLocationEntry
{
    public uint TerritoryId { get; set; }
    public string ZoneName { get; set; } = "";
    public string MapName { get; set; } = "";
    public string AetheryteName { get; set; } = "";
    public float FlagX { get; set; }
    public float FlagY { get; set; }
    public float FlagZ { get; set; }
    public float RealX { get; set; }
    public float RealY { get; set; }
    public float RealZ { get; set; }
    public bool HasRealXYZ { get; set; }
    public string? RecordedAt { get; set; }
    public string? Source { get; set; }
    public int Index { get; set; } = -1; // New field, -1 = not assigned
}
