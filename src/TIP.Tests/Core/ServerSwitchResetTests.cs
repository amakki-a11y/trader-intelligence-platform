using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Api;
using TIP.Core.Engines;

namespace TIP.Tests.Core;

/// <summary>
/// Tests for server-switch reset methods on engines and caches.
/// Ensures all in-memory state is properly cleared when switching MT5 servers.
/// </summary>
[TestClass]
public class ServerSwitchResetTests
{
    [TestMethod]
    public void PnLEngine_Reset_ClearsPositionsAndResults()
    {
        var engine = new PnLEngine(NullLogger<PnLEngine>.Instance);
        engine.Initialize(new List<OpenPosition>
        {
            new() { PositionId = 1, Login = 100, Symbol = "EURUSD", Direction = 0, Volume = 1.0, OpenPrice = 1.1000 },
            new() { PositionId = 2, Login = 200, Symbol = "GBPUSD", Direction = 1, Volume = 2.0, OpenPrice = 1.2500 }
        });

        // Generate P&L results via tick
        engine.OnTick("EURUSD", 1.1020, 1.1022);

        Assert.IsTrue(engine.TrackedPositionCount > 0);
        Assert.IsTrue(engine.GetAllPositions().Count > 0);

        engine.Reset();

        Assert.AreEqual(0, engine.TrackedPositionCount);
        Assert.AreEqual(0, engine.GetAllPositions().Count);
        Assert.AreEqual(0.0, engine.TotalUnrealizedPnL);
    }

    [TestMethod]
    public void ExposureEngine_Reset_ClearsExposures()
    {
        var engine = new ExposureEngine(NullLogger<ExposureEngine>.Instance);

        var pnlData = new Dictionary<long, PnLResult>
        {
            [1] = new PnLResult
            {
                PositionId = 1, Login = 100, Symbol = "EURUSD", Direction = 0,
                Volume = 1.0, OpenPrice = 1.1, CurrentPrice = 1.11,
                UnrealizedPnL = 100, Swap = 0, CalculatedAt = System.DateTimeOffset.UtcNow
            }
        };

        engine.Recalculate(pnlData);
        Assert.IsTrue(engine.SymbolCount > 0);

        engine.Reset();

        Assert.AreEqual(0, engine.SymbolCount);
        Assert.AreEqual(0, engine.GetAllSymbolExposures().Count);
    }

    [TestMethod]
    public void PriceCache_Clear_RemovesAllPrices()
    {
        var cache = new PriceCache();
        cache.Update("EURUSD", 1.1000, 1.1002, 1000000);
        cache.Update("GBPUSD", 1.2500, 1.2502, 1000001);

        Assert.AreEqual(2, cache.Count);

        cache.Clear();

        Assert.AreEqual(0, cache.Count);
        Assert.IsNull(cache.Get("EURUSD"));
        Assert.IsNull(cache.Get("GBPUSD"));
    }
}
