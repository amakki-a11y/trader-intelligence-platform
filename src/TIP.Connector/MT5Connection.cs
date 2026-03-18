using System;
using System.Threading;
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
/// Delegates the three-phase startup (buffer → backfill → live) to PipelineOrchestrator,
/// then monitors connection health and triggers reconnect + catch-up on failure.
///
/// Design rationale:
/// - Runs as a hosted BackgroundService so ASP.NET Core manages its lifetime.
/// - Uses PipelineOrchestrator for the startup sequence, which handles:
///   Phase 1: connect + buffer deals, Phase 2: backfill from checkpoint, Phase 3: go live.
/// - On reconnect, orchestrator re-runs the same sequence — SyncStateTracker ensures
///   only new data is fetched from the last checkpoint.
/// - Exponential backoff (1s → 2s → 4s → ... → 60s max) prevents hammering
///   the MT5 server during outages while recovering quickly from transient failures.
/// - ConnectionManager integration: reads config at each loop iteration so the REST API
///   can update credentials. Watches a reconnect CancellationToken so config changes
///   interrupt the heartbeat loop immediately.
/// </summary>
public sealed class MT5Connection : BackgroundService
{
    private readonly ILogger<MT5Connection> _logger;
    private readonly IMT5Api _api;
    private readonly DealSink _dealSink;
    private readonly TickListener _tickListener;
    private readonly PipelineOrchestrator _orchestrator;
    private readonly ConnectionManager _connectionManager;

    private const int InitialBackoffMs = 1000;
    private const int MaxBackoffMs = 60000;
    private const int MaxReconnectAttempts = 10;
    private int _reconnectAttempts;

    /// <summary>
    /// Initializes a new MT5 connection manager.
    /// </summary>
    /// <param name="logger">Logger for connection lifecycle events.</param>
    /// <param name="api">MT5 API implementation (real or simulator).</param>
    /// <param name="dealSink">Deal sink for buffered/live deal forwarding.</param>
    /// <param name="tickListener">Tick listener for price cache and channel writes.</param>
    /// <param name="orchestrator">Pipeline orchestrator for three-phase startup.</param>
    /// <param name="connectionManager">Connection manager for runtime config updates.</param>
    public MT5Connection(
        ILogger<MT5Connection> logger,
        IMT5Api api,
        DealSink dealSink,
        TickListener tickListener,
        PipelineOrchestrator orchestrator,
        ConnectionManager connectionManager)
    {
        _logger = logger;
        _api = api;
        _dealSink = dealSink;
        _tickListener = tickListener;
        _orchestrator = orchestrator;
        _connectionManager = connectionManager;
    }

    /// <summary>
    /// Main execution loop: orchestrator startup → heartbeat → reconnect on failure.
    /// Uses exponential backoff with a 60-second cap for reconnection attempts.
    /// Reads config from ConnectionManager each iteration so runtime changes take effect.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoffMs = InitialBackoffMs;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Create a linked CTS so ConnectionManager can signal reconnect
            using var reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _connectionManager.RegisterReconnectToken(reconnectCts);

            // Read current config from ConnectionManager (may have been updated via REST API)
            var config = _connectionManager.CurrentConfig;

