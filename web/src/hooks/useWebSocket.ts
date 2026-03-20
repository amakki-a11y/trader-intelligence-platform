import { useEffect, useRef, useState, useCallback } from "react";
import { getAccessToken } from "../services/api";

/**
 * Custom hook for WebSocket connections with exponential backoff (FIX 2)
 * and proper timer cleanup on unmount (FIX 5).
 * JWT token sent as query parameter for authentication.
 *
 * @param channels - array of channel names to subscribe to (e.g., ["prices"], ["deals"])
 * @param enabled - whether the WebSocket should be active
 * @param onMessage - callback for incoming messages
 * @returns { status, wsRef }
 */
export type WsStatus = "disconnected" | "connecting" | "connected" | "reconnecting";

interface UseWebSocketOptions {
  channels: string[];
  enabled: boolean;
  onMessage: (msg: { type: string; data: Record<string, unknown> }) => void;
  staleTimeoutMs?: number;
}

export default function useWebSocket({ channels, enabled, onMessage, staleTimeoutMs = 30000 }: UseWebSocketOptions): {
  status: WsStatus;
  wsRef: React.RefObject<WebSocket | null>;
} {
  const [status, setStatus] = useState<WsStatus>("disconnected");
  const wsRef = useRef<WebSocket | null>(null);
  const onMessageRef = useRef(onMessage);
  onMessageRef.current = onMessage;

  const channelsKey = channels.join(",");

  const connect = useCallback(() => {
    // This is just a placeholder; the actual connect logic is in useEffect
  }, []);
  void connect; // suppress unused warning

  useEffect(() => {
    if (!enabled) {
      if (wsRef.current) { wsRef.current.close(); wsRef.current = null; }
      setStatus("disconnected");
      return;
    }

    let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    let staleCheckTimer: ReturnType<typeof setInterval> | null = null;
    let attempt = 0;
    let lastMessageAt = Date.now();
    let ws: WebSocket;
    let disposed = false;

    const doConnect = () => {
      if (disposed) return;
      setStatus(attempt === 0 ? "connecting" : "reconnecting");
      const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
      const token = getAccessToken();
      const tokenParam = token ? `?token=${encodeURIComponent(token)}` : "";
      ws = new WebSocket(`${proto}//${window.location.host}/ws${tokenParam}`);
      wsRef.current = ws;
      lastMessageAt = Date.now();

      ws.onopen = () => {
        if (disposed) { ws.close(); return; }
        setStatus("connected");
        attempt = 0; // FIX 2: reset attempt on success
        ws.send(JSON.stringify({ subscribe: channelsKey.split(",") }));
      };

      ws.onmessage = (ev) => {
        lastMessageAt = Date.now();
        try {
          const msg = JSON.parse(ev.data) as { type: string; data: Record<string, unknown> };
          onMessageRef.current(msg);
        } catch { /* ignore parse errors */ }
      };

      ws.onclose = () => {
        if (disposed) return;
        setStatus("disconnected");
        wsRef.current = null;
        // FIX 2: exponential backoff — Math.min(1000 * 2^attempt, 30000)
        const delay = Math.min(1000 * Math.pow(2, attempt), 30000);
        attempt++;
        console.log(`[useWebSocket] reconnect attempt ${attempt} in ${delay}ms`);
        reconnectTimer = setTimeout(doConnect, delay);
      };

      ws.onerror = (ev) => {
        console.error("[useWebSocket] WebSocket error:", ev);
        ws.close();
      };
    };

    doConnect();

    // Stale connection detector
    if (staleTimeoutMs > 0) {
      staleCheckTimer = setInterval(() => {
        if (wsRef.current && wsRef.current.readyState === WebSocket.OPEN && Date.now() - lastMessageAt > staleTimeoutMs) {
          console.warn("[useWebSocket] Stale connection detected — forcing reconnect");
          setStatus("reconnecting");
          wsRef.current.close();
        }
      }, 5000);
    }

    // FIX 5: cleanup all timers on unmount
    return () => {
      disposed = true;
      if (reconnectTimer) clearTimeout(reconnectTimer);
      if (staleCheckTimer) clearInterval(staleCheckTimer);
      if (wsRef.current) { wsRef.current.close(); wsRef.current = null; }
      setStatus("disconnected");
    };
  }, [enabled, channelsKey, staleTimeoutMs]);

  return { status, wsRef };
}
