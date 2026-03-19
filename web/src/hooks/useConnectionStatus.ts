import { useState, useEffect } from "react";
import type { ConnectionStatus } from "../store/TipStore";

/**
 * Polls the backend for MT5 connection status every 5 seconds.
 * FIX 4: Uses AbortController to cancel in-flight requests on unmount.
 * FIX 5: Clears interval on unmount.
 */
export default function useConnectionStatus(): ConnectionStatus {
  const [status, setStatus] = useState<ConnectionStatus>({
    connected: false, server: "", login: "", accountsInScope: 0, uptimeSeconds: 0, error: null,
  });

  useEffect(() => {
    let active = true;
    const controller = new AbortController();

    const poll = async () => {
      try {
        const res = await fetch("/api/settings/connection/status", { signal: controller.signal });
        if (res.ok && active) setStatus(await res.json() as ConnectionStatus);
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === "AbortError") return;
        console.error("[useConnectionStatus] poll failed:", err);
      }
    };

    poll();
    const id = setInterval(poll, 5000);

    return () => {
      active = false;
      clearInterval(id);
      controller.abort();
    };
  }, []);

  return status;
}
