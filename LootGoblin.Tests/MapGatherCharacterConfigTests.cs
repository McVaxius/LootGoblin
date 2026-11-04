using LootGoblin.Models;
using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class MapGatherCharacterConfigTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-16T10:00:00Z");

    private static List<string> PreviousLandingDefaults() =>
    [
        "/rotation Auto", "/bmrai on", "/vbmai on", "/fr off",
        "/cbt disable AutoFollow", "/bmrai followoutofcombat off",
        "/bmrai followcombat off", "/vbmai follow Slot1", "", "",
    ];

    [Fact]
    public void FactoryCombatCommandsMigrateGlobalAndCharacterListsWithoutChangingOverridesOrFinishCommands()
    {
        var global = PreviousLandingDefaults();
        var factoryProfile = new MapGatherCharacterConfig
        {
            OverrideCommandTriggers = true,
            LandingOrDutyCommandTriggers = PreviousLandingDefaults(),
            FinishCommandTriggers = ["/custom finish"],
        };
        var custom = PreviousLandingDefaults();
        custom[0] = "/rotation custom";
        var customProfile = new MapGatherCharacterConfig { LandingOrDutyCommandTriggers = custom };

        Assert.True(LandingCommandDefaults.MigratePreviousFactory(11, global, [factoryProfile, customProfile]));

        string[] expected =
        [
            "/rotation cancel", "/bmrai on", "/vbmai off", "/fr off",
            "/cbt disable AutoFollow", "/bmrai followoutofcombat off",
            "/bmrai followcombat off", "", "", "",
        ];
        Assert.Equal(expected, LandingCommandDefaults.Values);
        Assert.Equal(expected, global);
        Assert.Equal(expected, factoryProfile.LandingOrDutyCommandTriggers);
        Assert.True(factoryProfile.OverrideCommandTriggers);
        Assert.Equal(new[] { "/custom finish" }, factoryProfile.FinishCommandTriggers);
        Assert.Equal("/rotation custom", customProfile.LandingOrDutyCommandTriggers![0]);
        Assert.False(customProfile.OverrideCommandTriggers);
        Assert.False(LandingCommandDefaults.MigratePreviousFactory(11, global, [factoryProfile, customProfile]));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    public void LaterSavedChoicesAreNeverRemigrated(int version)
    {
        var global = PreviousLandingDefaults();
        var profile = new MapGatherCharacterConfig { LandingOrDutyCommandTriggers = PreviousLandingDefaults() };
        Assert.False(LandingCommandDefaults.MigratePreviousFactory(version, global, [profile]));
        Assert.Equal(PreviousLandingDefaults(), global);
        Assert.Equal(PreviousLandingDefaults(), profile.LandingOrDutyCommandTriggers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CharacterFactoryMigrationDoesNotDependOnGlobalDefaultsOrOverrideBeingActive(bool missingGlobal)
    {
        List<string>? global = missingGlobal ? null : ["/custom rotation"];
        var profile = new MapGatherCharacterConfig
        {
            OverrideCommandTriggers = false,
            LandingOrDutyCommandTriggers = PreviousLandingDefaults(),
        };

        Assert.True(LandingCommandDefaults.MigratePreviousFactory(11, global, [profile]));

        Assert.Equal(missingGlobal ? null : new[] { "/custom rotation" }, global?.ToArray());
        Assert.Equal(LandingCommandDefaults.Values, profile.LandingOrDutyCommandTriggers);
        Assert.False(profile.OverrideCommandTriggers);
    }

    [Theory]
    [InlineData("case")]
    [InlineData("space")]
    [InlineData("short")]
    [InlineData("extra")]
    [InlineData("empty")]
    [InlineData("null")]
    public void CustomizedOrMissingCommandListsRemainUntouched(string change)
    {
        List<string>? commands = PreviousLandingDefaults();
        switch (change)
        {
            case "case": commands[0] = "/rotation auto"; break;
            case "space": commands[0] += " "; break;
            case "short": commands.RemoveAt(commands.Count - 1); break;
            case "extra": commands.Add("/custom action"); break;
            case "empty": commands.Clear(); break;
            case "null": commands = null; break;
        }
        var before = commands?.ToArray();
        Assert.False(LandingCommandDefaults.MigratePreviousFactory(11, commands));
        Assert.Equal(before, commands?.ToArray());
    }

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
