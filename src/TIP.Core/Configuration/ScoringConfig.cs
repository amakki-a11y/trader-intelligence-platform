namespace TIP.Core.Configuration;

/// <summary>
/// All tunable thresholds for scoring, detection, and routing engines.
/// Loaded from appsettings.json section "Scoring". Runtime-adjustable without rebuild.
/// </summary>
public class ScoringConfig
{
    /// <summary>Abuse score >= this → Critical risk.</summary>
    public int CriticalThreshold { get; set; } = 70;

    /// <summary>Abuse score >= this → High risk.</summary>
    public int HighThreshold { get; set; } = 50;

    /// <summary>Abuse score >= this → Medium risk.</summary>
    public int MediumThreshold { get; set; } = 30;

    /// <summary>Timing CV below this → suspected bot.</summary>
    public double BotEntropyThreshold { get; set; } = 0.1;

    /// <summary>Expert trade ratio above this → automated.</summary>
    public double ExpertTradeRatioThreshold { get; set; } = 0.95;

    /// <summary>Correlation time window in seconds.</summary>
    public int CorrelationWindowSeconds { get; set; } = 5;

    /// <summary>Max fingerprints before auto-prune.</summary>
    public int MaxFingerprints { get; set; } = 500_000;

    /// <summary>Hold time under this (minutes) = scalp.</summary>
    public double ScalpMaxHoldMinutes { get; set; } = 5.0;

    /// <summary>Scalp win rate above this is suspicious.</summary>
    public double ScalpWinRateThreshold { get; set; } = 0.70;

    /// <summary>Book routing: weight for abuse score signal.</summary>
    public double BookRoutingAbuseWeight { get; set; } = 0.35;

    /// <summary>Book routing: weight for trading style signal.</summary>
    public double BookRoutingStyleWeight { get; set; } = 0.25;

    /// <summary>Book routing: weight for profitability signal.</summary>
    public double BookRoutingProfitWeight { get; set; } = 0.20;

    /// <summary>Book routing: weight for timing precision signal.</summary>
    public double BookRoutingTimingWeight { get; set; } = 0.10;

    /// <summary>Book routing: weight for volume signal.</summary>
    public double BookRoutingVolumeWeight { get; set; } = 0.10;

    /// <summary>Simulation: commission per lot (round-trip).</summary>
    public double SimulationCommissionPerLot { get; set; } = 7.0;

    /// <summary>Simulation: spread capture per lot.</summary>
    public double SimulationSpreadCapture { get; set; } = 2.0;

    /// <summary>Hybrid routing: volume threshold per lot.</summary>
    public double HybridVolumeThreshold { get; set; } = 1.0;

    /// <summary>Bonus abuse: max seconds from deposit to first trade.</summary>
    public int DepositToFirstTradeMaxSeconds { get; set; } = 60;

    /// <summary>Bonus abuse: max hours from trade to withdrawal.</summary>
    public int TradeToWithdrawalMaxHours { get; set; } = 24;
}
