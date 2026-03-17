using System;
using TIP.Core.Models;

namespace TIP.Core.AI;

/// <summary>
/// Suggestion result from the BookRouter containing routing recommendation and reasoning.
/// </summary>
/// <param name="Routing">Suggested book routing (ABook, BBook, or Hybrid).</param>
/// <param name="Confidence">Confidence level (0.0-1.0) of the suggestion.</param>
/// <param name="Reasoning">Human-readable explanation of why this routing was suggested.</param>
public sealed record BookRoutingSuggestion(
    BookRouting Routing,
    double Confidence,
    string Reasoning);

/// <summary>
/// Suggests A-Book, B-Book, or Hybrid routing for trading accounts.
///
/// Routing indicators:
/// - A-Book (route to LP): Consistently profitable traders, scalpers with high win rates,
///   latency arbitrage suspects, accounts with high abuse scores.
/// - B-Book (internalize): Unprofitable traders, high churn accounts, bonus-dependent traders,
///   accounts with negative expected value.
/// - Hybrid: Mixed profitability, medium-term traders, accounts where routing confidence
///   is below the threshold for pure A/B classification.
///
/// Key inputs:
/// - TotalProfit trajectory (profitable → A-Book)
/// - AbuseScore (high score → A-Book to avoid internalization risk)
/// - TradingStyle (Scalper → likely A-Book, Swing → evaluate profitability)
/// - WinRateOnShortTrades (high → A-Book)
/// - Volume relative to group average (outliers → A-Book)
///
/// Design rationale:
/// - Uses a weighted scoring approach rather than hard rules, so the confidence
///   level naturally reflects how clear-cut the routing decision is.
/// - Returns reasoning text so risk managers can understand and override suggestions.
/// </summary>
public class BookRouter
{
    /// <summary>
    /// Suggests book routing for a trading account based on its analysis and profile.
    /// </summary>
    /// <param name="analysis">Account analysis with behavioral metrics and abuse score.</param>
    /// <param name="profile">Trader profile with style classification and identity info.</param>
    /// <returns>Routing suggestion with confidence level and reasoning.</returns>
    public BookRoutingSuggestion Suggest(AccountAnalysis analysis, TraderProfile profile)
    {
        // TODO: Phase 5, Task 16 — Implement routing logic using the indicators described
        //       in the XML doc comment above. Key considerations:
        //       - High abuse score (>70) should strongly favor A-Book routing.
        //       - Consistently unprofitable traders are B-Book candidates.
        //       - New accounts with insufficient data should return Hybrid with low confidence.
        //       - Reasoning string should be specific enough for risk managers to act on.

        // Insufficient data guard
        if (analysis.TotalTrades < 10 || profile.FirstSeen == default)
        {
            return new BookRoutingSuggestion(
                BookRouting.Unknown,
                0.0,
                "Insufficient data for routing suggestion");
        }

        // Placeholder — full implementation in Phase 5
        return new BookRoutingSuggestion(
            BookRouting.Unknown,
            0.0,
            "Insufficient data for routing suggestion");
    }
}
