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
        var pluginDir = Plugin.PluginInterface.ConfigDirectory.FullName; // Plugin directory, not user config
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
                // Create sample file if it doesn't exist (for development)
                CreateSampleFile();
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
            // Create sample file on error
            CreateSampleFile();
        }
    }

    /// <summary>
    /// Create sample SpecialNavigation.json file (for development only).
    /// </summary>
    private void CreateSampleFile()
    {
        var sampleEntries = new List<SpecialNavigationEntry>
        {
            new SpecialNavigationEntry
            {
                DestinationIndex = 42, // Example destination
                ZoneName = "The Sea of Clouds - Underwater Area",
                PreX = 123.4f, // Surface coordinates
                PreY = 45.6f,
                PreZ = 789.0f,
                MainX = 124.0f, // Underwater coordinates
                MainY = 40.0f,
                MainZ = 790.5f,
                Notes = "Sample underwater map - fly to surface first, then dive when Condition 81 is true",
                IsActive = true
            }
        };

        _entries.Clear();
        _entries.AddRange(sampleEntries);
        
        try
        {
            var json = JsonSerializer.Serialize(sampleEntries, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(_filePath, json);
            _log.Information($"[SpecialNavDB] Created sample SpecialNavigation.json with {sampleEntries.Count} entries");
        }
        catch (Exception ex)
        {
            _log.Error($"[SpecialNavDB] Failed to create sample file: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // No save needed - this is a distributed file
    }
}
