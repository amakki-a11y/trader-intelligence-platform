using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using TIP.Connector;
using TIP.Core.Models;
using TIP.Core.Resilience;

using TIP.Data;

namespace TIP.Api;

/// <summary>
/// Background service that reads DealEvents from the Channel&lt;DealEvent&gt; pipeline
/// and writes them to TimescaleDB via DealRepository.
///
/// Design rationale:
/// - Deals arrive at a much lower rate than ticks (~100/sec vs 250/sec), so we use
///   smaller batches with INSERT ON CONFLICT for idempotency.
/// - Batches deals by count or time interval (whichever triggers first).
/// - Converts DealEvent (transit record) to DealRecord (persistence record) with server tag.
/// - Circuit breaker wraps DB writes: on repeated failures, rejects fast and buffers accumulate.
/// - Graceful shutdown: drains remaining channel items and does a final flush.
/// </summary>
public sealed class DealWriterService : BackgroundService
{
    private readonly ILogger<DealWriterService> _logger;
    private readonly ChannelReader<DealEvent> _dealReader;
    private readonly DealRepository _dealRepository;
    private readonly bool _dbEnabled;
    private readonly int _batchSize;
    private readonly int _flushIntervalMs;
    private volatile string _serverName;
    private List<DealRecord> _buffer;
    private readonly object _bufferLock = new();
    private readonly CircuitBreaker<int> _dbCircuit;
    private readonly ServiceHealthTracker _healthTracker;
    private long _totalProcessed;
    private DateTimeOffset _lastCircuitWarning = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes the deal writer background service.
    /// </summary>
    /// <param name="logger">Logger for service lifecycle events.</param>
    /// <param name="dealReader">Channel reader for incoming deal events.</param>
    /// <param name="dealRepository">Deal repository for database writes.</param>
    /// <param name="dbCircuit">Circuit breaker for database writes.</param>
    /// <param name="healthTracker">Shared health tracker for service metrics.</param>
    /// <param name="dbEnabled">Whether database writes are enabled (false = log-only mode).</param>
    /// <param name="batchSize">Deal batch size before flush (default 500).</param>
    /// <param name="flushIntervalMs">Max interval between flushes in ms (default 2000).</param>
    /// <param name="serverName">MT5 server name for deal records.</param>
    public DealWriterService(
        ILogger<DealWriterService> logger,
        ChannelReader<DealEvent> dealReader,
        DealRepository dealRepository,
        CircuitBreaker<int> dbCircuit,
        ServiceHealthTracker healthTracker,
        bool dbEnabled = false,
        int batchSize = 500,
        int flushIntervalMs = 2000,
        string serverName = "")
    {
        _logger = logger;
        _dealReader = dealReader;
        _dealRepository = dealRepository;
        _dbCircuit = dbCircuit;
        _healthTracker = healthTracker;
        _dbEnabled = dbEnabled;
        _batchSize = batchSize;
        _flushIntervalMs = flushIntervalMs;
        _serverName = serverName;
        _buffer = new List<DealRecord>(batchSize);
    }

    /// <summary>
    /// Total deals processed since startup.
    /// </summary>
    public long TotalProcessed => Interlocked.Read(ref _totalProcessed);

    /// <summary>
    /// Updates the server name tag for new deal records. Called on server switch.
    /// </summary>
    public void UpdateServerName(string serverName)
    {
        _serverName = serverName;
        _logger.LogInformation("DealWriterService server name updated to '{Server}'", serverName);
    }

