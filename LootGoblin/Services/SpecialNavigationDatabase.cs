using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;
using LootGoblin.Models;

namespace LootGoblin.Services;

/// <summary>
/// Database for special navigation entries (pre-dive coordinates for underwater maps).
/// Manually updated via SpecialNavigation.json file.
/// </summary>
public class SpecialNavigationDatabase : IDisposable
{
    private readonly Plugin _plugin;
    private readonly IPluginLog _log;
    private readonly string _filePath;
    private readonly List<SpecialNavigationEntry> _entries = new();

    public IReadOnlyList<SpecialNavigationEntry> Entries => _entries.AsReadOnly();

    public SpecialNavigationDatabase(Plugin plugin, IPluginLog log)
    {
        _plugin = plugin;
        _log = log;
        var pluginDir = Plugin.PluginInterface.ConfigDirectory.FullName;
        _filePath = Path.Combine(pluginDir, "SpecialNavigation.json");
        Load();
    }

    /// <summary>
    /// Find special navigation entry for a destination index.
    /// </summary>
    public SpecialNavigationEntry? FindEntry(int destinationIndex)
    {
        return _entries.FirstOrDefault(e => e.DestinationIndex == destinationIndex && e.IsActive);
    }

    /// <summary>
    /// Load special navigation entries from JSON file.
    /// This is a distributed file that overwrites on each release.
    /// </summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                CreateDefaultFile();
                return;
            }

            var json = File.ReadAllText(_filePath);
            var entries = JsonSerializer.Deserialize<List<SpecialNavigationEntry>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (entries != null)
            {
                _entries.Clear();
                _entries.AddRange(entries);
                _log.Information($"[SpecialNavDB] Loaded {_entries.Count} special navigation entries");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"[SpecialNavDB] Failed to load special navigation entries: {ex.Message}");
            CreateDefaultFile();
        }
    }

    /// <summary>
    /// Create an inactive default SpecialNavigation.json file.
    /// </summary>
    private void CreateDefaultFile()
    {
        var defaultEntries = new List<SpecialNavigationEntry>();

        _entries.Clear();
        _entries.AddRange(defaultEntries);
        
        try
        {
            var json = JsonSerializer.Serialize(defaultEntries, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(_filePath, json);
            _log.Information("[SpecialNavDB] Created empty SpecialNavigation.json; special navigation is opt-in.");
        }
        catch (Exception ex)
        {
            _log.Error($"[SpecialNavDB] Failed to create default file: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // No save needed - this is a distributed file
    }
}
