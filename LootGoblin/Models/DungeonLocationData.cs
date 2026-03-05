using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LootGoblin.Models;

public class DungeonTransitionPoint
{
    public Vector3 Position { get; }
    public string Label { get; }

    public DungeonTransitionPoint(float x, float y, float z, string label)
    {
        Position = new Vector3(x, y, z);
        Label = label;
    }
}

public static class DungeonLocationData
{
    // Key: Territory ID
    // Value: (DungeonStart, List of DoorTransitions)
    private static readonly Dictionary<uint, (DungeonTransitionPoint Start, List<DungeonTransitionPoint> Doors)> Dungeons = new()
    {
        // Territory 712: The Lost Canals of Uznair
        {
            712,
            (
                new DungeonTransitionPoint(0.018579918891191f, 149.7960357666f, 388.267578125f, "Dungeon Start"),
                new List<DungeonTransitionPoint>
                {
                    // Room 1
                    new(-22.346755981445f, 99.70531463623f, 277.4631652832f, "Room 1 Left Door"),
                    new(23.240827560425f, 99.085502624512f, 276.923828125f, "Room 1 Right Door"),
                    // Room 2
                    new(-22.666055679321f, 49.530879974365f, 157.37890625f, "Room 2 Left Door"),
                    new(22.353483200073f, 49.650783538818f, 157.34358215332f, "Room 2 Right Door"),
                    // Room 3
                    new(-22.334154129028f, -0.1754378080368f, 37.725860595703f, "Room 3 Left Door"),
                    new(22.305755615234f, -0.16592562198639f, 37.719509124756f, "Room 3 Right Door"),
                    // Room 4
                    new(-22.364219665527f, -50.172252655029f, -82.23673248291f, "Room 4 Left Door"),
                    new(22.503517150879f, -50.322967529297f, -82.445877075195f, "Room 4 Right Door"),
                    // Room 5
                    new(-22.151628494263f, -100.21077728271f, -202.53842163086f, "Room 5 Left Door"),
                    new(22.411542892456f, -100.28004455566f, -202.43859863281f, "Room 5 Right Door"),
                    // Room 6
                    new(-23.203735351562f, -150.77336120605f, -322.78707885742f, "Room 6 Left Door"),
                    new(22.608999252319f, -150.35568237305f, -322.41604614258f, "Room 6 Right Door"),
                }
            )
        },

        // Territory 725: The Hidden Canals of Uznair
        {
            725,
            (
                new DungeonTransitionPoint(0.018579918891191f, 149.7960357666f, 388.267578125f, "Dungeon Start"),
                new List<DungeonTransitionPoint>
                {
                    // Room 1
                    new(-22.346755981445f, 99.70531463623f, 277.4631652832f, "Room 1 Left Door"),
                    new(0.1539793163538f, 100.00548553467f, 267.77084350586f, "Room 1 Centre Door"),
                    new(23.240827560425f, 99.085502624512f, 276.923828125f, "Room 1 Right Door"),
                    // Room 2
                    new(-22.666055679321f, 49.530879974365f, 157.37890625f, "Room 2 Left Door"),
                    new(0.23556911945343f, 49.569042205811f, 147.67918395996f, "Room 2 Centre Door"),
                    new(22.353483200073f, 49.650783538818f, 157.34358215332f, "Room 2 Right Door"),
                    // Room 3
                    new(-22.334154129028f, -0.1754378080368f, 37.725860595703f, "Room 3 Left Door"),
                    new(0.18939842283726f, -0.62255167961121f, 27.164403915405f, "Room 3 Centre Door"),
                    new(22.305755615234f, -0.16592562198639f, 37.719509124756f, "Room 3 Right Door"),
                    // Room 4
                    new(-22.364219665527f, -50.172252655029f, -82.23673248291f, "Room 4 Left Door"),
                    new(-0.35707533359528f, -50.351070404053f, -92.11400604248f, "Room 4 Centre Door"),
                    new(22.503517150879f, -50.322967529297f, -82.445877075195f, "Room 4 Right Door"),
                    // Room 5
                    new(-22.151628494263f, -100.21077728271f, -202.53842163086f, "Room 5 Left Door"),
                    new(-0.24459838867188f, -100.71656799316f, -213.06108093262f, "Room 5 Centre Door"),
                    new(22.411542892456f, -100.28004455566f, -202.43859863281f, "Room 5 Right Door"),
                    // Room 6
                    new(-23.203735351562f, -150.77336120605f, -322.78707885742f, "Room 6 Left Door"),
                    new(0.18377348780632f, -150.18067932129f, -331.67248535156f, "Room 6 Centre Door"),
                    new(22.608999252319f, -150.35568237305f, -322.41604614258f, "Room 6 Right Door"),
                }
            )
        },
    };

    /// <summary>
    /// Get the dungeon start position for a given territory ID.
    /// Returns null if the territory has no known dungeon data.
    /// </summary>
    public static DungeonTransitionPoint? GetDungeonStart(uint territoryId)
    {
        return Dungeons.TryGetValue(territoryId, out var data) ? data.Start : null;
    }

    /// <summary>
    /// Find the nearest door transition point within a given range of the player.
    /// Returns null if no door transition is within range.
    /// </summary>
    public static DungeonTransitionPoint? FindNearestDoorTransition(uint territoryId, Vector3 playerPosition, float maxRange = 10f)
    {
        if (!Dungeons.TryGetValue(territoryId, out var data))
            return null;

        return data.Doors
            .Where(d => Vector3.Distance(playerPosition, d.Position) <= maxRange)
            .OrderBy(d => Vector3.Distance(playerPosition, d.Position))
            .FirstOrDefault();
    }

    /// <summary>
    /// Check if a territory ID has known dungeon location data.
    /// </summary>
    public static bool HasDungeonData(uint territoryId)
    {
        return Dungeons.ContainsKey(territoryId);
    }
}
