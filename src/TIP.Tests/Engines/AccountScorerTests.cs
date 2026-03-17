using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Engines;
using TIP.Core.Models;

namespace TIP.Tests.Engines;

/// <summary>
/// Tests for AccountScorer incremental scoring and risk classification.
/// Verifies deal processing, metric calculation, risk levels, and ring detection.
/// </summary>
[TestClass]
public class AccountScorerTests
{
    private AccountScorer CreateScorer(IEnumerable<Rule>? rules = null)
    {
        var ruleEngine = new RuleEngine(rules ?? new[]
        {
            new Rule(RuleMetric.TotalTrades, RuleOperator.GreaterThan, 5, 25, "Many trades"),
            new Rule(RuleMetric.IsRingMember, RuleOperator.Equal, 1.0, 20, "Ring member"),
            new Rule(RuleMetric.TimingEntropyCV, RuleOperator.LessThan, 0.1, 30, "Bot timing"),
            new Rule(RuleMetric.ExpertTradeRatio, RuleOperator.GreaterThan, 0.9, 15, "High EA ratio"),
        });
        var correlationEngine = new CorrelationEngine(NullLogger<CorrelationEngine>.Instance);
        return new AccountScorer(ruleEngine, correlationEngine, NullLogger<AccountScorer>.Instance);
    }

    [TestMethod]
    public void ProcessDeal_BuyDeal_UpdatesTradeMetrics()
    {
        var scorer = CreateScorer();

        var result = scorer.ProcessDeal(
            dealId: 1, login: 100, action: 0, volume: 1.5, profit: 0,
            commission: -3.0, swap: 0, expertId: 0, reason: 0,
            timeMsc: 1000000, symbol: "EURUSD", positionId: 500);

        Assert.AreEqual(1, result.TotalTrades);
        Assert.AreEqual(1.5, result.TotalVolume);
        Assert.AreEqual(3.0, result.TotalCommission);
    }

    [TestMethod]
    public void ProcessDeal_BalanceDeal_UpdatesDeposit()
    {
        var scorer = CreateScorer();

        var result = scorer.ProcessDeal(
            dealId: 2, login: 100, action: 2, volume: 0, profit: 1000.0,
            commission: 0, swap: 0, expertId: 0, reason: 0,
            timeMsc: 1000000, symbol: "", positionId: 0);

        Assert.AreEqual(0, result.TotalTrades);
        Assert.AreEqual(1000.0, result.TotalDeposits);
        Assert.AreEqual(1, result.DepositCount);
    }

    [TestMethod]
    public void ProcessDeal_BonusDeal_UpdatesBonuses()
    {
        var scorer = CreateScorer();

        var result = scorer.ProcessDeal(
            dealId: 3, login: 100, action: 6, volume: 0, profit: 500.0,
            commission: 0, swap: 0, expertId: 0, reason: 0,
            timeMsc: 1000000, symbol: "", positionId: 0);

        Assert.AreEqual(500.0, result.TotalBonuses);
    }

    [TestMethod]
    public void ProcessDeal_WithdrawalDeal_UpdatesWithdrawals()
    {
        var scorer = CreateScorer();

        var result = scorer.ProcessDeal(
            dealId: 4, login: 100, action: 2, volume: 0, profit: -200.0,
            commission: 0, swap: 0, expertId: 0, reason: 0,
            timeMsc: 1000000, symbol: "", positionId: 0);

        Assert.AreEqual(200.0, result.TotalWithdrawals);
    }

    [TestMethod]
    public void ProcessDeal_MultipleTrades_CalculatesRatios()
    {
        var scorer = CreateScorer();

        for (int i = 0; i < 5; i++)
        {
            scorer.ProcessDeal(
                dealId: (ulong)i, login: 100, action: 0, volume: 2.0, profit: 10.0,
                commission: -4.0, swap: 0, expertId: 0, reason: 0,
                timeMsc: 1000000 + i * 60000, symbol: "EURUSD", positionId: (ulong)(100 + i));
        }

        var account = scorer.GetAccount(100);
        Assert.IsNotNull(account);
        Assert.AreEqual(5, account.TotalTrades);
        Assert.AreEqual(10.0, account.TotalVolume);
        Assert.AreEqual(2.0, account.AvgVolumeLots);
        Assert.IsTrue(account.CommissionToVolumeRatio > 0);
    }

    [TestMethod]
    public void ProcessDeal_ExpertTradeTracking()
    {
        var scorer = CreateScorer();

        // 3 EA trades, 1 manual
        scorer.ProcessDeal(1, 100, 0, 1.0, 0, 0, 0, 999, 0, 1000000, "EURUSD", 1);
        scorer.ProcessDeal(2, 100, 0, 1.0, 0, 0, 0, 999, 0, 1060000, "EURUSD", 2);
        scorer.ProcessDeal(3, 100, 0, 1.0, 0, 0, 0, 888, 0, 1120000, "EURUSD", 3);
        scorer.ProcessDeal(4, 100, 0, 1.0, 0, 0, 0, 0, 0, 1180000, "EURUSD", 4);

        var account = scorer.GetAccount(100);
        Assert.IsNotNull(account);
        Assert.AreEqual(0.75, account.ExpertTradeRatio);
        Assert.AreEqual(2, account.UniqueExpertIds); // 999 and 888
    }

    [TestMethod]
    public void RiskLevel_CriticalAbove70()
    {
        var scorer = CreateScorer(new[]
        {
            new Rule(RuleMetric.TotalTrades, RuleOperator.GreaterThan, 0, 75, "Always fires high"),
        });

        var result = scorer.ProcessDeal(1, 100, 0, 1.0, 0, 0, 0, 0, 0, 1000000, "EURUSD", 1);

        Assert.AreEqual(RiskLevel.Critical, result.RiskLevel);
        Assert.IsTrue(result.AbuseScore >= 70);
    }

    [TestMethod]
    public void GetAllAccountsSorted_ReturnsByScoreDescending()
    {
        var scorer = CreateScorer();

        // Process deals for different accounts — more trades = higher chance of higher score
        for (int i = 0; i < 10; i++)
            scorer.ProcessDeal((ulong)i, 100, 0, 1.0, 0, 0, 0, 0, 0, 1000000 + i * 60000, "EURUSD", (ulong)(100 + i));

        scorer.ProcessDeal(20, 200, 0, 1.0, 0, 0, 0, 0, 0, 1000000, "EURUSD", 200);

        var all = scorer.GetAllAccountsSorted();
        Assert.AreEqual(2, all.Count);
        Assert.IsTrue(all[0].AbuseScore >= all[1].AbuseScore);
    }
}
