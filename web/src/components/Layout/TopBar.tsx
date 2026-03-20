import type { ReactNode } from "react";
import C, { sevColor } from "../../styles/colors";
import type { Account } from "../../store/TipStore";

function Badge({ color, children, small }: { color: string; children: ReactNode; small?: boolean }) {
  return (
    <span style={{
      display: "inline-block", fontFamily: "'JetBrains Mono',monospace",
      fontSize: small ? 9 : 10, fontWeight: 600, letterSpacing: "0.5px",
      color, background: color + "14", border: `1px solid ${color}40`,
      borderRadius: 4, padding: small ? "1px 6px" : "2px 8px",
    }}>{children}</span>
  );
}

interface User { displayName: string; role: string; }

interface TopBarProps {
  view: string;
  accounts: Account[];
  version: string;
  isLive: boolean;
  onToggleLive: () => void;
  onScan: () => void;
  scanning: boolean;
  user?: User | null;
}

function TopBar({ view, accounts, version, isLive, onToggleLive, onScan, scanning, user: _user }: TopBarProps) {
  const critCount = accounts.filter(a => a.sev === "CRITICAL").length;
  const highCount = accounts.filter(a => a.sev === "HIGH").length;
  const titles: Record<string, string> = {
    grid: "Account Scanner", live: "Live Monitor", market: "Market Watch",
    threats: "Threat Intelligence", settings: "Settings",
    "admin-users": "User Management", "admin-servers": "MT5 Servers",
    "admin-roles": "Roles & Permissions", "change-password": "Change Password",
  };
  return (
    <div style={{
      height: 52, padding: "0 20px", display: "flex", alignItems: "center",
      borderBottom: `1px solid ${C.border}`, background: C.bg2, gap: 16,
    }}>
      <h1 style={{ fontSize: 15, fontWeight: 600, color: C.t1, letterSpacing: "-0.3px", margin: 0 }}>
        {titles[view]}
      </h1>
      {version === "v2" && <Badge color={C.purple} small>v2 PREVIEW</Badge>}
      <div style={{ flex: 1 }} />
      <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
        <span style={{ fontSize: 11, color: C.t3, fontFamily: "'JetBrains Mono',monospace" }}>
          {accounts.length} accounts
        </span>
        <Badge color={sevColor(70)}>{critCount} CRIT</Badge>
        <Badge color={sevColor(50)}>{highCount} HIGH</Badge>
      </div>
      {view === "live" && (
        <button onClick={onToggleLive} style={{
          padding: "6px 14px", borderRadius: 6, border: "none", cursor: "pointer",
          background: isLive ? C.red : C.teal, color: "#fff", fontSize: 11, fontWeight: 600,
          fontFamily: "'JetBrains Mono',monospace", display: "flex", alignItems: "center", gap: 6,
        }}>
          <span style={{ width: 6, height: 6, borderRadius: "50%", background: "#fff", animation: isLive ? "pulse 1s infinite" : "none" }} />
          {isLive ? "STOP" : "GO LIVE"}
        </button>
      )}
      {view === "grid" && (
        <button onClick={onScan} disabled={scanning} style={{
          padding: "6px 14px", borderRadius: 6, border: `1px solid ${C.teal}40`,
          background: scanning ? C.border : C.tealBg, color: scanning ? C.t3 : C.teal, fontSize: 11, fontWeight: 600,
          fontFamily: "'JetBrains Mono',monospace", cursor: scanning ? "wait" : "pointer",
          opacity: scanning ? 0.7 : 1,
        }}>{scanning ? "SCANNING..." : "SCAN"}</button>
      )}
    </div>
  );
}

export { Badge };
export default TopBar;
