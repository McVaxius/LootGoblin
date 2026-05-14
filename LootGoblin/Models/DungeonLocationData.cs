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
        // Territory 558: The Aquapolis (Dungeon Type, Door: "Vault Door", 7 rooms, single path, no cutscene - player walks to next room)
        {
            558,
            (
                new DungeonTransitionPoint(1.0083782672882f, 0.19999814033508f, 340.36688232422f, "Room 1 Start"),
                new List<DungeonTransitionPoint>
                {
                    new(-0.016964452341199f, -7.8000040054321f, 217.08427429199f, "Room 2"),
                    new(0.0065348446369171f, -15.800004959106f, 92.169876098633f, "Room 3"),
                    new(-0.12571297585964f, -23.800001144409f, -30.496042251587f, "Room 4"),
                    new(0.25867503881454f, -31.724830627441f, -157.66818237305f, "Room 5"),
                    new(-0.096912704408169f, -39.779590606689f, -282.12805175781f, "Room 6"),
                    new(-0.095316514372826f, -47.70182800293f, -403.92584228516f, "Room 7"),
                }
            )
        },

        // Territory 879: The Dungeons of Lyhe Ghiah (Dungeon Type, Door: "Elaborate Gate", 5 rooms, left/right paths)
        {
            879,
            (
                new DungeonTransitionPoint(0.30181908607483f, -39.97151184082f, 142.62704467773f, "Room 1 Start"),
                new List<DungeonTransitionPoint>
                {
                    // Room 2 (left and right are same position)
                    new(28.071523666382f, -39.235473632812f, 101.03690338135f, "Room 2 Left Door"),
                    new(28.071523666382f, -39.235473632812f, 101.03690338135f, "Room 2 Right Door"),
                    // Room 3
                    new(-29.093864440918f, 1.1753497123718f, -29.101762771606f, "Room 3 Left Door"),
                    new(29.330530166626f, 1.2513842582703f, -29.013732910156f, "Room 3 Right Door"),
                    // Room 4
                    new(-29.061462402344f, 41.129096984863f, -158.93838500977f, "Room 4 Left Door"),
                    new(29.223150253296f, 41.200305938721f, -158.97257995605f, "Room 4 Right Door"),
                    // Room 5
                    new(-28.82586479187f, 81.004730224609f, -288.78784179688f, "Room 5 Left Door"),
                    new(29.285348892212f, 81.20580291748f, -288.87875366211f, "Room 5 Right Door"),
                }
            )
        },

        // Territory 1000: The Excitatron 6000 (Dungeon Type, Door: "Stage Door", 5 rooms, left/right paths)
        {
            1000,
            (
                new DungeonTransitionPoint(0.032300509512424f, 20.000007629395f, 254.26850891113f, "Room 1 Start"),
                new List<DungeonTransitionPoint>
                {
                    // Room 2
                    new(80.953315734863f, -10.038639068604f, 101.36717224121f, "Room 2 Left Door"),
                    new(138.77351379395f, -10.038636207581f, 101.35768127441f, "Room 2 Right Door"),
                    // Room 3
                    new(81.37922668457f, -10.038649559021f, -48.605068206787f, "Room 3 Left Door"),
                    new(138.8713684082f, -10.038649559021f, -48.890167236328f, "Room 3 Right Door"),
                    // Room 4
                    new(-138.51029968262f, 19.961349487305f, -168.72546386719f, "Room 4 Left Door"),
                    new(-81.48990031738f, 19.961349487305f, -168.72546386719f, "Room 4 Right Door"),
                    // Room 5
                    new(-138.91505432129f, 19.961368560791f, -319.07586669922f, "Room 5 Left Door"),
                    new(-81.359451293945f, 19.961378097534f, -318.83532714844f, "Room 5 Right Door"),
                }
            )
        },

        // Territory 1209: Cenote Ja Ja Gural (Dungeon Type, Door: "Vault Door", 5 rooms, left/right paths)
        {
            1209,
            (
                new DungeonTransitionPoint(0.12231740355492f, -400.0f, 377.30017089844f, "Room 1 Start"),
                new List<DungeonTransitionPoint>
                {
                    // Room 2
                    new(-35.063709259033f, -400.00003051758f, 341.51950073242f, "Room 2 Left Door"),
                    new(35.617687225342f, -400.0f, 341.68154907227f, "Room 2 Right Door"),
                    // Room 3
                    new(-35.291564941406f, -400.0f, 156.95146179199f, "Room 3 Left Door"),
                    new(35.601676940918f, -400.0f, 156.61825561523f, "Room 3 Right Door"),
                    // Room 4
                    new(124.29012298584f, -290.00003051758f, -16.546094894409f, "Room 4 Left Door"),
                    new(195.96708679199f, -290.00015258789f, -16.54662322998f, "Room 4 Right Door"),
                    // Room 5
                    new(-232.17561340332f, -169.0f, -180.32594299316f, "Room 5 Left Door"),
                    new(-162.12966918945f, -169.00015258789f, -179.98109436035f, "Room 5 Right Door"),
                }
            )
        },
    };

    // Door names by territory for object detection
    private static readonly Dictionary<uint, string> DoorNames = new()
    {
        { 558, "Vault Door" },
        { 712, "Sluice Gate" },
        { 725, "Sluice Gate" },
        { 879, "Elaborate Gate" },
        { 1000, "Stage Door" },
        { 1209, "Vault Door" },
    };

    // Sphere/roulette names by territory for object detection
    private static readonly Dictionary<uint, string> SphereNames = new()
    {
        { 924, "Arcane Sphere" },
        { 1123, "Arcane Sphere" },
        { 1279, "Hypnoslot Machine" },
    };

    // Room count by territory
    private static readonly Dictionary<uint, int> RoomCounts = new()
    {
        { 558, 7 },
        { 712, 7 },
        { 725, 7 },
        { 879, 5 },
        { 924, 0 }, // Roulette
        { 1000, 5 },
        { 1123, 0 }, // Roulette
        { 1209, 5 },
        { 1279, 0 }, // Roulette
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

    /// <summary>
    /// Get the door name for a given territory (e.g. "Vault Door", "Elaborate Gate", "Stage Door").
    /// </summary>
    public static string? GetDoorName(uint territoryId)
    {
        return DoorNames.TryGetValue(territoryId, out var name) ? name : null;
    }

    /// <summary>
    /// Get the sphere/roulette object name for a given territory (e.g. "Arcane Sphere", "Hypnoslot Machine").
    /// </summary>
    public static string? GetSphereName(uint territoryId)
    {
        return SphereNames.TryGetValue(territoryId, out var name) ? name : null;
    }

    /// <summary>
    /// Get the room count for a given territory. Returns 0 for roulette-type dungeons.
    /// </summary>
    public static int GetRoomCount(uint territoryId)
    {
        return RoomCounts.TryGetValue(territoryId, out var count) ? count : 0;
    }

    /// <summary>
    /// Get all known sphere/roulette object names across all dungeons.
    /// </summary>
    public static IEnumerable<string> GetAllSphereNames()
    {
        return SphereNames.Values.Distinct();
    }

    /// <summary>
    /// Get all known door names across all dungeons.
    /// </summary>
    public static IEnumerable<string> GetAllDoorNames()
    {
        return DoorNames.Values.Distinct();
    }
}
