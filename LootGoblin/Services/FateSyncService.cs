using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;

namespace LootGoblin.Services;

public sealed class FateSyncService
{
    private const string LevelSyncCommand = "/levelsync on";
    private static readonly TimeSpan DeferLogInterval = TimeSpan.FromSeconds(10);

    private readonly Plugin plugin;
    private ushort lastSyncedFateId;
    private ushort lastDeferredFateId;
    private string lastDeferredReason = "";
    private DateTime lastDeferLogAt = DateTime.MinValue;

    public FateSyncService(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Update()
    {
        if (!plugin.Configuration.AutoSyncFate)
            return;

        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
            return;

        if (!TryGetCurrentFateId(out var fateId))
        {
            ResetFateState();
            return;
        }

        if (lastSyncedFateId == fateId)
            return;

        if (IsBlocked(out var reason))
        {
            LogDeferred(fateId, reason);
            return;
        }

        if (!CommandHelper.TrySendCommand(LevelSyncCommand))
            return;

        lastSyncedFateId = fateId;
        lastDeferredFateId = 0;
        lastDeferredReason = "";
        Plugin.Log.Information($"[LG][FATE-SYNC] Sent {LevelSyncCommand} for fateId={fateId}");
    }

    private static unsafe bool TryGetCurrentFateId(out ushort fateId)
    {
        fateId = 0;

        try
        {
            var fm = FateManager.Instance();
            if (fm == null || fm->FateJoined == 0 || fm->CurrentFate == null)
                return false;

            fateId = fm->GetCurrentFateId();
            return fateId != 0;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"[LG][FATE-SYNC] FateManager read failed: {ex.Message}");
            return false;
        }
    }

    private static bool IsBlocked(out string reason)
    {
        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "between areas";
            return true;
        }

        if (Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.RidingPillion])
        {
            reason = "mounted or riding pillion";
            return true;
        }

        reason = "";
        return false;
    }

    private void LogDeferred(ushort fateId, string reason)
    {
        var now = DateTime.UtcNow;
        if (lastDeferredFateId == fateId
            && string.Equals(lastDeferredReason, reason, StringComparison.Ordinal)
            && now - lastDeferLogAt < DeferLogInterval)
        {
            return;
        }

        lastDeferredFateId = fateId;
        lastDeferredReason = reason;
        lastDeferLogAt = now;
        Plugin.Log.Information($"[LG][FATE-SYNC] Deferring fateId={fateId}: {reason}");
    }

    private void ResetFateState()
    {
        lastSyncedFateId = 0;
        lastDeferredFateId = 0;
        lastDeferredReason = "";
    }
}
