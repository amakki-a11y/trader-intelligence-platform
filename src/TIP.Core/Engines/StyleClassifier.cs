using System;
using System.Collections.Generic;
using TIP.Core.Models;

namespace TIP.Core.Engines;

/// <summary>
/// Classifies trader behavior into trading styles (Scalper, DayTrader, Swing, EA, Manual, Mixed)
/// based on hold times, trade frequency, EA usage, and timing patterns.
///
/// Design rationale:
/// - Pure rule-based classification — no ML dependencies, fully deterministic.
/// - Accepts AccountAnalysis metrics as input (no direct DB access).
/// - Returns a StyleResult with primary style, confidence (0.0-1.0), and signals list
///   explaining which behavioral indicators contributed to the classification.
/// - Confidence is computed as the dominant style's score relative to total signal strength.
/// - Thread-safe: stateless classifier, all state lives in the AccountAnalysis passed in.
/// </summary>
public sealed class StyleClassifier
{
    /// <summary>
    /// Result of a style classification containing the primary style, confidence level,
    /// and the behavioral signals that contributed to the classification.
    /// </summary>
    public sealed record StyleResult(
        TradingStyle Style,
        double Confidence,
        List<string> Signals);

    /// <summary>
    /// Classifies the trading style for an account based on its analysis metrics.
    /// </summary>
    /// <param name="account">Account analysis containing trading metrics.</param>
    /// <returns>Style classification with confidence and contributing signals.</returns>
    public StyleResult Classify(AccountAnalysis account)
    {
        if (account.TotalTrades == 0)
        {
            return new StyleResult(TradingStyle.Unknown, 0.0, new List<string> { "No trades recorded" });
        }

        var scores = new Dictionary<TradingStyle, double>();
        var signals = new List<string>();

        // ── Scalper signals ──────────────────────────────────────────────────
        double scalperScore = 0;
        if (account.AvgHoldSeconds > 0 && account.AvgHoldSeconds < 60)
        {
            scalperScore += 3.0;
            signals.Add($"Avg hold {account.AvgHoldSeconds:F0}s < 60s → Scalper");
        }
        else if (account.AvgHoldSeconds >= 60 && account.AvgHoldSeconds < 300)
        {
            scalperScore += 1.0;
            signals.Add($"Avg hold {account.AvgHoldSeconds:F0}s (short) → mild Scalper");
        }

        if (account.TradesPerHour > 20)
        {
            scalperScore += 2.0;
            signals.Add($"Trades/hour {account.TradesPerHour:F1} > 20 → high frequency");
        }
        else if (account.TradesPerHour > 10)
        {
            scalperScore += 1.0;
            signals.Add($"Trades/hour {account.TradesPerHour:F1} > 10 → moderate frequency");
        }

        if (account.ScalpCount > 0 && account.TotalTrades > 0 &&
            (double)account.ScalpCount / account.TotalTrades > 0.7)
        {
            scalperScore += 2.0;
            signals.Add($"Scalp ratio {(double)account.ScalpCount / account.TotalTrades:P0} > 70%");
        }
        scores[TradingStyle.Scalper] = scalperScore;

        // ── EA / HFT signals ─────────────────────────────────────────────────
        double eaScore = 0;
        if (account.ExpertTradeRatio > 0.8)
        {
            eaScore += 3.0;
            signals.Add($"Expert ratio {account.ExpertTradeRatio:P0} > 80% → EA-driven");
        }
        else if (account.ExpertTradeRatio > 0.5)
        {
            eaScore += 1.5;
            signals.Add($"Expert ratio {account.ExpertTradeRatio:P0} > 50% → partial EA");
        }

        if (account.TimingEntropyCV > 0 && account.TimingEntropyCV < 0.15)
        {
            eaScore += 2.0;
            signals.Add($"Timing CV {account.TimingEntropyCV:F3} < 0.15 → robotic precision");
        }

        if (account.UniqueExpertIds > 1)
        {
            eaScore += 1.0;
            signals.Add($"{account.UniqueExpertIds} unique EAs detected");
        }
        scores[TradingStyle.EA] = eaScore;

        // ── Swing signals ────────────────────────────────────────────────────
        double swingScore = 0;
        if (account.AvgHoldSeconds > 86400) // > 1 day
        {
            swingScore += 3.0;
            signals.Add($"Avg hold {account.AvgHoldSeconds / 3600:F1}h > 24h → Swing");
        }
        else if (account.AvgHoldSeconds > 14400) // > 4 hours
        {
            swingScore += 1.5;
            signals.Add($"Avg hold {account.AvgHoldSeconds / 3600:F1}h > 4h → long holds");
        }

        if (account.TradesPerHour > 0 && account.TradesPerHour < 1)
        {
            swingScore += 1.5;
            signals.Add($"Trades/hour {account.TradesPerHour:F2} < 1 → low frequency");
        }
        scores[TradingStyle.Swing] = swingScore;

        // ── DayTrader signals ────────────────────────────────────────────────
        double dayTraderScore = 0;
        if (account.AvgHoldSeconds >= 300 && account.AvgHoldSeconds <= 14400)
        {
            dayTraderScore += 2.5;
            signals.Add($"Avg hold {account.AvgHoldSeconds / 60:F0}m (5min–4h) → DayTrader");
        }

        if (account.TradesPerHour >= 1 && account.TradesPerHour <= 10)
        {
            dayTraderScore += 1.5;
            signals.Add($"Trades/hour {account.TradesPerHour:F1} (1-10) → moderate frequency");
        }
        scores[TradingStyle.DayTrader] = dayTraderScore;

        // ── Manual signals ───────────────────────────────────────────────────
        double manualScore = 0;
        if (account.ExpertTradeRatio < 0.1)
        {
            manualScore += 2.5;
            signals.Add($"Expert ratio {account.ExpertTradeRatio:P0} < 10% → Manual");
        }
        else if (account.ExpertTradeRatio < 0.3)
        {
            manualScore += 1.0;
            signals.Add($"Expert ratio {account.ExpertTradeRatio:P0} < 30% → mostly manual");
        }

        if (account.TimingEntropyCV > 1.5)
        {
            manualScore += 1.5;
            signals.Add($"Timing CV {account.TimingEntropyCV:F2} > 1.5 → human-like randomness");
        }
        scores[TradingStyle.Manual] = manualScore;

        // ── Find dominant style ──────────────────────────────────────────────
        var totalScore = 0.0;
        TradingStyle bestStyle = TradingStyle.Unknown;
        double bestScore = 0;

        foreach (var (style, score) in scores)
        {
            totalScore += score;
            if (score > bestScore)
            {
                bestScore = score;
                bestStyle = style;
            }
        }

        // If no clear winner or very low total signal strength, classify as Mixed/Unknown
        if (totalScore < 1.0)
        {
            return new StyleResult(TradingStyle.Unknown, 0.0, signals.Count > 0 ? signals : new List<string> { "Insufficient signal strength" });
        }

        var confidence = bestScore / totalScore;

        // If top two styles are very close, classify as Mixed
        double secondBest = 0;
        foreach (var (style, score) in scores)
        {
            if (style != bestStyle && score > secondBest)
                secondBest = score;
        }

        if (secondBest > 0 && bestScore > 0 && secondBest / bestScore > 0.8)
        {
            return new StyleResult(TradingStyle.Mixed, confidence * 0.6, signals);
        }

        return new StyleResult(bestStyle, Math.Min(confidence, 1.0), signals);
    }
}
