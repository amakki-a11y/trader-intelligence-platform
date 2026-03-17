using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Engines;
using TIP.Core.Models;

namespace TIP.Tests.Engines;

/// <summary>
/// Tests for CorrelationEngine ring detection algorithm.
/// Verifies bucket indexing, pair matching, ring clustering, scoring, and live check.
/// </summary>
[TestClass]
public class CorrelationEngineTests
{
    private CorrelationEngine CreateEngine(int windowMs = 5000, int minPairs = 3)
        => new(NullLogger<CorrelationEngine>.Instance, windowMs, minPairs);

    [TestMethod]
    public void IndexFingerprints_GroupsIntoBuckets()
    {
        var engine = CreateEngine();
        var fingerprints = new List<TradeFingerprint>
        {
            new(1, 100, 10000, "EURUSD", 0, 1.0, 0),
            new(2, 200, 10001, "EURUSD", 1, 1.0, 0),
            new(3, 300, 20000, "EURUSD", 0, 1.0, 0), // Different bucket
        };

        engine.IndexFingerprints(fingerprints);

        Assert.AreEqual(3, engine.IndexedCount);
        Assert.AreEqual(2, engine.BucketCount); // 10000/5000=2 and 20000/5000=4
    }

    [TestMethod]
    public void AnalyzeRings_OppositeDirections_FindsPairs()
    {
        var engine = CreateEngine(windowMs: 5000, minPairs: 1);
        var fingerprints = new List<TradeFingerprint>
        {
            new(1, 100, 10000, "EURUSD", 0, 1.0, 0), // BUY
            new(2, 200, 10001, "EURUSD", 1, 1.0, 0), // SELL — correlated
        };

        engine.IndexFingerprints(fingerprints);
        var rings = engine.AnalyzeRings();

        Assert.AreEqual(1, rings.Count);
        Assert.IsTrue(rings[0].MemberLogins.Contains(100UL));
        Assert.IsTrue(rings[0].MemberLogins.Contains(200UL));
    }

    [TestMethod]
    public void AnalyzeRings_SameDirection_NoPairs()
    {
        var engine = CreateEngine(windowMs: 5000, minPairs: 1);
        var fingerprints = new List<TradeFingerprint>
        {
            new(1, 100, 10000, "EURUSD", 0, 1.0, 0),
            new(2, 200, 10001, "EURUSD", 0, 1.0, 0), // Same direction — not correlated
        };

        engine.IndexFingerprints(fingerprints);
        var rings = engine.AnalyzeRings();

        Assert.AreEqual(0, rings.Count);
    }

    [TestMethod]
    public void AnalyzeRings_SameLogin_NoPairs()
    {
        var engine = CreateEngine(windowMs: 5000, minPairs: 1);
        var fingerprints = new List<TradeFingerprint>
        {
            new(1, 100, 10000, "EURUSD", 0, 1.0, 0),
            new(2, 100, 10001, "EURUSD", 1, 1.0, 0), // Same login — not correlated
        };

        engine.IndexFingerprints(fingerprints);
        var rings = engine.AnalyzeRings();

        Assert.AreEqual(0, rings.Count);
    }

    [TestMethod]
    public void AnalyzeRings_BelowMinPairs_NoRing()
    {
        var engine = CreateEngine(windowMs: 5000, minPairs: 3);
        var fingerprints = new List<TradeFingerprint>
        {
            // Only 2 correlated pairs — below threshold of 3
            new(1, 100, 10000, "EURUSD", 0, 1.0, 0),
            new(2, 200, 10001, "EURUSD", 1, 1.0, 0),
            new(3, 100, 20000, "EURUSD", 0, 1.0, 0),
            new(4, 200, 20001, "EURUSD", 1, 1.0, 0),
        };

        engine.IndexFingerprints(fingerprints);
        var rings = engine.AnalyzeRings();

        Assert.AreEqual(0, rings.Count);
    }

    [TestMethod]
    public void AnalyzeRings_ThreeMembers_GetsConfidenceBoost()
    {
        var engine = CreateEngine(windowMs: 5000, minPairs: 1);
        // 3 accounts trading together
        var fingerprints = new List<TradeFingerprint>
        {
            new(1, 100, 10000, "EURUSD", 0, 1.0, 0),
            new(2, 200, 10001, "EURUSD", 1, 1.0, 0),
            new(3, 300, 10002, "EURUSD", 1, 1.0, 0),
        };

        engine.IndexFingerprints(fingerprints);
        var rings = engine.AnalyzeRings();

        Assert.IsTrue(rings.Count >= 1);
        var ring = rings[0];
        Assert.IsTrue(ring.MemberLogins.Count >= 2);
        Assert.IsTrue(ring.ConfidenceScore > 0);
    }

    [TestMethod]
    public void AnalyzeRings_SharedExpertIds_IncreasesConfidence()
    {
        var engine = CreateEngine(windowMs: 5000, minPairs: 1);
        var sharedEa = 12345UL;
        var fingerprints = new List<TradeFingerprint>
        {
            new(1, 100, 10000, "EURUSD", 0, 1.0, sharedEa),
            new(2, 200, 10001, "EURUSD", 1, 1.0, sharedEa),
        };

        engine.IndexFingerprints(fingerprints);
        var rings = engine.AnalyzeRings();

        Assert.AreEqual(1, rings.Count);
        Assert.IsTrue(rings[0].SharedExpertIds.Contains(sharedEa));
        Assert.IsTrue(rings[0].ConfidenceScore >= 0.2); // At least the EA bonus
    }

    [TestMethod]
    public void CheckDeal_LiveMode_FindsCorrelation()
    {
        var engine = CreateEngine();

        // Index an existing trade
        engine.IndexFingerprints(new List<TradeFingerprint>
        {
            new(1, 100, 10000, "EURUSD", 0, 1.0, 0)
        });

        // Check a new opposing trade
        var matches = engine.CheckDeal(new TradeFingerprint(2, 200, 10001, "EURUSD", 1, 1.0, 0));

        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual(200UL, matches[0].LoginA);
        Assert.AreEqual(100UL, matches[0].LoginB);
        Assert.AreEqual(1L, matches[0].TimeDifferenceMs);
    }

    [TestMethod]
    public void CheckDeal_AdjacentBucket_StillFindsMatch()
    {
        var engine = CreateEngine(windowMs: 5000);

        // Trade at bucket boundary: 4999ms (bucket 0)
        engine.IndexFingerprints(new List<TradeFingerprint>
        {
            new(1, 100, 4999, "EURUSD", 0, 1.0, 0)
        });

        // Trade at 5001ms (bucket 1) — adjacent bucket
        var matches = engine.CheckDeal(new TradeFingerprint(2, 200, 5001, "EURUSD", 1, 1.0, 0));

        Assert.AreEqual(1, matches.Count);
    }

    [TestMethod]
    public void Prune_RemovesOldFingerprints()
    {
        var engine = CreateEngine();
        var now = DateTimeOffset.UtcNow;
        var oldTime = now.AddHours(-2).ToUnixTimeMilliseconds();
        var recentTime = now.ToUnixTimeMilliseconds();

        engine.IndexFingerprints(new List<TradeFingerprint>
        {
            new(1, 100, oldTime, "EURUSD", 0, 1.0, 0),
            new(2, 200, recentTime, "EURUSD", 1, 1.0, 0),
        });

        Assert.AreEqual(2, engine.IndexedCount);

        var pruned = engine.Prune(now.AddHours(-1));

        Assert.AreEqual(1, pruned);
        Assert.AreEqual(1, engine.IndexedCount);
    }
}
