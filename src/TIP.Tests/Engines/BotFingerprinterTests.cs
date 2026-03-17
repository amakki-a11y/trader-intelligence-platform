using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Engines;
using TIP.Core.Models;

namespace TIP.Tests.Engines;

/// <summary>
/// Tests for BotFingerprinter analysis.
/// Verifies bot detection rules, timing entropy, EA clustering, and confidence scoring.
/// </summary>
[TestClass]
public class BotFingerprinterTests
{
    private BotFingerprinter CreateFingerprinter() => new();

    [TestMethod]
    public void Analyze_EmptyTrades_NotBot()
    {
        var fp = CreateFingerprinter();

        var result = fp.Analyze(100, new List<TradeFingerprint>());

        Assert.IsFalse(result.IsSuspectedBot);
        Assert.AreEqual(0.0, result.BotConfidence);
        Assert.AreEqual(1.0, result.TimingEntropyCV); // Default for no data
    }

    [TestMethod]
    public void Analyze_PreciseTiming_HighFrequency_DetectedAsBot()
    {
        var fp = CreateFingerprinter();

        // 100 trades at exactly 1000ms intervals → CV ≈ 0 and high TPH
        var trades = new List<TradeFingerprint>();
        for (int i = 0; i < 100; i++)
        {
            trades.Add(new TradeFingerprint((ulong)i, 100, 1000000 + i * 1000, "EURUSD", 0, 0.01, 999));
        }

        var result = fp.Analyze(100, trades);

        Assert.IsTrue(result.IsSuspectedBot);
        Assert.IsTrue(result.TimingEntropyCV < 0.1);
        Assert.IsTrue(result.TradesPerHour > 20);
        Assert.IsTrue(result.BotConfidence > 0.5);
    }

    [TestMethod]
    public void Analyze_HighEARatio_HighFrequency_DetectedAsBot()
    {
        var fp = CreateFingerprinter();

        // All trades from same EA at 500ms intervals
        var trades = new List<TradeFingerprint>();
        for (int i = 0; i < 200; i++)
        {
            trades.Add(new TradeFingerprint((ulong)i, 100, 1000000 + i * 500, "EURUSD", i % 2, 1.0, 12345));
        }

        var result = fp.Analyze(100, trades);

        Assert.AreEqual(1.0, result.ExpertTradeRatio);
        Assert.AreEqual(1, result.UniqueExpertIds);
        Assert.IsTrue(result.TradesPerHour > 30);
        Assert.IsTrue(result.IsSuspectedBot);
    }

    [TestMethod]
    public void Analyze_MicroLotFarming_LowVariance_DetectedAsBot()
    {
        var fp = CreateFingerprinter();

        // All trades exactly 0.01 lots → CV ≈ 0
        var trades = new List<TradeFingerprint>();
        for (int i = 0; i < 50; i++)
        {
            trades.Add(new TradeFingerprint((ulong)i, 100, 1000000 + i * 100, "EURUSD", 0, 0.01, 0));
        }

        var result = fp.Analyze(100, trades);

        Assert.AreEqual(0.01, result.AvgVolumeLots, 0.001);
        Assert.IsTrue(result.VolumeVarianceCV < 0.05);
        Assert.IsTrue(result.IsSuspectedBot);
    }

    [TestMethod]
    public void Analyze_HumanTrading_NotDetectedAsBot()
    {
        var fp = CreateFingerprinter();
        var rng = new Random(42);

        // Random intervals between 5-120 seconds, varying volumes
        var trades = new List<TradeFingerprint>();
        long time = 1000000;
        for (int i = 0; i < 20; i++)
        {
            time += rng.Next(5000, 120000);
            var volume = Math.Round(rng.NextDouble() * 5 + 0.1, 2);
            trades.Add(new TradeFingerprint((ulong)i, 100, time, "EURUSD", i % 2, volume, 0));
        }

        var result = fp.Analyze(100, trades);

        Assert.IsFalse(result.IsSuspectedBot);
        Assert.IsTrue(result.TimingEntropyCV > 0.1);
        Assert.AreEqual(0.0, result.ExpertTradeRatio);
    }
}
