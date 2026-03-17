using System;

namespace TIP.Api.Hubs;

/// <summary>
/// Account summary DTO pushed to dashboard clients when a score changes.
/// Contains all fields the AbuseGrid needs for in-place row updates.
/// </summary>
public sealed record AccountSummaryDto(
    ulong Login, string Name, string Group, string Server,
    double AbuseScore, double PreviousScore, string RiskLevel,
    int TotalTrades, double TotalVolume, double TotalCommission,
    double TotalProfit, double TotalDeposits,
    bool IsRingMember, int RingCorrelationCount,
    double TimingEntropyCV, double ExpertTradeRatio,
    DateTimeOffset LastScored);

/// <summary>
/// Symbol price DTO pushed to MarketWatch clients on each throttled tick.
/// Includes derived spread, change, and change-percent for display.
/// </summary>
public sealed record SymbolPriceDto(
    string Symbol, double Bid, double Ask, double Spread,
    long TimeMsc, double Change, double ChangePercent);

/// <summary>
/// Position summary DTO pushed for live P&amp;L tracking.
/// </summary>
public sealed record PositionSummaryDto(
    long PositionId, ulong Login, string Symbol,
    int Direction, double Volume, double OpenPrice,
    double CurrentPrice, double UnrealizedPnl, double Swap);

/// <summary>
/// Alert DTO pushed when scores cross thresholds or change significantly.
/// </summary>
public sealed record AlertMessageDto(
    ulong Login, string Message, string Severity,
    DateTimeOffset Timestamp);

/// <summary>
/// Connection status DTO pushed when MT5 connection state changes.
/// Used by the dashboard footer and settings page to show real-time connection state.
/// </summary>
public sealed record ConnectionStatusDto(
    bool Connected, string Server, string Login,
    int AccountsInScope, long UptimeSeconds, string? Error);
