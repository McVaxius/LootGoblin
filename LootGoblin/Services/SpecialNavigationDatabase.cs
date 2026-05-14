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
            var bundledEntries = LoadBundledDefaultEntries();
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
            _entries.AddRange(LoadBundledDefaultEntries().Select(CloneEntry));
            LogLoadedEntries(0);
        }
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

    private static int MergeMissingBuiltInActiveEntries(List<SpecialNavigationEntry> entries, List<SpecialNavigationEntry> builtInEntries)
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
