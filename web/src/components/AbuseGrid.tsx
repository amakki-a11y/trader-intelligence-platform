import { useState, useMemo } from "react";
import type { CSSProperties, ReactNode } from "react";
import C, { sevColor } from "../styles/colors";
import type { Account, Threats } from "../store/TipStore";

function Badge({ color, children }: { color: string; children: ReactNode }) {
  return (
    <span style={{
      display: "inline-block", fontFamily: "'JetBrains Mono',monospace",
      fontSize: 10, fontWeight: 600, letterSpacing: "0.5px",
      color, background: color + "14", border: `1px solid ${color}40`,
      borderRadius: 4, padding: "2px 8px",
    }}>{children}</span>
  );
}

function ScoreBar({ score, width = 80 }: { score: number; width?: number }) {
  const pct = Math.min(100, Math.max(0, score));
  const color = sevColor(score);
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
      <div style={{ width, height: 5, borderRadius: 3, background: "rgba(255,255,255,0.06)" }}>
        <div style={{ width: `${pct}%`, height: "100%", borderRadius: 3, background: color, transition: "width 0.5s" }} />
      </div>
      <span style={{ fontFamily: "'JetBrains Mono',monospace", fontSize: 12, fontWeight: 600, color, minWidth: 24 }}>{score}</span>
    </div>
  );
}

function ThreatBars({ threats }: { threats: Threats }) {
  const items = [
    { key: "ring" as const, label: "Ring", color: C.red },
    { key: "latency" as const, label: "Lat", color: C.coral },
    { key: "bonus" as const, label: "Bon", color: C.amber },
    { key: "bot" as const, label: "Bot", color: C.purple },
  ];
  return (
    <div style={{ display: "flex", gap: 3, alignItems: "end", height: 24 }}>
      {items.map(({ key, label, color }) => {
        const v = threats[key];
        return (
          <div key={key} style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 1 }}>
            <div style={{
              width: 14, height: Math.max(2, v * 22), borderRadius: 2,
              background: v > 0.5 ? color : "rgba(255,255,255,0.08)", transition: "height 0.4s",
            }} />
            <span style={{ fontSize: 7, color: C.t3, fontFamily: "'JetBrains Mono',monospace" }}>{label}</span>
          </div>
        );
      })}
    </div>
  );
}

function RoutingBadge({ routing }: { routing: string }) {
  const colors: Record<string, string> = { "A-Book": C.red, "Review": C.amber, "B-Book": C.green };
  return <Badge color={colors[routing] ?? C.t3}>{routing}</Badge>;
}

interface AccountGridProps {
  accounts: Account[];
  version: string;
  onSelect: (a: Account) => void;
  flashRows: Set<number>;
}

