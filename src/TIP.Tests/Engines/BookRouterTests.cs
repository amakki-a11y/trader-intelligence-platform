using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Engines;
using TIP.Core.Models;

namespace TIP.Tests.Engines;

/// <summary>
/// Tests for BookRouter — verifies book routing recommendations based on risk signals.
/// </summary>
[TestClass]
public class BookRouterTests
{
    private BookRouter CreateRouter() => new();
    private StyleClassifier CreateClassifier() => new();

    private AccountAnalysis CreateAccount(
        double abuseScore = 20,
        bool isRingMember = false,
        int ringCorrelationCount = 0,
        double expertTradeRatio = 0.1,
        double timingEntropyCV = 2.0,
        double totalProfit = -500,
        int totalTrades = 50,
        double totalVolume = 10)
    {
        return new AccountAnalysis
        {
            Login = 1001,
            AbuseScore = abuseScore,
            RiskLevel = abuseScore >= 70 ? RiskLevel.Critical : abuseScore >= 50 ? RiskLevel.High : abuseScore >= 30 ? RiskLevel.Medium : RiskLevel.Low,
            IsRingMember = isRingMember,
            RingCorrelationCount = ringCorrelationCount,
            ExpertTradeRatio = expertTradeRatio,
            TimingEntropyCV = timingEntropyCV,
            TotalProfit = totalProfit,
            TotalTrades = totalTrades,
            TotalVolume = totalVolume,
            AvgHoldSeconds = 1800,
            TradesPerHour = 3
        };
    }

    [TestMethod]
    public void Route_CriticalScore_ReturnsABook()
    {
        var router = CreateRouter();
        var classifier = CreateClassifier();
        var account = CreateAccount(abuseScore: 85);
        var style = classifier.Classify(account);

        var result = router.Route(account, style);

        Assert.AreEqual(BookRouting.ABook, result.Recommendation, "Critical score should route A-Book");
        Assert.IsTrue(result.RiskFlags.Any(f => f.Contains("CRITICAL")), "Should flag critical score");
    }

    [TestMethod]
    public void Route_RingMember_ReturnsABookRegardless()
    {
        var router = CreateRouter();
        var classifier = CreateClassifier();
        // Ring member with moderate score and break-even P&L — ring signal should dominate
        var account = CreateAccount(abuseScore: 30, isRingMember: true, ringCorrelationCount: 10, totalProfit: 0);
        var style = classifier.Classify(account);

        var result = router.Route(account, style);

        Assert.AreEqual(BookRouting.ABook, result.Recommendation,
            $"Ring member should always route A-Book, got {result.Recommendation} (flags: {string.Join(", ", result.RiskFlags)})");
        Assert.IsTrue(result.RiskFlags.Any(f => f.Contains("Ring")), "Should flag ring membership");
    }

    [TestMethod]
    public void Route_CleanManualRetail_ReturnsBBook()
    {
        var router = CreateRouter();
        var classifier = CreateClassifier();
        var account = CreateAccount(abuseScore: 10, expertTradeRatio: 0.02, timingEntropyCV: 2.5, totalProfit: -200);
        var style = classifier.Classify(account);

        var result = router.Route(account, style);

        Assert.AreEqual(BookRouting.BBook, result.Recommendation, "Clean manual retail trader should route B-Book");
    }

    [TestMethod]
    public void Route_MediumRisk_ReturnsHybrid()
    {
        var router = CreateRouter();
        var classifier = CreateClassifier();
        var account = CreateAccount(abuseScore: 40, expertTradeRatio: 0.5, totalProfit: 100, totalTrades: 20);
        var style = classifier.Classify(account);

        var result = router.Route(account, style);

        Assert.AreEqual(BookRouting.Hybrid, result.Recommendation,
            $"Medium risk should route Hybrid, got {result.Recommendation} (flags: {string.Join(", ", result.RiskFlags)})");
    }

    [TestMethod]
    public void Route_HFTBot_ReturnsABook()
    {
        var router = CreateRouter();
        var classifier = CreateClassifier();
        var account = CreateAccount(abuseScore: 60, expertTradeRatio: 0.98, timingEntropyCV: 0.05);
        account.TradesPerHour = 50;
        account.AvgHoldSeconds = 10;
        account.ScalpCount = 45;
        account.UniqueExpertIds = 2;
        var style = classifier.Classify(account);

        var result = router.Route(account, style);

        Assert.AreEqual(BookRouting.ABook, result.Recommendation, "HFT bot should route A-Book");
    }

    [TestMethod]
    public void Route_SummaryNonEmpty()
    {
        var router = CreateRouter();
        var classifier = CreateClassifier();
        var account = CreateAccount();
        var style = classifier.Classify(account);

        var result = router.Route(account, style);

        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Summary), "Summary should not be empty");
        Assert.IsTrue(result.Summary.Contains("Recommended"), "Summary should contain routing recommendation");
    }

    [TestMethod]
    public void Route_ConfidenceBetweenZeroAndOne()
    {
        var router = CreateRouter();
        var classifier = CreateClassifier();
        var account = CreateAccount();
        var style = classifier.Classify(account);

        var result = router.Route(account, style);

        Assert.IsTrue(result.Confidence >= 0.0 && result.Confidence <= 1.0,
            $"Confidence should be 0-1, got {result.Confidence}");
    }

    [TestMethod]
    public void Route_RiskFlagsPopulatedWhenConcerned()
    {
        var router = CreateRouter();
        var classifier = CreateClassifier();
        var account = CreateAccount(abuseScore: 75, isRingMember: true, expertTradeRatio: 0.95);
        account.TimingEntropyCV = 0.05;
        account.AvgHoldSeconds = 10;
        account.TradesPerHour = 50;
        account.ScalpCount = 45;
        account.UniqueExpertIds = 2;
        var style = classifier.Classify(account);

        var result = router.Route(account, style);

        Assert.IsTrue(result.RiskFlags.Count >= 2, $"Should have multiple risk flags, got {result.RiskFlags.Count}");
    }
}
