using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TIP.Connector;

/// <summary>
/// Configuration for connecting to an MT5 Manager API server.
/// </summary>
/// <param name="ServerAddress">MT5 server address in "host:port" format.</param>
/// <param name="ManagerLogin">Manager account login number.</param>
/// <param name="Password">Manager account password.</param>
/// <param name="GroupMask">Group filter mask (e.g., "*" for all groups, "real\*" for real accounts).</param>
/// <param name="HealthHeartbeatIntervalMs">Interval in ms between heartbeat checks (default 30000).</param>
public sealed record ConnectionConfig(
    string ServerAddress,
    ulong ManagerLogin,
    string Password,
    string GroupMask,
    int HealthHeartbeatIntervalMs = 30000);

/// <summary>
/// Background service managing the MT5 Manager API connection lifecycle.
/// Handles connect → subscribe → heartbeat → reconnect with exponential backoff.
///
/// Design rationale:
/// - Runs as a hosted BackgroundService so ASP.NET Core manages its lifetime.
/// - Uses IMT5Api abstraction so it works with both real MT5 and simulator.
/// - Writes deal and tick events into Channel&lt;T&gt; for downstream consumers.
/// - Exponential backoff (1s → 2s → 4s → ... → 60s max) prevents hammering
///   the MT5 server during outages while recovering quickly from transient failures.
/// - Backoff resets on successful connection to ensure fast reconnect after brief blips.
/// </summary>
public sealed class MT5Connection : BackgroundService
{
    private readonly ILogger<MT5Connection> _logger;
    private readonly IMT5Api _api;
    private readonly DealSink _dealSink;
    private readonly TickListener _tickListener;
    private readonly ConnectionConfig _config;

    private const int InitialBackoffMs = 1000;
    private const int MaxBackoffMs = 60000;

    /// <summary>
    /// Initializes a new MT5 connection manager.
    /// </summary>
    /// <param name="logger">Logger for connection lifecycle events.</param>
    /// <param name="api">MT5 API implementation (real or simulator).</param>
    /// <param name="dealSink">Deal sink for buffered/live deal forwarding.</param>
    /// <param name="tickListener">Tick listener for price cache and channel writes.</param>
    /// <param name="config">MT5 server connection configuration.</param>
    public MT5Connection(
        ILogger<MT5Connection> logger,
        IMT5Api api,
        DealSink dealSink,
        TickListener tickListener,
        ConnectionConfig config)
    {
        _logger = logger;
        _api = api;
        _dealSink = dealSink;
        _tickListener = tickListener;
        _config = config;
    }

    /// <summary>
    /// Main execution loop: connect → subscribe → heartbeat → reconnect on failure.
    /// Uses exponential backoff with a 60-second cap for reconnection attempts.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoffMs = InitialBackoffMs;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation(
                    "Connecting to MT5 server {Server} as login {Login}...",
                    _config.ServerAddress, _config.ManagerLogin);

                if (!_api.Initialize())
                {
                    throw new InvalidOperationException("MT5 API initialization failed");
                }

                if (!_api.Connect(_config.ServerAddress, _config.ManagerLogin, _config.Password))
                {
                    throw new InvalidOperationException(
                        $"Failed to connect to MT5 server {_config.ServerAddress}");
                }

                // Wire deal callbacks → DealSink
                _api.OnDealAdd += OnDealAdd;
                _api.OnDealUpdate += OnDealUpdate;

                // Wire tick callbacks → TickListener
                _api.OnTick += OnTickReceived;

                // Subscribe to live events
                if (!_api.SubscribeDeals())
                {
                    _logger.LogWarning("Failed to subscribe to deal events");
                }

                if (!_api.SubscribeTicks(_config.GroupMask))
                {
                    _logger.LogWarning("Failed to subscribe to tick events");
                }

                _logger.LogInformation("Connected to MT5 server {Server}", _config.ServerAddress);
                backoffMs = InitialBackoffMs; // Reset backoff on successful connection

                // Heartbeat loop — check connection health at configured interval
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(_config.HealthHeartbeatIntervalMs, stoppingToken)
                        .ConfigureAwait(false);

                    if (!_api.IsConnected)
                    {
                        _logger.LogWarning("MT5 heartbeat detected disconnection");
                        break;
                    }

                    _logger.LogDebug(
                        "MT5 heartbeat OK — {SymbolCount} symbols cached",
                        _tickListener.CachedSymbolCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("MT5 connection shutting down gracefully");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MT5 connection failed. Reconnecting in {BackoffMs}ms...", backoffMs);

                try
                {
                    await Task.Delay(backoffMs, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
            }
            finally
            {
                // Unwire events and disconnect
                _api.OnDealAdd -= OnDealAdd;
                _api.OnDealUpdate -= OnDealUpdate;
                _api.OnTick -= OnTickReceived;
                _api.UnsubscribeDeals();
                _api.UnsubscribeTicks();
                _api.Disconnect();
            }
        }

        _logger.LogInformation("MT5 connection service stopped");
    }

    /// <summary>
    /// Converts a RawDeal to DealEvent and passes to DealSink.
    /// </summary>
    private void OnDealAdd(RawDeal raw)
    {
        var dealEvent = ConvertDeal(raw);
        _dealSink.OnDealReceived(dealEvent);
    }

    /// <summary>
    /// Handles deal updates (logged, forwarded as deal event).
    /// </summary>
    private void OnDealUpdate(RawDeal raw)
    {
        _logger.LogDebug("Deal updated: {DealId} login {Login}", raw.DealId, raw.Login);
        var dealEvent = ConvertDeal(raw);
        _dealSink.OnDealReceived(dealEvent);
    }

    /// <summary>
    /// Converts a RawTick to TickEvent and passes to TickListener.
    /// </summary>
    private void OnTickReceived(RawTick raw)
    {
        var tickEvent = new TickEvent(
            Symbol: raw.Symbol,
            Bid: raw.Bid,
            Ask: raw.Ask,
            TimeMsc: raw.TimeMsc,
            ReceivedAt: DateTimeOffset.UtcNow);

        _tickListener.OnTick(tickEvent);
    }

    /// <summary>
    /// Converts a RawDeal from the MT5 API into a DealEvent for the pipeline.
    /// </summary>
    private static DealEvent ConvertDeal(RawDeal raw)
    {
        return new DealEvent(
            DealId: raw.DealId,
            Login: raw.Login,
            TimeMsc: raw.TimeMsc,
            Symbol: raw.Symbol,
            Action: (int)raw.Action,
            Volume: raw.VolumeLots,         // Already converted from raw
            Price: raw.Price,
            Profit: raw.Profit,
            Commission: raw.Commission,
            Swap: raw.Storage,              // MT5 calls it "Storage", we call it "Swap"
            Fee: raw.Fee,
            Reason: (int)raw.Reason,
            ExpertId: raw.ExpertId,
            Comment: raw.Comment,
            PositionId: raw.PositionId,
            ReceivedAt: DateTimeOffset.UtcNow);
    }
}
