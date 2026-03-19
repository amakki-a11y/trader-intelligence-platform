using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Engines;

namespace TIP.Tests.Engines;

/// <summary>
/// Tests for PnLEngine tick-driven P&amp;L calculation.
/// Verifies BUY/SELL formulas, position lifecycle, and aggregation queries.
/// </summary>
[TestClass]
public class PnLEngineTests
{
    private PnLEngine CreateEngine() => new(NullLogger<PnLEngine>.Instance);

    [TestMethod]
    public void OnTick_BuyPosition_CalculatesCorrectPnL()
    {
        var engine = CreateEngine();
        engine.Initialize(new List<OpenPosition>
        {
            new() { PositionId = 1, Login = 100, Symbol = "EURUSD", Direction = 0, Volume = 1.0, OpenPrice = 1.1000 }
        });

        engine.OnTick("EURUSD", 1.1050, 1.1052);

        var result = engine.GetPositionPnL(1);
        Assert.IsNotNull(result);
        // BUY P&L = (bid - openPrice) * volume * contractSize = (1.1050 - 1.1000) * 1.0 * 100000 = 500
        Assert.AreEqual(500.0, result.UnrealizedPnL);
    }

    [TestMethod]
    public void OnTick_SellPosition_CalculatesCorrectPnL()
    {
        var engine = CreateEngine();
        engine.Initialize(new List<OpenPosition>
        {
            new() { PositionId = 2, Login = 200, Symbol = "EURUSD", Direction = 1, Volume = 2.0, OpenPrice = 1.1050 }
        });

        engine.OnTick("EURUSD", 1.1000, 1.1002);

        var result = engine.GetPositionPnL(2);
        Assert.IsNotNull(result);
        // SELL P&L = (openPrice - bid) * volume * contractSize = (1.1050 - 1.1000) * 2.0 * 100000 = 1000
        Assert.AreEqual(1000.0, result.UnrealizedPnL);
    }

    [TestMethod]
    public void OnTick_GoldPosition_UsesCorrectContractSize()
    {
        var engine = CreateEngine();
        engine.Initialize(new List<OpenPosition>
        {
            new() { PositionId = 3, Login = 300, Symbol = "XAUUSD", Direction = 0, Volume = 1.0, OpenPrice = 2000.00 }
        });

        engine.OnTick("XAUUSD", 2010.00, 2010.50);

        var result = engine.GetPositionPnL(3);
        Assert.IsNotNull(result);
        // BUY P&L = (2010 - 2000) * 1.0 * 100 = 1000
        Assert.AreEqual(1000.0, result.UnrealizedPnL);
    }

    [TestMethod]
    public void OnPositionOpened_ThenTick_CalculatesPnL()
    {
        var engine = CreateEngine();

        engine.OnPositionOpened(new OpenPosition
        {
            PositionId = 10, Login = 100, Symbol = "EURUSD", Direction = 0, Volume = 1.0, OpenPrice = 1.1000
        });

        // P&L result is created on tick, not on open
        Assert.IsNull(engine.GetPositionPnL(10));

        engine.OnTick("EURUSD", 1.1020, 1.1022);
        var result = engine.GetPositionPnL(10);
        Assert.IsNotNull(result);
        Assert.AreEqual(1, engine.TrackedPositionCount);
        Assert.AreEqual(200.0, result.UnrealizedPnL);
    }

    [TestMethod]
    public void OnPositionClosed_RemovesFromTracker()
    {
        var engine = CreateEngine();
        engine.Initialize(new List<OpenPosition>
        {
            new() { PositionId = 20, Login = 100, Symbol = "EURUSD", Direction = 0, Volume = 1.0, OpenPrice = 1.1000 }
        });

        // Need a tick first to create the PnL result
        engine.OnTick("EURUSD", 1.1010, 1.1012);
        Assert.AreEqual(1, engine.TrackedPositionCount);

        engine.OnPositionClosed(20, "EURUSD");
        Assert.AreEqual(0, engine.TrackedPositionCount);
        Assert.IsNull(engine.GetPositionPnL(20));
    }

    [TestMethod]
    public void GetUnrealizedPnLByLogin_AggregatesCorrectly()
    {
        var engine = CreateEngine();
        engine.Initialize(new List<OpenPosition>
        {
            new() { PositionId = 1, Login = 100, Symbol = "EURUSD", Direction = 0, Volume = 1.0, OpenPrice = 1.1000 },
            new() { PositionId = 2, Login = 100, Symbol = "GBPUSD", Direction = 0, Volume = 1.0, OpenPrice = 1.2500 },
            new() { PositionId = 3, Login = 200, Symbol = "EURUSD", Direction = 1, Volume = 1.0, OpenPrice = 1.1050 }
        });

        engine.OnTick("EURUSD", 1.1020, 1.1022);
        engine.OnTick("GBPUSD", 1.2520, 1.2522);

        var login100Pnl = engine.GetUnrealizedPnLByLogin(100);
        // EURUSD BUY: (1.1020 - 1.1000) * 1.0 * 100000 = 200
        // GBPUSD BUY: (1.2520 - 1.2500) * 1.0 * 100000 = 200
        Assert.AreEqual(400.0, login100Pnl);

        var login200Pnl = engine.GetUnrealizedPnLByLogin(200);
        // EURUSD SELL: (1.1050 - 1.1020) * 1.0 * 100000 = 300 (uses bid, not ask)
        Assert.AreEqual(300.0, login200Pnl);
    }
}
