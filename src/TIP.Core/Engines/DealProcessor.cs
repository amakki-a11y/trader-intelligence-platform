using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TIP.Core.Engines;

/// <summary>
/// Classifies deal events by type and determines their effect on position state.
///
/// Design rationale:
/// - Pure logic class with no I/O dependencies — fully testable without mocks.
/// - Maintains an in-memory position index (PositionId → volume) so it can determine
///   whether a trade deal opens, closes, or modifies a position.
/// - Thread-safe via ConcurrentDictionary for the position index.
/// - Called for every deal — both historical (backfill) and live — so it must be fast.
/// </summary>
public sealed class DealProcessor
{
    private readonly ConcurrentDictionary<ulong, PositionState> _openPositions = new();
    private readonly ConcurrentDictionary<ulong, object> _positionLocks = new();
    private readonly ConcurrentDictionary<ulong, long> _lastDealTimestamps = new();
    private readonly ILogger<DealProcessor>? _logger;

    /// <summary>
    /// Initializes a DealProcessor with optional logging.
    /// </summary>
    /// <param name="logger">Logger for validation warnings (optional for backward compat).</param>
    public DealProcessor(ILogger<DealProcessor>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Process a single deal event. Updates position state and classifies the deal.
    /// Called for every deal — both historical (backfill) and live.
    /// Validates input fields; returns Invalid result for bad data instead of throwing.
    /// </summary>
    /// <param name="dealId">MT5 deal ticket number.</param>
    /// <param name="action">Deal action code (0=BUY, 1=SELL, 2=BALANCE, etc.).</param>
    /// <param name="volume">Deal volume in lots.</param>
    /// <param name="positionId">Position ticket this deal belongs to.</param>
    /// <param name="login">Account login (0 = invalid).</param>
    /// <param name="symbol">Trading symbol (null/empty = invalid for trade deals).</param>
    /// <param name="timeMsc">Deal time in milliseconds since epoch (0 = invalid).</param>
    /// <param name="entry">Deal entry type: 0=IN, 1=OUT, 2=INOUT, 3=OUT_BY. Default -1 (unknown).</param>
    /// <returns>Classification result with deal type and position effect.</returns>
    public DealProcessingResult ProcessDeal(ulong dealId, int action, double volume, ulong positionId,
        ulong login, string? symbol, long timeMsc, int entry = -1)
    {
        // Validate required fields
        if (login == 0)
        {
            _logger?.LogWarning("Invalid deal {DealId}: login is zero", dealId);
            return new DealProcessingResult
            {
                DealId = dealId,
                Type = DealType.Unknown,
                PositionEffect = PositionAction.Invalid,
                AffectedPositionId = null
            };
        }

        if (string.IsNullOrEmpty(symbol) && (action == 0 || action == 1))
        {
            _logger?.LogWarning("Invalid deal {DealId}: symbol is null/empty for trade action {Action}", dealId, action);
            return new DealProcessingResult
            {
                DealId = dealId,
                Type = DealType.Unknown,
                PositionEffect = PositionAction.Invalid,
                AffectedPositionId = null
            };
        }

        if (timeMsc <= 0)
        {
            _logger?.LogWarning("Invalid deal {DealId}: timeMsc is {TimeMsc} (epoch or before)", dealId, timeMsc);
            return new DealProcessingResult
            {
                DealId = dealId,
                Type = DealType.Unknown,
                PositionEffect = PositionAction.Invalid,
                AffectedPositionId = null
            };
        }

        var dealType = ClassifyAction(action);

        if (dealType == DealType.Buy || dealType == DealType.Sell)
        {
            // Close deals (OUT=1, OUT_BY=3) MUST always be processed — you can't "re-close" a position,
            // and rejecting them causes ghost positions after liquidation/stop-out.
            // Only apply out-of-order protection to opening deals (IN=0).
            var isCloseDeal = entry == 1 || entry == 3;

            if (!isCloseDeal && _lastDealTimestamps.TryGetValue(positionId, out var lastMsc) && timeMsc < lastMsc)
            {
                _logger?.LogWarning("Out-of-order deal {DealId} for position {PositionId}: " +
                    "timeMsc {TimeMsc} < last {LastMsc}. Skipping.", dealId, positionId, timeMsc, lastMsc);
                return new DealProcessingResult
                {
                    DealId = dealId,
                    Type = dealType,
                    PositionEffect = PositionAction.Invalid,
                    AffectedPositionId = positionId
                };
            }
            _lastDealTimestamps[positionId] = Math.Max(timeMsc,
                _lastDealTimestamps.TryGetValue(positionId, out var existing) ? existing : 0);

            return ProcessTradeDeal(dealId, dealType, volume, positionId);
        }

        return new DealProcessingResult
        {
            DealId = dealId,
            Type = dealType,
            PositionEffect = PositionAction.None,
            AffectedPositionId = null
        };
    }

    /// <summary>
    /// Resets all internal state (open positions, timestamps, locks).
    /// Called before warmup replay to ensure clean processing.
    /// </summary>
    public void Reset()
    {
        _openPositions.Clear();
        _positionLocks.Clear();
        _lastDealTimestamps.Clear();
        _logger?.LogInformation("DealProcessor reset — cleared all position tracking state");
    }

    /// <summary>
    /// Gets the current number of tracked open positions.
    /// </summary>
    public int OpenPositionCount => _openPositions.Count;

    /// <summary>
    /// Checks whether a position is currently tracked as open.
    /// </summary>
    /// <param name="positionId">Position ticket to check.</param>
    /// <returns>True if the position is open.</returns>
    public bool IsPositionOpen(ulong positionId)
    {
        return _openPositions.ContainsKey(positionId);
    }

    /// <summary>
    /// Processes a BUY or SELL trade deal and determines position effect.
    /// </summary>
    private DealProcessingResult ProcessTradeDeal(ulong dealId, DealType dealType, double volume, ulong positionId)
    {
        var posLock = _positionLocks.GetOrAdd(positionId, _ => new object());
        lock (posLock)
        {
            if (_openPositions.TryGetValue(positionId, out var existing))
            {
                // Position exists — this is a close or partial close
                var remainingVolume = existing.Volume - volume;

                if (remainingVolume <= 0.0001) // Effectively zero (floating point tolerance)
                {
                    _openPositions.TryRemove(positionId, out _);
                    _positionLocks.TryRemove(positionId, out _);
                    return new DealProcessingResult
                    {
                        DealId = dealId,
                        Type = dealType,
                        PositionEffect = PositionAction.Closed,
                        AffectedPositionId = positionId
                    };
                }
                else
                {
                    _openPositions[positionId] = existing with { Volume = remainingVolume };
                    return new DealProcessingResult
                    {
                        DealId = dealId,
                        Type = dealType,
                        PositionEffect = PositionAction.Modified,
                        AffectedPositionId = positionId,
                        RemainingVolume = remainingVolume
                    };
                }
            }
            else
            {
                // New position
                _openPositions[positionId] = new PositionState(positionId, volume);
                return new DealProcessingResult
                {
                    DealId = dealId,
                    Type = dealType,
                    PositionEffect = PositionAction.Opened,
                    AffectedPositionId = positionId
                };
            }
        }
    }

    /// <summary>
    /// Maps MT5 deal action codes to DealType enum values.
    /// </summary>
    private static DealType ClassifyAction(int action)
    {
        return action switch
        {
            0 => DealType.Buy,
            1 => DealType.Sell,
            2 => DealType.Balance,
            3 => DealType.Credit,
            4 => DealType.Charge,
            5 => DealType.Correction,
            6 => DealType.Bonus,
            7 => DealType.Commission,
            8 => DealType.CommissionDaily,
            9 => DealType.CommissionMonthly,
            10 => DealType.AgentDaily,
            11 => DealType.AgentMonthly,
            18 => DealType.Agent,
            19 => DealType.SOCompensation,
            _ => DealType.Unknown
        };
    }
}

/// <summary>
/// Minimal tracked state for an open position.
/// </summary>
/// <param name="PositionId">Position ticket.</param>
/// <param name="Volume">Remaining open volume in lots.</param>
internal sealed record PositionState(ulong PositionId, double Volume);

/// <summary>
/// Result of processing a single deal through the DealProcessor.
/// </summary>
public sealed record DealProcessingResult
{
    /// <summary>The deal ticket that was processed.</summary>
    public required ulong DealId { get; init; }

