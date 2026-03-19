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

function RoutingBadge({ routing }: { routing: string }) {
  const colors: Record<string, string> = { "A-Book": C.red, "Review": C.amber, "B-Book": C.green };
  return <Badge color={colors[routing] ?? C.t3}>{routing}</Badge>;
}

interface ThreatViewProps {
  accounts: Account[];
  version: string;
  onSelect: (a: Account) => void;
}

function ThreatView({ accounts, version, onSelect }: ThreatViewProps) {
  const [selectedThreat, setSelectedThreat] = useState("all");
  const threats = [
    { id: "all", label: "All Threats", color: C.t1, icon: "\u25C6" },
    { id: "ring", label: "Ring Trading", color: C.red, icon: "\u27F7" },
    { id: "latency", label: "Latency Arb", color: C.coral, icon: "\u26A1" },
    { id: "bonus", label: "Bonus Abuse", color: C.amber, icon: "\u{1F4B0}" },
    { id: "bot", label: "Bot Farming", color: C.purple, icon: "\u{1F916}" },
  ];
  const filtered = useMemo(() => {
    if (selectedThreat === "all") return accounts.filter(a => Object.values(a.threats).some(v => v > 0.4));
    const key = selectedThreat as keyof Threats;
    return accounts.filter(a => a.threats[key] > 0.4).sort((a, b) => b.threats[key] - a.threats[key]);
  }, [accounts, selectedThreat]);

  if (version === "v1") {
    return (
      <div style={{ flex: 1, display: "flex", alignItems: "center", justifyContent: "center", flexDirection: "column", gap: 12 }}>
        <span style={{ fontSize: 40, opacity: 0.3 }}>{"\u25C6"}</span>
        <span style={{ fontSize: 14, color: C.t3 }}>Threat Intelligence is a v2 feature</span>
        <span style={{ fontSize: 12, color: C.t3 }}>Switch to v2 using the toggle in the sidebar</span>
      </div>
    );
  }
  const thStyle: CSSProperties = { padding: "8px 10px", fontSize: 9, fontFamily: "'JetBrains Mono',monospace", fontWeight: 600, color: C.t3, textAlign: "left", borderBottom: `1px solid ${C.border}`, position: "sticky", top: 0, background: C.bg2, textTransform: "uppercase", letterSpacing: "0.5px" };
  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", overflow: "hidden" }}>
      <div style={{ display: "flex", gap: 4, padding: "10px 16px", borderBottom: `1px solid ${C.border}` }}>
        {threats.map(t => (
          <button key={t.id} onClick={() => setSelectedThreat(t.id)} style={{
            padding: "5px 14px", borderRadius: 6, border: `1px solid ${selectedThreat === t.id ? t.color + "40" : C.border}`,
            background: selectedThreat === t.id ? t.color + "14" : "transparent",
            color: selectedThreat === t.id ? t.color : C.t3,
            fontSize: 11, fontWeight: 500, cursor: "pointer", display: "flex", alignItems: "center", gap: 5,
          }}>
            <span style={{ fontSize: 12 }}>{t.icon}</span>
            {t.label}
            <span style={{ fontSize: 9, fontFamily: "'JetBrains Mono',monospace", fontWeight: 700, background: t.color + "20", borderRadius: 3, padding: "1px 5px" }}>
              {t.id === "all" ? accounts.filter(a => Object.values(a.threats).some(v => v > 0.4)).length : accounts.filter(a => a.threats[t.id as keyof Threats] > 0.4).length}
            </span>
          </button>
        ))}
      </div>
      <div style={{ flex: 1, overflow: "auto" }}>
        <table style={{ width: "100%", borderCollapse: "collapse" }}>
          <thead><tr>{["Login","Name","Score","Ring","Latency","Bonus","Bot","Route","IB","EA"].map(h => <th key={h} style={thStyle}>{h}</th>)}</tr></thead>
          <tbody>
            {filtered.map(acc => (
              <tr key={acc.login} onClick={() => onSelect(acc)} style={{ cursor: "pointer" }}
                onMouseEnter={e => { e.currentTarget.style.background = "rgba(255,255,255,0.03)"; }}
                onMouseLeave={e => { e.currentTarget.style.background = "transparent"; }}>
                <td style={{ padding: "7px 10px", fontSize: 12, fontFamily: "'JetBrains Mono',monospace", color: C.t1, borderBottom: `1px solid ${C.border}` }}>{acc.login}</td>
                <td style={{ padding: "7px 10px", fontSize: 12, color: C.t2, borderBottom: `1px solid ${C.border}` }}>{acc.name}</td>
                <td style={{ padding: "7px 10px", borderBottom: `1px solid ${C.border}` }}><ScoreBar score={acc.score} width={50} /></td>
                {(["ring","latency","bonus","bot"] as const).map(key => {
                  const v = acc.threats[key];
                  const tc = key === "ring" ? C.red : key === "latency" ? C.coral : key === "bonus" ? C.amber : C.purple;
                  return (
                    <td key={key} style={{ padding: "7px 10px", borderBottom: `1px solid ${C.border}` }}>
                      <div style={{ display: "flex", alignItems: "center", gap: 6 }}>
                        <div style={{ width: 30, height: 4, borderRadius: 2, background: "rgba(255,255,255,0.06)" }}>
                          <div style={{ width: `${v * 100}%`, height: "100%", borderRadius: 2, background: v > 0.5 ? tc : "rgba(255,255,255,0.15)" }} />
                        </div>
                        <span style={{ fontSize: 10, fontFamily: "'JetBrains Mono',monospace", color: v > 0.5 ? tc : C.t3 }}>{(v * 100).toFixed(0)}</span>
                      </div>
                    </td>
                  );
                })}
                <td style={{ padding: "7px 10px", borderBottom: `1px solid ${C.border}` }}><RoutingBadge routing={acc.routing} /></td>
                <td style={{ padding: "7px 10px", fontSize: 11, fontFamily: "'JetBrains Mono',monospace", color: C.t3, borderBottom: `1px solid ${C.border}` }}>{acc.ib}</td>
                <td style={{ padding: "7px 10px", fontSize: 11, fontFamily: "'JetBrains Mono',monospace", color: C.t3, borderBottom: `1px solid ${C.border}` }}>{acc.primaryEA || "\u2014"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

export default ThreatView;
