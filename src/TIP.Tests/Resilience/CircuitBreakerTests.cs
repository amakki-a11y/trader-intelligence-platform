using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TIP.Core.Resilience;

namespace TIP.Tests.Resilience;

/// <summary>
/// Tests for the CircuitBreaker&lt;T&gt; utility — verifies state transitions,
/// failure counting, half-open probe logic, and thread safety.
/// </summary>
[TestClass]
public class CircuitBreakerTests
{
    private static CircuitBreaker<int> MakeBreaker(int threshold = 3, int openMs = 200)
        => new("test", threshold, TimeSpan.FromMilliseconds(openMs), NullLogger.Instance);

    [TestMethod]
    public void StartsInClosedState()
    {
        var cb = MakeBreaker();
        Assert.AreEqual(CircuitState.Closed, cb.State);
    }

    [TestMethod]
    public async Task StaysClosedBelowThreshold()
    {
        var cb = MakeBreaker(threshold: 3);

        // 2 failures — still below threshold
        for (int i = 0; i < 2; i++)
        {
            try { await cb.ExecuteAsync(() => Task.FromException<int>(new InvalidOperationException("fail"))); }
            catch { /* expected */ }
        }

        Assert.AreEqual(CircuitState.Closed, cb.State);
        Assert.AreEqual(2, cb.ConsecutiveFailures);
    }

    [TestMethod]
    public async Task OpensAfterNConsecutiveFailures()
    {
        var cb = MakeBreaker(threshold: 3);

        for (int i = 0; i < 3; i++)
        {
            try { await cb.ExecuteAsync(() => Task.FromException<int>(new InvalidOperationException("fail"))); }
            catch { /* expected */ }
        }

        Assert.AreEqual(CircuitState.Open, cb.State);
    }

    [TestMethod]
    public async Task RejectsCallsWhenOpen()
    {
        var cb = MakeBreaker(threshold: 1, openMs: 5000);

        // Trip the circuit
        try { await cb.ExecuteAsync(() => Task.FromException<int>(new InvalidOperationException("fail"))); }
        catch (Exception ex) when (ex is not CircuitBreakerOpenException) { /* expected */ }

        // Next call should be rejected immediately
        await Assert.ThrowsExactlyAsync<CircuitBreakerOpenException>(async () =>
        {
            await cb.ExecuteAsync(() => Task.FromResult(42));
        });
    }

    [TestMethod]
    public async Task TransitionsToHalfOpenAfterDuration()
    {
        var cb = MakeBreaker(threshold: 1, openMs: 100);

        // Trip it
        try { await cb.ExecuteAsync(() => Task.FromException<int>(new InvalidOperationException("fail"))); }
        catch (Exception ex) when (ex is not CircuitBreakerOpenException) { /* expected */ }

        Assert.AreEqual(CircuitState.Open, cb.State);

        // Wait for open duration
        await Task.Delay(150);

        Assert.AreEqual(CircuitState.HalfOpen, cb.State);
    }

    [TestMethod]
    public async Task ClosesAfterSuccessfulProbe()
    {
        var cb = MakeBreaker(threshold: 1, openMs: 100);

        // Trip it
        try { await cb.ExecuteAsync(() => Task.FromException<int>(new InvalidOperationException("fail"))); }
        catch (Exception ex) when (ex is not CircuitBreakerOpenException) { /* expected */ }

        // Wait for half-open
        await Task.Delay(150);
        Assert.AreEqual(CircuitState.HalfOpen, cb.State);

        // Successful probe
        var result = await cb.ExecuteAsync(() => Task.FromResult(42));
        Assert.AreEqual(42, result);
        Assert.AreEqual(CircuitState.Closed, cb.State);
    }

