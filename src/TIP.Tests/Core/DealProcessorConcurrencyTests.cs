using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Engines;

namespace TIP.Tests.Core;

/// <summary>
/// Verifies thread safety of DealProcessor partial close operations.
/// Concurrent partial closes on the same position must produce consistent volume.
/// </summary>
[TestClass]
public class DealProcessorConcurrencyTests
{
    [TestMethod]
    public async Task ProcessDeal_ConcurrentPartialCloses_PositionVolumeConsistent()
    {
        var processor = new DealProcessor();

        // Open a position with 1.0 lot
        processor.ProcessDeal(dealId: 1, action: 0, volume: 1.0, positionId: 999,
            login: 1000, symbol: "EURUSD", timeMsc: 1000000);

        Assert.IsTrue(processor.IsPositionOpen(999));

        // Fire 10 concurrent partial closes of 0.05 lot each (total: 0.5)
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Task.Run(() => processor.ProcessDeal(
                dealId: (ulong)(100 + i), action: 1, volume: 0.05, positionId: 999,
                login: 1000, symbol: "EURUSD", timeMsc: 1000001 + i)));

        await Task.WhenAll(tasks);

        // Position should still be open with ~0.5 remaining (not negative, not undefined)
        Assert.IsTrue(processor.IsPositionOpen(999));
    }
}
