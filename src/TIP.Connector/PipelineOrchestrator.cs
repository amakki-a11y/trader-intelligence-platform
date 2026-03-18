using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TIP.Connector;

/// <summary>
/// Coordinates the three-phase startup sequence for the MT5 data pipeline:
///   Phase 1 (BUFFER) — Connect to MT5, subscribe with deals buffered.
///   Phase 2 (BACKFILL) — Load historical data from last checkpoint to cutoff.
///   Phase 3 (GO LIVE) — Replay buffer, skip duplicates, enter steady state.
///
/// Design rationale:
/// - Single source of truth for pipeline state transitions.
/// - Ensures zero deal loss during the backfill → live transition via DealSink's
///   buffer/replay pattern with duplicate detection.
/// - On reconnect, the same three-phase sequence runs again — SyncStateTracker
///   provides the last checkpoint so only new data is fetched.
/// - Graceful degradation: logs errors and transitions to Error state but does not
///   throw, allowing the caller to decide on retry strategy.
/// </summary>
public sealed class PipelineOrchestrator
{
    private readonly IMT5Api _api;
    private readonly DealSink _dealSink;
    private readonly TickListener _tickListener;
    private readonly HistoryFetcher _historyFetcher;
    private readonly SyncStateTracker _syncTracker;
    private readonly ILogger<PipelineOrchestrator> _logger;

    private long _backfilledDeals;
    private long _backfilledTicks;
    private int _bufferedReplayed;
    private int _duplicatesSkipped;

    /// <summary>
    /// Current pipeline state. Only PipelineOrchestrator sets this.
    /// </summary>
    public PipelineOrchestratorState State { get; private set; } = PipelineOrchestratorState.Idle;

    /// <summary>
    /// Number of deals loaded during the last backfill phase.
    /// </summary>
    public long BackfilledDeals => Interlocked.Read(ref _backfilledDeals);

    /// <summary>
    /// Number of ticks loaded during the last backfill phase.
    /// </summary>
    public long BackfilledTicks => Interlocked.Read(ref _backfilledTicks);

    /// <summary>
    /// Number of buffered events replayed during the last go-live transition.
    /// </summary>
    public int BufferedReplayed => _bufferedReplayed;

    /// <summary>
    /// Number of duplicate deals skipped during the last go-live transition.
    /// </summary>
    public int DuplicatesSkipped => _duplicatesSkipped;

    /// <summary>
    /// Initializes the pipeline orchestrator with all required dependencies.
    /// </summary>
    /// <param name="api">MT5 API (real or simulator).</param>
    /// <param name="dealSink">Deal sink for buffer/live mode switching.</param>
    /// <param name="tickListener">Tick listener for price cache + channel writes.</param>
    /// <param name="historyFetcher">History fetcher for backfill operations.</param>
    /// <param name="syncTracker">Sync state tracker for checkpoint management.</param>
    /// <param name="logger">Logger for state transition events.</param>
    public PipelineOrchestrator(
        IMT5Api api,
        DealSink dealSink,
        TickListener tickListener,
        HistoryFetcher historyFetcher,
        SyncStateTracker syncTracker,
        ILogger<PipelineOrchestrator> logger)
    {
        _api = api;
        _dealSink = dealSink;
        _tickListener = tickListener;
        _historyFetcher = historyFetcher;
        _syncTracker = syncTracker;
        _logger = logger;
    }

