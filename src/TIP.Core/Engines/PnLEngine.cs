using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace TIP.Core.Engines;

/// <summary>
/// Lightweight position data for P&amp;L calculation. Defined in TIP.Core to avoid
/// a dependency on TIP.Data. Program.cs maps PositionRecord → OpenPosition.
/// </summary>
public sealed class OpenPosition
{
    /// <summary>Position ticket number.</summary>
    public long PositionId { get; set; }

    /// <summary>Account login that owns this position.</summary>
    public ulong Login { get; set; }

    /// <summary>Trading instrument symbol.</summary>
    public string Symbol { get; set; } = "";

    /// <summary>Position direction: 0=BUY, 1=SELL.</summary>
    public int Direction { get; set; }

    /// <summary>Open volume in lots.</summary>
    public double Volume { get; set; }

    /// <summary>Position open price.</summary>
    public double OpenPrice { get; set; }

    /// <summary>Accumulated swap/rollover charge.</summary>
    public double Swap { get; set; }
}

/// <summary>
/// Real-time unrealized P&amp;L calculation engine.
///
/// Design rationale:
/// - Maintains an in-memory position cache indexed by symbol for fast tick-driven updates.
/// - On each tick, recalculates P&amp;L for ALL positions on that symbol using the latest bid/ask.
/// - Thread-safe via ConcurrentDictionary for position and result caches.
/// - Performance target: &lt;10ms per tick recalculation.
///
/// P&amp;L formula:
///   BUY:  (currentBid - openPrice) * volume * contractSize
///   SELL: (openPrice - currentAsk) * volume * contractSize
/// </summary>
public sealed class PnLEngine
{
    private readonly ILogger<PnLEngine> _logger;

    /// <summary>Positions indexed by symbol for fast tick-driven lookup.</summary>
    private readonly ConcurrentDictionary<string, List<OpenPosition>> _positionsBySymbol = new();

    /// <summary>Latest P&amp;L result per position.</summary>
    private readonly ConcurrentDictionary<long, PnLResult> _pnlResults = new();

    /// <summary>Lock for modifying position lists (add/remove).</summary>
    private readonly object _positionLock = new();

