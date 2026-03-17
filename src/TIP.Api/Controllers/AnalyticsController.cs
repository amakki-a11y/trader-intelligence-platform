using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using TIP.Core.Engines;
using TIP.Core.Models;

namespace TIP.Api.Controllers;

/// <summary>
/// REST API controller for real-time analytics: account scores, positions, exposure, and rings.
///
/// Design rationale:
/// - All data served from in-memory engine state — no DB queries on read path.
/// - Lightweight DTOs returned directly (no AutoMapper overhead).
/// - Endpoints designed for dashboard polling until WebSocket push is implemented (Phase 4).
/// </summary>
[ApiController]
[Route("api")]
public sealed class AnalyticsController : ControllerBase
{
    private readonly AccountScorer _accountScorer;
    private readonly PnLEngine _pnlEngine;
    private readonly ExposureEngine _exposureEngine;
    private readonly CorrelationEngine _correlationEngine;

    /// <summary>
    /// Initializes the analytics controller with engine dependencies.
    /// </summary>
    public AnalyticsController(
        AccountScorer accountScorer,
        PnLEngine pnlEngine,
        ExposureEngine exposureEngine,
        CorrelationEngine correlationEngine)
    {
        _accountScorer = accountScorer;
        _pnlEngine = pnlEngine;
        _exposureEngine = exposureEngine;
        _correlationEngine = correlationEngine;
    }

    /// <summary>
    /// GET /api/accounts — Returns all scored accounts sorted by abuse score descending.
    /// Supports optional risk level filter via query parameter.
    /// </summary>
    [HttpGet("accounts")]
    public IActionResult GetAccounts([FromQuery] string? risk = null)
    {
        if (!string.IsNullOrEmpty(risk) && Enum.TryParse<RiskLevel>(risk, true, out var level))
        {
            var filtered = _accountScorer.GetAccountsByRisk(level);
            return Ok(filtered.Select(MapAccount));
        }

        var accounts = _accountScorer.GetAllAccountsSorted();
        return Ok(accounts.Select(MapAccount));
    }

    /// <summary>
    /// GET /api/accounts/{login} — Returns detailed analysis for a single account.
    /// </summary>
    [HttpGet("accounts/{login}")]
    public IActionResult GetAccount(ulong login)
    {
        var account = _accountScorer.GetAccount(login);
        if (account == null)
            return NotFound(new { error = $"Account {login} not found" });

        return Ok(MapAccountDetail(account));
    }

