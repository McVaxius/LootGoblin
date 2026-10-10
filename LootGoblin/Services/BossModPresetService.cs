using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LootGoblin.Services;

internal interface IBossModPresetIpc
{
    string? GetPreset(string name);
    bool DeletePreset(string name);
    bool CreatePreset(string json);
    string? GetActivePreset();
    bool SetActivePreset(string name);
    string GetAiPreset();
    void SetAiPreset(string name);
}

/// <summary>Installs FrenRider's packaged presets and prepares BMR movement for external combat rotations.</summary>
internal sealed class BossModPresetService(string directory, IBossModPresetIpc ipc)
{
    internal static readonly string[] PackagedNames =
    [
        "FRENRIDER - TANK", "FRENRIDER - MELEE", "FRENRIDER - RANGED",
        "passive - tank", "passive - melee", "passive - ranged",
    ];

    private bool installed;

    internal static string? PassivePresetForJob(uint jobId) => jobId switch
    {
        1 or 3 or 19 or 21 or 32 or 37 => "passive - tank",
        2 or 4 or 20 or 22 or 29 or 30 or 34 or 39 or 41 or 43 => "passive - melee",
        5 or 6 or 7 or 23 or 24 or 25 or 26 or 27 or 28 or 31 or 33 or 35 or 36 or 38 or 40 or 42 => "passive - ranged",
        _ => null,
    };

    internal bool Refresh(out string detail)
    {
        installed = false;
        try
        {
            // Read all six files before replacing any definitions. An incomplete bundle must not delete presets.
            var presets = new List<(string Name, string Json)>();
            foreach (var name in PackagedNames)
            {
                var json = File.ReadAllText(Path.Combine(directory, name + ".json"));
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("Name", out var value)
                    || value.ValueKind != JsonValueKind.String || value.GetString() != name
                    || !document.RootElement.TryGetProperty("Modules", out var modules)
                    || modules.ValueKind != JsonValueKind.Object)
                {
                    detail = $"Packaged BossMod preset '{name}' is invalid.";
                    return false;
                }
                presets.Add((name, json));
            }

            var previousActive = ipc.GetActivePreset();
            var previousAi = ipc.GetAiPreset();
            var failures = new List<string>();
            foreach (var preset in presets)
            {
                // Match FrenRider: remove an existing named definition, then create with overwrite enabled.
                if (ipc.GetPreset(preset.Name) is not null)
                    _ = ipc.DeletePreset(preset.Name);
                if (!ipc.CreatePreset(preset.Json))
                    failures.Add(preset.Name);
                else if (previousActive == preset.Name && !ipc.SetActivePreset(preset.Name))
                    failures.Add(preset.Name + " (active selection)");
            }

            // Rebind BMR's saved AI selection to the newly created object, rather than its deleted definition.
            if (!string.IsNullOrEmpty(previousAi))
            {
                ipc.SetAiPreset(previousAi);
                if (ipc.GetAiPreset() != previousAi)
                    failures.Add("previous AI selection");
            }
            if (!string.IsNullOrEmpty(previousActive) && ipc.GetActivePreset() != previousActive)
            {
                if (!ipc.SetActivePreset(previousActive) || ipc.GetActivePreset() != previousActive)
                    failures.Add("previous active selection");
            }
            installed = failures.Count == 0;
            detail = installed ? "Six packaged BossMod presets refreshed."
                : $"BossMod preset refresh incomplete: {string.Join(", ", failures)}.";
            return installed;
        }
        catch (Exception ex)
        {
            detail = $"BossMod preset refresh failed: {ex.Message}";
            return false;
        }
    }

    internal bool PreparePassive(uint jobId, out string detail)
    {
        var name = PassivePresetForJob(jobId);
        if (name is null)
        {
            detail = $"No passive BossMod combat preset for class/job {jobId}.";
            return false;
        }
        if (!installed && !Refresh(out detail))
            return false;
        try
        {
            if (ipc.GetPreset(name) is null)
            {
                detail = $"BossMod preset '{name}' is unavailable.";
                return false;
            }
            if (ipc.GetAiPreset() != name)
                ipc.SetAiPreset(name);
            if (ipc.GetAiPreset() != name)
            {
                detail = $"BMR AI preset '{name}' could not be confirmed.";
                return false;
            }
            if ((ipc.GetActivePreset() != name && !ipc.SetActivePreset(name))
                || ipc.GetActivePreset() != name)
            {
                detail = $"BossMod active preset '{name}' could not be confirmed.";
                return false;
            }
            detail = $"BMR AI and active preset '{name}' confirmed for class/job {jobId}.";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"BossMod preset preparation failed: {ex.Message}";
            return false;
        }
    }
}
