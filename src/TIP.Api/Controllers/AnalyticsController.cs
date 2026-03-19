using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using TIP.Core.Engines;
using TIP.Core.Models;
using TIP.Data;

namespace TIP.Api.Controllers;

/// <summary>
/// REST API controller for real-time analytics: account scores, positions, exposure, and rings.
///
/// Design rationale:
/// - All data served from in-memory engine state — no DB queries on read path.
/// - Lightweight DTOs returned directly (no AutoMapper overhead).
/// - Endpoints designed for dashboard polling until WebSocket push is implemented (Phase 4).
/// - FIX 6: GetUser results are cached in a static ConcurrentDictionary to avoid repeated MT5 API calls.
/// </summary>
[ApiController]
[Route("api")]
public sealed class AnalyticsController : ControllerBase
{
    /// <summary>
    /// In-memory cache for MT5 GetUser results to avoid expensive repeated API calls.
    /// Key: login, Value: (Name, Group). Cleared on scan.
    /// </summary>
    private static readonly ConcurrentDictionary<ulong, (string Name, string Group)> _userCache = new();

    /// <summary>
    /// Clears the user info cache. Called on server switch so names/groups
    /// are re-fetched from the new MT5 server.
    /// </summary>
    public static void ClearUserCache() => _userCache.Clear();

    private readonly AccountScorer _accountScorer;
    private readonly PnLEngine _pnlEngine;
    private readonly ExposureEngine _exposureEngine;
    private readonly CorrelationEngine _correlationEngine;
    private readonly DealProcessor _dealProcessor;
    private readonly DealRepository _dealRepository;
    private readonly ILogger<AnalyticsController> _logger;