    /// <summary>
    /// GET /api/positions — Returns all open position P&amp;L results.
    /// Supports optional login and symbol filters.
    /// </summary>
    [HttpGet("positions")]
    public IActionResult GetPositions([FromQuery] ulong? login = null, [FromQuery] string? symbol = null)
    {
        var allPnl = _pnlEngine.GetAllPnL();

        var results = allPnl.Values.AsEnumerable();

        if (login.HasValue)
            results = results.Where(r => r.Login == login.Value);

        if (!string.IsNullOrEmpty(symbol))
            results = results.Where(r => r.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        return Ok(results.Select(r => new
        {
            r.PositionId,
            r.Login,
            r.Symbol,
            direction = r.Direction == 0 ? "BUY" : "SELL",
            r.Volume,
            r.OpenPrice,
            r.CurrentPrice,
            r.UnrealizedPnL,
            r.Swap,
            calculatedAt = r.CalculatedAt
        }));
    }

    /// <summary>
    /// GET /api/exposure — Returns net exposure by symbol and portfolio totals.
    /// </summary>
    [HttpGet("exposure")]
    public IActionResult GetExposure()
    {
        var symbols = _exposureEngine.GetAllSymbolExposures();
        var (totalLong, totalShort, netExposure) = _exposureEngine.GetTotalExposure();

        return Ok(new
        {
            symbols = symbols.Select(e => new
            {
                e.Symbol,
                e.LongVolume,
                e.ShortVolume,
                e.NetVolume,
                e.LongPositionCount,
                e.ShortPositionCount,
                e.UnrealizedPnL,
                e.FlaggedAccountCount
            }),
            totals = new
            {
                totalLong,
                totalShort,
                netExposure,
                symbolCount = _exposureEngine.SymbolCount
            }
        });
    }

    /// <summary>
    /// GET /api/accounts/{login}/info — Returns live MT5 account info (balance, equity, etc).
    /// </summary>
    [HttpGet("accounts/{login}/info")]
    public IActionResult GetAccountInfo(ulong login)
    {
        var api = HttpContext.RequestServices.GetService<TIP.Connector.IMT5Api>();
        if (api == null || !api.IsConnected)
            return Ok(new { error = "Not connected to MT5" });

        var user = api.GetUser(login);
        if (user == null)
            return NotFound(new { error = $"User {login} not found on MT5" });

        return Ok(new
        {
            login = user.Login,
            name = user.Name,
            group = user.Group,
            balance = user.Balance,
            equity = user.Equity,
            leverage = user.Leverage,
        });
    }

    /// <summary>
    /// GET /api/accounts/{login}/deals — Returns deal history from MT5 for a login.
    /// </summary>
    [HttpGet("accounts/{login}/deals")]
    public IActionResult GetAccountDeals(ulong login, [FromQuery] string? from = null, [FromQuery] string? to = null)
    {
        var api = HttpContext.RequestServices.GetService<TIP.Connector.IMT5Api>();
        if (api == null || !api.IsConnected)
            return Ok(Array.Empty<object>());

        var fromDate = from != null ? DateTimeOffset.Parse(from) : DateTimeOffset.UtcNow.AddDays(-90);
        var toDate = to != null ? DateTimeOffset.Parse(to) : DateTimeOffset.UtcNow;

        var rawDeals = api.RequestDeals(login, fromDate, toDate);
        return Ok(rawDeals.Select(d => new
        {
            dealId = d.DealId,
            login = d.Login,
            time = DateTimeOffset.FromUnixTimeMilliseconds(d.TimeMsc).ToString("o"),
            symbol = d.Symbol,
            action = d.Action,
            volume = d.VolumeLots,
            price = d.Price,
            profit = d.Profit,
            commission = d.Commission,
            swap = d.Storage,
            fee = d.Fee,
            reason = d.Reason,
            expertId = d.ExpertId,
            comment = d.Comment,
            positionId = d.PositionId,
        }));
    }

    /// <summary>
    /// GET /api/rings — Returns detected trading rings with member details.
    /// </summary>
    [HttpGet("rings")]
    public IActionResult GetRings()
    {
        var ringMembers = _accountScorer.GetRingMembers();
        var riskCounts = _accountScorer.GetRiskCounts();

        return Ok(new
        {
            ringMembers = ringMembers.Select(a => new
            {
                a.Login,
                a.AbuseScore,
                riskLevel = a.RiskLevel.ToString(),
                a.RingCorrelationCount,
                linkedLogins = a.LinkedLogins,
                a.IsRingMember
            }),
            summary = new
            {
                totalRingMembers = ringMembers.Count,
                totalAccounts = _accountScorer.AccountCount,
                correlationPairs = _correlationEngine.PairCount,
                indexedFingerprints = _correlationEngine.IndexedCount,
                riskBreakdown = new
                {
                    critical = riskCounts.Critical,
                    high = riskCounts.High,
                    medium = riskCounts.Medium,
                    low = riskCounts.Low
                }
            }
        });
    }

    /// <summary>
    /// Maps AccountAnalysis to a summary DTO for the list endpoint.
    /// </summary>
    private static object MapAccount(AccountAnalysis a) => new
    {
        a.Login,
        a.AbuseScore,
        previousScore = a.PreviousScore,
        riskLevel = a.RiskLevel.ToString(),
        a.TotalTrades,
        a.TotalVolume,
        a.TotalCommission,
        a.TotalProfit,
        a.TotalDeposits,
        a.IsRingMember,
        a.TimingEntropyCV,
        a.ExpertTradeRatio,
        lastScored = a.LastScored
    };

    /// <summary>
    /// Maps AccountAnalysis to a detailed DTO for the single-account endpoint.
    /// </summary>
    private static object MapAccountDetail(AccountAnalysis a) => new
    {
        a.Login,
        a.Name,
        a.Group,
        a.Server,
        a.AbuseScore,
        a.PreviousScore,
        riskLevel = a.RiskLevel.ToString(),
        lastScored = a.LastScored,
        metrics = new
        {
            a.TotalTrades,
            a.TotalVolume,
            a.TotalCommission,
            a.TotalProfit,
            a.TotalDeposits,
            a.TotalWithdrawals,
            a.TotalBonuses,
            a.DepositCount,
            a.SOCompensationCount,
            a.CommissionToVolumeRatio,
            a.ProfitToCommissionRatio,
            a.AvgHoldSeconds,
            a.WinRateOnShortTrades,
            a.ScalpCount,
            a.SlippageDirectionBias,
            a.BonusToDepositRatio,
            a.TimingEntropyCV,
            a.ExpertTradeRatio,
            a.AvgVolumeLots,
            a.TradesPerHour,
            a.UniqueExpertIds
        },
        ring = new
        {
            a.IsRingMember,
            a.RingCorrelationCount,
            linkedLogins = a.LinkedLogins
        }
    };
}
