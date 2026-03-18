using System.Threading.Tasks;

namespace TIP.Api.Hubs;

/// <summary>
/// Abstraction for broadcasting real-time updates to connected dashboard clients.
///
/// Design rationale:
/// - Decouples compute services from WebSocket transport details.
/// - Enables testing of broadcast logic without WebSocket connections.
/// - Implemented by DealerHub which manages client connections and throttling.
/// </summary>
public interface IWebSocketBroadcaster
{
    /// <summary>Pushes a scored account update to clients subscribed to "accounts".</summary>
    Task BroadcastAccountUpdate(AccountSummaryDto account);

    /// <summary>Pushes a price tick to clients subscribed to "prices".</summary>
    Task BroadcastPriceUpdate(SymbolPriceDto price);

    /// <summary>Pushes a position P&amp;L update to clients subscribed to "positions".</summary>
    Task BroadcastPositionUpdate(PositionSummaryDto position);

    /// <summary>Pushes an alert to clients subscribed to "alerts".</summary>
    Task BroadcastAlert(AlertMessageDto alert);

    /// <summary>Pushes a deal event to clients subscribed to "deals".</summary>
    Task BroadcastDealEvent(DealEventDto deal);

    /// <summary>Pushes connection status change to clients subscribed to "connection".</summary>
    Task BroadcastConnectionStatus(ConnectionStatusDto status);
}
