using System;

namespace TIP.Core.Models;

/// <summary>
/// Persistent deal record mapping to the "deals" hypertable in TimescaleDB.
/// Extends the DealEvent data with the server identifier for multi-server deployments.
///
/// Design rationale:
/// - Separate from DealEvent (which is a transit record) because DealRecord includes
///   server context and may gain additional persistence-layer fields over time.
/// - Maps 1:1 to the deals hypertable columns for straightforward ORM-free mapping.
/// </summary>
public class DealRecord
{
    /// <summary>MT5 deal ticket number — unique per server.</summary>
    public ulong DealId { get; set; }

    /// <summary>MT5 account login that owns this deal.</summary>
    public ulong Login { get; set; }

    /// <summary>Deal execution time in milliseconds since Unix epoch.</summary>
    public long TimeMsc { get; set; }

    /// <summary>Trading instrument symbol.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Deal action: 0=BUY, 1=SELL, 2=BALANCE, 6=BONUS.</summary>
    public int Action { get; set; }

    /// <summary>Deal volume in lots.</summary>
    public double Volume { get; set; }

    /// <summary>Execution price.</summary>
    public double Price { get; set; }

    /// <summary>Realized profit/loss in deposit currency.</summary>
    public double Profit { get; set; }

    /// <summary>Commission charged in deposit currency.</summary>
    public double Commission { get; set; }

    /// <summary>Swap/rollover charge in deposit currency.</summary>
    public double Swap { get; set; }

    /// <summary>Additional fee in deposit currency.</summary>
    public double Fee { get; set; }

    /// <summary>Deal reason: 0=CLIENT, 1=EXPERT, 2=DEALER.</summary>
    public int Reason { get; set; }

    /// <summary>Expert Advisor ID (0 if manual).</summary>
    public ulong ExpertId { get; set; }

    /// <summary>Free-text comment attached to the deal.</summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>Position ticket this deal belongs to.</summary>
    public ulong PositionId { get; set; }

    /// <summary>UTC timestamp when TIP received this event.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>MT5 server identifier for multi-server deployments.</summary>
    public string Server { get; set; } = string.Empty;
}
