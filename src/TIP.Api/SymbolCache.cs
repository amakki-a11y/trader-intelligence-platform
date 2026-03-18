using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using TIP.Connector;

namespace TIP.Api;

/// <summary>
/// Thread-safe in-memory cache of MT5 symbol metadata.
///
/// Design rationale:
/// - Loaded once on startup from mt5Api.GetSymbols() — no per-request MT5 calls.
/// - Provides digits per symbol for accurate price formatting (e.g. EURUSD=5, US30=0).
/// - Prices are NOT stored here — PriceCache owns live bid/ask from tick stream only.
/// - MarketController.GetSymbols() reads from here instead of calling MT5 each time.
/// </summary>
public sealed class SymbolCache
{
    private readonly ConcurrentDictionary<string, CachedSymbol> _symbols = new();

    /// <summary>Number of symbols loaded.</summary>
    public int Count => _symbols.Count;

    /// <summary>
    /// Loads all symbols from the MT5 API. Called once on startup after pipeline connects.
    /// </summary>
    public void Load(IEnumerable<RawSymbol> symbols)
    {
        _symbols.Clear();
        foreach (var s in symbols)
        {
            _symbols[s.Symbol] = new CachedSymbol(s.Symbol, s.Description, s.Digits, s.ContractSize, s.CurrencyBase, s.CurrencyProfit);
        }
    }

    /// <summary>Gets metadata for a specific symbol, or null if not found.</summary>
    public CachedSymbol? Get(string symbol) =>
        _symbols.TryGetValue(symbol, out var s) ? s : null;

    /// <summary>Gets all cached symbols, optionally filtered by search term.</summary>
    public List<CachedSymbol> GetAll(string? search = null)
    {
        var all = _symbols.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            all = all.Where(s =>
                s.Symbol.Contains(search, System.StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(search, System.StringComparison.OrdinalIgnoreCase));
        }
        return all.OrderBy(s => s.Symbol).ToList();
    }

    /// <summary>Returns digits for a symbol (default 5 if unknown).</summary>
    public int GetDigits(string symbol) =>
        _symbols.TryGetValue(symbol, out var s) ? s.Digits : 5;
}

/// <summary>Immutable MT5 symbol metadata record.</summary>
public sealed record CachedSymbol(
    string Symbol,
    string Description,
    int Digits,
    double ContractSize,
    string CurrencyBase,
    string CurrencyProfit);
