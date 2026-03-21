import { useRef, useEffect } from "react";
import type { CSSProperties } from "react";
import C, { sevColor } from "../../styles/colors";
import type { Account } from "../../store/TipStore";
import Badge from "../shared/Badge";

interface TopBarProps {
  view: string;
  accounts: Account[];
  isLive: boolean;
  onToggleLive: () => void;
  onScan: () => void;
  scanning: boolean;
}

function TopBar({ view, accounts, isLive, onToggleLive, onScan, scanning }: TopBarProps) {
  const critCount = accounts.filter(a => a.sev === "CRITICAL").length;
  const highCount = accounts.filter(a => a.sev === "HIGH").length;
  const prevCritRef = useRef(critCount);
  const critBadgeRef = useRef<HTMLSpanElement>(null);

  useEffect(() => {
    if (critCount !== prevCritRef.current && critBadgeRef.current) {
      const el = critBadgeRef.current;
      el.style.transform = "scale(1.25)";
      const timer = setTimeout(() => { el.style.transform = "scale(1)"; }, 250);
      prevCritRef.current = critCount;
      return () => clearTimeout(timer);
    }
    prevCritRef.current = critCount;
  }, [critCount]);

  const titles: Record<string, string> = {
    grid: "Account Scanner", live: "Live Monitor", market: "Market Watch",
    threats: "Threat Intelligence", settings: "Settings",
  };
  return (
    <div style={{
      height: 52, padding: "0 20px", display: "flex", alignItems: "center",
      borderBottom: `1px solid ${C.border}`, background: C.bg2, gap: 16,
    }}>
      <h1 style={{ fontSize: 15, fontWeight: 600, color: C.t1, letterSpacing: "-0.3px", margin: 0 }}>
        {titles[view] || "Settings"}
      </h1>
      <div style={{ flex: 1 }} />
      <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
        <span style={{ fontSize: 11, color: C.t3, fontFamily: "'JetBrains Mono',monospace" }}>
          {accounts.length} accounts
        </span>
        <span ref={critBadgeRef} style={{ transition: "transform 0.25s ease-out", display: "inline-block" } as CSSProperties}>
          <Badge color={sevColor(70)}>{critCount} CRIT</Badge>
        </span>
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

export default TopBar;
