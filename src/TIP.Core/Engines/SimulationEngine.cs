using System;
using System.Collections.Generic;
using System.Linq;
using TIP.Core.Models;

namespace TIP.Core.Engines;

/// <summary>
/// P&amp;L replay simulation engine for book routing analysis.
///
/// Replays historical deals to compute what-if scenarios:
/// - A-Book: broker earns only commission (client P&amp;L passed through to LP).
/// - B-Book: broker internalizes — broker P&amp;L = -clientP&amp;L + commission + spread capture.
/// - Hybrid: splits flow based on trade size/risk threshold — partial internalization.
///
/// Design rationale:
/// - Pure computation, no DB access — takes a List&lt;DealRecord&gt; as input.
/// - Returns SimulationResult with cumulative timeline for charting.
/// - No ML dependencies — deterministic replay with configurable parameters.
/// - Thread-safe: stateless engine, all state in input/output.
/// </summary>
public sealed class SimulationEngine
{
    /// <summary>Default spread capture per lot for B-Book simulation (in deposit currency).</summary>
    private const double DefaultSpreadCapture = 2.0;

    /// <summary>Default commission per lot (round-trip) for A-Book simulation.</summary>
    private const double DefaultCommissionPerLot = 7.0;

    /// <summary>Volume threshold for Hybrid mode: trades above this go A-Book.</summary>
    private const double HybridVolumeThreshold = 1.0;

    /// <summary>
    /// Result of a single routing simulation scenario.
    /// </summary>
    public sealed record SimulationResult(
        string RoutingMode,
        double BrokerPnL,
        double CommissionRevenue,
        double SpreadCapture,
        double ClientPnL,
        int TradeCount,
        List<TimelinePoint> Timeline);

    /// <summary>
    /// A single point on the cumulative P&amp;L timeline for charting.
    /// </summary>
    public sealed record TimelinePoint(
        long TimeMsc,
        double CumulativeBrokerPnL,
        double CumulativeClientPnL,
        int TradeIndex);

    /// <summary>
    /// Comparison of all three routing scenarios with a recommendation.
    /// </summary>
    public sealed record RoutingComparison(
        SimulationResult ABook,
        SimulationResult BBook,
        SimulationResult Hybrid,
        string Recommendation);

    /// <summary>
    /// Simulates A-Book routing: broker earns commission only, client P&amp;L is passed through.
    /// </summary>
    /// <param name="deals">Historical deals sorted chronologically.</param>
    /// <returns>Simulation result with timeline.</returns>
    public SimulationResult SimulateABook(List<DealRecord> deals)
    {
        var timeline = new List<TimelinePoint>();
        double brokerPnL = 0;
        double clientPnL = 0;
        double totalCommission = 0;
        int tradeIndex = 0;

        foreach (var deal in deals)
        {
            if (!IsTrade(deal)) continue;
            tradeIndex++;

            var commission = Math.Abs(deal.Commission);
            if (commission < 0.01)
                commission = deal.Volume * DefaultCommissionPerLot;

            totalCommission += commission;
            brokerPnL += commission; // A-Book: broker only earns commission
            clientPnL += deal.Profit - commission;

            timeline.Add(new TimelinePoint(deal.TimeMsc, brokerPnL, clientPnL, tradeIndex));
        }

        return new SimulationResult("A-Book", brokerPnL, totalCommission, 0, clientPnL, tradeIndex, timeline);
    }

