using System;
using System.Collections.Generic;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Connector;

namespace TIP.Tests.Connector;

/// <summary>
/// Tests for DealSink buffer/live mode switching.
/// Verifies the two-phase approach: buffer during backfill, then switch to live
/// with deduplication of already-seen deal IDs.
/// </summary>
[TestClass]
public class DealSinkTests
{
    private static DealEvent MakeDeal(ulong dealId, ulong login = 50001)
    {
        return new DealEvent(
            DealId: dealId,
            Login: login,
            TimeMsc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Symbol: "EURUSD",
            Action: 0,
            Volume: 1.0,
            Price: 1.0850,
            Profit: 0,
            Commission: -3.5,
            Swap: 0,
            Fee: 0,
            Reason: 0,
            ExpertId: 0,
            Comment: "",
            PositionId: dealId,
            ReceivedAt: DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public void InitialState_IsBufferMode()
    {
        var channel = Channel.CreateUnbounded<DealEvent>();
        var sink = new DealSink(NullLogger<DealSink>.Instance, channel.Writer);

        Assert.IsFalse(sink.IsLive);
        Assert.AreEqual(0, sink.BufferCount);
    }

    [TestMethod]
    public void BufferMode_StoresEvents()
    {
        var channel = Channel.CreateUnbounded<DealEvent>();
        var sink = new DealSink(NullLogger<DealSink>.Instance, channel.Writer);

        sink.OnDealReceived(MakeDeal(1));
        sink.OnDealReceived(MakeDeal(2));
        sink.OnDealReceived(MakeDeal(3));

        Assert.AreEqual(3, sink.BufferCount);
        Assert.IsFalse(channel.Reader.TryRead(out _), "Events should NOT go to channel in buffer mode");
    }

    [TestMethod]
    public void SwitchToLiveMode_ReplaysNonDuplicates()
    {
        var channel = Channel.CreateUnbounded<DealEvent>();
        var sink = new DealSink(NullLogger<DealSink>.Instance, channel.Writer);

        // Buffer 3 deals
        sink.OnDealReceived(MakeDeal(1));
        sink.OnDealReceived(MakeDeal(2));
        sink.OnDealReceived(MakeDeal(3));

        // Mark deal 2 as already seen (from backfill)
        var seenIds = new HashSet<ulong> { 2 };
        var replayed = sink.SwitchToLiveMode(seenIds);

        Assert.IsTrue(sink.IsLive);
        Assert.AreEqual(2, replayed); // Deals 1 and 3 replayed, deal 2 skipped

        // Verify channel received the replayed deals
        Assert.IsTrue(channel.Reader.TryRead(out var deal1));
        Assert.AreEqual(1UL, deal1.DealId);
        Assert.IsTrue(channel.Reader.TryRead(out var deal3));
        Assert.AreEqual(3UL, deal3.DealId);
        Assert.IsFalse(channel.Reader.TryRead(out _), "No more events expected");
    }

    [TestMethod]
    public void SwitchToLiveMode_SkipsAllWhenAllSeen()
    {
        var channel = Channel.CreateUnbounded<DealEvent>();
        var sink = new DealSink(NullLogger<DealSink>.Instance, channel.Writer);

        sink.OnDealReceived(MakeDeal(1));
        sink.OnDealReceived(MakeDeal(2));

        var seenIds = new HashSet<ulong> { 1, 2 };
        var replayed = sink.SwitchToLiveMode(seenIds);

        Assert.AreEqual(0, replayed);
        Assert.IsFalse(channel.Reader.TryRead(out _));
    }

    [TestMethod]
    public void LiveMode_WritesDirectlyToChannel()
    {
        var channel = Channel.CreateUnbounded<DealEvent>();
        var sink = new DealSink(NullLogger<DealSink>.Instance, channel.Writer);

        sink.SwitchToLiveMode(new HashSet<ulong>());
        Assert.IsTrue(sink.IsLive);

        sink.OnDealReceived(MakeDeal(100));

        Assert.IsTrue(channel.Reader.TryRead(out var deal));
        Assert.AreEqual(100UL, deal.DealId);
    }

    [TestMethod]
    public void BufferCount_ReturnsZeroAfterSwitch()
    {
        var channel = Channel.CreateUnbounded<DealEvent>();
        var sink = new DealSink(NullLogger<DealSink>.Instance, channel.Writer);

        sink.OnDealReceived(MakeDeal(1));
        Assert.AreEqual(1, sink.BufferCount);

        sink.SwitchToLiveMode(new HashSet<ulong>());
        Assert.AreEqual(0, sink.BufferCount);
    }

    [TestMethod]
    public void SwitchToLiveMode_WithEmptyBuffer_Works()
    {
        var channel = Channel.CreateUnbounded<DealEvent>();
        var sink = new DealSink(NullLogger<DealSink>.Instance, channel.Writer);

        var replayed = sink.SwitchToLiveMode(new HashSet<ulong>());

        Assert.AreEqual(0, replayed);
        Assert.IsTrue(sink.IsLive);
    }
}
