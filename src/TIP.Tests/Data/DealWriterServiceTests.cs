using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Api;
using TIP.Connector;
using TIP.Core.Resilience;
using TIP.Data;

namespace TIP.Tests.Data;

/// <summary>
/// Tests for DealWriterService — verifies the Channel → DealRepository pipeline
/// works correctly with db disabled (log-only mode).
/// </summary>
[TestClass]
public class DealWriterServiceTests
{
    private static DealEvent MakeDeal(ulong dealId, string symbol = "EURUSD")
    {
        return new DealEvent(
            DealId: dealId,
            Login: 50001,
            TimeMsc: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Symbol: symbol,
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
    public async Task Service_ReadsFromChannel_AndCountsDeals()
    {
        var channel = Channel.CreateUnbounded<DealEvent>();
        var factory = new DbConnectionFactory("Host=localhost;Database=tip_test_dummy");
        var repo = new DealRepository(NullLogger<DealRepository>.Instance, factory);

        var service = new DealWriterService(
            NullLogger<DealWriterService>.Instance,
            channel.Reader,
            repo,
            new CircuitBreaker<int>("test-db", 5, TimeSpan.FromSeconds(30), NullLogger.Instance),
            new ServiceHealthTracker(),
            dbEnabled: false);

        using var cts = new CancellationTokenSource();
        var serviceTask = service.StartAsync(cts.Token);

        // Write some deals
        for (ulong i = 1; i <= 5; i++)
        {
            channel.Writer.TryWrite(MakeDeal(i));
        }

        await Task.Delay(300);

        Assert.AreEqual(5L, service.TotalProcessed);

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Service_GracefulShutdown_CompletesCleanly()
    {
        var channel = Channel.CreateUnbounded<DealEvent>();
        var factory = new DbConnectionFactory("Host=localhost;Database=tip_test_dummy");
        var repo = new DealRepository(NullLogger<DealRepository>.Instance, factory);

        var service = new DealWriterService(
            NullLogger<DealWriterService>.Instance,
            channel.Reader,
            repo,
            new CircuitBreaker<int>("test-db", 5, TimeSpan.FromSeconds(30), NullLogger.Instance),
            new ServiceHealthTracker(),
            dbEnabled: false);

        using var cts = new CancellationTokenSource();
        var serviceTask = service.StartAsync(cts.Token);

        channel.Writer.TryWrite(MakeDeal(1));
        await Task.Delay(200);

        channel.Writer.Complete();
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(1L, service.TotalProcessed);
    }
}
