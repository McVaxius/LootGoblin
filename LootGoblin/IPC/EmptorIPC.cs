using System;
using System.Collections.Generic;
using System.Linq;
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

public enum EmptorPriceLookupScope
{
    World,
    DataCenter,
    Region,
    Reachable,
}

public enum EmptorPriceRefreshStatus
{
    NotRequested,
    DeferredUntilLogin,
    Unavailable,
    Queued,
    Refreshing,
    PendingFollowUp,
    Complete,
    Failed,
}

public sealed record EmptorCityOption(string Key, string Label, string Route);

public sealed record EmptorPriceSnapshot(
    uint ItemId,
    long? NqMinimumListing,
    string World,
    string Location,
    string Age,
    EmptorPriceLookupScope Scope,
    DateTimeOffset? FetchedAtUtc,
    string Error)
{
    public bool HasPositiveHint => NqMinimumListing is > 0 and <= 999_999_999;
}

public sealed class EmptorIPC : IDisposable
{
    private static readonly TimeSpan PricePendingFollowUpDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ManualPriceRefreshCooldown = TimeSpan.FromMinutes(5);
    private static readonly IReadOnlyList<EmptorCityOption> FallbackCities = new[]
    {
        new EmptorCityOption(string.Empty, "Ul'dah (Emptor default)", "LiMarketBoard"),
        new EmptorCityOption("limsa", "Limsa Lominsa", "Teleport"),
        new EmptorCityOption("gridania", "Gridania", "AethernetHop"),
        new EmptorCityOption("ishgard", "Foundation", "AethernetHop"),
        new EmptorCityOption("kugane", "Kugane", "AethernetHop"),
        new EmptorCityOption("crystarium", "The Crystarium", "AethernetHop"),
        new EmptorCityOption("sharlayan", "Old Sharlayan", "Teleport"),
        new EmptorCityOption("tuliyollal", "Tuliyollal", "AethernetHop"),
    };

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private DateTime nextStatusRefreshUtc = DateTime.MinValue;
    private IReadOnlyList<EmptorCityOption> cityOptions = FallbackCities;
    private bool cityLoadAttempted;
    private bool initialPriceRequestIssued;
    private bool manualPriceRefreshRequested;
    private DateTime pendingPriceFollowUpAtUtc = DateTime.MinValue;
    private IReadOnlyList<uint> pendingPriceItemIds = Array.Empty<uint>();
    private EmptorPriceLookupScope pendingPriceScope;
    private Dictionary<uint, EmptorPriceSnapshot> priceSnapshots = new();

    public int ApiVersion { get; private set; }
    public bool IsAvailable { get; private set; }
    public string StatusText { get; private set; } = "Emptor IPC has not been checked.";
    public IReadOnlyList<EmptorCityOption> CityOptions => cityOptions;
    public bool UsesDynamicCityOptions { get; private set; }
    public string CityOptionsStatusText { get; private set; } = "Using built-in city choices until Emptor v4 city IPC is available.";
    public string PriceStatusText { get; private set; } = "Price hints have not been requested.";
    public EmptorPriceRefreshStatus PriceRefreshStatus { get; private set; } = EmptorPriceRefreshStatus.NotRequested;
    public bool IsPriceRefreshPending => pendingPriceItemIds.Count > 0;
    public bool IsManualPriceRefreshRequested => manualPriceRefreshRequested;
    public DateTime NextManualPriceRefreshUtc { get; private set; } = DateTime.MinValue;
    public EmptorPriceLookupScope? LastPriceLookupScope { get; private set; }

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

