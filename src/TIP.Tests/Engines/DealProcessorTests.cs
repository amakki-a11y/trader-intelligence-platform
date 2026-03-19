using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Engines;

namespace TIP.Tests.Engines;

/// <summary>
/// Tests for DealProcessor deal classification and position tracking.
/// Verifies action code mapping, position open/close/modify detection,
/// and correct handling of non-trade deal types.
/// </summary>
[TestClass]
public class DealProcessorTests
{
    [TestMethod]
    public void BuyDeal_NewPosition_ReturnsOpened()
    {
        var processor = new DealProcessor();

        var result = processor.ProcessDeal(dealId: 1, action: 0, volume: 1.0, positionId: 100,
            login: 1000, symbol: "EURUSD", timeMsc: 1000000);

        Assert.AreEqual(DealType.Buy, result.Type);
        Assert.AreEqual(PositionAction.Opened, result.PositionEffect);
        Assert.AreEqual(100UL, result.AffectedPositionId);
    }

    [TestMethod]
    public void SellDeal_NewPosition_ReturnsOpened()
    {
        var processor = new DealProcessor();

        var result = processor.ProcessDeal(dealId: 2, action: 1, volume: 2.0, positionId: 200,
            login: 1000, symbol: "EURUSD", timeMsc: 1000000);

        Assert.AreEqual(DealType.Sell, result.Type);
        Assert.AreEqual(PositionAction.Opened, result.PositionEffect);
        Assert.AreEqual(200UL, result.AffectedPositionId);
    }

    [TestMethod]
    public void BalanceDeal_ReturnsBalanceWithNoPositionEffect()
    {
        var processor = new DealProcessor();

        var result = processor.ProcessDeal(dealId: 3, action: 2, volume: 0, positionId: 0,
            login: 1000, symbol: "", timeMsc: 1000000);

        Assert.AreEqual(DealType.Balance, result.Type);
        Assert.AreEqual(PositionAction.None, result.PositionEffect);
        Assert.IsNull(result.AffectedPositionId);
    }

    [TestMethod]
    public void BonusDeal_ReturnsBonusWithNoPositionEffect()
    {
        var processor = new DealProcessor();

        var result = processor.ProcessDeal(dealId: 4, action: 6, volume: 0, positionId: 0,
            login: 1000, symbol: "", timeMsc: 1000000);

        Assert.AreEqual(DealType.Bonus, result.Type);
        Assert.AreEqual(PositionAction.None, result.PositionEffect);
        Assert.IsNull(result.AffectedPositionId);
    }

    [TestMethod]
    public void CloseDeal_SamePositionId_ReturnsClosed()
    {
        var processor = new DealProcessor();

        // Open a position with 1.0 lots
        processor.ProcessDeal(dealId: 10, action: 0, volume: 1.0, positionId: 500,
            login: 1000, symbol: "EURUSD", timeMsc: 1000000);

        // Close the same position with matching volume
        var result = processor.ProcessDeal(dealId: 11, action: 1, volume: 1.0, positionId: 500,
            login: 1000, symbol: "EURUSD", timeMsc: 1000001);

        Assert.AreEqual(DealType.Sell, result.Type);
        Assert.AreEqual(PositionAction.Closed, result.PositionEffect);
        Assert.AreEqual(500UL, result.AffectedPositionId);
        Assert.IsFalse(processor.IsPositionOpen(500));
    }

    [TestMethod]
    public void PartialClose_SamePositionId_ReturnsModified()
    {
        var processor = new DealProcessor();

        // Open 2.0 lots
        processor.ProcessDeal(dealId: 20, action: 0, volume: 2.0, positionId: 600,
            login: 1000, symbol: "EURUSD", timeMsc: 1000000);

        // Close only 0.5 lots
        var result = processor.ProcessDeal(dealId: 21, action: 1, volume: 0.5, positionId: 600,
            login: 1000, symbol: "EURUSD", timeMsc: 1000001);

        Assert.AreEqual(DealType.Sell, result.Type);
        Assert.AreEqual(PositionAction.Modified, result.PositionEffect);
        Assert.AreEqual(600UL, result.AffectedPositionId);
        Assert.IsTrue(processor.IsPositionOpen(600));
    }

    [TestMethod]
    public void UnknownAction_ReturnsUnknown()
    {
        var processor = new DealProcessor();

        var result = processor.ProcessDeal(dealId: 30, action: 99, volume: 0, positionId: 0,
            login: 1000, symbol: "EURUSD", timeMsc: 1000000);

        Assert.AreEqual(DealType.Unknown, result.Type);
        Assert.AreEqual(PositionAction.None, result.PositionEffect);
    }

    [TestMethod]
    public void CreditDeal_ReturnsCreditWithNoPositionEffect()
    {
        var processor = new DealProcessor();

        var result = processor.ProcessDeal(dealId: 40, action: 3, volume: 0, positionId: 0,
            login: 1000, symbol: "", timeMsc: 1000000);

        Assert.AreEqual(DealType.Credit, result.Type);
        Assert.AreEqual(PositionAction.None, result.PositionEffect);
    }

    [TestMethod]
    public void CommissionDeal_ReturnsCommissionWithNoPositionEffect()
    {
        var processor = new DealProcessor();

        var result = processor.ProcessDeal(dealId: 50, action: 7, volume: 0, positionId: 0,
            login: 1000, symbol: "", timeMsc: 1000000);

        Assert.AreEqual(DealType.Commission, result.Type);
        Assert.AreEqual(PositionAction.None, result.PositionEffect);
    }

    [TestMethod]
    public void OpenPositionCount_TracksCorrectly()
    {
        var processor = new DealProcessor();

        Assert.AreEqual(0, processor.OpenPositionCount);

        processor.ProcessDeal(dealId: 1, action: 0, volume: 1.0, positionId: 100,
            login: 1000, symbol: "EURUSD", timeMsc: 1000000);
        Assert.AreEqual(1, processor.OpenPositionCount);

        processor.ProcessDeal(dealId: 2, action: 0, volume: 1.0, positionId: 200,
            login: 1000, symbol: "GBPUSD", timeMsc: 1000001);
        Assert.AreEqual(2, processor.OpenPositionCount);

        // Close one
        processor.ProcessDeal(dealId: 3, action: 1, volume: 1.0, positionId: 100,
            login: 1000, symbol: "EURUSD", timeMsc: 1000002);
        Assert.AreEqual(1, processor.OpenPositionCount);
    }
}
