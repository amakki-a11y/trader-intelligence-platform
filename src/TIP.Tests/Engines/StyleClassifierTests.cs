using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Engines;
using TIP.Core.Models;

namespace TIP.Tests.Engines;

/// <summary>
/// Tests for StyleClassifier — verifies trading style classification from account metrics.
/// </summary>
[TestClass]
public class StyleClassifierTests
{
    private StyleClassifier CreateClassifier() => new();

    private AccountAnalysis CreateAccount(
        int totalTrades = 100,
        double avgHoldSeconds = 30,
        double tradesPerHour = 25,
        int scalpCount = 80,
        double expertTradeRatio = 0,
        double timingEntropyCV = 2.0,
        int uniqueExpertIds = 0)
    {
        return new AccountAnalysis
        {
            Login = 1001,
            TotalTrades = totalTrades,
            AvgHoldSeconds = avgHoldSeconds,
            TradesPerHour = tradesPerHour,
            ScalpCount = scalpCount,
            ExpertTradeRatio = expertTradeRatio,
            TimingEntropyCV = timingEntropyCV,
            UniqueExpertIds = uniqueExpertIds
        };
    }

    [TestMethod]
    public void Classify_HighFrequencyShortHold_ReturnsScalper()
    {
        var classifier = CreateClassifier();
        var account = CreateAccount(avgHoldSeconds: 20, tradesPerHour: 30, scalpCount: 90);

        var result = classifier.Classify(account);

        Assert.AreEqual(TradingStyle.Scalper, result.Style, "High frequency short hold should classify as Scalper");
        Assert.IsTrue(result.Confidence > 0.3, "Scalper confidence should be meaningful");
        Assert.IsTrue(result.Signals.Any(s => s.Contains("Scalper")), "Should have Scalper signal");
    }

    [TestMethod]
    public void Classify_NearZeroEntropyHighExpert_ReturnsEA()
    {
        var classifier = CreateClassifier();
        var account = CreateAccount(
            avgHoldSeconds: 600,
            tradesPerHour: 5,
            scalpCount: 10,
            expertTradeRatio: 0.95,
            timingEntropyCV: 0.05,
            uniqueExpertIds: 3);

        var result = classifier.Classify(account);

        Assert.AreEqual(TradingStyle.EA, result.Style, "Low entropy + high expert ratio should classify as EA");
        Assert.IsTrue(result.Signals.Any(s => s.Contains("EA") || s.Contains("robotic")), "Should have EA/robotic signal");
    }

    [TestMethod]
    public void Classify_LongHoldTimes_ReturnsSwing()
    {
        var classifier = CreateClassifier();
        var account = CreateAccount(
            avgHoldSeconds: 172800, // 2 days
            tradesPerHour: 0.5,
            scalpCount: 0,
            expertTradeRatio: 0.1,
            timingEntropyCV: 2.5);

        var result = classifier.Classify(account);

        Assert.AreEqual(TradingStyle.Swing, result.Style, "Long hold times should classify as Swing");
        Assert.IsTrue(result.Signals.Any(s => s.Contains("Swing") || s.Contains("24h")), "Should have Swing signal");
    }

    [TestMethod]
    public void Classify_LowExpertHighEntropy_ReturnsManual()
    {
        var classifier = CreateClassifier();
        // Use hold time outside DayTrader range (>4h) and low frequency to avoid DayTrader/Swing signals
        var account = CreateAccount(
            avgHoldSeconds: 50000, // ~14 hours — between DayTrader (4h) and Swing (24h)
            tradesPerHour: 0.3,
            scalpCount: 2,
            expertTradeRatio: 0.02,
            timingEntropyCV: 2.5);

        var result = classifier.Classify(account);

        Assert.AreEqual(TradingStyle.Manual, result.Style,
            $"Low expert ratio + high entropy should classify as Manual, got {result.Style} (signals: {string.Join(", ", result.Signals)})");
    }

    [TestMethod]
    public void Classify_ConfidenceCalculation_BetweenZeroAndOne()
    {
        var classifier = CreateClassifier();
        var account = CreateAccount(avgHoldSeconds: 20, tradesPerHour: 30, scalpCount: 90);

        var result = classifier.Classify(account);

        Assert.IsTrue(result.Confidence >= 0.0 && result.Confidence <= 1.0,
            $"Confidence should be 0-1, got {result.Confidence}");
    }

    [TestMethod]
    public void Classify_SignalsPopulated_NonEmpty()
    {
        var classifier = CreateClassifier();
        var account = CreateAccount(avgHoldSeconds: 20, tradesPerHour: 30, scalpCount: 90);

        var result = classifier.Classify(account);

        Assert.IsTrue(result.Signals.Count > 0, "Signals list should not be empty for active account");
    }

    [TestMethod]
    public void Classify_ZeroTrades_ReturnsUnknown()
    {
        var classifier = CreateClassifier();
        var account = new AccountAnalysis { Login = 1001, TotalTrades = 0 };

        var result = classifier.Classify(account);

        Assert.AreEqual(TradingStyle.Unknown, result.Style, "Zero trades should classify as Unknown");
        Assert.AreEqual(0.0, result.Confidence, "Unknown style should have zero confidence");
    }

    [TestMethod]
    public void Classify_AllMetricsZero_ReturnsUnknown()
    {
        var classifier = CreateClassifier();
        var account = new AccountAnalysis
        {
            Login = 1001,
            TotalTrades = 0,
            AvgHoldSeconds = 0,
            TradesPerHour = 0,
            ScalpCount = 0,
            ExpertTradeRatio = 0,
            TimingEntropyCV = 0
        };

        var result = classifier.Classify(account);

        Assert.AreEqual(TradingStyle.Unknown, result.Style, "All-zero metrics should classify as Unknown");
    }
}
