using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TIP.Connector;

namespace TIP.Api;

/// <summary>
/// Fans out deal and tick events from the main ingest channels to multiple consumer channels.
///
/// Design rationale:
/// - The main deal/tick channels receive events from DealSink and TickListener respectively.
/// - Multiple consumers (DB writers + compute engines) need independent copies of every event.
/// - Channel&lt;T&gt; with multiple readers is a race (each reader gets a different event),
///   so we fan out explicitly: one reader on the main channel, N writers to consumer channels.
/// </summary>
public sealed class ChannelFanOutService : BackgroundService
{
    private readonly ILogger<ChannelFanOutService> _logger;
    private readonly ChannelReader<DealEvent> _dealSource;
    private readonly ChannelWriter<DealEvent>[] _dealTargets;
    private readonly ChannelReader<TickEvent> _tickSource;
    private readonly ChannelWriter<TickEvent>[] _tickTargets;

    /// <summary>
    /// Initializes the fan-out service.
    /// </summary>
    public ChannelFanOutService(
        ILogger<ChannelFanOutService> logger,
        ChannelReader<DealEvent> dealSource,
        ChannelWriter<DealEvent>[] dealTargets,
        ChannelReader<TickEvent> tickSource,
        ChannelWriter<TickEvent>[] tickTargets)
    {
        _logger = logger;
        _dealSource = dealSource;
        _dealTargets = dealTargets;
        _tickSource = tickSource;
        _tickTargets = tickTargets;
    }

    /// <summary>
    /// Runs both deal and tick fan-out loops concurrently.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ChannelFanOutService started — {DealTargets} deal targets, {TickTargets} tick targets",
            _dealTargets.Length, _tickTargets.Length);

        var dealTask = FanOutDeals(stoppingToken);
        var tickTask = FanOutTicks(stoppingToken);

        await Task.WhenAll(dealTask, tickTask).ConfigureAwait(false);

        _logger.LogInformation("ChannelFanOutService stopped");
    }

    /// <summary>
    /// Reads deals from source and writes to all deal targets.
    /// </summary>
    private async Task FanOutDeals(CancellationToken ct)
    {
        try
        {
            await foreach (var deal in _dealSource.ReadAllAsync(ct).ConfigureAwait(false))
            {
                foreach (var target in _dealTargets)
                {
                    target.TryWrite(deal);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during shutdown
        }
    }

    /// <summary>
    /// Reads ticks from source and writes to all tick targets.
    /// </summary>
    private async Task FanOutTicks(CancellationToken ct)
    {
        try
        {
            await foreach (var tick in _tickSource.ReadAllAsync(ct).ConfigureAwait(false))
            {
                foreach (var target in _tickTargets)
                {
                    target.TryWrite(tick);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during shutdown
        }
    }
}
