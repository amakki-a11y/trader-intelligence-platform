using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Connector;

namespace TIP.Tests.Connector;

/// <summary>
/// Tests for the MT5ApiSimulator — verifies that simulated ticks and deals
/// are generated correctly and that the IMT5Api contract is satisfied.
/// </summary>
[TestClass]
public class MT5SimulatorTests
{
    private MT5ApiSimulator CreateSimulator()
    {
        return new MT5ApiSimulator(NullLogger<MT5ApiSimulator>.Instance);
    }

    [TestMethod]
    public void Initialize_ReturnsTrue()
    {
        using var sim = CreateSimulator();
        Assert.IsTrue(sim.Initialize());
    }

    [TestMethod]
    public void Connect_ReturnsTrue_AndSetsIsConnected()
    {
        using var sim = CreateSimulator();
        sim.Initialize();
        Assert.IsTrue(sim.Connect("test:443", 1000, "pass"));
        Assert.IsTrue(sim.IsConnected);
    }

    [TestMethod]
    public void Disconnect_SetsIsConnectedFalse()
    {
        using var sim = CreateSimulator();
        sim.Initialize();
        sim.Connect("test:443", 1000, "pass");
        sim.Disconnect();
        Assert.IsFalse(sim.IsConnected);
    }

    [TestMethod]
    public async Task SubscribeTicks_FiresOnTickEvents()
    {
        using var sim = CreateSimulator();
        sim.Initialize();
        sim.Connect("test:443", 1000, "pass");

        var ticks = new List<RawTick>();
        var received = new TaskCompletionSource<bool>();

        sim.OnTick += tick =>
        {
            lock (ticks)
            {
                ticks.Add(tick);
                if (ticks.Count >= 5)
                    received.TrySetResult(true);
            }
        };

        sim.SubscribeTicks();

        var completed = await Task.WhenAny(received.Task, Task.Delay(5000));
        sim.UnsubscribeTicks();

        Assert.AreEqual(received.Task, completed, "Should receive at least 5 ticks within 5 seconds");
        Assert.IsTrue(ticks.Count >= 5);

        // Verify tick structure
        var firstTick = ticks[0];
        Assert.IsFalse(string.IsNullOrEmpty(firstTick.Symbol));
        Assert.IsTrue(firstTick.Bid > 0);
        Assert.IsTrue(firstTick.Ask > 0);
        Assert.IsTrue(firstTick.Ask >= firstTick.Bid);
        Assert.IsTrue(firstTick.TimeMsc > 0);
    }

    [TestMethod]
    public async Task SubscribeDeals_FiresOnDealAddEvents()
    {
        using var sim = CreateSimulator();
        sim.Initialize();
        sim.Connect("test:443", 1000, "pass");

        var deals = new List<RawDeal>();
        var received = new TaskCompletionSource<bool>();

        sim.OnDealAdd += deal =>
        {
            lock (deals)
            {
                deals.Add(deal);
                received.TrySetResult(true);
            }
        };

        sim.SubscribeDeals();

        var completed = await Task.WhenAny(received.Task, Task.Delay(10000));
        sim.UnsubscribeDeals();

        Assert.AreEqual(received.Task, completed, "Should receive at least 1 deal within 10 seconds");
        Assert.IsTrue(deals.Count >= 1);

        // Verify deal structure
        var deal0 = deals[0];
        Assert.IsTrue(deal0.DealId > 0);
        Assert.IsTrue(deal0.Login >= 50001 && deal0.Login <= 50020);
        Assert.IsFalse(string.IsNullOrEmpty(deal0.Symbol));
        Assert.IsTrue(deal0.Action <= 1); // BUY or SELL
        Assert.IsTrue(deal0.VolumeRaw > 0);
        Assert.IsTrue(deal0.VolumeLots > 0);
    }

    [TestMethod]
    public void GetUserLogins_Returns20SimulatedLogins()
    {
        using var sim = CreateSimulator();
        var logins = sim.GetUserLogins("*");
        Assert.AreEqual(20, logins.Length);
        Assert.AreEqual(50001UL, logins[0]);
        Assert.AreEqual(50020UL, logins[19]);
    }

    [TestMethod]
    public void GetUser_ReturnsDataForValidLogin()
    {
        using var sim = CreateSimulator();
        var user = sim.GetUser(50001);
        Assert.IsNotNull(user);
        Assert.AreEqual(50001UL, user.Login);
        Assert.IsFalse(string.IsNullOrEmpty(user.Name));
        Assert.IsTrue(user.Balance > 0);
    }

    [TestMethod]
    public void GetUser_ReturnsNullForInvalidLogin()
    {
        using var sim = CreateSimulator();
        Assert.IsNull(sim.GetUser(99999));
    }

    [TestMethod]
    public void GetSymbols_Returns5Symbols()
    {
        using var sim = CreateSimulator();
        var symbols = sim.GetSymbols();
        Assert.AreEqual(5, symbols.Count);
        Assert.AreEqual("EURUSD", symbols[0].Symbol);
    }

    [TestMethod]
    public void RequestDeals_ReturnsEmptyList()
    {
        using var sim = CreateSimulator();
        var deals = sim.RequestDeals(50001, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
        Assert.AreEqual(0, deals.Count);
    }

    [TestMethod]
    public void VolumeLots_ConvertsCorrectly()
    {
        var deal = new RawDeal
        {
            DealId = 1,
            Login = 50001,
            TimeMsc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Symbol = "EURUSD",
            Action = 0,
            VolumeRaw = 10000, // 1.00 lot
            Price = 1.0850,
            Profit = 0,
            Commission = 0,
            Storage = 0,
            Fee = 0,
            Reason = 0,
            ExpertId = 0,
            Comment = "",
            PositionId = 1,
            Entry = 0
        };

        Assert.AreEqual(1.0, deal.VolumeLots);
    }

    [TestMethod]
    public async Task TickListener_UpdatesPriceCache_OnSimulatorTick()
    {
        using var sim = CreateSimulator();
        sim.Initialize();
        sim.Connect("test:443", 1000, "pass");

        var tickChannel = Channel.CreateUnbounded<TickEvent>();
        var listener = new TickListener(
            NullLogger<TickListener>.Instance,
            tickChannel.Writer);

        sim.OnTick += raw =>
        {
            listener.OnTick(new TickEvent(
                Symbol: raw.Symbol,
                Bid: raw.Bid,
                Ask: raw.Ask,
                TimeMsc: raw.TimeMsc,
                ReceivedAt: DateTimeOffset.UtcNow));
        };

        sim.SubscribeTicks();
        await Task.Delay(500); // Wait for a few tick cycles
        sim.UnsubscribeTicks();

        Assert.IsTrue(listener.CachedSymbolCount > 0, "Price cache should have entries after ticks");
        var eurusd = listener.GetLatestPrice("EURUSD");
        Assert.IsNotNull(eurusd, "EURUSD should be in the price cache");
        Assert.IsTrue(eurusd.Bid > 0);
    }
}
