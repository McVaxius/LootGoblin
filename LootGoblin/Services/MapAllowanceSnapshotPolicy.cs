namespace LootGoblin.Services;

internal static class MapAllowanceSnapshotPolicy
{
    public static bool ShouldWrite(ulong activeContentId, ulong snapshotContentId)
        => snapshotContentId != 0 && activeContentId == snapshotContentId;
}
