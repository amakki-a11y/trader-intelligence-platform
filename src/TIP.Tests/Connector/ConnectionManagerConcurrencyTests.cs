using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Connector;

namespace TIP.Tests.Connector;

/// <summary>
/// Concurrency tests for ConnectionManager thread-safe properties.
/// Verifies that volatile fields and lock-protected state behave correctly
/// when accessed from multiple threads simultaneously.
/// </summary>
[TestClass]
public class ConnectionManagerConcurrencyTests
{
    private static ConnectionConfig TestConfig => new(
        ServerAddress: "simulator:0",
        ManagerLogin: 1,
        Password: "test",
        GroupMask: "*",
        HealthHeartbeatIntervalMs: 30000);

    /// <summary>
    /// Setting DisconnectRequested to true from 10 concurrent threads
    /// must always result in a true read from any subsequent thread.
    /// Validates the volatile field ensures visibility across threads.
    /// </summary>
    [TestMethod]
    public void DisconnectRequested_SetFromMultipleThreads_AlwaysVisible()
    {
        var manager = new ConnectionManager(NullLogger<ConnectionManager>.Instance, TestConfig);
        Assert.IsFalse(manager.DisconnectRequested, "Initial state should be false");

        const int threadCount = 10;
        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (var i = 0; i < threadCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                manager.DisconnectRequested = true;
            });
        }

        Task.WaitAll(tasks);

        // After all threads wrote true, every read must see true
        for (var i = 0; i < 100; i++)
        {
            Assert.IsTrue(manager.DisconnectRequested,
                $"Read {i}: DisconnectRequested must be true after concurrent writes");
        }
    }
}
