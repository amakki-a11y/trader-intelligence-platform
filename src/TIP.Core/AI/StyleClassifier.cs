using System;
using TIP.Core.Models;

namespace TIP.Core.AI;

/// <summary>
/// Classifies trading accounts into behavioral style categories based on their metrics.
///
/// Classification logic:
/// - Scalper: AvgHoldSeconds &lt; 60 AND TradesPerHour &gt; 10 AND ScalpCount/TotalTrades &gt; 0.7
/// - DayTrader: AvgHoldSeconds between 60 and 28800 (8 hours), no overnight positions
/// - Swing: AvgHoldSeconds &gt; 28800, multi-day positions
/// - EA: ExpertTradeRatio &gt; 0.9 (90%+ trades placed by Expert Advisors)
/// - Manual: ExpertTradeRatio &lt; 0.1 (less than 10% EA trades)
/// - Mixed: Significant presence of both manual and EA trades
/// - Unknown: Insufficient data (TotalTrades &lt; 10)
///
/// Design rationale:
/// - Uses AccountAnalysis metrics only (no raw deal data) for O(1) classification.
/// - Priority order matters: EA/Manual style check takes precedence over time-based
///   classification because a scalping bot is categorized as EA, not Scalper.
/// - Thresholds are intentionally simple for v2.0; Phase 6 will add ML-based classification.
/// </summary>
public class StyleClassifier
{
    /// <summary>
    /// Classifies a trading account's style based on its analysis metrics.
    /// </summary>
    /// <param name="analysis">Account analysis containing behavioral metrics.</param>
    /// <returns>The classified trading style.</returns>
    public TradingStyle Classify(AccountAnalysis analysis)
    {
        // TODO: Phase 3, Task 12 — Implement full classification logic using the thresholds
        //       described in the XML doc comment above.

        // Insufficient data guard
        if (analysis.TotalTrades < 10)
        {
            return TradingStyle.Unknown;
        }

        // Placeholder — full implementation in Phase 3
        return TradingStyle.Unknown;
    }
}
