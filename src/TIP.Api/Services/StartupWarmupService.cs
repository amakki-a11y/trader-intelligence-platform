using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TIP.Connector;
using TIP.Core.Engines;

namespace TIP.Api.Services;

/// <summary>
/// Ensures SymbolCache and PnLEngine positions are populated before the app
/// begins serving requests. Registered as IHostedService so it runs during startup,
/// blocking until caches are fully loaded.
/// </summary>
public sealed class StartupWarmupService : IHostedService
{
    private readonly SymbolCache _symbolCache;
    private readonly IMT5Api _mt5Api;
    private readonly PnLEngine _pnlEngine;
    private readonly PipelineOrchestrator _orchestrator;
    private readonly ConnectionManager _connectionManager;
    private readonly ILogger<StartupWarmupService> _logger;

    /// <summary>
    /// Initializes the startup warmup service with all dependencies needed
    /// for symbol cache and position loading.
    /// </summary>
    public StartupWarmupService(
        SymbolCache symbolCache,
        IMT5Api mt5Api,
        PnLEngine pnlEngine,
        PipelineOrchestrator orchestrator,
        ConnectionManager connectionManager,
        ILogger<StartupWarmupService> logger)
    {
        _symbolCache = symbolCache;
        _mt5Api = mt5Api;
        _pnlEngine = pnlEngine;
        _orchestrator = orchestrator;
        _connectionManager = connectionManager;
        _logger = logger;
    }

    /// <summary>
    /// Waits for MT5 connection, then loads symbol cache and open positions.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("StartupWarmupService: waiting for MT5 connection...");

        // Wait for pipeline to reach at least Buffering (MT5 connected)
        while (_orchestrator.State < PipelineOrchestratorState.Buffering)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        // Load symbol cache
        try
        {
            var symbols = _mt5Api.GetSymbols();
            _symbolCache.Load(symbols);
            _logger.LogInformation("StartupWarmupService: symbol cache ready ({Count} symbols)",
                _symbolCache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StartupWarmupService: failed to load symbols from MT5");
        }

        // Load open positions from MT5 into PnLEngine
        try
        {
            var groupMask = _connectionManager.CurrentConfig.GroupMask;
            var logins = _mt5Api.GetUserLogins(groupMask);
            var allPositions = new List<OpenPosition>();

            foreach (var login in logins)
            {
                try
                {
                    var rawPositions = _mt5Api.GetPositions(login);
                    foreach (var rp in rawPositions)
                    {
                        allPositions.Add(new OpenPosition
                        {
                            PositionId = (long)rp.PositionId,
                            Login = rp.Login,
                            Symbol = rp.Symbol,
                            Direction = (int)rp.Action,
                            Volume = rp.Volume,
                            OpenPrice = rp.PriceOpen,
                        });
                    }
                }
                catch { /* skip logins without positions */ }
            }

            if (allPositions.Count > 0)
            {
                _pnlEngine.Initialize(allPositions);
                _logger.LogInformation("StartupWarmupService: loaded {Count} open positions from {Logins} logins",
                    allPositions.Count, logins.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StartupWarmupService: failed to load positions from MT5");
        }
    }

    /// <summary>No-op on stop.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
