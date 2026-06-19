using System;
using System.Numerics;

namespace LootGoblin.Services;

internal readonly record struct AlexandriteRunLimitState(
    int RequestedRuns,
    int MaxRunnableRuns,
    int InventoryMapCount,
    int ActiveMapCount,
    int PurchasableMapCount,
    bool CanStart);

internal enum AlexandriteBuyStartAction
{
    UseInventoryMap,
    SkipLifestream,
    UseLifestream,
}

internal enum AlexandriteLifestreamArrivalWaitReason
{
    None,
    Loading,
    WrongTerritory,
    Settling,
    NoPlayer,
    Casting,
    PlayerUnavailable,
}

internal readonly record struct AlexandriteLifestreamArrivalDecision(
    bool CanAdvance,
    AlexandriteLifestreamArrivalWaitReason WaitReason);

internal enum AlexandriteApproachAction
{
    WaitForPlayerPosition,
    Mount,
    WaitForMounting,
    MoveToAuriana,
    Dismount,
    Interact,
}

internal static class AlexandritePolicy
{
    public const uint MysteriousMapItemId = 7884;
    public const uint MorDhonaTerritoryId = 156;
    public const int PoeticsPerMysteriousMap = 75;
    public const float AurianaLifestreamSkipXzDistance = 50f;
    public const float AurianaInteractionDistance = 5f;
    public const string LifestreamRevenantsTollCommand = "/li rev";
    private const double DistanceEpsilon = 0.0001d;

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

    public static AlexandriteBuyStartAction EvaluateBuyStart(
        int inventoryMapCount,
        uint territoryId,
        Vector3? playerPosition,
        Vector3 aurianaPosition)
    {
        if (inventoryMapCount > 0)
            return AlexandriteBuyStartAction.UseInventoryMap;

        if (territoryId != MorDhonaTerritoryId || playerPosition is not { } position)
            return AlexandriteBuyStartAction.UseLifestream;

        return CalculateXzDistance(position, aurianaPosition) < AurianaLifestreamSkipXzDistance - DistanceEpsilon
            ? AlexandriteBuyStartAction.SkipLifestream
            : AlexandriteBuyStartAction.UseLifestream;
    }

    public static AlexandriteLifestreamArrivalDecision EvaluateLifestreamArrival(
        bool loading,
        uint territoryId,
        TimeSpan settleElapsed,
        TimeSpan requiredSettleDelay,
        bool hasPlayer,
        bool playerIsCasting,
        bool conditionCasting,
        bool playerAvailable)
    {
        if (loading)
            return new(false, AlexandriteLifestreamArrivalWaitReason.Loading);

        if (territoryId != MorDhonaTerritoryId)
            return new(false, AlexandriteLifestreamArrivalWaitReason.WrongTerritory);

        if (settleElapsed < requiredSettleDelay)
            return new(false, AlexandriteLifestreamArrivalWaitReason.Settling);

        if (!hasPlayer)
            return new(false, AlexandriteLifestreamArrivalWaitReason.NoPlayer);

        if (playerIsCasting || conditionCasting)
            return new(false, AlexandriteLifestreamArrivalWaitReason.Casting);

        if (!playerAvailable)
            return new(false, AlexandriteLifestreamArrivalWaitReason.PlayerUnavailable);

        return new(true, AlexandriteLifestreamArrivalWaitReason.None);
    }

    public static AlexandriteApproachAction EvaluateApproach(
        bool hasPlayerPosition,
        float distanceToAuriana,
        bool mounted,
        bool mounting)
    {
        if (!hasPlayerPosition)
            return AlexandriteApproachAction.WaitForPlayerPosition;

        if (mounting)
            return AlexandriteApproachAction.WaitForMounting;

        if (distanceToAuriana < AurianaInteractionDistance)
            return mounted
                ? AlexandriteApproachAction.Dismount
                : AlexandriteApproachAction.Interact;

        return mounted
            ? AlexandriteApproachAction.MoveToAuriana
            : AlexandriteApproachAction.Mount;
    }

    public static bool ShouldBypassStartMapRefresh(uint pendingMapTargetItemId)
        => pendingMapTargetItemId == MysteriousMapItemId;

    private static double CalculateXzDistance(Vector3 from, Vector3 to)
    {
        var dx = from.X - to.X;
        var dz = from.Z - to.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
