using System;
using System.Collections.Generic;
using System.Linq;
using TIP.Core.Models;

namespace TIP.Core.Engines;

/// <summary>
/// Bot detection engine using timing entropy analysis and Expert Advisor clustering.
///
/// Design rationale:
/// - Pure logic class with no I/O dependencies — fully testable.
/// - Identifies automated trading bots by analyzing behavioral patterns:
///   timing entropy (CV), ExpertID clustering, volume patterns, trade frequency.
/// - Bot confidence is a weighted combination of multiple signals (0.0-1.0).
/// - Performance target: Analyze 1,000 accounts in under 2 seconds.
/// </summary>
public sealed class BotFingerprinter
{
    /// <summary>
    /// Analyze an account's trading pattern for bot indicators.
    /// </summary>
    /// <param name="login">Account login being analyzed.</param>
    /// <param name="trades">Trade fingerprints for this account.</param>
    /// <returns>Bot analysis result with all detection signals.</returns>
    public BotAnalysis Analyze(ulong login, IReadOnlyList<TradeFingerprint> trades)
    {
        if (trades.Count == 0)
        {
            return new BotAnalysis
            {
                TimingEntropyCV = 1.0,
                ExpertTradeRatio = 0,
                AvgVolumeLots = 0,
                VolumeVarianceCV = 1.0,
                TradesPerHour = 0,
                UniqueExpertIds = 0,
                ExpertIds = new HashSet<ulong>(),
                IsSuspectedBot = false,
                BotConfidence = 0
            };
        }

        // Timing entropy CV
        var timingCV = CalculateTimingEntropy(trades);

        // Expert trade ratio
        var expertTrades = trades.Count(t => t.ExpertId != 0);
        var expertRatio = (double)expertTrades / trades.Count;

        // Volume analysis
        var volumes = trades.Select(t => t.Volume).ToList();
        var avgVolume = volumes.Average();
        var volumeCV = CalculateCV(volumes);

        // Trades per hour
        var tradesPerHour = 0.0;
        if (trades.Count >= 2)
        {
            var spanMs = trades.Max(t => t.TimeMsc) - trades.Min(t => t.TimeMsc);
            if (spanMs > 0)
                tradesPerHour = trades.Count / (spanMs / 3600000.0);
        }

        // Unique ExpertIDs
        var expertIds = new HashSet<ulong>(trades.Where(t => t.ExpertId != 0).Select(t => t.ExpertId));

        // Bot detection rules
        var isSuspected =
            (timingCV < 0.1 && tradesPerHour > 20) ||
            (expertRatio > 0.95 && tradesPerHour > 30) ||
            (Math.Abs(avgVolume - 0.01) < 0.001 && volumeCV < 0.05);

        // Confidence calculation
        var confidence = 0.0;
        if (timingCV < 0.1) confidence += 0.3;
        if (expertRatio > 0.95) confidence += 0.2;
        if (tradesPerHour > 50) confidence += 0.2;
        if (volumeCV < 0.05) confidence += 0.15;
        if (expertIds.Count == 1 && expertTrades == trades.Count) confidence += 0.15;

        return new BotAnalysis
        {
            TimingEntropyCV = Math.Round(timingCV, 4),
            ExpertTradeRatio = Math.Round(expertRatio, 4),
            AvgVolumeLots = Math.Round(avgVolume, 4),
            VolumeVarianceCV = Math.Round(volumeCV, 4),
            TradesPerHour = Math.Round(tradesPerHour, 2),
            UniqueExpertIds = expertIds.Count,
            ExpertIds = expertIds,
            IsSuspectedBot = isSuspected,
            BotConfidence = Math.Min(Math.Round(confidence, 2), 1.0)
        };
    }

    /// <summary>
    /// Calculate the coefficient of variation of inter-trade intervals.
    /// </summary>
    private static double CalculateTimingEntropy(IReadOnlyList<TradeFingerprint> trades)
    {
        if (trades.Count < 3) return 1.0;

        var sorted = trades.OrderBy(t => t.TimeMsc).ToList();
        var intervals = new List<double>();
        for (var i = 1; i < sorted.Count; i++)
        {
            intervals.Add(sorted[i].TimeMsc - sorted[i - 1].TimeMsc);
        }

        return CalculateCV(intervals);
    }

    /// <summary>
    /// Calculate coefficient of variation (σ/μ) for a list of values.
    /// </summary>
    private static double CalculateCV(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 1.0;

        var mean = values.Average();
        if (mean == 0) return 0;

        var variance = values.Average(x => (x - mean) * (x - mean));
        var stdDev = Math.Sqrt(variance);
        return stdDev / mean;
    }
}

/// <summary>
/// Result of bot fingerprint analysis for a single account.
/// </summary>
public sealed record BotAnalysis
{
    /// <summary>Coefficient of variation of inter-trade intervals. CV &lt; 0.1 = bot-like.</summary>
    public required double TimingEntropyCV { get; init; }

    /// <summary>Ratio of trades placed by Expert Advisors (0.0-1.0). &gt; 0.95 = almost all EA.</summary>
    public required double ExpertTradeRatio { get; init; }

    /// <summary>Average trade volume in lots. 0.01 consistently = micro lot farming.</summary>
    public required double AvgVolumeLots { get; init; }

    /// <summary>CV of trade volumes. &lt; 0.05 = no variation in lot sizes.</summary>
    public required double VolumeVarianceCV { get; init; }

    /// <summary>Average trades per hour. &gt; 50 = beyond human capability.</summary>
    public required double TradesPerHour { get; init; }

    /// <summary>Number of distinct Expert Advisor IDs used.</summary>
    public required int UniqueExpertIds { get; init; }

    /// <summary>Specific EA magic numbers used by this account.</summary>
    public required HashSet<ulong> ExpertIds { get; init; }

    /// <summary>Summary flag: true if any bot detection rule triggered.</summary>
    public required bool IsSuspectedBot { get; init; }

    /// <summary>Weighted confidence score (0.0-1.0) that this is a bot.</summary>
    public required double BotConfidence { get; init; }
}
