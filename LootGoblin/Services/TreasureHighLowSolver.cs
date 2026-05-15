using System;
using System.Collections.Generic;

namespace LootGoblin.Services;

public enum TreasureHighLowAction
{
    PlayHigher,
    PlayLower,
    CashOut,
}

public readonly record struct TreasureHighLowDecision(
    TreasureHighLowAction Action,
    string Reason);

public static class TreasureHighLowSolver
{
    public static TreasureHighLowDecision Decide(int gambleNumber, int currentCard)
    {
        if (gambleNumber < 1 || gambleNumber > 5)
            throw new ArgumentOutOfRangeException(nameof(gambleNumber), gambleNumber, "Gamble number must be 1-5.");

        if (currentCard < 1 || currentCard > 9)
            throw new ArgumentOutOfRangeException(nameof(currentCard), currentCard, "Card must be 1-9.");

        if (ShouldCashOut(gambleNumber, currentCard))
            return new TreasureHighLowDecision(
                TreasureHighLowAction.CashOut,
                $"stage={gambleNumber} card={currentCard} is in stop set");

        return currentCard <= 5
            ? new TreasureHighLowDecision(
                TreasureHighLowAction.PlayHigher,
                $"stage={gambleNumber} card={currentCard} -> higher")
            : new TreasureHighLowDecision(
                TreasureHighLowAction.PlayLower,
                $"stage={gambleNumber} card={currentCard} -> lower");
    }

    public static bool ShouldCashOut(int gambleNumber, int currentCard)
    {
        var stopCards = GetStopCards(gambleNumber);
        return stopCards.Contains(currentCard);
    }

    public static IReadOnlySet<int> GetStopCards(int gambleNumber)
        => gambleNumber switch
        {
            1 => EmptyStopCards,
            2 => StageTwoStopCards,
            3 => StageThreeStopCards,
            4 or 5 => StageFourFiveStopCards,
            _ => throw new ArgumentOutOfRangeException(nameof(gambleNumber), gambleNumber, "Gamble number must be 1-5."),
        };

    private static readonly IReadOnlySet<int> EmptyStopCards = new HashSet<int>();
    private static readonly IReadOnlySet<int> StageTwoStopCards = new HashSet<int> { 5 };
    private static readonly IReadOnlySet<int> StageThreeStopCards = new HashSet<int> { 4, 5, 6 };
    private static readonly IReadOnlySet<int> StageFourFiveStopCards = new HashSet<int> { 3, 4, 5, 6, 7 };
}
