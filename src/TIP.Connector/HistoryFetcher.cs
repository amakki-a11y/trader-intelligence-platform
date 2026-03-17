using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TIP.Connector;

/// <summary>
/// Rate-limited historical data backfill from the MT5 Manager API.
///
/// Design rationale:
/// - MT5 Manager API has undocumented rate limits; hammering it with concurrent
///   history requests causes connection drops. We limit to 2 concurrent requests
///   via SemaphoreSlim and insert a 500ms delay between chunks.
/// - Returns the set of loaded deal IDs so DealSink can skip duplicates during
///   the buffer→live transition.
/// - Backfill runs once at startup (or on reconnect) to close any gaps since
///   the last checkpoint stored in SyncStateTracker.
/// </summary>
public sealed class HistoryFetcher : IDisposable
{
    private readonly ILogger<HistoryFetcher> _logger;
    private readonly SemaphoreSlim _concurrencyLimiter = new(2, 2);
    private const int ChunkDelayMs = 500;

    /// <summary>
    /// Initializes the history fetcher with rate-limiting controls.
    /// </summary>
    /// <param name="logger">Logger for backfill progress tracking.</param>
    public HistoryFetcher(ILogger<HistoryFetcher> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Backfills deal history for the specified logins from the given cutoff time.
    /// Rate-limited to max 2 concurrent MT5 API requests with 500ms inter-chunk delay.
    /// </summary>
    /// <param name="logins">Account logins to backfill deals for.</param>
    /// <param name="cutoffTime">Only fetch deals after this timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Set of deal IDs that were loaded during backfill.</returns>
    public async Task<HashSet<ulong>> BackfillDeals(
        IReadOnlyList<ulong> logins,
        DateTimeOffset cutoffTime,
        CancellationToken cancellationToken = default)
    {
        var loadedDealIds = new HashSet<ulong>();

        _logger.LogInformation(
            "Starting deal backfill for {LoginCount} logins from {CutoffTime}",
            logins.Count, cutoffTime);

        foreach (var login in logins)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _concurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // TODO: Phase 2, Task 4 — Call IMTManagerAPI.DealRequest(login, cutoffTime, now)
                // TODO: Phase 2, Task 4 — Iterate IMTDealArray, convert to DealEvent, add DealId to loadedDealIds
                // TODO: Phase 2, Task 4 — Write loaded deals to DealRepository for persistence

                _logger.LogDebug("Backfilled deals for login {Login}", login);
            }
            finally
            {
                _concurrencyLimiter.Release();
            }

            await Task.Delay(ChunkDelayMs, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Deal backfill complete. Loaded {Count} deals", loadedDealIds.Count);
        return loadedDealIds;
    }

    /// <summary>
    /// Backfills tick history for the specified symbols from the given cutoff time.
    /// Rate-limited to max 2 concurrent MT5 API requests with 500ms inter-chunk delay.
    /// </summary>
    /// <param name="symbols">Trading symbols to backfill ticks for.</param>
    /// <param name="cutoffTime">Only fetch ticks after this timestamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task BackfillTicks(
        IReadOnlyList<string> symbols,
        DateTimeOffset cutoffTime,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting tick backfill for {SymbolCount} symbols from {CutoffTime}",
            symbols.Count, cutoffTime);

        foreach (var symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _concurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // TODO: Phase 2, Task 4 — Call IMTManagerAPI.TickHistoryRequest(symbol, cutoffTime, now)
                // TODO: Phase 2, Task 4 — Convert ticks and write to TickWriter for persistence

                _logger.LogDebug("Backfilled ticks for symbol {Symbol}", symbol);
            }
            finally
            {
                _concurrencyLimiter.Release();
            }

            await Task.Delay(ChunkDelayMs, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Tick backfill complete for {SymbolCount} symbols", symbols.Count);
    }

    /// <summary>
    /// Disposes the concurrency limiter semaphore.
    /// </summary>
    public void Dispose()
    {
        _concurrencyLimiter.Dispose();
    }
}
