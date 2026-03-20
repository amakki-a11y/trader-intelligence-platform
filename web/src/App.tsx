import { useState, useEffect, useCallback } from "react";
import C from "./styles/colors";
import type { Account } from "./store/TipStore";
import { mapAccountResponse } from "./store/TipStore";
import type { AccountResponse } from "./types/api";
import useConnectionStatus from "./hooks/useConnectionStatus";
import ErrorBoundary from "./components/ErrorBoundary";
import Sidebar from "./components/Layout/Sidebar";
import TopBar from "./components/Layout/TopBar";
import MarketWatch from "./components/MarketWatch";
import AbuseGrid from "./components/AbuseGrid";
import AccountDetail from "./components/AccountDetail";
import LiveMonitor from "./components/LiveMonitor";
import ThreatView from "./components/ThreatView";
import SettingsView from "./components/SettingsView";
import LoginPage from "./components/LoginPage";
import ChangePasswordPage from "./components/ChangePasswordPage";
import { AuthProvider, useAuth } from "./contexts/AuthContext";
import { apiFetch } from "./services/api";

function AppContent() {
  const { user, isAuthenticated, isLoading, logout } = useAuth();
  const [view, setView] = useState("market");
  const version = "v2"; // v2.0 is production — no more v1/v2 toggle
  const [selectedAccount, setSelectedAccount] = useState<Account | null>(null);
  const [isLive, setIsLive] = useState(false);
  const [flashRows, setFlashRows] = useState<Set<number>>(new Set());
  const [accounts, setAccounts] = useState<Account[]>([]);
  const connectionStatus = useConnectionStatus();

  // FIX 3+4+5: Fetch real accounts with AbortController, error logging, cleanup
  useEffect(() => {
    if (!isAuthenticated) return;
    const controller = new AbortController();
    const fetchAccounts = async () => {
      try {
        const res = await apiFetch("/api/accounts", { signal: controller.signal });
        if (!res.ok) return;
        const data = await res.json();
        if (!Array.isArray(data)) return;
        if (data.length === 0) { setAccounts([]); return; }
        const mapped: Account[] = (data as AccountResponse[]).map(mapAccountResponse);
        setAccounts(mapped.sort((a, b) => b.score - a.score));
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === "AbortError") return;
        console.error("[App] accounts fetch failed:", err);
      }
    };
    fetchAccounts();
    const interval = setInterval(fetchAccounts, 5000);
    return () => { clearInterval(interval); controller.abort(); };
  }, [isAuthenticated]);

  useEffect(() => {
    const critLogins = accounts.filter(a => a.sev === "CRITICAL").map(a => a.login);
    setFlashRows(new Set(critLogins));
  }, [accounts]);

  const [scanning, setScanning] = useState(false);

  const handleScan = useCallback(async () => {
    if (scanning) return;
    setScanning(true);
    try {
      const res = await apiFetch("/api/accounts/scan", { method: "POST" });
      if (res.ok) {
        const data = await res.json();
        console.log("[SCAN]", data);
        const accRes = await apiFetch("/api/accounts");
        if (accRes.ok) {
          const accData = await accRes.json();
          if (Array.isArray(accData) && accData.length > 0) {
            const mapped: Account[] = (accData as AccountResponse[]).map(mapAccountResponse);
            setAccounts(mapped.sort((a, b) => b.score - a.score));
          }
        }
      } else {
        console.error("[SCAN] failed:", await res.text());
      }
    } catch (e) {
      console.error("[SCAN] error:", e);
    } finally {
      setScanning(false);
    }
  }, [scanning]);

  const handleSelect = (acc: Account) => { setSelectedAccount(acc); };

  // Loading state
  if (isLoading) {
    return (
      <div style={{
        width: "100%", height: "100vh", display: "flex",
        alignItems: "center", justifyContent: "center",
        background: C.bg2, color: C.t3, fontFamily: "'DM Sans',system-ui,sans-serif",
      }}>Loading...</div>
    );
  }

  // Not authenticated → show login
  if (!isAuthenticated || !user) {
    return <LoginPage />;
  }

  // Force password change
  if (user.mustChangePassword) {
    return <ChangePasswordPage />;
  }

  return (
    <div style={{
      width: "100%", height: "100vh", display: "flex",
      background: C.bg2, color: C.t1, fontFamily: "'DM Sans',system-ui,sans-serif", overflow: "hidden",
    }}>
      <style>{`
        @import url('https://fonts.googleapis.com/css2?family=DM+Sans:ital,opsz,wght@0,9..40,400;0,9..40,500;0,9..40,600;0,9..40,700&family=JetBrains+Mono:wght@400;500;600;700&display=swap');
        @keyframes pulse { 0%,100% { opacity:1; } 50% { opacity:0.4; } }
        @keyframes flashRow { 0% { background:rgba(255,82,82,0.08); } 100% { background:rgba(255,82,82,0.2); } }
        * { margin:0; padding:0; box-sizing:border-box; }
        ::-webkit-scrollbar { width:6px; height:6px; }
        ::-webkit-scrollbar-track { background:transparent; }
        ::-webkit-scrollbar-thumb { background:rgba(255,255,255,0.1); border-radius:3px; }
        ::-webkit-scrollbar-thumb:hover { background:rgba(255,255,255,0.2); }
      `}</style>
      <Sidebar
        view={view}
        setView={(v) => { setView(v); setSelectedAccount(null); }}
        connected={connectionStatus.connected}
        user={user}
        onLogout={logout}
      />
      <div style={{ flex: 1, display: "flex", flexDirection: "column", overflow: "hidden" }}>
        <TopBar view={view} accounts={accounts} isLive={isLive} onToggleLive={() => setIsLive(v => !v)} onScan={handleScan} scanning={scanning} />
        {selectedAccount ? (
          <ErrorBoundary name="AccountDetail"><AccountDetail account={selectedAccount} version={version} onBack={() => setSelectedAccount(null)} /></ErrorBoundary>
        ) : (
          <>
            {view === "grid" && <ErrorBoundary name="AbuseGrid"><AbuseGrid accounts={accounts} version={version} onSelect={handleSelect} flashRows={flashRows} /></ErrorBoundary>}
            {view === "live" && <ErrorBoundary name="LiveMonitor"><LiveMonitor accounts={accounts} isLive={isLive} onSelect={handleSelect} /></ErrorBoundary>}
            {view === "market" && <ErrorBoundary name="MarketWatch"><MarketWatch isLive={isLive} /></ErrorBoundary>}
            {view === "threats" && <ErrorBoundary name="ThreatView"><ThreatView accounts={accounts} version={version} onSelect={handleSelect} /></ErrorBoundary>}
            {view === "settings" && <ErrorBoundary name="Settings"><SettingsView connectionStatus={connectionStatus} /></ErrorBoundary>}
          </>
        )}
        <div style={{
          padding: "6px 16px", borderTop: `1px solid ${C.border}`,
          display: "flex", alignItems: "center", justifyContent: "space-between",
          fontSize: 10, color: C.t3, fontFamily: "'JetBrains Mono',monospace",
        }}>
          <span>Trader Intelligence Platform v2.0 — BBC Corp</span>
          <span style={{ color: connectionStatus.connected ? C.teal : C.red }}>
            MT5: {connectionStatus.connected ? `${connectionStatus.server} — Connected` : "Disconnected"}
          </span>
          <span>{user.displayName} ({user.role}) | {new Date().toLocaleDateString("en-GB")} {new Date().toLocaleTimeString("en-GB")}</span>
        </div>
      </div>
    </div>
  );
}

export default function App() {
  return (
    <AuthProvider>
      <AppContent />
    </AuthProvider>
  );
}
