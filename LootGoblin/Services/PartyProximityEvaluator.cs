using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LootGoblin.Services;

public enum PartyTerritoryStatus
{
    Unknown,
    Same,
    Different,
}

public enum PartyPositionSource
{
    None,
    DirectActor,
    PartyList,
}

public enum PartyProximityMemberStatus
{
    Local,
    Nearby,
    Far,
    Unresolved,
    OutOfTerritory,
}

public readonly record struct PartyProximityMember(
    string Name,
    ulong ContentId,
    uint WorldId,
    uint EntityId,
    bool IsLocalPlayer,
    PartyTerritoryStatus TerritoryStatus,
    bool IsLoaded,
    bool HasPosition,
    Vector3 Position,
    PartyPositionSource PositionSource);

public sealed class PartyProximityMemberEvaluation
{
    public PartyProximityMember Member { get; init; }
    public PartyProximityMemberStatus Status { get; init; }
    public double? XzDistance { get; init; }
}

public sealed class PartyProximityResult
{
    public bool CanProceed { get; init; }
    public bool SnapshotValid { get; init; }
    public bool TimedOut { get; init; }
    public bool GuardedRecoveryUsed { get; init; }
    public bool LocalSnapshotValid { get; init; }
    public int NearbyOthers { get; init; }
    public int RequiredOthers { get; init; }
    public int TotalOthers { get; init; }
    public int ResolvedSameTerritoryCount { get; init; }
    public int SameTerritoryFarCount { get; init; }
    public int UnresolvedCount { get; init; }
    public int OutOfTerritoryCount { get; init; }
    public IReadOnlyList<PartyProximityMemberEvaluation> Members { get; init; } =
        Array.Empty<PartyProximityMemberEvaluation>();
}

public static class PartyGateSemantics
{
    public static bool IsLoadedSameTerritory(bool isLoaded, PartyTerritoryStatus territoryStatus)
        => isLoaded && territoryStatus == PartyTerritoryStatus.Same;

    public static bool IsLoadedSameTerritoryMounted(
        bool isLoaded,
        PartyTerritoryStatus territoryStatus,
        bool isMounted)
        => IsLoadedSameTerritory(isLoaded, territoryStatus) && isMounted;

    public static int ResolveRequiredOthers(
        int totalOthers,
        bool useCountThreshold,
        int configuredRequiredOthers)
    {
        if (totalOthers <= 0)
            return 0;

        return useCountThreshold
            ? Math.Min(Math.Clamp(configuredRequiredOthers, 1, 7), totalOthers)
            : totalOthers;
    }

    public static bool HasRequiredOthers(
        int readyOthers,
        int totalOthers,
        bool useCountThreshold,
        int configuredRequiredOthers)
    {
        var requiredOthers = ResolveRequiredOthers(
            totalOthers,
            useCountThreshold,
            configuredRequiredOthers);

        return requiredOthers == 0 || readyOthers >= requiredOthers;
    }
}

public static class PartyProximityEvaluator
{
    public static PartyProximityResult Evaluate(
        bool snapshotValid,
        IReadOnlyList<PartyProximityMember> members,
        double maxXzDistance,
        bool useCountThreshold,
        int configuredRequiredOthers,
        bool timedOut)
    {
        if (!snapshotValid)
        {
            return new PartyProximityResult
            {
                CanProceed = false,
                SnapshotValid = false,
                TimedOut = timedOut,
                RequiredOthers = Math.Max(0, configuredRequiredOthers),
            };
        }

        var local = members.FirstOrDefault(member => member.IsLocalPlayer);
        var localValid = local.IsLocalPlayer && local.HasPosition;
        if (!localValid)
        {
            return new PartyProximityResult
            {
                CanProceed = false,
                SnapshotValid = true,
                TimedOut = timedOut,
                LocalSnapshotValid = false,
                TotalOthers = Math.Max(0, members.Count(member => !member.IsLocalPlayer)),
                RequiredOthers = Math.Max(0, configuredRequiredOthers),
                Members = members
                    .Select(member => new PartyProximityMemberEvaluation
                    {
                        Member = member,
                        Status = member.IsLocalPlayer
                            ? PartyProximityMemberStatus.Local
                            : PartyProximityMemberStatus.Unresolved,
                    })
                    .ToList(),
            };
        }

        var evaluations = new List<PartyProximityMemberEvaluation>(members.Count);
        var totalOthers = 0;
        var nearbyOthers = 0;
        var resolvedSameTerritory = 0;
        var sameTerritoryFar = 0;
        var unresolved = 0;
        var outOfTerritory = 0;

        foreach (var member in members)
        {
            if (member.IsLocalPlayer)
            {
                evaluations.Add(new PartyProximityMemberEvaluation
                {
                    Member = member,
                    Status = PartyProximityMemberStatus.Local,
                    XzDistance = 0,
                });
                continue;
            }

            totalOthers++;

            if (member.TerritoryStatus == PartyTerritoryStatus.Different)
            {
                outOfTerritory++;
                evaluations.Add(new PartyProximityMemberEvaluation
                {
                    Member = member,
                    Status = PartyProximityMemberStatus.OutOfTerritory,
                });
                continue;
            }

            if (member.TerritoryStatus != PartyTerritoryStatus.Same || !member.HasPosition)
            {
                unresolved++;
                evaluations.Add(new PartyProximityMemberEvaluation
                {
                    Member = member,
                    Status = PartyProximityMemberStatus.Unresolved,
                });
                continue;
            }

            resolvedSameTerritory++;
            var xzDistance = CalculateXZDistance(local.Position, member.Position);
            if (xzDistance <= maxXzDistance)
            {
                nearbyOthers++;
                evaluations.Add(new PartyProximityMemberEvaluation
                {
                    Member = member,
                    Status = PartyProximityMemberStatus.Nearby,
                    XzDistance = xzDistance,
                });
                continue;
            }

            sameTerritoryFar++;
            evaluations.Add(new PartyProximityMemberEvaluation
            {
                Member = member,
                Status = PartyProximityMemberStatus.Far,
                XzDistance = xzDistance,
            });
        }

        if (totalOthers == 0)
        {
            return new PartyProximityResult
            {
                CanProceed = true,
                SnapshotValid = true,
                TimedOut = timedOut,
                LocalSnapshotValid = true,
                Members = evaluations,
            };
        }

        var requiredOthers = PartyGateSemantics.ResolveRequiredOthers(
            totalOthers,
            useCountThreshold,
            configuredRequiredOthers);

        var canProceed = nearbyOthers >= requiredOthers;
        var guardedRecoveryUsed = false;
        if (timedOut && !useCountThreshold)
        {
            canProceed = resolvedSameTerritory > 0 && sameTerritoryFar == 0;
            guardedRecoveryUsed = canProceed && (unresolved > 0 || outOfTerritory > 0);
        }

        return new PartyProximityResult
        {
            CanProceed = canProceed,
            SnapshotValid = true,
            TimedOut = timedOut,
            GuardedRecoveryUsed = guardedRecoveryUsed,
            LocalSnapshotValid = true,
            NearbyOthers = nearbyOthers,
            RequiredOthers = requiredOthers,
            TotalOthers = totalOthers,
            ResolvedSameTerritoryCount = resolvedSameTerritory,
            SameTerritoryFarCount = sameTerritoryFar,
            UnresolvedCount = unresolved,
            OutOfTerritoryCount = outOfTerritory,
            Members = evaluations,
        };
    }

    private static double CalculateXZDistance(Vector3 from, Vector3 to)
    {
        var dx = from.X - to.X;
        var dz = from.Z - to.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
