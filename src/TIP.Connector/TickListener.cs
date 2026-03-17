using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TIP.Connector;

/// <summary>
/// Handles OnTick callbacks from MT5 and maintains a thread-safe price cache.
///
/// Design rationale:
/// - Uses ConcurrentDictionary for the price cache so that PnLEngine and ExposureEngine
///   can read latest prices without locking while ticks are being written.
/// - Each tick overwrites the previous price for that symbol — we only need the latest.
/// - Ticks are also forwarded to a Channel&lt;TickEvent&gt; for persistence via TickWriter.
/// </summary>
public sealed class TickListener
{
    private readonly ILogger<TickListener> _logger;
    private readonly ChannelWriter<TickEvent> _tickWriter;
    private readonly ConcurrentDictionary<string, TickEvent> _priceCache = new();

    /// <summary>
    /// Initializes the tick listener.
    /// </summary>
    /// <param name="logger">Logger for tick processing events.</param>
    /// <param name="tickWriter">Channel writer for forwarding ticks to persistence.</param>
    public TickListener(ILogger<TickListener> logger, ChannelWriter<TickEvent> tickWriter)
    {
        _logger = logger;
        _tickWriter = tickWriter;
    }

    /// <summary>
    /// Called by the MT5 native callback when a tick is received.
    /// Updates the price cache and writes to the tick channel.
    /// </summary>
    /// <param name="tickEvent">The tick event received from MT5.</param>
    public void OnTick(TickEvent tickEvent)
    {
        // TODO: Phase 2, Task 3 — Wire this to MT5 OnTick native callback

        _priceCache[tickEvent.Symbol] = tickEvent;

        if (!_tickWriter.TryWrite(tickEvent))
        {
            _logger.LogWarning("Tick channel full — dropping tick for {Symbol}", tickEvent.Symbol);
        }
    }

    /// <summary>
    /// Gets the latest tick event for the specified symbol.
    /// Returns null if no tick has been received for that symbol yet.
    /// </summary>
    /// <param name="symbol">Trading instrument symbol (e.g., "EURUSD").</param>
    /// <returns>The latest tick event, or null if not available.</returns>
    public TickEvent? GetLatestPrice(string symbol)
    {
        return _priceCache.TryGetValue(symbol, out var tick) ? tick : null;
    }

    /// <summary>
    /// Gets a snapshot of all latest prices across all symbols.
    /// </summary>
    /// <returns>Dictionary mapping symbol names to their latest tick events.</returns>
    public IReadOnlyDictionary<string, TickEvent> GetAllPrices()
    {
        return _priceCache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    /// <summary>
    /// Gets the number of symbols currently in the price cache.
    /// </summary>
    public int CachedSymbolCount => _priceCache.Count;
}
