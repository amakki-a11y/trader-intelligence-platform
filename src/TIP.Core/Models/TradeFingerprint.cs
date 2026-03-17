using System;

namespace TIP.Core.Models;

/// <summary>
/// Lightweight value type representing the minimal data needed to correlate trades
/// across accounts for ring detection. Uses readonly record struct (~50 bytes) for
/// memory efficiency when holding millions of fingerprints in the correlation index.
///
/// Design rationale:
/// - Value type avoids heap allocation overhead when stored in large collections.
/// - GetBucketKey() groups trades into time windows for O(1) lookup of potentially
///   correlated trades, reducing the O(n^2) naive comparison to O(n * k) where k
///   is the average bucket size.
/// </summary>
/// <param name="DealId">MT5 deal ticket number.</param>
/// <param name="Login">Account login that owns the deal.</param>
/// <param name="TimeMsc">Deal execution time in milliseconds since Unix epoch.</param>
/// <param name="Symbol">Trading instrument symbol.</param>
/// <param name="Direction">Trade direction: 0=BUY, 1=SELL.</param>
/// <param name="Volume">Trade volume in lots.</param>
/// <param name="ExpertId">Expert Advisor ID (0 if manual trade).</param>
public readonly record struct TradeFingerprint(
    ulong DealId,
    ulong Login,
    long TimeMsc,
    string Symbol,
    int Direction,
    double Volume,
    ulong ExpertId)
{
    /// <summary>
    /// Computes a bucket key for grouping trades within the same time window and symbol.
    /// Trades in the same bucket are candidates for correlation analysis.
    /// </summary>
    /// <param name="windowMs">Time window size in milliseconds (default 5000 = 5 seconds).</param>
    /// <returns>
    /// Tuple of (Symbol, Bucket) where Bucket is TimeMsc divided by windowMs.
    /// All trades within the same 5-second window on the same symbol share a bucket key.
    /// </returns>
    public (string Symbol, long Bucket) GetBucketKey(int windowMs = 5000)
    {
        return (Symbol, TimeMsc / windowMs);
    }
}
