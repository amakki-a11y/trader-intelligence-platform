using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using TIP.Core.Models;

namespace TIP.Core.Engines;

/// <summary>
/// Represents a detected trading ring — a group of accounts that trade in coordinated patterns.
/// </summary>
public sealed class DetectedRing
{
    /// <summary>Set of account logins that are part of this ring.</summary>
    public HashSet<ulong> MemberLogins { get; set; } = new();

    /// <summary>Total number of correlated trade pairs detected across all members.</summary>
    public int CorrelationCount { get; set; }

    /// <summary>Expert Advisor IDs shared by multiple ring members.</summary>
    public HashSet<ulong> SharedExpertIds { get; set; } = new();

    /// <summary>Confidence level (0.0-1.0) that this is a genuine ring.</summary>
    public double ConfidenceScore { get; set; }
}

/// <summary>
/// Represents a single correlation match between two trades on different accounts.
/// </summary>
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
/// 2. Match: Within each bucket, find trades from different accounts with opposite direction,
///    different login, and close timing.
/// 3. Cluster: Build a graph of correlated accounts and find connected components using union-find.
/// 4. Score: Evaluate ring confidence based on correlation density, shared EAs, and timing.
///
/// Performance target: Process 1M fingerprints in under 5 seconds using the bucket index.
/// </summary>
public sealed class CorrelationEngine
{
    private readonly Dictionary<(string Symbol, long Bucket), List<TradeFingerprint>> _index = new();
    private readonly Dictionary<(ulong, ulong), int> _pairCounts = new();
    private readonly Dictionary<ulong, HashSet<ulong>> _expertIdsByLogin = new();
    private readonly ILogger<CorrelationEngine> _logger;
    private readonly int _windowMs;
    private readonly int _minPairsForRing;
    private readonly int _maxFingerprints;

    /// <summary>
    /// Initializes the correlation engine.
    /// </summary>
    /// <param name="logger">Logger for ring detection events.</param>
    /// <param name="windowMs">Time window in milliseconds for correlation matching (default 5000).</param>
    /// <param name="minPairsForRing">Minimum correlated pairs to form a ring (default 3).</param>
    /// <param name="maxFingerprints">Maximum fingerprint count before auto-prune (default 500,000).</param>
    public CorrelationEngine(ILogger<CorrelationEngine> logger, int windowMs = 5000, int minPairsForRing = 3, int maxFingerprints = 500_000)
    {
        _logger = logger;
        _windowMs = windowMs;
        _minPairsForRing = minPairsForRing;
        _maxFingerprints = maxFingerprints;
    }

    /// <summary>
    /// Maximum fingerprint count before auto-prune triggers.
    /// </summary>
    public int MaxFingerprints => _maxFingerprints;

    /// <summary>
    /// Stage 1: Indexes a collection of trade fingerprints into time-window buckets.
    /// </summary>
    /// <param name="fingerprints">Trade fingerprints to index.</param>
    public void IndexFingerprints(IReadOnlyList<TradeFingerprint> fingerprints)
    {
        foreach (var fingerprint in fingerprints)
        {
            var key = fingerprint.GetBucketKey(_windowMs);

            if (!_index.TryGetValue(key, out var bucket))
            {
                bucket = new List<TradeFingerprint>();
                _index[key] = bucket;
            }

            bucket.Add(fingerprint);

            // Track ExpertIDs per login for ring enrichment
            TrackExpertId(fingerprint.Login, fingerprint.ExpertId);
        }
    }

    /// <summary>
    /// Stages 2-4: Analyzes the indexed fingerprints to detect trading rings.
    /// Stage 2: Find correlated pairs (opposite direction, different login, within window).
    /// Stage 3: Cluster pairs into rings using union-find.
    /// Stage 4: Score rings by confidence.
    /// </summary>
    /// <returns>List of detected rings with member logins and confidence scores.</returns>
    public List<DetectedRing> AnalyzeRings()
    {
        _pairCounts.Clear();

        // Stage 2: Find correlated pairs
        FindCorrelatedPairs();

        // Stage 3+4: Cluster and score
        var rings = ClusterRings();

        _logger.LogInformation(
            "Ring analysis complete: {PairCount} pairs found, {RingCount} rings detected",
            _pairCounts.Count, rings.Count);

        return rings;
    }

