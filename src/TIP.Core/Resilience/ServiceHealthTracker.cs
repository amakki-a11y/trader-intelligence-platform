using System.Collections.Concurrent;
using System.Threading;

namespace TIP.Core.Resilience;

/// <summary>
/// Tracks health metrics for background services — consecutive errors and total processed counts.
/// Registered as a singleton and queried by the /health endpoint.
///
/// Design rationale:
/// - ConcurrentDictionary for thread-safe per-service metric storage.
/// - Interlocked operations for lock-free counter updates on the hot path.
/// - Background services call RecordSuccess/RecordError after each iteration.
/// </summary>
public sealed class ServiceHealthTracker
{
    private readonly ConcurrentDictionary<string, ServiceMetrics> _metrics = new();

    /// <summary>
    /// Records a successful processing iteration for the named service.
    /// Resets consecutive errors to zero and increments total processed.
    /// </summary>
    /// <param name="serviceName">Service identifier (e.g., "tickWriter", "computeEngine").</param>
    public void RecordSuccess(string serviceName)
    {
        var m = _metrics.GetOrAdd(serviceName, _ => new ServiceMetrics());
        m.ResetErrors();
        m.IncrementProcessed();
    }

    /// <summary>
    /// Records a failed processing iteration for the named service.
    /// Increments consecutive errors and returns the new count.
    /// </summary>
    /// <param name="serviceName">Service identifier.</param>
    /// <returns>New consecutive error count (for threshold checking).</returns>
    public int RecordError(string serviceName)
    {
        var m = _metrics.GetOrAdd(serviceName, _ => new ServiceMetrics());
        return m.IncrementErrors();
    }

    /// <summary>
    /// Gets a snapshot of all service metrics for the health endpoint.
    /// </summary>
    /// <returns>Dictionary of service name → (consecutiveErrors, totalProcessed).</returns>
    public ConcurrentDictionary<string, ServiceMetrics> GetAll() => _metrics;

    /// <summary>
    /// Gets metrics for a specific service, or null if not yet reported.
    /// </summary>
    /// <param name="serviceName">Service identifier.</param>
    /// <returns>Metrics for the service, or null.</returns>
    public ServiceMetrics? Get(string serviceName) =>
        _metrics.TryGetValue(serviceName, out var m) ? m : null;
}

/// <summary>
/// Mutable health metrics for a single background service.
/// Fields are updated via Interlocked for thread safety.
/// </summary>
public sealed class ServiceMetrics
{
    private int _consecutiveErrors;
    private long _totalProcessed;

    /// <summary>Number of consecutive errors without a success.</summary>
    public int GetConsecutiveErrors() => Volatile.Read(ref _consecutiveErrors);

    /// <summary>Total items successfully processed since startup.</summary>
    public long GetTotalProcessed() => Interlocked.Read(ref _totalProcessed);

    /// <summary>Resets consecutive errors to zero.</summary>
    internal void ResetErrors() => Interlocked.Exchange(ref _consecutiveErrors, 0);

    /// <summary>Increments consecutive errors and returns the new count.</summary>
    internal int IncrementErrors() => Interlocked.Increment(ref _consecutiveErrors);

    /// <summary>Increments total processed count.</summary>
    internal void IncrementProcessed() => Interlocked.Increment(ref _totalProcessed);
}
