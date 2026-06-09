using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using LootGoblin.Models;

namespace LootGoblin.Services;

public readonly record struct JobSnapshot(uint ClassJobId, int GearsetId);

public sealed class JobSwitchService : IDisposable
{
    private const int MaxGearsetId = 100;

    private readonly Plugin plugin;
    private readonly IPluginLog log;

    public JobSwitchService(Plugin plugin, IPluginLog log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public void Dispose()
    {
    }

    public unsafe uint GetCurrentClassJobId()
    {
        try
        {
            var playerState = PlayerState.Instance();
            return playerState == null ? 0 : (uint)playerState->CurrentClassJobId;
        }
        catch (Exception ex)
        {
            log.Debug($"[JobSwitch] Could not read current class/job: {ex.Message}");
            return 0;
        }
    }

    public bool TryCaptureCurrentJob(out JobSnapshot snapshot, out string detail)
    {
        snapshot = default;
        var currentJobId = GetCurrentClassJobId();
        if (currentJobId == 0)
        {
            detail = "current job unavailable";
            return false;
        }

        TryFindGearsetForJob(currentJobId, out var gearsetId, out _);
        snapshot = new JobSnapshot(currentJobId, gearsetId);
        detail = gearsetId >= 0
            ? $"{ClassJobOptions.GetName(currentJobId)} gearset {gearsetId + 1}"
            : $"{ClassJobOptions.GetName(currentJobId)}";
        return true;
    }

    public bool TrySwitchToSnapshot(JobSnapshot snapshot, out string detail)
    {
        if (snapshot.ClassJobId == 0)
        {
            detail = "no job snapshot captured";
            return false;
        }

        if (snapshot.GearsetId >= 0)
            return TryEquipGearset(snapshot.GearsetId, snapshot.ClassJobId, out detail);

        return TrySwitchToJob(snapshot.ClassJobId, out detail);
    }

    public bool TrySwitchToJob(uint jobId, out string detail)
    {
        if (jobId == 0)
        {
            detail = "no job configured";
            return true;
        }

        if (!CanSwitchJobNow(out detail))
            return false;

        var currentJob = GetCurrentClassJobId();
        if (currentJob == jobId)
        {
            detail = $"Already on {ClassJobOptions.GetName(jobId)}.";
            return true;
        }

        if (!TryFindGearsetForJob(jobId, out var gearsetId, out detail))
            return false;

        return TryEquipGearset(gearsetId, jobId, out detail);
    }

    public unsafe bool TryFindGearsetForJob(uint jobId, out int gearsetId, out string detail)
    {
        gearsetId = -1;
        if (jobId == 0)
        {
            detail = "no job configured";
            return false;
        }

        try
        {
            var module = RaptureGearsetModule.Instance();
            if (module == null)
            {
                detail = "gearset module unavailable";
                return false;
            }

            for (var i = 0; i < MaxGearsetId; i++)
            {
                if (!module->IsValidGearset(i))
                    continue;

                var gearset = module->GetGearset(i);
                if (gearset == null)
                    continue;

                if (gearset->ClassJob != jobId)
                    continue;

                gearsetId = i;
                detail = $"Found {ClassJobOptions.GetName(jobId)} gearset {i + 1}.";
                return true;
            }

            detail = $"No gearset exists for configured job {ClassJobOptions.GetName(jobId)}.";
            return false;
        }
        catch (Exception ex)
        {
            detail = $"Could not inspect gearsets: {ex.Message}";
            Plugin.LogWarning($"[JobSwitch] {detail}");
            return false;
        }
    }

    private unsafe bool TryEquipGearset(int gearsetId, uint expectedJobId, out string detail)
    {
        if (!CanSwitchJobNow(out detail))
            return false;

        try
        {
            var module = RaptureGearsetModule.Instance();
            if (module == null)
            {
                detail = "gearset module unavailable";
                return false;
            }

            if (!module->IsValidGearset(gearsetId))
            {
                detail = $"Gearset {gearsetId + 1} is no longer valid.";
                return false;
            }

            var result = module->EquipGearset(gearsetId, 0);
            if (result != 0)
            {
                detail = $"EquipGearset({gearsetId + 1}) failed with result {result}.";
                return false;
            }

            detail = $"Switching to {ClassJobOptions.GetName(expectedJobId)} gearset {gearsetId + 1}.";
            plugin.AddDebugLog($"[JobSwitch] {detail}");
            return true;
        }
        catch (Exception ex)
        {
            detail = $"Job switch failed: {ex.Message}";
            Plugin.LogWarning($"[JobSwitch] {detail}");
            return false;
        }
    }

    private static bool CanSwitchJobNow(out string reason)
    {
        if (!Plugin.ClientState.IsLoggedIn)
        {
            reason = "not logged in";
            return false;
        }

        if (Plugin.ObjectTable.LocalPlayer == null)
        {
            reason = "player unavailable";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "loading";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.Casting])
        {
            reason = "casting";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            reason = "in combat";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.Mounting71])
        {
            reason = "mounted";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.Occupied] ||
            Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Plugin.Condition[ConditionFlag.Occupied33] ||
            Plugin.Condition[ConditionFlag.Occupied39] ||
            Plugin.Condition[ConditionFlag.WatchingCutscene])
        {
            reason = "occupied";
            return false;
        }

        reason = "ready";
        return true;
    }
}
