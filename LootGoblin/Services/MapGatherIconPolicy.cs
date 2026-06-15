namespace LootGoblin.Services;

internal enum MapGatherIconKind
{
    Seedling,
    Unavailable,
}

internal readonly record struct MapGatherIconInput(
    bool IsKnownMap,
    bool IsGatherable,
    bool HasGatherJob,
    bool GatherEnabled,
    bool AllowanceAvailable,
    bool AllowanceReady,
    string AllowanceCompactText);

internal readonly record struct MapGatherIconState(
    MapGatherIconKind Icon,
    bool GatherEnabled,
    bool IsInteractive,
    bool ShowCooldownOverlay,
    string Tooltip);

internal static class MapGatherIconPolicy
{
    public static MapGatherIconState Evaluate(MapGatherIconInput input)
    {
        if (!input.IsKnownMap)
        {
            return new MapGatherIconState(
                MapGatherIconKind.Unavailable,
                false,
                false,
                false,
                "This map is unknown to LootGoblin and cannot be gathered.");
        }

        if (!input.IsGatherable)
        {
            return new MapGatherIconState(
                MapGatherIconKind.Unavailable,
                false,
                false,
                false,
                "This map is unavailable through gathering.");
        }

        if (!input.HasGatherJob)
        {
            return new MapGatherIconState(
                MapGatherIconKind.Seedling,
                input.GatherEnabled,
                false,
                input.AllowanceAvailable && !input.AllowanceReady,
                "Configure Settings -> Run -> Gather Job to gather missing maps.");
        }

        var selection = input.GatherEnabled ? "Gathering enabled." : "Gathering disabled.";
        if (!input.AllowanceAvailable)
        {
            return new MapGatherIconState(
                MapGatherIconKind.Seedling,
                input.GatherEnabled,
                true,
                false,
                $"{selection} Map allowance status is unavailable.");
        }

        if (input.AllowanceReady)
        {
            return new MapGatherIconState(
                MapGatherIconKind.Seedling,
                input.GatherEnabled,
                true,
                false,
                $"{selection} Click the seedling to change the missing-map gather selection while allowance is ready.");
        }

        return new MapGatherIconState(
            MapGatherIconKind.Seedling,
            input.GatherEnabled,
            true,
            true,
            $"{selection} Allowance cooldown: {input.AllowanceCompactText} remaining. Selection is saved for the next allowance.");
    }
}