    /// <summary>
    /// Initializes the analytics controller with engine dependencies.
    /// </summary>
    public AnalyticsController(
        AccountScorer accountScorer,
        PnLEngine pnlEngine,
        ExposureEngine exposureEngine,
        CorrelationEngine correlationEngine,
        DealProcessor dealProcessor,
        DealRepository dealRepository,
        ILogger<AnalyticsController> logger)
    {
        _accountScorer = accountScorer;
        _pnlEngine = pnlEngine;
        _exposureEngine = exposureEngine;
        _correlationEngine = correlationEngine;
        _dealProcessor = dealProcessor;
        _dealRepository = dealRepository;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/accounts — Returns all scored accounts sorted by abuse score descending.
    /// Supports optional risk level filter via query parameter.
    /// </summary>
    [HttpGet("accounts")]
    public IActionResult GetAccounts([FromQuery] string? risk = null)
    {
        var api = HttpContext.RequestServices.GetService<TIP.Connector.IMT5Api>();

        if (!string.IsNullOrEmpty(risk) && Enum.TryParse<RiskLevel>(risk, true, out var level))
        {
            var filtered = _accountScorer.GetAccountsByRisk(level);
            EnrichNames(filtered, api);
            return Ok(filtered.Select(MapAccount));
        }

        var accounts = _accountScorer.GetAllAccountsSorted();
        EnrichNames(accounts, api);
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
            entry = (int)d.Entry,
        }));
    }

    /// <summary>
    /// GET /api/accounts/{login}/positions — Returns open positions from MT5 for a login.
    /// </summary>
    [HttpGet("accounts/{login}/positions")]
    public IActionResult GetAccountPositions(ulong login)
    {
        var api = HttpContext.RequestServices.GetService<TIP.Connector.IMT5Api>();
        if (api == null || !api.IsConnected)
            return Ok(Array.Empty<object>());

        var positions = api.GetPositions(login);
        return Ok(positions.Select(p => new
        {
            positionId = p.PositionId,
            login = p.Login,
            symbol = p.Symbol,
            action = p.Action == 0 ? "BUY" : "SELL",
            volume = p.Volume,
            priceOpen = p.PriceOpen,
            priceCurrent = p.PriceCurrent,
            profit = p.Profit,
            swap = p.Storage,
            sl = p.StopLoss,
            tp = p.TakeProfit,
            time = DateTimeOffset.FromUnixTimeMilliseconds(p.TimeMsc).ToString("o"),
            expertId = p.ExpertId,
            comment = p.Comment,
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
    /// POST /api/accounts/scan — Triggers a full rescan: fetches deals from MT5 for all
    /// accounts visible to the current manager, writes them to DB, and replays through
    /// the scoring pipeline. Returns the number of accounts scored.
    /// </summary>
    [HttpPost("accounts/scan")]
    [EnableRateLimiting("scan")]
    public async Task<IActionResult> ScanAccounts()
    {
        var api = HttpContext.RequestServices.GetService<TIP.Connector.IMT5Api>();
        if (api == null || !api.IsConnected)
            return BadRequest(new { error = "Not connected to MT5" });

        var connectionConfig = HttpContext.RequestServices.GetRequiredService<TIP.Connector.ConnectionConfig>();

        try
        {
            // Clear user cache before rescan to pick up any name/group changes
            _userCache.Clear();

            // Step 1: Get all account logins visible to this manager
            var logins = api.GetUserLogins(connectionConfig.GroupMask);
            _logger.LogInformation("Scan: found {Count} logins for group mask '{Mask}'",
                logins.Length, connectionConfig.GroupMask);

            // Step 2: Fetch deals from MT5 for each login and write to DB
            var fromDate = DateTimeOffset.UtcNow.AddDays(-90);
            var toDate = DateTimeOffset.UtcNow;
            var totalDeals = 0;

            foreach (var login in logins)
            {
                var rawDeals = api.RequestDeals(login, fromDate, toDate);
                if (rawDeals.Count == 0) continue;

                var records = rawDeals.Select(d => new DealRecord
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
                    PositionId = d.PositionId,
                    Entry = (int)d.Entry,
                    Server = connectionConfig.ServerAddress
                }).ToList();

                foreach (var record in records)
                {
                    try
                    {
                        await _dealRepository.InsertAsync(record).ConfigureAwait(false);
                    }
                    catch (Npgsql.PostgresException ex) when (ex.SqlState == "23505")
                    {
                        // Duplicate — ignore
                    }
                }

                totalDeals += rawDeals.Count;
            }

            _logger.LogInformation("Scan: wrote {TotalDeals} deals to DB for {Logins} logins",
                totalDeals, logins.Length);

            // Step 3: Reset scorer and replay all deals from DB through the scoring pipeline
            _accountScorer.Reset();
            var allDeals = await _dealRepository.GetAllDealsAsync().ConfigureAwait(false);
            var scoredCount = 0;

            foreach (var deal in allDeals)
            {
                _dealProcessor.ProcessDeal(deal.DealId, deal.Action, deal.Volume, deal.PositionId,
                    deal.Login, deal.Symbol, deal.TimeMsc);
                _accountScorer.ProcessDeal(
                    deal.DealId, deal.Login, deal.Action, deal.Volume, deal.Profit,
                    deal.Commission, deal.Swap, deal.ExpertId, deal.Reason,
                    deal.TimeMsc, deal.Symbol, deal.PositionId);
            }

            scoredCount = _accountScorer.GetAllAccountsSorted().Count;
            _logger.LogInformation("Scan complete: {Deals} deals replayed, {Accounts} accounts scored",
                allDeals.Count, scoredCount);

            return Ok(new
            {
                success = true,
                loginsScanned = logins.Length,
                dealsWritten = totalDeals,
                dealsReplayed = allDeals.Count,
                accountsScored = scoredCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed");
            return StatusCode(500, new { error = "Scan failed: " + ex.Message });
        }
    }

    /// <summary>
    /// Enriches account name/group from MT5 API if not already set.
    /// FIX 6: Uses static ConcurrentDictionary cache to avoid repeated GetUser calls.
    /// </summary>
    private static void EnrichNames(System.Collections.Generic.IReadOnlyList<AccountAnalysis> accounts, TIP.Connector.IMT5Api? api)
    {
        if (api == null || !api.IsConnected) return;
        foreach (var a in accounts)
        {
            if (!string.IsNullOrEmpty(a.Name)) continue;

            if (_userCache.TryGetValue(a.Login, out var cached))
            {
                a.Name = cached.Name;
                a.Group = cached.Group;
                continue;
            }

            var user = api.GetUser(a.Login);
            if (user == null) continue;
            a.Name = user.Name;
            a.Group = user.Group;
            _userCache.TryAdd(a.Login, (user.Name, user.Group));
        }
    }

    /// <summary>
    /// Maps AccountAnalysis to a summary DTO for the list endpoint.
    /// </summary>
    private static object MapAccount(AccountAnalysis a) => new
    {
        a.Login,
        a.Name,
        a.Group,
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
