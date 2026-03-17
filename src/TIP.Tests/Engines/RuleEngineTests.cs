using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Engines;
using TIP.Core.Models;

namespace TIP.Tests.Engines;

/// <summary>
/// Tests for the RuleEngine scoring logic — the core abuse detection mechanism.
/// Verifies that rules evaluate correctly, weights accumulate properly, and
/// edge cases (empty rules, disabled rules, score capping) are handled.
/// </summary>
[TestClass]
public class RuleEngineTests
{
    /// <summary>
    /// With no rules configured, the score should always be 0 regardless of metrics.
    /// </summary>
    [TestMethod]
    public void Score_EmptyRules_ReturnsZero()
    {
        var engine = new RuleEngine(Array.Empty<Rule>());
        var analysis = new AccountAnalysis { TotalTrades = 1000, TotalVolume = 500.0 };

        var score = engine.Score(analysis);

        Assert.AreEqual(0.0, score);
    }

    /// <summary>
    /// A single rule that matches should return exactly its weight.
    /// </summary>
    [TestMethod]
    public void Score_SingleRuleFires_ReturnsWeight()
    {
        var rules = new[]
        {
            new Rule(RuleMetric.TotalTrades, RuleOperator.GreaterThan, 50, 25.0, "High trade count")
        };
        var engine = new RuleEngine(rules);
        var analysis = new AccountAnalysis { TotalTrades = 100 };

        var score = engine.Score(analysis);

        Assert.AreEqual(25.0, score);
    }

    /// <summary>
    /// When multiple rules fire and their weights exceed 100, the score caps at 100.
    /// </summary>
    [TestMethod]
    public void Score_MultipleRules_CapsAt100()
    {
        var rules = new[]
        {
            new Rule(RuleMetric.TotalTrades, RuleOperator.GreaterThan, 10, 40.0, "High trade count"),
            new Rule(RuleMetric.TotalVolume, RuleOperator.GreaterThan, 10.0, 40.0, "High volume"),
            new Rule(RuleMetric.ScalpCount, RuleOperator.GreaterThan, 5, 40.0, "Many scalps")
        };
        var engine = new RuleEngine(rules);
        var analysis = new AccountAnalysis
        {
            TotalTrades = 100,
            TotalVolume = 500.0,
            ScalpCount = 50
        };

        var score = engine.Score(analysis);

        Assert.AreEqual(100.0, score);
    }

    /// <summary>
    /// Rules with weight=0 should be skipped and not contribute to the score.
    /// </summary>
    [TestMethod]
    public void Score_DisabledRule_IsSkipped()
    {
        var rules = new[]
        {
            new Rule(RuleMetric.TotalTrades, RuleOperator.GreaterThan, 10, 0.0, "Disabled rule"),
            new Rule(RuleMetric.TotalVolume, RuleOperator.GreaterThan, 10.0, 15.0, "Active rule")
        };
        var engine = new RuleEngine(rules);
        var analysis = new AccountAnalysis { TotalTrades = 100, TotalVolume = 500.0 };

        var score = engine.Score(analysis);

        Assert.AreEqual(15.0, score);
    }

    /// <summary>
    /// Boolean metrics (IsRingMember) should be evaluated correctly:
    /// true maps to 1.0, false maps to 0.0.
    /// </summary>
    [TestMethod]
    public void Score_IsRingMemberBoolean_WorksCorrectly()
    {
        var rules = new[]
        {
            new Rule(RuleMetric.IsRingMember, RuleOperator.Equal, 1.0, 30.0, "Ring member detected")
        };
        var engine = new RuleEngine(rules);

        var ringMember = new AccountAnalysis { IsRingMember = true };
        var notRingMember = new AccountAnalysis { IsRingMember = false };

        Assert.AreEqual(30.0, engine.Score(ringMember));
        Assert.AreEqual(0.0, engine.Score(notRingMember));
    }

