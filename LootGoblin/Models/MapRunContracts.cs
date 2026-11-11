namespace LootGoblin.Models;

public sealed record MapRunStartRequest
{
    public string RequestId { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public int RunCount { get; init; }
    public bool SuppressFateSync { get; init; }
    public bool KeepAutoDutyEnabled { get; init; }
}

public sealed record MapRunStatusResponse
{
    public string RequestId { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public int RunCount { get; init; }
    public bool SuppressFateSync { get; init; }
    public bool KeepAutoDutyEnabled { get; init; }
    public int CompletedRuns { get; init; }
    public bool Accepted { get; init; }
    public bool Terminal { get; init; }
    public bool Success { get; init; }
    public string State { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    public static MapRunStatusResponse Rejected(MapRunStartRequest request, string message, string state = MapRunRequestStates.Rejected)
        => new()
        {
            RequestId = request.RequestId,
            ItemId = request.ItemId,
            RunCount = request.RunCount,
            SuppressFateSync = request.SuppressFateSync,
            KeepAutoDutyEnabled = request.KeepAutoDutyEnabled,
            Terminal = true,
            State = state,
            Message = message,
        };
}

public static class MapRunRequestStates
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string CancelRequested = "CancelRequested";
    public const string Cancelled = "Cancelled";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Rejected = "Rejected";
    public const string Busy = "Busy";
    public const string Unknown = "Unknown";
}

public readonly record struct MapRunAdmission(bool Accepted, string State, string Message);
