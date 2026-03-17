using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Models;

namespace TIP.Tests.Models;

/// <summary>
/// Tests for the TradeFingerprint bucket key logic used by the CorrelationEngine
/// to group trades into time windows for ring detection.
/// </summary>
[TestClass]
public class TradeFingerprintTests
{
    /// <summary>
    /// Two trades within the same 5-second window on the same symbol
    /// should produce the same bucket key.
    /// </summary>
    [TestMethod]
    public void GetBucketKey_SameWindow_SameBucketKey()
    {
        var fp1 = new TradeFingerprint(1, 100, 10000, "EURUSD", 0, 1.0, 0);
        var fp2 = new TradeFingerprint(2, 200, 14999, "EURUSD", 1, 1.0, 0);

        var key1 = fp1.GetBucketKey();
        var key2 = fp2.GetBucketKey();

        Assert.AreEqual(key1, key2);
    }

    /// <summary>
    /// Two trades in different 5-second windows should produce different bucket keys,
    /// even if they are on the same symbol.
    /// </summary>
    [TestMethod]
    public void GetBucketKey_DifferentWindows_DifferentBucketKeys()
    {
        var fp1 = new TradeFingerprint(1, 100, 10000, "EURUSD", 0, 1.0, 0);
        var fp2 = new TradeFingerprint(2, 200, 15000, "EURUSD", 1, 1.0, 0);

        var key1 = fp1.GetBucketKey();
        var key2 = fp2.GetBucketKey();

        Assert.AreNotEqual(key1, key2);
    }

    /// <summary>
    /// Two trades at the same time but on different symbols should produce
    /// different bucket keys because they can't be correlated across instruments.
    /// </summary>
    [TestMethod]
    public void GetBucketKey_DifferentSymbols_DifferentBucketKeys()
    {
        var fp1 = new TradeFingerprint(1, 100, 10000, "EURUSD", 0, 1.0, 0);
        var fp2 = new TradeFingerprint(2, 200, 10000, "GBPUSD", 1, 1.0, 0);

        var key1 = fp1.GetBucketKey();
        var key2 = fp2.GetBucketKey();

        Assert.AreNotEqual(key1, key2);
    }

    /// <summary>
    /// Custom window size should change the bucketing granularity.
    /// </summary>
    [TestMethod]
    public void GetBucketKey_CustomWindowSize_GroupsCorrectly()
    {
        // 10500ms and 11500ms: 1 second apart
        var fp1 = new TradeFingerprint(1, 100, 10500, "EURUSD", 0, 1.0, 0);
        var fp2 = new TradeFingerprint(2, 200, 11500, "EURUSD", 1, 1.0, 0);

        // With 1-second window: 10500/1000=10, 11500/1000=11 → different buckets
        var key1 = fp1.GetBucketKey(1000);
        var key2 = fp2.GetBucketKey(1000);

        Assert.AreNotEqual(key1, key2);

        // With 5-second window (default): 10500/5000=2, 11500/5000=2 → same bucket
        var key3 = fp1.GetBucketKey();
        var key4 = fp2.GetBucketKey();

        Assert.AreEqual(key3, key4);
    }
}
