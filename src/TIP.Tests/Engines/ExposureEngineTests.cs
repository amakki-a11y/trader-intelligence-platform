using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Engines;

namespace TIP.Tests.Engines;

/// <summary>
/// Tests for ExposureEngine net exposure calculation.
/// Verifies per-symbol aggregation, net volume, and total portfolio exposure.
/// </summary>
[TestClass]
public class ExposureEngineTests
{
    private ExposureEngine CreateEngine() => new(NullLogger<ExposureEngine>.Instance);

    [TestMethod]
    public void Recalculate_SingleSymbol_CorrectExposure()
    {
        var engine = CreateEngine();
        var positions = new Dictionary<long, PnLResult>
        {
            [1] = new PnLResult { PositionId = 1, Login = 100, Symbol = "EURUSD", Direction = 0, Volume = 2.0, OpenPrice = 1.1, CurrentPrice = 1.1, UnrealizedPnL = 100, Swap = 0, CalculatedAt = DateTimeOffset.UtcNow },
            [2] = new PnLResult { PositionId = 2, Login = 200, Symbol = "EURUSD", Direction = 1, Volume = 1.0, OpenPrice = 1.1, CurrentPrice = 1.1, UnrealizedPnL = -50, Swap = 0, CalculatedAt = DateTimeOffset.UtcNow },
        };

        engine.Recalculate(positions);

        var exposure = engine.GetSymbolExposure("EURUSD");
        Assert.IsNotNull(exposure);
        Assert.AreEqual(2.0, exposure.LongVolume);
        Assert.AreEqual(1.0, exposure.ShortVolume);
        Assert.AreEqual(1.0, exposure.NetVolume);
        Assert.AreEqual(1, exposure.LongPositionCount);
        Assert.AreEqual(1, exposure.ShortPositionCount);
        Assert.AreEqual(50.0, exposure.UnrealizedPnL);
    }

    [TestMethod]
    public void Recalculate_MultipleSymbols_CorrectTotals()
    {
        var engine = CreateEngine();
        var positions = new Dictionary<long, PnLResult>
        {
            [1] = new PnLResult { PositionId = 1, Login = 100, Symbol = "EURUSD", Direction = 0, Volume = 3.0, OpenPrice = 1.1, CurrentPrice = 1.1, UnrealizedPnL = 0, Swap = 0, CalculatedAt = DateTimeOffset.UtcNow },
            [2] = new PnLResult { PositionId = 2, Login = 100, Symbol = "GBPUSD", Direction = 1, Volume = 2.0, OpenPrice = 1.25, CurrentPrice = 1.25, UnrealizedPnL = 0, Swap = 0, CalculatedAt = DateTimeOffset.UtcNow },
        };

        engine.Recalculate(positions);

        Assert.AreEqual(2, engine.SymbolCount);

        var (totalLong, totalShort, netExposure) = engine.GetTotalExposure();
        Assert.AreEqual(3.0, totalLong);
        Assert.AreEqual(2.0, totalShort);
        Assert.AreEqual(1.0, netExposure);
    }

    [TestMethod]
    public void Recalculate_EmptyPositions_ClearsExposure()
    {
        var engine = CreateEngine();
        var positions = new Dictionary<long, PnLResult>
        {
            [1] = new PnLResult { PositionId = 1, Login = 100, Symbol = "EURUSD", Direction = 0, Volume = 1.0, OpenPrice = 1.1, CurrentPrice = 1.1, UnrealizedPnL = 0, Swap = 0, CalculatedAt = DateTimeOffset.UtcNow },
        };

        engine.Recalculate(positions);
        Assert.AreEqual(1, engine.SymbolCount);

        engine.Recalculate(new Dictionary<long, PnLResult>());
        Assert.AreEqual(0, engine.SymbolCount);
    }

    [TestMethod]
    public void GetAllSymbolExposures_SortedByAbsNetVolume()
    {
        var engine = CreateEngine();
        var positions = new Dictionary<long, PnLResult>
        {
            [1] = new PnLResult { PositionId = 1, Login = 100, Symbol = "EURUSD", Direction = 0, Volume = 1.0, OpenPrice = 1.1, CurrentPrice = 1.1, UnrealizedPnL = 0, Swap = 0, CalculatedAt = DateTimeOffset.UtcNow },
            [2] = new PnLResult { PositionId = 2, Login = 100, Symbol = "GBPUSD", Direction = 0, Volume = 5.0, OpenPrice = 1.25, CurrentPrice = 1.25, UnrealizedPnL = 0, Swap = 0, CalculatedAt = DateTimeOffset.UtcNow },
            [3] = new PnLResult { PositionId = 3, Login = 100, Symbol = "USDJPY", Direction = 1, Volume = 3.0, OpenPrice = 150, CurrentPrice = 150, UnrealizedPnL = 0, Swap = 0, CalculatedAt = DateTimeOffset.UtcNow },
        };

        engine.Recalculate(positions);
        var all = engine.GetAllSymbolExposures();

        Assert.AreEqual(3, all.Count);
        // GBPUSD (5.0) > USDJPY (3.0) > EURUSD (1.0)
        Assert.AreEqual("GBPUSD", all[0].Symbol);
        Assert.AreEqual("USDJPY", all[1].Symbol);
        Assert.AreEqual("EURUSD", all[2].Symbol);
    }
}
