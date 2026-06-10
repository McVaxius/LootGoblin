namespace LootGoblin.Services;

public enum PortalTimeoutAction
{
    ContinuePortalInteraction,
    WaitForDutyToClear,
    CompleteMap,
}

public readonly record struct PortalTimeoutState(
    bool MapDutyActive,
    bool HasLivePortal,
    bool HasCapturedPortalPosition);

public static class PortalTimeoutPolicy
{
    public static PortalTimeoutAction Evaluate(PortalTimeoutState state)
    {
        // Captured XYZ is navigation recovery data, not proof that the portal still exists.
        if (state.HasLivePortal)
            return PortalTimeoutAction.ContinuePortalInteraction;

        if (state.MapDutyActive)
            return PortalTimeoutAction.WaitForDutyToClear;

        return PortalTimeoutAction.CompleteMap;
    }
}
