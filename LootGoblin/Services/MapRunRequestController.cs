using System;
using System.Collections.Generic;
using System.Linq;
using LootGoblin.Models;

namespace LootGoblin.Services;

public sealed class MapRunRequestController
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> requests = new(StringComparer.OrdinalIgnoreCase);
    private Entry? current;
    private long nextTerminalOrder;

    private sealed class Entry(MapRunStartRequest request, MapRunStatusResponse status)
    {
        public MapRunStartRequest Request { get; } = request;
        public MapRunStatusResponse Status { get; set; } = status;
        public bool StartPending { get; set; } = true;
        public bool CancelPending { get; set; }
        public long TerminalOrder { get; set; }
    }

    public MapRunStartRequest? ActiveRequest
    {
        get
        {
            lock (gate)
                return current is { Status.Accepted: true, Status.Terminal: false } ? current.Request : null;
        }
    }

    public MapRunStatusResponse Submit(MapRunStartRequest request)
    {
        lock (gate)
        {
            if (request == null)
                return MapRunStatusResponse.Rejected(new(), "A map-run request is required.");

            request = request with
            {
                RequestId = string.IsNullOrWhiteSpace(request.RequestId)
                    ? Guid.NewGuid().ToString("N")
                    : request.RequestId.Trim(),
            };

            if (request.RequestId.Length > 128)
                return MapRunStatusResponse.Rejected(request, "Request ID must be at most 128 characters.");

            if (requests.TryGetValue(request.RequestId, out var known))
                return known.Request.ItemId == request.ItemId &&
                       known.Request.RunCount == request.RunCount &&
                       known.Request.SuppressFateSync == request.SuppressFateSync
                    ? known.Status
                    : MapRunStatusResponse.Rejected(request, "Request ID already belongs to a different map-run request.");

            if (!TreasureMapData.KnownMaps.TryGetValue(request.ItemId, out var map) ||
                map.Status != ImplementationStatus.Implemented)
                return Remember(new(request, MapRunStatusResponse.Rejected(request, "A supported map item ID is required.")));

            if (request.RunCount <= 0)
                return Remember(new(request, MapRunStatusResponse.Rejected(request, "Run count must be positive.")));

            if (current is { Status.Terminal: false })
                return Remember(new(request, MapRunStatusResponse.Rejected(request, "Another map-run request is pending or running.", MapRunRequestStates.Busy)));

            var response = new MapRunStatusResponse
            {
                RequestId = request.RequestId,
                ItemId = request.ItemId,
                RunCount = request.RunCount,
                SuppressFateSync = request.SuppressFateSync,
                State = MapRunRequestStates.Queued,
                Message = "Queued for admission on LootGoblin's framework update.",
            };
            current = new(request, response);
            return Remember(current);
        }
    }

    public MapRunStatusResponse GetStatus(string requestId)
    {
        lock (gate)
            return GetStatusLocked(requestId);
    }

    public MapRunStatusResponse RequestCancel(string requestId)
    {
        lock (gate)
        {
            requestId = requestId?.Trim() ?? string.Empty;
            if (current is not { Status.Terminal: false } ||
                !string.Equals(current.Request.RequestId, requestId, StringComparison.OrdinalIgnoreCase))
                return GetStatusLocked(requestId);

            current.CancelPending = true;
            current.Status = current.Status with
            {
                State = MapRunRequestStates.CancelRequested,
                Message = "Cancellation queued for LootGoblin's framework update.",
            };
            return current.Status;
        }
    }

    public void RequestCancelCurrent()
    {
        lock (gate)
        {
            if (current is { Status.Terminal: false })
                RequestCancel(current.Request.RequestId);
        }
    }

    // Called only by the existing framework owner loop, never by an IPC callback.
    public void ProcessPending(Func<MapRunStartRequest, MapRunAdmission> start, Action cancel)
    {
        lock (gate)
        {
            var entry = current;
            if (entry == null || entry.Status.Terminal)
                return;

            if (entry.CancelPending)
            {
                var admitted = entry.Status.Accepted;
                Finish(entry, MapRunRequestStates.Cancelled, "Map-run request cancelled.", entry.Status.CompletedRuns);
                if (admitted)
                {
                    try
                    {
                        cancel();
                    }
                    catch (Exception)
                    {
                        Finish(entry, MapRunRequestStates.Failed, "The native map runner failed during cancellation.", entry.Status.CompletedRuns);
                    }
                }
                return;
            }

            if (!entry.StartPending)
                return;

            entry.StartPending = false;
            try
            {
                var admission = start(entry.Request);
                if (!admission.Accepted)
                {
                    entry.Status = entry.Status with { Accepted = false };
                    Finish(entry, admission.State, admission.Message, entry.Status.CompletedRuns);
                }
                else if (!entry.Status.Terminal)
                {
                    entry.Status = entry.Status with
                    {
                        Accepted = true,
                        State = entry.CancelPending ? MapRunRequestStates.CancelRequested : MapRunRequestStates.Running,
                        Message = admission.Message,
                    };
                }
            }
            catch (Exception)
            {
                Finish(entry, MapRunRequestStates.Failed, "The native map runner failed during admission.", entry.Status.CompletedRuns);
            }
        }
    }

    public void UpdateProgress(int completedRuns, string message)
    {
        lock (gate)
        {
            if (current is not { Status.Accepted: true, Status.Terminal: false })
                return;

            current.Status = current.Status with
            {
                CompletedRuns = Math.Clamp(completedRuns, 0, current.Request.RunCount),
                State = current.CancelPending ? MapRunRequestStates.CancelRequested : MapRunRequestStates.Running,
                Message = current.CancelPending ? current.Status.Message : message,
            };
        }
    }

    public void Complete(string state, string message, int completedRuns)
    {
        lock (gate)
        {
            if (current is { Status.Terminal: false } entry)
                Finish(entry, state, message, completedRuns);
        }
    }

    private void Finish(Entry entry, string state, string message, int completedRuns)
    {
        entry.Status = entry.Status with
        {
            CompletedRuns = Math.Clamp(completedRuns, 0, entry.Request.RunCount),
            Terminal = true,
            Success = state == MapRunRequestStates.Completed,
            State = state,
            Message = message,
        };
        if (ReferenceEquals(current, entry))
            current = null;
        Remember(entry);
    }

    private MapRunStatusResponse GetStatusLocked(string requestId)
    {
        requestId = requestId?.Trim() ?? string.Empty;
        return requests.TryGetValue(requestId, out var entry)
            ? entry.Status
            : MapRunStatusResponse.Rejected(new() { RequestId = requestId },
                string.IsNullOrEmpty(requestId) ? "Request ID required." : "No map-run request with this ID is known.",
                MapRunRequestStates.Unknown);
    }

    private MapRunStatusResponse Remember(Entry entry)
    {
        // Removed dictionary slots can be reused; eviction follows terminal arrival order.
        if (entry.Status.Terminal && entry.TerminalOrder == 0)
            entry.TerminalOrder = ++nextTerminalOrder;
        requests[entry.Request.RequestId] = entry;
        while (requests.Count(pair => pair.Value.Status.Terminal) > 20)
            requests.Remove(requests.Where(pair => pair.Value.Status.Terminal)
                .OrderBy(pair => pair.Value.TerminalOrder).First().Key);
        return entry.Status;
    }
}