    /// <summary>Classified deal type (Buy, Sell, Balance, Bonus, etc.).</summary>
    public DealType Type { get; init; }

    /// <summary>What effect this deal had on position state, or null for non-trade deals.</summary>
    public PositionAction? PositionEffect { get; init; }

    /// <summary>The position ticket affected, or null for non-trade deals.</summary>
    public ulong? AffectedPositionId { get; init; }

    /// <summary>Remaining volume after partial close, or null if not a partial close.</summary>
    public double? RemainingVolume { get; init; }
}

/// <summary>
/// MT5 deal action types. Maps to the Action field in CIMTDeal.
/// </summary>
public enum DealType
{
    /// <summary>Action 0 — Buy trade.</summary>
    Buy,
    /// <summary>Action 1 — Sell trade.</summary>
    Sell,
    /// <summary>Action 2 — Balance operation (deposit/withdrawal).</summary>
    Balance,
    /// <summary>Action 3 — Credit operation.</summary>
    Credit,
    /// <summary>Action 4 — Additional charge.</summary>
    Charge,
    /// <summary>Action 5 — Correction.</summary>
    Correction,
    /// <summary>Action 6 — Bonus credit.</summary>
    Bonus,
    /// <summary>Action 7 — Commission charge.</summary>
    Commission,
    /// <summary>Action 8 — Daily commission.</summary>
    CommissionDaily,
    /// <summary>Action 9 — Monthly commission.</summary>
    CommissionMonthly,
    /// <summary>Action 10 — Daily agent commission.</summary>
    AgentDaily,
    /// <summary>Action 11 — Monthly agent commission.</summary>
    AgentMonthly,
    /// <summary>Action 18 — Agent commission.</summary>
    Agent,
    /// <summary>Action 19 — Stop-out compensation.</summary>
    SOCompensation,
    /// <summary>Unrecognized action code.</summary>
    Unknown
}

/// <summary>
/// The effect a deal has on position state.
/// </summary>
public enum PositionAction
{
    /// <summary>Deal opens a new position (entry).</summary>
    Opened,
    /// <summary>Deal closes an existing position (exit).</summary>
    Closed,
    /// <summary>Deal partially closes an existing position.</summary>
    Modified,
    /// <summary>Non-trade deal — no position effect.</summary>
    None,
    /// <summary>Deal had invalid input data and was rejected.</summary>
    Invalid
}
