namespace TIP.Api.Models;

/// <summary>
/// Request DTO for connecting to an MT5 Manager API server.
/// </summary>
public sealed record ConnectionConfigRequest(
    string Server,
    string Login,
    string? Password,
    string? GroupMask);

/// <summary>
/// Response DTO for current connection configuration. Never includes password.
/// </summary>
public sealed record ConnectionConfigResponse(
    string Server,
    string Login,
    string GroupMask,
    bool Connected,
    int AccountsInScope,
    long UptimeSeconds);

/// <summary>
/// Response DTO for connection health status.
/// </summary>
public sealed record ConnectionStatusResponse(
    bool Connected,
    string Server,
    string Login,
    int AccountsInScope,
    long UptimeSeconds,
    string? Error);

/// <summary>
/// Request DTO for scan/analysis settings.
/// </summary>
public sealed record ScanSettingsRequest(
    int HistoryDays,
    decimal MinDeposit,
    int PollIntervalMs,
    int CriticalThreshold);

/// <summary>
/// Response DTO for current scan settings.
/// </summary>
public sealed record ScanSettingsResponse(
    int HistoryDays,
    decimal MinDeposit,
    int PollIntervalMs,
    int CriticalThreshold);
