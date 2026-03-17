using System;

namespace TIP.Core.Models;

/// <summary>
/// Trading style classification for a trader's behavior pattern.
/// Determined by StyleClassifier based on hold times, frequency, and EA usage.
/// </summary>
public enum TradingStyle
{
    /// <summary>Not yet classified.</summary>
    Unknown,
    /// <summary>Very short holds (&lt;60s), high frequency.</summary>
    Scalper,
    /// <summary>Intraday positions, closed before end of day.</summary>
    DayTrader,
    /// <summary>Multi-day positions.</summary>
    Swing,
    /// <summary>Primarily Expert Advisor driven trading.</summary>
    EA,
    /// <summary>Manual trading without EA assistance.</summary>
    Manual,
    /// <summary>Mix of manual and EA trading styles.</summary>
    Mixed
}

/// <summary>
/// Book routing classification suggesting how to route a trader's flow.
/// </summary>
public enum BookRouting
{
    /// <summary>Not yet classified.</summary>
    Unknown,
    /// <summary>Route to liquidity provider (A-Book) — trader is profitable/skilled.</summary>
    ABook,
    /// <summary>Internalize (B-Book) — trader is unprofitable/unskilled.</summary>
    BBook,
    /// <summary>Partial internalization based on instrument or position size.</summary>
    Hybrid
}

/// <summary>
/// Comprehensive profile for a trading account combining identity, style classification,
/// and book routing information. Updated incrementally as new trades are processed.
///
/// Design rationale:
/// - Mutable class because profiles are updated over time as more data arrives.
/// - RoutingConfidence (0.0-1.0) allows the UI to show how certain the system is,
///   and operators can set minimum confidence thresholds for auto-routing.
/// </summary>
public class TraderProfile
{
    /// <summary>MT5 account login number.</summary>
    public ulong Login { get; set; }

    /// <summary>Account holder name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>MT5 group the account belongs to.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>MT5 server identifier.</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>Introducing Broker agent login (0 if direct client).</summary>
    public ulong IBAgentLogin { get; set; }

    /// <summary>Classified trading style based on behavior analysis.</summary>
    public TradingStyle Style { get; set; }

    /// <summary>Suggested book routing based on profitability and risk analysis.</summary>
    public BookRouting Routing { get; set; }

    /// <summary>Confidence level (0.0-1.0) of the routing suggestion.</summary>
    public double RoutingConfidence { get; set; }

    /// <summary>When the account first appeared in the system.</summary>
    public DateTimeOffset FirstSeen { get; set; }

    /// <summary>When the profile was last updated with new data.</summary>
    public DateTimeOffset LastUpdated { get; set; }
}
