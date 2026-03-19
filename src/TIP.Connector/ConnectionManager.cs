using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace TIP.Connector;

/// <summary>
/// Thread-safe manager for MT5 connection configuration and runtime reconnect signaling.
///
/// Design rationale:
/// - Decouples connection config from the BackgroundService lifecycle so the REST API
///   can update credentials and trigger reconnection at runtime.
/// - Holds mutable state (current config, connection status, uptime) behind a lock.
/// - Uses a CancellationTokenSource to signal MT5Connection to break its heartbeat loop
///   and reconnect with updated configuration.
/// - Tracks connection events in a circular log buffer for the dashboard connection log.
/// </summary>
public sealed class ConnectionManager
{
    private readonly ILogger<ConnectionManager> _logger;
    private readonly object _lock = new();

    private ConnectionConfig _config;
    private CancellationTokenSource? _reconnectCts;
    private DateTimeOffset _connectedSince;
    private bool _isConnected;
    private string? _lastError;
    private int _accountsInScope;

    /// <summary>Maximum number of connection log entries to retain.</summary>
    private const int MaxLogEntries = 100;
    private readonly ConnectionLogEntry[] _logBuffer = new ConnectionLogEntry[MaxLogEntries];
    private int _logIndex;
    private int _logCount;

    /// <summary>
    /// Scan/analysis settings — mutable at runtime via the settings API.
    /// </summary>
    public int HistoryDays { get; set; } = 90;

    /// <summary>Minimum deposit threshold for scanning.</summary>
    public decimal MinDeposit { get; set; }

    /// <summary>Poll interval in milliseconds for live data.</summary>
    public int PollIntervalMs { get; set; } = 5000;

    /// <summary>Abuse score threshold for CRITICAL classification.</summary>
    public int CriticalThreshold { get; set; } = 70;

    /// <summary>
    /// Initializes the connection manager with the initial configuration from appsettings.
    /// </summary>
    public ConnectionManager(ILogger<ConnectionManager> logger, ConnectionConfig initialConfig)
    {
        _logger = logger;
        _config = initialConfig;
    }

    /// <summary>
    /// Gets the current connection configuration. Thread-safe.
    /// </summary>
    public ConnectionConfig CurrentConfig
    {
        get { lock (_lock) { return _config; } }
    }

    /// <summary>
    /// Whether the MT5 connection is currently active.
    /// </summary>
    public bool IsConnected
    {
        get { lock (_lock) { return _isConnected; } }
    }

    /// <summary>
    /// Number of accounts matching the current group mask.
    /// </summary>
    public int AccountsInScope
    {
        get { lock (_lock) { return _accountsInScope; } }
    }

    /// <summary>
    /// Uptime in seconds since last successful connection, or 0 if disconnected.
    /// </summary>
    public long UptimeSeconds
    {
        get
        {
            lock (_lock)
            {
                if (!_isConnected) return 0;
                return (long)(DateTimeOffset.UtcNow - _connectedSince).TotalSeconds;
            }
        }
    }

    /// <summary>
    /// Last error message, or null if no error.
    /// </summary>
    public string? LastError
    {
        get { lock (_lock) { return _lastError; } }
    }

    /// <summary>
    /// Updates the connection configuration and signals MT5Connection to reconnect.
    /// </summary>
    /// <param name="server">MT5 server address (host:port).</param>
    /// <param name="login">Manager login number.</param>
    /// <param name="password">Manager password.</param>
    /// <param name="groupMask">Group filter mask.</param>
    public void UpdateConfigAndReconnect(string server, ulong login, string password, string groupMask)
    {
        lock (_lock)
        {
            _config = new ConnectionConfig(server, login, password, groupMask);
            _lastError = null;
            _logger.LogInformation(
                "Connection config updated: server={Server}, login={Login}, groupMask={GroupMask}",
                server, login, groupMask);
            AddLog("info", $"Config updated — {server} login {login} mask '{groupMask}'");
        }

        SignalReconnect();
    }

