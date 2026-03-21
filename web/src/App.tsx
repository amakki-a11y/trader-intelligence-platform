import { useState, useEffect, useCallback, useRef } from "react";
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
import Skeleton from "./components/shared/Skeleton";
import { AuthProvider, useAuth } from "./contexts/AuthContext";
import { apiFetch } from "./services/api";

function AppContent() {
  const { user, isAuthenticated, isLoading, logout } = useAuth();
  const [view, setView] = useState("market");
  const version = "v2";
  const [selectedAccount, setSelectedAccount] = useState<Account | null>(null);
  const [isLive, setIsLive] = useState(false);
  const [flashRows, setFlashRows] = useState<Set<number>>(new Set());
  const [accounts, setAccounts] = useState<Account[]>([]);
  const connectionStatus = useConnectionStatus();
  const [now, setNow] = useState(new Date());

  // Live clock
  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 1000);
    return () => clearInterval(timer);
  }, []);

  // Auto-start live when switching to live view
  useEffect(() => {
    if (view === "live" && !isLive) setIsLive(true);
  }, [view, isLive]);

  // Fetch accounts
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

  const critCount = accounts.filter(a => a.sev === "CRITICAL").length;

  // CRITICAL alert banner pulse tracking
  const prevCritCountRef = useRef(critCount);
  const [bannerPulse, setBannerPulse] = useState(false);
  useEffect(() => {
    if (critCount > prevCritCountRef.current) {
      setBannerPulse(true);
      const timer = setTimeout(() => setBannerPulse(false), 600);
      prevCritCountRef.current = critCount;
      return () => clearTimeout(timer);
    }
    prevCritCountRef.current = critCount;
  }, [critCount]);

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

  // Keyboard navigation
  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && selectedAccount) {
        setSelectedAccount(null);
      }
      if (e.ctrlKey && e.shiftKey) {
        const viewMap: Record<string, string> = { "1": "market", "2": "grid", "3": "live", "4": "threats" };
        if (viewMap[e.key]) {
          e.preventDefault();
          setView(viewMap[e.key]!);
          setSelectedAccount(null);
        }
      }
    };
    window.addEventListener("keydown", handleKey);
    return () => window.removeEventListener("keydown", handleKey);
  }, [selectedAccount]);

  // Loading state
  if (isLoading) {
    return (
      <div style={{
        width: "100%", height: "100vh", display: "flex",
        alignItems: "center", justifyContent: "center",
        background: C.bg2, color: C.t3, fontFamily: "'DM Sans',system-ui,sans-serif",
        flexDirection: "column", gap: 16,
      }}>
        <div style={{ width: 400, display: "flex", flexDirection: "column", gap: 8 }}>
          {[1,2,3,4,5].map(i => <Skeleton key={i} height={32} />)}
        </div>
      </div>
    );
  }

  if (!isAuthenticated || !user) {
    return <LoginPage />;
  }

  if (user.mustChangePassword) {
    return <ChangePasswordPage />;
  }

  return (
    <div style={{
      width: "100%", height: "100vh", display: "flex",
      background: C.bg2, color: C.t1, fontFamily: "'DM Sans',system-ui,sans-serif", overflow: "hidden",
    }}>
      <style>{`
        @keyframes pulse { 0%,100% { opacity:1; } 50% { opacity:0.4; } }
        @keyframes flashRow { 0% { background:rgba(255,82,82,0.08); } 100% { background:rgba(255,82,82,0.2); } }
        @keyframes skeletonPulse { 0% { background-position:200% 0; } 100% { background-position:-200% 0; } }
        @keyframes bannerPulse { 0% { opacity:1; } 50% { opacity:0.7; } 100% { opacity:1; } }
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
        critCount={critCount}
        user={user}
        onLogout={logout}
      />
      <div style={{ flex: 1, display: "flex", flexDirection: "column", overflow: "hidden" }}>
        <TopBar view={view} accounts={accounts} isLive={isLive} onToggleLive={() => setIsLive(v => !v)} onScan={handleScan} scanning={scanning} />
        {/* CRITICAL Alert Banner */}
        {critCount > 0 && !selectedAccount && (
          <div style={{
            padding: "8px 16px", display: "flex", alignItems: "center", gap: 10,
            background: "rgba(255,82,82,0.12)", borderLeft: `3px solid ${C.red}`,
            animation: bannerPulse ? "bannerPulse 0.6s ease-in-out" : "none",
          }}>
            <span style={{ fontSize: 12, color: C.red, fontWeight: 600 }}>
              {"\u26A0"} {critCount} CRITICAL account(s) require attention
            </span>
            <button onClick={() => { setView("grid"); }} style={{
              padding: "3px 10px", borderRadius: 4, border: `1px solid ${C.red}40`,
              background: "rgba(255,82,82,0.15)", color: C.red, fontSize: 10, fontWeight: 600,
              fontFamily: "'JetBrains Mono',monospace", cursor: "pointer",
            }}>VIEW</button>
          </div>
        )}
        {selectedAccount ? (
          <ErrorBoundary name="AccountDetail"><AccountDetail account={selectedAccount} version={version} onBack={() => setSelectedAccount(null)} /></ErrorBoundary>
        ) : (
          <>
            <div style={{ display: view === "market" ? "flex" : "none", flex: 1, flexDirection: "column", overflow: "hidden" }}>
              <ErrorBoundary name="MarketWatch"><MarketWatch isLive={isLive} /></ErrorBoundary>
            </div>
            <div style={{ display: view === "grid" ? "contents" : "none" }}>
              <ErrorBoundary name="AbuseGrid"><AbuseGrid accounts={accounts} version={version} onSelect={handleSelect} flashRows={flashRows} /></ErrorBoundary>
            </div>
            <div style={{ display: view === "live" ? "contents" : "none" }}>
              <ErrorBoundary name="LiveMonitor"><LiveMonitor accounts={accounts} isLive={isLive} onSelect={handleSelect} /></ErrorBoundary>
            </div>
            <div style={{ display: view === "threats" ? "contents" : "none" }}>
              <ErrorBoundary name="ThreatView"><ThreatView accounts={accounts} version={version} onSelect={handleSelect} /></ErrorBoundary>
            </div>
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
          <span>{user.displayName} ({user.role}) | {now.toLocaleDateString("en-GB")} {now.toLocaleTimeString("en-GB")}</span>
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
