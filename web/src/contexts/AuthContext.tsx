import { createContext, useContext, useState, useEffect, useCallback, type ReactNode } from "react";
import { setAccessToken, setOnAuthExpired, refreshAccessToken, apiFetch } from "../services/api";

/** User info from JWT/server. */
interface User {
  id: number;
  username: string;
  displayName: string;
  email: string;
  role: string;
  permissions: string[];
  serverIds: number[];
  mustChangePassword: boolean;
}

/** Auth state exposed to the app. */
interface AuthState {
  user: User | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  login: (username: string, password: string) => Promise<{ ok: boolean; error?: string }>;
  logout: () => Promise<void>;
  changePassword: (oldPwd: string, newPwd: string) => Promise<{ ok: boolean; error?: string }>;
  hasPermission: (perm: string) => boolean;
}

const AuthContext = createContext<AuthState | null>(null);

/** Hook to access auth state. Must be used within AuthProvider. */
export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within AuthProvider");
  return ctx;
}

/** Auth provider wrapping the app. Handles login, refresh, logout. */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  // Try silent refresh on mount (httpOnly cookie-based)
  useEffect(() => {
    let cancelled = false;
    const tryRefresh = async () => {
      const ok = await refreshAccessToken();
      if (ok && !cancelled) {
        // Fetch user info
        try {
          const res = await apiFetch("/api/auth/me");
          if (res.ok) {
            const u = await res.json();
            setUser(u);
          }
        } catch { /* ignore */ }
      }
      if (!cancelled) setIsLoading(false);
    };
    tryRefresh();
    return () => { cancelled = true; };
  }, []);

  // Register auth expired callback
  useEffect(() => {
    setOnAuthExpired(() => {
      setUser(null);
      setAccessToken(null);
    });
  }, []);

  // Auto-refresh before token expires (every 14 minutes)
  useEffect(() => {
    if (!user) return;
    const interval = setInterval(async () => {
      await refreshAccessToken();
    }, 14 * 60 * 1000);
    return () => clearInterval(interval);
  }, [user]);

  const login = useCallback(async (username: string, password: string) => {
    try {
      const res = await fetch("/api/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        credentials: "include",
        body: JSON.stringify({ username, password }),
      });
      const data = await res.json();
      if (!res.ok) return { ok: false, error: data.error || "Login failed" };

      setAccessToken(data.accessToken);
      setUser(data.user);
      return { ok: true };
    } catch (err) {
      return { ok: false, error: "Network error" };
    }
  }, []);

  const logout = useCallback(async () => {
    try {
      await fetch("/api/auth/logout", { method: "POST", credentials: "include" });
    } catch { /* ignore */ }
    setAccessToken(null);
    setUser(null);
  }, []);

  const changePassword = useCallback(async (oldPwd: string, newPwd: string) => {
    try {
      const res = await apiFetch("/api/auth/change-password", {
        method: "POST",
        body: JSON.stringify({ oldPassword: oldPwd, newPassword: newPwd }),
      });
      const data = await res.json();
      if (!res.ok) return { ok: false, error: data.error || "Failed" };
      // Password changed — must re-login
      setAccessToken(null);
      setUser(null);
      return { ok: true };
    } catch {
      return { ok: false, error: "Network error" };
    }
  }, []);

  const hasPermission = useCallback((perm: string) => {
    if (!user) return false;
    return user.permissions.includes(perm);
  }, [user]);

  return (
    <AuthContext.Provider value={{
      user,
      isAuthenticated: !!user,
      isLoading,
      login,
      logout,
      changePassword,
      hasPermission,
    }}>
      {children}
    </AuthContext.Provider>
  );
}