            // If disconnect was requested OR password is placeholder, wait for real credentials
            if (_connectionManager.DisconnectRequested ||
                config.Password == "CHANGE_ME" || string.IsNullOrWhiteSpace(config.Password))
            {
                if (config.Password == "CHANGE_ME" || string.IsNullOrWhiteSpace(config.Password))
                {
                    _logger.LogWarning("No real password configured — waiting for credentials via Settings page");
                }
                _connectionManager.SetDisconnected();
                _logger.LogInformation("Waiting for new connection config...");

                try
                {
                    await Task.Delay(Timeout.Infinite, reconnectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    _connectionManager.DisconnectRequested = false;
                    continue; // Config was updated, loop again
                }

                break; // Host shutdown
            }

            try
            {
                _logger.LogInformation(
                    "Starting pipeline for MT5 server {Server} as login {Login}...",
                    config.ServerAddress, config.ManagerLogin);

                // Wire deal callbacks → DealSink
                _api.OnDealAdd += OnDealAdd;
                _api.OnDealUpdate += OnDealUpdate;
                _api.OnTick += OnTickReceived;

                // Run three-phase startup: connect → backfill → go live
                // Note: we hook into state changes to report "connected" early
                var previousState = _orchestrator.State;
                _ = Task.Run(async () =>
                {
                    // Poll orchestrator state to detect when Phase 1 (Buffering) starts
                    // This means MT5 Connect succeeded — report to dashboard immediately
                    // Wait a bit for PUMP to fully initialize before querying logins
                    while (!reconnectCts.Token.IsCancellationRequested)
                    {
                        var currentState = _orchestrator.State;
                        if (currentState != previousState)
                        {
                            if (currentState == PipelineOrchestratorState.Buffering ||
                                currentState == PipelineOrchestratorState.Backfilling)
                            {
                                // Give pump 2s to fully sync user data
                                await Task.Delay(2000, reconnectCts.Token).ConfigureAwait(false);
                                var scopeLogins = _api.GetUserLogins(config.GroupMask);
                                _connectionManager.SetConnected(config.ServerAddress, scopeLogins.Length);
                                break;
                            }
                            if (currentState == PipelineOrchestratorState.Error)
                                break;
                            previousState = currentState;
                        }
                        await Task.Delay(100, reconnectCts.Token).ConfigureAwait(false);
                    }
                }, reconnectCts.Token);

                await _orchestrator.StartPipeline(config, reconnectCts.Token).ConfigureAwait(false);

                // Pipeline fully live — ensure connected status is set (background task may have missed it)
                if (!_connectionManager.IsConnected)
                {
                    var scopeLogins = _api.GetUserLogins(config.GroupMask);
                    _connectionManager.SetConnected(config.ServerAddress, scopeLogins.Length);
                }
                _connectionManager.SetLive(_tickListener.CachedSymbolCount);

                backoffMs = InitialBackoffMs; // Reset backoff on successful startup
                _reconnectAttempts = 0;     // Reset reconnect counter on success

                // Heartbeat loop — check connection health at configured interval
                while (!reconnectCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(config.HealthHeartbeatIntervalMs, reconnectCts.Token)
                        .ConfigureAwait(false);

                    if (!_api.IsConnected)
                    {
                        _logger.LogWarning("MT5 heartbeat detected disconnection");
                        _connectionManager.SetDisconnected("Heartbeat detected disconnection");
                        break;
                    }

                    _logger.LogDebug(
                        "MT5 heartbeat OK — {SymbolCount} symbols cached, pipeline={State}",
                        _tickListener.CachedSymbolCount, _orchestrator.State);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("MT5 connection shutting down gracefully");
                _connectionManager.SetDisconnected();
                break;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Reconnect signal from ConnectionManager — config was updated
                _logger.LogInformation("Reconnecting with updated configuration...");
                _connectionManager.SetDisconnected();
                backoffMs = InitialBackoffMs;
            }
            catch (Exception ex)
            {
                _reconnectAttempts++;
                _logger.LogError(ex, "MT5 connection failed (attempt {Attempt}/{Max}). Reconnecting in {BackoffMs}ms...",
                    _reconnectAttempts, MaxReconnectAttempts, backoffMs);
                _connectionManager.SetDisconnected(ex.Message);

                if (_reconnectAttempts >= MaxReconnectAttempts)
                {
                    _logger.LogCritical("MT5 reconnect exhausted after {Max} attempts — manual intervention required", MaxReconnectAttempts);
                    // Wait for manual config update via REST API
                    try
                    {
                        await Task.Delay(Timeout.Infinite, reconnectCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        _reconnectAttempts = 0;
                        backoffMs = InitialBackoffMs;
                        continue;
                    }
                    break;
                }

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
    private long _tickCount;
    private void OnTickReceived(RawTick raw)
    {
        var count = System.Threading.Interlocked.Increment(ref _tickCount);
        if (count <= 5 || count % 10000 == 0)
        {
            _logger.LogInformation("Tick received #{Count}: {Symbol} bid={Bid} ask={Ask}",
                count, raw.Symbol, raw.Bid, raw.Ask);
        }

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