    /// <summary>
    /// Initializes the P&amp;L engine.
    /// </summary>
    /// <param name="logger">Logger for P&amp;L calculation events.</param>
    public PnLEngine(ILogger<PnLEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Load all open positions into the in-memory cache.
    /// Called once at startup after positions are loaded from DB.
    /// </summary>
    /// <param name="positions">All currently open positions.</param>
    public void Initialize(IReadOnlyList<OpenPosition> positions)
    {
        lock (_positionLock)
        {
            _positionsBySymbol.Clear();
            _pnlResults.Clear();

            foreach (var pos in positions)
            {
                if (!_positionsBySymbol.TryGetValue(pos.Symbol, out var list))
                {
                    list = new List<OpenPosition>();
                    _positionsBySymbol[pos.Symbol] = list;
                }
                list.Add(pos);
            }
        }

        _logger.LogInformation("PnLEngine initialized with {Count} positions across {Symbols} symbols",
            positions.Count, _positionsBySymbol.Count);
    }

    /// <summary>
    /// Called on every tick. Recalculates unrealized P&amp;L for all positions on this symbol.
    /// </summary>
    /// <param name="symbol">Symbol that ticked.</param>
    /// <param name="bid">Current bid price.</param>
    /// <param name="ask">Current ask price.</param>
    public void OnTick(string symbol, double bid, double ask)
    {
        if (!_positionsBySymbol.TryGetValue(symbol, out var positions))
            return;

        var contractSize = GetContractSize(symbol);
        var now = DateTimeOffset.UtcNow;

        lock (_positionLock)
        {
            foreach (var pos in positions)
            {
                var currentPrice = pos.Direction == 0 ? bid : ask;
                var pnl = pos.Direction == 0
                    ? (bid - pos.OpenPrice) * pos.Volume * contractSize
                    : (pos.OpenPrice - ask) * pos.Volume * contractSize;

                _pnlResults[pos.PositionId] = new PnLResult
                {
                    PositionId = pos.PositionId,
                    Login = pos.Login,
                    Symbol = pos.Symbol,
                    Direction = pos.Direction,
                    Volume = pos.Volume,
                    OpenPrice = pos.OpenPrice,
                    CurrentPrice = currentPrice,
                    UnrealizedPnL = Math.Round(pnl, 2),
                    Swap = pos.Swap,
                    CalculatedAt = now
                };
            }
        }
    }

    /// <summary>
    /// Update position cache when a deal opens a new position.
    /// </summary>
    /// <param name="position">The newly opened position.</param>
    public void OnPositionOpened(OpenPosition position)
    {
        lock (_positionLock)
        {
            if (!_positionsBySymbol.TryGetValue(position.Symbol, out var list))
            {
                list = new List<OpenPosition>();
                _positionsBySymbol[position.Symbol] = list;
            }
            list.Add(position);
        }

        _logger.LogDebug("PnL tracking position {PositionId} on {Symbol}", position.PositionId, position.Symbol);
    }

    /// <summary>
    /// Remove a position from the cache when it is closed.
    /// </summary>
    /// <param name="positionId">Position ticket to remove.</param>
    /// <param name="symbol">Symbol the position was on.</param>
    public void OnPositionClosed(long positionId, string symbol)
    {
        lock (_positionLock)
        {
            if (_positionsBySymbol.TryGetValue(symbol, out var list))
            {
                list.RemoveAll(p => p.PositionId == positionId);
                if (list.Count == 0)
                    _positionsBySymbol.TryRemove(symbol, out _);
            }
        }

        _pnlResults.TryRemove(positionId, out _);
        _logger.LogDebug("PnL stopped tracking position {PositionId}", positionId);
    }

    /// <summary>Get current P&amp;L for a single position.</summary>
    public PnLResult? GetPositionPnL(long positionId)
    {
        return _pnlResults.TryGetValue(positionId, out var result) ? result : null;
    }

    /// <summary>Get all current P&amp;L results.</summary>
    public IReadOnlyDictionary<long, PnLResult> GetAllPnL()
    {
        return _pnlResults.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>Get aggregated unrealized P&amp;L for a single login.</summary>
    public double GetUnrealizedPnLByLogin(ulong login)
    {
        return _pnlResults.Values.Where(r => r.Login == login).Sum(r => r.UnrealizedPnL);
    }

    /// <summary>Get aggregated unrealized P&amp;L for a symbol.</summary>
    public double GetUnrealizedPnLBySymbol(string symbol)
    {
        return _pnlResults.Values.Where(r => r.Symbol == symbol).Sum(r => r.UnrealizedPnL);
    }

    /// <summary>Gets the number of tracked positions.</summary>
    public int TrackedPositionCount => _pnlResults.Count;

    /// <summary>Gets the total unrealized P&amp;L across all positions.</summary>
    public double TotalUnrealizedPnL => _pnlResults.Values.Sum(r => r.UnrealizedPnL);

    /// <summary>
    /// Simplified contract size lookup. Full symbol specs will be wired in Phase 6.
    /// </summary>
    private static double GetContractSize(string symbol) => symbol switch
    {
        var s when s.Contains("XAU") => 100,
        var s when s.Contains("XAG") => 5000,
        var s when s.Contains("BTC") => 1,
        var s when s.Contains("ETH") => 1,
        _ => 100000
    };
}

/// <summary>
/// Result of a P&amp;L calculation for a single position.
/// </summary>
public sealed record PnLResult
{
    /// <summary>Position ticket number.</summary>
    public required long PositionId { get; init; }

    /// <summary>Account login that owns this position.</summary>
    public required ulong Login { get; init; }

    /// <summary>Trading instrument symbol.</summary>
    public required string Symbol { get; init; }

    /// <summary>Position direction: 0=BUY, 1=SELL.</summary>
    public required int Direction { get; init; }

    /// <summary>Open volume in lots.</summary>
    public required double Volume { get; init; }

    /// <summary>Position open price.</summary>
    public required double OpenPrice { get; init; }

    /// <summary>Current market price used for calculation.</summary>
    public required double CurrentPrice { get; init; }

    /// <summary>Unrealized profit/loss in deposit currency.</summary>
    public required double UnrealizedPnL { get; init; }

    /// <summary>Accumulated swap/rollover charge.</summary>
    public required double Swap { get; init; }

    /// <summary>When this P&amp;L was last calculated.</summary>
    public required DateTimeOffset CalculatedAt { get; init; }
}
