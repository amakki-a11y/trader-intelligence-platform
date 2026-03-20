import { useState, useEffect } from "react";
import type { CSSProperties, ReactNode } from "react";
import { LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer } from "recharts";
import C from "../styles/colors";
import { apiFetch } from "../services/api";

interface AIProfile {
  login: number; name: string; group: string;
  style: string; styleConfidence: number; styleSignals: string[];
  bookRecommendation: string; bookConfidence: number;
  bookReasoning: string; bookSummary: string;
  score: number; riskLevel: string;
  avgHoldSeconds: number; winRate: number; timingEntropyCV: number;
  expertTradeRatio: number; tradesPerHour: number;
  isRingMember: boolean; correlatedTradeCount: number;
}

interface TimelinePoint { timeMsc: number; cumulativeBrokerPnL: number; cumulativeClientPnL: number; tradeIndex: number }
interface SimMode { routingMode: string; brokerPnL: number; commissionRevenue: number; spreadCapture: number; clientPnL: number; tradeCount: number; timeline: TimelinePoint[] }
interface SimComparison {
  aBook: SimMode; bBook: SimMode; hybrid: SimMode; recommendation: string;
}

function AIRoutingPanel({ login }: { login: number }) {
  const [profile, setProfile] = useState<AIProfile | null>(null);
  const [sim, setSim] = useState<SimComparison | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setLoading(true);
    setError(null);
    const controller = new AbortController();
    Promise.all([
      apiFetch(`/api/intelligence/profiles/${login}`, { signal: controller.signal }).then(r => r.ok ? r.json() : null),
      apiFetch(`/api/intelligence/profiles/${login}/simulate`, { signal: controller.signal }).then(r => r.ok ? r.json() : null),
    ]).then(([p, s]) => {
      setProfile(p as AIProfile | null);
      setSim(s as SimComparison | null);
    }).catch((err: unknown) => {
      if (err instanceof DOMException && err.name === "AbortError") return;
      console.error("[AIRoutingPanel] fetch failed:", err);
      setError("Failed to load AI analysis");
    }).finally(() => setLoading(false));
    return () => controller.abort();
  }, [login]);

  if (loading) return <div style={{ padding: 30, textAlign: "center", color: C.t3, fontSize: 12 }}>Loading AI analysis...</div>;
  if (error) return <div style={{ padding: 30, textAlign: "center", color: C.red, fontSize: 12 }}>{error}</div>;
  if (!profile) return <div style={{ padding: 30, textAlign: "center", color: C.t3, fontSize: 12 }}>No intelligence data available</div>;

  const bookColor = profile.bookRecommendation === "ABook" ? "#42a5f5" : profile.bookRecommendation === "BBook" ? C.green : C.amber;
  const styleColor = profile.style === "EA" || profile.style === "Scalper" ? C.red : profile.style === "Manual" ? C.green : C.amber;

  const chartData = sim?.aBook?.timeline?.map((_: TimelinePoint, i: number) => ({
    trade: i + 1,
    "A-Book": sim.aBook.timeline[i]?.cumulativeBrokerPnL ?? 0,
    "B-Book": sim.bBook.timeline[i]?.cumulativeBrokerPnL ?? 0,
    "Hybrid": sim.hybrid.timeline[i]?.cumulativeBrokerPnL ?? 0,
  })) ?? [];

  const cardStyle: CSSProperties = { background: C.card, border: `1px solid ${C.border}`, borderRadius: 8, padding: 14, marginBottom: 12 };
  const labelStyle: CSSProperties = { fontSize: 10, color: C.t3, textTransform: "uppercase", letterSpacing: 1, marginBottom: 4 };
  const badgeStyle = (color: string): CSSProperties => ({
    display: "inline-block", padding: "3px 10px", borderRadius: 4, fontSize: 11, fontWeight: 700,
    background: color + "20", color: color, border: `1px solid ${color}40`,
  });
  const confBar = (conf: number, color: string): ReactNode => (
    <div style={{ display: "flex", alignItems: "center", gap: 8, marginTop: 4 }}>
      <div style={{ width: 80, height: 5, borderRadius: 3, background: "rgba(255,255,255,0.06)" }}>
        <div style={{ width: `${Math.round(conf * 100)}%`, height: "100%", borderRadius: 3, background: color }} />
      </div>
      <span style={{ fontSize: 10, color: C.t3 }}>{(conf * 100).toFixed(0)}%</span>
    </div>
  );

  return (
    <div style={{ overflow: "auto", maxHeight: 500 }}>
      <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12, marginBottom: 12 }}>
        <div style={cardStyle}>
          <div style={labelStyle}>Trading Style</div>
          <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 8 }}>
            <span style={badgeStyle(styleColor)}>{profile.style}</span>
            {confBar(profile.styleConfidence, styleColor)}
          </div>
          <div style={{ fontSize: 10, color: C.t3, lineHeight: 1.6 }}>
            {profile.styleSignals.map((s, i) => <div key={i}>{"\u2022"} {s}</div>)}
          </div>
        </div>
        <div style={cardStyle}>
          <div style={labelStyle}>Book Recommendation</div>
          <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 8 }}>
            <span style={badgeStyle(bookColor)}>{profile.bookRecommendation.replace("Book", "-Book")}</span>
            {confBar(profile.bookConfidence, bookColor)}
          </div>
          <div style={{ fontSize: 11, color: C.t2, marginBottom: 6 }}>{profile.bookSummary}</div>
          {profile.bookReasoning && (
            <div style={{ fontSize: 10, color: C.t3, lineHeight: 1.6 }}>
              {profile.bookReasoning.split("; ").map((f, i) => (
                <div key={i} style={{ color: f.includes("CRITICAL") || f.includes("Ring") ? C.red : C.t3 }}>{"\u26A0"} {f}</div>
              ))}
            </div>
          )}
        </div>
      </div>

      {sim && chartData.length > 0 && (
        <div style={cardStyle}>
          <div style={labelStyle}>Routing Simulation — Cumulative Broker P&L</div>
          <div style={{ width: "100%", height: 220 }}>
            <ResponsiveContainer>
              <LineChart data={chartData} margin={{ top: 5, right: 20, bottom: 5, left: 10 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="rgba(255,255,255,0.06)" />
                <XAxis dataKey="trade" stroke={C.t3} fontSize={10} label={{ value: "Trade #", position: "insideBottom", offset: -2, fill: C.t3, fontSize: 10 }} />
                <YAxis stroke={C.t3} fontSize={10} tickFormatter={(v: number) => `$${v.toFixed(0)}`} />
                <Tooltip contentStyle={{ background: C.card, border: `1px solid ${C.border}`, borderRadius: 6, fontSize: 11 }}
                  formatter={(v: unknown) => [`$${Number(v).toFixed(2)}`, ""]} labelFormatter={(l: unknown) => `Trade #${l}`} />
                <Legend wrapperStyle={{ fontSize: 10 }} />
                <Line type="monotone" dataKey="A-Book" stroke="#42a5f5" strokeWidth={2} dot={false} />
                <Line type="monotone" dataKey="B-Book" stroke={C.green} strokeWidth={2} dot={false} />
                <Line type="monotone" dataKey="Hybrid" stroke={C.amber} strokeWidth={2} dot={false} />
              </LineChart>
            </ResponsiveContainer>
          </div>
          <div style={{ fontSize: 11, color: C.teal, marginTop: 8 }}>{sim.recommendation}</div>
        </div>
      )}

      {sim && (
        <div style={cardStyle}>
          <div style={labelStyle}>Simulation Comparison</div>
          <table style={{ width: "100%", borderCollapse: "collapse" }}>
            <thead>
              <tr>
                {["Metric", "A-Book", "B-Book", "Hybrid"].map(h => (
                  <th key={h} style={{ padding: "6px 10px", textAlign: h === "Metric" ? "left" : "right", fontSize: 10, color: C.t3, borderBottom: `1px solid ${C.border}` }}>{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {[
                { label: "Broker P&L", a: sim.aBook.brokerPnL, b: sim.bBook.brokerPnL, h: sim.hybrid.brokerPnL },
                { label: "Commission", a: sim.aBook.commissionRevenue, b: sim.bBook.commissionRevenue, h: sim.hybrid.commissionRevenue },
                { label: "Spread Capture", a: sim.aBook.spreadCapture, b: sim.bBook.spreadCapture, h: sim.hybrid.spreadCapture },
                { label: "Client P&L", a: sim.aBook.clientPnL, b: sim.bBook.clientPnL, h: sim.hybrid.clientPnL },
                { label: "Trades", a: sim.aBook.tradeCount, b: sim.bBook.tradeCount, h: sim.hybrid.tradeCount },
              ].map(row => (
                <tr key={row.label}>
                  <td style={{ padding: "5px 10px", fontSize: 11, color: C.t2 }}>{row.label}</td>
                  {[row.a, row.b, row.h].map((v, i) => (
                    <td key={i} style={{ padding: "5px 10px", textAlign: "right", fontSize: 11, fontFamily: "'JetBrains Mono', monospace",
                      color: row.label === "Trades" ? C.t2 : v >= 0 ? C.green : C.red }}>
                      {row.label === "Trades" ? v : `$${v.toFixed(2)}`}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

export default AIRoutingPanel;
