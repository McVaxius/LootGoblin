namespace LootGoblin.Models;

public enum BotState
{
    Idle,
    SelectingMap,
    OpeningMap,
    DetectingLocation,
    Teleporting,
    Mounting,
    WaitingForParty,
    Flying,
    OpeningChest,
    InCombat,
    InDungeon,
    Completed,
    Error,
}
