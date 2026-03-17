using System;
using System.Collections.Generic;

namespace TIP.Connector;

/// <summary>
/// Abstraction over the MT5 Manager API.
/// Real implementation wraps native MetaQuotes DLLs (MT5ApiReal).
/// Simulator implementation generates fake data for development/testing (MT5ApiSimulator).
///
/// Design rationale:
/// - Decouples all connector logic from native MetaQuotes types so the solution builds
///   and tests pass without the MT5 Manager API DLLs on the machine.
/// - Enables a simulator for full end-to-end pipeline development without a live MT5 server.
/// - The rest of TIP.Connector depends ONLY on this interface — never on MetaQuotes types directly.
/// </summary>
public interface IMT5Api : IDisposable
{
    /// <summary>
    /// Initializes the MT5 Manager API factory. Must be called before Connect.
    /// </summary>
    /// <returns>True if initialization succeeded.</returns>
    bool Initialize();

    /// <summary>
    /// Connects to an MT5 server with manager credentials.
    /// </summary>
    /// <param name="server">Server address in "host:port" format.</param>
    /// <param name="login">Manager login number.</param>
    /// <param name="password">Manager password.</param>
    /// <param name="timeoutMs">Connection timeout in milliseconds.</param>
    /// <returns>True if connection succeeded.</returns>
    bool Connect(string server, ulong login, string password, uint timeoutMs = 30000);

    /// <summary>
    /// Disconnects from the MT5 server and releases resources.
    /// </summary>
    void Disconnect();

    /// <summary>
    /// Whether the API is currently connected to an MT5 server.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Fired when a new deal is added on the MT5 server (CIMTDealSink.OnDealAdd equivalent).
    /// </summary>
    event Action<RawDeal>? OnDealAdd;

    /// <summary>
    /// Fired when a deal is updated on the MT5 server (CIMTDealSink.OnDealUpdate equivalent).
    /// </summary>
    event Action<RawDeal>? OnDealUpdate;

    /// <summary>
    /// Subscribes to real-time deal events via the MT5 deal sink.
    /// </summary>
    /// <returns>True if subscription succeeded.</returns>
    bool SubscribeDeals();

    /// <summary>
    /// Unsubscribes from deal events.
    /// </summary>
    void UnsubscribeDeals();

    /// <summary>
    /// Fired when a tick (price update) is received from the MT5 server.
    /// </summary>
    event Action<RawTick>? OnTick;

    /// <summary>
    /// Subscribes to real-time tick events for symbols matching the mask.
    /// </summary>
    /// <param name="symbolMask">Symbol filter (e.g., "*" for all, "EUR*" for EUR pairs).</param>
    /// <returns>True if subscription succeeded.</returns>
    bool SubscribeTicks(string symbolMask = "*");

    /// <summary>
    /// Unsubscribes from tick events.
    /// </summary>
    void UnsubscribeTicks();

    /// <summary>
    /// Requests historical deals for a specific login within a time range.
    /// Used for backfill on startup and catch-up on reconnect.
    /// </summary>
    List<RawDeal> RequestDeals(ulong login, DateTimeOffset from, DateTimeOffset toTime);

    /// <summary>
    /// Requests historical ticks for a symbol within a time range.
    /// Used for backfill on startup and catch-up on reconnect.
    /// </summary>
    List<RawTick> RequestTicks(string symbol, DateTimeOffset from, DateTimeOffset toTime);

    /// <summary>
    /// Gets all user logins matching the group mask.
    /// </summary>
    /// <param name="groupMask">Group filter (e.g., "*" for all, "real\*" for real accounts).</param>
    ulong[] GetUserLogins(string groupMask);

    /// <summary>
    /// Gets user/account details for a specific login.
    /// </summary>
    /// <param name="login">Account login number.</param>
    /// <returns>User data, or null if not found.</returns>
    RawUser? GetUser(ulong login);

    /// <summary>
    /// Gets all available trading symbols from the MT5 server.
    /// </summary>
    List<RawSymbol> GetSymbols();
}
