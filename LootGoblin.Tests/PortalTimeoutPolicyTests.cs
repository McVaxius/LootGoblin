using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class PortalTimeoutPolicyTests
{
    [Fact]
    public void CapturedPortalWithActiveMapDutyKeepsWaiting()
    {
        var action = Evaluate(mapDutyActive: true, hasLivePortal: false, hasCapturedPortalPosition: true);

        Assert.Equal(PortalTimeoutAction.WaitForDutyToClear, action);
    }

    [Fact]
    public void CapturedPortalAfterMapDutyClearsCompletesMap()
    {
        var action = Evaluate(mapDutyActive: false, hasLivePortal: false, hasCapturedPortalPosition: true);

        Assert.Equal(PortalTimeoutAction.CompleteMap, action);
    }

    [Fact]
    public void LivePortalContinuesPortalInteraction()
    {
        var action = Evaluate(mapDutyActive: false, hasLivePortal: true, hasCapturedPortalPosition: true);

        Assert.Equal(PortalTimeoutAction.ContinuePortalInteraction, action);
    }

    private static PortalTimeoutAction Evaluate(
        bool mapDutyActive,
        bool hasLivePortal,
        bool hasCapturedPortalPosition)
        => PortalTimeoutPolicy.Evaluate(
            new PortalTimeoutState(mapDutyActive, hasLivePortal, hasCapturedPortalPosition));
}
