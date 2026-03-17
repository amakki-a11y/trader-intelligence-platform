using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TIP.Connector;
using TIP.Core.Engines;
using TIP.Core.Models;

namespace TIP.Api;

/// <summary>
/// WebSocket server that pushes real-time data to connected dashboard clients.
///
/// Design rationale:
/// - Tracks connected clients via ConcurrentDictionary for thread-safe add/remove.
/// - Throttles tick updates to max 1 per symbol per 500ms to avoid flooding.
/// - Score and deal updates are sent immediately (low frequency, high importance).
/// - Position P&amp;L updates throttled to max 1 per second.
/// - All messages are JSON with a "type" field for client-side dispatch.
/// </summary>
public sealed class WebSocketHub
{
    private readonly ILogger<WebSocketHub> _logger;
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly ConcurrentDictionary<string, long> _lastTickSent = new();
    private long _lastPositionBroadcast;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Initializes the WebSocket hub.
    /// </summary>
    public WebSocketHub(ILogger<WebSocketHub> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the number of connected clients.
    /// </summary>
    public int ClientCount => _clients.Count;

    /// <summary>
    /// Handles a new WebSocket connection. Keeps the socket alive until the client disconnects.
    /// </summary>
    public async Task HandleConnection(WebSocket ws, CancellationToken ct)
    {
        var clientId = Guid.NewGuid().ToString("N")[..8];
        _clients[clientId] = ws;
        _logger.LogInformation("WebSocket client connected: {ClientId} ({Count} total)", clientId, _clients.Count);

        try
        {
            var buffer = new byte[1024];
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (WebSocketException)
        {
            // Client disconnected
        }
        catch (OperationCanceledException)
        {
            // Server shutting down
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            _logger.LogInformation("WebSocket client disconnected: {ClientId} ({Count} remaining)", clientId, _clients.Count);

            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best effort close
                }
            }
        }
    }

    /// <summary>
    /// Broadcasts a tick update. Throttled to 1 per symbol per 500ms.
    /// </summary>
    public async Task BroadcastTick(TickEvent tick)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var key = tick.Symbol;

        if (_lastTickSent.TryGetValue(key, out var lastSent) && now - lastSent < 500)
            return;

        _lastTickSent[key] = now;

        await Broadcast("tick", new
        {
            symbol = tick.Symbol,
            bid = tick.Bid,
            ask = tick.Ask,
            timeMsc = tick.TimeMsc
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Broadcasts a deal event immediately.
    /// </summary>
    public async Task BroadcastDeal(DealEvent deal)
    {
        await Broadcast("deal", new
        {
            dealId = deal.DealId,
            login = deal.Login,
            symbol = deal.Symbol,
            action = deal.Action,
            volume = deal.Volume,
            price = deal.Price,
            profit = deal.Profit
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Broadcasts a score update immediately.
    /// </summary>
    public async Task BroadcastScoreUpdate(AccountAnalysis account)
    {
        var trend = account.AbuseScore > account.PreviousScore + 2 ? "up"
            : account.AbuseScore < account.PreviousScore - 2 ? "down"
            : "stable";

        await Broadcast("score", new
        {
            login = account.Login,
            score = account.AbuseScore,
            previousScore = account.PreviousScore,
            riskLevel = account.RiskLevel.ToString(),
            trend,
            totalTrades = account.TotalTrades,
            totalVolume = account.TotalVolume,
            totalProfit = account.TotalProfit,
            totalCommission = account.TotalCommission,
            totalDeposits = account.TotalDeposits,
            isRingMember = account.IsRingMember
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Broadcasts a position P&amp;L update. Throttled to 1 per second globally.
    /// </summary>
    public async Task BroadcastPositionUpdate(PnLResult pnl)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - Interlocked.Read(ref _lastPositionBroadcast) < 1000)
            return;

        Interlocked.Exchange(ref _lastPositionBroadcast, now);

        await Broadcast("position", new
        {
            positionId = pnl.PositionId,
            login = pnl.Login,
            symbol = pnl.Symbol,
            direction = pnl.Direction,
            volume = pnl.Volume,
            pnl = pnl.UnrealizedPnL
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Broadcasts an alert immediately.
    /// </summary>
    public async Task BroadcastAlert(ulong login, string message, string severity)
    {
        await Broadcast("alert", new
        {
            login,
            message,
            severity,
            timestamp = DateTimeOffset.UtcNow
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Broadcasts exposure update.
    /// </summary>
    public async Task BroadcastExposure(SymbolExposure exposure)
    {
        await Broadcast("exposure", new
        {
            symbol = exposure.Symbol,
            longVolume = exposure.LongVolume,
            shortVolume = exposure.ShortVolume,
            netVolume = exposure.NetVolume
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a typed JSON message to all connected clients.
    /// </summary>
    private async Task Broadcast(string type, object data)
    {
        if (_clients.IsEmpty)
            return;

        var message = JsonSerializer.Serialize(new { type, data }, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(message);
        var segment = new ArraySegment<byte>(bytes);

        foreach (var (clientId, ws) in _clients)
        {
            if (ws.State != WebSocketState.Open)
            {
                _clients.TryRemove(clientId, out _);
                continue;
            }

            try
            {
                await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                _clients.TryRemove(clientId, out _);
            }
        }
    }
}
