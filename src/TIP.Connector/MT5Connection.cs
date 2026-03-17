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
/// - Writes deal and tick events into Channel&lt;T&gt; for downstream consumers.
/// - Exponential backoff (1s → 2s → 4s → ... → 60s max) prevents hammering
///   the MT5 server during outages while recovering quickly from transient failures.
/// - Backoff resets on successful connection to ensure fast reconnect after brief blips.
/// </summary>
public sealed class MT5Connection : BackgroundService
{
    private readonly ILogger<MT5Connection> _logger;
    private readonly ChannelWriter<DealEvent> _dealWriter;
    private readonly ChannelWriter<TickEvent> _tickWriter;
    private readonly ConnectionConfig _config;

    private const int InitialBackoffMs = 1000;
    private const int MaxBackoffMs = 60000;

    /// <summary>
    /// Initializes a new MT5 connection manager.
    /// </summary>
    /// <param name="logger">Logger for connection lifecycle events.</param>
    /// <param name="dealWriter">Channel writer for deal events.</param>
    /// <param name="tickWriter">Channel writer for tick events.</param>
    /// <param name="config">MT5 server connection configuration.</param>
    public MT5Connection(
        ILogger<MT5Connection> logger,
        ChannelWriter<DealEvent> dealWriter,
        ChannelWriter<TickEvent> tickWriter,
        ConnectionConfig config)
    {
        _logger = logger;
        _dealWriter = dealWriter;
        _tickWriter = tickWriter;
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

                // TODO: Phase 2, Task 3 — Create CIMTManagerAPI instance
                // TODO: Phase 2, Task 3 — Call IMTManagerAPI.Connect(_config.ServerAddress, _config.ManagerLogin, _config.Password)
                // TODO: Phase 2, Task 3 — Subscribe DealSink via IMTManagerAPI.DealSubscribe
                // TODO: Phase 2, Task 3 — Subscribe TickListener via IMTManagerAPI.TickSubscribe(_config.GroupMask)

                _logger.LogInformation("Connected to MT5 server {Server}", _config.ServerAddress);
                backoffMs = InitialBackoffMs; // Reset backoff on successful connection

                // Heartbeat loop — check connection health at configured interval
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(_config.HealthHeartbeatIntervalMs, stoppingToken).ConfigureAwait(false);

                    // TODO: Phase 2, Task 3 — Check IMTManagerAPI.IsConnected()
                    // TODO: Phase 2, Task 3 — If disconnected, break to trigger reconnect
                    _logger.LogDebug("MT5 heartbeat OK");
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
                // TODO: Phase 2, Task 3 — Disconnect and release CIMTManagerAPI instance
            }
        }

        _logger.LogInformation("MT5 connection service stopped");
    }
}
