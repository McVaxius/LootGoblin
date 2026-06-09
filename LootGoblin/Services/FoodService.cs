using System;
using Dalamud.Game.ClientState.Conditions;

namespace LootGoblin.Services;

public sealed class FoodService
{
    private const long FoodCheckMs = 10000;
    private const long FoodAttemptMs = 5000;
    private const long FoodSuccessDelayMs = 20000;

    private readonly Plugin plugin;

    private long nextFoodCheckMs;
    private long lastFoodAttemptMs;
    private uint resolvedFoodItemId;
    private string resolvedFoodItemName = "";
    private bool resolvedFoodUseHighQuality;
    private bool foodIdResolved;
    private int cachedFeedMeItemId = int.MinValue;
    private string cachedFeedMeItem = "";
    private bool cachedFeedMeUseHighQuality;

    public string FoodStatus { get; private set; } = "";

    public FoodService(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Update()
    {
        var config = plugin.Configuration;
        if (!IsFoodConfigured(config))
        {
            FoodStatus = "";
            return;
        }

        if (!config.Enabled)
        {
            FoodStatus = "Bot disabled";
            return;
        }

        var now = Environment.TickCount64;
        if (now < nextFoodCheckMs)
            return;

        nextFoodCheckMs = now + FoodCheckMs;
        CheckFood(config, now);
    }

    private void CheckFood(Configuration config, long now)
    {
        if (!CanEatNow(out var blockedReason))
        {
            FoodStatus = $"Paused: {blockedReason}";
            return;
        }

        if (BackfillLegacyFoodConfig(config))
            InvalidateFoodCache();

        InvalidateFoodCacheIfConfigChanged(config);

        if (!foodIdResolved)
            ResolveFoodItemId(config);

        if (resolvedFoodItemId == 0)
        {
            FoodStatus = "No food item resolved";
            return;
        }

        var wellFedRemaining = GameHelpers.GetStatusTimeRemaining(GameHelpers.WellFedStatusId);
        if (wellFedRemaining > 90f)
        {
            FoodStatus = $"Well Fed: {wellFedRemaining:F0}s ({resolvedFoodItemName} [{QualityLabel(resolvedFoodUseHighQuality)}])";
            return;
        }

        var count = GameHelpers.GetInventoryItemCount(resolvedFoodItemId, resolvedFoodUseHighQuality);
        if (count > 0)
        {
            if (now - lastFoodAttemptMs < FoodAttemptMs)
            {
                FoodStatus = $"Need food (cooldown {(FoodAttemptMs - (now - lastFoodAttemptMs)) / 1000.0:F1}s)";
                return;
            }

            lastFoodAttemptMs = now;
            var qualityLabel = QualityLabel(resolvedFoodUseHighQuality);
            Plugin.Log.Information($"Eating food: {resolvedFoodItemName} [{qualityLabel}] (ID={resolvedFoodItemId}, count={count}, wellFed={wellFedRemaining:F1}s)");

            if (GameHelpers.UseItem(resolvedFoodItemId, resolvedFoodUseHighQuality))
            {
                FoodStatus = $"Ate {resolvedFoodItemName} [{qualityLabel}] ({count - 1} left)";
                nextFoodCheckMs = now + FoodSuccessDelayMs;
            }
            else
            {
                FoodStatus = $"Failed to eat {resolvedFoodItemName} [{qualityLabel}]";
            }

            return;
        }

        if (!config.FeedMeSearch)
        {
            FoodStatus = $"Out of {resolvedFoodItemName} [{QualityLabel(resolvedFoodUseHighQuality)}]";
            return;
        }

        var (foundId, foundName, foundHighQuality, foundCount) = GameHelpers.FindBestAvailableFood();
        if (foundId > 0)
        {
            var qualityLabel = QualityLabel(foundHighQuality);
            Plugin.Log.Information($"Food search: switched from {resolvedFoodItemName} [{QualityLabel(resolvedFoodUseHighQuality)}] to {foundName} [{qualityLabel}] (ID={foundId}, count={foundCount})");
            resolvedFoodItemId = foundId;
            resolvedFoodItemName = foundName;
            resolvedFoodUseHighQuality = foundHighQuality;
            FoodStatus = $"Switched to {foundName} [{qualityLabel}]";
        }
        else
        {
            FoodStatus = "No food in inventory";
            resolvedFoodItemId = 0;
            foodIdResolved = false;
        }
    }

    private void ResolveFoodItemId(Configuration config)
    {
        foodIdResolved = true;
        resolvedFoodItemId = 0;
        resolvedFoodItemName = "";
        resolvedFoodUseHighQuality = config.FeedMeUseHighQuality;

        if (config.FeedMeItemId > 0)
        {
            resolvedFoodItemId = (uint)config.FeedMeItemId;
            resolvedFoodItemName = !string.IsNullOrWhiteSpace(config.FeedMeItem)
                ? config.FeedMeItem.Trim()
                : GameHelpers.LookupItemName((uint)config.FeedMeItemId);

            if (string.IsNullOrWhiteSpace(resolvedFoodItemName))
                resolvedFoodItemName = $"Item {config.FeedMeItemId}";

            if (string.IsNullOrWhiteSpace(config.FeedMeItem) &&
                !resolvedFoodItemName.StartsWith("Item ", StringComparison.Ordinal))
            {
                config.FeedMeItem = resolvedFoodItemName;
                config.Save();
            }

            Plugin.Log.Information($"Food resolved from config ID: {resolvedFoodItemName} [{QualityLabel(resolvedFoodUseHighQuality)}] -> ID {resolvedFoodItemId}");
            return;
        }

        var foodName = config.FeedMeItem.Trim();
        foreach (var (id, name) in GameHelpers.FoodList)
        {
            if (name.Equals(foodName, StringComparison.OrdinalIgnoreCase))
            {
                resolvedFoodItemId = id;
                resolvedFoodItemName = name;
                Plugin.Log.Information($"Food resolved from known list: {name} [{QualityLabel(resolvedFoodUseHighQuality)}] -> ID {id}");
                return;
            }
        }

        var (itemId, itemName) = GameHelpers.LookupFoodItem(foodName);
        if (itemId > 0)
        {
            resolvedFoodItemId = itemId;
            resolvedFoodItemName = itemName;
            Plugin.Log.Information($"Food resolved from Lumina: {itemName} [{QualityLabel(resolvedFoodUseHighQuality)}] -> ID {itemId}");
            return;
        }

        if (config.FeedMeSearch)
        {
            var (foundId, foundName, foundHighQuality, foundCount) = GameHelpers.FindBestAvailableFood();
            if (foundId > 0)
            {
                resolvedFoodItemId = foundId;
                resolvedFoodItemName = foundName;
                resolvedFoodUseHighQuality = foundHighQuality;
                Plugin.Log.Information($"Food search found: {foundName} [{QualityLabel(foundHighQuality)}] -> ID {foundId} (count={foundCount})");
                return;
            }
        }

        Plugin.LogWarning($"Could not resolve food item: {foodName}");
    }

    private bool BackfillLegacyFoodConfig(Configuration config)
    {
        if (config.FeedMeItemId > 0) return false;
        if (string.IsNullOrWhiteSpace(config.FeedMeItem)) return false;

        var (itemId, itemName) = GameHelpers.LookupFoodItem(config.FeedMeItem);
        if (itemId == 0) return false;

        config.FeedMeItemId = (int)itemId;
        config.FeedMeItem = itemName;
        config.Save();
        Plugin.Log.Information($"Backfilled legacy food config: {itemName} -> ID {itemId}");
        return true;
    }

    private void InvalidateFoodCacheIfConfigChanged(Configuration config)
    {
        var foodName = config.FeedMeItem ?? "";
        if (cachedFeedMeItemId == config.FeedMeItemId
            && string.Equals(cachedFeedMeItem, foodName, StringComparison.Ordinal)
            && cachedFeedMeUseHighQuality == config.FeedMeUseHighQuality)
        {
            return;
        }

        cachedFeedMeItemId = config.FeedMeItemId;
        cachedFeedMeItem = foodName;
        cachedFeedMeUseHighQuality = config.FeedMeUseHighQuality;
        foodIdResolved = false;
        resolvedFoodItemId = 0;
        resolvedFoodItemName = "";
        resolvedFoodUseHighQuality = config.FeedMeUseHighQuality;
    }

    public void InvalidateFoodCache()
    {
        resolvedFoodItemId = 0;
        resolvedFoodItemName = "";
        resolvedFoodUseHighQuality = false;
        foodIdResolved = false;
        cachedFeedMeItemId = int.MinValue;
        cachedFeedMeItem = "";
        cachedFeedMeUseHighQuality = false;
        nextFoodCheckMs = 0;
    }

    private static bool IsFoodConfigured(Configuration config)
        => config.FeedMeItemId > 0 || !string.IsNullOrWhiteSpace(config.FeedMeItem);

    private static bool CanEatNow(out string reason)
    {
        if (!Plugin.ClientState.IsLoggedIn)
        {
            reason = "logged out";
            return false;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
        {
            reason = "player unavailable";
            return false;
        }

        if (!GameHelpers.IsPlayerAlive())
        {
            reason = "dead";
            return false;
        }

        if (player.IsCasting || Plugin.Condition[ConditionFlag.Casting])
        {
            reason = "casting";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.InCombat])
        {
            reason = "in combat";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            reason = "between areas";
            return false;
        }

        if (Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Plugin.Condition[ConditionFlag.Occupied33] ||
            Plugin.Condition[ConditionFlag.Occupied39] ||
            Plugin.Condition[ConditionFlag.WatchingCutscene])
        {
            reason = "busy or in cutscene";
            return false;
        }

        reason = "ready";
        return true;
    }

    private static string QualityLabel(bool highQuality)
        => highQuality ? "HQ" : "NQ";
}
