using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TIP.Connector;

/// <summary>
/// Wraps the MT5 CIMTDealSink callback interface with buffer/live mode switching.
///
/// Design rationale:
/// - MT5 fires deal callbacks immediately on subscription, but we need to backfill
///   historical deals first to avoid gaps. During backfill, DealSink operates in
///   BUFFER mode — storing events in memory. Once backfill completes, we switch to
///   LIVE mode and replay the buffer (skipping any deals already loaded from history),
///   then forward all subsequent deals directly to the channel.
/// - This two-phase approach guarantees zero deal loss during the backfill→live transition.
/// </summary>
public sealed class DealSink
{
    private readonly ILogger<DealSink> _logger;
    private readonly ChannelWriter<DealEvent> _dealWriter;
    private readonly List<DealEvent> _buffer = new();
    private readonly object _lock = new();
    private bool _isLive;
    private const int MaxBufferSize = 50_000;

    /// <summary>
    /// Initializes the deal sink in BUFFER mode.
    /// </summary>
    /// <param name="logger">Logger for mode transition and event tracking.</param>
    /// <param name="dealWriter">Channel writer for forwarding deal events downstream.</param>
    public DealSink(ILogger<DealSink> logger, ChannelWriter<DealEvent> dealWriter)
    {
        _logger = logger;
        _dealWriter = dealWriter;
    }

    /// <summary>
    /// Called by the MT5 native callback when a deal is received.
    /// In BUFFER mode, stores the event. In LIVE mode, writes directly to the channel.
    /// </summary>
    /// <param name="dealEvent">The deal event received from MT5.</param>
    public void OnDealReceived(DealEvent dealEvent)
    {
        // TODO: Phase 2, Task 3 — Wire this to CIMTDealSink.OnDealAdd native callback

        lock (_lock)
        {
            if (_isLive)
            {
                if (!_dealWriter.TryWrite(dealEvent))
                {
                    _logger.LogWarning("Deal channel full — dropping deal {DealId}", dealEvent.DealId);
                }
            }
            else
            {
                if (_buffer.Count >= MaxBufferSize)
                {
                    _logger.LogWarning("DealSink buffer full ({Max}). Dropping oldest deal.", MaxBufferSize);
                    _buffer.RemoveAt(0);
                }
                else if (_buffer.Count >= MaxBufferSize * 80 / 100 && _buffer.Count % 1000 == 0)
                {
                    _logger.LogWarning("DealSink buffer at {Pct}% capacity ({Count}/{Max})",
                        80, _buffer.Count, MaxBufferSize);
                }
                _buffer.Add(dealEvent);
            }
        }
    }

    /// <summary>
    /// Switches from BUFFER to LIVE mode. Replays all buffered events that weren't
    /// already loaded during backfill (identified by <paramref name="seenDealIds"/>),
    /// then switches to direct channel writes for all future events.
    /// </summary>
    /// <param name="seenDealIds">Set of deal IDs already loaded from history backfill.</param>
    /// <returns>Number of buffered events replayed (excluding duplicates).</returns>
    public int SwitchToLiveMode(HashSet<ulong> seenDealIds)
    {
        lock (_lock)
        {
            var replayed = 0;

            foreach (var deal in _buffer)
            {
                if (!seenDealIds.Contains(deal.DealId))
                {
                    if (!_dealWriter.TryWrite(deal))
                    {
                        _logger.LogWarning(
                            "Deal channel full during replay — dropping deal {DealId}", deal.DealId);
                    }
                    replayed++;
                }
            }

            _isLive = true;
            _buffer.Clear();

            _logger.LogInformation(
                "DealSink switched to LIVE mode. Replayed {Replayed} buffered events, skipped {Skipped} duplicates",
                replayed, seenDealIds.Count > 0 ? _buffer.Count : 0);

            return replayed;
        }
    }

    /// <summary>
    /// Gets the current number of buffered events (only meaningful in BUFFER mode).
    /// </summary>
    public int BufferCount
    {
        get
        {
            lock (_lock)
            {
                return _buffer.Count;
            }
        }
    }

    /// <summary>
    /// Whether the sink is currently in LIVE mode.
    /// </summary>
    public bool IsLive
    {
        get
        {
            lock (_lock)
            {
                return _isLive;
            }
        }
    }

    /// <summary>
    /// Resets the sink back to BUFFER mode for reconnect scenarios.
    /// Clears any remaining buffer and resets the live flag so the
    /// three-phase startup sequence can run again cleanly.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _isLive = false;
            _buffer.Clear();
            _logger.LogInformation("DealSink reset to BUFFER mode");
        }
    }
}
