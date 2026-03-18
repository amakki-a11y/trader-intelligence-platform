using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TIP.Core.Resilience;

/// <summary>
/// Circuit breaker states.
/// </summary>
public enum CircuitState
{
    /// <summary>Normal operation — calls pass through.</summary>
    Closed,
    /// <summary>Too many failures — calls rejected immediately.</summary>
    Open,
    /// <summary>Testing recovery — one probe call allowed.</summary>
    HalfOpen
}

/// <summary>
/// Thrown when a call is rejected because the circuit breaker is in the Open state.
/// </summary>
public sealed class CircuitBreakerOpenException : Exception
{
    /// <summary>Name of the circuit breaker that rejected the call.</summary>
    public string CircuitName { get; }

    /// <summary>
    /// Initializes a new CircuitBreakerOpenException.
    /// </summary>
    /// <param name="circuitName">The name of the open circuit.</param>
    public CircuitBreakerOpenException(string circuitName)
        : base($"Circuit breaker '{circuitName}' is open — call rejected")
    {
        CircuitName = circuitName;
    }
}

/// <summary>
/// Generic thread-safe circuit breaker that prevents cascade failures by fast-failing
/// calls to an unhealthy dependency after N consecutive failures.
///
/// Design rationale:
/// - Three states: Closed (normal), Open (reject fast), HalfOpen (probe recovery).
/// - After failureThreshold consecutive failures, transitions to Open and rejects all calls.
/// - After openDuration, allows one probe call (HalfOpen). Success → Closed, failure → Open.
/// - Thread-safe via lock on state transitions and Interlocked for counters.
/// - Logs all state transitions for operational visibility.
/// </summary>
/// <typeparam name="T">Return type of the protected operation.</typeparam>
public sealed class CircuitBreaker<T>
{
    private readonly string _name;
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAt;

    /// <summary>
    /// Initializes a circuit breaker with the given parameters.
    /// </summary>
    /// <param name="name">Descriptive name for logging (e.g., "database", "mt5-history").</param>
    /// <param name="failureThreshold">Number of consecutive failures before opening the circuit.</param>
    /// <param name="openDuration">How long the circuit stays open before allowing a probe.</param>
    /// <param name="logger">Logger for state transition events.</param>
    public CircuitBreaker(string name, int failureThreshold, TimeSpan openDuration, ILogger logger)
    {
        _name = name;
        _failureThreshold = failureThreshold;
        _openDuration = openDuration;
        _logger = logger;
    }

    /// <summary>
    /// Current state of the circuit breaker.
    /// </summary>
    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                if (_state == CircuitState.Open && DateTimeOffset.UtcNow - _openedAt >= _openDuration)
                {
                    _state = CircuitState.HalfOpen;
                    _logger.LogInformation("Circuit [{Name}] half-open — allowing probe call", _name);
                }
                return _state;
            }
        }
    }

    /// <summary>
    /// Current consecutive failure count.
    /// </summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (_lock)
            {
                return _consecutiveFailures;
            }
        }
    }

    /// <summary>
    /// Executes the given action through the circuit breaker.
    /// If the circuit is Open, throws CircuitBreakerOpenException immediately.
    /// If HalfOpen, allows one probe call.
    /// </summary>
    /// <param name="action">The async operation to protect.</param>
    /// <returns>The result of the action.</returns>
    public async Task<T> ExecuteAsync(Func<Task<T>> action)
    {
        var currentState = State; // Triggers HalfOpen transition check

        if (currentState == CircuitState.Open)
        {
            throw new CircuitBreakerOpenException(_name);
        }

        try
        {
            var result = await action().ConfigureAwait(false);

            lock (_lock)
            {
                if (_state == CircuitState.HalfOpen)
                {
                    _state = CircuitState.Closed;
                    _logger.LogInformation("Circuit [{Name}] closed — probe succeeded", _name);
                }
                _consecutiveFailures = 0;
            }

            return result;
        }
        catch (Exception)
        {
            lock (_lock)
            {
                _consecutiveFailures++;

                if (_state == CircuitState.HalfOpen)
                {
                    _state = CircuitState.Open;
                    _openedAt = DateTimeOffset.UtcNow;
                    _logger.LogWarning("Circuit [{Name}] re-opened — probe failed", _name);
                }
                else if (_consecutiveFailures >= _failureThreshold)
                {
                    _state = CircuitState.Open;
                    _openedAt = DateTimeOffset.UtcNow;
                    _logger.LogWarning("Circuit [{Name}] opened after {N} consecutive failures", _name, _consecutiveFailures);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Executes a void async action through the circuit breaker.
    /// </summary>
    /// <param name="action">The async operation to protect.</param>
    public async Task ExecuteAsync(Func<Task> action)
    {
        await ExecuteAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return default(T)!;
        }).ConfigureAwait(false);
    }
}
