using System;
using System.Collections.Generic;
using TIP.Core.Models;

namespace TIP.Core.Engines;

/// <summary>
/// Represents a detected trading ring — a group of accounts that trade in coordinated patterns.
/// </summary>
/// <param name="MemberLogins">Set of account logins that are part of this ring.</param>
/// <param name="CorrelationCount">Total number of correlated trade pairs detected.</param>
/// <param name="SharedExpertIds">Expert Advisor IDs used by multiple ring members.</param>
/// <param name="ConfidenceScore">Confidence level (0.0-1.0) that this is a genuine ring.</param>
public sealed record DetectedRing(
    HashSet<ulong> MemberLogins,
    int CorrelationCount,
    HashSet<ulong> SharedExpertIds,
    double ConfidenceScore);

/// <summary>
/// Represents a single correlation match between two trades on different accounts.
/// </summary>
/// <param name="LoginA">First account login.</param>
/// <param name="LoginB">Second account login.</param>
/// <param name="Symbol">Instrument on which the correlated trades occurred.</param>
/// <param name="TimeDifferenceMs">Time difference in milliseconds between the two trades.</param>
public sealed record CorrelationMatch(
    ulong LoginA,
    ulong LoginB,
    string Symbol,
    long TimeDifferenceMs);

/// <summary>
/// 4-stage ring detection algorithm that identifies coordinated trading across accounts.
///
/// Algorithm stages:
/// 1. Index: Group trade fingerprints into time-window buckets by (Symbol, TimeBucket).
/// 2. Match: Within each bucket, find trades from different accounts with matching
///    direction, similar volume, and close timing.
/// 3. Cluster: Build a graph of correlated accounts and find connected components.
/// 4. Score: Evaluate ring confidence based on correlation density, shared EAs,
///    volume symmetry, and timing consistency.
///
/// Performance target: Process 1M fingerprints in under 5 seconds using the bucket
/// index to avoid O(n^2) pairwise comparison.
/// </summary>
public class CorrelationEngine
{
    private readonly Dictionary<(string Symbol, long Bucket), List<TradeFingerprint>> _index = new();

    /// <summary>
    /// Indexes a collection of trade fingerprints into time-window buckets for efficient lookup.
    /// Each fingerprint is grouped by its (Symbol, TimeBucket) key so that only trades
    /// in the same time window on the same symbol are compared during ring detection.
    /// </summary>
    /// <param name="fingerprints">Trade fingerprints to index.</param>
    public void IndexFingerprints(IReadOnlyList<TradeFingerprint> fingerprints)
    {
        foreach (var fingerprint in fingerprints)
        {
            var key = fingerprint.GetBucketKey();

            if (!_index.TryGetValue(key, out var bucket))
            {
                bucket = new List<TradeFingerprint>();
                _index[key] = bucket;
            }

            bucket.Add(fingerprint);
        }
    }

    /// <summary>
    /// Analyzes the indexed fingerprints to detect trading rings.
    /// Implements stages 2-4 of the ring detection algorithm.
    /// </summary>
    /// <returns>List of detected rings with member logins and confidence scores.</returns>
    public List<DetectedRing> AnalyzeRings()
    {
        // TODO: Phase 3, Task 8 — Implement matching: within each bucket, find trades from
        //       different accounts with same direction, similar volume (within 10%), and
        //       close timing (within bucket window).
        // TODO: Phase 3, Task 8 — Implement clustering: build adjacency graph of correlated
        //       accounts, find connected components using union-find.
        // TODO: Phase 3, Task 8 — Implement scoring: evaluate ring confidence based on
        //       correlation density, shared Expert Advisor IDs, volume symmetry.

        // Scan indexed buckets for multi-account activity (placeholder for full implementation)
        var rings = new List<DetectedRing>();
        foreach (var bucket in _index.Values)
        {
            if (bucket.Count > 1)
            {
                // Full matching/clustering/scoring will be implemented in Phase 3
            }
        }
        return rings;
    }

    /// <summary>
    /// Processes a single new trade fingerprint: adds it to the index and checks
    /// for correlations with existing fingerprints in the same time bucket.
    /// Used for real-time ring detection as deals arrive.
    /// </summary>
    /// <param name="fingerprint">The new trade fingerprint to process.</param>
    /// <returns>Any correlation matches found with existing fingerprints.</returns>
    public List<CorrelationMatch> CheckDeal(TradeFingerprint fingerprint)
    {
        var key = fingerprint.GetBucketKey();

        if (!_index.TryGetValue(key, out var bucket))
        {
            bucket = new List<TradeFingerprint>();
            _index[key] = bucket;
        }

        // TODO: Phase 3, Task 8 — Before adding, scan existing bucket entries for matches:
        //       same direction, different login, similar volume, within time window.
        //       Return CorrelationMatch for each match found.

        bucket.Add(fingerprint);
        return new List<CorrelationMatch>();
    }

    /// <summary>
    /// Gets the total number of indexed fingerprints across all buckets.
    /// </summary>
    public int IndexedCount => _index.Values.Sum(b => b.Count);

    /// <summary>
    /// Gets the number of distinct buckets in the index.
    /// </summary>
    public int BucketCount => _index.Count;

    /// <summary>
    /// Clears all indexed fingerprints. Used when starting a fresh analysis.
    /// </summary>
    public void Clear()
    {
        _index.Clear();
    }
}
