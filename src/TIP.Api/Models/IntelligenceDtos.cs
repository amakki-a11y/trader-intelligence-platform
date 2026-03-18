using System;
using System.Collections.Generic;

namespace TIP.Api.Models;

/// <summary>
/// Trader intelligence profile DTO combining style classification, book routing,
/// and account metrics for the AI Routing panel in AccountDetail.
/// </summary>
public sealed record TraderProfileDto(
    ulong Login,
    string Name,
    string Group,
    string Style,
    double StyleConfidence,
    List<string> StyleSignals,
    string BookRecommendation,
    double BookConfidence,
    string BookReasoning,
    string BookSummary,
    double Score,
    string RiskLevel,
    double AvgHoldSeconds,
    double WinRate,
    double TimingEntropyCV,
    double ExpertTradeRatio,
    double TradesPerHour,
    bool IsRingMember,
    int CorrelatedTradeCount,
    DateTimeOffset LastSeen);

/// <summary>
/// Score history entry DTO for tracking score changes over time.
/// </summary>
public sealed record ScoreHistoryDto(
    DateTimeOffset Time,
    double Score,
    string RiskLevel,
    string TriggerRule);

/// <summary>
/// Simulation comparison DTO containing all three routing scenarios and a recommendation.
/// </summary>
public sealed record SimulationComparisonDto(
    SimulationResultDto ABook,
    SimulationResultDto BBook,
    SimulationResultDto Hybrid,
    string Recommendation);

/// <summary>
/// Single routing simulation scenario result DTO.
/// </summary>
public sealed record SimulationResultDto(
    string RoutingMode,
    double BrokerPnL,
    double CommissionRevenue,
    double SpreadCapture,
    double ClientPnL,
    int TradeCount,
    List<TimelinePointDto> Timeline);

/// <summary>
/// Timeline point for charting cumulative P&amp;L across simulation scenarios.
/// </summary>
public sealed record TimelinePointDto(
    long TimeMsc,
    double CumulativeBrokerPnL,
    double CumulativeClientPnL,
    int TradeIndex);
