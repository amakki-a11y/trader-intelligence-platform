using System;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using TIP.Api.Hubs;
using TIP.Api.Models;
using TIP.Connector;

namespace TIP.Api.Controllers;

/// <summary>
/// REST API for managing MT5 connection settings and scan configuration.
///
/// Design rationale:
/// - Connection endpoints delegate to ConnectionManager which signals MT5Connection
///   to reconnect with updated credentials at runtime.
/// - Password is NEVER returned in any GET response — only stored in memory.
/// - Scan settings are stored in ConnectionManager and can be updated without reconnect.
/// - Connection status reflects the real state of MT5Connection.IsConnected via
///   ConnectionManager, not a cached/stale value.
/// </summary>
[ApiController]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly ILogger<SettingsController> _logger;
    private readonly ConnectionManager _connectionManager;
    private readonly IWebSocketBroadcaster _broadcaster;

    /// <summary>
    /// Initializes the settings controller.
    /// </summary>
    public SettingsController(
        ILogger<SettingsController> logger,
        ConnectionManager connectionManager,
        IWebSocketBroadcaster broadcaster)
    {
        _logger = logger;
        _connectionManager = connectionManager;
        _broadcaster = broadcaster;
    }

    /// <summary>
    /// Returns the current connection configuration. Never includes password.
    /// </summary>
    [HttpGet("connection")]
    public ActionResult<ConnectionConfigResponse> GetConnectionConfig()
    {
        var config = _connectionManager.CurrentConfig;
        return Ok(new ConnectionConfigResponse(
            Server: config.ServerAddress,
            Login: config.ManagerLogin.ToString(CultureInfo.InvariantCulture),
            GroupMask: config.GroupMask,
            Connected: _connectionManager.IsConnected,
            AccountsInScope: _connectionManager.AccountsInScope,
            UptimeSeconds: _connectionManager.UptimeSeconds));
    }

    /// <summary>
    /// Updates MT5 connection credentials and triggers reconnection.
    /// Validates all required fields before applying.
    /// </summary>
    [HttpPost("connection")]
    [EnableRateLimiting("api")]
    public ActionResult<ConnectionConfigResponse> Connect([FromBody] ConnectionConfigRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Server))
            return BadRequest(new { error = "Server address is required" });

        if (string.IsNullOrWhiteSpace(request.Login))
            return BadRequest(new { error = "Manager login is required" });

        if (!ulong.TryParse(request.Login, out var login))
            return BadRequest(new { error = "Manager login must be a numeric value" });

        // If password is empty, reuse the stored password from current config (enables reconnect without re-entering)
        var password = request.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            var stored = _connectionManager.CurrentConfig.Password;
            if (string.IsNullOrWhiteSpace(stored) || stored == "CHANGE_ME")
                return BadRequest(new { error = "Password is required (no stored credentials available)" });
            password = stored;
        }

        var groupMask = string.IsNullOrWhiteSpace(request.GroupMask) ? "*" : request.GroupMask;

        try
        {
            _connectionManager.UpdateConfigAndReconnect(request.Server, login, password, groupMask);

            _logger.LogInformation(
                "Connection config updated via API: server={Server}, login={Login}",
                request.Server, request.Login);

            // Broadcast connection status change to dashboard clients
            _ = _broadcaster.BroadcastAlert(new AlertMessageDto(
                Login: 0,
                Message: $"Connecting to {request.Server}...",
                Severity: "Info",
                Timestamp: DateTimeOffset.UtcNow));

            return Ok(new ConnectionConfigResponse(
                Server: request.Server,
                Login: request.Login,
                GroupMask: groupMask,
                Connected: false, // Not yet connected — reconnecting
                AccountsInScope: 0,
                UptimeSeconds: 0));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update connection config");
            return StatusCode(500, new { error = "Failed to update connection configuration" });
        }
    }

    /// <summary>
    /// Saves MT5 connection credentials without triggering a connection attempt.
    /// Useful for storing credentials for later use via CONNECT or RECONNECT.
    /// </summary>
    [HttpPost("connection/save")]
    public ActionResult<ConnectionConfigResponse> SaveConfig([FromBody] ConnectionConfigRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Server))
            return BadRequest(new { error = "Server address is required" });

        if (string.IsNullOrWhiteSpace(request.Login))
            return BadRequest(new { error = "Manager login is required" });

        if (!ulong.TryParse(request.Login, out var login))
            return BadRequest(new { error = "Manager login must be a numeric value" });

        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { error = "Password is required when saving credentials" });

        var groupMask = string.IsNullOrWhiteSpace(request.GroupMask) ? "*" : request.GroupMask;

        _connectionManager.UpdateConfig(request.Server, login, request.Password, groupMask);

        _logger.LogInformation(
            "Credentials saved via API (no connect): server={Server}, login={Login}",
            request.Server, request.Login);

        return Ok(new ConnectionConfigResponse(
            Server: request.Server,
            Login: request.Login,
            GroupMask: groupMask,
            Connected: _connectionManager.IsConnected,
            AccountsInScope: _connectionManager.AccountsInScope,
            UptimeSeconds: _connectionManager.UptimeSeconds));
    }

    /// <summary>
    /// Disconnects from the current MT5 server.
    /// </summary>
    [HttpPost("connection/disconnect")]
    public ActionResult Disconnect()
    {
        _connectionManager.DisconnectRequested = true;
        _connectionManager.SignalDisconnect();

        _logger.LogInformation("Disconnect requested via API");

        _ = _broadcaster.BroadcastAlert(new AlertMessageDto(
            Login: 0,
            Message: "Manual disconnect requested",
            Severity: "Warning",
            Timestamp: DateTimeOffset.UtcNow));

        return Ok(new { disconnected = true });
    }

    /// <summary>
    /// Returns the current connection status including uptime and error info.
    /// </summary>
    [HttpGet("connection/status")]
    public ActionResult<ConnectionStatusResponse> GetConnectionStatus()
    {
        var config = _connectionManager.CurrentConfig;
        return Ok(new ConnectionStatusResponse(
            Connected: _connectionManager.IsConnected,
            Server: config.ServerAddress,
            Login: config.ManagerLogin.ToString(CultureInfo.InvariantCulture),
            AccountsInScope: _connectionManager.AccountsInScope,
            UptimeSeconds: _connectionManager.UptimeSeconds,
            Error: _connectionManager.LastError));
    }

    /// <summary>
    /// Returns recent connection log entries for the dashboard log panel.
    /// </summary>
    [HttpGet("connection/logs")]
    public ActionResult GetConnectionLogs([FromQuery] int count = 50)
    {
        var logs = _connectionManager.GetRecentLogs(count);
        return Ok(logs);
    }

    /// <summary>
    /// Debug endpoint: lists groups and logins visible to the connected manager.
    /// Helps diagnose GroupMask configuration issues.
    /// </summary>
    [HttpGet("connection/debug")]
    public ActionResult GetConnectionDebug()
    {
        var api = HttpContext.RequestServices.GetService<IMT5Api>();
        if (api == null || !api.IsConnected)
            return Ok(new { error = "Not connected" });

        // Try different masks to find accounts
        var allLogins = api.GetUserLogins("*");
        var realLogins = api.GetUserLogins("real\\*");
        var symbols = api.GetSymbols();

        // Get group info for each login found with wildcard
        var loginDetails = new List<object>();
        foreach (var login in allLogins)
        {
            var user = api.GetUser(login);
            if (user != null)
            {
                loginDetails.Add(new
                {
                    login = user.Login,
                    name = user.Name,
                    group = user.Group,
                    balance = user.Balance,
                    equity = user.Equity
                });
            }
            if (loginDetails.Count >= 50) break; // Limit for safety
        }

        return Ok(new
        {
            allLoginsCount = allLogins.Length,
            realMaskCount = realLogins.Length,
            symbolCount = symbols.Count,
            logins = loginDetails
        });
    }

    /// <summary>
    /// Returns the current scan/analysis settings.
    /// </summary>
    [HttpGet("scan")]
    public ActionResult<ScanSettingsResponse> GetScanSettings()
    {
        return Ok(new ScanSettingsResponse(
            HistoryDays: _connectionManager.HistoryDays,
            MinDeposit: _connectionManager.MinDeposit,
            PollIntervalMs: _connectionManager.PollIntervalMs,
            CriticalThreshold: _connectionManager.CriticalThreshold));
    }

    /// <summary>
    /// Updates the scan/analysis settings. Does not require reconnection.
    /// </summary>
    [HttpPost("scan")]
    public ActionResult<ScanSettingsResponse> UpdateScanSettings([FromBody] ScanSettingsRequest request)
    {
        if (request.HistoryDays < 1 || request.HistoryDays > 365)
            return BadRequest(new { error = "HistoryDays must be between 1 and 365" });

        if (request.PollIntervalMs < 1000 || request.PollIntervalMs > 60000)
            return BadRequest(new { error = "PollIntervalMs must be between 1000 and 60000" });

        if (request.CriticalThreshold < 1 || request.CriticalThreshold > 100)
            return BadRequest(new { error = "CriticalThreshold must be between 1 and 100" });

        _connectionManager.HistoryDays = request.HistoryDays;
        _connectionManager.MinDeposit = request.MinDeposit;
        _connectionManager.PollIntervalMs = request.PollIntervalMs;
        _connectionManager.CriticalThreshold = request.CriticalThreshold;

        _logger.LogInformation(
            "Scan settings updated: historyDays={HistoryDays}, minDeposit={MinDeposit}, pollMs={PollMs}, critThreshold={Threshold}",
            request.HistoryDays, request.MinDeposit, request.PollIntervalMs, request.CriticalThreshold);

        return Ok(new ScanSettingsResponse(
            HistoryDays: request.HistoryDays,
            MinDeposit: request.MinDeposit,
            PollIntervalMs: request.PollIntervalMs,
            CriticalThreshold: request.CriticalThreshold));
    }
}
