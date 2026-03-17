using System;
using System.Collections.Generic;
using System.Linq;
using TIP.Core.Models;

namespace TIP.Core.Engines;

/// <summary>
/// Enumeration of all metrics available for rule evaluation.
/// Each value maps to a property on <see cref="AccountAnalysis"/>.
/// </summary>
public enum RuleMetric
{
    // v1.0 Metrics (11)
    TotalTrades,
    TotalVolume,
    TotalCommission,
    TotalProfit,
    TotalDeposits,
    TotalWithdrawals,
    TotalBonuses,
    DepositCount,
    SOCompensationCount,
    CommissionToVolumeRatio,
    ProfitToCommissionRatio,

    // Latency Arbitrage Metrics (4)
    AvgHoldSeconds,
    WinRateOnShortTrades,
    ScalpCount,
    SlippageDirectionBias,

    // Bonus Abuse Metrics (4)
    DepositToFirstTradeSeconds,
    TradeToWithdrawalHours,
    BonusToDepositRatio,
    VolumeMeetsBonusReqExactly,

    // Bot Farming Metrics (5)
    TimingEntropyCV,
    ExpertTradeRatio,
    AvgVolumeLots,
    TradesPerHour,
    UniqueExpertIds,

    // Ring Detection (2)
    IsRingMember,
    RingCorrelationCount
}

/// <summary>
/// Comparison operators for rule evaluation.
/// </summary>
public enum RuleOperator
{
    GreaterThan,
    LessThan,
    GreaterOrEqual,
    LessOrEqual,
    Equal,
    NotEqual
}

/// <summary>
/// A single scoring rule that evaluates a metric against a threshold using an operator.
/// If the condition is met, the rule's weight is added to the abuse score.
/// </summary>
/// <param name="Metric">Which metric to evaluate.</param>
/// <param name="Operator">Comparison operator.</param>
/// <param name="Threshold">Value to compare the metric against.</param>
/// <param name="Weight">Points to add to abuse score if the rule fires (0 = disabled).</param>
/// <param name="Description">Human-readable explanation of what this rule detects.</param>
public sealed record Rule(
    RuleMetric Metric,
    RuleOperator Operator,
    double Threshold,
    double Weight,
    string Description);

/// <summary>
/// Configurable scoring engine that evaluates trading accounts against a set of rules.
/// Ported from the v1.0 RebateAbuseDetector with expanded metrics for latency arbitrage,
/// bonus abuse, bot farming, and ring detection.
///
/// Design rationale:
/// - Rules are data-driven (not hard-coded if/else) so operators can tune thresholds
///   and weights without code changes.
/// - 23 metrics across 6 categories, evaluated with 6 comparison operators.
/// - Score is capped at 100 to maintain a consistent 0-100 scale for UI display.
/// - Rules with weight=0 are treated as disabled and skipped for performance.
/// </summary>
public class RuleEngine
{
    private readonly List<Rule> _rules;

    /// <summary>
    /// Initializes the rule engine with the provided rule set.
    /// </summary>
    /// <param name="rules">Rules to evaluate. Rules with weight=0 are disabled.</param>
    public RuleEngine(IEnumerable<Rule> rules)
    {
        _rules = rules.ToList();
    }

    /// <summary>
    /// Gets the currently active rules (weight > 0).
    /// </summary>
    public IReadOnlyList<Rule> ActiveRules => _rules.Where(r => r.Weight > 0).ToList();

    /// <summary>
    /// Scores an account analysis against all active rules.
    /// Iterates active rules, evaluates each against the account's metrics,
    /// sums the weights of triggered rules, and caps the result at 100.
    /// </summary>
    /// <param name="analysis">Account analysis containing all metric values.</param>
    /// <returns>Abuse score from 0 to 100.</returns>
    public double Score(AccountAnalysis analysis)
    {
        var totalScore = 0.0;

        foreach (var rule in _rules)
        {
            if (rule.Weight <= 0)
            {
                continue;
            }

            var metricValue = GetMetricValue(analysis, rule.Metric);

            if (EvaluateOperator(metricValue, rule.Operator, rule.Threshold))
            {
                totalScore += rule.Weight;
            }
        }

        return Math.Min(totalScore, 100.0);
    }

