using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace LootGoblin.Services;

internal sealed class MogtomeEventClient : IDisposable
{
    // Eventy's public Supabase client details:
    // https://github.com/Infiziert90/Eventy/blob/ffdf4469c5cf039af8955362235b9d10a5753e38/Eventy/Updater.cs
    private const string EventsUrl =
        "https://xzwnvwjxgmaqtrxewngh.supabase.co/rest/v1/Events?id=gt.0&select=name,begin,end";
    private const string SupabaseAnonKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Inh6d252d2p4Z21hcXRyeGV3bmdoIiwicm9sZSI6ImFub24iLCJpYXQiOjE2ODk3NzcwMDIsImV4cCI6MjAwNTM1MzAwMn0.aNYTnhY_Sagi9DyH5Q9tCz9lwaRCYzMC12SZ7q7jZBc";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);

    private readonly IPluginLog log;
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly CancellationTokenSource disposeCancellation = new();
    private IReadOnlyList<MogtomeEvent> cachedEvents = Array.Empty<MogtomeEvent>();
    private DateTimeOffset cacheExpiresAtUtc = DateTimeOffset.MinValue;
    private bool disposed;

    public MogtomeEventClient(IPluginLog log)
    {
        this.log = log;
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("apikey", SupabaseAnonKey);
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {SupabaseAnonKey}");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Prefer", "return=representation");
    }

    public async Task<IReadOnlyList<MogtomeEvent>> GetEventsAsync()
    {
        if (disposed)
            return Array.Empty<MogtomeEvent>();

        var enteredGate = false;
        try
        {
            await refreshGate.WaitAsync(disposeCancellation.Token).ConfigureAwait(false);
            enteredGate = true;

            var nowUtc = DateTimeOffset.UtcNow;
            if (nowUtc < cacheExpiresAtUtc)
                return cachedEvents;

            using var response = await httpClient.GetAsync(
                    EventsUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    disposeCancellation.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log.Debug($"[LootGoblin][Mogtome] Event feed returned HTTP {(int)response.StatusCode}.");
                return Array.Empty<MogtomeEvent>();
            }

            var json = await response.Content.ReadAsStringAsync(disposeCancellation.Token).ConfigureAwait(false);
            if (!MogtomeEventPolicy.TryParseFeed(json, out var parsedEvents))
            {
                log.Debug("[LootGoblin][Mogtome] Event feed could not be parsed.");
                return Array.Empty<MogtomeEvent>();
            }

            cachedEvents = parsedEvents;
            cacheExpiresAtUtc = nowUtc + CacheDuration;
            return cachedEvents;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<MogtomeEvent>();
        }
        catch (Exception ex)
        {
            log.Debug($"[LootGoblin][Mogtome] Event feed check failed: {ex.GetType().Name}: {ex.Message}");
            return Array.Empty<MogtomeEvent>();
        }
        finally
        {
            if (enteredGate)
                refreshGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        disposeCancellation.Cancel();
        httpClient.Dispose();
        disposeCancellation.Dispose();
    }
}
