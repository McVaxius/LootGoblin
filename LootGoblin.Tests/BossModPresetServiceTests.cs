using System.Text.Json;
using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class BossModPresetServiceTests
{
    private static readonly string PresetDirectory = Path.Combine(AppContext.BaseDirectory, "data", "bm");

    [Theory]
    [InlineData(19u, "passive - tank")]
    [InlineData(37u, "passive - tank")]
    [InlineData(20u, "passive - melee")]
    [InlineData(41u, "passive - melee")]
    [InlineData(43u, "passive - melee")]
    [InlineData(24u, "passive - ranged")]
    [InlineData(42u, "passive - ranged")]
    public void FreshSetupInstallsSixAndSelectsBothAiAndRuntimeForTheJob(uint job, string name)
    {
        var ipc = new FakeIpc();
        var service = new BossModPresetService(PresetDirectory, ipc);

        Assert.True(service.PreparePassive(job, out var detail), detail);
        Assert.Equal(6, ipc.Created.Count);
        Assert.Equal(BossModPresetService.PackagedNames, ipc.Created);
        Assert.Equal(name, ipc.Ai);
        Assert.Equal(name, ipc.Active);
        Assert.True(ipc.Calls.IndexOf("AI " + name) < ipc.Calls.IndexOf("Active " + name));
    }

    [Fact]
    public void RefreshReplacesStaleDefinitionsAndRebindsPreviousSelections()
    {
        var ipc = new FakeIpc { Active = "passive - tank", Ai = "passive - tank" };
        foreach (var name in BossModPresetService.PackagedNames)
            ipc.Presets[name] = JsonSerializer.Serialize(new { Name = name, Modules = new Dictionary<string, object>() });
        ipc.Presets["Custom"] = "custom untouched";
        var service = new BossModPresetService(PresetDirectory, ipc);

        Assert.True(service.Refresh(out var detail), detail);
        Assert.Equal(6, ipc.Deleted.Count);
        Assert.Equal(6, ipc.Created.Count);
        Assert.Equal("passive - tank", ipc.Active);
        Assert.Equal("passive - tank", ipc.Ai);
        Assert.Equal("custom untouched", ipc.Presets["Custom"]);
        using var definition = JsonDocument.Parse(ipc.Presets["passive - tank"]);
        Assert.True(definition.RootElement.GetProperty("Modules").TryGetProperty(
            "BossMod.Autorotation.MiscAI.StayCloseToTarget", out var range));
        Assert.Equal("2.3", range[0].GetProperty("Option").GetString());
        Assert.Contains("AI passive - tank", ipc.Calls);
    }

    [Fact]
    public void NewJobAndCombatRestartSelectWithoutReinstallingDefinitions()
    {
        var ipc = new FakeIpc();
        var service = new BossModPresetService(PresetDirectory, ipc);
        Assert.True(service.PreparePassive(19, out _));
        ipc.Active = null; // /bmrai off clears BMR's runtime preset between combats.

        Assert.True(service.PreparePassive(19, out var detail), detail);
        Assert.Equal("passive - tank", ipc.Active);
        Assert.True(service.PreparePassive(20, out detail), detail);
        Assert.Equal("passive - melee", ipc.Active);
        Assert.Equal("passive - melee", ipc.Ai);
        Assert.Equal(6, ipc.Created.Count);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(16u)]
    [InlineData(999u)]
    public void UnknownOrGatheringJobCannotSelectRangedOrWritePresets(uint job)
    {
        var ipc = new FakeIpc();
        var service = new BossModPresetService(PresetDirectory, ipc);
        Assert.False(service.PreparePassive(job, out _));
        Assert.Empty(ipc.Calls);
    }

    [Fact]
    public void MissingBundleDoesNotDeleteExistingDefinitions()
    {
        var ipc = new FakeIpc { Active = "Custom", Ai = "Custom" };
        var service = new BossModPresetService(Path.Combine(PresetDirectory, "missing"), ipc);
        Assert.False(service.Refresh(out var detail));
        Assert.Contains("failed", detail);
        Assert.Empty(ipc.Calls);
        Assert.Equal("Custom", ipc.Active);
        Assert.Equal("Custom", ipc.Ai);
    }

    [Fact]
    public void RejectedInstallationCannotReportPrepared()
    {
        var ipc = new FakeIpc { RejectCreate = "passive - melee" };
        var service = new BossModPresetService(PresetDirectory, ipc);
        Assert.False(service.PreparePassive(20, out var detail));
        Assert.Contains("incomplete", detail);
        Assert.Null(ipc.Active);
        Assert.Equal(string.Empty, ipc.Ai);
    }

    [Fact]
    public void UnconfirmedAiSelectionDoesNotActivateRuntime()
    {
        var ipc = new FakeIpc { IgnoreAiSelection = true };
        var service = new BossModPresetService(PresetDirectory, ipc);
        Assert.False(service.PreparePassive(20, out var detail));
        Assert.Contains("AI preset", detail);
        Assert.Null(ipc.Active);
        Assert.DoesNotContain("Active passive - melee", ipc.Calls);
    }

    [Fact]
    public void RejectedRuntimeSelectionCannotReportPrepared()
    {
        var ipc = new FakeIpc { RejectActive = true };
        var service = new BossModPresetService(PresetDirectory, ipc);
        Assert.False(service.PreparePassive(20, out var detail));
        Assert.Contains("active preset", detail);
    }

    private sealed class FakeIpc : IBossModPresetIpc
    {
        internal readonly Dictionary<string, string> Presets = [];
        internal readonly List<string> Created = [];
        internal readonly List<string> Deleted = [];
        internal readonly List<string> Calls = [];
        internal string? Active;
        internal string Ai = string.Empty;
        internal string? RejectCreate;
        internal bool IgnoreAiSelection;
        internal bool RejectActive;

        public string? GetPreset(string name) => Presets.GetValueOrDefault(name);
        public bool DeletePreset(string name)
        {
            Calls.Add("Delete " + name);
            Deleted.Add(name);
            if (Active == name) Active = null;
            return Presets.Remove(name);
        }
        public bool CreatePreset(string json)
        {
            using var definition = JsonDocument.Parse(json);
            var name = definition.RootElement.GetProperty("Name").GetString()!;
            Calls.Add("Create " + name);
            if (name == RejectCreate) return false;
            Presets[name] = json;
            Created.Add(name);
            return true;
        }
        public string? GetActivePreset() => Active;
        public bool SetActivePreset(string name)
        {
            Calls.Add("Active " + name);
            if (RejectActive || !Presets.ContainsKey(name)) return false;
            Active = name;
            return true;
        }
        public string GetAiPreset() => Ai;
        public void SetAiPreset(string name)
        {
            Calls.Add("AI " + name);
            if (!IgnoreAiSelection) Ai = Presets.ContainsKey(name) ? name : string.Empty;
        }
    }
}
