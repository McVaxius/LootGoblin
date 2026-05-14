using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private readonly string _bundledFilePath;
    private readonly List<SpecialNavigationEntry> _entries = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Code-owned defaults keep required special nav available even if bundled JSON is stale.
    private static readonly SpecialNavigationEntry[] RequiredBuiltInDefaultEntries =
    {
        new()
        {
            DestinationIndex = 534,
            ZoneName = "The Lochs",
            PreX = 54.0f,
            PreY = -10.0f,
            PreZ = -45.0f,
            MainX = 42.1f,
            MainY = -12.9f,
            MainZ = -23.7f,
            Notes = "Lochs Thief ice dive entry for flag 34.6,-249.1,-12.9.",
            IsActive = true,
        },
        new()
        {
            DestinationIndex = 536,
            ZoneName = "The Lochs",
            PreX = -181.0f,
            PreY = -10.0f,
            PreZ = -173.0f,
            MainX = -217.6f,
            MainY = -277.3f,
            MainZ = -114.2f,
            Notes = "Lochs Thief ice dive entry for flag -217.8,-277.3,-114.2.",
            IsActive = true,
        },
        new()
        {
            DestinationIndex = 537,
            ZoneName = "The Lochs",
            PreX = -0.9f,
            PreY = -10.0f,
            PreZ = -282.8f,
            MainX = -0.75f,
            MainY = -281.6f,
            MainZ = -282.9f,
            Notes = "Lochs Thief ice dive entry for flag -0.9,-281.6,-282.8.",
            IsActive = true,
        },
        new()
        {
            DestinationIndex = 538,
            ZoneName = "The Lochs",
            PreX = 141.0f,
            PreY = -10.0f,
            PreZ = 243.0f,
            MainX = 103.6f,
            MainY = -343.7f,
            MainZ = 207.9f,
            Notes = "Lochs Thief ice dive entry for flag 103.5,-343.7,207.7.",
            IsActive = true,
        },
    };

    public IReadOnlyList<SpecialNavigationEntry> Entries => _entries.AsReadOnly();

    public SpecialNavigationDatabase(Plugin plugin, IPluginLog log)
    {
        _plugin = plugin;
        _log = log;
        var pluginDir = Plugin.PluginInterface.GetPluginConfigDirectory();
        _filePath = Path.Combine(pluginDir, "SpecialNavigation.json");
        var assemblyDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? AppContext.BaseDirectory;
        _bundledFilePath = Path.Combine(assemblyDir, "SpecialNavigation.json");
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
    /// Load special navigation entries from the config file.
    /// Built-in active entries are merged when absent so existing installs pick up new defaults.
    /// </summary>
    public void Load()
    {
        try
        {
            var bundledEntries = LoadBuiltInDefaultEntries();
            var mergedBuiltInCount = 0;

            if (!File.Exists(_filePath))
            {
                var defaultEntries = bundledEntries.Select(CloneEntry).ToList();
                SaveEntries(defaultEntries);
                _entries.Clear();
                _entries.AddRange(defaultEntries);
                mergedBuiltInCount = defaultEntries.Count(e => e.IsActive);
                _log.Information($"[SpecialNavDB] Created SpecialNavigation.json with {mergedBuiltInCount} bundled active entries");
                LogLoadedEntries(mergedBuiltInCount);
                return;
            }

            var entries = LoadEntriesFromFile(_filePath);
            mergedBuiltInCount = MergeMissingBuiltInActiveEntries(entries, bundledEntries);
            if (mergedBuiltInCount > 0)
                SaveEntries(entries);

            _entries.Clear();
            _entries.AddRange(entries);
            LogLoadedEntries(mergedBuiltInCount);
        }
        catch (Exception ex)
        {
            _log.Error($"[SpecialNavDB] Failed to load special navigation entries: {ex.Message}");
            _entries.Clear();
            _entries.AddRange(LoadBuiltInDefaultEntries().Select(CloneEntry));
            LogLoadedEntries(0);
        }
    }

    /// <summary>
    /// Load all built-in entries from the bundle and code-owned required defaults.
    /// </summary>
    private List<SpecialNavigationEntry> LoadBuiltInDefaultEntries()
    {
        var entries = LoadBundledDefaultEntries();
        MergeMissingBuiltInActiveEntries(entries, RequiredBuiltInDefaultEntries);
        return entries;
    }

    /// <summary>
    /// Load bundled entries copied next to the plugin assembly.
    /// </summary>
    private List<SpecialNavigationEntry> LoadBundledDefaultEntries()
    {
        try
        {
            if (!File.Exists(_bundledFilePath) || IsSamePath(_bundledFilePath, _filePath))
                return new List<SpecialNavigationEntry>();

            var entries = LoadEntriesFromFile(_bundledFilePath);
            var activeCount = entries.Count(e => e.IsActive);
            _log.Information($"[SpecialNavDB] Loaded {entries.Count} bundled default entries ({activeCount} active)");
            return entries;
        }
        catch (Exception ex)
        {
            _log.Error($"[SpecialNavDB] Failed to load bundled defaults: {ex.Message}");
            return new List<SpecialNavigationEntry>();
        }
    }

    private static List<SpecialNavigationEntry> LoadEntriesFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<SpecialNavigationEntry>>(json, JsonOptions) ?? new List<SpecialNavigationEntry>();
    }

    private void SaveEntries(List<SpecialNavigationEntry> entries)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(entries, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    private static int MergeMissingBuiltInActiveEntries(List<SpecialNavigationEntry> entries, IEnumerable<SpecialNavigationEntry> builtInEntries)
    {
        var existingDestinationIndices = entries
            .Where(e => e.DestinationIndex > 0)
            .Select(e => e.DestinationIndex)
            .ToHashSet();

        var added = 0;
        foreach (var builtInEntry in builtInEntries.Where(e => e.IsActive && e.DestinationIndex > 0))
        {
            if (!existingDestinationIndices.Add(builtInEntry.DestinationIndex))
                continue;

            entries.Add(CloneEntry(builtInEntry));
            added++;
        }

        return added;
    }

    private static SpecialNavigationEntry CloneEntry(SpecialNavigationEntry entry)
    {
        return new SpecialNavigationEntry
        {
            DestinationIndex = entry.DestinationIndex,
            ZoneName = entry.ZoneName,
            PreX = entry.PreX,
            PreY = entry.PreY,
            PreZ = entry.PreZ,
            MainX = entry.MainX,
            MainY = entry.MainY,
            MainZ = entry.MainZ,
            Notes = entry.Notes,
            IsActive = entry.IsActive,
        };
    }

    private void LogLoadedEntries(int mergedBuiltInCount)
    {
        var activeDestinationIndices = _entries
            .Where(e => e.IsActive)
            .Select(e => e.DestinationIndex)
            .OrderBy(i => i)
            .ToList();
        var activeText = activeDestinationIndices.Count > 0
            ? string.Join(", ", activeDestinationIndices)
            : "none";

        _log.Information(
            $"[SpecialNavDB] Loaded {_entries.Count} special navigation entries " +
            $"({activeDestinationIndices.Count} active: {activeText}); " +
            $"merged {mergedBuiltInCount} built-in active entries");
    }

    private static bool IsSamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        // No save needed - this is a distributed file
    }
}
