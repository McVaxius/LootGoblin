using LootGoblin.Models;
using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class MapGatherCharacterConfigTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-16T10:00:00Z");

    [Fact]
    public void LegacyGatherSettingsMigrateOnceToFirstBoundCharacter()
    {
        var store = new MapGatherCharacterConfigStore();

        var first = store.BindCharacter(
            "ABC",
            ClassJobOptions.Miner,
            new uint[] { 0, 6688, 6688, 7884 },
            out var firstMigrated);
        var second = store.BindCharacter(
            "DEF",
            ClassJobOptions.Miner,
            new uint[] { 6688 },
            out var secondMigrated);

        Assert.True(firstMigrated);
        Assert.Equal(ClassJobOptions.Miner, first.SelectedGatherJobId);
        Assert.Equal(new uint[] { 6688 }, first.GatherEnabledMapTypes);

        Assert.False(secondMigrated);
        Assert.Equal(0u, second.SelectedGatherJobId);
        Assert.Empty(second.GatherEnabledMapTypes);
    }

    [Fact]
    public void NewAltProfileStartsEmptyAfterLegacyMigration()
    {
        var store = new MapGatherCharacterConfigStore { LegacyGatherSettingsMigrated = true };

        var profile = store.BindCharacter(
            "DEF",
            ClassJobOptions.Botanist,
            new uint[] { 6688 },
            out var migrated);

        Assert.False(migrated);
        Assert.Equal(0u, profile.SelectedGatherJobId);
        Assert.Empty(profile.GatherEnabledMapTypes);
    }

    [Fact]
    public void GatherMapListNormalizationRemovesInvalidMapIds()
    {
        var profile = new MapGatherCharacterConfig();

        profile.CopyLegacyGatherSettings(
            ClassJobOptions.Fisher,
            new uint[] { 0, 6688, 7884, 8156, 6688 });

        Assert.Equal(ClassJobOptions.Fisher, profile.SelectedGatherJobId);
        Assert.Equal(new uint[] { 6688 }, profile.GatherEnabledMapTypes);
    }

    [Fact]
    public void AllowanceSnapshotRecomputesPerCharacterRemaining()
    {
        var profile = new MapGatherCharacterConfig();
        profile.SetMapAllowanceSnapshot(new MapAllowanceStatus(
            false,
            TimeSpan.FromHours(18),
            Now.AddHours(18),
            string.Empty));

        var found = profile.TryGetMapAllowanceSnapshot(Now.AddMinutes(45), out var status);

        Assert.True(found);
        Assert.True(status.IsAvailable);
        Assert.False(status.IsReady);
        Assert.Equal(TimeSpan.FromHours(17).Add(TimeSpan.FromMinutes(15)), status.Remaining);
    }

    [Fact]
    public void CommandTriggersUseGlobalWhenOverrideDisabled()
    {
        var profile = new MapGatherCharacterConfig
        {
            OverrideCommandTriggers = false,
            LandingOrDutyCommandTriggers = new List<string> { "/character landing" },
            FinishCommandTriggers = new List<string> { "/character finish" },
        };

        Assert.Equal(new[] { "/global landing" }, profile.GetLandingOrDutyCommandTriggers(new[] { "/global landing" }));
        Assert.Equal(new[] { "/global finish" }, profile.GetFinishCommandTriggers(new[] { "/global finish" }));
    }

    [Fact]
    public void CommandTriggerOverridesTakePrecedenceWhenEnabled()
    {
        var profile = new MapGatherCharacterConfig
        {
            OverrideCommandTriggers = true,
            LandingOrDutyCommandTriggers = new List<string> { "/character landing" },
            FinishCommandTriggers = new List<string> { "/character finish" },
        };

        Assert.Equal(new[] { "/character landing" }, profile.GetLandingOrDutyCommandTriggers(new[] { "/global landing" }));
        Assert.Equal(new[] { "/character finish" }, profile.GetFinishCommandTriggers(new[] { "/global finish" }));
    }
}