    /// <summary>
    /// Live check: process a single new deal against the index.
    /// Returns any new correlations found.
    /// </summary>
    /// <param name="fingerprint">The new trade fingerprint to process.</param>
    /// <returns>Any correlation matches found with existing fingerprints.</returns>
    public List<CorrelationMatch> CheckDeal(TradeFingerprint fingerprint)
    {
        try
        {
            // Auto-prune if fingerprint count exceeds limit
            if (IndexedCount >= _maxFingerprints)
            {
                var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
                var pruned = Prune(cutoff);
                _logger.LogWarning("CorrelationEngine auto-pruned {Pruned} fingerprints (was at {Max} limit), now {Count}",
                    pruned, _maxFingerprints, IndexedCount);
            }

            var matches = new List<CorrelationMatch>();
            var key = fingerprint.GetBucketKey(_windowMs);

            // Check same bucket
            if (_index.TryGetValue(key, out var bucket))
            {
                FindMatchesInBucket(fingerprint, bucket, matches);
            }

            // Check adjacent buckets (boundary case: 4999ms and 5001ms in different buckets)
            var adjacentKey = (fingerprint.Symbol, key.Bucket - 1);
            if (_index.TryGetValue(adjacentKey, out var prevBucket))
            {
                FindMatchesInBucket(fingerprint, prevBucket, matches);
            }

            var nextKey = (fingerprint.Symbol, key.Bucket + 1);
            if (_index.TryGetValue(nextKey, out var nextBucket))
            {
                FindMatchesInBucket(fingerprint, nextBucket, matches);
            }

            // Add to index
            if (!_index.TryGetValue(key, out var list))
            {
                list = new List<TradeFingerprint>();
                _index[key] = list;
            }
            list.Add(fingerprint);

            // Track ExpertID
            TrackExpertId(fingerprint.Login, fingerprint.ExpertId);

            return matches;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CorrelationEngine.CheckDeal failed for login {Login} — returning no correlation", fingerprint.Login);
            return new List<CorrelationMatch>();
        }
    }

    /// <summary>
    /// Removes fingerprints older than the specified cutoff to manage memory.
    /// </summary>
    /// <param name="olderThan">Remove fingerprints with TimeMsc before this time.</param>
    /// <returns>Number of fingerprints pruned.</returns>
    public int Prune(DateTimeOffset olderThan)
    {
        var cutoffMsc = olderThan.ToUnixTimeMilliseconds();
        var pruned = 0;
        var keysToRemove = new List<(string, long)>();

        foreach (var kvp in _index)
        {
            var before = kvp.Value.Count;
            kvp.Value.RemoveAll(f => f.TimeMsc < cutoffMsc);
            pruned += before - kvp.Value.Count;

            if (kvp.Value.Count == 0)
                keysToRemove.Add(kvp.Key);
        }

        foreach (var key in keysToRemove)
            _index.Remove(key);

        if (pruned > 0)
            _logger.LogInformation("Pruned {Count} old fingerprints from correlation index", pruned);

        return pruned;
    }

    /// <summary>
    /// Gets the total number of indexed fingerprints across all buckets.
    /// </summary>
    public int IndexedCount
    {
        get
        {
            try { return _index.Values.Sum(b => b.Count); }
            catch (InvalidOperationException) { return 0; }
        }
    }

    /// <summary>
    /// Gets the number of distinct buckets in the index.
    /// </summary>
    public int BucketCount => _index.Count;

    /// <summary>
    /// Gets the number of correlated pairs found.
    /// </summary>
    public int PairCount => _pairCounts.Count;

    /// <summary>
    /// Gets the detected rings from the last analysis.
    /// </summary>
    public IReadOnlyDictionary<(ulong, ulong), int> PairCounts => _pairCounts;

    /// <summary>
    /// Clears all indexed fingerprints and pair counts.
    /// </summary>
    public void Clear()
    {
        _index.Clear();
        _pairCounts.Clear();
        _expertIdsByLogin.Clear();
    }

