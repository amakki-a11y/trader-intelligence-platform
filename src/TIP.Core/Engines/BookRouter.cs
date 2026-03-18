using System;
using System.Collections.Generic;
using TIP.Core.Models;

namespace TIP.Core.Engines;

/// <summary>
/// Determines the optimal book routing (A-Book, B-Book, Hybrid) for a trading account
/// based on abuse score, trading style, profitability, ring membership, and risk signals.
///
/// Design rationale:
/// - Pure rule-based routing — no ML, fully auditable decisions.
/// - Accepts AccountAnalysis + StyleClassifier.StyleResult as inputs.
/// - Returns a BookResult containing the recommendation, confidence, reasoning summary,
///   and a list of risk flags that influenced the decision.
/// - Routing logic prioritizes risk containment: high-risk accounts always go A-Book
///   to shift risk to the liquidity provider. Low-risk retail goes B-Book for margin capture.
/// - Thread-safe: stateless router, all state is passed in.
/// </summary>
public sealed class BookRouter
{
    /// <summary>
    /// Result of a book routing decision containing the recommendation, confidence,
    /// human-readable summary, and risk flags.
    /// </summary>
    public sealed record BookResult(
        BookRouting Recommendation,
        double Confidence,
        string Summary,
        List<string> RiskFlags);

    /// <summary>
    /// Determines the optimal book routing for an account.
    /// </summary>
    /// <param name="account">Account analysis with scoring and metrics.</param>
    /// <param name="style">Style classification result from StyleClassifier.</param>
    /// <returns>Book routing recommendation with confidence and reasoning.</returns>
    public BookResult Route(AccountAnalysis account, StyleClassifier.StyleResult style)
    {
        var flags = new List<string>();
        double aBookWeight = 0;
        double bBookWeight = 0;

        // ── Critical score → A-Book (mandatory) ─────────────────────────────
        if (account.AbuseScore >= 70)
        {
            aBookWeight += 5.0;
            flags.Add($"CRITICAL abuse score ({account.AbuseScore:F0}) — must A-Book");
        }
        else if (account.AbuseScore >= 50)
        {
            aBookWeight += 3.0;
            flags.Add($"HIGH abuse score ({account.AbuseScore:F0}) — A-Book recommended");
        }
        else if (account.AbuseScore >= 30)
        {
            aBookWeight += 1.0;
            bBookWeight += 1.0;
            flags.Add($"MEDIUM abuse score ({account.AbuseScore:F0}) — review needed");
        }
        else
        {
            bBookWeight += 2.0;
            // No flag for normal scores
        }

        // ── Ring membership → A-Book (mandatory) ────────────────────────────
        if (account.IsRingMember)
        {
            aBookWeight += 6.0;
            flags.Add("Ring member detected — coordinated trading risk");
        }

        if (account.RingCorrelationCount > 5)
        {
            aBookWeight += 1.0;
            flags.Add($"{account.RingCorrelationCount} correlated trades with other accounts");
        }

        // ── Style-based routing ──────────────────────────────────────────────
        if (style.Style == TradingStyle.EA || style.Style == TradingStyle.Scalper)
        {
            aBookWeight += 2.0;
            flags.Add($"{style.Style} style — algorithmic/HFT risk");
        }
        else if (style.Style == TradingStyle.Manual)
        {
            bBookWeight += 1.5;
            // Manual traders are typically retail — B-Book favorable
        }
        else if (style.Style == TradingStyle.Swing)
        {
            bBookWeight += 1.0;
            // Swing traders have low frequency — lower manipulation risk
        }

        // ── Profitability signals ────────────────────────────────────────────
        if (account.TotalTrades > 10 && account.TotalProfit > 0)
        {
            // Profitable trader — A-Book to avoid broker loss
            var profitPerTrade = account.TotalProfit / account.TotalTrades;
            if (profitPerTrade > 50)
            {
                aBookWeight += 2.0;
                flags.Add($"Highly profitable (${profitPerTrade:F0}/trade) — A-Book to hedge");
            }
            else if (profitPerTrade > 10)
            {
                aBookWeight += 1.0;
                flags.Add($"Profitable (${profitPerTrade:F0}/trade)");
            }
        }
        else if (account.TotalTrades > 10 && account.TotalProfit < 0)
        {
            // Losing trader — B-Book is profitable for broker
            bBookWeight += 1.5;
        }

        // ── Timing precision / bot signals ───────────────────────────────────
        if (account.TimingEntropyCV > 0 && account.TimingEntropyCV < 0.15)
        {
            aBookWeight += 1.5;
            flags.Add($"Robotic timing (CV={account.TimingEntropyCV:F3}) — potential HFT");
        }

        // ── Expert trade ratio ───────────────────────────────────────────────
        if (account.ExpertTradeRatio > 0.9)
        {
            aBookWeight += 1.0;
            flags.Add($"Expert ratio {account.ExpertTradeRatio:P0} — fully automated");
        }

        // ── Volume signals ───────────────────────────────────────────────────
        if (account.TotalVolume > 100)
        {
            aBookWeight += 1.0;
            flags.Add($"High volume ({account.TotalVolume:F0} lots) — significant exposure");
        }

        // ── Decision ─────────────────────────────────────────────────────────
        var totalWeight = aBookWeight + bBookWeight;
        if (totalWeight < 0.01)
        {
            return new BookResult(BookRouting.Unknown, 0.0, "Insufficient data for routing decision", flags);
        }

        var aBookRatio = aBookWeight / totalWeight;

        BookRouting recommendation;
        double confidence;
        string summary;

        if (aBookRatio >= 0.7)
        {
            recommendation = BookRouting.ABook;
            confidence = Math.Min(aBookRatio, 1.0);
            summary = BuildSummary(account, "A-Book", flags);
        }
        else if (aBookRatio <= 0.35)
        {
            recommendation = BookRouting.BBook;
            confidence = Math.Min(1.0 - aBookRatio, 1.0);
            summary = BuildSummary(account, "B-Book", flags);
        }
        else
        {
            recommendation = BookRouting.Hybrid;
            confidence = 1.0 - Math.Abs(aBookRatio - 0.5) * 2; // Peaks at 50/50
            summary = BuildSummary(account, "Hybrid", flags);
        }

        return new BookResult(recommendation, confidence, summary, flags);
    }

    /// <summary>
    /// Builds a human-readable summary of the routing decision.
    /// </summary>
    private static string BuildSummary(AccountAnalysis account, string routing, List<string> flags)
    {
        var riskNote = flags.Count > 0 ? $" {flags.Count} risk flag(s) identified." : "";
        var profitNote = account.TotalTrades > 0
            ? $" P&L: ${account.TotalProfit:F2} over {account.TotalTrades} trades."
            : "";
        return $"Recommended {routing} routing for login {account.Login}.{profitNote}{riskNote}";
    }
}