function AbuseGrid({ accounts, version, onSelect, flashRows }: AccountGridProps) {
  const [sortCol, setSortCol] = useState("score");
  const [sortDir, setSortDir] = useState(-1);
  const [filter, setFilter] = useState("");
  const sorted = useMemo(() => {
    let f = accounts;
    if (filter) f = f.filter(a =>
      a.login.toString().includes(filter) || a.name.toLowerCase().includes(filter.toLowerCase()) ||
      a.sev.toLowerCase().includes(filter.toLowerCase()) || a.ib.includes(filter)
    );
    return [...f].sort((a, b) => {
      const av = (a as unknown as Record<string, unknown>)[sortCol];
      const bv = (b as unknown as Record<string, unknown>)[sortCol];
      if (typeof av === "number" && typeof bv === "number") return (av - bv) * sortDir;
      return String(av).localeCompare(String(bv)) * sortDir;
    });
  }, [accounts, sortCol, sortDir, filter]);
  const toggleSort = (col: string) => {
    if (sortCol === col) setSortDir(d => d * -1);
    else { setSortCol(col); setSortDir(-1); }
  };
  const cols = [
    { key: "login", label: "Login", w: 70 },
    { key: "name", label: "Name", w: 90 },
    { key: "score", label: "Score", w: 100 },
    { key: "sev", label: "Severity", w: 80 },
    ...(version === "v2" ? [{ key: "threats", label: "Threats", w: 90 }] : []),
    { key: "tradeCount", label: "Trades", w: 60 },
    { key: "volume", label: "Volume", w: 70 },
    { key: "commissions", label: "Comm.", w: 70 },
    { key: "pnl", label: "P&L", w: 80 },
    { key: "expertRatio", label: "EA%", w: 55 },
    { key: "ib", label: "IB", w: 72 },
    ...(version === "v2" ? [{ key: "routing", label: "Route", w: 64 }] : []),
  ];
  const thBase: CSSProperties = {
    padding: "8px 8px", textAlign: "left", fontSize: 10,
    fontFamily: "'JetBrains Mono',monospace", fontWeight: 600, color: C.t3,
    borderBottom: `1px solid ${C.border}`, position: "sticky", top: 0, background: C.bg2, zIndex: 2,
    letterSpacing: "0.5px", textTransform: "uppercase",
  };
  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", overflow: "hidden" }}>
      <div style={{ padding: "10px 16px", borderBottom: `1px solid ${C.border}`, display: "flex", gap: 10, alignItems: "center" }}>
        <input value={filter} onChange={e => setFilter(e.target.value)} placeholder="Filter by login, name, severity, IB..."
          style={{ flex: 1, maxWidth: 320, background: C.bg3, border: `1px solid ${C.border}`, borderRadius: 6, padding: "6px 12px", color: C.t1, fontSize: 12, fontFamily: "'JetBrains Mono',monospace", outline: "none" }} />
        <span style={{ fontSize: 11, color: C.t3 }}>{sorted.length} rows</span>
      </div>
      <div style={{ flex: 1, overflow: "auto" }}>
        <table style={{ width: "100%", borderCollapse: "collapse", tableLayout: "fixed" }}>
          <thead><tr>
            {cols.map(col => (
              <th key={col.key} onClick={() => col.key !== "threats" && toggleSort(col.key)}
                style={{ ...thBase, width: col.w, cursor: col.key !== "threats" ? "pointer" : "default" }}>
                {col.label} {sortCol === col.key ? (sortDir === 1 ? "\u2191" : "\u2193") : ""}
              </th>
            ))}
          </tr></thead>
          <tbody>
            {sorted.map(acc => {
              const isFlashing = flashRows.has(acc.login);
              return (
                <tr key={acc.login} onClick={() => onSelect(acc)} style={{
                  cursor: "pointer",
                  background: isFlashing ? "rgba(255,82,82,0.15)" : "transparent",
                  animation: isFlashing ? "flashRow 0.5s infinite alternate" : "none",
                  transition: "background 0.2s",
                }}
                  onMouseEnter={e => { if (!isFlashing) e.currentTarget.style.background = "rgba(255,255,255,0.03)"; }}
                  onMouseLeave={e => { if (!isFlashing) e.currentTarget.style.background = "transparent"; }}>
                  {cols.map(col => {
                    const tdS: CSSProperties = {
                      padding: "7px 8px", fontSize: 12, color: C.t2, borderBottom: `1px solid ${C.border}`,
                      fontFamily: ["login","ib","score"].includes(col.key) ? "'JetBrains Mono',monospace" : "inherit",
                    };
                    let content: ReactNode;
                    if (col.key === "score") content = <ScoreBar score={acc.score} />;
                    else if (col.key === "sev") content = <Badge color={sevColor(acc.score)}>{acc.sev}</Badge>;
                    else if (col.key === "threats") content = <ThreatBars threats={acc.threats} />;
                    else if (col.key === "routing") content = <RoutingBadge routing={acc.routing} />;
                    else if (col.key === "pnl") content = <span style={{ color: acc.pnl >= 0 ? C.green : C.red }}>{acc.pnl >= 0 ? "+" : ""}{acc.pnl.toLocaleString()}</span>;
                    else if (col.key === "expertRatio") content = `${(acc.expertRatio * 100).toFixed(0)}%`;
                    else if (col.key === "volume") content = acc.volume.toLocaleString();
                    else if (col.key === "commissions") content = `$${acc.commissions.toLocaleString()}`;
                    else content = String((acc as unknown as Record<string, unknown>)[col.key] ?? "");
                    return <td key={col.key} style={tdS}>{content}</td>;
                  })}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

export default AbuseGrid;
