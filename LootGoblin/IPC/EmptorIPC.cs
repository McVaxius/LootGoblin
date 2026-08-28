using System;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace LootGoblin.IPC;

internal sealed record EmptorOrderStatus(
    string OrderId,
    string State,
    string Message,
    int PurchasedQuantity,
    string StoppedReason,
    bool ListingsExhausted)
{
    public bool IsTerminal => State is "completed" or "cancelled" or "rejected" or "failed";
}

public sealed class EmptorIPC : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private DateTime nextStatusRefreshUtc = DateTime.MinValue;

    public int ApiVersion { get; private set; }
    public bool IsAvailable { get; private set; }
    public string StatusText { get; private set; } = "Emptor IPC has not been checked.";

    public EmptorIPC(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        RefreshStatus(force: true);
    }

    public void Dispose()
    {
    }

    public void RefreshStatus(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now < nextStatusRefreshUtc)
            return;

        nextStatusRefreshUtc = now + TimeSpan.FromSeconds(5);
        try
        {
            ApiVersion = pluginInterface.GetIpcSubscriber<int>("Emptor.ApiVersion").InvokeFunc();
            IsAvailable = ApiVersion >= 1;
            StatusText = IsAvailable
                ? $"Emptor API v{ApiVersion} is available."
                : $"Emptor API v{ApiVersion} is incompatible; v1 or newer is required.";
        }
        catch (Exception ex)
        {
            ApiVersion = 0;
            IsAvailable = false;
            StatusText = "Emptor is not installed, loaded, or exposing supported v1 IPC.";
            log.Debug($"[Emptor] ApiVersion unavailable: {ex.Message}");
        }
    }

    public bool TrySubmitOrder(uint itemId, int maximumGil, out string orderId, out string error)
    {
        orderId = string.Empty;
        error = string.Empty;
        RefreshStatus(force: true);
        if (!IsAvailable)
        {
            error = StatusText;
            return false;
        }

        if (itemId == 0 || maximumGil <= 0)
        {
            error = "The map item ID and maximum gil must both be positive.";
            return false;
        }

        var request = JsonSerializer.Serialize(new
        {
            totalGilBudget = maximumGil,
            items = new[]
            {
                new
                {
                    itemId,
                    maxUnitPrice = maximumGil,
                    quantity = 1,
                    quality = "either",
                    overshoot = "skip",
                },
            },
        });

        try
        {
            var response = pluginInterface
                .GetIpcSubscriber<string, string>("Emptor.SubmitOrder")
                .InvokeFunc(request);
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            orderId = GetString(root, "orderId");
            if (!string.IsNullOrWhiteSpace(orderId))
                return true;

            var state = GetString(root, "state");
            var message = GetString(root, "message");
            error = string.IsNullOrWhiteSpace(message)
                ? $"Emptor rejected the order ({(string.IsNullOrWhiteSpace(state) ? "unknown state" : state)})."
                : message;
            return false;
        }
        catch (Exception ex)
        {
            error = $"Emptor order submission failed: {ex.Message}";
            return false;
        }
    }

    internal bool TryGetOrder(string ownedOrderId, uint itemId, out EmptorOrderStatus status, out string error)
    {
        status = new EmptorOrderStatus(ownedOrderId, string.Empty, string.Empty, 0, string.Empty, false);
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(ownedOrderId))
        {
            error = "No LootGoblin-owned Emptor order ID is active.";
            return false;
        }

        try
        {
            var response = pluginInterface
                .GetIpcSubscriber<string, string>("Emptor.GetOrder")
                .InvokeFunc(ownedOrderId);
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var returnedOrderId = GetString(root, "orderId");
            if (!string.IsNullOrWhiteSpace(returnedOrderId) &&
                !string.Equals(returnedOrderId, ownedOrderId, StringComparison.Ordinal))
            {
                error = $"Emptor returned a different order ID ({returnedOrderId}).";
                return false;
            }

            var purchasedQuantity = 0;
            var stoppedReason = string.Empty;
            var listingsExhausted = false;
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var returnedItemId = item.TryGetProperty("itemId", out var itemIdElement) && itemIdElement.TryGetUInt32(out var parsedItemId)
                        ? parsedItemId
                        : 0;
                    if (returnedItemId != 0 && returnedItemId != itemId)
                        continue;

                    if (item.TryGetProperty("purchasedQuantity", out var purchased) && purchased.TryGetInt32(out var parsedQuantity))
                        purchasedQuantity = parsedQuantity;
                    stoppedReason = GetString(item, "stoppedReason");
                    listingsExhausted = item.TryGetProperty("listingsExhausted", out var exhausted) &&
                                        exhausted.ValueKind is JsonValueKind.True;
                    break;
                }
            }

            status = new EmptorOrderStatus(
                ownedOrderId,
                GetString(root, "state").Trim().ToLowerInvariant(),
                GetString(root, "message"),
                purchasedQuantity,
                stoppedReason,
                listingsExhausted);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Emptor order polling failed: {ex.Message}";
            return false;
        }
    }

    public bool TryCancelOrder(string ownedOrderId, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(ownedOrderId))
            return true;

        try
        {
            var cancelled = pluginInterface
                .GetIpcSubscriber<string, bool>("Emptor.CancelOrder")
                .InvokeFunc(ownedOrderId);
            if (!cancelled)
                error = $"Emptor did not accept cancellation for LootGoblin order {ownedOrderId}.";
            return cancelled;
        }
        catch (Exception ex)
        {
            error = $"Could not cancel LootGoblin Emptor order {ownedOrderId}: {ex.Message}";
            return false;
        }
    }

    public bool TryIsBusy(out bool isBusy)
    {
        try
        {
            isBusy = pluginInterface.GetIpcSubscriber<bool>("Emptor.IsBusy").InvokeFunc();
            return true;
        }
        catch
        {
            isBusy = false;
            return false;
        }
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
