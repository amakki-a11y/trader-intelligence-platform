import { useState, useEffect } from "react";
import type { ConnectionStatus } from "../store/TipStore";
import { apiFetch } from "../services/api";

/**
 * Polls the backend for MT5 connection status every 5 seconds.
 * Uses apiFetch for JWT auth.
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
        const res = await apiFetch("/api/settings/connection/status", { signal: controller.signal });
        if (res.ok && active) setStatus(await res.json() as ConnectionStatus);
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === "AbortError") return;
        // Don't log 401 errors (expected when not logged in)
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
