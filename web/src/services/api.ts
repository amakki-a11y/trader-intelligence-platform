/**
 * Authenticated fetch wrapper that auto-attaches JWT access tokens
 * and handles 401 refresh flow.
 *
 * Design rationale:
 * - Access token stored in memory (NOT localStorage — XSS protection).
 * - Refresh token stored as httpOnly cookie (set by backend).
 * - On 401, tries silent refresh once, then redirects to login.
 * - Provides typed wrapper for common API patterns.
 */

let accessToken: string | null = null;
let onAuthExpired: (() => void) | null = null;

/** Set the current access token (called after login/refresh). */
export function setAccessToken(token: string | null): void {
  accessToken = token;
}

/** Get the current access token (for WebSocket connections). */
export function getAccessToken(): string | null {
  return accessToken;
}

/** Register callback for when auth expires and cannot be refreshed. */
export function setOnAuthExpired(callback: () => void): void {
  onAuthExpired = callback;
}

/** Try to refresh the access token using the httpOnly cookie. */
export async function refreshAccessToken(): Promise<boolean> {
  try {
    const res = await fetch("/api/auth/refresh", {
      method: "POST",
      credentials: "include",
    });
    if (!res.ok) {
      accessToken = null;
      return false;
    }
    const data = await res.json();
    accessToken = data.accessToken;
    return true;
  } catch {
    accessToken = null;
    return false;
  }
}

/**
 * Authenticated fetch wrapper. Attaches JWT and handles 401 refresh.
 */
export async function apiFetch(
  url: string,
  options: RequestInit = {}
): Promise<Response> {
  const headers = new Headers(options.headers);
  if (accessToken) {
    headers.set("Authorization", `Bearer ${accessToken}`);
  }
  if (!headers.has("Content-Type") && options.body && typeof options.body === "string") {
    headers.set("Content-Type", "application/json");
  }

  const res = await fetch(url, {
    ...options,
    headers,
    credentials: "include",
  });

  // On 401, try refresh once
  if (res.status === 401 && accessToken) {
    const refreshed = await refreshAccessToken();
    if (refreshed) {
      // Retry the original request with new token
      const retryHeaders = new Headers(options.headers);
      retryHeaders.set("Authorization", `Bearer ${accessToken}`);
      if (!retryHeaders.has("Content-Type") && options.body && typeof options.body === "string") {
        retryHeaders.set("Content-Type", "application/json");
      }
      return fetch(url, {
        ...options,
        headers: retryHeaders,
        credentials: "include",
      });
    } else {
      // Refresh failed — trigger auth expired
      if (onAuthExpired) onAuthExpired();
    }
  }

  return res;
}
