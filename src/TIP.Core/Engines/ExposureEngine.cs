using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace TIP.Core.Engines;

/// <summary>
/// Net exposure calculation engine for risk monitoring.
///
/// Design rationale:
/// - Recalculates from P&amp;L results (not raw positions) since PnLEngine already
///   maintains the latest position state with current prices.
/// - Thread-safe via ConcurrentDictionary for exposure snapshots.
/// - Performance target: full recalculation in under 100ms.
/// </summary>
public sealed class ExposureEngine
{
    private readonly ILogger<ExposureEngine> _logger;

    /// <summary>Cached exposure snapshots by symbol.</summary>
    private readonly ConcurrentDictionary<string, SymbolExposure> _bySymbol = new();

    /// <summary>
    /// Initializes the exposure engine.
    /// </summary>
    /// <param name="logger">Logger for exposure events.</param>
    public ExposureEngine(ILogger<ExposureEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Recalculate all exposures from current P&amp;L results.
    /// Called after position changes or periodically.
    /// </summary>
    /// <param name="positions">Current P&amp;L results keyed by position ID.</param>
    public void Recalculate(IReadOnlyDictionary<long, PnLResult> positions)
    {
        _bySymbol.Clear();

        var groups = positions.Values.GroupBy(p => p.Symbol);

        foreach (var group in groups)
        {
            var symbol = group.Key;
            var longVol = 0.0;
            var shortVol = 0.0;
            var longCount = 0;
            var shortCount = 0;
            var pnl = 0.0;

            foreach (var pos in group)
            {
                if (pos.Direction == 0) // BUY
                {
                    longVol += pos.Volume;
                    longCount++;
                }
                else // SELL
                {
                    shortVol += pos.Volume;
                    shortCount++;
                }
                pnl += pos.UnrealizedPnL;
            }

            _bySymbol[symbol] = new SymbolExposure
            {
                Symbol = symbol,
                LongVolume = Math.Round(longVol, 2),
                ShortVolume = Math.Round(shortVol, 2),
                NetVolume = Math.Round(longVol - shortVol, 2),
                LongPositionCount = longCount,
                ShortPositionCount = shortCount,
                UnrealizedPnL = Math.Round(pnl, 2),
                FlaggedAccountCount = 0
            };
        }
    }

    /// <summary>
    /// Get net exposure for a single symbol.
    /// </summary>
    /// <param name="symbol">Trading symbol.</param>
    /// <returns>Symbol exposure, or null if no positions.</returns>
    public SymbolExposure? GetSymbolExposure(string symbol)
    {
        return _bySymbol.TryGetValue(symbol, out var exposure) ? exposure : null;
    }

    /// <summary>
    /// Get all symbol exposures sorted by absolute net volume.
    /// </summary>
    /// <returns>Sorted list of symbol exposures.</returns>
    public IReadOnlyList<SymbolExposure> GetAllSymbolExposures()
    {
        return _bySymbol.Values
            .OrderByDescending(e => Math.Abs(e.NetVolume))
            .ToList();
    }

    /// <summary>
    /// Get total long and short volume across all symbols.
    /// </summary>
    /// <returns>Aggregate exposure totals.</returns>
    public (double TotalLong, double TotalShort, double NetExposure) GetTotalExposure()
    {
        var totalLong = _bySymbol.Values.Sum(e => e.LongVolume);
        var totalShort = _bySymbol.Values.Sum(e => e.ShortVolume);
        return (Math.Round(totalLong, 2), Math.Round(totalShort, 2), Math.Round(totalLong - totalShort, 2));
    }

    /// <summary>
    /// Gets the number of symbols with exposure.
    /// </summary>
    public int SymbolCount => _bySymbol.Count;
}

/// <summary>
/// Exposure summary for a single trading symbol.
/// </summary>
public sealed record SymbolExposure
{
    /// <summary>Trading symbol.</summary>
    public required string Symbol { get; init; }

    /// <summary>Total BUY volume in lots.</summary>
    public required double LongVolume { get; init; }

    /// <summary>Total SELL volume in lots.</summary>
    public required double ShortVolume { get; init; }

    /// <summary>Net volume (Long - Short).</summary>
    public required double NetVolume { get; init; }

    /// <summary>Number of long positions.</summary>
    public required int LongPositionCount { get; init; }

    /// <summary>Number of short positions.</summary>
    public required int ShortPositionCount { get; init; }

    /// <summary>Aggregate unrealized P&amp;L for this symbol.</summary>
    public required double UnrealizedPnL { get; init; }

    /// <summary>Number of accounts with abuse score &gt;= 50.</summary>
    public required int FlaggedAccountCount { get; init; }
}
