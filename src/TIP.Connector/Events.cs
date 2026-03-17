using System;

namespace TIP.Connector;

/// <summary>
/// Represents a deal event received from the MT5 server via CIMTDealSink.
/// Immutable record to ensure thread-safe passage through Channel&lt;T&gt; pipelines.
/// All monetary values are in the account's deposit currency.
/// </summary>
/// <param name="DealId">MT5 deal ticket number — globally unique per server.</param>
/// <param name="Login">MT5 account login that owns this deal.</param>
/// <param name="TimeMsc">Deal execution time in milliseconds since Unix epoch (MT5 server time).</param>
/// <param name="Symbol">Trading instrument symbol (e.g., "EURUSD", "XAUUSD").</param>
/// <param name="Action">Deal action: 0=BUY, 1=SELL, 2=BALANCE, 6=BONUS.</param>
/// <param name="Volume">Deal volume in lots (e.g., 1.00 = 1 standard lot = 100,000 units).</param>
/// <param name="Price">Execution price at which the deal was filled.</param>
/// <param name="Profit">Realized profit/loss in deposit currency.</param>
/// <param name="Commission">Commission charged for the deal in deposit currency.</param>
/// <param name="Swap">Swap/rollover charge in deposit currency.</param>
/// <param name="Fee">Additional fee in deposit currency.</param>
/// <param name="Reason">Deal reason: 0=CLIENT, 1=EXPERT, 2=DEALER.</param>
/// <param name="ExpertId">Expert Advisor ID that placed the deal (0 if manual).</param>
/// <param name="Comment">Free-text comment attached to the deal.</param>
/// <param name="PositionId">Position ticket this deal belongs to (for grouping entry/exit).</param>
/// <param name="ReceivedAt">UTC timestamp when TIP received this event from MT5.</param>
public sealed record DealEvent(
    ulong DealId,
    ulong Login,
    long TimeMsc,
    string Symbol,
    int Action,
    double Volume,
    double Price,
    double Profit,
    double Commission,
    double Swap,
    double Fee,
    int Reason,
    ulong ExpertId,
    string Comment,
    ulong PositionId,
    DateTimeOffset ReceivedAt);

/// <summary>
/// Represents a tick (price update) received from the MT5 server via OnTick callback.
/// Immutable record for thread-safe usage in the price cache and Channel&lt;T&gt; pipeline.
/// </summary>
/// <param name="Symbol">Trading instrument symbol.</param>
/// <param name="Bid">Current bid price.</param>
/// <param name="Ask">Current ask price.</param>
/// <param name="TimeMsc">Tick time in milliseconds since Unix epoch (MT5 server time).</param>
/// <param name="ReceivedAt">UTC timestamp when TIP received this tick.</param>
public sealed record TickEvent(
    string Symbol,
    double Bid,
    double Ask,
    long TimeMsc,
    DateTimeOffset ReceivedAt);
