using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TIP.Connector;

/// <summary>
/// Rate-limited historical data backfill from the MT5 Manager API via IMT5Api.
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
    private readonly IMT5Api _api;
    private readonly ChannelWriter<DealEvent> _dealWriter;
    private readonly ChannelWriter<TickEvent> _tickWriter;
    private readonly SyncStateTracker _syncTracker;
    private readonly SemaphoreSlim _concurrencyLimiter = new(2, 2);
    private const int ChunkDelayMs = 500;

    /// <summary>
    /// Initializes the history fetcher with rate-limiting controls.
    /// </summary>
    /// <param name="logger">Logger for backfill progress tracking.</param>
    /// <param name="api">MT5 API for requesting historical data.</param>
    /// <param name="dealWriter">Channel writer for deal events loaded from history.</param>
    /// <param name="tickWriter">Channel writer for tick events loaded from history.</param>
    /// <param name="syncTracker">Sync state tracker for checkpoint lookups.</param>
    public HistoryFetcher(
        ILogger<HistoryFetcher> logger,
        IMT5Api api,
        ChannelWriter<DealEvent> dealWriter,
        ChannelWriter<TickEvent> tickWriter,
        SyncStateTracker syncTracker)
    {
        _logger = logger;
        _api = api;
        _dealWriter = dealWriter;
        _tickWriter = tickWriter;
        _syncTracker = syncTracker;
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

        for (var i = 0; i < logins.Count; i++)
        {
            var login = logins[i];
            cancellationToken.ThrowIfCancellationRequested();

            if (i > 0 && i % 10 == 0)
            {
                _logger.LogInformation(
                    "Backfilled {Current}/{Total} logins ({DealCount} deals so far)...",
                    i, logins.Count, loadedDealIds.Count);
            }

            await _concurrencyLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Check sync state for last checkpoint; default to 90 days back
                var lastSync = _syncTracker.GetLastSyncTimestamp("deal_login", login.ToString());
                var from = lastSync ?? cutoffTime.AddDays(-90);
                var to = cutoffTime;

                var rawDeals = _api.RequestDeals(login, from, to);

                foreach (var raw in rawDeals)
                {
                    loadedDealIds.Add(raw.DealId);

                    var dealEvent = new DealEvent(
                        DealId: raw.DealId,
                        Login: raw.Login,
                        TimeMsc: raw.TimeMsc,
                        Symbol: raw.Symbol,
                        Action: (int)raw.Action,
                        Volume: raw.VolumeLots,
                        Price: raw.Price,
                        Profit: raw.Profit,
                        Commission: raw.Commission,
                        Swap: raw.Storage,
                        Fee: raw.Fee,
                        Reason: (int)raw.Reason,
                        ExpertId: raw.ExpertId,
                        Comment: raw.Comment,
                        PositionId: raw.PositionId,
                        ReceivedAt: DateTimeOffset.UtcNow);

                    _dealWriter.TryWrite(dealEvent);
                }

                if (rawDeals.Count > 0)
                {
                    _syncTracker.UpdateCheckpoint("deal_login", login.ToString(), to);
                }

                _logger.LogDebug(
                    "Backfilled {Count} deals for login {Login}", rawDeals.Count, login);
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
                var lastSync = _syncTracker.GetLastSyncTimestamp("tick_symbol", symbol);
                var from = lastSync ?? cutoffTime;
                var to = DateTimeOffset.UtcNow;

                var rawTicks = _api.RequestTicks(symbol, from, to);

                foreach (var raw in rawTicks)
                {
                    var tickEvent = new TickEvent(
                        Symbol: raw.Symbol,
                        Bid: raw.Bid,
                        Ask: raw.Ask,
                        TimeMsc: raw.TimeMsc,
                        ReceivedAt: DateTimeOffset.UtcNow);

                    _tickWriter.TryWrite(tickEvent);
                }

                if (rawTicks.Count > 0)
                {
                    _syncTracker.UpdateCheckpoint("tick_symbol", symbol, to);
                }

                _logger.LogDebug(
                    "Backfilled {Count} ticks for symbol {Symbol}", rawTicks.Count, symbol);
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
