using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TIP.Connector;

namespace TIP.Api.Controllers;

/// <summary>
/// REST API for real-time market data from the MT5 price feed.
///
/// Design rationale:
/// - Reads from PriceCache (populated by PnLEngineService on every tick).
/// - Provides initial price snapshot for dashboard MarketWatch on page load.
/// - After initial load, clients switch to WebSocket "prices" channel for live updates.
/// - Supports filtering by symbol list to reduce payload for watchlist-based UIs.
/// </summary>
[ApiController]
[Route("api/market")]
public sealed class MarketController : ControllerBase
{
    private readonly PriceCache _priceCache;
    private readonly ILogger<MarketController> _logger;

    /// <summary>
    /// Initializes the market controller with the shared price cache.
    /// </summary>
    public MarketController(PriceCache priceCache, ILogger<MarketController> logger)
    {
        _priceCache = priceCache;
        _logger = logger;
    }

    /// <summary>
    /// Gets current prices for the specified symbols (or all cached symbols if none specified).
    /// </summary>
    /// <param name="symbols">Comma-separated list of symbol names (e.g., "XAUUSD,EURUSD,GBPUSD").</param>
    /// <returns>List of current prices with bid, ask, spread, change data.</returns>
    [HttpGet("prices")]
    public IActionResult GetPrices([FromQuery] string? symbols = null)
    {
        List<CachedPrice> prices;

        if (!string.IsNullOrWhiteSpace(symbols))
        {
            var symbolList = symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            prices = _priceCache.Get(symbolList);
        }
        else
        {
            prices = _priceCache.GetAll();
        }

        var result = prices.Select(p => new
        {
            p.Symbol,
            p.Bid,
            p.Ask,
            p.Spread,
            p.Change,
            p.ChangePercent,
            p.TimeMsc,
            p.PreviousBid
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Gets the count of symbols currently in the price cache.
    /// Useful for health checks and debugging.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            cachedSymbols = _priceCache.Count,
            timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Fetches the last known tick directly from MT5 for specified symbols.
    /// Tries TickLast first, falls back to TickStat if TickLast returns no data.
    /// Also seeds the price cache for any symbol with valid data.
    /// </summary>
    [HttpGet("tick-last")]
    public IActionResult GetTickLast([FromQuery] string symbols)
    {
        if (string.IsNullOrWhiteSpace(symbols))
            return BadRequest("symbols parameter required");

        var api = HttpContext.RequestServices.GetRequiredService<IMT5Api>();
        var symbolList = symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = new List<object>();
        foreach (var sym in symbolList)
        {
            try
            {
                // Try TickLast first (most recent tick)
                var tick = api.GetTickLast(sym);
                if (tick != null && tick.Bid > 0)
                {
                    _priceCache.Update(tick.Symbol, tick.Bid, tick.Ask, tick.TimeMsc);
                    results.Add(new { symbol = sym, bid = tick.Bid, ask = tick.Ask, timeMsc = tick.TimeMsc, source = "tick_last", status = "ok" });
                    continue;
                }

                // Fall back to TickStat (session statistics — available even without recent ticks)
                var stat = api.GetTickStat(sym);
                if (stat != null && stat.Bid > 0)
                {
                    _priceCache.Update(stat.Symbol, stat.Bid, stat.Ask, stat.TimeMsc);
                    results.Add(new { symbol = sym, bid = stat.Bid, ask = stat.Ask, timeMsc = stat.TimeMsc, source = "tick_stat", status = "ok" });
                    continue;
                }

                results.Add(new { symbol = sym, bid = 0.0, ask = 0.0, timeMsc = 0L, source = "none", status = "no_data" });
            }
            catch (Exception ex)
            {
                results.Add(new { symbol = sym, bid = 0.0, ask = 0.0, timeMsc = 0L, source = "error", status = ex.Message });
            }
        }

        return Ok(results);
    }

    /// <summary>
    /// Fetches last known ticks for ALL symbols via the batch MT5 TickLast API.
    /// Returns every symbol the MT5 pump has price data for.
    /// Also seeds the price cache for all returned symbols.
    /// </summary>
    [HttpGet("tick-batch")]
    public IActionResult GetTickBatch()
    {
        var api = HttpContext.RequestServices.GetRequiredService<IMT5Api>();
        var ticks = api.GetTickLastBatch();

        foreach (var tick in ticks)
        {
            if (tick.Bid > 0)
                _priceCache.Update(tick.Symbol, tick.Bid, tick.Ask, tick.TimeMsc);
        }

        var result = ticks.Select(t => new
        {
            symbol = t.Symbol,
            bid = t.Bid,
            ask = t.Ask,
            timeMsc = t.TimeMsc
        }).ToList();

        return Ok(new { count = result.Count, ticks = result });
    }

    /// <summary>
    /// Gets all available symbol names from the MT5 server.
    /// Used by the frontend to populate the symbol search/add feature.
    /// </summary>
    [HttpGet("symbols")]
    public IActionResult GetSymbols([FromQuery] string? search = null)
    {
        var api = HttpContext.RequestServices.GetRequiredService<IMT5Api>();
        var symbols = api.GetSymbols();

        if (!string.IsNullOrWhiteSpace(search))
        {
            symbols = symbols.Where(s =>
                s.Symbol.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var result = symbols.Select(s => new
        {
            s.Symbol,
            s.Description,
            s.Digits,
            s.ContractSize,
            s.CurrencyBase,
            s.CurrencyProfit
        }).Take(200).ToList();

        return Ok(result);
    }
}