    /// <summary>
    /// Simulates B-Book routing: broker internalizes all trades.
    /// Broker P&amp;L = -client_trade_pnl + commission + spread_capture.
    /// </summary>
    /// <param name="deals">Historical deals sorted chronologically.</param>
    /// <returns>Simulation result with timeline.</returns>
    public SimulationResult SimulateBBook(List<DealRecord> deals)
    {
        var timeline = new List<TimelinePoint>();
        double brokerPnL = 0;
        double clientPnL = 0;
        double totalCommission = 0;
        double totalSpread = 0;
        int tradeIndex = 0;

        foreach (var deal in deals)
        {
            if (!IsTrade(deal)) continue;
            tradeIndex++;

            var commission = Math.Abs(deal.Commission);
            if (commission < 0.01)
                commission = deal.Volume * DefaultCommissionPerLot;

            var spreadCapture = deal.Volume * DefaultSpreadCapture;

            totalCommission += commission;
            totalSpread += spreadCapture;

            // B-Book: broker takes opposite side of client trade
            brokerPnL += -deal.Profit + commission + spreadCapture;
            clientPnL += deal.Profit - commission;

            timeline.Add(new TimelinePoint(deal.TimeMsc, brokerPnL, clientPnL, tradeIndex));
        }

        return new SimulationResult("B-Book", brokerPnL, totalCommission, totalSpread, clientPnL, tradeIndex, timeline);
    }

    /// <summary>
    /// Simulates Hybrid routing: small trades B-Booked, large trades A-Booked.
    /// </summary>
    /// <param name="deals">Historical deals sorted chronologically.</param>
    /// <returns>Simulation result with timeline.</returns>
    public SimulationResult SimulateHybrid(List<DealRecord> deals)
    {
        var timeline = new List<TimelinePoint>();
        double brokerPnL = 0;
        double clientPnL = 0;
        double totalCommission = 0;
        double totalSpread = 0;
        int tradeIndex = 0;

        foreach (var deal in deals)
        {
            if (!IsTrade(deal)) continue;
            tradeIndex++;

            var commission = Math.Abs(deal.Commission);
            if (commission < 0.01)
                commission = deal.Volume * DefaultCommissionPerLot;

            totalCommission += commission;

            if (deal.Volume >= HybridVolumeThreshold)
            {
                // Large trade → A-Book (pass through)
                brokerPnL += commission;
            }
            else
            {
                // Small trade → B-Book (internalize)
                var spreadCapture = deal.Volume * DefaultSpreadCapture;
                totalSpread += spreadCapture;
                brokerPnL += -deal.Profit + commission + spreadCapture;
            }

            clientPnL += deal.Profit - commission;
            timeline.Add(new TimelinePoint(deal.TimeMsc, brokerPnL, clientPnL, tradeIndex));
        }

        return new SimulationResult("Hybrid", brokerPnL, totalCommission, totalSpread, clientPnL, tradeIndex, timeline);
    }

    /// <summary>
    /// Compares all three routing scenarios and recommends the best one for the broker.
    /// </summary>
    /// <param name="deals">Historical deals sorted chronologically.</param>
    /// <returns>Comparison with recommendation.</returns>
    public RoutingComparison CompareRoutings(List<DealRecord> deals)
    {
        var aBook = SimulateABook(deals);
        var bBook = SimulateBBook(deals);
        var hybrid = SimulateHybrid(deals);

        // Recommend the routing with highest broker P&L
        string recommendation;
        if (aBook.BrokerPnL >= bBook.BrokerPnL && aBook.BrokerPnL >= hybrid.BrokerPnL)
            recommendation = "A-Book yields highest broker P&L — client is profitable, hedge externally.";
        else if (bBook.BrokerPnL >= aBook.BrokerPnL && bBook.BrokerPnL >= hybrid.BrokerPnL)
            recommendation = "B-Book yields highest broker P&L — client is unprofitable, internalize for margin capture.";
        else
            recommendation = "Hybrid yields highest broker P&L — split flow by trade size for optimal risk/return.";

        return new RoutingComparison(aBook, bBook, hybrid, recommendation);
    }

    /// <summary>
    /// Determines if a deal record represents an actual trade (BUY or SELL).
    /// Filters out balance operations, bonuses, credits, etc.
    /// </summary>
    private static bool IsTrade(DealRecord deal)
    {
        return deal.Action == 0 || deal.Action == 1; // 0=BUY, 1=SELL
    }
}
