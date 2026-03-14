namespace LootGoblin.Models;

/// <summary>
/// Special navigation entry for underwater maps with pre-dive and main coordinates.
/// Manually updated via SpecialNavigation.json file.
/// </summary>
public class SpecialNavigationEntry
{
    /// <summary>Links to MapLocationEntry.Index</summary>
    public int DestinationIndex { get; set; }
    
    /// <summary>Zone name for reference</summary>
    public string ZoneName { get; set; } = "";
    
    /// <summary>Pre-diving coordinates (surface approach)</summary>
    public float PreX { get; set; }
    public float PreY { get; set; }
    public float PreZ { get; set; }
    
    /// <summary>Main coordinates (when diving/Condition 81 is true)</summary>
    public float MainX { get; set; }
    public float MainY { get; set; }
    public float MainZ { get; set; }
    
    /// <summary>Manual notes for reference</summary>
    public string? Notes { get; set; }
    
    /// <summary>Enable/disable special navigation</summary>
    public bool IsActive { get; set; } = true;
}
