using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using TIP.Api.Models;
using TIP.Connector;
using TIP.Core.Engines;
using TIP.Core.Models;

namespace TIP.Api.Controllers;

/// <summary>
/// REST API controller for trader intelligence endpoints.
/// Serves style classifications, book routing recommendations, and simulation comparisons.
///
/// Design rationale:
/// - All data served from in-memory engines (StyleClassifier, BookRouter, SimulationEngine).
/// - Profiles are computed on-demand from AccountScorer data — no separate persistence needed.
/// - Simulation uses real deal history from MT5 API for accurate what-if analysis.
/// </summary>
[ApiController]
[Route("api/intelligence")]
public sealed class IntelligenceController : ControllerBase
{
    private readonly AccountScorer _accountScorer;
    private readonly StyleClassifier _styleClassifier;
    private readonly BookRouter _bookRouter;
    private readonly SimulationEngine _simulationEngine;

    /// <summary>
    /// Initializes the intelligence controller with required engines.
    /// </summary>
    public IntelligenceController(
        AccountScorer accountScorer,
        StyleClassifier styleClassifier,
        BookRouter bookRouter,
        SimulationEngine simulationEngine)
    {
        _accountScorer = accountScorer;
        _styleClassifier = styleClassifier;
        _bookRouter = bookRouter;
        _simulationEngine = simulationEngine;
    }

    /// <summary>
    /// Gets intelligence profiles for all scored accounts.
    /// </summary>
    [HttpGet("profiles")]
    public IActionResult GetProfiles()
    {
        var accounts = _accountScorer.GetAllAccountsSorted();
        var profiles = accounts.Select(BuildProfile).ToList();
        return Ok(profiles);
    }

    /// <summary>
    /// Gets the intelligence profile for a specific account.
    /// </summary>
    [HttpGet("profiles/{login}")]
    public IActionResult GetProfile(ulong login)
    {
        var account = _accountScorer.GetAccount(login);
        if (account == null)
            return NotFound(new { error = $"Account {login} not found" });

        return Ok(BuildProfile(account));
    }

    /// <summary>
    /// Runs a 3-way routing simulation (A-Book, B-Book, Hybrid) for an account.
    /// Uses real deal history from MT5 to compute what-if P&amp;L scenarios.
    /// </summary>
    [HttpGet("profiles/{login}/simulate")]
    public IActionResult SimulateRouting(ulong login)
    {
        var account = _accountScorer.GetAccount(login);
        if (account == null)
            return NotFound(new { error = $"Account {login} not found" });

        // Get deal history from MT5 API
        var api = HttpContext.RequestServices.GetService<IMT5Api>();
        var deals = new List<DealRecord>();

        if (api != null && api.IsConnected)
        {
            var from = DateTimeOffset.UtcNow.AddDays(-90);
            var to = DateTimeOffset.UtcNow;
            var rawDeals = api.RequestDeals(login, from, to);
            deals = rawDeals.Select(d => new DealRecord
            {
                DealId = d.DealId,
                Login = d.Login,
                TimeMsc = d.TimeMsc,
                Symbol = d.Symbol,
                Action = (int)d.Action,
                Volume = d.VolumeLots,
                Price = d.Price,
                Profit = d.Profit,
                Commission = d.Commission,
                Swap = d.Storage,
                Fee = d.Fee,
                Reason = (int)d.Reason,
                ExpertId = d.ExpertId,
                Comment = d.Comment,
                PositionId = d.PositionId
            }).ToList();
        }

        if (deals.Count == 0)
        {
            return Ok(new SimulationComparisonDto(
                new SimulationResultDto("A-Book", 0, 0, 0, 0, 0, new List<TimelinePointDto>()),
                new SimulationResultDto("B-Book", 0, 0, 0, 0, 0, new List<TimelinePointDto>()),
                new SimulationResultDto("Hybrid", 0, 0, 0, 0, 0, new List<TimelinePointDto>()),
                "No deals available for simulation"));
        }

        var comparison = _simulationEngine.CompareRoutings(deals);

        return Ok(new SimulationComparisonDto(
            MapSimResult(comparison.ABook),
            MapSimResult(comparison.BBook),
            MapSimResult(comparison.Hybrid),
            comparison.Recommendation));
    }

    /// <summary>
    /// Builds a TraderProfileDto from an AccountAnalysis by running style and book classification.
    /// </summary>
    private TraderProfileDto BuildProfile(AccountAnalysis account)
    {
        var style = _styleClassifier.Classify(account);
        var book = _bookRouter.Route(account, style);

        return new TraderProfileDto(
            Login: account.Login,
            Name: account.Name,
            Group: account.Group,
            Style: style.Style.ToString(),
            StyleConfidence: style.Confidence,
            StyleSignals: style.Signals,
            BookRecommendation: book.Recommendation.ToString(),
            BookConfidence: book.Confidence,
            BookReasoning: string.Join("; ", book.RiskFlags),
            BookSummary: book.Summary,
            Score: account.AbuseScore,
            RiskLevel: account.RiskLevel.ToString(),
            AvgHoldSeconds: account.AvgHoldSeconds,
            WinRate: account.WinRateOnShortTrades,
            TimingEntropyCV: account.TimingEntropyCV,
            ExpertTradeRatio: account.ExpertTradeRatio,
            TradesPerHour: account.TradesPerHour,
            IsRingMember: account.IsRingMember,
            CorrelatedTradeCount: account.RingCorrelationCount,
            LastSeen: account.LastScored);
    }

    /// <summary>
    /// Maps internal SimulationResult to the API DTO.
    /// </summary>
    private static SimulationResultDto MapSimResult(SimulationEngine.SimulationResult result)
    {
        return new SimulationResultDto(
            result.RoutingMode,
            result.BrokerPnL,
            result.CommissionRevenue,
            result.SpreadCapture,
            result.ClientPnL,
            result.TradeCount,
            result.Timeline.Select(t => new TimelinePointDto(
                t.TimeMsc, t.CumulativeBrokerPnL, t.CumulativeClientPnL, t.TradeIndex
            )).ToList());
    }
}
