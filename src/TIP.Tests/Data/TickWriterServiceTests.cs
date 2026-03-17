using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Api;
using TIP.Connector;
using TIP.Data;

namespace TIP.Tests.Data;

/// <summary>
/// Tests for TickWriterService — verifies the Channel → TickWriter pipeline
/// works correctly with db disabled (log-only mode).
/// </summary>
[TestClass]
public class TickWriterServiceTests
{
    [TestMethod]
    public async Task Service_ReadsFromChannel_AndBuffersTicks()
    {
        var channel = Channel.CreateUnbounded<TickEvent>();
        var factory = new DbConnectionFactory("Host=localhost;Database=tip_test_dummy");
        var writer = new TickWriter(NullLogger<TickWriter>.Instance, factory, batchSize: 100);

        // dbEnabled = false so no actual DB writes
        var service = new TickWriterService(
            NullLogger<TickWriterService>.Instance,
            channel.Reader,
            writer,
            dbEnabled: false);

        using var cts = new CancellationTokenSource();

        // Start the service
        var serviceTask = service.StartAsync(cts.Token);

        // Write some ticks to the channel
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < 10; i++)
        {
            channel.Writer.TryWrite(new TickEvent("EURUSD", 1.0850 + i * 0.0001, 1.0852, now + i, DateTimeOffset.UtcNow));
        }

        // Give service time to process
        await Task.Delay(300);

        // Verify ticks were buffered
        Assert.AreEqual(10L, writer.TotalWritten);
        Assert.AreEqual(10, writer.BufferedCount); // Not flushed because dbEnabled=false

        // Stop the service
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task Service_GracefulShutdown_CompletesCleanly()
    {
        var channel = Channel.CreateUnbounded<TickEvent>();
        var factory = new DbConnectionFactory("Host=localhost;Database=tip_test_dummy");
        var writer = new TickWriter(NullLogger<TickWriter>.Instance, factory);

        var service = new TickWriterService(
            NullLogger<TickWriterService>.Instance,
            channel.Reader,
            writer,
            dbEnabled: false);

        using var cts = new CancellationTokenSource();
        var serviceTask = service.StartAsync(cts.Token);

        // Write a tick
        channel.Writer.TryWrite(new TickEvent("EURUSD", 1.0850, 1.0852,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow));

        await Task.Delay(200);

        // Complete the channel and stop
        channel.Writer.Complete();
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Should complete without exceptions
        Assert.AreEqual(1L, writer.TotalWritten);
    }
}