    /// <summary>
    /// Stage 2: Find all correlated pairs across all buckets.
    /// A pair is: same symbol, opposite direction, different login, within time window.
    /// </summary>
    private void FindCorrelatedPairs()
    {
        foreach (var (key, fingerprints) in _index)
        {
            // Within same bucket
            for (var i = 0; i < fingerprints.Count; i++)
            {
                for (var j = i + 1; j < fingerprints.Count; j++)
                {
                    CheckAndRecordPair(fingerprints[i], fingerprints[j]);
                }
            }

            // Check adjacent bucket (next) for boundary cases
            var adjacentKey = (key.Symbol, key.Bucket + 1);
            if (_index.TryGetValue(adjacentKey, out var nextBucket))
            {
                foreach (var a in fingerprints)
                {
                    foreach (var b in nextBucket)
                    {
                        CheckAndRecordPair(a, b);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Checks if two fingerprints form a correlated pair and records the match.
    /// </summary>
    private void CheckAndRecordPair(TradeFingerprint a, TradeFingerprint b)
    {
        if (a.Login == b.Login) return;
        if (a.Direction == b.Direction) return;

        var timeDiff = Math.Abs(a.TimeMsc - b.TimeMsc);
        if (timeDiff > _windowMs) return;

        var pairKey = MakePairKey(a.Login, b.Login);
        _pairCounts.TryGetValue(pairKey, out var count);
        _pairCounts[pairKey] = count + 1;
    }

    /// <summary>
    /// Finds matches for a fingerprint within a bucket (used for live CheckDeal).
    /// </summary>
    private void FindMatchesInBucket(TradeFingerprint fingerprint, List<TradeFingerprint> bucket, List<CorrelationMatch> matches)
    {
        foreach (var existing in bucket)
        {
            if (existing.Login == fingerprint.Login) continue;
            if (existing.Direction == fingerprint.Direction) continue;

            var timeDiff = Math.Abs(existing.TimeMsc - fingerprint.TimeMsc);
            if (timeDiff <= _windowMs)
            {
                matches.Add(new CorrelationMatch(
                    fingerprint.Login,
                    existing.Login,
                    fingerprint.Symbol,
                    timeDiff));

                var pairKey = MakePairKey(fingerprint.Login, existing.Login);
                _pairCounts.TryGetValue(pairKey, out var count);
                _pairCounts[pairKey] = count + 1;
            }
        }
    }

    /// <summary>
    /// Stage 3+4: Cluster confirmed pairs into rings using union-find, then score.
    /// </summary>
    private List<DetectedRing> ClusterRings()
    {
        var parent = new Dictionary<ulong, ulong>();

        ulong Find(ulong x)
        {
            if (!parent.ContainsKey(x)) parent[x] = x;
            if (parent[x] != x) parent[x] = Find(parent[x]);
            return parent[x];
        }

        void Union(ulong a, ulong b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        // Union all pairs that exceed threshold
        foreach (var ((loginA, loginB), count) in _pairCounts)
        {
            if (count >= _minPairsForRing)
            {
                Union(loginA, loginB);
            }
        }

        // Group by root to form rings
        var ringMap = new Dictionary<ulong, DetectedRing>();
        foreach (var login in parent.Keys)
        {
            var root = Find(login);
            if (!ringMap.TryGetValue(root, out var ring))
            {
                ring = new DetectedRing();
                ringMap[root] = ring;
            }
            ring.MemberLogins.Add(login);
        }

        // Enrich and score rings, filter to 2+ members
        var rings = new List<DetectedRing>();
        foreach (var ring in ringMap.Values)
        {
            if (ring.MemberLogins.Count < 2) continue;

            EnrichRing(ring);
            rings.Add(ring);
        }

        return rings;
    }

    /// <summary>
    /// Enriches a ring with correlation counts, shared ExpertIDs, and confidence score.
    /// </summary>
    private void EnrichRing(DetectedRing ring)
    {
        var members = ring.MemberLogins;
        var totalPairs = 0;

        // Sum pair counts within this ring
        foreach (var ((a, b), count) in _pairCounts)
        {
            if (members.Contains(a) && members.Contains(b))
            {
                totalPairs += count;
            }
        }
        ring.CorrelationCount = totalPairs;

        // Find shared ExpertIDs across ring members
        HashSet<ulong>? sharedIds = null;
        foreach (var login in members)
        {
            if (_expertIdsByLogin.TryGetValue(login, out var ids))
            {
                var nonZeroIds = new HashSet<ulong>(ids.Where(id => id != 0));
                if (nonZeroIds.Count == 0) continue;

                if (sharedIds == null)
                    sharedIds = new HashSet<ulong>(nonZeroIds);
                else
                    sharedIds.IntersectWith(nonZeroIds);
            }
        }
        ring.SharedExpertIds = sharedIds ?? new HashSet<ulong>();

        // Calculate confidence score
        var confidence = 0.0;
        var maxPossiblePairs = members.Count * (members.Count - 1) / 2;
        if (maxPossiblePairs > 0)
        {
            var density = (double)totalPairs / (maxPossiblePairs * _minPairsForRing);
            confidence = Math.Min(density * 0.5, 0.5); // Up to 50% from density
        }

        if (ring.SharedExpertIds.Count > 0)
            confidence += 0.2; // +20% for shared EAs

        if (members.Count >= 3)
            confidence += 0.15; // +15% for 3+ members

        if (totalPairs > _minPairsForRing * members.Count)
            confidence += 0.15; // +15% for high correlation volume

        ring.ConfidenceScore = Math.Min(confidence, 1.0);
    }

    /// <summary>
    /// Tracks ExpertIDs used by each login for ring enrichment.
    /// </summary>
    private void TrackExpertId(ulong login, ulong expertId)
    {
        if (!_expertIdsByLogin.TryGetValue(login, out var ids))
        {
            ids = new HashSet<ulong>();
            _expertIdsByLogin[login] = ids;
        }
        ids.Add(expertId);
    }

    /// <summary>
    /// Creates a canonical pair key with the smaller login first.
    /// </summary>
    private static (ulong, ulong) MakePairKey(ulong a, ulong b)
        => a < b ? (a, b) : (b, a);
}