    /// <summary>
    /// Extracts the numeric value of a metric from an account analysis.
    /// Boolean metrics are converted to 1.0 (true) or 0.0 (false).
    /// </summary>
    /// <param name="analysis">Account analysis to read from.</param>
    /// <param name="metric">Which metric to extract.</param>
    /// <returns>The metric's numeric value.</returns>
    public static double GetMetricValue(AccountAnalysis analysis, RuleMetric metric)
    {
        return metric switch
        {
            // v1.0 Metrics
            RuleMetric.TotalTrades => analysis.TotalTrades,
            RuleMetric.TotalVolume => analysis.TotalVolume,
            RuleMetric.TotalCommission => analysis.TotalCommission,
            RuleMetric.TotalProfit => analysis.TotalProfit,
            RuleMetric.TotalDeposits => analysis.TotalDeposits,
            RuleMetric.TotalWithdrawals => analysis.TotalWithdrawals,
            RuleMetric.TotalBonuses => analysis.TotalBonuses,
            RuleMetric.DepositCount => analysis.DepositCount,
            RuleMetric.SOCompensationCount => analysis.SOCompensationCount,
            RuleMetric.CommissionToVolumeRatio => analysis.CommissionToVolumeRatio,
            RuleMetric.ProfitToCommissionRatio => analysis.ProfitToCommissionRatio,

            // Latency Arbitrage
            RuleMetric.AvgHoldSeconds => analysis.AvgHoldSeconds,
            RuleMetric.WinRateOnShortTrades => analysis.WinRateOnShortTrades,
            RuleMetric.ScalpCount => analysis.ScalpCount,
            RuleMetric.SlippageDirectionBias => analysis.SlippageDirectionBias,

            // Bonus Abuse
            RuleMetric.DepositToFirstTradeSeconds => analysis.DepositToFirstTradeSeconds,
            RuleMetric.TradeToWithdrawalHours => analysis.TradeToWithdrawalHours,
            RuleMetric.BonusToDepositRatio => analysis.BonusToDepositRatio,
            RuleMetric.VolumeMeetsBonusReqExactly => analysis.VolumeMeetsBonusReqExactly ? 1.0 : 0.0,

            // Bot Farming
            RuleMetric.TimingEntropyCV => analysis.TimingEntropyCV,
            RuleMetric.ExpertTradeRatio => analysis.ExpertTradeRatio,
            RuleMetric.AvgVolumeLots => analysis.AvgVolumeLots,
            RuleMetric.TradesPerHour => analysis.TradesPerHour,
            RuleMetric.UniqueExpertIds => analysis.UniqueExpertIds,

            // Ring Detection
            RuleMetric.IsRingMember => analysis.IsRingMember ? 1.0 : 0.0,
            RuleMetric.RingCorrelationCount => analysis.RingCorrelationCount,

            _ => 0.0
        };
    }

    /// <summary>
    /// Evaluates a comparison operator against a value and threshold.
    /// </summary>
    /// <param name="value">The metric value to test.</param>
    /// <param name="op">The comparison operator.</param>
    /// <param name="threshold">The threshold to compare against.</param>
    /// <returns>True if the condition is satisfied.</returns>
    public static bool EvaluateOperator(double value, RuleOperator op, double threshold)
    {
        return op switch
        {
            RuleOperator.GreaterThan => value > threshold,
            RuleOperator.LessThan => value < threshold,
            RuleOperator.GreaterOrEqual => value >= threshold,
            RuleOperator.LessOrEqual => value <= threshold,
            RuleOperator.Equal => Math.Abs(value - threshold) < 0.0001,
            RuleOperator.NotEqual => Math.Abs(value - threshold) >= 0.0001,
            _ => false
        };
    }
}