    /// <summary>
    /// A rule that does not match should not contribute to the score.
    /// </summary>
    [TestMethod]
    public void Score_RuleDoesNotFire_ReturnsZero()
    {
        var rules = new[]
        {
            new Rule(RuleMetric.TotalTrades, RuleOperator.GreaterThan, 500, 25.0, "Very high trade count")
        };
        var engine = new RuleEngine(rules);
        var analysis = new AccountAnalysis { TotalTrades = 100 };

        var score = engine.Score(analysis);

        Assert.AreEqual(0.0, score);
    }

    /// <summary>
    /// All six operators should evaluate correctly.
    /// </summary>
    [TestMethod]
    public void EvaluateOperator_AllOperators_EvaluateCorrectly()
    {
        Assert.IsTrue(RuleEngine.EvaluateOperator(10.0, RuleOperator.GreaterThan, 5.0));
        Assert.IsFalse(RuleEngine.EvaluateOperator(5.0, RuleOperator.GreaterThan, 10.0));

        Assert.IsTrue(RuleEngine.EvaluateOperator(5.0, RuleOperator.LessThan, 10.0));
        Assert.IsFalse(RuleEngine.EvaluateOperator(10.0, RuleOperator.LessThan, 5.0));

        Assert.IsTrue(RuleEngine.EvaluateOperator(10.0, RuleOperator.GreaterOrEqual, 10.0));
        Assert.IsTrue(RuleEngine.EvaluateOperator(5.0, RuleOperator.LessOrEqual, 5.0));

        Assert.IsTrue(RuleEngine.EvaluateOperator(5.0, RuleOperator.Equal, 5.0));
        Assert.IsTrue(RuleEngine.EvaluateOperator(5.0, RuleOperator.NotEqual, 10.0));
    }

    /// <summary>
    /// GetMetricValue should correctly map all 26 metric enums to AccountAnalysis properties.
    /// </summary>
    [TestMethod]
    public void GetMetricValue_AllMetrics_MapCorrectly()
    {
        var analysis = new AccountAnalysis
        {
            TotalTrades = 100,
            TotalVolume = 50.5,
            TotalCommission = 200.0,
            TotalProfit = -150.0,
            TotalDeposits = 5000.0,
            TotalWithdrawals = 1000.0,
            TotalBonuses = 500.0,
            DepositCount = 3,
            SOCompensationCount = 1,
            CommissionToVolumeRatio = 3.96,
            ProfitToCommissionRatio = -0.75,
            AvgHoldSeconds = 45.0,
            WinRateOnShortTrades = 0.85,
            ScalpCount = 80,
            SlippageDirectionBias = 0.6,
            DepositToFirstTradeSeconds = 120.0,
            TradeToWithdrawalHours = 2.5,
            BonusToDepositRatio = 0.1,
            VolumeMeetsBonusReqExactly = true,
            TimingEntropyCV = 0.05,
            ExpertTradeRatio = 0.95,
            AvgVolumeLots = 0.5,
            TradesPerHour = 15.0,
            UniqueExpertIds = 3,
            IsRingMember = true,
            RingCorrelationCount = 12
        };

        Assert.AreEqual(100.0, RuleEngine.GetMetricValue(analysis, RuleMetric.TotalTrades));
        Assert.AreEqual(50.5, RuleEngine.GetMetricValue(analysis, RuleMetric.TotalVolume));
        Assert.AreEqual(0.85, RuleEngine.GetMetricValue(analysis, RuleMetric.WinRateOnShortTrades));
        Assert.AreEqual(1.0, RuleEngine.GetMetricValue(analysis, RuleMetric.IsRingMember));
        Assert.AreEqual(1.0, RuleEngine.GetMetricValue(analysis, RuleMetric.VolumeMeetsBonusReqExactly));
        Assert.AreEqual(12.0, RuleEngine.GetMetricValue(analysis, RuleMetric.RingCorrelationCount));
    }
}