    /// <summary>
    /// Main service loop: reads deals from the channel and batches them for database writes.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "DealWriterService started (db={DbEnabled}, batchSize={BatchSize}, flushInterval={FlushMs}ms)",
            _dbEnabled, _batchSize, _flushIntervalMs);

        // Start timer-based flush task
        var flushTask = RunPeriodicFlushAsync(stoppingToken);

        try
        {
            await foreach (var deal in _dealReader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    Interlocked.Increment(ref _totalProcessed);

                    var record = ConvertToRecord(deal);
                    bool batchFull;

                    lock (_bufferLock)
                    {
                        _buffer.Add(record);
                        batchFull = _buffer.Count >= _batchSize;
                    }

                    if (batchFull && _dbEnabled)
                    {
                        await FlushBufferWithCircuitBreaker(stoppingToken).ConfigureAwait(false);
                    }

                    _healthTracker.RecordSuccess("dealWriter");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var errors = _healthTracker.RecordError("dealWriter");
                    _logger.LogError(ex, "DealWriterService loop iteration failed — continuing");
                    if (errors >= 50)
                    {
                        _logger.LogCritical("DealWriterService failing repeatedly — {Errors} consecutive errors — possible systemic issue", errors);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown
        }

        // Final flush on shutdown
        if (_dbEnabled)
        {
            await FlushBufferAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await flushTask.ConfigureAwait(false);

        _logger.LogInformation(
            "DealWriterService stopped (totalProcessed={TotalProcessed}, totalInserted={TotalInserted})",
            TotalProcessed, _dealRepository.TotalInserted);
    }

    /// <summary>
    /// Flushes buffered deals through the circuit breaker.
    /// On open circuit, logs a warning at most once per minute.
    /// </summary>
    private async Task FlushBufferWithCircuitBreaker(CancellationToken cancellationToken)
    {
        try
        {
            await _dbCircuit.ExecuteAsync(async () =>
            {
                await FlushBufferAsync(cancellationToken).ConfigureAwait(false);
                return 0;
            }).ConfigureAwait(false);
        }
        catch (CircuitBreakerOpenException)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastCircuitWarning >= TimeSpan.FromMinutes(1))
            {
                _lastCircuitWarning = now;
                int count;
                lock (_bufferLock) { count = _buffer.Count; }
                _logger.LogWarning("DB circuit open — {N} deals buffered", count);
            }
        }
    }

    /// <summary>
    /// Flushes buffered deals to TimescaleDB.
    /// </summary>
    private async Task FlushBufferAsync(CancellationToken cancellationToken)
    {
        List<DealRecord> batch;

        lock (_bufferLock)
        {
            if (_buffer.Count == 0)
                return;

            batch = _buffer;
            _buffer = new List<DealRecord>(batch.Capacity);
        }

        try
        {
            await _dealRepository.BulkInsertAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23505")
        {
            // Duplicate key — deals already exist. Fall back to individual upserts (ON CONFLICT DO NOTHING)
            _logger.LogWarning("Deal bulk insert hit duplicates — falling back to individual upsert for {Count} deals", batch.Count);
            var inserted = 0;
            foreach (var deal in batch)
            {
                try
                {
                    await _dealRepository.InsertAsync(deal, cancellationToken).ConfigureAwait(false);
                    inserted++;
                }
                catch (Exception innerEx) when (innerEx is not OperationCanceledException)
                {
                    // Individual insert also failed — skip this deal
                }
            }
            _logger.LogInformation("Individual upsert complete — {Inserted}/{Total} deals inserted", inserted, batch.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Deal batch flush failed for {Count} deals — re-buffering", batch.Count);

            lock (_bufferLock)
            {
                _buffer.InsertRange(0, batch);
            }

            throw; // Re-throw so circuit breaker sees the failure
        }
    }

    /// <summary>
    /// Periodic flush loop: flushes partial batches every flushIntervalMs.
    /// </summary>
    private async Task RunPeriodicFlushAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_flushIntervalMs, stoppingToken).ConfigureAwait(false);

                int bufferedCount;
                lock (_bufferLock)
                {
                    bufferedCount = _buffer.Count;
                }

                if (_dbEnabled && bufferedCount > 0)
                {
                    await FlushBufferWithCircuitBreaker(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Periodic deal flush failed — will retry");
            }
        }
    }

    /// <summary>
    /// Converts a DealEvent (pipeline transit record) to a DealRecord (persistence record).
    /// </summary>
    private DealRecord ConvertToRecord(DealEvent deal)
    {
        return new DealRecord
        {
            DealId = deal.DealId,
            Login = deal.Login,
            TimeMsc = deal.TimeMsc,
            Symbol = deal.Symbol,
            Action = deal.Action,
            Volume = deal.Volume,
            Price = deal.Price,
            Profit = deal.Profit,
            Commission = deal.Commission,
            Swap = deal.Swap,
            Fee = deal.Fee,
            Reason = deal.Reason,
            ExpertId = deal.ExpertId,
            Comment = deal.Comment,
            PositionId = deal.PositionId,
            Entry = deal.Entry,
            ReceivedAt = deal.ReceivedAt,
            Server = _serverName
        };
    }
}