    /// <summary>
    /// Updates the connection configuration without triggering a reconnect.
    /// Used when the user wants to save credentials for later use.
    /// </summary>
    public void UpdateConfig(string server, ulong login, string password, string groupMask)
    {
        lock (_lock)
        {
            _config = new ConnectionConfig(server, login, password, groupMask);
            _logger.LogInformation(
                "Connection config saved (no reconnect): server={Server}, login={Login}, groupMask={GroupMask}",
                server, login, groupMask);
            AddLog("info", $"Credentials saved — {server} login {login} mask '{groupMask}'");
        }
    }

    /// <summary>
    /// Signals MT5Connection to disconnect (without changing config).
    /// </summary>
    public void SignalDisconnect()
    {
        lock (_lock)
        {
            _logger.LogInformation("Disconnect requested by user");
            AddLog("info", "Disconnect requested");
        }

        SignalReconnect();
    }

    /// <summary>
    /// Registers a CancellationTokenSource that MT5Connection watches.
    /// When reconnect is needed, this CTS is cancelled.
    /// </summary>
    public void RegisterReconnectToken(CancellationTokenSource cts)
    {
        lock (_lock)
        {
            _reconnectCts = cts;
        }
    }

    /// <summary>
    /// Marks the connection as established. Called by MT5Connection on successful connect.
    /// </summary>
    public void SetConnected(string server, int accountsInScope)
    {
        lock (_lock)
        {
            _isConnected = true;
            _connectedSince = DateTimeOffset.UtcNow;
            _accountsInScope = accountsInScope;
            _lastError = null;
            AddLog("success", $"Connected to {server}");
            AddLog("info", $"Manager authenticated — login {_config.ManagerLogin}");
            AddLog("info", $"Group mask applied: {_config.GroupMask}");
            AddLog("info", $"{accountsInScope} accounts in scope");
        }
    }

    /// <summary>
    /// Marks the connection as having entered live mode. Called after pipeline startup.
    /// </summary>
    public void SetLive(int symbolCount)
    {
        lock (_lock)
        {
            AddLog("success", "CIMTDealSink registered");
            AddLog("success", $"OnTick active — {symbolCount} symbols");
        }
    }

    /// <summary>
    /// Marks the connection as disconnected. Called by MT5Connection on disconnect.
    /// </summary>
    public void SetDisconnected(string? error = null)
    {
        lock (_lock)
        {
            _isConnected = false;
            _accountsInScope = 0;
            if (error != null)
            {
                _lastError = error;
                AddLog("error", error);
            }
            else
            {
                AddLog("info", "Disconnected");
            }
        }
    }

    /// <summary>
    /// Returns recent connection log entries (newest first).
    /// </summary>
    public ConnectionLogEntry[] GetRecentLogs(int count = 50)
    {
        lock (_lock)
        {
            var total = Math.Min(count, _logCount);
            var result = new ConnectionLogEntry[total];
            for (var i = 0; i < total; i++)
            {
                var idx = (_logIndex - 1 - i + MaxLogEntries) % MaxLogEntries;
                result[i] = _logBuffer[idx];
            }
            return result;
        }
    }

    /// <summary>
    /// Whether a disconnect (without reconnect) was requested by the user.
    /// Reset after MT5Connection reads it.
    /// </summary>
    private volatile bool _disconnectRequested;

    /// <summary>Whether a user-initiated disconnect has been requested.</summary>
    public bool DisconnectRequested
    {
        get => _disconnectRequested;
        set => _disconnectRequested = value;
    }

    private void SignalReconnect()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _reconnectCts;
            _reconnectCts = null;
        }

        if (cts != null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void AddLog(string level, string message)
    {
        _logBuffer[_logIndex] = new ConnectionLogEntry(DateTimeOffset.UtcNow, level, message);
        _logIndex = (_logIndex + 1) % MaxLogEntries;
        if (_logCount < MaxLogEntries) _logCount++;
    }
}

/// <summary>
/// A single connection log entry for the dashboard connection log panel.
/// </summary>
/// <param name="Timestamp">When the event occurred.</param>
/// <param name="Level">Event level: "success", "error", or "info".</param>
/// <param name="Message">Human-readable event description.</param>
public sealed record ConnectionLogEntry(DateTimeOffset Timestamp, string Level, string Message);
