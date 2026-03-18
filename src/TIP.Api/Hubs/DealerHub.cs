using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TIP.Api.Hubs;

/// <summary>
/// WebSocket server that pushes real-time data to connected dealer dashboard clients.
///
/// Design rationale:
/// - Clients send a subscribe message on connect: { "subscribe": ["prices","accounts",...] }
/// - Only subscribed message types are pushed to each client (saves bandwidth).
/// - Tracks clients via ConcurrentDictionary for thread-safe add/remove.
/// - Prices are forwarded instantly with zero throttle for real-time accuracy.
/// - Scores and alerts are sent immediately (low frequency, high importance).
/// - All outbound messages follow: { "type": "prices"|"accounts"|"positions"|"alerts", "data": ... }
/// </summary>
public sealed class DealerHub : IWebSocketBroadcaster
{
    private readonly ILogger<DealerHub> _logger;
    private readonly ConcurrentDictionary<string, ClientState> _clients = new();
    // Price throttle removed — ticks forwarded instantly for real-time accuracy
    private long _lastPositionBroadcast;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Tracks a single connected client's WebSocket and subscriptions.
    /// </summary>
    private sealed class ClientState
    {
        public WebSocket Socket { get; }
        public HashSet<string> Subscriptions { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ClientState(WebSocket socket)
        {
            Socket = socket;
        }
    }

    /// <summary>
    /// Initializes the dealer hub.
    /// </summary>
    public DealerHub(ILogger<DealerHub> logger)
    {
        _logger = logger;
    }

    /// <summary>Number of connected clients.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>
    /// Handles a new WebSocket connection. Reads subscribe messages, keeps alive until disconnect.
    /// </summary>
    public async Task HandleConnection(WebSocket ws, CancellationToken ct)
    {
        var clientId = Guid.NewGuid().ToString("N")[..8];
        var state = new ClientState(ws);
        _clients[clientId] = state;
        _logger.LogInformation("WS client connected: {ClientId} ({Count} total)", clientId, _clients.Count);

        try
        {
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                {
                    HandleClientMessage(clientId, state, buffer, result.Count);
                }
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            _clients.TryRemove(clientId, out _);
            _logger.LogInformation("WS client disconnected: {ClientId} ({Count} remaining)", clientId, _clients.Count);

            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// Parses a client subscribe message and updates their subscription set.
    /// Expected format: { "subscribe": ["prices", "accounts", "positions", "alerts"] }
    /// </summary>
    private void HandleClientMessage(string clientId, ClientState state, byte[] buffer, int count)
    {
        try
        {
            var json = Encoding.UTF8.GetString(buffer, 0, count);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("subscribe", out var subs) && subs.ValueKind == JsonValueKind.Array)
            {
                state.Subscriptions.Clear();
                foreach (var item in subs.EnumerateArray())
                {
                    var channel = item.GetString();
                    if (!string.IsNullOrEmpty(channel))
                        state.Subscriptions.Add(channel);
                }
                _logger.LogDebug("Client {ClientId} subscribed to: {Subs}", clientId, string.Join(", ", state.Subscriptions));
            }
        }
        catch (JsonException)
        {
            // Ignore malformed messages
        }
    }

    /// <inheritdoc/>
    public async Task BroadcastAccountUpdate(AccountSummaryDto account)
    {
        await Broadcast("accounts", account).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task BroadcastPriceUpdate(SymbolPriceDto price)
    {
        await Broadcast("prices", price).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task BroadcastPositionUpdate(PositionSummaryDto position)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - Interlocked.Read(ref _lastPositionBroadcast) < 1000)
            return;

        Interlocked.Exchange(ref _lastPositionBroadcast, now);
        await Broadcast("positions", position).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task BroadcastAlert(AlertMessageDto alert)
    {
        await Broadcast("alerts", alert).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task BroadcastDealEvent(DealEventDto deal)
    {
        await Broadcast("deals", deal).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task BroadcastConnectionStatus(ConnectionStatusDto status)
    {
        await Broadcast("connection", status).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a typed JSON message to all clients subscribed to the given channel.
    /// </summary>
    private async Task Broadcast(string type, object data)
    {
        if (_clients.IsEmpty)
            return;

        var message = JsonSerializer.Serialize(new { type, data }, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(message);
        var segment = new ArraySegment<byte>(bytes);

        foreach (var (clientId, state) in _clients)
        {
            if (state.Socket.State != WebSocketState.Open)
            {
                _clients.TryRemove(clientId, out _);
                continue;
            }

            // Only send if client subscribed to this channel (or has no subscriptions = all)
            if (state.Subscriptions.Count > 0 && !state.Subscriptions.Contains(type))
                continue;

            try
            {
                await state.Socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                _clients.TryRemove(clientId, out _);
            }
        }
    }
}
