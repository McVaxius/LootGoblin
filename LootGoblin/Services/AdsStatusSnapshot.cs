using System;
using System.Text.Json;

namespace LootGoblin.Services;

public sealed class AdsStatusSnapshot
{
    public static AdsStatusSnapshot Empty { get; } = new();

    public bool IsAvailable { get; init; }
    public bool StatusReadable { get; init; }
    public string OwnershipMode { get; init; } = string.Empty;
    public string ExecutionPhase { get; init; } = string.Empty;
    public string ExecutionStatus { get; init; } = string.Empty;
    public bool UtilityRunning { get; init; }
    public string UtilityTask { get; init; } = string.Empty;
    public string UtilityMode { get; init; } = string.Empty;
    public string UtilityStatus { get; init; } = string.Empty;
    public string UtilityLastSuccess { get; init; } = string.Empty;
    public string UtilityLastFailure { get; init; } = string.Empty;
    public DateTime? UtilityCompletedAtUtc { get; init; }
    public bool InDuty { get; init; }
    public bool SupportedDuty { get; init; }
    public bool? UnsafeTransition { get; init; }
    public uint TerritoryTypeId { get; init; }
    public uint ContentFinderConditionId { get; init; }
    public AdsCompletionTreasureSweepSnapshot? CompletionTreasureSweep { get; init; }
    public DateTime CapturedAtUtc { get; init; }

    public bool IsOwned
        => OwnershipMode is "OwnedStartOutside" or "OwnedStartInside" or "OwnedResumeInside" or "Leaving";

    public bool HasCompletedTreasureSweepFor(uint territoryId, uint contentId)
        => IsAvailable && StatusReadable && InDuty && UnsafeTransition == false
           && OwnershipMode == "Observing" && territoryId != 0 && contentId != 0
           && TerritoryTypeId == territoryId && ContentFinderConditionId == contentId
           && CompletionTreasureSweep is { WithoutExit: true, Completed: true } sweep
           && sweep.TerritoryTypeId == territoryId && sweep.ContentFinderConditionId == contentId;

    internal static AdsStatusSnapshot Parse(string json, DateTime capturedAtUtc)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("ADS status must be a JSON object.");

        return new AdsStatusSnapshot
        {
            IsAvailable = true,
            StatusReadable = true,
            OwnershipMode = GetString(root, "ownershipMode"),
            ExecutionPhase = GetString(root, "executionPhase"),
            ExecutionStatus = GetString(root, "executionStatus"),
            UtilityRunning = GetBool(root, "utilityRunning") == true,
            UtilityTask = GetString(root, "utilityTask"),
            UtilityMode = GetString(root, "utilityMode"),
            UtilityStatus = GetString(root, "utilityStatus"),
            UtilityLastSuccess = GetString(root, "utilityLastSuccess"),
            UtilityLastFailure = GetString(root, "utilityLastFailure"),
            UtilityCompletedAtUtc = GetDateTime(root, "utilityCompletedAtUtc"),
            InDuty = GetBool(root, "inDuty") == true || GetBool(root, "inInstancedDuty") == true,
            SupportedDuty = GetBool(root, "supportedDuty") == true,
            UnsafeTransition = GetBool(root, "unsafeTransition"),
            TerritoryTypeId = GetUInt(root, "territoryTypeId"),
            ContentFinderConditionId = GetUInt(root, "contentFinderConditionId"),
            CompletionTreasureSweep = GetCompletionTreasureSweep(root),
            CapturedAtUtc = capturedAtUtc,
        };
    }

    private static AdsCompletionTreasureSweepSnapshot? GetCompletionTreasureSweep(JsonElement root)
    {
        if (!root.TryGetProperty("completionTreasureSweep", out var result) || result.ValueKind != JsonValueKind.Object)
            return null;
        var territory = GetUInt(result, "territoryTypeId");
        var content = GetUInt(result, "contentFinderConditionId");
        var withoutExit = GetBool(result, "withoutExit");
        var completed = GetBool(result, "completed");
        return territory != 0 && content != 0 && withoutExit.HasValue && completed.HasValue
            ? new AdsCompletionTreasureSweepSnapshot(territory, content, withoutExit.Value, completed.Value)
            : null;
    }

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static bool? GetBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;

    private static uint GetUInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number)
            ? number : 0;

    private static DateTime? GetDateTime(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
           && DateTime.TryParse(value.GetString(), out var date) ? date : null;
}

public sealed record AdsCompletionTreasureSweepSnapshot(
    uint TerritoryTypeId, uint ContentFinderConditionId, bool WithoutExit, bool Completed);
