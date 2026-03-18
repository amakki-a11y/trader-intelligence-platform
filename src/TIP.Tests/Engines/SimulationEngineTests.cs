using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Engines;
using TIP.Core.Models;

namespace TIP.Tests.Engines;

/// <summary>
/// Tests for SimulationEngine — verifies A-Book, B-Book, and Hybrid routing P&amp;L simulations.
/// </summary>
[TestClass]
public class SimulationEngineTests
{
    private SimulationEngine CreateEngine() => new();

    private List<DealRecord> CreateDeals(double profit, int count = 10)
    {
        var deals = new List<DealRecord>();
        for (int i = 0; i < count; i++)
        {
            deals.Add(new DealRecord
            {
                DealId = (ulong)(1000 + i),
                Login = 1001,
                TimeMsc = 1700000000000L + i * 60000L,
                Symbol = "EURUSD",
                Action = i % 2 == 0 ? 0 : 1, // Alternating BUY/SELL
                Volume = 0.1,
                Price = 1.08500 + i * 0.0001,
                Profit = profit / count,
                Commission = -0.7,
            });
        }
        return deals;
    }

    [TestMethod]
    public void SimulateBBook_LosingClient_PositiveBrokerPnL()
    {
        var engine = CreateEngine();
        var deals = CreateDeals(profit: -500, count: 10); // Client loses $500

        var result = engine.SimulateBBook(deals);

        Assert.IsTrue(result.BrokerPnL > 0,
            $"B-Book should profit from losing client, got broker P&L: {result.BrokerPnL:F2}");
        Assert.AreEqual("B-Book", result.RoutingMode);
    }

    [TestMethod]
    public void SimulateBBook_WinningClient_NegativeBrokerPnL()
    {
        var engine = CreateEngine();
        var deals = CreateDeals(profit: 5000, count: 10); // Client wins $5000

        var result = engine.SimulateBBook(deals);

        Assert.IsTrue(result.BrokerPnL < 0,
            $"B-Book should lose from winning client, got broker P&L: {result.BrokerPnL:F2}");
    }

    [TestMethod]
    public void SimulateABook_AnyClient_BrokerPnLIsCommissionOnly()
    {
        var engine = CreateEngine();
        var deals = CreateDeals(profit: 5000, count: 10);

        var result = engine.SimulateABook(deals);

        // A-Book: broker only earns commission, doesn't take opposite side
        Assert.IsTrue(result.BrokerPnL > 0, "A-Book broker P&L should be positive (commission)");
        Assert.AreEqual(result.BrokerPnL, result.CommissionRevenue, 0.01,
            "A-Book broker P&L should equal commission revenue");
        Assert.AreEqual("A-Book", result.RoutingMode);
    }

    [TestMethod]
    public void SimulateHybrid_PnLBetweenABookAndBBook()
    {
        var engine = CreateEngine();
        var deals = CreateDeals(profit: -500, count: 10);

        var aBook = engine.SimulateABook(deals);
        var bBook = engine.SimulateBBook(deals);
        var hybrid = engine.SimulateHybrid(deals);

        // Hybrid should be between A-Book and B-Book (or equal to one)
        var min = System.Math.Min(aBook.BrokerPnL, bBook.BrokerPnL);
        var max = System.Math.Max(aBook.BrokerPnL, bBook.BrokerPnL);
        Assert.IsTrue(hybrid.BrokerPnL >= min - 0.01 && hybrid.BrokerPnL <= max + 0.01,
            $"Hybrid P&L ({hybrid.BrokerPnL:F2}) should be between A-Book ({aBook.BrokerPnL:F2}) and B-Book ({bBook.BrokerPnL:F2})");
    }

    [TestMethod]
    public void Timeline_HasOnePointPerTrade()
    {
        var engine = CreateEngine();
        var deals = CreateDeals(profit: -100, count: 8);

        var result = engine.SimulateABook(deals);

        Assert.AreEqual(8, result.Timeline.Count, "Timeline should have one point per trade (BUY/SELL)");
        Assert.AreEqual(8, result.TradeCount, "Trade count should match");
    }

    [TestMethod]
    public void EmptyDealList_ZeroPnL()
    {
        var engine = CreateEngine();
        var deals = new List<DealRecord>();

        var result = engine.SimulateABook(deals);

        Assert.AreEqual(0, result.BrokerPnL, "Empty deals should yield zero broker P&L");
        Assert.AreEqual(0, result.ClientPnL, "Empty deals should yield zero client P&L");
        Assert.AreEqual(0, result.TradeCount, "Empty deals should yield zero trade count");
        Assert.AreEqual(0, result.Timeline.Count, "Empty deals should yield empty timeline");
    }
}
