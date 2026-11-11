using System.Text.Json;
using LootGoblin.Models;
using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class MapRunRequestControllerTests
{
    private static MapRunStartRequest Request(string id = "fabricated-run", uint itemId = 7884, int runCount = 3)
        => new() { RequestId = id, ItemId = itemId, RunCount = runCount, SuppressFateSync = true };

    private static MapRunAdmission Admit(MapRunStartRequest _)
        => new(true, MapRunRequestStates.Running, "Native runner admitted the request.");

    [Theory]
    [InlineData(7884u, "", false)]
    [InlineData(43556u, "", false)]
    [InlineData(7884u, ",\"keepAutoDutyEnabled\":false", false)]
    [InlineData(43556u, ",\"keepAutoDutyEnabled\":false", false)]
    [InlineData(7884u, ",\"keepAutoDutyEnabled\":true", true)]
    [InlineData(43556u, ",\"keepAutoDutyEnabled\":true", true)]
    public void AutoDutyWireChoiceReachesOnlyItsQueuedAdmissionAndReportedState(
        uint itemId, string optionJson, bool keepAutoDutyEnabled)
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize<MapRunStartRequest>(
            $"{{\"requestId\":\"fabricated-autoduty\",\"itemId\":{itemId},\"runCount\":3{optionJson}}}", jsonOptions)!;
        var controller = new MapRunRequestController();
        var queued = controller.Submit(request);
        Assert.Equal(keepAutoDutyEnabled, queued.KeepAutoDutyEnabled);
        Assert.Null(controller.ActiveRequest);
        var starts = 0;
        controller.ProcessPending(admitted =>
        {
            starts++;
            Assert.Equal(itemId, admitted.ItemId);
            Assert.Equal(keepAutoDutyEnabled, admitted.KeepAutoDutyEnabled);
            return Admit(admitted);
        }, () => Assert.Fail("Admission must not cancel a runner."));
        Assert.Equal(1, starts);
        Assert.NotNull(controller.ActiveRequest);
        Assert.Equal(keepAutoDutyEnabled, controller.ActiveRequest!.KeepAutoDutyEnabled);
        var running = controller.GetStatus(queued.RequestId);
        Assert.True(running.Accepted);
        Assert.Equal(keepAutoDutyEnabled, running.KeepAutoDutyEnabled);
        using var reported = JsonDocument.Parse(JsonSerializer.Serialize(running, jsonOptions));
        Assert.Equal(keepAutoDutyEnabled, reported.RootElement.GetProperty("keepAutoDutyEnabled").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateAutoDutyChoiceCannotReplaceQueuedOrAdmittedRequest(bool keepAutoDutyEnabled)
    {
        var controller = new MapRunRequestController();
        var request = Request() with { KeepAutoDutyEnabled = keepAutoDutyEnabled };
        var queued = controller.Submit(request);
        Assert.Equal(queued, controller.Submit(request));
        var conflicting = request with { KeepAutoDutyEnabled = !keepAutoDutyEnabled };
        var rejected = controller.Submit(conflicting);
        Assert.Equal(MapRunRequestStates.Rejected, rejected.State);
        Assert.Equal(!keepAutoDutyEnabled, rejected.KeepAutoDutyEnabled);
        Assert.Equal(queued, controller.GetStatus(request.RequestId));
        controller.ProcessPending(Admit, () => Assert.Fail("Unexpected cancellation."));
        var running = controller.GetStatus(request.RequestId);
        Assert.Equal(MapRunRequestStates.Rejected, controller.Submit(conflicting).State);
        Assert.Equal(running, controller.Submit(request));
        Assert.Equal(keepAutoDutyEnabled, controller.ActiveRequest!.KeepAutoDutyEnabled);
    }

    [Theory]
    [InlineData("queued-cancel", MapRunRequestStates.Cancelled)]
    [InlineData("cancel", MapRunRequestStates.Cancelled)]
    [InlineData("complete", MapRunRequestStates.Completed)]
    [InlineData("failed", MapRunRequestStates.Failed)]
    [InlineData("rejected", MapRunRequestStates.Rejected)]
    [InlineData("throw", MapRunRequestStates.Failed)]
    public void TerminalAutoDutyOptInCannotLeakToTheNextDefaultRequest(string ending, string expectedState)
    {
        var controller = new MapRunRequestController();
        var original = controller.Submit(Request() with { KeepAutoDutyEnabled = true });
        if (ending == "queued-cancel")
        {
            controller.RequestCancel(original.RequestId);
            controller.ProcessPending(_ => throw new Exception("Cancelled request must not start."),
                () => Assert.Fail("Queued cancellation must not stop a runner."));
        }
        else if (ending == "rejected")
            controller.ProcessPending(_ => new(false, MapRunRequestStates.Rejected, "Native admission declined."),
                () => Assert.Fail("Rejected admission must not stop a runner."));
        else if (ending == "throw")
            controller.ProcessPending(_ => throw new InvalidOperationException("Fabricated admission failure."),
                () => Assert.Fail("Failed admission must not stop a runner."));
        else
        {
            controller.ProcessPending(Admit, () => Assert.Fail("Unexpected cancellation."));
            if (ending == "cancel")
            {
                controller.RequestCancel(original.RequestId);
                controller.ProcessPending(Admit, () => { });
            }
            else
                controller.Complete(ending == "complete" ? MapRunRequestStates.Completed : MapRunRequestStates.Failed,
                    "Native runner ended.", 1);
        }
        var terminal = controller.GetStatus(original.RequestId);
        Assert.Equal(expectedState, terminal.State);
        Assert.True(terminal.Terminal);
        Assert.True(terminal.KeepAutoDutyEnabled);
        Assert.Null(controller.ActiveRequest);
        var next = controller.Submit(Request("fabricated-next-autoduty", itemId: 43556));
        Assert.False(next.KeepAutoDutyEnabled);
        controller.ProcessPending(request =>
        {
            Assert.False(request.KeepAutoDutyEnabled);
            return Admit(request);
        }, () => Assert.Fail("New admission must not cancel a runner."));
        Assert.False(controller.ActiveRequest!.KeepAutoDutyEnabled);
        Assert.False(controller.GetStatus(next.RequestId).KeepAutoDutyEnabled);
        Assert.True(controller.GetStatus(original.RequestId).KeepAutoDutyEnabled);
    }

    [Fact]
    public void MysteriousMapIsQueuedBeforeNativeAdmissionAndSnapshotsStayImmutable()
    {
        var controller = new MapRunRequestController();
        var starts = 0;
        var queued = controller.Submit(Request());

        Assert.Equal(MapRunRequestStates.Queued, queued.State);
        Assert.False(queued.Accepted);
        Assert.False(queued.Terminal);
        Assert.Null(controller.ActiveRequest);

        controller.ProcessPending(request =>
        {
            starts++;
            Assert.Equal(7884u, request.ItemId);
            Assert.Equal(3, request.RunCount);
            return Admit(request);
        }, () => Assert.Fail("Admission must not cancel the runner."));
        controller.ProcessPending(_ => throw new Exception("Started twice."), () => Assert.Fail("Unexpected cancellation."));

        var running = controller.GetStatus(queued.RequestId);
        Assert.Equal(1, starts);
        Assert.True(running.Accepted);
        Assert.Equal(MapRunRequestStates.Running, running.State);
        Assert.True(controller.ActiveRequest?.SuppressFateSync);
        Assert.False(queued.Accepted);
        Assert.Equal(MapRunRequestStates.Queued, queued.State);
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(uint.MaxValue, 1)]
    [InlineData(7884u, 0)]
    [InlineData(7884u, -1)]
    public void InvalidMapOrCountNeverReachesNativeRunner(uint itemId, int count)
    {
        var controller = new MapRunRequestController();
        var starts = 0;
        var stops = 0;
        var rejected = controller.Submit(Request(itemId: itemId, runCount: count));
        controller.ProcessPending(request => { starts++; return Admit(request); }, () => stops++);

        Assert.Equal(0, starts);
        Assert.Equal(0, stops);
        Assert.Equal(MapRunRequestStates.Rejected, rejected.State);
        Assert.True(rejected.Terminal);
        Assert.False(rejected.Accepted);
    }

    [Fact]
    public void RequestIdsAreBoundedNormalizedAndGeneratedWhenMissing()
    {
        var controller = new MapRunRequestController();
        Assert.Equal(MapRunRequestStates.Rejected, controller.Submit(null!).State);
        Assert.Equal(MapRunRequestStates.Rejected, controller.Submit(Request(new string('x', 129))).State);

        var generated = controller.Submit(Request(" ", runCount: int.MaxValue));
        Assert.False(string.IsNullOrWhiteSpace(generated.RequestId));
        Assert.Equal(int.MaxValue, generated.RunCount);
        controller.RequestCancel(generated.RequestId);
        controller.ProcessPending(Admit, () => Assert.Fail("Queued request must not stop a runner."));

        var trimmed = controller.Submit(Request(" fabricated-second "));
        Assert.Equal("fabricated-second", trimmed.RequestId);
        Assert.Equal(trimmed, controller.GetStatus(" FABRICATED-SECOND "));
    }

    [Fact]
    public void DuplicateReturnsOriginalAndConflictingIdCannotReplaceIt()
    {
        var controller = new MapRunRequestController();
        var original = controller.Submit(Request());

        Assert.Equal(original, controller.Submit(Request("FABRICATED-RUN")));
        Assert.Equal(MapRunRequestStates.Rejected, controller.Submit(Request(runCount: 4)).State);
        Assert.Equal(MapRunRequestStates.Rejected, controller.Submit(Request() with { SuppressFateSync = false }).State);
        Assert.Equal(original, controller.GetStatus(original.RequestId));

        controller.ProcessPending(Admit, () => Assert.Fail("Unexpected cancellation."));
        Assert.True(controller.GetStatus(original.RequestId).Accepted);
    }

    [Fact]
    public void AQueuedOrRunningRequestRejectsAnotherRequestAsBusy()
    {
        var controller = new MapRunRequestController();
        var original = controller.Submit(Request());
        Assert.Equal(MapRunRequestStates.Busy, controller.Submit(Request("fabricated-second")).State);
        controller.ProcessPending(Admit, () => Assert.Fail("Unexpected cancellation."));
        Assert.Equal(MapRunRequestStates.Busy, controller.Submit(Request("fabricated-third")).State);
        Assert.Equal(original.RequestId, controller.ActiveRequest?.RequestId);
    }

    [Theory]
    [InlineData(MapRunRequestStates.Busy)]
    [InlineData(MapRunRequestStates.Rejected)]
    [InlineData(MapRunRequestStates.Failed)]
    public void NativeAdmissionFailureIsTerminalAndDoesNotClaimAcceptance(string state)
    {
        var controller = new MapRunRequestController();
        var request = controller.Submit(Request());
        controller.ProcessPending(_ => new(false, state, "Native admission declined."), () => Assert.Fail("Rejected admission must not cancel a foreign runner."));

        var status = controller.GetStatus(request.RequestId);
        Assert.Equal(state, status.State);
        Assert.True(status.Terminal);
        Assert.False(status.Accepted);
        Assert.False(status.Success);
        Assert.Null(controller.ActiveRequest);
    }

    [Fact]
    public void CancelingQueuedRequestNeverStartsOrStopsNativeRunner()
    {
        var controller = new MapRunRequestController();
        var starts = 0;
        var stops = 0;
        var queued = controller.Submit(Request());
        var pending = controller.RequestCancel(queued.RequestId);
        Assert.Equal(MapRunRequestStates.CancelRequested, pending.State);
        Assert.False(pending.Terminal);

        controller.ProcessPending(request => { starts++; return Admit(request); }, () => stops++);
        var status = controller.GetStatus(queued.RequestId);
        Assert.Equal(0, starts);
        Assert.Equal(0, stops);
        Assert.Equal(MapRunRequestStates.Cancelled, status.State);
        Assert.False(status.Accepted);
        Assert.True(status.Terminal);
    }

    [Fact]
    public void CancelTargetsOnlyMatchingAdmittedRequestAndDispatchesOnce()
    {
        var controller = new MapRunRequestController();
        var request = controller.Submit(Request());
        var stops = 0;
        controller.ProcessPending(Admit, () => stops++);

        Assert.Equal(MapRunRequestStates.Unknown, controller.RequestCancel("fabricated-foreign").State);
        controller.ProcessPending(Admit, () => stops++);
        Assert.Equal(0, stops);
        controller.RequestCancel(request.RequestId);
        controller.UpdateProgress(1, "Native runner made progress.");
        Assert.Equal(MapRunRequestStates.CancelRequested, controller.GetStatus(request.RequestId).State);
        controller.ProcessPending(Admit, () =>
        {
            stops++;
            Assert.Null(controller.ActiveRequest);
            controller.Complete(MapRunRequestStates.Completed, "Reentrant cleanup cannot overwrite cancellation.", 3);
        });
        controller.RequestCancel(request.RequestId);
        controller.ProcessPending(Admit, () => stops++);

        var status = controller.GetStatus(request.RequestId);
        Assert.Equal(1, stops);
        Assert.Equal(MapRunRequestStates.Cancelled, status.State);
        Assert.True(status.Accepted);
        Assert.False(status.Success);
        Assert.Equal(1, status.CompletedRuns);
    }

    [Fact]
    public void NativeCancellationExceptionReportsFailure()
    {
        var controller = new MapRunRequestController();
        var request = controller.Submit(Request());
        controller.ProcessPending(Admit, () => Assert.Fail("Unexpected cancellation."));
        controller.RequestCancel(request.RequestId);
        controller.ProcessPending(Admit, () =>
        {
            controller.Complete(MapRunRequestStates.Cancelled, "Native stop began.", 0);
            throw new InvalidOperationException("Fabricated native stop failure.");
        });

        var status = controller.GetStatus(request.RequestId);
        Assert.Equal(MapRunRequestStates.Failed, status.State);
        Assert.True(status.Terminal);
        Assert.False(status.Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancelCurrentQueuesWithoutNativeWorkAndStopsOnlyAnAdmittedRequest(bool admitted)
    {
        var controller = new MapRunRequestController();
        var request = controller.Submit(Request());
        var starts = 0;
        var stops = 0;
        MapRunAdmission Start(MapRunStartRequest pending)
        {
            starts++;
            return Admit(pending);
        }

        if (admitted)
            controller.ProcessPending(Start, () => stops++);

        controller.RequestCancelCurrent();
        Assert.Equal(admitted ? 1 : 0, starts);
        Assert.Equal(0, stops);
        Assert.Equal(MapRunRequestStates.CancelRequested, controller.GetStatus(request.RequestId).State);
        controller.ProcessPending(Start, () => stops++);
        controller.RequestCancelCurrent();
        controller.ProcessPending(Start, () => stops++);

        Assert.Equal(admitted ? 1 : 0, starts);
        Assert.Equal(admitted ? 1 : 0, stops);
        Assert.Equal(MapRunRequestStates.Cancelled, controller.GetStatus(request.RequestId).State);
        Assert.Null(controller.ActiveRequest);
    }

    [Fact]
    public void NativeStartExceptionDoesNotStopAnUnownedRunner()
    {
        var controller = new MapRunRequestController();
        var request = controller.Submit(Request());
        controller.ProcessPending(_ => throw new InvalidOperationException("Fabricated admission failure."), () => Assert.Fail("Controller must not globally cancel failed admission."));

        var status = controller.GetStatus(request.RequestId);
        Assert.Equal(MapRunRequestStates.Failed, status.State);
        Assert.True(status.Terminal);
        Assert.False(status.Accepted);
    }

    [Fact]
    public void FailedAdmissionWinsOverReentrantStartupCleanup()
    {
        var controller = new MapRunRequestController();
        var request = controller.Submit(Request());
        controller.ProcessPending(_ =>
        {
            controller.Complete(MapRunRequestStates.Cancelled, "Native startup cleanup ran.", 0);
            return new(false, MapRunRequestStates.Failed, "Native startup failed.");
        }, () => Assert.Fail("Unexpected cancellation."));

        var status = controller.GetStatus(request.RequestId);
        Assert.Equal(MapRunRequestStates.Failed, status.State);
        Assert.Equal("Native startup failed.", status.Message);
        Assert.False(status.Accepted);
    }

    [Fact]
    public void ProgressAndCompletionRetainHonestRunCountAndReleaseSuppression()
    {
        var controller = new MapRunRequestController();
        var request = controller.Submit(Request());
        controller.ProcessPending(Admit, () => Assert.Fail("Unexpected cancellation."));
        controller.UpdateProgress(2, "Running final map.");
        Assert.Equal(2, controller.GetStatus(request.RequestId).CompletedRuns);
        Assert.False(controller.GetStatus(request.RequestId).Success);

        controller.Complete(MapRunRequestStates.Completed, "All requested maps completed.", 3);
        controller.UpdateProgress(0, "Stale update.");
        controller.Complete(MapRunRequestStates.Failed, "Stale cleanup.", 0);
        var status = controller.GetStatus(request.RequestId);
        Assert.Equal(MapRunRequestStates.Completed, status.State);
        Assert.Equal(3, status.CompletedRuns);
        Assert.True(status.Accepted);
        Assert.True(status.Terminal);
        Assert.True(status.Success);
        Assert.Null(controller.ActiveRequest);
        Assert.Equal(MapRunRequestStates.Queued, controller.Submit(Request("fabricated-next", itemId: 43556)).State);
    }

    [Fact]
    public void TerminalHistoryIsBoundedWithoutEvictingCurrentRequest()
    {
        var controller = new MapRunRequestController();
        var request = controller.Submit(Request());
        for (var index = 0; index < 30; index++)
            controller.Submit(Request($"fabricated-busy-{index}"));

        Assert.Equal(MapRunRequestStates.Queued, controller.GetStatus(request.RequestId).State);
        Assert.Equal(MapRunRequestStates.Unknown, controller.GetStatus("fabricated-busy-0").State);
        Assert.Equal(MapRunRequestStates.Busy, controller.GetStatus("fabricated-busy-29").State);
        controller.ProcessPending(Admit, () => Assert.Fail("Unexpected cancellation."));
        Assert.Equal(request.RequestId, controller.ActiveRequest?.RequestId);
        controller.Complete(MapRunRequestStates.Completed, "Current request completed most recently.", 3);
        Assert.Equal(MapRunRequestStates.Completed, controller.GetStatus(request.RequestId).State);
    }
}
