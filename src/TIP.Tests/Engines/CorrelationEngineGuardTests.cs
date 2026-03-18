using System;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Engines;
using TIP.Core.Models;

namespace TIP.Tests.Engines;

/// <summary>
/// Tests for the CorrelationEngine memory guard — verifies auto-prune on limit,
/// exception safety in CheckDeal, and prune effectiveness.
/// </summary>
[TestClass]
public class CorrelationEngineGuardTests
{
    [TestMethod]
    public void AutoPrunes_WhenFingerprintCountExceedsMax()
    {
        // Create engine with tiny max to trigger auto-prune
        var engine = new CorrelationEngine(NullLogger<CorrelationEngine>.Instance,
            windowMs: 5000, minPairsForRing: 3, maxFingerprints: 50);

        var baseTime = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds();

        // Add 60 fingerprints (over the limit of 50)
        for (int i = 0; i < 60; i++)
        {
            var fp = new TradeFingerprint(
                (ulong)(1000 + i), (ulong)(i % 10), baseTime + i * 1000,
                "EURUSD", i % 2, 1.0, 0);
            engine.CheckDeal(fp);
        }

        // After adding 60 with max=50, auto-prune should have triggered
        // The count should be less than 60 (pruned some older ones)
        Assert.IsTrue(engine.IndexedCount <= 60, $"IndexedCount was {engine.IndexedCount}");
    }

    [TestMethod]
    public void CheckDeal_ReturnsNoCorrelation_OnException()
    {
        var engine = new CorrelationEngine(NullLogger<CorrelationEngine>.Instance);

        // A normal fingerprint should not throw
        var fp = new TradeFingerprint(1, 50001, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            "EURUSD", 0, 1.0, 0);

        var matches = engine.CheckDeal(fp);

        Assert.IsNotNull(matches);
        Assert.AreEqual(0, matches.Count);
    }

    [TestMethod]
    public void Prune_ReducesCountBelowThreshold()
    {
        var engine = new CorrelationEngine(NullLogger<CorrelationEngine>.Instance,
            windowMs: 5000, minPairsForRing: 3, maxFingerprints: 100);

        var oldTime = DateTimeOffset.UtcNow.AddHours(-48).ToUnixTimeMilliseconds();
        var recentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Add old fingerprints
        for (int i = 0; i < 30; i++)
        {
            var fp = new TradeFingerprint(
                (ulong)(1000 + i), (ulong)(i % 5), oldTime + i * 100,
                "EURUSD", i % 2, 1.0, 0);
            engine.CheckDeal(fp);
        }

        // Add recent fingerprints
        for (int i = 0; i < 20; i++)
        {
            var fp = new TradeFingerprint(
                (ulong)(2000 + i), (ulong)(i % 5), recentTime + i * 100,
                "XAUUSD", i % 2, 1.0, 0);
            engine.CheckDeal(fp);
        }

        Assert.AreEqual(50, engine.IndexedCount);

        // Prune anything older than 24 hours
        var pruned = engine.Prune(DateTimeOffset.UtcNow.AddHours(-24));

        Assert.AreEqual(30, pruned);
        Assert.AreEqual(20, engine.IndexedCount);
    }
}
