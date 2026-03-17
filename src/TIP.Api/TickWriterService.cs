using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TIP.Connector;

using TIP.Data;

namespace TIP.Api;

/// <summary>
/// Background service that reads TickEvents from the Channel&lt;TickEvent&gt; pipeline
/// and writes them to TimescaleDB in batches via TickWriter.
///
/// Design rationale:
/// - Decouples the MT5 tick ingestion rate from the database write rate.
/// - Uses two flush triggers: batch size threshold OR timer interval, whichever
///   fires first. This ensures both high throughput (large batches) and low latency
///   (partial batches flushed within flushIntervalMs).
/// - A separate Task handles the timer-based flush so the main read loop isn't blocked.
/// - Graceful shutdown: drains remaining channel items and does a final flush.
/// </summary>
public sealed class TickWriterService : BackgroundService
{
    private readonly ILogger<TickWriterService> _logger;
    private readonly ChannelReader<TickEvent> _tickReader;
    private readonly TickWriter _tickWriter;
    private readonly bool _dbEnabled;

    /// <summary>
    /// Initializes the tick writer background service.
    /// </summary>
    /// <param name="logger">Logger for service lifecycle events.</param>
    /// <param name="tickReader">Channel reader for incoming tick events.</param>
    /// <param name="tickWriter">Tick writer that handles batching and COPY protocol writes.</param>
    /// <param name="dbEnabled">Whether database writes are enabled (false = log-only mode).</param>
    public TickWriterService(
        ILogger<TickWriterService> logger,
        ChannelReader<TickEvent> tickReader,
        TickWriter tickWriter,
        bool dbEnabled = false)
    {
        _logger = logger;
        _tickReader = tickReader;
        _tickWriter = tickWriter;
        _dbEnabled = dbEnabled;
    }

    /// <summary>
    /// Main service loop: reads ticks from the channel and batches them for database writes.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "TickWriterService started (db={DbEnabled}, batchSize={BatchSize}, flushInterval={FlushMs}ms)",
            _dbEnabled, _tickWriter.BatchSize, _tickWriter.FlushIntervalMs);

        // Start timer-based flush task
        var flushTask = RunPeriodicFlushAsync(stoppingToken);

        try
        {
            await foreach (var tick in _tickReader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                var batchFull = _tickWriter.AddTick(tick.Symbol, tick.Bid, tick.Ask, tick.TimeMsc);

                if (batchFull && _dbEnabled)
                {
                    try
                    {
                        await _tickWriter.FlushAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Tick batch flush failed — will retry on next trigger");
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
            try
            {
                var remaining = await _tickWriter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                if (remaining > 0)
                {
                    _logger.LogInformation("Final tick flush: {Count} ticks written", remaining);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Final tick flush failed — {Count} ticks may be lost",
                    _tickWriter.BufferedCount);
            }
        }

        await flushTask.ConfigureAwait(false);

        _logger.LogInformation(
            "TickWriterService stopped (totalWritten={TotalWritten}, totalFlushed={TotalFlushed})",
            _tickWriter.TotalWritten, _tickWriter.TotalFlushed);
    }

    /// <summary>
    /// Periodic flush loop: flushes partial batches every flushIntervalMs to keep latency bounded.
    /// </summary>
    private async Task RunPeriodicFlushAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_tickWriter.FlushIntervalMs, stoppingToken).ConfigureAwait(false);

                if (_dbEnabled && _tickWriter.BufferedCount > 0)
                {
                    await _tickWriter.FlushAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Periodic tick flush failed — will retry");
            }
        }
    }
}
