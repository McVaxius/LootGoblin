using System.Collections.Generic;
using System.Linq;

namespace LootGoblin.Models;

public readonly record struct ClassJobOption(uint Id, string Name);

public static class ClassJobOptions
{
    public const uint Miner = 16;
    public const uint Botanist = 17;
    public const uint Fisher = 18;

    public static readonly IReadOnlyList<ClassJobOption> CombatJobs = new[]
    {
        new ClassJobOption(19, "Paladin"),
        new ClassJobOption(20, "Monk"),
        new ClassJobOption(21, "Warrior"),
        new ClassJobOption(22, "Dragoon"),
        new ClassJobOption(23, "Bard"),
        new ClassJobOption(24, "White Mage"),
        new ClassJobOption(25, "Black Mage"),
        new ClassJobOption(27, "Summoner"),
        new ClassJobOption(28, "Scholar"),
        new ClassJobOption(30, "Ninja"),
        new ClassJobOption(31, "Machinist"),
        new ClassJobOption(32, "Dark Knight"),
        new ClassJobOption(33, "Astrologian"),
        new ClassJobOption(34, "Samurai"),
        new ClassJobOption(35, "Red Mage"),
        new ClassJobOption(36, "Blue Mage"),
        new ClassJobOption(37, "Gunbreaker"),
        new ClassJobOption(38, "Dancer"),
        new ClassJobOption(39, "Reaper"),
        new ClassJobOption(40, "Sage"),
        new ClassJobOption(41, "Viper"),
        new ClassJobOption(42, "Pictomancer"),
    };

    public static readonly IReadOnlyList<ClassJobOption> GatherJobs = new[]
    {
        new ClassJobOption(Botanist, "Botanist"),
        new ClassJobOption(Miner, "Miner"),
        new ClassJobOption(Fisher, "Fisher"),
    };

    public static bool IsCombatJob(uint jobId)
        => jobId == 0 || CombatJobs.Any(job => job.Id == jobId);

    public static bool IsGatherJob(uint jobId)
        => jobId == 0 || GatherJobs.Any(job => job.Id == jobId);

    public static string GetName(uint jobId)
    {
        var job = CombatJobs.Concat(GatherJobs).FirstOrDefault(option => option.Id == jobId);
        return job.Id == 0 ? $"Job {jobId}" : job.Name;
    }
}
