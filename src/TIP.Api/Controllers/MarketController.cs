using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TIP.Connector;
using TIP.Core.Engines;

namespace TIP.Api.Controllers;

/// <summary>
/// REST API for real-time market data from the MT5 price feed.
///
/// Design rationale:
/// - Prices come from PriceCache (populated by live CIMTTickSink ticks) for bid/ask/spread.
/// - Session high/low comes from MT5 TickStat API for accurate full-session data.
/// - SymbolCache provides symbol metadata (digits, description) loaded once on startup.
/// - GET /api/market/prices: initial snapshot for dashboard page load; WebSocket takes over after.
/// - GET /api/market/symbols: reads from SymbolCache (no per-request MT5 calls).
/// - GET /api/market/volume: aggregates open position buy/sell/net per symbol.
/// - GET /api/market/session-stats: MT5 TickStat session high/low for watchlist symbols.
/// </summary>
[ApiController]
[Route("api/market")]
[Authorize]
public sealed class MarketController : ControllerBase
{
    private readonly PriceCache _priceCache;
    private readonly SymbolCache _symbolCache;
    private readonly ExposureEngine _exposureEngine;
    private readonly PnLEngine _pnlEngine;
    private readonly IMT5Api _mt5Api;
    private readonly ILogger<MarketController> _logger;

    /// <summary>
    /// Initializes the market controller.
    /// </summary>
    public MarketController(
        PriceCache priceCache,
        SymbolCache symbolCache,
        ExposureEngine exposureEngine,
        PnLEngine pnlEngine,
        IMT5Api mt5Api,
        ILogger<MarketController> logger)
    {
        _priceCache = priceCache;
        _symbolCache = symbolCache;
        _exposureEngine = exposureEngine;
        _pnlEngine = pnlEngine;
        _mt5Api = mt5Api;
        _logger = logger;
    }

    /// <summary>
    /// Gets current prices for the specified symbols (or all cached symbols if none specified).
    /// Includes digits per symbol for accurate frontend formatting.
    /// </summary>
    [HttpGet("prices")]
    public IActionResult GetPrices([FromQuery] string? symbols = null)
    {
        var prices = string.IsNullOrWhiteSpace(symbols)
            ? _priceCache.GetAll()
            : _priceCache.Get(symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var result = prices.Select(p => new
        {
            p.Symbol,
            p.Bid,
            p.Ask,
            p.Spread,
            p.Change,
            p.ChangePercent,
            p.TimeMsc,
            p.PreviousBid,
            p.SessionHighBid,
            p.SessionLowBid,
            Digits = _symbolCache.GetDigits(p.Symbol)
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Gets the count of symbols currently in the price cache and symbol metadata cache.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            cachedPrices = _priceCache.Count,
            cachedSymbols = _symbolCache.Count,
            timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <summary>
    /// Gets buy/sell/net volume per symbol from open positions, plus top buyer and seller.
    /// Used by MarketWatch to show real-time volume data alongside prices.
    /// </summary>
    [HttpGet("volume")]
    public IActionResult GetVolume()
    {
        var allPositions = _pnlEngine.GetAllPositions();

        var bySymbol = allPositions
            .GroupBy(p => p.Symbol)
            .Select(g =>
            {
                var buys = g.Where(p => p.Direction == 0).ToList();
                var sells = g.Where(p => p.Direction == 1).ToList();

                var buyVol = buys.Sum(p => p.Volume);
                var sellVol = sells.Sum(p => p.Volume);

                var topBuyer = buys
                    .GroupBy(p => p.Login)
                    .Select(lg => new { login = lg.Key, volume = lg.Sum(p => p.Volume) })
                    .OrderByDescending(x => x.volume)
                    .FirstOrDefault();

                var topSeller = sells
                    .GroupBy(p => p.Login)
                    .Select(lg => new { login = lg.Key, volume = lg.Sum(p => p.Volume) })
                    .OrderByDescending(x => x.volume)
                    .FirstOrDefault();

                return new
                {
                    symbol = g.Key,
                    buyVolume = Math.Round(buyVol, 2),
                    sellVolume = Math.Round(sellVol, 2),
                    netVolume = Math.Round(buyVol - sellVol, 2),
                    topBuyer = new { login = topBuyer?.login ?? 0UL, volume = Math.Round(topBuyer?.volume ?? 0, 2) },
                    topSeller = new { login = topSeller?.login ?? 0UL, volume = Math.Round(topSeller?.volume ?? 0, 2) }
                };
            })
            .OrderByDescending(v => Math.Abs(v.netVolume))
            .ToList();

        return Ok(bySymbol);
    }

    /// <summary>
    /// Gets MT5 session statistics (high/low/open/close) for specified symbols via TickStat API.
    /// This calls MT5 directly (not cache) for accurate full-session data.
    /// Used by MarketWatch ADVANCED mode for real session high/low.
    /// </summary>
    [HttpGet("session-stats")]
    public IActionResult GetSessionStats([FromQuery] string? symbols = null)
    {
        if (string.IsNullOrWhiteSpace(symbols))
        {
            return BadRequest(new { error = "symbols query parameter required (comma-separated)" });
        }

        if (!_mt5Api.IsConnected)
        {
            return Ok(new List<object>());
        }

        var symbolList = symbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<object>();

        foreach (var sym in symbolList)
        {
            var stat = _mt5Api.GetTickStat(sym);
            if (stat != null)
            {
                result.Add(new
                {
                    symbol = sym,
                    high = stat.High,
                    low = stat.Low,
                    bid = stat.Bid,
                    ask = stat.Ask,
                    last = stat.Last,
                    digits = _symbolCache.GetDigits(sym)
                });
            }
        }

        return Ok(result);
    }

    /// <summary>
    /// Gets all available symbol names and metadata from the SymbolCache (loaded once on startup).
    /// No MT5 API call per request — reads from in-memory cache only.
    /// </summary>
    [HttpGet("symbols")]
    public IActionResult GetSymbols([FromQuery] string? search = null)
    {
        var symbols = _symbolCache.GetAll(search);

        var result = symbols.Select(s => new
        {
            s.Symbol,
            s.Description,
            s.Digits,
            s.ContractSize,
            s.CurrencyBase,
            s.CurrencyProfit
        }).ToList();

        return Ok(result);
    }
}
