using System;

namespace TIP.Connector;

/// <summary>
/// Raw deal data copied from CIMTDeal inside the callback.
/// Every field is captured immediately — the native MT5 object is INVALID after the callback returns.
///
/// Design rationale:
/// - Plain C# record with no MetaQuotes dependencies so it can be used throughout the solution.
/// - VolumeRaw stores the native MT5 format (1/10000 of a lot); VolumeLots provides the converted value.
/// - Storage maps to what MT5 calls "Storage" (swap/rollover charge).
/// </summary>
public sealed record RawDeal
{
    /// <summary>MT5 deal ticket number — unique per server.</summary>
    public required ulong DealId { get; init; }

    /// <summary>Account login that owns this deal.</summary>
    public required ulong Login { get; init; }

    /// <summary>Deal time in milliseconds since Unix epoch (MT5 server time).</summary>
    public required long TimeMsc { get; init; }

    /// <summary>Trading instrument symbol (empty for balance operations).</summary>
    public required string Symbol { get; init; }

    /// <summary>Deal action: 0=BUY, 1=SELL, 2=BALANCE, 6=BONUS.</summary>
    public required uint Action { get; init; }

    /// <summary>Volume in MT5 native format (1/10000 of a lot). Use VolumeLots for standard lots.</summary>
    public required ulong VolumeRaw { get; init; }

    /// <summary>Execution price.</summary>
    public required double Price { get; init; }

    /// <summary>Realized profit/loss in deposit currency.</summary>
    public required double Profit { get; init; }

    /// <summary>Commission charged.</summary>
    public required double Commission { get; init; }

    /// <summary>Swap/rollover charge — MT5 calls this "Storage".</summary>
    public required double Storage { get; init; }

    /// <summary>Additional fee.</summary>
    public required double Fee { get; init; }

    /// <summary>Deal reason: 0=CLIENT, 1=EXPERT, 2=DEALER.</summary>
    public required uint Reason { get; init; }

    /// <summary>Expert Advisor ID (magic number). 0 = manual trade.</summary>
    public required ulong ExpertId { get; init; }

    /// <summary>Free-text comment attached to the deal.</summary>
    public required string Comment { get; init; }

    /// <summary>Position ticket this deal belongs to (links open/close deals).</summary>
    public required ulong PositionId { get; init; }

    /// <summary>Deal entry type: 0=IN (open), 1=OUT (close), 2=INOUT (reverse), 3=OUT_BY (close by).</summary>
    public required uint Entry { get; init; }

    /// <summary>Volume in standard lots (VolumeRaw / 10000.0).</summary>
    public double VolumeLots => VolumeRaw / 10000.0;
}

/// <summary>
/// Raw tick data copied from the MT5 tick callback.
/// </summary>
public sealed record RawTick
{
    /// <summary>Trading instrument symbol.</summary>
    public required string Symbol { get; init; }

    /// <summary>Current bid price.</summary>
    public required double Bid { get; init; }

    /// <summary>Current ask price.</summary>
    public required double Ask { get; init; }

    /// <summary>Tick time in milliseconds since Unix epoch.</summary>
    public required long TimeMsc { get; init; }
}

/// <summary>
/// Raw user/account data from MT5 UserRequest.
/// </summary>
public sealed record RawUser
{
    /// <summary>Account login number.</summary>
    public required ulong Login { get; init; }

    /// <summary>Account holder name.</summary>
    public required string Name { get; init; }

    /// <summary>Account group (e.g., "real\standard").</summary>
    public required string Group { get; init; }

    /// <summary>Account leverage.</summary>
    public required uint Leverage { get; init; }

    /// <summary>Account balance.</summary>
    public required double Balance { get; init; }

    /// <summary>Account equity.</summary>
    public required double Equity { get; init; }

    /// <summary>IB parent login (0 if no agent).</summary>
    public required ulong Agent { get; init; }
}

/// <summary>
/// Raw open position data from MT5 PositionGet.
/// </summary>
public sealed record RawPosition
{
    /// <summary>Position ticket.</summary>
    public required ulong PositionId { get; init; }

    /// <summary>Account login that owns this position.</summary>
    public required ulong Login { get; init; }

    /// <summary>Trading instrument symbol.</summary>
    public required string Symbol { get; init; }

    /// <summary>Position direction: 0=BUY, 1=SELL.</summary>
    public required uint Action { get; init; }

    /// <summary>Volume in standard lots.</summary>
    public required double Volume { get; init; }

    /// <summary>Open price.</summary>
    public required double PriceOpen { get; init; }

    /// <summary>Current price.</summary>
    public required double PriceCurrent { get; init; }

    /// <summary>Unrealized profit/loss.</summary>
    public required double Profit { get; init; }

    /// <summary>Swap/rollover charge.</summary>
    public required double Storage { get; init; }

    /// <summary>Stop loss level.</summary>
    public required double StopLoss { get; init; }

    /// <summary>Take profit level.</summary>
    public required double TakeProfit { get; init; }

    /// <summary>Position open time in milliseconds since Unix epoch.</summary>
    public required long TimeMsc { get; init; }

    /// <summary>Expert Advisor ID (magic number). 0 = manual trade.</summary>
    public required ulong ExpertId { get; init; }

    /// <summary>Free-text comment.</summary>
    public required string Comment { get; init; }
}

/// <summary>
/// Raw tick statistics from MT5 TickStat API.
/// Contains bid, ask, high, low, and volume data for a symbol.
/// Available even for symbols that don't have recent live ticks flowing.
/// </summary>
public sealed record RawTickStat
{
    /// <summary>Trading instrument symbol.</summary>
    public required string Symbol { get; init; }

    /// <summary>Current bid price.</summary>
    public required double Bid { get; init; }

    /// <summary>Current ask price.</summary>
    public required double Ask { get; init; }

    /// <summary>Last deal price.</summary>
    public required double Last { get; init; }

    /// <summary>Session high bid price.</summary>
    public required double High { get; init; }

    /// <summary>Session low bid price.</summary>
    public required double Low { get; init; }

    /// <summary>Tick time in milliseconds since Unix epoch.</summary>
    public required long TimeMsc { get; init; }
}

/// <summary>
/// Raw symbol specification from the MT5 server.
/// </summary>
public sealed record RawSymbol
{
    /// <summary>Symbol name (e.g., "EURUSD", "XAUUSD").</summary>
    public required string Symbol { get; init; }

    /// <summary>Human-readable description.</summary>
    public required string Description { get; init; }

    /// <summary>Price decimal places.</summary>
    public required int Digits { get; init; }

    /// <summary>Contract size (e.g., 100000 for standard FX).</summary>
    public required double ContractSize { get; init; }

    /// <summary>Minimum price change.</summary>
    public required double TickSize { get; init; }

    /// <summary>Value of one tick movement.</summary>
    public required double TickValue { get; init; }

    /// <summary>Base currency of the symbol.</summary>
    public required string CurrencyBase { get; init; }

    /// <summary>Profit currency of the symbol.</summary>
    public required string CurrencyProfit { get; init; }
}
