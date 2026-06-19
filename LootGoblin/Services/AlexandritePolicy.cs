using System;

namespace LootGoblin.Services;

internal readonly record struct AlexandriteRunLimitState(
    int RequestedRuns,
    int MaxRunnableRuns,
    int InventoryMapCount,
    int ActiveMapCount,
    int PurchasableMapCount,
    bool CanStart);

internal static class AlexandritePolicy
{
    public const uint MysteriousMapItemId = 7884;
    public const int PoeticsPerMysteriousMap = 75;

    public static AlexandriteRunLimitState EvaluateRunLimit(
        int requestedRuns,
        int inventoryMapCount,
        bool hasActiveMysteriousMap,
        int currentPoetics)
    {
        var safeInventoryMapCount = Math.Max(0, inventoryMapCount);
        var safeCurrentPoetics = Math.Max(0, currentPoetics);
        var activeMapCount = hasActiveMysteriousMap ? 1 : 0;
        var purchasableMapCount = safeCurrentPoetics / PoeticsPerMysteriousMap;
        var maxRunnableRuns = safeInventoryMapCount + activeMapCount + purchasableMapCount;
        var clampedRequestedRuns = maxRunnableRuns > 0
            ? Math.Clamp(requestedRuns, 1, maxRunnableRuns)
            : 0;

        return new AlexandriteRunLimitState(
            clampedRequestedRuns,
            maxRunnableRuns,
            safeInventoryMapCount,
            activeMapCount,
            purchasableMapCount,
            maxRunnableRuns > 0);
    }
}
