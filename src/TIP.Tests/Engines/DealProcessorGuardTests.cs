using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Engines;

namespace TIP.Tests.Engines;

/// <summary>
/// Tests for the DealProcessor input validation guard — verifies that invalid deals
/// return Invalid results instead of throwing exceptions.
/// </summary>
[TestClass]
public class DealProcessorGuardTests
{
    private readonly DealProcessor _processor = new(NullLogger<DealProcessor>.Instance);

    [TestMethod]
    public void ReturnsInvalid_ForZeroLogin()
    {
        var result = _processor.ProcessDeal(1, 0, 1.0, 100, login: 0, symbol: "EURUSD", timeMsc: 1000);

        Assert.AreEqual(PositionAction.Invalid, result.PositionEffect);
        Assert.AreEqual(DealType.Unknown, result.Type);
    }

    [TestMethod]
    public void ReturnsInvalid_ForNullSymbol()
    {
        var result = _processor.ProcessDeal(1, 0, 1.0, 100, login: 50001, symbol: null, timeMsc: 1000);

        Assert.AreEqual(PositionAction.Invalid, result.PositionEffect);
    }

    [TestMethod]
    public void ReturnsInvalid_ForEmptySymbol()
    {
        var result = _processor.ProcessDeal(1, 0, 1.0, 100, login: 50001, symbol: "", timeMsc: 1000);

        Assert.AreEqual(PositionAction.Invalid, result.PositionEffect);
    }

    [TestMethod]
    public void ReturnsInvalid_ForEpochTime()
    {
        var result = _processor.ProcessDeal(1, 0, 1.0, 100, login: 50001, symbol: "EURUSD", timeMsc: 0);

        Assert.AreEqual(PositionAction.Invalid, result.PositionEffect);
    }

    [TestMethod]
    public void DoesNotThrow_OnAnyInput()
    {
        // Even extreme inputs should return a result, never throw
        var result1 = _processor.ProcessDeal(0, -1, -999, 0, login: 0, symbol: null, timeMsc: -1);
        Assert.IsNotNull(result1);

        var result2 = _processor.ProcessDeal(ulong.MaxValue, 999, double.MaxValue, ulong.MaxValue,
            login: ulong.MaxValue, symbol: "X", timeMsc: long.MaxValue);
        Assert.IsNotNull(result2);
    }
}