            if (ApiVersion >= 4 && !cityLoadAttempted)
                LoadCityOptions();
            else if (ApiVersion < 4)
                UseFallbackCityOptions("Using built-in city choices because Emptor v4 city IPC is unavailable.", allowFutureAttempt: true);
        }
        catch (Exception ex)
        {
            ApiVersion = 0;
            IsAvailable = false;
            StatusText = "Emptor is not installed, loaded, or exposing supported v1 IPC.";
            UseFallbackCityOptions("Using built-in city choices because Emptor city IPC is unavailable.", allowFutureAttempt: true);
            log.Debug($"[Emptor] ApiVersion unavailable: {ex.Message}");
        }
    }

    public bool RequestManualPriceRefresh(bool isLoggedIn, out string error)
    {
        error = string.Empty;
        if (!isLoggedIn)
        {
            error = "Log in before refreshing Emptor price hints.";
            return false;
        }

        if (!IsAvailable || ApiVersion < 5)
        {
            error = ApiVersion > 0
                ? $"Emptor API v5 or newer is required for price hints; detected v{ApiVersion}."
                : "Emptor API v5 price IPC is unavailable.";
            return false;
        }

        if (manualPriceRefreshRequested || IsPriceRefreshPending)
        {
            error = "An Emptor price refresh is already pending.";
            return false;
        }

        var remaining = GetManualPriceRefreshCooldown(DateTime.UtcNow);
        if (remaining > TimeSpan.Zero)
        {
            error = $"Manual price refresh is available in {FormatCountdown(remaining)}.";
            return false;
        }

        manualPriceRefreshRequested = true;
        PriceRefreshStatus = EmptorPriceRefreshStatus.Queued;
        PriceStatusText = "Manual Emptor price refresh queued.";
        return true;
    }

    public TimeSpan GetManualPriceRefreshCooldown(DateTime utcNow)
        => NextManualPriceRefreshUtc > utcNow
            ? NextManualPriceRefreshUtc - utcNow
            : TimeSpan.Zero;

    public bool TryGetPriceSnapshot(uint itemId, out EmptorPriceSnapshot snapshot)
        => priceSnapshots.TryGetValue(itemId, out snapshot!);

    public void UpdatePrices(
        bool isLoggedIn,
        IReadOnlyCollection<uint> marketableKnownMapItemIds,
        EmptorPriceLookupScope scope)
    {
        var now = DateTime.UtcNow;
        if (pendingPriceItemIds.Count > 0 && now >= pendingPriceFollowUpAtUtc)
        {
            CompletePendingPriceLookup();
            return;
        }

        if (pendingPriceItemIds.Count > 0)
            return;

        if (manualPriceRefreshRequested)
        {
            if (!isLoggedIn)
            {
                PriceRefreshStatus = EmptorPriceRefreshStatus.DeferredUntilLogin;
                PriceStatusText = "Manual price refresh is deferred until login.";
                return;
            }

            if (!IsAvailable || ApiVersion < 5)
            {
                manualPriceRefreshRequested = false;
                PriceRefreshStatus = EmptorPriceRefreshStatus.Unavailable;
                PriceStatusText = ApiVersion > 0
                    ? $"Price hints unavailable: Emptor v5+ is required; detected v{ApiVersion}."
                    : "Price hints unavailable: Emptor v5 price IPC is not loaded.";
                return;
            }

            StartPriceLookup(marketableKnownMapItemIds, scope, refresh: true, isInitial: false);
            return;
        }

        if (initialPriceRequestIssued)
            return;

        if (!isLoggedIn)
        {
            PriceRefreshStatus = EmptorPriceRefreshStatus.DeferredUntilLogin;
            PriceStatusText = "Initial Emptor price lookup is deferred until login.";
            return;
        }

        if (!IsAvailable || ApiVersion < 5)
        {
            PriceRefreshStatus = EmptorPriceRefreshStatus.Unavailable;
            PriceStatusText = ApiVersion > 0
                ? $"Price hints unavailable: Emptor v5+ is required; detected v{ApiVersion}."
                : "Price hints unavailable: Emptor v5 price IPC is not loaded.";
            return;
        }

        StartPriceLookup(marketableKnownMapItemIds, scope, refresh: false, isInitial: true);
    }

    public bool TrySubmitOrder(uint itemId, int maximumGil, out string orderId, out string error)
        => TrySubmitOrder(itemId, maximumGil, string.Empty, out orderId, out error);

    public bool TrySubmitOrder(
        uint itemId,
        int maximumGil,
        string cityKey,
        out string orderId,
        out string error)
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

        cityKey = cityKey?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(cityKey) && ApiVersion < 4)
        {
            error = $"Emptor API v4 or newer is required for the configured marketboard city; detected v{ApiVersion}. Select Ul'dah (Emptor default) to keep using Emptor v1-v3.";
            return false;
        }

        var items = new[]
        {
            new
            {
                itemId,
                maxUnitPrice = maximumGil,
                quantity = 1,
                quality = "either",
                overshoot = "skip",
            },
        };
        var request = string.IsNullOrEmpty(cityKey)
            ? JsonSerializer.Serialize(new { totalGilBudget = maximumGil, items })
            : JsonSerializer.Serialize(new { totalGilBudget = maximumGil, city = cityKey, items });

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

    private void LoadCityOptions()
    {
        cityLoadAttempted = true;
        try
        {
            var response = pluginInterface.GetIpcSubscriber<string>("Emptor.GetCities").InvokeFunc();
            using var document = JsonDocument.Parse(response);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new FormatException("response root was not an array");

            var parsed = new List<EmptorCityOption>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    throw new FormatException("a city entry was not an object");

                var providerKey = GetString(element, "key").Trim();
                var label = GetString(element, "display").Trim();
                var route = GetString(element, "route").Trim();
                if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(label))
                    throw new FormatException("a city entry had no key or display label");

                var compatibilityKey = IsUldah(providerKey, label) ? string.Empty : providerKey;
                if (!keys.Add(compatibilityKey))
                    throw new FormatException($"duplicate city key '{providerKey}'");

                parsed.Add(new EmptorCityOption(
                    compatibilityKey,
                    string.IsNullOrEmpty(compatibilityKey) ? $"{label} (Emptor default)" : label,
                    route));
            }

            if (parsed.Count == 0 || parsed.All(city => !string.IsNullOrEmpty(city.Key)))
                throw new FormatException("the city list did not include Ul'dah");

            cityOptions = parsed;
            UsesDynamicCityOptions = true;
            CityOptionsStatusText = $"Loaded {parsed.Count} city choices from Emptor v{ApiVersion}.";
        }
        catch (Exception ex)
        {
            UseFallbackCityOptions($"Using built-in city choices because Emptor.GetCities returned invalid data: {ex.Message}", allowFutureAttempt: false);
            log.Warning($"[Emptor] GetCities unavailable or invalid: {ex.Message}");
        }
    }

    private void UseFallbackCityOptions(string status, bool allowFutureAttempt)
    {
        cityOptions = FallbackCities;
        UsesDynamicCityOptions = false;
        CityOptionsStatusText = status;
        if (allowFutureAttempt)
            cityLoadAttempted = false;
    }

    private void StartPriceLookup(
        IReadOnlyCollection<uint> marketableKnownMapItemIds,
        EmptorPriceLookupScope scope,
        bool refresh,
        bool isInitial)
    {
        var itemIds = marketableKnownMapItemIds
            .Where(itemId => itemId != 0)
            .Distinct()
            .OrderBy(itemId => itemId)
            .ToArray();

        if (itemIds.Length == 0)
        {
            if (isInitial)
                initialPriceRequestIssued = true;
            manualPriceRefreshRequested = false;
            PriceRefreshStatus = EmptorPriceRefreshStatus.Unavailable;
            PriceStatusText = "Price hints unavailable: no marketable known maps were found.";
            return;
        }

        if (isInitial)
            initialPriceRequestIssued = true;
        manualPriceRefreshRequested = false;
        LastPriceLookupScope = scope;
        NextManualPriceRefreshUtc = DateTime.UtcNow + ManualPriceRefreshCooldown;
        PriceRefreshStatus = EmptorPriceRefreshStatus.Refreshing;
        PriceStatusText = $"Requesting {itemIds.Length} Emptor price hint(s) for {GetScopeLabel(scope)}...";

        try
        {
            var response = InvokePriceLookup(itemIds, scope, refresh);
            ApplyPriceResponse(response, itemIds, scope, allowPendingFollowUp: true);
        }
        catch (Exception ex)
        {
            SetUnavailableSnapshots(itemIds, scope, $"Emptor price lookup failed: {ex.Message}");
            PriceRefreshStatus = EmptorPriceRefreshStatus.Failed;
            PriceStatusText = $"Price hints unavailable: Emptor lookup failed ({ex.Message}).";
            log.Warning($"[Emptor] LookupPrices failed: {ex.Message}");
        }
    }

    private void CompletePendingPriceLookup()
    {
        var itemIds = pendingPriceItemIds;
        var scope = pendingPriceScope;
        pendingPriceItemIds = Array.Empty<uint>();
        pendingPriceFollowUpAtUtc = DateTime.MinValue;

        try
        {
            var response = InvokePriceLookup(itemIds, scope, refresh: false);
            ApplyPriceResponse(response, itemIds, scope, allowPendingFollowUp: false);
        }
        catch (Exception ex)
        {
            MergeUnavailableSnapshots(itemIds, scope, $"Emptor price follow-up failed: {ex.Message}");
            PriceRefreshStatus = EmptorPriceRefreshStatus.Failed;
            PriceStatusText = $"Emptor price follow-up failed for {itemIds.Count} map(s): {ex.Message}";
            log.Warning($"[Emptor] LookupPrices pending follow-up failed: {ex.Message}");
        }
    }

    private string InvokePriceLookup(
        IReadOnlyCollection<uint> itemIds,
        EmptorPriceLookupScope scope,
        bool refresh)
    {
        var items = itemIds.Select(itemId => new { itemId }).ToArray();
        var request = JsonSerializer.Serialize(new
        {
            scope = GetScopeWireValue(scope),
            refresh,
            items,
        });
        return pluginInterface
            .GetIpcSubscriber<string, string>("Emptor.LookupPrices")
            .InvokeFunc(request);
    }

    private void ApplyPriceResponse(
        string response,
        IReadOnlyCollection<uint> expectedItemIds,
        EmptorPriceLookupScope scope,
        bool allowPendingFollowUp)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("response root was not an object");

            var topLevelError = GetOptionalString(root, "error");
            if (!string.IsNullOrWhiteSpace(topLevelError))
            {
                MergeUnavailableSnapshots(expectedItemIds, scope, topLevelError);
                PriceRefreshStatus = EmptorPriceRefreshStatus.Failed;
                PriceStatusText = $"Price hints unavailable: {topLevelError}";
                return;
            }

            var parsedSnapshots = new Dictionary<uint, EmptorPriceSnapshot>();
            if (root.TryGetProperty("items", out var itemsElement))
            {
                if (itemsElement.ValueKind != JsonValueKind.Array)
                    throw new FormatException("items was not an array");

                foreach (var itemElement in itemsElement.EnumerateArray())
                {
                    var snapshot = ParsePriceSnapshot(itemElement, scope);
                    if (snapshot.ItemId != 0)
                        parsedSnapshots[snapshot.ItemId] = snapshot;
                }
            }

            var pendingIds = new HashSet<uint>();
            if (root.TryGetProperty("pending", out var pendingElement))
            {
                if (pendingElement.ValueKind != JsonValueKind.Array)
                    throw new FormatException("pending was not an array");

                foreach (var pendingItem in pendingElement.EnumerateArray())
                {
                    if (!pendingItem.TryGetUInt32(out var itemId) || itemId == 0)
                        throw new FormatException("pending contained an invalid item ID");
                    pendingIds.Add(itemId);
                }
            }

            var expected = expectedItemIds.ToHashSet();
            foreach (var itemId in expected)
            {
                if (parsedSnapshots.TryGetValue(itemId, out var snapshot))
                {
                    priceSnapshots[itemId] = snapshot;
                }
                else if (pendingIds.Contains(itemId))
                {
                    priceSnapshots[itemId] = UnavailableSnapshot(
                        itemId,
                        scope,
                        allowPendingFollowUp
                            ? "Emptor price lookup is pending."
                            : "Emptor price lookup was still pending after the required follow-up.");
                }
                else
                {
                    priceSnapshots[itemId] = UnavailableSnapshot(itemId, scope, "Emptor returned no price data for this item.");
                }
            }

            var expectedPending = pendingIds.Where(expected.Contains).OrderBy(itemId => itemId).ToArray();
            if (allowPendingFollowUp && expectedPending.Length > 0)
            {
                pendingPriceItemIds = expectedPending;
                pendingPriceScope = scope;
                pendingPriceFollowUpAtUtc = DateTime.UtcNow + PricePendingFollowUpDelay;
                PriceRefreshStatus = EmptorPriceRefreshStatus.PendingFollowUp;
                PriceStatusText = $"Emptor price lookup pending for {expectedPending.Length} map(s); one follow-up is scheduled.";
                return;
            }

            UpdatePriceCompletionStatus(scope);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            MergeUnavailableSnapshots(expectedItemIds, scope, $"Malformed Emptor price response: {ex.Message}");
            PriceRefreshStatus = EmptorPriceRefreshStatus.Failed;
            PriceStatusText = $"Price hints unavailable: malformed Emptor response ({ex.Message}).";
            log.Warning($"[Emptor] LookupPrices response invalid: {ex.Message}");
        }
    }

    private static EmptorPriceSnapshot ParsePriceSnapshot(JsonElement itemElement, EmptorPriceLookupScope requestedScope)
    {
        if (itemElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("a price item was not an object");
        if (!itemElement.TryGetProperty("itemId", out var itemIdElement) ||
            !itemIdElement.TryGetUInt32(out var itemId) ||
            itemId == 0)
        {
            throw new FormatException("a price item had no valid item ID");
        }

        var itemError = GetOptionalString(itemElement, "error");
        var fetchedAtUtc = ParseUnixMilliseconds(itemElement, "fetchedUnixMs");
        if (!string.IsNullOrWhiteSpace(itemError))
            return UnavailableSnapshot(itemId, requestedScope, itemError, fetchedAtUtc);

        if (!itemElement.TryGetProperty("levels", out var levelsElement) || levelsElement.ValueKind != JsonValueKind.Array)
            return UnavailableSnapshot(itemId, requestedScope, "Emptor returned no price levels for this item.", fetchedAtUtc);

        var wantedLevel = GetScopeWireValue(requestedScope);
        JsonElement? selectedLevel = null;
        foreach (var levelElement in levelsElement.EnumerateArray())
        {
            if (levelElement.ValueKind != JsonValueKind.Object)
                throw new FormatException($"price levels for item {itemId} contained a non-object entry");
            if (string.Equals(GetOptionalString(levelElement, "level"), wantedLevel, StringComparison.OrdinalIgnoreCase))
            {
                selectedLevel = levelElement;
                break;
            }
        }

        if (selectedLevel is not { } level)
            return UnavailableSnapshot(itemId, requestedScope, $"Emptor returned no {GetScopeLabel(requestedScope)} price level.", fetchedAtUtc);

        var location = GetOptionalString(level, "location");
        if (!level.TryGetProperty("nq", out var nqElement) || nqElement.ValueKind != JsonValueKind.Object ||
            !nqElement.TryGetProperty("minListing", out var listingElement) || listingElement.ValueKind != JsonValueKind.Object)
        {
            return UnavailableSnapshot(itemId, requestedScope, "No NQ listings were returned for this scope.", fetchedAtUtc, location);
        }

        if (!listingElement.TryGetProperty("price", out var priceElement) ||
            !priceElement.TryGetInt64(out var price) || price <= 0)
        {
            return UnavailableSnapshot(itemId, requestedScope, "No positive NQ minimum listing was returned for this scope.", fetchedAtUtc, location);
        }

        var error = price > 999_999_999
            ? "The NQ minimum listing exceeds Loot Goblin's maximum ceiling."
            : string.Empty;
        return new EmptorPriceSnapshot(
            itemId,
            price,
            GetOptionalString(listingElement, "world"),
            location,
            GetOptionalString(listingElement, "age"),
            requestedScope,
            fetchedAtUtc,
            error);
    }

    private void SetUnavailableSnapshots(
        IReadOnlyCollection<uint> itemIds,
        EmptorPriceLookupScope scope,
        string error)
    {
        priceSnapshots = itemIds
            .Distinct()
            .ToDictionary(itemId => itemId, itemId => UnavailableSnapshot(itemId, scope, error));
    }

    private void MergeUnavailableSnapshots(
        IReadOnlyCollection<uint> itemIds,
        EmptorPriceLookupScope scope,
        string error)
    {
        foreach (var itemId in itemIds.Distinct())
            priceSnapshots[itemId] = UnavailableSnapshot(itemId, scope, error);
    }

    private void UpdatePriceCompletionStatus(EmptorPriceLookupScope scope)
    {
        var matching = priceSnapshots.Values.Where(snapshot => snapshot.Scope == scope).ToArray();
        var available = matching.Count(snapshot => snapshot.HasPositiveHint);
        var unavailable = matching.Length - available;
        PriceRefreshStatus = EmptorPriceRefreshStatus.Complete;
        PriceStatusText = unavailable == 0
            ? $"Loaded {available} Emptor price hint(s) for {GetScopeLabel(scope)}."
            : $"Loaded {available} Emptor price hint(s) for {GetScopeLabel(scope)}; {unavailable} unavailable.";
    }

    private static EmptorPriceSnapshot UnavailableSnapshot(
        uint itemId,
        EmptorPriceLookupScope scope,
        string error,
        DateTimeOffset? fetchedAtUtc = null,
        string location = "")
        => new(itemId, null, string.Empty, location, string.Empty, scope, fetchedAtUtc, error);

    private static DateTimeOffset? ParseUnixMilliseconds(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;
        if (!property.TryGetInt64(out var unixMs) || unixMs <= 0)
            throw new FormatException($"{propertyName} was not a positive integer");

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new FormatException($"{propertyName} was outside the supported range", ex);
        }
    }

    private static bool IsUldah(string key, string label)
        => string.Equals(key, "uldah", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(label, "Ul'dah", StringComparison.OrdinalIgnoreCase);

    private static string GetScopeWireValue(EmptorPriceLookupScope scope) => scope switch
    {
        EmptorPriceLookupScope.World => "world",
        EmptorPriceLookupScope.DataCenter => "datacenter",
        EmptorPriceLookupScope.Region => "region",
        EmptorPriceLookupScope.Reachable => "reachable",
        _ => "world",
    };

    public static string GetScopeLabel(EmptorPriceLookupScope scope) => scope switch
    {
        EmptorPriceLookupScope.World => "current world",
        EmptorPriceLookupScope.DataCenter => "current data center",
        EmptorPriceLookupScope.Region => "current region",
        EmptorPriceLookupScope.Reachable => "reachable regions + Materia",
        _ => "current world",
    };

    private static string FormatCountdown(TimeSpan remaining)
        => $"{Math.Max(0, (int)remaining.TotalMinutes):00}:{Math.Max(0, remaining.Seconds):00}";

    private static string GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (property.ValueKind != JsonValueKind.String)
            throw new FormatException($"{propertyName} was not a string");
        return property.GetString() ?? string.Empty;
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
