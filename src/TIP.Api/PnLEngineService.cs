using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TIP.Api.Hubs;
using TIP.Connector;
using TIP.Core.Engines;
using TIP.Core.Resilience;

namespace TIP.Api;

/// <summary>
/// Background service that feeds tick events to the PnLEngine for real-time P&amp;L calculation.
///
/// Design rationale:
/// - Reads from a dedicated fan-out tick channel (separate from TickWriterService)
///   so both DB persistence and P&amp;L calculation consume ticks independently.
/// - Broadcasts tick and position updates via WebSocketHub for dashboard push.
/// - Logs aggregate P&amp;L stats every 60 seconds for monitoring.
/// - Waits for PipelineOrchestrator to reach LIVE state before processing.
/// </summary>
public sealed class PnLEngineService : BackgroundService
{
    private readonly ILogger<PnLEngineService> _logger;
    private readonly ChannelReader<TickEvent> _tickReader;
    private readonly PnLEngine _pnlEngine;
    private readonly PipelineOrchestrator _orchestrator;
    private readonly ExposureEngine _exposureEngine;
    private readonly IWebSocketBroadcaster _broadcaster;
    private readonly PriceCache _priceCache;
    private readonly SymbolCache _symbolCache;
    private readonly ServiceHealthTracker _healthTracker;
    private readonly ConcurrentDictionary<string, double> _firstBid = new();
    private long _ticksProcessed;

    /// <summary>
    /// Initializes the P&amp;L engine background service.
    /// </summary>
    public PnLEngineService(
        ILogger<PnLEngineService> logger,
        ChannelReader<TickEvent> tickReader,
        PnLEngine pnlEngine,
        PipelineOrchestrator orchestrator,
        ExposureEngine exposureEngine,
        IWebSocketBroadcaster broadcaster,
        PriceCache priceCache,
        SymbolCache symbolCache,
        ServiceHealthTracker healthTracker)
    {
        _logger = logger;
        _tickReader = tickReader;
        _pnlEngine = pnlEngine;
        _orchestrator = orchestrator;
        _exposureEngine = exposureEngine;
        _broadcaster = broadcaster;
        _priceCache = priceCache;
        _symbolCache = symbolCache;
        _healthTracker = healthTracker;
    }

    /// <summary>
    /// Main loop: reads ticks and forwards to PnLEngine. Logs stats every 60s.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PnLEngineService started — waiting for pipeline to go live");

        // Wait for pipeline to reach at least BUFFERING state (ticks flow immediately)
        while (_orchestrator.State < PipelineOrchestratorState.Buffering && !stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(500, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        _logger.LogInformation("PnLEngineService active — processing ticks");

        // Start periodic stats logging
        var statsTask = LogStatsAsync(stoppingToken);

        try
        {
            await foreach (var tick in _tickReader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    _pnlEngine.OnTick(tick.Symbol, tick.Bid, tick.Ask);
                    Interlocked.Increment(ref _ticksProcessed);

                    // Update the shared price cache for REST API consumers
                    _priceCache.Update(tick.Symbol, tick.Bid, tick.Ask, tick.TimeMsc);

                    // Track first bid for change calculation
                    _firstBid.TryAdd(tick.Symbol, tick.Bid);
                    var firstBid = _firstBid.GetOrAdd(tick.Symbol, tick.Bid);
                    var change = tick.Bid - firstBid;
                    var changePct = firstBid != 0 ? (change / firstBid) * 100.0 : 0;

                    // Broadcast price to connected dashboards — includes digits for accurate frontend formatting
                    var digits = _symbolCache.GetDigits(tick.Symbol);
                    await _broadcaster.BroadcastPriceUpdate(new SymbolPriceDto(
                        tick.Symbol, tick.Bid, tick.Ask, tick.Ask - tick.Bid,
                        tick.TimeMsc, change, changePct, digits)).ConfigureAwait(false);

                    _healthTracker.RecordSuccess("pnlEngine");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var errors = _healthTracker.RecordError("pnlEngine");
                    _logger.LogError(ex, "PnLEngineService loop iteration failed — continuing");
                    if (errors >= 50)
                    {
                        _logger.LogCritical("PnLEngineService failing repeatedly — {Errors} consecutive errors — possible systemic issue", errors);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown
        }

        await statsTask.ConfigureAwait(false);
        _logger.LogInformation("PnLEngineService stopped — {TicksProcessed} ticks processed", _ticksProcessed);
    }

    /// <summary>
    /// Broadcasts live position P&amp;L updates every 500ms and logs stats every 60 seconds.
    /// Position data stays accurate via deal events (open/close/partial close) processed
    /// by ComputeEngineService → PnLEngine. No MT5 polling needed.
    /// </summary>
    private async Task LogStatsAsync(CancellationToken ct)
    {
        var lastStatsLog = DateTimeOffset.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);

                // Broadcast position P&L updates every 500ms for live dashboard
                var allPnl = _pnlEngine.GetAllPnL();
                foreach (var pnl in allPnl.Values)
                {
                    await _broadcaster.BroadcastPositionUpdate(new PositionSummaryDto(
                        pnl.PositionId, pnl.Login, pnl.Symbol,
                        pnl.Direction, pnl.Volume, pnl.OpenPrice,
                        pnl.CurrentPrice, pnl.UnrealizedPnL, pnl.Swap)).ConfigureAwait(false);
                }

                // Log stats + recalculate exposure every 60 seconds
                if ((DateTimeOffset.UtcNow - lastStatsLog).TotalSeconds >= 60)
                {
                    lastStatsLog = DateTimeOffset.UtcNow;
                    _exposureEngine.Recalculate(allPnl);

                    _logger.LogInformation(
                        "PnL stats: {Positions} positions, total P&L={TotalPnL:F2}, ticks processed={TicksProcessed}",
                        _pnlEngine.TrackedPositionCount, _pnlEngine.TotalUnrealizedPnL, _ticksProcessed);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
