using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Data;

namespace TIP.Tests.Data;

/// <summary>
/// Tests for TickWriter batching logic.
/// These tests verify the in-memory buffer management WITHOUT a real database connection.
/// Database COPY protocol tests require a running TimescaleDB instance (integration tests).
/// </summary>
[TestClass]
public class TickWriterTests
{
    /// <summary>
    /// Creates a TickWriter with a dummy connection factory (no real DB needed for buffer tests).
    /// </summary>
    private static TickWriter CreateWriter(int batchSize = 100, int flushMs = 1000)
    {
        // Using a dummy connection string — these tests only exercise buffer logic
        var factory = new DbConnectionFactory("Host=localhost;Database=tip_test_dummy");
        return new TickWriter(
            NullLogger<TickWriter>.Instance,
            factory,
            batchSize,
            flushMs);
    }

    [TestMethod]
    public void AddTick_IncrementsBufferCount()
    {
        using var writer = CreateWriter();

        writer.AddTick("EURUSD", 1.0850, 1.0852, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.AreEqual(1, writer.BufferedCount);
        Assert.AreEqual(1L, writer.TotalWritten);
    }

    [TestMethod]
    public void AddTick_ReturnsFalse_WhenBatchNotFull()
    {
        using var writer = CreateWriter(batchSize: 10);

        var full = writer.AddTick("EURUSD", 1.0850, 1.0852, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Assert.IsFalse(full);
    }

    [TestMethod]
    public void AddTick_ReturnsTrue_WhenBatchFull()
    {
        using var writer = CreateWriter(batchSize: 3);

        writer.AddTick("EURUSD", 1.0850, 1.0852, 1000L);
        writer.AddTick("GBPUSD", 1.2650, 1.2652, 1001L);
        var full = writer.AddTick("USDJPY", 151.50, 151.55, 1002L);

        Assert.IsTrue(full, "Batch should be full after 3 ticks (batchSize=3)");
        Assert.AreEqual(3, writer.BufferedCount);
    }

    [TestMethod]
    public void MultipleAdds_TrackTotalWritten()
    {
        using var writer = CreateWriter();

        for (int i = 0; i < 50; i++)
        {
            writer.AddTick("EURUSD", 1.0850 + i * 0.0001, 1.0852 + i * 0.0001,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + i);
        }

        Assert.AreEqual(50L, writer.TotalWritten);
        Assert.AreEqual(50, writer.BufferedCount);
    }

    [TestMethod]
    public void BatchSize_Property_ReturnsConfiguredValue()
    {
        using var writer = CreateWriter(batchSize: 5000);
        Assert.AreEqual(5000, writer.BatchSize);
    }

    [TestMethod]
    public void FlushIntervalMs_Property_ReturnsConfiguredValue()
    {
        using var writer = CreateWriter(flushMs: 2000);
        Assert.AreEqual(2000, writer.FlushIntervalMs);
    }

    [TestMethod]
    public void InitialState_AllCountersZero()
    {
        using var writer = CreateWriter();

        Assert.AreEqual(0, writer.BufferedCount);
        Assert.AreEqual(0L, writer.TotalWritten);
        Assert.AreEqual(0L, writer.TotalFlushed);
    }

    [TestMethod]
    public void AddTick_ThreadSafety_ConcurrentAdds()
    {
        using var writer = CreateWriter(batchSize: 10000);
        var count = 1000;
        var tasks = new Task[4];

        for (int t = 0; t < 4; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < count; i++)
                {
                    writer.AddTick("EURUSD", 1.0850, 1.0852,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
            });
        }

        Task.WaitAll(tasks);

        Assert.AreEqual(4000L, writer.TotalWritten);
        Assert.AreEqual(4000, writer.BufferedCount);
    }
}
