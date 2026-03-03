namespace LootGoblin.Models;

public class MapLocation
{
    public uint TerritoryId { get; set; }
    public string ZoneName { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public uint NearestAetheryteId { get; set; }
    public string NearestAetheryteName { get; set; } = string.Empty;
    public bool IsResolved { get; set; }
}
