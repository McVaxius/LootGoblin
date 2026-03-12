using System.Collections.Generic;

namespace LootGoblin.Services;

/// <summary>
/// Community-sourced default aetheryte positions.
/// These are used as seed data for new users so they don't need to cycle all aetherytes.
/// Positions get corrected organically as users teleport (passive recording overwrites these).
/// </summary>
public static class DefaultAetheryteData
{
    public static Dictionary<uint, AetherytePosition> GetDefaults()
    {
        return new Dictionary<uint, AetherytePosition>
        {
            // ARR - Gridania
            { 2, new AetherytePosition { AetheryteId = 2, Name = "New Gridania", X = 50.5f, Y = 5.6f, Z = -14.3f } },
            { 3, new AetherytePosition { AetheryteId = 3, Name = "Bentbranch Meadows", X = 50.5f, Y = 5.6f, Z = -14.3f } },
            { 4, new AetherytePosition { AetheryteId = 4, Name = "The Hawthorne Hut", X = 13.1f, Y = -1.2f, Z = 44.2f } },
            { 5, new AetherytePosition { AetheryteId = 5, Name = "Quarrymill", X = -193.3f, Y = 2.2f, Z = 286.3f } },
            { 6, new AetherytePosition { AetheryteId = 6, Name = "Camp Tranquil", X = -193.3f, Y = 2.2f, Z = 286.3f } },
            { 7, new AetherytePosition { AetheryteId = 7, Name = "Fallgourd Float", X = -233.9f, Y = 21.5f, Z = 349.8f } },
            // ARR - Limsa
            { 8, new AetherytePosition { AetheryteId = 8, Name = "Limsa Lominsa Lower Decks", X = 108.3f, Y = -20f, Z = -7.7f } },
            { 10, new AetherytePosition { AetheryteId = 10, Name = "Moraby Drydocks", X = -752.2f, Y = -40f, Z = -537.2f } },
            { 11, new AetherytePosition { AetheryteId = 11, Name = "Costa del Sol", X = 156.5f, Y = 14.1f, Z = 682.5f } },
            { 12, new AetherytePosition { AetheryteId = 12, Name = "Wineport", X = 495f, Y = 17.4f, Z = 465.3f } },
            { 13, new AetherytePosition { AetheryteId = 13, Name = "Swiftperch", X = 495f, Y = 17.4f, Z = 465.3f } },
            { 14, new AetherytePosition { AetheryteId = 14, Name = "Aleport", X = 651.8f, Y = 9.4f, Z = 507.7f } },
            { 15, new AetherytePosition { AetheryteId = 15, Name = "Camp Bronze Lake", X = 651.8f, Y = 9.4f, Z = 507.7f } },
            { 16, new AetherytePosition { AetheryteId = 16, Name = "Camp Overlook", X = 436.4f, Y = 4.2f, Z = 83.4f } },
            { 52, new AetherytePosition { AetheryteId = 52, Name = "Summerford Farms", X = -752.2f, Y = -40f, Z = -537.2f } },
            { 55, new AetherytePosition { AetheryteId = 55, Name = "Wolves' Den Pier", X = 436.4f, Y = 4.2f, Z = 83.4f } },
            // ARR - Ul'dah
            { 9, new AetherytePosition { AetheryteId = 9, Name = "Ul'dah - Steps of Nald", X = -233.9f, Y = 21.5f, Z = 349.8f } },
            { 17, new AetherytePosition { AetheryteId = 17, Name = "Horizon", X = -140.2f, Y = -3.2f, Z = -174.5f } },
            { 18, new AetherytePosition { AetheryteId = 18, Name = "Camp Drybone", X = -15.4f, Y = -1.9f, Z = -162.7f } },
            { 19, new AetherytePosition { AetheryteId = 19, Name = "Little Ala Mhigo", X = -15.4f, Y = -1.9f, Z = -162.7f } },
            { 20, new AetherytePosition { AetheryteId = 20, Name = "Forgotten Springs", X = -163.4f, Y = 26.1f, Z = -421.4f } },
            { 21, new AetherytePosition { AetheryteId = 21, Name = "Camp Bluefog", X = -163.4f, Y = 26.1f, Z = -421.4f } },
            { 22, new AetherytePosition { AetheryteId = 22, Name = "Ceruleum Processing Plant", X = 21.4f, Y = 7f, Z = 462.2f } },
            { 53, new AetherytePosition { AetheryteId = 53, Name = "Black Brush Station", X = -140.2f, Y = -3.2f, Z = -174.5f } },
            // ARR - Coerthas / Mor Dhona
            { 23, new AetherytePosition { AetheryteId = 23, Name = "Camp Dragonhead", X = 9.3f, Y = 0f, Z = -7f } },
            { 24, new AetherytePosition { AetheryteId = 24, Name = "Revenant's Toll", X = -147.1f, Y = -281.5f, Z = 203.1f } },
            // ARR - Special
            { 58, new AetherytePosition { AetheryteId = 58, Name = "Estate Hall (Free Company)", X = -233.3f, Y = 6f, Z = 169.7f } },
            { 62, new AetherytePosition { AetheryteId = 62, Name = "The Gold Saucer", X = 21.4f, Y = 7f, Z = 462.2f } },
            { 165, new AetherytePosition { AetheryteId = 165, Name = "Estate Hall (Private)", X = 445.2f, Y = 68f, Z = -87.7f } },
            // HW - Ishgard
            { 70, new AetherytePosition { AetheryteId = 70, Name = "Foundation", X = 9.3f, Y = 0f, Z = -7f } },
            { 71, new AetherytePosition { AetheryteId = 71, Name = "Falcon's Nest", X = 219.3f, Y = 312f, Z = -230f } },
            { 72, new AetherytePosition { AetheryteId = 72, Name = "Camp Cloudtop", X = 219.3f, Y = 312f, Z = -230f } },
            { 73, new AetherytePosition { AetheryteId = 73, Name = "Ok' Zundu", X = -628.6f, Y = -122.5f, Z = 550.9f } },
            { 74, new AetherytePosition { AetheryteId = 74, Name = "Helix", X = -628.6f, Y = -122.5f, Z = 550.9f } },
            { 75, new AetherytePosition { AetheryteId = 75, Name = "Idyllshire", X = -715.4f, Y = -187f, Z = -584.6f } },
            { 76, new AetherytePosition { AetheryteId = 76, Name = "Tailfeather", X = -715.4f, Y = -187f, Z = -584.6f } },
            { 77, new AetherytePosition { AetheryteId = 77, Name = "Anyx Trine", X = 531f, Y = -51.3f, Z = 17.3f } },
            { 78, new AetherytePosition { AetheryteId = 78, Name = "Moghome", X = 531f, Y = -51.3f, Z = 17.3f } },
            { 79, new AetherytePosition { AetheryteId = 79, Name = "Zenith", X = 266.6f, Y = -42.2f, Z = 590.3f } },
            // SB - Gyr Abania
            { 98, new AetherytePosition { AetheryteId = 98, Name = "Castrum Oriens", X = 81.9f, Y = 0f, Z = 107.1f } },
            { 99, new AetherytePosition { AetheryteId = 99, Name = "The Peering Stones", X = 81.9f, Y = 0f, Z = 107.1f } },
            { 100, new AetherytePosition { AetheryteId = 100, Name = "Ala Gannha", X = 427.2f, Y = 114.3f, Z = 243.1f } },
            { 101, new AetherytePosition { AetheryteId = 101, Name = "Ala Ghiri", X = 427.2f, Y = 114.3f, Z = 243.1f } },
            { 102, new AetherytePosition { AetheryteId = 102, Name = "Porta Praetoria", X = -277.6f, Y = 257.8f, Z = 742.4f } },
            { 103, new AetherytePosition { AetheryteId = 103, Name = "The Ala Mhigan Quarter", X = -277.6f, Y = 257.8f, Z = 742.4f } },
            { 104, new AetherytePosition { AetheryteId = 104, Name = "Rhalgr's Reach", X = 266.6f, Y = -42.2f, Z = 590.3f } },
            // SB - Far East
            { 105, new AetherytePosition { AetheryteId = 105, Name = "Tamamizu", X = 53.4f, Y = 4.5f, Z = -28.2f } },
            { 106, new AetherytePosition { AetheryteId = 106, Name = "Onokoro", X = 53.4f, Y = 4.5f, Z = -28.2f } },
            { 107, new AetherytePosition { AetheryteId = 107, Name = "Namai", X = 92.4f, Y = 3f, Z = -577.6f } },
            // NOTE: House of the Fierce coords were wrong (copied from Namai). Cleared to force Level/MapMarker fallback.
            // Passive recording will populate correct position when user teleports here.
            { 108, new AetherytePosition { AetheryteId = 108, Name = "The House of the Fierce", X = 0f, Y = 0f, Z = 0f } },
            { 109, new AetherytePosition { AetheryteId = 109, Name = "Reunion", X = 247.2f, Y = 4.5f, Z = -409.8f } },
            { 110, new AetherytePosition { AetheryteId = 110, Name = "The Dawn Throne", X = 247.2f, Y = 4.5f, Z = -409.8f } },
            { 111, new AetherytePosition { AetheryteId = 111, Name = "Kugane", X = 624.6f, Y = 81f, Z = 660.2f } },
            { 127, new AetherytePosition { AetheryteId = 127, Name = "The Doman Enclave", X = 75.1f, Y = 114.9f, Z = 22.3f } },
            { 128, new AetherytePosition { AetheryteId = 128, Name = "Dhoro Iloh", X = 75.1f, Y = 114.9f, Z = 22.3f } },
            // ShB - Norvrandt
            { 132, new AetherytePosition { AetheryteId = 132, Name = "Fort Jobb", X = -60.1f, Y = 2.8f, Z = -6.6f } },
            { 133, new AetherytePosition { AetheryteId = 133, Name = "The Crystarium", X = -209.7f, Y = 30f, Z = -594.1f } },
            { 134, new AetherytePosition { AetheryteId = 134, Name = "Eulmore", X = -60.1f, Y = 2.8f, Z = -6.6f } },
            { 136, new AetherytePosition { AetheryteId = 136, Name = "The Ostall Imperative", X = 767.8f, Y = 22.6f, Z = -30f } },
            { 137, new AetherytePosition { AetheryteId = 137, Name = "Stilltide", X = 767.8f, Y = 22.6f, Z = -30f } },
            { 138, new AetherytePosition { AetheryteId = 138, Name = "Wright", X = 669f, Y = 28.1f, Z = 294.2f } },
            { 139, new AetherytePosition { AetheryteId = 139, Name = "Tomra", X = 669f, Y = 28.1f, Z = 294.2f } },
            { 140, new AetherytePosition { AetheryteId = 140, Name = "Mord Souq", X = -431.8f, Y = 417.2f, Z = -618.2f } },
            { 141, new AetherytePosition { AetheryteId = 141, Name = "Twine", X = 409.3f, Y = -28.4f, Z = 308.5f } },
            { 142, new AetherytePosition { AetheryteId = 142, Name = "Slitherbough", X = 379.6f, Y = 86.8f, Z = -675.6f } },
            { 143, new AetherytePosition { AetheryteId = 143, Name = "Fanow", X = 379.6f, Y = 86.8f, Z = -675.6f } },
            { 144, new AetherytePosition { AetheryteId = 144, Name = "Lydha Lran", X = 409.3f, Y = -28.4f, Z = 308.5f } },
            { 145, new AetherytePosition { AetheryteId = 145, Name = "Pla Enni", X = -335.9f, Y = 48f, Z = 504.7f } },
            { 146, new AetherytePosition { AetheryteId = 146, Name = "Wolekdorf", X = -335.9f, Y = 48f, Z = 504.7f } },
            { 147, new AetherytePosition { AetheryteId = 147, Name = "The Ondo Cups", X = 384f, Y = 20.5f, Z = -185.2f } },
            { 148, new AetherytePosition { AetheryteId = 148, Name = "The Macarenses Angle", X = 384f, Y = 20.5f, Z = -185.2f } },
            { 161, new AetherytePosition { AetheryteId = 161, Name = "The Inn at Journey's Head", X = -431.8f, Y = 417.2f, Z = -618.2f } },
            // EW - Old Sharlayan / Radz-at-Han
            { 166, new AetherytePosition { AetheryteId = 166, Name = "The Archeion", X = 5f, Y = 3.3f, Z = 0.7f } },
            { 167, new AetherytePosition { AetheryteId = 167, Name = "Sharlayan Hamlet", X = 5f, Y = 3.3f, Z = 0.7f } },
            { 168, new AetherytePosition { AetheryteId = 168, Name = "Aporia", X = 2.6f, Y = -28.2f, Z = -48.4f } },
            { 169, new AetherytePosition { AetheryteId = 169, Name = "Yedlihmad", X = 36.1f, Y = 1f, Z = -12.9f } },
            { 170, new AetherytePosition { AetheryteId = 170, Name = "The Great Work", X = 200.1f, Y = 5.8f, Z = 629.7f } },
            { 171, new AetherytePosition { AetheryteId = 171, Name = "Palaka's Stand", X = 200.1f, Y = 5.8f, Z = 629.7f } },
            { 172, new AetherytePosition { AetheryteId = 172, Name = "Camp Broken Glass", X = 405.2f, Y = 3.8f, Z = -237.8f } },
            { 173, new AetherytePosition { AetheryteId = 173, Name = "Tertium", X = 405.2f, Y = 3.8f, Z = -237.8f } },
            { 174, new AetherytePosition { AetheryteId = 174, Name = "Sinus Lacrimarum", X = 2.6f, Y = -28.2f, Z = -48.4f } },
            { 175, new AetherytePosition { AetheryteId = 175, Name = "Bestways Burrow", X = -557.6f, Y = 132.5f, Z = 668.7f } },
            { 176, new AetherytePosition { AetheryteId = 176, Name = "Anagnorisis", X = 489.1f, Y = 437f, Z = 324.8f } },
            { 177, new AetherytePosition { AetheryteId = 177, Name = "The Twelve Wonders", X = 168.5f, Y = 10.4f, Z = 133.5f } },
            { 178, new AetherytePosition { AetheryteId = 178, Name = "Poieten Oikos", X = 168.5f, Y = 10.4f, Z = 133.5f } },
            { 179, new AetherytePosition { AetheryteId = 179, Name = "Reah Tahra", X = -557.6f, Y = 132.5f, Z = 668.7f } },
            { 180, new AetherytePosition { AetheryteId = 180, Name = "Abode of the Ea", X = -554.3f, Y = 71.8f, Z = 270f } },
            { 181, new AetherytePosition { AetheryteId = 181, Name = "Base Omicron", X = -554.3f, Y = 71.8f, Z = 270f } },
            { 182, new AetherytePosition { AetheryteId = 182, Name = "Old Sharlayan", X = -147.1f, Y = -281.5f, Z = 203.1f } },
            { 183, new AetherytePosition { AetheryteId = 183, Name = "Radz-at-Han", X = 36.1f, Y = 1f, Z = -12.9f } },
            // DT - Tural
            { 200, new AetherytePosition { AetheryteId = 200, Name = "Wachunpelo", X = 524.2f, Y = -36f, Z = -183f } },
            { 201, new AetherytePosition { AetheryteId = 201, Name = "Worlar's Echo", X = 341.9f, Y = -159.4f, Z = -422.4f } },
            { 202, new AetherytePosition { AetheryteId = 202, Name = "Ok'hanu", X = 341.9f, Y = -159.4f, Z = -422.4f } },
            { 203, new AetherytePosition { AetheryteId = 203, Name = "Many Fires", X = -156.5f, Y = 6.3f, Z = -480.7f } },
            { 204, new AetherytePosition { AetheryteId = 204, Name = "Earthenshire", X = 539.8f, Y = 116.6f, Z = 194.6f } },
            { 205, new AetherytePosition { AetheryteId = 205, Name = "Iq Br'aax", X = 787.1f, Y = 12.7f, Z = -223.1f } },
            { 206, new AetherytePosition { AetheryteId = 206, Name = "Mamook", X = 787.1f, Y = 12.7f, Z = -223.1f } },
            { 207, new AetherytePosition { AetheryteId = 207, Name = "Hhusatahwi", X = 729.6f, Y = -132.7f, Z = 527.3f } },
            { 208, new AetherytePosition { AetheryteId = 208, Name = "Sheshenewezi Springs", X = 376.2f, Y = 0f, Z = 469.9f } },
            { 209, new AetherytePosition { AetheryteId = 209, Name = "Mehwahhetsoan", X = 376.2f, Y = 0f, Z = 469.9f } },
            { 210, new AetherytePosition { AetheryteId = 210, Name = "Yyasulani Station", X = 320.6f, Y = -13.8f, Z = -571.1f } },
            { 211, new AetherytePosition { AetheryteId = 211, Name = "The Outskirts", X = 320.6f, Y = -13.8f, Z = -571.1f } },
            { 212, new AetherytePosition { AetheryteId = 212, Name = "Electrope Strike", X = -209.7f, Y = 30f, Z = -594.1f } },
            { 213, new AetherytePosition { AetheryteId = 213, Name = "Leynode Mnemo", X = -523.2f, Y = 159.9f, Z = -211.3f } },
            { 214, new AetherytePosition { AetheryteId = 214, Name = "Leynode Pyro", X = -523.2f, Y = 159.9f, Z = -211.3f } },
            { 215, new AetherytePosition { AetheryteId = 215, Name = "Leynode Aero", X = 668.5f, Y = 25.1f, Z = -297.5f } },
            { 216, new AetherytePosition { AetheryteId = 216, Name = "Tuliyollal", X = 524.2f, Y = -36f, Z = -183f } },
            { 217, new AetherytePosition { AetheryteId = 217, Name = "Solution Nine", X = 729.6f, Y = -132.7f, Z = 527.3f } },
            { 238, new AetherytePosition { AetheryteId = 238, Name = "Dock Poga", X = 539.8f, Y = 116.6f, Z = 194.6f } },
        };
    }
}
