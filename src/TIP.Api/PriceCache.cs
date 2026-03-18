using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace TIP.Api;

/// <summary>
/// Thread-safe in-memory cache of the latest price per symbol.
///
/// Design rationale:
/// - Populated by PnLEngineService on every tick from the MT5 feed.
/// - Seeded on startup from MT5 symbol data (last known bid/ask).
/// - Read by MarketController for REST endpoint and by DealerHub for WebSocket push.
/// - ConcurrentDictionary ensures safe concurrent reads/writes from tick processing + API threads.
/// </summary>
public sealed class PriceCache
{
    private readonly ConcurrentDictionary<string, CachedPrice> _prices = new();

    /// <summary>
    /// Updates the cached price for a symbol.
    /// </summary>
    public void Update(string symbol, double bid, double ask, long timeMsc)
    {
        var spread = ask - bid;
        _prices.AddOrUpdate(symbol,
            _ => new CachedPrice(symbol, bid, ask, spread, timeMsc, bid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                SessionHighBid = bid,
                SessionLowBid = bid
            },
            (_, existing) =>
            {
                var change = bid - existing.SessionOpenBid;
                var changePct = existing.SessionOpenBid != 0 ? (change / existing.SessionOpenBid) * 100.0 : 0;
                return new CachedPrice(symbol, bid, ask, spread, timeMsc, existing.SessionOpenBid, existing.SessionStartMsc)
                {
                    Change = change,
                    ChangePercent = changePct,
                    PreviousBid = existing.Bid,
                    SessionHighBid = Math.Max(existing.SessionHighBid, bid),
                    SessionLowBid = existing.SessionLowBid > 0 ? Math.Min(existing.SessionLowBid, bid) : bid
                };
            });
    }

    /// <summary>
    /// Seeds the cache with an initial price (e.g., from MT5 symbol data on startup).
    /// Only sets the price if the symbol is not already cached.
    /// </summary>
    public void Seed(string symbol, double bid, double ask, long timeMsc)
    {
        var spread = ask - bid;
        _prices.TryAdd(symbol, new CachedPrice(symbol, bid, ask, spread, timeMsc, bid, timeMsc));
    }

    /// <summary>
    /// Gets the cached price for a symbol, or null if not cached.
    /// </summary>
    public CachedPrice? Get(string symbol)
    {
        return _prices.TryGetValue(symbol, out var price) ? price : null;
    }

    /// <summary>
    /// Gets cached prices for the specified symbols. Symbols not in cache are omitted.
    /// </summary>
    public List<CachedPrice> Get(IEnumerable<string> symbols)
    {
        var result = new List<CachedPrice>();
        foreach (var sym in symbols)
        {
            if (_prices.TryGetValue(sym, out var price))
                result.Add(price);
        }
        return result;
    }

    /// <summary>
    /// Gets all cached prices.
    /// </summary>
    public List<CachedPrice> GetAll()
    {
        return _prices.Values.ToList();
    }

    /// <summary>
    /// Returns the number of symbols currently in the cache.
    /// </summary>
    public int Count => _prices.Count;
}

/// <summary>
/// Immutable snapshot of a symbol's latest price state.
/// </summary>
public sealed record CachedPrice(
    string Symbol,
    double Bid,
    double Ask,
    double Spread,
    long TimeMsc,
    double SessionOpenBid,
    long SessionStartMsc)
{
    /// <summary>Price change from session open.</summary>
    public double Change { get; init; }

    /// <summary>Price change percent from session open.</summary>
    public double ChangePercent { get; init; }

    /// <summary>Previous bid value for flash direction detection.</summary>
    public double PreviousBid { get; init; }

    /// <summary>Session high bid price.</summary>
    public double SessionHighBid { get; init; }

    /// <summary>Session low bid price.</summary>
    public double SessionLowBid { get; init; }
}
