using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin.Services;

namespace LootGoblin.Services;

/// <summary>
/// Stores known world positions of aetherytes, learned from player teleport arrivals.
/// Permanently solves the Level sheet / MapMarker fallback problem by recording
/// the player's actual position when they arrive at each aetheryte.
/// File: AetherytePositions.json in plugin config directory.
/// </summary>
public class AetherytePositionDatabase
{
    private readonly Plugin _plugin;
    private readonly IPluginLog _log;
    private readonly string _filePath;
    private readonly Dalamud.Plugin.Services.IDataManager _dataManager;

    private Dictionary<uint, AetherytePosition> _positions = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AetherytePositionDatabase(Plugin plugin, IPluginLog log)
    {
        _plugin = plugin;
        _log = log;
        _dataManager = Plugin.DataManager;
        var configDir = Plugin.PluginInterface.GetPluginConfigDirectory();
        _filePath = Path.Combine(configDir, "AetherytePositions.json");
        _plugin.AddDebugLog($"[AetheryteDB] Using config directory: {configDir}");
        _plugin.AddDebugLog($"[AetheryteDB] File path: {_filePath}");
        Load();
    }

    public int Count => _positions.Count;

    /// <summary>Get stored position for an aetheryte, or null if not recorded.</summary>
    public AetherytePosition? GetPosition(uint aetheryteId)
    {
        return _positions.TryGetValue(aetheryteId, out var pos) ? pos : null;
    }

    /// <summary>Record player position as an aetheryte's world position.</summary>
    public void RecordPosition(uint aetheryteId, string name, float x, float y, float z)
    {
        var pos = new AetherytePosition
        {
            AetheryteId = aetheryteId,
            Name = name,
            X = (float)Math.Round(x, 1),
            Y = (float)Math.Round(y, 1),
            Z = (float)Math.Round(z, 1),
            RecordedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        bool isNew = !_positions.ContainsKey(aetheryteId);
        _positions[aetheryteId] = pos;
        Save();
        _plugin.AddDebugLog($"[AetheryteDB] {(isNew ? "Recorded" : "Updated")} {name} (ID:{aetheryteId}) at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}) [total: {_positions.Count}]");
    }

    /// <summary>Check if we have a stored position for this aetheryte.</summary>
    public bool HasPosition(uint aetheryteId) => _positions.ContainsKey(aetheryteId);

    /// <summary>Remove an aetheryte position from the database (used to force re-recording).</summary>
    public void RemovePosition(uint aetheryteId)
    {
        if (_positions.Remove(aetheryteId))
        {
            _plugin.AddDebugLog($"[AetheryteDB] Removed aetheryte ID {aetheryteId} from database - will appear as missing");
            Save(); // Persist the removal
        }
    }

    /// <summary>Clear all recorded aetheryte positions (for reset).</summary>
    public void ClearAllPositions()
    {
        var count = _positions.Count;
        
        // Delete the user file to force complete reset on next load
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
                _plugin.AddDebugLog($"[AetheryteDB] Deleted user file - will reload with defaults only");
            }
        }
        catch (Exception ex)
        {
            _plugin.AddDebugLog($"[AetheryteDB] Failed to delete user file: {ex.Message}");
        }
        
        // Clear current positions and reload (will load defaults only)
        _positions.Clear();
        Load(); // This will load only defaults since user file was deleted
        
        _plugin.AddDebugLog($"[AetheryteDB] Reset complete - {_positions.Count} default positions loaded");
    }

    /// <summary>Get all stored positions.</summary>
    public IReadOnlyDictionary<uint, AetherytePosition> AllPositions => _positions;

    /// <summary>Get the config directory path (for "Open Folder" button).</summary>
    public string ConfigDirectory => Path.GetDirectoryName(_filePath) ?? "";

    /// <summary>
    /// Get all unlocked aetherytes that are missing stored positions.
    /// Returns list of (AetheryteId, Name, TerritoryId) tuples.
    /// </summary>
    public unsafe List<(uint Id, string Name, uint TerritoryId)> GetMissingAetherytes(Dalamud.Plugin.Services.IDataManager dataManager)
    {
        var missing = new List<(uint Id, string Name, uint TerritoryId)>();

        try
        {
            // Don't access Telepo during zone transitions
            if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] ||
                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51])
                return missing;

            var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
            if (telepo == null) return missing;

            telepo->UpdateAetheryteList();
            var count = telepo->TeleportList.Count;
            if (count == 0) return missing;

            var aetheryteSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
            if (aetheryteSheet == null) return missing;

            for (int i = 0; i < count; i++)
            {
                var entry = telepo->TeleportList[i];
                if (entry.AetheryteId == 0) continue;

                if (_positions.ContainsKey(entry.AetheryteId)) continue;

                var aetheryte = aetheryteSheet.GetRow(entry.AetheryteId);
                var name = aetheryte.PlaceName.ValueNullable?.Name.ToString() ?? $"ID {entry.AetheryteId}";
                var territoryId = aetheryte.Territory.RowId;

                // Skip housing aetherytes (Private, Free Company, etc.)
                if (name.Contains("Estate Hall") || name.Contains("Apartment") || name.Contains("Room"))
                    continue;

                missing.Add((entry.AetheryteId, name, territoryId));
            }
        }
        catch (Exception ex)
        {
            _log.Error($"GetMissingAetherytes failed: {ex.Message}");
        }

        return missing;
    }

    /// <summary>
    /// Get total count of unlocked aetherytes.
    /// </summary>
    public unsafe int GetTotalUnlockedCount()
    {
        try
        {
            // Don't access Telepo during zone transitions
            if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] ||
                Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51])
                return 0;

            var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
            if (telepo == null) return 0;

            telepo->UpdateAetheryteList();
            int count = 0;
            var aetheryteSheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>();
            for (int i = 0; i < telepo->TeleportList.Count; i++)
            {
                var entry = telepo->TeleportList[i];
                if (entry.AetheryteId == 0) continue;

                // Skip housing aetherytes (same filter as GetMissingAetherytes)
                if (aetheryteSheet != null)
                {
                    var aetheryte = aetheryteSheet.GetRow(entry.AetheryteId);
                    var name = aetheryte.PlaceName.ValueNullable?.Name.ToString() ?? "";
                    if (name.Contains("Estate Hall") || name.Contains("Apartment") || name.Contains("Room"))
                        continue;
                }

                count++;
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private void Load()
    {
        try
        {
            // Seed with community defaults first
            _positions = DefaultAetheryteData.GetDefaults();
            var defaultCount = _positions.Count;

            // Overlay user-recorded data (takes priority over defaults)
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<AetherytePosition>>(json, JsonOptions);
                if (list != null)
                {
                    foreach (var pos in list)
                        _positions[pos.AetheryteId] = pos;
                    _plugin.AddDebugLog($"[AetheryteDB] Loaded {list.Count} user positions over {defaultCount} defaults = {_positions.Count} total");
                }
            }
            else
            {
                _plugin.AddDebugLog($"[AetheryteDB] No user file - using {defaultCount} community defaults");
                Save(); // Write defaults to disk so user can see/share the file
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load AetherytePositions: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var list = _positions.Values.OrderBy(p => p.AetheryteId).ToList();
            var json = JsonSerializer.Serialize(list, JsonOptions);
            File.WriteAllText(_filePath, json);
            _plugin.AddDebugLog($"[AetheryteDB] Saved {_positions.Count} positions to {_filePath}");
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to save AetherytePositions: {ex.Message}");
        }
    }
}

public class AetherytePosition
{
    public uint AetheryteId { get; set; }
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public string? RecordedAt { get; set; }
}
