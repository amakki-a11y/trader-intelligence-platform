using System;
using System.Collections.Generic;

namespace TIP.Core.Models;

/// <summary>
/// Risk level classification for an account based on its abuse score.
/// </summary>
public enum RiskLevel
{
    /// <summary>Score 0-25: No significant abuse indicators detected.</summary>
    Low,
    /// <summary>Score 26-50: Some suspicious patterns — monitor closely.</summary>
    Medium,
    /// <summary>Score 51-75: Multiple abuse indicators — investigate immediately.</summary>
    High,
    /// <summary>Score 76-100: Strong abuse evidence — urgent action required.</summary>
    Critical
}

/// <summary>
/// Complete analysis result for a single trading account, containing all 25+ metrics
/// used by the RuleEngine for abuse scoring. This is the central data structure that
/// flows through the scoring pipeline.
///
/// Design rationale:
/// - Mutable class (not record) because metrics are computed incrementally by different
///   engines (PnL, Exposure, Bot, Correlation) and assembled over time.
/// - All metric properties have sensible defaults (0 or false) so partially-computed
///   analyses are safe to score — missing metrics simply won't trigger rules.
/// </summary>
public class AccountAnalysis
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>MT5 account login number.</summary>
    public ulong Login { get; set; }

    /// <summary>Account holder name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>MT5 group the account belongs to (e.g., "real\standard").</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>MT5 server identifier.</summary>
    public string Server { get; set; } = string.Empty;

    // ── Scoring ───────────────────────────────────────────────────────────────

    /// <summary>Composite abuse score (0-100) computed by RuleEngine.</summary>
    public double AbuseScore { get; set; }

    /// <summary>Risk classification derived from AbuseScore.</summary>
    public RiskLevel RiskLevel { get; set; }

    /// <summary>Previous abuse score for trend comparison.</summary>
    public double PreviousScore { get; set; }

    /// <summary>When the account was last scored.</summary>
    public DateTimeOffset LastScored { get; set; }

    // ── v1.0 Metrics (11) ─────────────────────────────────────────────────────

    /// <summary>Total number of executed trades.</summary>
    public int TotalTrades { get; set; }

    /// <summary>Total traded volume in lots.</summary>
    public double TotalVolume { get; set; }

    /// <summary>Total commission paid in deposit currency.</summary>
    public double TotalCommission { get; set; }

    /// <summary>Total realized profit/loss in deposit currency.</summary>
    public double TotalProfit { get; set; }

    /// <summary>Total deposit amount in deposit currency.</summary>
    public double TotalDeposits { get; set; }

    /// <summary>Total withdrawal amount in deposit currency.</summary>
    public double TotalWithdrawals { get; set; }

    /// <summary>Total bonus amount received.</summary>
    public double TotalBonuses { get; set; }

    /// <summary>Number of deposit transactions.</summary>
    public int DepositCount { get; set; }

    /// <summary>Number of stop-out compensation events.</summary>
    public int SOCompensationCount { get; set; }

    /// <summary>Ratio of commission to volume — low ratio on high volume suggests rebate farming.</summary>
    public double CommissionToVolumeRatio { get; set; }

    /// <summary>Ratio of profit to commission — near-zero suggests wash trading for commission rebates.</summary>
    public double ProfitToCommissionRatio { get; set; }

    // ── Latency Arbitrage Metrics (4) ─────────────────────────────────────────

    /// <summary>Average trade hold time in seconds — very low values suggest latency arbitrage.</summary>
    public double AvgHoldSeconds { get; set; }

    /// <summary>Win rate on trades held under 60 seconds — high values suggest latency abuse.</summary>
    public double WinRateOnShortTrades { get; set; }

    /// <summary>Number of trades held under 60 seconds.</summary>
    public int ScalpCount { get; set; }

    /// <summary>
    /// Directional bias of slippage: positive means slippage consistently favors the trader,
    /// suggesting they exploit price feed delays. Range: -1.0 to 1.0.
    /// </summary>
    public double SlippageDirectionBias { get; set; }

    // ── Bonus Abuse Metrics (4) ───────────────────────────────────────────────

    /// <summary>Seconds between deposit and first trade — very low suggests automated abuse.</summary>
    public double DepositToFirstTradeSeconds { get; set; }

    /// <summary>Hours between last trade and withdrawal — very low suggests hit-and-run.</summary>
    public double TradeToWithdrawalHours { get; set; }

    /// <summary>Ratio of bonus to deposit — high values suggest bonus exploitation.</summary>
    public double BonusToDepositRatio { get; set; }

    /// <summary>Whether traded volume exactly meets bonus requirements — suspicious precision.</summary>
    public bool VolumeMeetsBonusReqExactly { get; set; }

    // ── Bot Farming Metrics (5) ───────────────────────────────────────────────

    /// <summary>
    /// Coefficient of variation of trade timing intervals.
    /// Low CV (&lt;0.1) means robotic precision; high CV (&gt;2.0) means human-like randomness.
    /// </summary>
    public double TimingEntropyCV { get; set; }

    /// <summary>Ratio of trades placed by Expert Advisors vs manual (0.0 to 1.0).</summary>
    public double ExpertTradeRatio { get; set; }

    /// <summary>Average trade volume in lots.</summary>
    public double AvgVolumeLots { get; set; }

    /// <summary>Average number of trades per hour during active trading periods.</summary>
    public double TradesPerHour { get; set; }

    /// <summary>Number of distinct Expert Advisor IDs used.</summary>
    public int UniqueExpertIds { get; set; }

    // ── Ring Detection Metrics ────────────────────────────────────────────────

    /// <summary>Whether this account is part of a detected trading ring.</summary>
    public bool IsRingMember { get; set; }

    /// <summary>Number of correlated trade pairs found with other accounts.</summary>
    public int RingCorrelationCount { get; set; }

    /// <summary>Login numbers of accounts this account is correlated with.</summary>
    public List<ulong> LinkedLogins { get; set; } = new();
}
