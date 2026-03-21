import { useState, useMemo, useEffect, useRef, useCallback } from "react";
import type { CSSProperties, ReactNode } from "react";
import C, { sevColor } from "../styles/colors";
import type { Account } from "../store/TipStore";
import Badge from "./shared/Badge";
import ScoreBar from "./shared/ScoreBar";
import ThreatBars from "./shared/ThreatBars";
import RoutingBadge from "./shared/RoutingBadge";
import { thStyle as sharedTh } from "./shared/TableStyles";

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
  const [selectedIndex, setSelectedIndex] = useState(-1);
  const tableRef = useRef<HTMLDivElement>(null);
  const filterRef = useRef<HTMLInputElement>(null);

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

  // Reset selected index when sort/filter changes
  useEffect(() => { setSelectedIndex(-1); }, [sortCol, sortDir, filter]);

  const toggleSort = (col: string) => {
    if (sortCol === col) setSortDir(d => d * -1);
    else { setSortCol(col); setSortDir(-1); }
  };

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setSelectedIndex(i => Math.min(i + 1, sorted.length - 1));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setSelectedIndex(i => Math.max(i - 1, 0));
    } else if (e.key === "Enter" && selectedIndex >= 0 && sorted[selectedIndex]) {
      onSelect(sorted[selectedIndex]);
    } else if ((e.ctrlKey && e.key === "f") || e.key === "/") {
      e.preventDefault();
      filterRef.current?.focus();
    }
  }, [sorted, selectedIndex, onSelect]);

  const cols = [
    { key: "login", label: "Login", w: 90 },
    { key: "name", label: "Name", w: 120 },
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

  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", overflow: "hidden" }}>
      <div style={{ padding: "10px 16px", borderBottom: `1px solid ${C.border}`, display: "flex", gap: 10, alignItems: "center" }}>
        <input ref={filterRef} value={filter} onChange={e => setFilter(e.target.value)} placeholder="Filter by login, name, severity, IB..."
          style={{ flex: 1, maxWidth: 320, background: C.bg3, border: `1px solid ${C.border}`, borderRadius: 6, padding: "6px 12px", color: C.t1, fontSize: 12, fontFamily: "'JetBrains Mono',monospace", outline: "none" }} />
        <span style={{ fontSize: 11, color: C.t3 }}>{sorted.length} rows</span>
      </div>
      <div ref={tableRef} tabIndex={0} onKeyDown={handleKeyDown}
        style={{ flex: 1, overflow: "auto", outline: "none" }}>
        <table style={{ width: "100%", borderCollapse: "collapse", tableLayout: "fixed" }}>
          <thead><tr>
            {cols.map(col => (
              <th key={col.key} onClick={() => col.key !== "threats" && toggleSort(col.key)}
                style={{ ...sharedTh, width: col.w, cursor: col.key !== "threats" ? "pointer" : "default" }}>
                {col.label} {sortCol === col.key ? (sortDir === 1 ? "\u2191" : "\u2193") : ""}
              </th>
            ))}
          </tr></thead>
          <tbody>
            {sorted.map((acc, idx) => {
              const isFlashing = flashRows.has(acc.login);
              const isSelected = idx === selectedIndex;
              return (
                <tr key={acc.login} onClick={() => onSelect(acc)} style={{
                  cursor: "pointer",
                  background: isFlashing ? "rgba(255,82,82,0.15)" : isSelected ? "rgba(61,217,160,0.08)" : idx % 2 === 1 ? "rgba(255,255,255,0.015)" : "transparent",
                  animation: isFlashing ? "flashRow 0.5s infinite alternate" : "none",
                  transition: "background 0.2s",
                  outline: isSelected ? `1px solid ${C.teal}40` : "none",
                }}
                  onMouseEnter={e => { if (!isFlashing && !isSelected) e.currentTarget.style.background = "rgba(255,255,255,0.03)"; }}
                  onMouseLeave={e => { if (!isFlashing && !isSelected) e.currentTarget.style.background = idx % 2 === 1 ? "rgba(255,255,255,0.015)" : "transparent"; }}>
                  {cols.map(col => {
                    const isNumeric = ["volume", "commissions", "pnl", "tradeCount", "expertRatio"].includes(col.key);
                    const tdS: CSSProperties = {
                      padding: "7px 8px", fontSize: 12, color: C.t2, borderBottom: `1px solid ${C.border}`,
                      fontFamily: ["login","ib","score","volume","commissions","pnl","tradeCount","expertRatio"].includes(col.key) ? "'JetBrains Mono',monospace" : "inherit",
                      textAlign: isNumeric ? "right" : "left",
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