    [TestMethod]
    public async Task ReOpensAfterFailedProbe()
    {
        var cb = MakeBreaker(threshold: 1, openMs: 100);

        // Trip it
        try { await cb.ExecuteAsync(() => Task.FromException<int>(new InvalidOperationException("fail"))); }
        catch (Exception ex) when (ex is not CircuitBreakerOpenException) { /* expected */ }

        // Wait for half-open
        await Task.Delay(150);
        Assert.AreEqual(CircuitState.HalfOpen, cb.State);

        // Failed probe
        try { await cb.ExecuteAsync(() => Task.FromException<int>(new InvalidOperationException("still broken"))); }
        catch (Exception ex) when (ex is not CircuitBreakerOpenException) { /* expected */ }

        Assert.AreEqual(CircuitState.Open, cb.State);
    }

    [TestMethod]
    public async Task ResetsFailureCountOnSuccess()
    {
        var cb = MakeBreaker(threshold: 3);

        // 2 failures
        for (int i = 0; i < 2; i++)
        {
            try { await cb.ExecuteAsync(() => Task.FromException<int>(new InvalidOperationException("fail"))); }
            catch { /* expected */ }
        }

        Assert.AreEqual(2, cb.ConsecutiveFailures);

        // 1 success — resets counter
        await cb.ExecuteAsync(() => Task.FromResult(1));
        Assert.AreEqual(0, cb.ConsecutiveFailures);

        // 2 more failures — still below threshold (not 4 consecutive)
        for (int i = 0; i < 2; i++)
        {
            try { await cb.ExecuteAsync(() => Task.FromException<int>(new InvalidOperationException("fail"))); }
            catch { /* expected */ }
        }

        Assert.AreEqual(CircuitState.Closed, cb.State);
    }

    [TestMethod]
    public async Task ConcurrentCallsDontCorruptState()
    {
        var cb = MakeBreaker(threshold: 50, openMs: 5000);
        var tasks = new List<Task>();
        var errors = new ConcurrentBag<Exception>();

        // 100 concurrent calls — mix of success and failure
        for (int i = 0; i < 100; i++)
        {
            var shouldFail = i % 2 == 0;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await cb.ExecuteAsync(() =>
                        shouldFail
                            ? Task.FromException<int>(new InvalidOperationException("fail"))
                            : Task.FromResult(1));
                }
                catch (Exception ex) when (ex is not CircuitBreakerOpenException)
                {
                    errors.Add(ex);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // State should be valid (either Closed or Open, not corrupted)
        var state = cb.State;
        Assert.IsTrue(state == CircuitState.Closed || state == CircuitState.Open,
            $"State should be Closed or Open, was {state}");
    }

    [TestMethod]
    public async Task LogsStateTransitions()
    {
        var logMessages = new List<string>();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(logMessages)));
        var logger = loggerFactory.CreateLogger("test");

        var cb = new CircuitBreaker<int>("myservice", 1, TimeSpan.FromMilliseconds(100), logger);

        // Trip it — should log "opened"
        try { await cb.ExecuteAsync(() => Task.FromException<int>(new InvalidOperationException("fail"))); }
        catch { /* expected */ }

        // Wait for half-open — should log "half-open"
        await Task.Delay(150);
        _ = cb.State; // Trigger state check

        // Successful probe — should log "closed"
        await cb.ExecuteAsync(() => Task.FromResult(1));

        Assert.IsTrue(logMessages.Exists(m => m.Contains("opened")), "Expected 'opened' log message");
        Assert.IsTrue(logMessages.Exists(m => m.Contains("half-open")), "Expected 'half-open' log message");
        Assert.IsTrue(logMessages.Exists(m => m.Contains("closed")), "Expected 'closed' log message");
    }

    /// <summary>
    /// Simple test logger provider that collects log messages into a list.
    /// </summary>
    private sealed class TestLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages;
        public TestLoggerProvider(List<string> messages) => _messages = messages;
        public ILogger CreateLogger(string categoryName) => new TestLogger(_messages);
        public void Dispose() { }
    }

    private sealed class TestLogger : ILogger
    {
        private readonly List<string> _messages;
        public TestLogger(List<string> messages) => _messages = messages;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_messages) { _messages.Add(formatter(state, exception)); }
        }
    }
}
