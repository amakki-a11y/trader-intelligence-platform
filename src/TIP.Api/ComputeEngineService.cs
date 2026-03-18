using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TIP.Api.Hubs;
using TIP.Connector;
using TIP.Core.Engines;
using TIP.Core.Models;

namespace TIP.Api;

/// <summary>
/// Background service that feeds deal events through the compute engine pipeline:
/// DealProcessor → AccountScorer → CorrelationEngine → PnLEngine → ExposureEngine.
///
/// Design rationale:
/// - Reads from a dedicated fan-out deal channel (separate from DealWriterService)
///   so both DB persistence and scoring consume deals independently.
/// - Broadcasts score updates, deals, and alerts via WebSocketHub for dashboard push.
/// - Waits for PipelineOrchestrator to reach LIVE state before processing.
/// - Logs alerts when scores change significantly or cross risk thresholds.
/// </summary>
public sealed class ComputeEngineService : BackgroundService
{
    private readonly ILogger<ComputeEngineService> _logger;
    private readonly ChannelReader<DealEvent> _dealReader;
    private readonly DealProcessor _dealProcessor;
    private readonly AccountScorer _accountScorer;
    private readonly CorrelationEngine _correlationEngine;
    private readonly PnLEngine _pnlEngine;
    private readonly ExposureEngine _exposureEngine;
    private readonly PipelineOrchestrator _orchestrator;
    private readonly IWebSocketBroadcaster _broadcaster;
    private long _dealsProcessed;

    /// <summary>
    /// Initializes the compute engine background service.
    /// </summary>
    public ComputeEngineService(
        ILogger<ComputeEngineService> logger,
        ChannelReader<DealEvent> dealReader,
        DealProcessor dealProcessor,
        AccountScorer accountScorer,
        CorrelationEngine correlationEngine,
        PnLEngine pnlEngine,
        ExposureEngine exposureEngine,
        PipelineOrchestrator orchestrator,
        IWebSocketBroadcaster broadcaster)
    {
        _logger = logger;
        _dealReader = dealReader;
        _dealProcessor = dealProcessor;
        _accountScorer = accountScorer;
        _correlationEngine = correlationEngine;
        _pnlEngine = pnlEngine;
        _exposureEngine = exposureEngine;
        _orchestrator = orchestrator;
        _broadcaster = broadcaster;
    }

    /// <summary>
    /// Gets the total number of deals processed since startup.
    /// </summary>
    public long DealsProcessed => Interlocked.Read(ref _dealsProcessed);

    /// <summary>
    /// Main loop: reads deals and forwards through the compute pipeline.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ComputeEngineService started — waiting for pipeline to go live");

        // Wait for pipeline to reach at least BUFFERING state (deals flow during backfill too)
        while (_orchestrator.State < PipelineOrchestratorState.Buffering && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(500, stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation("ComputeEngineService active — processing deals");

        try
        {
            await foreach (var deal in _dealReader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessDeal(deal).ConfigureAwait(false);
                Interlocked.Increment(ref _dealsProcessed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown
        }

        _logger.LogInformation("ComputeEngineService stopped — {DealsProcessed} deals processed", _dealsProcessed);
    }

    /// <summary>
    /// Processes a single deal through the full compute pipeline.
    /// </summary>
    private async Task ProcessDeal(DealEvent deal)
    {
        // Step 1: Classify the deal and determine position effect
        var result = _dealProcessor.ProcessDeal(deal.DealId, deal.Action, deal.Volume, deal.PositionId);

        // Step 2: Score the account
        var account = _accountScorer.ProcessDeal(
            deal.DealId, deal.Login, deal.Action, deal.Volume, deal.Profit,
            deal.Commission, deal.Swap, deal.ExpertId, deal.Reason,
            deal.TimeMsc, deal.Symbol, deal.PositionId);

        // Step 3: Check correlations for BUY/SELL trades
        if (result.Type == DealType.Buy || result.Type == DealType.Sell)
        {
            var fingerprint = new TradeFingerprint(
                deal.DealId, deal.Login, deal.TimeMsc, deal.Symbol,
                deal.Action, deal.Volume, deal.ExpertId);

            _correlationEngine.CheckDeal(fingerprint);
        }

        // Step 4: Update PnL position cache based on position effect
        if (result.PositionEffect == PositionAction.Opened)
        {
            _pnlEngine.OnPositionOpened(new OpenPosition
            {
                PositionId = (long)deal.PositionId,
                Login = deal.Login,
                Symbol = deal.Symbol,
                Direction = deal.Action,
                Volume = deal.Volume,
                OpenPrice = deal.Price
            });
        }
        else if (result.PositionEffect == PositionAction.Closed && result.AffectedPositionId.HasValue)
        {
            _pnlEngine.OnPositionClosed((long)result.AffectedPositionId.Value, deal.Symbol);
        }

        // Step 5: Recalculate exposure on position changes
        if (result.PositionEffect == PositionAction.Opened ||
            result.PositionEffect == PositionAction.Closed ||
            result.PositionEffect == PositionAction.Modified)
        {
            _exposureEngine.Recalculate(_pnlEngine.GetAllPnL());
        }

        // Step 6: Broadcast deal event to Live Monitor clients
        var actionName = deal.Action switch { 0 => "BUY", 1 => "SELL", 2 => "BALANCE", 6 => "BONUS", _ => $"ACTION_{deal.Action}" };
        await _broadcaster.BroadcastDealEvent(new DealEventDto(
            deal.DealId, deal.Login, deal.Symbol, actionName,
            deal.Volume, deal.Price, deal.Profit,
            account.AbuseScore, account.AbuseScore - account.PreviousScore,
            account.IsRingMember,
            account.RiskLevel.ToString(),
            deal.TimeMsc)).ConfigureAwait(false);

        // Step 7: Broadcast account update to WebSocket clients (AbuseGrid)
        await _broadcaster.BroadcastAccountUpdate(new AccountSummaryDto(
            account.Login, account.Name, account.Group, account.Server,
            account.AbuseScore, account.PreviousScore, account.RiskLevel.ToString(),
            account.TotalTrades, account.TotalVolume, account.TotalCommission,
            account.TotalProfit, account.TotalDeposits,
            account.IsRingMember, account.RingCorrelationCount,
            account.TimingEntropyCV, account.ExpertTradeRatio,
            account.LastScored)).ConfigureAwait(false);

        // Step 8: Alert on significant score changes
        var scoreDelta = Math.Abs(account.AbuseScore - account.PreviousScore);
        if (scoreDelta > 5)
        {
            _logger.LogWarning(
                "Score alert: login {Login} score changed {PreviousScore:F1} → {NewScore:F1} (Δ{Delta:F1}), risk={Risk}",
                deal.Login, account.PreviousScore, account.AbuseScore, scoreDelta, account.RiskLevel);

            await _broadcaster.BroadcastAlert(new AlertMessageDto(
                deal.Login,
                $"Score changed {account.PreviousScore:F0} → {account.AbuseScore:F0}",
                account.RiskLevel.ToString().ToLowerInvariant(),
                DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }

        // Step 9: Critical threshold alert
        if (account.AbuseScore >= 70 && account.PreviousScore < 70)
        {
            _logger.LogError(
                "CRITICAL ALERT: login {Login} crossed CRITICAL threshold — score={Score:F1}, trades={Trades}, ring={IsRing}",
                deal.Login, account.AbuseScore, account.TotalTrades, account.IsRingMember);

            await _broadcaster.BroadcastAlert(new AlertMessageDto(
                deal.Login,
                "Score crossed CRITICAL threshold",
                "critical",
                DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }
    }
}
