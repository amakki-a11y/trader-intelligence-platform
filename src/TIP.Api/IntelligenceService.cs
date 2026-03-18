using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TIP.Connector;
using TIP.Core.Engines;
using TIP.Core.Models;
using TIP.Core.Resilience;
using TIP.Data;

namespace TIP.Api;

/// <summary>
/// Background service that periodically runs intelligence analysis cycles:
/// - Classifies trading styles for all scored accounts.
/// - Computes book routing recommendations.
/// - Upserts trader profiles to the database.
/// - Saves score history snapshots.
///
/// Design rationale:
/// - Runs every 5 minutes (configurable) to keep intelligence data fresh.
/// - Reads from in-memory AccountScorer (no DB on read path).
/// - Writes to TraderProfileRepository + score_history for persistence.
/// - Waits for PipelineOrchestrator to reach LIVE state before starting.
/// - Thread-safe: each cycle processes a snapshot of current accounts.
/// </summary>
public sealed class IntelligenceService : BackgroundService
{
    private readonly ILogger<IntelligenceService> _logger;
    private readonly AccountScorer _accountScorer;
    private readonly StyleClassifier _styleClassifier;
    private readonly BookRouter _bookRouter;
    private readonly PipelineOrchestrator _orchestrator;
    private readonly TraderProfileRepository _profileRepository;
    private readonly ServiceHealthTracker _healthTracker;
    private readonly bool _dbEnabled;
    private int _cycleCount;

    /// <summary>
    /// Initializes the intelligence background service.
    /// </summary>
    public IntelligenceService(
        ILogger<IntelligenceService> logger,
        AccountScorer accountScorer,
        StyleClassifier styleClassifier,
        BookRouter bookRouter,
        PipelineOrchestrator orchestrator,
        TraderProfileRepository profileRepository,
        ServiceHealthTracker healthTracker,
        bool dbEnabled)
    {
        _logger = logger;
        _accountScorer = accountScorer;
        _styleClassifier = styleClassifier;
        _bookRouter = bookRouter;
        _orchestrator = orchestrator;
        _profileRepository = profileRepository;
        _healthTracker = healthTracker;
        _dbEnabled = dbEnabled;
    }

    /// <summary>Total intelligence cycles completed since startup.</summary>
    public int CycleCount => _cycleCount;

    /// <summary>
    /// Main loop: waits for pipeline, then runs intelligence cycles every 5 minutes.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IntelligenceService started — waiting for pipeline");

        // Wait for pipeline to reach at least LIVE state
        while (_orchestrator.State < PipelineOrchestratorState.Live && !stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(2000, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        _logger.LogInformation("IntelligenceService active — running intelligence cycles every 5 minutes");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycle().ConfigureAwait(false);
                Interlocked.Increment(ref _cycleCount);
                _healthTracker.RecordSuccess("intelligence");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var errors = _healthTracker.RecordError("intelligence");
                _logger.LogError(ex, "IntelligenceService cycle failed — continuing");
                if (errors >= 50)
                {
                    _logger.LogCritical("IntelligenceService failing repeatedly — {Errors} consecutive errors — possible systemic issue", errors);
                }
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation("IntelligenceService stopped — {CycleCount} cycles completed", _cycleCount);
    }

    /// <summary>
    /// Runs a single intelligence cycle: classify, route, and persist for all accounts.
    /// </summary>
    private async Task RunCycle()
    {
        var accounts = _accountScorer.GetAllAccountsSorted();
        if (accounts.Count == 0)
        {
            _logger.LogDebug("Intelligence cycle skipped — no scored accounts");
            return;
        }

        _logger.LogInformation("Intelligence cycle starting — {AccountCount} accounts to process", accounts.Count);

        var profileCount = 0;
        var aBookCount = 0;
        var bBookCount = 0;
        var hybridCount = 0;

        foreach (var account in accounts)
        {
            var style = _styleClassifier.Classify(account);
            var book = _bookRouter.Route(account, style);

            profileCount++;

            switch (book.Recommendation)
            {
                case BookRouting.ABook: aBookCount++; break;
                case BookRouting.BBook: bBookCount++; break;
                case BookRouting.Hybrid: hybridCount++; break;
            }

            // Persist to DB if enabled
            if (_dbEnabled)
            {
                try
                {
                    var profile = new TraderProfile
                    {
                        Login = account.Login,
                        Name = account.Name,
                        Group = account.Group,
                        Server = account.Server,
                        Style = style.Style,
                        Routing = book.Recommendation,
                        RoutingConfidence = book.Confidence,
                        LastUpdated = DateTimeOffset.UtcNow
                    };

                    await _profileRepository.UpsertAsync(profile).ConfigureAwait(false);
                    _logger.LogDebug("Upserted profile for login {Login}: style={Style}, book={Book}",
                        account.Login, style.Style, book.Recommendation);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to upsert trader profile for login {Login}", account.Login);
                }

                try
                {
                    await _profileRepository.InsertScoreHistoryAsync(
                        account.Login, account.AbuseScore, account.RiskLevel.ToString(),
                        account.IsRingMember, account.Server).ConfigureAwait(false);
                    _logger.LogDebug("Saved score history for login {Login}: score={Score}",
                        account.Login, account.AbuseScore);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save score history for login {Login}", account.Login);
                }
            }
        }

        _logger.LogInformation(
            "Intelligence cycle complete — {ProfileCount} profiles: A-Book={ABook}, B-Book={BBook}, Hybrid={Hybrid}",
            profileCount, aBookCount, bBookCount, hybridCount);
    }
}