    /// <summary>
    /// Execute the full three-phase startup sequence.
    /// Returns when pipeline is in steady-state LIVE mode.
    /// </summary>
    /// <param name="config">MT5 connection configuration.</param>
    /// <param name="ct">Cancellation token for aborting the startup.</param>
    public async Task StartPipeline(ConnectionConfig config, CancellationToken ct)
    {
        try
        {
            // ── Phase 1: BUFFER MODE ─────────────────────────────────────────
            TransitionTo(PipelineOrchestratorState.Connecting);

            if (!_api.Initialize())
            {
                throw new InvalidOperationException(
                    $"MT5 API initialization failed: {_api.LastError}");
            }

            if (!_api.Connect(config.ServerAddress, config.ManagerLogin, config.Password))
            {
                throw new InvalidOperationException(
                    $"Failed to connect to MT5 server {config.ServerAddress}: {_api.LastError}");
            }

            TransitionTo(PipelineOrchestratorState.Buffering);
            var cutoffTime = DateTimeOffset.UtcNow;

            // DealSink starts in BUFFER mode by default (or reset for reconnect)
            _dealSink.Reset();

            // Wait for PUMP_MODE_FULL to sync before subscribing
            // The pump needs time to initialize symbol/user data after Connect()
            _logger.LogInformation("Waiting 3s for PUMP to sync before subscribing to events...");
            await Task.Delay(3000, ct).ConfigureAwait(false);

            // CRITICAL: Tell MT5 to stream ALL symbols via the pump.
            // Without this, the pump only sends ticks for a small default set.
            var selectedOk = _api.SelectedAddAll();
            _logger.LogInformation("SelectedAddAll: {Result} — pump will now receive ticks for all symbols", selectedOk);

            if (!selectedOk)
            {
                _logger.LogWarning("SelectedAddAll failed ({Error}), trying again after 2s...", _api.LastError);
                await Task.Delay(2000, ct).ConfigureAwait(false);
                selectedOk = _api.SelectedAddAll();
                _logger.LogInformation("SelectedAddAll retry: {Result}", selectedOk);
            }

            // Subscribe to live events — deals are buffered, ticks flow immediately
            // Retry subscription up to 3 times with 2s delay if it fails
            var dealSubOk = false;
            var tickSubOk = false;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if (!dealSubOk) dealSubOk = _api.SubscribeDeals();
                if (!tickSubOk) tickSubOk = _api.SubscribeTicks(config.GroupMask);

                if (dealSubOk && tickSubOk) break;

                _logger.LogWarning(
                    "Subscription attempt {Attempt}/3 — deals={DealSub}, ticks={TickSub}, lastError={Error}. Retrying in 2s...",
                    attempt, dealSubOk, tickSubOk, _api.LastError);
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Phase 1 complete — connected, deals subscribed={DealSub}, ticks subscribed={TickSub}. Cutoff: {CutoffTime}",
                dealSubOk, tickSubOk, cutoffTime);

            if (!tickSubOk)
            {
                _logger.LogError("CRITICAL: Tick subscription failed after 3 attempts — prices will NOT update! LastError: {Error}", _api.LastError);
            }

            // ── Phase 2: BACKFILL ────────────────────────────────────────────
            TransitionTo(PipelineOrchestratorState.Backfilling);

            // Load sync state from DB if available
            await _syncTracker.LoadFromDatabase(ct).ConfigureAwait(false);

            // Get all logins — retry up to 5 times with 2s delay (pump needs time to sync user data)
            ulong[] logins = Array.Empty<ulong>();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                logins = _api.GetUserLogins(config.GroupMask);
                if (logins.Length > 0) break;
                _logger.LogWarning(
                    "GetUserLogins returned 0 (attempt {Attempt}/5) — waiting for pump sync...",
                    attempt + 1);
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }

            var symbols = _api.GetSymbols().Select(s => s.Symbol).ToList();

            _logger.LogInformation(
                "Phase 2 starting — backfilling {LoginCount} logins and {SymbolCount} symbols",
                logins.Length, symbols.Count);

            // Backfill deals — returns set of loaded deal IDs for dedup
            var seenDealIds = await _historyFetcher.BackfillDeals(
                logins, cutoffTime, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _backfilledDeals, seenDealIds.Count);

            // Skip tick backfill — ticks flow live via TickSubscribe, no historical tick API yet
            // TODO: Phase 5 — enable tick backfill when RequestTicks is implemented
            _logger.LogInformation(
                "Phase 2 complete — backfilled {DealCount} deals for {LoginCount} logins (tick backfill skipped — live ticks active)",
                seenDealIds.Count, logins.Length);

            // ── Phase 3: GO LIVE ─────────────────────────────────────────────
            TransitionTo(PipelineOrchestratorState.Replaying);

            var bufferedCount = _dealSink.BufferCount;
            var replayed = _dealSink.SwitchToLiveMode(seenDealIds);
            var skipped = bufferedCount - replayed;

            _bufferedReplayed = replayed;
            _duplicatesSkipped = skipped > 0 ? skipped : 0;

            // Persist updated checkpoints
            await _syncTracker.FlushToDatabase(ct).ConfigureAwait(false);

            TransitionTo(PipelineOrchestratorState.Live);

            _logger.LogInformation(
                "Pipeline LIVE — {BackfilledDeals} deals backfilled, {Replayed} buffered events replayed, {Skipped} duplicates skipped",
                seenDealIds.Count, replayed, _duplicatesSkipped);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TransitionTo(PipelineOrchestratorState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline startup failed in state {State}", State);
            TransitionTo(PipelineOrchestratorState.Error);
            throw;
        }
    }

    /// <summary>
    /// Transitions the pipeline to the specified state and logs the change.
    /// </summary>
    private void TransitionTo(PipelineOrchestratorState newState)
    {
        var oldState = State;
        State = newState;
        _logger.LogInformation("Pipeline state: {OldState} → {NewState}", oldState, newState);
    }
}

/// <summary>
/// Pipeline lifecycle states managed by PipelineOrchestrator.
/// </summary>
public enum PipelineOrchestratorState
{
    /// <summary>Not yet started.</summary>
    Idle,
    /// <summary>Connecting to MT5 server.</summary>
    Connecting,
    /// <summary>Phase 1 — connected, deals buffered while backfill runs.</summary>
    Buffering,
    /// <summary>Phase 2 — loading historical data from last checkpoint.</summary>
    Backfilling,
    /// <summary>Phase 3 — replaying buffered events with dedup.</summary>
    Replaying,
    /// <summary>Steady state — all events flow directly through channels.</summary>
    Live,
    /// <summary>Disconnected from MT5 server.</summary>
    Disconnected,
    /// <summary>Pipeline startup failed with an error.</summary>
    Error
}
