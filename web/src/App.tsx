import { Fragment, useState, useEffect, useMemo, useRef, useCallback, type CSSProperties, type ReactNode } from "react";
import { LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer } from "recharts";

// ─── Types ─────────────────────────────────────────────────────
interface Threats { ring: number; latency: number; bonus: number; bot: number }
interface Account {
  login: number; name: string; group: string; score: number; sev: string;
  deposits: number; totalDeposited: number; bonuses: number; volume: number; commissions: number;
  pnl: number; tradeCount: number; expertRatio: number; ib: string; primaryEA: number;
  avgHoldSec: number; winRate: number; tradesPerHour: number; timingCV: number;
  isRingMember: boolean; ringPartners: number[]; bonusToDepRatio: number; lastActivity: string;
  threats: Threats; routing: string;
}
interface Deal {
  ticket: number; time: string; login: number; symbol: string; action: string;
  volume: number; price: number; profit: number; commission: number; swap: number;
  reason: string; expertId: number; holdSec: number;
}
interface OpenTrade {
  ticket: number; time: string; symbol: string; action: string; volume: number;
  openPrice: number; currentPrice: number; profit: number; swap: number; sl: number; tp: number;
}
interface MoneyOp { id: number; time: string; type: string; amount: number; method: string; status: string }
interface MarketDataPoint { symbol: string; bid: number; ask: number; spread: number; change24h: number; digits: number }
interface LiveEvent {
  id: number; time: string; login: number; name: string; symbol: string; action: string;
  volume: number; score: number; scoreChange: number; isCorrelated: boolean;
  correlated: number | null; severity: string;
}
interface VolumeData { buy: number; sell: number; net: number; topBuyer: { login: number; volume: number }; topSeller: { login: number; volume: number } }

// ─── Dummy Data ────────────────────────────────────────────────
const SYMBOLS = ["XAUUSD","EURUSD","GBPUSD","USDJPY","BTCUSD","AUDUSD","USDCHF","NZDUSD","EURJPY","GBPJPY"];
const DEFAULT_WATCHLIST = ["US30-","XAUUSD-","EURUSD-","GBPUSD-","USDJPY-","NZDUSD-","AUDUSD-","USDCAD-","GBPJPY-","EURJPY-","EURGBP-","BTCUSD-"];
const EAS = [0, 77201, 77201, 88302, 99100, 0, 77201, 0, 88302, 0, 55010, 77201, 0, 88302, 0, 77201, 0, 99100];

const randBetween = (a: number, b: number) => Math.floor(Math.random() * (b - a + 1)) + a;
const randFloat = (a: number, b: number, d = 2) => +(Math.random() * (b - a) + a).toFixed(d);
const pick = <T,>(arr: T[]): T => arr[Math.floor(Math.random() * arr.length)]!;
const sevColor = (s: number) => s >= 70 ? "#FF5252" : s >= 50 ? "#FF7B6B" : s >= 30 ? "#FFBA42" : "#3DD9A0";


// @ts-ignore — kept for v1 fallback mode
function generateDeals(login: number, count = 50): Deal[] {
  const deals: Deal[] = [];
  let t = Date.now() - 86400000 * 30;
  for (let i = 0; i < count; i++) {
    t += randBetween(60000, 3600000);
    const sym = pick(SYMBOLS);
    const action = Math.random() > 0.5 ? "BUY" : "SELL";
    const vol = pick([0.01, 0.05, 0.1, 0.5, 1.0, 2.0]);
    const price = sym === "XAUUSD" ? randFloat(2300, 2400, 2) : (sym === "BTCUSD" ? randFloat(60000, 70000, 0) : randFloat(0.9, 1.8, 5));
    const profit = randFloat(-200, 300, 2);
    const commission = +(vol * randFloat(3, 7, 2)).toFixed(2);
    deals.push({
      ticket: 1000000 + i + login * 100, time: new Date(t).toISOString(),
      login, symbol: sym, action, volume: vol, price,
      profit: +profit, commission, swap: randFloat(-5, 5, 2),
      reason: Math.random() > 0.3 ? "EXPERT" : "CLIENT",
      expertId: pick(EAS), holdSec: randBetween(2, 7200),
    });
  }
  return deals;
}

// ─── Styles ────────────────────────────────────────────────────
const C = {
  bg: "#0B0E13", bg2: "#111620", bg3: "#171D28", card: "#1C2333",
  border: "rgba(255,255,255,0.07)", borderHi: "rgba(255,255,255,0.14)",
  t1: "#EBE8E0", t2: "#8F99A8", t3: "#555F70",
  teal: "#3DD9A0", tealBg: "rgba(61,217,160,0.07)",
  purple: "#9B8AFF", purpleBg: "rgba(155,138,255,0.07)",
  coral: "#FF7B6B", coralBg: "rgba(255,123,107,0.07)",
  amber: "#FFBA42", amberBg: "rgba(255,186,66,0.07)",
  red: "#FF5252", redBg: "rgba(255,82,82,0.07)",
  green: "#66BB6A", blue: "#5B9EFF",
};

// ─── Components ────────────────────────────────────────────────
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

// ─── Sidebar ───────────────────────────────────────────────────
function Sidebar({ view, setView, version, setVersion, connected }: {
  view: string; setView: (v: string) => void; version: string;
  setVersion: (fn: (v: string) => string) => void; connected: boolean;
}) {
  const navItems = [
    { id: "market", icon: "📊", label: "Market Watch" },
    { id: "grid", icon: "▦", label: "Accounts" },
    { id: "live", icon: "◉", label: "Live Monitor" },
    { id: "threats", icon: "◆", label: "Threat View" },
  ];
  const btnS = (active: boolean): CSSProperties => ({
    width: 40, height: 40, borderRadius: 8, border: "none", cursor: "pointer",
    background: active ? "rgba(61,217,160,0.12)" : "transparent",
    color: active ? C.teal : C.t3, fontSize: 18,
    display: "flex", alignItems: "center", justifyContent: "center", transition: "all 0.15s",
  });
  return (
    <div style={{
      width: 56, background: C.bg, borderRight: `1px solid ${C.border}`,
      display: "flex", flexDirection: "column", alignItems: "center",
      paddingTop: 12, gap: 4, flexShrink: 0,
    }}>
      <div style={{
        width: 34, height: 34, borderRadius: 8, background: "linear-gradient(135deg, #3DD9A020, #9B8AFF20)",
        border: `1px solid ${C.border}`, display: "flex", alignItems: "center", justifyContent: "center",
        marginBottom: 16, fontSize: 16, fontWeight: 700, color: C.teal,
      }}>R</div>
      {navItems.map(item => (
        <button key={item.id} onClick={() => setView(item.id)} style={btnS(view === item.id)} title={item.label}>{item.icon}</button>
      ))}
      <div style={{ flex: 1 }} />
      <button onClick={() => setView("settings")} style={btnS(view === "settings")} title="Settings">⚙</button>
      <button onClick={() => setVersion(v => v === "v1" ? "v2" : "v1")} style={{
        width: 40, height: 22, borderRadius: 4, border: `1px solid ${C.border}`,
        background: version === "v2" ? C.purpleBg : C.bg3, cursor: "pointer",
        color: version === "v2" ? C.purple : C.t3,
        fontSize: 9, fontFamily: "'JetBrains Mono',monospace", fontWeight: 600, marginBottom: 8,
      }}>{version}</button>
      <div style={{
        width: 8, height: 8, borderRadius: "50%", marginBottom: 16,
        background: connected ? C.teal : C.red,
        boxShadow: connected ? `0 0 8px ${C.teal}60` : `0 0 8px ${C.red}60`,
      }} />
    </div>
  );
}

// ─── Top Bar ───────────────────────────────────────────────────
function TopBar({ view, accounts, version, isLive, onToggleLive }: {
  view: string; accounts: Account[]; version: string; isLive: boolean; onToggleLive: () => void;
}) {
  const critCount = accounts.filter(a => a.sev === "CRITICAL").length;
  const highCount = accounts.filter(a => a.sev === "HIGH").length;
  const titles: Record<string, string> = { grid: "Account Scanner", live: "Live Monitor", market: "Market Watch", threats: "Threat Intelligence", settings: "Settings" };
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
        <Badge color={C.red}>{critCount} CRIT</Badge>
        <Badge color={C.coral}>{highCount} HIGH</Badge>
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
        <button style={{
          padding: "6px 14px", borderRadius: 6, border: `1px solid ${C.teal}40`,
          background: C.tealBg, color: C.teal, fontSize: 11, fontWeight: 600,
          fontFamily: "'JetBrains Mono',monospace", cursor: "pointer",
        }}>SCAN</button>
      )}
    </div>
  );
}

// ─── Main Grid ─────────────────────────────────────────────────
function AccountGrid({ accounts, version, onSelect, flashRows }: {
  accounts: Account[]; version: string; onSelect: (a: Account) => void; flashRows: Set<number>;
}) {
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
                {col.label} {sortCol === col.key ? (sortDir === 1 ? "↑" : "↓") : ""}
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

// ─── Account Detail ────────────────────────────────────────────
function AccountDetail({ account, version, onBack }: { account: Account; version: string; onBack: () => void }) {
  const [deals, setDeals] = useState<Deal[]>([]);
  const [tab, setTab] = useState("history");
  const [acctInfoExpanded, setAcctInfoExpanded] = useState(false);
  const [dateFrom, setDateFrom] = useState(() => { const d = new Date(); d.setDate(d.getDate() - 90); return d.toISOString().slice(0, 10); });
  const [dateTo, setDateTo] = useState(() => new Date().toISOString().slice(0, 10));

  const [acctInfo, setAcctInfo] = useState({
    balance: 0, equity: 0, margin: 0, freeMargin: 0,
    marginLevel: "0%", leverage: "1:100",
    credit: 0, registration: "", lastLogin: "", server: "", currency: "USD",
  });

  // Fetch live account info from MT5
  useEffect(() => {
    fetch(`/api/accounts/${account.login}/info`)
      .then(r => r.json())
      .then(d => {
        if (d.error) return;
        setAcctInfo(prev => ({
          ...prev,
          balance: d.balance ?? 0,
          equity: d.equity ?? 0,
          leverage: d.leverage ? `1:${d.leverage}` : prev.leverage,
          freeMargin: (d.equity ?? 0) - prev.margin,
        }));
      })
      .catch(() => {});
  }, [account.login]);

  // Fetch real deal history from MT5
  useEffect(() => {
    fetch(`/api/accounts/${account.login}/deals?from=${dateFrom}&to=${dateTo}`)
      .then(r => r.json())
      .then((data: any[]) => {
        if (!Array.isArray(data)) return;
        const actionMap: Record<number, string> = { 0: "BUY", 1: "SELL", 2: "BALANCE", 3: "CREDIT", 4: "CHARGE", 5: "CORRECTION", 6: "BONUS" };
        setDeals(data.map(d => ({
          ticket: d.dealId,
          time: d.time,
          login: d.login,
          symbol: d.symbol ?? "",
          action: actionMap[d.action] ?? `ACTION_${d.action}`,
          volume: d.volume ?? 0,
          price: d.price ?? 0,
          profit: d.profit ?? 0,
          commission: d.commission ?? 0,
          swap: d.swap ?? 0,
          reason: d.reason?.toString() ?? "",
          expertId: d.expertId ?? 0,
          holdSec: 0,
        })));
      })
      .catch(() => {});
  }, [account.login, dateFrom, dateTo]);

  const [openTrades, setOpenTrades] = useState<OpenTrade[]>([]);

  // Fetch open positions from MT5
  useEffect(() => {
    const fetchPositions = async () => {
      try {
        const res = await fetch(`/api/accounts/${account.login}/positions`);
        if (!res.ok) return;
        const data = await res.json();
        if (!Array.isArray(data)) return;
        setOpenTrades(data.map((p: any) => ({
          ticket: p.positionId,
          time: p.time,
          symbol: p.symbol ?? "",
          action: p.action ?? "BUY",
          volume: p.volume ?? 0,
          openPrice: p.priceOpen ?? 0,
          currentPrice: p.priceCurrent ?? 0,
          profit: p.profit ?? 0,
          swap: p.swap ?? 0,
          sl: p.sl ?? 0,
          tp: p.tp ?? 0,
        })));
      } catch { /* ignore */ }
    };
    fetchPositions();
    const interval = setInterval(fetchPositions, 5000);
    return () => clearInterval(interval);
  }, [account.login]);

  const moneyOps = useMemo((): MoneyOp[] => {
    return deals
      .filter(d => ["BALANCE", "CREDIT", "BONUS"].includes(d.action))
      .map((d) => ({
        id: d.ticket,
        time: d.time,
        type: d.action === "BALANCE" ? (d.profit >= 0 ? "Deposit" : "Withdrawal") : d.action === "CREDIT" ? "Credit" : "Bonus",
        amount: Math.abs(d.profit),
        method: "",
        status: "Completed",
      }));
  }, [deals]);

  const tabStyle = (id: string): CSSProperties => ({
    padding: "7px 16px", borderRadius: 6, border: `1px solid ${tab === id ? C.teal + "40" : C.border}`,
    background: tab === id ? C.tealBg : "transparent", color: tab === id ? C.teal : C.t3,
    fontSize: 11, fontWeight: 500, cursor: "pointer", fontFamily: "'JetBrains Mono',monospace",
  });
  const thStyle: CSSProperties = { padding: "6px 6px", fontSize: 9, fontFamily: "'JetBrains Mono',monospace", fontWeight: 600, color: C.t3, textAlign: "left", borderBottom: `1px solid ${C.border}`, position: "sticky", top: 0, background: C.bg2, textTransform: "uppercase", letterSpacing: "0.5px" };
  const tdStyle: CSSProperties = { padding: "5px 6px", fontSize: 11, color: C.t2, fontFamily: "'JetBrains Mono',monospace", borderBottom: `1px solid ${C.border}` };
  const dateInputStyle: CSSProperties = { background: C.bg3, border: `1px solid ${C.border}`, borderRadius: 5, padding: "4px 8px", color: C.t1, fontSize: 11, fontFamily: "'JetBrains Mono',monospace", outline: "none", colorScheme: "dark" };
  const infoItems = [
    { label: "Balance", val: "$" + acctInfo.balance.toLocaleString(), color: C.t1 },
    { label: "Equity", val: "$" + acctInfo.equity.toLocaleString(), color: acctInfo.equity >= acctInfo.balance ? C.green : C.red },
    { label: "Margin", val: "$" + acctInfo.margin.toLocaleString(), color: C.amber },
    { label: "Free Margin", val: "$" + acctInfo.freeMargin.toLocaleString(), color: C.t1 },
    { label: "Margin Level", val: acctInfo.marginLevel, color: C.teal },
    { label: "Leverage", val: acctInfo.leverage, color: C.purple },
    { label: "Credit", val: "$" + acctInfo.credit.toLocaleString(), color: C.amber },
    { label: "Currency", val: acctInfo.currency, color: C.t1 },
    { label: "Registered", val: acctInfo.registration, color: C.t3 },
    { label: "Last Login", val: acctInfo.lastLogin, color: C.t3 },
  ];
  const totalOpenPnl = openTrades.reduce((s, t) => s + t.profit, 0);

  return (
    <div style={{ flex: 1, overflow: "auto", padding: 20 }}>
      <button onClick={onBack} style={{ background: "none", border: `1px solid ${C.border}`, borderRadius: 6, color: C.t2, fontSize: 12, padding: "5px 12px", cursor: "pointer", marginBottom: 16 }}>← Back to grid</button>
      {/* Header */}
      <div style={{ display: "flex", gap: 20, marginBottom: 20, alignItems: "flex-start" }}>
        <div style={{ flex: 1 }}>
          <div style={{ display: "flex", alignItems: "center", gap: 12, marginBottom: 6 }}>
            <span style={{ fontSize: 20, fontWeight: 700, color: C.t1, fontFamily: "'JetBrains Mono',monospace" }}>{account.login}</span>
            <span style={{ fontSize: 15, color: C.t2 }}>{account.name}</span>
            <Badge color={sevColor(account.score)}>{account.sev}</Badge>
            {account.isRingMember && <Badge color={C.red}>RING MEMBER</Badge>}
            {version === "v2" && <RoutingBadge routing={account.routing} />}
          </div>
          <div style={{ fontSize: 12, color: C.t3, fontFamily: "'JetBrains Mono',monospace" }}>
            {account.group} &nbsp;·&nbsp; IB: {account.ib} &nbsp;·&nbsp; EA: {account.primaryEA || "None"}
          </div>
        </div>
        <div style={{ textAlign: "right" }}>
          <div style={{ fontSize: 36, fontWeight: 700, color: sevColor(account.score), fontFamily: "'JetBrains Mono',monospace", lineHeight: 1 }}>{account.score}</div>
          <div style={{ fontSize: 10, color: C.t3, marginTop: 2 }}>ABUSE SCORE</div>
        </div>
      </div>
      {/* Account Info Panel */}
      <div style={{ background: C.bg3, borderRadius: 10, border: `1px solid ${C.border}`, marginBottom: 20, overflow: "hidden" }}>
        <div onClick={() => setAcctInfoExpanded(e => !e)} style={{ padding: "12px 18px", display: "flex", alignItems: "center", cursor: "pointer", userSelect: "none" }}>
          <span style={{ fontSize: 10, color: C.t3, textTransform: "uppercase", letterSpacing: "0.5px", fontWeight: 600, marginRight: 8 }}>Account Information</span>
          <span style={{ fontSize: 10, color: C.t3, transition: "transform 0.2s", display: "inline-block", transform: acctInfoExpanded ? "rotate(180deg)" : "rotate(0deg)" }}>▼</span>
          <div style={{ flex: 1 }} />
          {!acctInfoExpanded && (
            <div style={{ display: "flex", gap: 20 }}>
              {infoItems.slice(0, 6).map(({ label, val, color }) => (
                <div key={label} style={{ display: "flex", alignItems: "center", gap: 5 }}>
                  <span style={{ fontSize: 8, color: C.t3, textTransform: "uppercase" }}>{label}</span>
                  <span style={{ fontSize: 11, fontWeight: 600, color, fontFamily: "'JetBrains Mono',monospace" }}>{val}</span>
                </div>
              ))}
            </div>
          )}
        </div>
        {acctInfoExpanded && (
          <div style={{ padding: "0 18px 14px", display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(130px, 1fr))", gap: 8 }}>
            {infoItems.map(({ label, val, color }) => (
              <div key={label}>
                <div style={{ fontSize: 9, color: C.t3, marginBottom: 2, textTransform: "uppercase" }}>{label}</div>
                <div style={{ fontSize: 13, fontWeight: 600, color, fontFamily: "'JetBrains Mono',monospace" }}>{val}</div>
              </div>
            ))}
          </div>
        )}
      </div>
      {/* Date range */}
      <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 14 }}>
        <span style={{ fontSize: 10, color: C.t3, textTransform: "uppercase", fontWeight: 600, letterSpacing: "0.5px" }}>Request Data</span>
        <span style={{ fontSize: 10, color: C.t3 }}>From</span>
        <input type="date" value={dateFrom} onChange={e => setDateFrom(e.target.value)} style={dateInputStyle} />
        <span style={{ fontSize: 10, color: C.t3 }}>Till</span>
        <input type="date" value={dateTo} onChange={e => setDateTo(e.target.value)} style={dateInputStyle} />
        <button style={{ padding: "4px 12px", borderRadius: 5, border: `1px solid ${C.teal}40`, background: C.tealBg, color: C.teal, fontSize: 10, fontWeight: 600, fontFamily: "'JetBrains Mono',monospace", cursor: "pointer" }}>LOAD</button>
      </div>
      {/* Stats cards */}
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(150px, 1fr))", gap: 10, marginBottom: 20 }}>
        {[
          { label: "Trades", val: String(account.tradeCount), color: C.t1 },
          { label: "Volume", val: account.volume.toLocaleString() + " lots", color: C.t1 },
          { label: "Commissions", val: "$" + account.commissions.toLocaleString(), color: C.amber },
          { label: "Net P&L", val: (account.pnl >= 0 ? "+" : "") + "$" + account.pnl.toLocaleString(), color: account.pnl >= 0 ? C.green : C.red },
          { label: "Deposits", val: account.deposits + "× ($" + account.totalDeposited.toLocaleString() + ")", color: C.t1 },
          { label: "Bonuses", val: "$" + account.bonuses.toLocaleString(), color: C.amber },
          { label: "EA Trade %", val: (account.expertRatio * 100).toFixed(0) + "%", color: account.expertRatio > 0.8 ? C.coral : C.t1 },
          { label: "Avg Hold", val: account.avgHoldSec < 60 ? account.avgHoldSec + "s" : Math.round(account.avgHoldSec / 60) + "m", color: account.avgHoldSec < 30 ? C.red : C.t1 },
          { label: "Win Rate", val: (account.winRate * 100).toFixed(0) + "%", color: C.t1 },
          { label: "Timing CV", val: account.timingCV.toFixed(2), color: account.timingCV < 0.15 ? C.red : C.t1 },
        ].map(({ label, val, color }) => (
          <div key={label} style={{ background: C.bg3, borderRadius: 8, padding: "10px 14px", border: `1px solid ${C.border}` }}>
            <div style={{ fontSize: 10, color: C.t3, marginBottom: 4, textTransform: "uppercase", letterSpacing: "0.5px" }}>{label}</div>
            <div style={{ fontSize: 15, fontWeight: 600, color, fontFamily: "'JetBrains Mono',monospace" }}>{val}</div>
          </div>
        ))}
      </div>
      {/* v2 Threat breakdown */}
      {version === "v2" && (
        <div style={{ marginBottom: 20 }}>
          <div style={{ fontSize: 10, color: C.t3, marginBottom: 10, textTransform: "uppercase", letterSpacing: "0.5px", fontWeight: 600 }}>Threat Breakdown</div>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(4, 1fr)", gap: 10 }}>
            {[
              { label: "Ring Trading", val: account.threats.ring, color: C.red },
              { label: "Latency Arb", val: account.threats.latency, color: C.coral },
              { label: "Bonus Abuse", val: account.threats.bonus, color: C.amber },
              { label: "Bot Farming", val: account.threats.bot, color: C.purple },
            ].map(({ label, val, color }) => (
              <div key={label} style={{ background: val > 0.5 ? color + "12" : C.bg3, borderRadius: 8, padding: "12px 14px", border: `1px solid ${val > 0.5 ? color + "40" : C.border}` }}>
                <div style={{ fontSize: 10, color: C.t3, marginBottom: 6 }}>{label}</div>
                <div style={{ fontSize: 22, fontWeight: 700, color: val > 0.5 ? color : C.t3, fontFamily: "'JetBrains Mono',monospace" }}>{(val * 100).toFixed(0)}%</div>
                <div style={{ height: 3, borderRadius: 2, background: "rgba(255,255,255,0.06)", marginTop: 6 }}>
                  <div style={{ width: `${val * 100}%`, height: "100%", borderRadius: 2, background: color }} />
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
      {/* Ring info */}
      {account.isRingMember && (
        <div style={{ background: C.redBg, border: "1px solid rgba(255,82,82,0.25)", borderRadius: 8, padding: 16, marginBottom: 20 }}>
          <div style={{ fontSize: 12, fontWeight: 600, color: C.red, marginBottom: 6 }}>Ring Detected</div>
          <div style={{ fontSize: 12, color: C.t2 }}>
            Linked accounts: {account.ringPartners.map(p => <span key={p} style={{ fontFamily: "'JetBrains Mono',monospace", color: C.t1, marginRight: 8 }}>{p}</span>)}
            <br />Shared EA (magic: {account.primaryEA}), same IB ({account.ib}), correlated trades detected.
          </div>
        </div>
      )}
      {/* Tabs */}
      <div style={{ display: "flex", gap: 6, marginBottom: 14 }}>
        <button onClick={() => setTab("open")} style={tabStyle("open")}>Open Trades ({openTrades.length})</button>
        <button onClick={() => setTab("history")} style={tabStyle("history")}>History ({deals.length})</button>
        <button onClick={() => setTab("deposits")} style={tabStyle("deposits")}>Deposits & Withdrawals ({moneyOps.length})</button>
        <button onClick={() => setTab("ai")} style={tabStyle("ai")}>AI Routing</button>
      </div>
      {/* Open Trades */}
      {tab === "open" && (
        <div style={{ overflow: "auto", maxHeight: 340 }}>
          {openTrades.length === 0 ? (
            <div style={{ padding: 30, textAlign: "center", color: C.t3, fontSize: 12 }}>No open positions</div>
          ) : (
            <table style={{ width: "100%", borderCollapse: "collapse" }}>
              <thead><tr>{["Ticket","Time","Symbol","Action","Vol","Open Price","Current","Profit","Swap","S/L","T/P"].map(h => <th key={h} style={thStyle}>{h}</th>)}</tr></thead>
              <tbody>
                {openTrades.map(t => (
                  <tr key={t.ticket}>
                    <td style={tdStyle}>{t.ticket}</td>
                    <td style={tdStyle}>{new Date(t.time).toLocaleString("en-GB", { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit" })}</td>
                    <td style={tdStyle}>{t.symbol}</td>
                    <td style={{ ...tdStyle, color: t.action === "BUY" ? C.blue : C.red }}>{t.action}</td>
                    <td style={tdStyle}>{t.volume}</td><td style={tdStyle}>{t.openPrice}</td><td style={tdStyle}>{t.currentPrice}</td>
                    <td style={{ ...tdStyle, color: t.profit >= 0 ? C.green : C.red, fontWeight: 600 }}>{t.profit >= 0 ? "+" : ""}{t.profit}</td>
                    <td style={tdStyle}>{t.swap}</td>
                    <td style={{ ...tdStyle, color: C.red }}>{t.sl}</td>
                    <td style={{ ...tdStyle, color: C.green }}>{t.tp}</td>
                  </tr>
                ))}
                <tr>
                  <td colSpan={7} style={{ ...tdStyle, textAlign: "right", fontSize: 10, color: C.t3 }}>TOTAL P&L:</td>
                  <td style={{ ...tdStyle, fontWeight: 700, color: totalOpenPnl >= 0 ? C.green : C.red }}>{totalOpenPnl >= 0 ? "+" : ""}{totalOpenPnl.toFixed(2)}</td>
                  <td colSpan={3} style={tdStyle} />
                </tr>
              </tbody>
            </table>
          )}
        </div>
      )}
      {/* History */}
      {tab === "history" && (
        <div style={{ overflow: "auto", maxHeight: 340 }}>
          <table style={{ width: "100%", borderCollapse: "collapse" }}>
            <thead><tr>{["Ticket","Time","Symbol","Action","Vol","Price","Profit","Comm","Reason","EA","Hold"].map(h => <th key={h} style={thStyle}>{h}</th>)}</tr></thead>
            <tbody>
              {deals.slice(0, 40).map(d => (
                <tr key={d.ticket}>
                  <td style={tdStyle}>{d.ticket}</td>
                  <td style={tdStyle}>{new Date(d.time).toLocaleString("en-GB", { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit" })}</td>
                  <td style={tdStyle}>{d.symbol}</td>
                  <td style={{ ...tdStyle, color: d.action === "BUY" ? C.blue : C.red }}>{d.action}</td>
                  <td style={tdStyle}>{d.volume}</td><td style={tdStyle}>{d.price}</td>
                  <td style={{ ...tdStyle, color: d.profit >= 0 ? C.green : C.red }}>{d.profit >= 0 ? "+" : ""}{d.profit}</td>
                  <td style={tdStyle}>${d.commission}</td><td style={tdStyle}>{d.reason}</td>
                  <td style={tdStyle}>{d.expertId || "—"}</td>
                  <td style={tdStyle}>{d.holdSec < 60 ? d.holdSec + "s" : Math.round(d.holdSec / 60) + "m"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {/* Deposits */}
      {tab === "deposits" && (
        <div style={{ overflow: "auto", maxHeight: 340 }}>
          <table style={{ width: "100%", borderCollapse: "collapse" }}>
            <thead><tr>{["ID","Date","Type","Amount","Method","Status"].map(h => <th key={h} style={thStyle}>{h}</th>)}</tr></thead>
            <tbody>
              {moneyOps.map(op => {
                const typeColor = op.type === "Deposit" ? C.green : (op.type === "Withdrawal" ? C.red : C.amber);
                return (
                  <tr key={op.id}>
                    <td style={tdStyle}>{op.id}</td>
                    <td style={tdStyle}>{new Date(op.time).toLocaleDateString("en-GB", { day: "2-digit", month: "short", year: "numeric" })}</td>
                    <td style={{ ...tdStyle, color: typeColor, fontWeight: 600 }}>{op.type}</td>
                    <td style={{ ...tdStyle, color: typeColor }}>{op.type === "Withdrawal" ? "-" : "+"}${op.amount.toLocaleString()}</td>
                    <td style={tdStyle}>{op.method}</td>
                    <td style={tdStyle}><span style={{ color: C.green }}>✓</span> {op.status}</td>
                  </tr>
                );
              })}
              <tr>
                <td colSpan={3} style={{ ...tdStyle, textAlign: "right", fontSize: 10, color: C.t3 }}>NET DEPOSITS:</td>
                <td style={{ ...tdStyle, fontWeight: 700, color: C.teal }}>
                  ${moneyOps.reduce((s, o) => s + (o.type === "Withdrawal" ? -o.amount : o.amount), 0).toLocaleString()}
                </td>
                <td colSpan={2} style={tdStyle} />
              </tr>
            </tbody>
          </table>
        </div>
      )}
      {/* AI Routing Tab */}
      {tab === "ai" && (
        <AIRoutingPanel login={account.login} />
      )}
    </div>
  );
}

// ─── AI Routing Panel ──────────────────────────────────────────
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
interface SimComparison {
  aBook: { routingMode: string; brokerPnL: number; commissionRevenue: number; spreadCapture: number; clientPnL: number; tradeCount: number; timeline: { timeMsc: number; cumulativeBrokerPnL: number; cumulativeClientPnL: number; tradeIndex: number }[] };
  bBook: { routingMode: string; brokerPnL: number; commissionRevenue: number; spreadCapture: number; clientPnL: number; tradeCount: number; timeline: { timeMsc: number; cumulativeBrokerPnL: number; cumulativeClientPnL: number; tradeIndex: number }[] };
  hybrid: { routingMode: string; brokerPnL: number; commissionRevenue: number; spreadCapture: number; clientPnL: number; tradeCount: number; timeline: { timeMsc: number; cumulativeBrokerPnL: number; cumulativeClientPnL: number; tradeIndex: number }[] };
  recommendation: string;
}

function AIRoutingPanel({ login }: { login: number }) {
  const [profile, setProfile] = useState<AIProfile | null>(null);
  const [sim, setSim] = useState<SimComparison | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    setLoading(true);
    Promise.all([
      fetch(`/api/intelligence/profiles/${login}`).then(r => r.ok ? r.json() : null),
      fetch(`/api/intelligence/profiles/${login}/simulate`).then(r => r.ok ? r.json() : null),
    ]).then(([p, s]) => {
      setProfile(p);
      setSim(s);
    }).catch(() => {}).finally(() => setLoading(false));
  }, [login]);

  if (loading) return <div style={{ padding: 30, textAlign: "center", color: C.t3, fontSize: 12 }}>Loading AI analysis...</div>;
  if (!profile) return <div style={{ padding: 30, textAlign: "center", color: C.t3, fontSize: 12 }}>No intelligence data available</div>;

  const bookColor = profile.bookRecommendation === "ABook" ? "#42a5f5" : profile.bookRecommendation === "BBook" ? C.green : C.amber;
  const styleColor = profile.style === "EA" || profile.style === "Scalper" ? C.red : profile.style === "Manual" ? C.green : C.amber;

  // Merge simulation timelines for chart
  const chartData = sim?.aBook?.timeline?.map((_: { timeMsc: number; cumulativeBrokerPnL: number; cumulativeClientPnL: number; tradeIndex: number }, i: number) => ({
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
        {/* Style Card */}
        <div style={cardStyle}>
          <div style={labelStyle}>Trading Style</div>
          <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 8 }}>
            <span style={badgeStyle(styleColor)}>{profile.style}</span>
            {confBar(profile.styleConfidence, styleColor)}
          </div>
          <div style={{ fontSize: 10, color: C.t3, lineHeight: 1.6 }}>
            {profile.styleSignals.map((s, i) => <div key={i}>• {s}</div>)}
          </div>
        </div>
        {/* Book Recommendation Card */}
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
                <div key={i} style={{ color: f.includes("CRITICAL") || f.includes("Ring") ? C.red : C.t3 }}>⚠ {f}</div>
              ))}
            </div>
          )}
        </div>
      </div>

      {/* Simulation Chart */}
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

      {/* Simulation Summary Table */}
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

// ─── Live Monitor ──────────────────────────────────────────────
function LiveMonitor({ accounts, isLive, onSelect }: {
  accounts: Account[]; isLive: boolean; onSelect: (a: Account) => void;
}) {
  const [events, setEvents] = useState<LiveEvent[]>([]);
  const [wsStatus, setWsStatus] = useState<"connecting" | "connected" | "disconnected">("disconnected");
  const logRef = useRef<HTMLDivElement>(null);
  const wsRef = useRef<WebSocket | null>(null);

  // Load recent deals from all accounts on GO LIVE, then listen via WebSocket
  useEffect(() => {
    if (!isLive) {
      if (wsRef.current) { wsRef.current.close(); wsRef.current = null; }
      setWsStatus("disconnected");
      return;
    }

    let reconnectTimer: ReturnType<typeof setTimeout>;
    let ws: WebSocket;
    const seenIds = new Set<number>();

    // Load recent deal history for scored accounts
    const loadRecent = async () => {
      try {
        const actionMap: Record<number, string> = { 0: "BUY", 1: "SELL", 2: "BALANCE", 3: "CREDIT", 4: "CHARGE", 5: "CORRECTION", 6: "BONUS" };
        const from = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString();
        // Fetch real account list from API (not the prop which may still have dummy data)
        const acctRes = await fetch("/api/accounts");
        if (!acctRes.ok) return;
        const acctList = await acctRes.json();
        if (!Array.isArray(acctList)) return;
        const allDeals: LiveEvent[] = [];
        for (const acc of acctList) {
          try {
            const res = await fetch(`/api/accounts/${acc.login}/deals?from=${from}`);
            if (!res.ok) continue;
            const data = await res.json();
            if (!Array.isArray(data)) continue;
            for (const d of data) {
              if (seenIds.has(d.dealId)) continue;
              seenIds.add(d.dealId);
              allDeals.push({
                id: d.dealId,
                time: d.time ? new Date(d.time).toLocaleTimeString("en-GB") : "",
                login: d.login, name: acc.name ?? d.login?.toString() ?? "",
                symbol: d.symbol ?? "",
                action: actionMap[d.action] ?? `ACTION_${d.action}`,
                volume: d.volume ?? 0,
                score: acc.abuseScore ?? 0, scoreChange: 0,
                isCorrelated: false, correlated: null,
                severity: acc.riskLevel === "Critical" ? "CRITICAL" : acc.riskLevel === "High" ? "HIGH" : acc.riskLevel === "Medium" ? "MEDIUM" : "LOW",
              });
            }
          } catch { /* skip this account */ }
        }
        allDeals.sort((a, b) => (b.id as number) - (a.id as number));
        setEvents(allDeals.slice(0, 200));
      } catch { /* ignore */ }
    };
    loadRecent();

    // WebSocket for new deals in real-time
    const connect = () => {
      setWsStatus("connecting");
      const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
      ws = new WebSocket(`${proto}//${window.location.host}/ws`);
      wsRef.current = ws;

      ws.onopen = () => {
        setWsStatus("connected");
        ws.send(JSON.stringify({ subscribe: ["deals"] }));
      };

      ws.onmessage = (evt) => {
        try {
          const msg = JSON.parse(evt.data);
          if (msg.type !== "deals" || !msg.data) return;
          const d = msg.data;
          if (seenIds.has(d.dealId)) return;
          seenIds.add(d.dealId);
          const time = d.timeMsc ? new Date(d.timeMsc).toLocaleTimeString("en-GB") : new Date().toLocaleTimeString("en-GB");
          setEvents(prev => [{
            id: d.dealId ?? Date.now() + Math.random(),
            time,
            login: d.login,
            name: d.login?.toString() ?? "",
            symbol: d.symbol ?? "",
            action: d.action ?? "",
            volume: d.volume ?? 0,
            score: d.score ?? 0,
            scoreChange: d.scoreChange ?? 0,
            isCorrelated: d.isCorrelated ?? false,
            correlated: null,
            severity: d.severity ?? "Low",
          }, ...prev].slice(0, 200));
        } catch { /* ignore parse errors */ }
      };

      ws.onclose = () => {
        setWsStatus("disconnected");
        wsRef.current = null;
        reconnectTimer = setTimeout(connect, 3000);
      };

      ws.onerror = () => { ws.close(); };
    };

    connect();
    return () => {
      clearTimeout(reconnectTimer);
      if (wsRef.current) { wsRef.current.close(); wsRef.current = null; }
    };
  }, [isLive]);
  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", overflow: "hidden" }}>
      <div style={{ padding: "10px 16px", borderBottom: `1px solid ${C.border}`, display: "flex", alignItems: "center", gap: 12 }}>
        <span style={{ width: 8, height: 8, borderRadius: "50%", background: wsStatus === "connected" ? C.teal : wsStatus === "connecting" ? C.amber : C.t3, animation: wsStatus === "connected" ? "pulse 1.5s infinite" : "none" }} />
        <span style={{ fontSize: 12, color: wsStatus === "connected" ? C.teal : wsStatus === "connecting" ? C.amber : C.t3, fontFamily: "'JetBrains Mono',monospace" }}>
          {wsStatus === "connected" ? "LIVE — WebSocket connected" : wsStatus === "connecting" ? "Connecting..." : "STOPPED"}
        </span>
        <div style={{ flex: 1 }} />
        <span style={{ fontSize: 11, color: C.t3 }}>{events.length} events captured</span>
      </div>
      <div ref={logRef} style={{ flex: 1, overflow: "auto", padding: "8px 0" }}>
        {events.length === 0 && (
          <div style={{ textAlign: "center", padding: 60, color: C.t3, fontSize: 13 }}>
            {isLive ? "Waiting for deals..." : "Click GO LIVE to start monitoring"}
          </div>
        )}
        {events.map(ev => (
          <div key={ev.id} onClick={() => { const a = accounts.find(x => x.login === ev.login); if (a) onSelect(a); }}
            style={{
              display: "flex", alignItems: "center", gap: 10, padding: "7px 16px",
              borderBottom: `1px solid ${C.border}`, cursor: "pointer",
              background: ev.isCorrelated ? "rgba(255,82,82,0.06)" : "transparent", transition: "background 0.15s",
            }}
            onMouseEnter={e => { e.currentTarget.style.background = "rgba(255,255,255,0.03)"; }}
            onMouseLeave={e => { e.currentTarget.style.background = ev.isCorrelated ? "rgba(255,82,82,0.06)" : "transparent"; }}>
            <span style={{ fontSize: 10, color: C.t3, fontFamily: "'JetBrains Mono',monospace", width: 60, flexShrink: 0 }}>{ev.time}</span>
            <span style={{ fontSize: 11, fontFamily: "'JetBrains Mono',monospace", color: C.t1, width: 48, flexShrink: 0 }}>{ev.login}</span>
            <span style={{ fontSize: 11, color: ev.action === "BUY" ? C.blue : C.red, width: 30, flexShrink: 0 }}>{ev.action}</span>
            <span style={{ fontSize: 11, color: C.t2, width: 65, flexShrink: 0 }}>{ev.symbol}</span>
            <span style={{ fontSize: 11, color: C.t2, width: 35 }}>{ev.volume}</span>
            <div style={{ flex: 1 }} />
            {ev.isCorrelated && <Badge color={C.red}>CORRELATED → {ev.correlated}</Badge>}
            {ev.scoreChange !== 0 && (
              <span style={{ fontSize: 10, fontFamily: "'JetBrains Mono',monospace", color: ev.scoreChange > 0 ? C.red : C.green }}>
                {ev.scoreChange > 0 ? "▲" : "▼"}{Math.abs(ev.scoreChange)}
              </span>
            )}
            <ScoreBar score={Math.min(100, ev.score + ev.scoreChange)} width={50} />
          </div>
        ))}
      </div>
    </div>
  );
}

// ─── Market Watch ──────────────────────────────────────────────
function MarketWatch({ isLive: _isLive }: { isLive: boolean }) {
  const [watchlist, setWatchlist] = useState<string[]>(DEFAULT_WATCHLIST);
  const [allSymbols, setAllSymbols] = useState<Array<{ symbol: string; description: string }>>([]);
  const [marketData, setMarketData] = useState<Record<string, MarketDataPoint>>({});
  const [addInput, setAddInput] = useState("");
  const [showAdd, setShowAdd] = useState(false);
  const [sortCol, setSortCol] = useState<string | null>(null);
  const [sortDir, setSortDir] = useState(-1);
  const [advanced, setAdvanced] = useState(false);
  const [wsStatus, setWsStatus] = useState<"disconnected" | "connecting" | "connected">("disconnected");
  const wsRef = useRef<WebSocket | null>(null);
  const marketDataRef = useRef(marketData);
  marketDataRef.current = marketData;
  const toggleSort = (col: string) => { if (sortCol === col) setSortDir(d => d * -1); else { setSortCol(col); setSortDir(-1); } };

  const [volumeBySymbol, setVolumeBySymbol] = useState<Record<string, VolumeData>>({});

  const [sessionHighLow, setSessionHighLow] = useState<Record<string, { high: number; low: number }>>({});

  const advancedData = useMemo(() => {
    const data: Record<string, { low: number; high: number }> = {};
    watchlist.forEach(sym => {
      const hl = sessionHighLow[sym];
      if (hl && hl.high > 0) {
        data[sym] = { low: hl.low, high: hl.high };
      } else {
        const md = marketData[sym];
        data[sym] = md && md.bid > 0 ? { low: md.bid, high: md.ask } : { low: 0, high: 0 };
      }
    });
    return data;
  }, [watchlist, marketData, sessionHighLow]);

  // Load available symbols from MT5 (for the add symbol search)
  useEffect(() => {
    fetch("/api/market/symbols")
      .then(r => r.ok ? r.json() : [])
      .then((syms: Array<{ symbol: string; description: string }>) => setAllSymbols(syms))
      .catch(() => {});
  }, []);

  // One-time initial snapshot from REST (fills cache before WS connects)
  useEffect(() => {
    fetch("/api/market/prices")
      .then(r => r.ok ? r.json() : [])
      .then((prices: Array<{ symbol: string; bid: number; ask: number; spread: number; changePercent: number; timeMsc: number; digits: number }>) => {
        setMarketData(prev => {
          const next = { ...prev };
          for (const p of prices) {
            if (p.bid > 0) {
              next[p.symbol] = { symbol: p.symbol, bid: p.bid, ask: p.ask, spread: p.spread, change24h: p.changePercent, digits: p.digits ?? 5 };
            }
          }
          return next;
        });
      })
      .catch(() => {});
  }, []);

  // Pending WS price updates — batched and flushed every animation frame for performance
  const pendingPricesRef = useRef<Record<string, MarketDataPoint>>({});
  const rafIdRef = useRef<number>(0);

  // Flush batched WS prices into React state once per frame (16ms)
  const flushPrices = useCallback(() => {
    const pending = pendingPricesRef.current;
    const keys = Object.keys(pending);
    if (keys.length === 0) return;
    pendingPricesRef.current = {};
    setMarketData(prev => {
      const next = { ...prev };
      for (const k of keys) { const v = pending[k]; if (v) next[k] = v; }
      return next;
    });
  }, []);

  // Fetch volume + session high/low periodically (slow-changing data, not ticks)
  useEffect(() => {
    const fetchVolume = async () => {
      try {
        const res = await fetch("/api/market/volume");
        if (!res.ok) return;
        const data = await res.json() as Array<{ symbol: string; buyVolume: number; sellVolume: number; netVolume: number; topBuyer: { login: number; volume: number }; topSeller: { login: number; volume: number } }>;
        const vols: Record<string, VolumeData> = {};
        for (const v of data) {
          vols[v.symbol] = { buy: v.buyVolume, sell: v.sellVolume, net: v.netVolume, topBuyer: v.topBuyer, topSeller: v.topSeller };
        }
        setVolumeBySymbol(vols);
      } catch { /* ignore */ }
    };
    const fetchSessionHL = async () => {
      try {
        const res = await fetch("/api/market/prices");
        if (!res.ok) return;
        const prices = await res.json() as Array<{ symbol: string; bid: number; ask: number; spread: number; changePercent: number; timeMsc: number; sessionHighBid: number; sessionLowBid: number }>;
        const hl: Record<string, { high: number; low: number }> = {};
        for (const p of prices) {
          if (p.sessionHighBid > 0) hl[p.symbol] = { high: p.sessionHighBid, low: p.sessionLowBid };
        }
        setSessionHighLow(hl);
      } catch { /* ignore */ }
    };
    fetchVolume();
    fetchSessionHL();
    // Session high/low every 5s; volume every 5s (both change only on deals, not ticks)
    const hlInterval = setInterval(fetchSessionHL, 5000);
    const volumeInterval = setInterval(fetchVolume, 5000);
    return () => { clearInterval(hlInterval); clearInterval(volumeInterval); };
  }, []);

  // WebSocket — zero delay live price stream (no polling, no throttle)
  useEffect(() => {
    let reconnectTimer: ReturnType<typeof setTimeout>;
    let ws: WebSocket;

    const connect = () => {
      setWsStatus("connecting");
      const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
      ws = new WebSocket(`${proto}//${window.location.host}/ws`);
      wsRef.current = ws;

      ws.onopen = () => {
        setWsStatus("connected");
        ws.send(JSON.stringify({ subscribe: ["prices"] }));
      };

      ws.onmessage = (ev) => {
        try {
          const msg = JSON.parse(ev.data) as { type: string; data: { symbol: string; bid: number; ask: number; spread: number; changePercent: number; timeMsc: number; digits: number } };
          if (msg.type === "prices" && msg.data) {
            const p = msg.data;
            if (p.bid > 0) {
              // Batch into pending ref — flushed to React state once per animation frame
              pendingPricesRef.current[p.symbol] = { symbol: p.symbol, bid: p.bid, ask: p.ask, spread: p.spread, change24h: p.changePercent, digits: p.digits ?? 5 };
              if (!rafIdRef.current) {
                rafIdRef.current = requestAnimationFrame(() => { rafIdRef.current = 0; flushPrices(); });
              }
            }
          }
        } catch { /* ignore parse errors */ }
      };

      ws.onclose = () => {
        setWsStatus("disconnected");
        wsRef.current = null;
        reconnectTimer = setTimeout(connect, 1000);
      };
      ws.onerror = () => { ws.close(); };
    };

    connect();
    return () => {
      clearTimeout(reconnectTimer);
      ws?.close();
      wsRef.current = null;
      setWsStatus("disconnected");
      if (rafIdRef.current) { cancelAnimationFrame(rafIdRef.current); rafIdRef.current = 0; }
    };
  }, [flushPrices]);

  // Filtered search results for add symbol dropdown
  const searchResults = useMemo(() => {
    if (!addInput.trim()) return [];
    const q = addInput.toUpperCase().trim();
    return allSymbols
      .filter(s => !watchlist.includes(s.symbol) &&
        (s.symbol.toUpperCase().includes(q) || s.description.toUpperCase().includes(q)))
      .slice(0, 15);
  }, [addInput, allSymbols, watchlist]);

  const handleAdd = (sym: string) => {
    if (sym && !watchlist.includes(sym)) setWatchlist(prev => [...prev, sym]);
    setAddInput(""); setShowAdd(false);
  };
  const handleRemove = (sym: string) => setWatchlist(prev => prev.filter(s => s !== sym));

  const headers = [{ k: "symbol", l: "Symbol" }, { k: "bid", l: "Bid" }, { k: "ask", l: "Ask" }, { k: "spread", l: "Spread" }, { k: "buyVol", l: "Buy Vol" }, { k: "sellVol", l: "Sell Vol" }, { k: "netVol", l: "Net Vol" }, { k: "change", l: "24h %" }, { k: "", l: "" }];

  // Format prices using the symbol's actual digits from MT5 (e.g. EURUSD=5, US30=0, XAUUSD=2)
  // Falls back to magnitude heuristic if digits not yet available
  const formatPrice = (val: number, digits?: number): string => {
    if (val === 0) return "0";
    if (digits !== undefined && digits >= 0) return val.toFixed(digits);
    const abs = Math.abs(val);
    if (abs >= 10000) return val.toFixed(0);
    if (abs >= 100) return val.toFixed(2);
    if (abs >= 10) return val.toFixed(3);
    return val.toFixed(5);
  };

  type MktRow = { sym: string; bid: number; ask: number; spread: number; buyVol: number; sellVol: number; netVol: number; change: number; digits: number };
  let rows: MktRow[] = watchlist.map(sym => {
    const md = marketData[sym];
    const vol = volumeBySymbol[sym];
    return { sym, bid: md?.bid ?? 0, ask: md?.ask ?? 0, spread: md?.spread ?? 0, buyVol: vol?.buy ?? 0, sellVol: vol?.sell ?? 0, netVol: vol?.net ?? 0, change: md?.change24h ?? 0, digits: md?.digits ?? 5 };
  });
  // Default sort: symbols with live prices first, then alphabetical
  if (!sortCol) {
    rows = [...rows].sort((a, b) => {
      if (a.bid > 0 && b.bid <= 0) return -1;
      if (a.bid <= 0 && b.bid > 0) return 1;
      return a.sym.localeCompare(b.sym);
    });
  }
  if (sortCol) {
    rows = [...rows].sort((a, b) => {
      const av = sortCol === "symbol" ? a.sym : (a as Record<string, unknown>)[sortCol];
      const bv = sortCol === "symbol" ? b.sym : (b as Record<string, unknown>)[sortCol];
      if (typeof av === "string" && typeof bv === "string") return av.localeCompare(bv) * sortDir;
      return ((av as number) - (bv as number)) * sortDir;
    });
  }

  const priceCount = Object.values(marketData).filter(m => m.bid > 0).length;

  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", overflow: "hidden" }}>
      <div style={{ padding: "10px 16px", borderBottom: `1px solid ${C.border}`, display: "flex", alignItems: "center", gap: 10 }}>
        <span style={{ fontSize: 11, color: C.t3, fontFamily: "'JetBrains Mono',monospace" }}>{watchlist.length} symbols</span>
        <button onClick={() => setAdvanced(a => !a)} style={{
          padding: "4px 10px", borderRadius: 4, cursor: "pointer", border: `1px solid ${advanced ? C.purple + "50" : C.border}`,
          background: advanced ? C.purpleBg : "transparent", color: advanced ? C.purple : C.t3,
          fontSize: 9, fontFamily: "'JetBrains Mono',monospace", fontWeight: 600, display: "flex", alignItems: "center", gap: 5,
        }}><span style={{ fontSize: 11 }}>{advanced ? "☑" : "☐"}</span> ADVANCED</button>
        <div style={{ flex: 1 }} />
        {showAdd ? (
          <div style={{ display: "flex", gap: 6, alignItems: "center", position: "relative" }}>
            <div style={{ position: "relative" }}>
              <input value={addInput} onChange={e => setAddInput(e.target.value)}
                onKeyDown={e => { if (e.key === "Enter" && searchResults.length > 0 && searchResults[0]) handleAdd(searchResults[0].symbol); if (e.key === "Escape") { setShowAdd(false); setAddInput(""); } }}
                placeholder="Search symbols..." autoFocus
                style={{ width: 220, background: C.bg3, border: `1px solid ${C.border}`, borderRadius: 6, padding: "5px 10px", color: C.t1, fontSize: 11, fontFamily: "'JetBrains Mono',monospace", outline: "none" }} />
              {searchResults.length > 0 && (
                <div style={{ position: "absolute", top: "100%", left: 0, right: 0, marginTop: 4, background: C.bg2, border: `1px solid ${C.border}`, borderRadius: 6, maxHeight: 250, overflow: "auto", zIndex: 100, boxShadow: "0 8px 24px rgba(0,0,0,0.5)" }}>
                  {searchResults.map(s => (
                    <div key={s.symbol} onClick={() => handleAdd(s.symbol)}
                      onMouseEnter={e => { e.currentTarget.style.background = "rgba(255,255,255,0.05)"; }}
                      onMouseLeave={e => { e.currentTarget.style.background = "transparent"; }}
                      style={{ padding: "6px 10px", cursor: "pointer", display: "flex", justifyContent: "space-between", alignItems: "center", borderBottom: `1px solid ${C.border}` }}>
                      <span style={{ fontSize: 11, fontWeight: 600, color: C.t1, fontFamily: "'JetBrains Mono',monospace" }}>{s.symbol}</span>
                      <span style={{ fontSize: 9, color: C.t3, maxWidth: 120, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{s.description}</span>
                    </div>
                  ))}
                </div>
              )}
            </div>
            <button onClick={() => { setShowAdd(false); setAddInput(""); }} style={{ padding: "5px 10px", borderRadius: 5, border: `1px solid ${C.border}`, background: "transparent", color: C.t3, fontSize: 10, cursor: "pointer" }}>CANCEL</button>
          </div>
        ) : (
          <button onClick={() => setShowAdd(true)} style={{ padding: "5px 12px", borderRadius: 5, border: `1px solid ${C.teal}40`, background: C.tealBg, color: C.teal, fontSize: 11, fontWeight: 600, fontFamily: "'JetBrains Mono',monospace", cursor: "pointer", display: "flex", alignItems: "center", gap: 4 }}>+ ADD SYMBOL</button>
        )}
      </div>
      <div style={{ flex: 1, overflow: "auto" }}>
        <table style={{ width: "100%", borderCollapse: "collapse" }}>
          <thead><tr>
            {headers.map(h => (
              <th key={h.k || "del"} onClick={() => h.k && toggleSort(h.k)} style={{
                padding: "8px 10px", fontSize: 9, fontFamily: "'JetBrains Mono',monospace", fontWeight: 600,
                color: sortCol === h.k ? C.teal : C.t3, textAlign: h.k === "" ? "center" : "left",
                borderBottom: `1px solid ${C.border}`, position: "sticky", top: 0, background: C.bg2,
                textTransform: "uppercase", letterSpacing: "0.5px", cursor: h.k ? "pointer" : "default", userSelect: "none",
              }}>{h.l} {sortCol === h.k ? (sortDir === 1 ? "↑" : "↓") : ""}</th>
            ))}
          </tr></thead>
          <tbody>
            {rows.map(({ sym, bid, ask, spread, buyVol, sellVol, netVol, change, digits }) => {
              const netColor = netVol > 0 ? C.green : netVol < 0 ? C.red : C.t3;
              const changeColor = change >= 0 ? C.green : C.red;
              const maxVol = Math.max(buyVol, sellVol, 1);
              const adv = advancedData[sym] ?? { low: 0, high: 0 };
              const vol = volumeBySymbol[sym] ?? { buy: 0, sell: 0, net: 0, topBuyer: { login: 0, volume: 0 }, topSeller: { login: 0, volume: 0 } };
              const bdr = advanced ? "none" : `1px solid ${C.border}`;
              return (
                <Fragment key={sym}>
                  <tr onMouseEnter={e => { e.currentTarget.style.background = "rgba(255,255,255,0.03)"; }} onMouseLeave={e => { e.currentTarget.style.background = "transparent"; }}>
                    <td style={{ padding: "10px 10px", borderBottom: bdr }}><span style={{ fontSize: 13, fontWeight: 600, color: bid > 0 ? C.t1 : C.t3, fontFamily: "'JetBrains Mono',monospace" }}>{sym}</span></td>
                    <td style={{ padding: "10px 10px", fontSize: 13, fontFamily: "'JetBrains Mono',monospace", color: bid > 0 ? C.blue : C.t3, borderBottom: bdr, opacity: bid > 0 ? 1 : 0.4 }}>{bid > 0 ? formatPrice(bid, digits) : "No feed"}</td>
                    <td style={{ padding: "10px 10px", fontSize: 13, fontFamily: "'JetBrains Mono',monospace", color: bid > 0 ? C.red : C.t3, borderBottom: bdr, opacity: bid > 0 ? 1 : 0.4 }}>{ask > 0 ? formatPrice(ask, digits) : "—"}</td>
                    <td style={{ padding: "10px 10px", fontSize: 11, fontFamily: "'JetBrains Mono',monospace", color: C.t3, borderBottom: bdr }}>{spread ? formatPrice(spread, digits) : "—"}</td>
                    <td style={{ padding: "10px 10px", borderBottom: bdr }}>
                      <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                        <div style={{ width: 50, height: 4, borderRadius: 2, background: "rgba(255,255,255,0.06)" }}><div style={{ width: `${(buyVol / maxVol) * 100}%`, height: "100%", borderRadius: 2, background: C.blue, transition: "width 0.3s" }} /></div>
                        <span style={{ fontSize: 11, fontFamily: "'JetBrains Mono',monospace", color: C.blue }}>{buyVol}</span>
                      </div>
                    </td>
                    <td style={{ padding: "10px 10px", borderBottom: bdr }}>
                      <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                        <div style={{ width: 50, height: 4, borderRadius: 2, background: "rgba(255,255,255,0.06)" }}><div style={{ width: `${(sellVol / maxVol) * 100}%`, height: "100%", borderRadius: 2, background: C.red, transition: "width 0.3s" }} /></div>
                        <span style={{ fontSize: 11, fontFamily: "'JetBrains Mono',monospace", color: C.red }}>{sellVol}</span>
                      </div>
                    </td>
                    <td style={{ padding: "10px 10px", borderBottom: bdr }}><span style={{ fontSize: 12, fontWeight: 600, fontFamily: "'JetBrains Mono',monospace", color: netColor }}>{netVol > 0 ? "+" : ""}{netVol}</span></td>
                    <td style={{ padding: "10px 10px", borderBottom: bdr }}><span style={{ fontSize: 11, fontFamily: "'JetBrains Mono',monospace", color: changeColor }}>{change >= 0 ? "▲" : "▼"} {Math.abs(change).toFixed(2)}%</span></td>
                    <td style={{ padding: "10px 6px", borderBottom: bdr, textAlign: "center" }}>
                      <button onClick={() => handleRemove(sym)} style={{ background: "none", border: "none", color: C.t3, fontSize: 14, cursor: "pointer", opacity: 0.5, lineHeight: 1 }} title="Remove symbol"
                        onMouseEnter={e => { e.currentTarget.style.opacity = "1"; }} onMouseLeave={e => { e.currentTarget.style.opacity = "0.5"; }}>×</button>
                    </td>
                  </tr>
                  {advanced && (
                    <tr style={{ background: "rgba(155,138,255,0.03)" }}>
                      <td style={{ padding: "2px 10px 8px", borderBottom: `1px solid ${C.border}`, fontSize: 9, color: C.t3, fontFamily: "'JetBrains Mono',monospace" }} />
                      <td style={{ padding: "2px 10px 8px", borderBottom: `1px solid ${C.border}` }}><span style={{ fontSize: 9, color: C.t3, letterSpacing: "0.5px" }}>LOW </span><span style={{ fontSize: 10, fontFamily: "'JetBrains Mono',monospace", color: C.amber }}>{adv.low > 0 ? formatPrice(adv.low, digits) : "—"}</span></td>
                      <td style={{ padding: "2px 10px 8px", borderBottom: `1px solid ${C.border}` }}><span style={{ fontSize: 9, color: C.t3, letterSpacing: "0.5px" }}>HIGH </span><span style={{ fontSize: 10, fontFamily: "'JetBrains Mono',monospace", color: C.teal }}>{adv.high > 0 ? formatPrice(adv.high, digits) : "—"}</span></td>
                      <td style={{ padding: "2px 10px 8px", borderBottom: `1px solid ${C.border}` }} />
                      <td style={{ padding: "2px 10px 8px", borderBottom: `1px solid ${C.border}` }}><span style={{ fontSize: 9, color: C.t3 }}>TOP </span><span style={{ fontSize: 10, fontFamily: "'JetBrains Mono',monospace", color: C.blue }}>{vol.topBuyer.login}</span><span style={{ fontSize: 9, color: C.t3 }}> ({vol.topBuyer.volume})</span></td>
                      <td style={{ padding: "2px 10px 8px", borderBottom: `1px solid ${C.border}` }}><span style={{ fontSize: 9, color: C.t3 }}>TOP </span><span style={{ fontSize: 10, fontFamily: "'JetBrains Mono',monospace", color: C.red }}>{vol.topSeller.login}</span><span style={{ fontSize: 9, color: C.t3 }}> ({vol.topSeller.volume})</span></td>
                      <td style={{ padding: "2px 10px 8px", borderBottom: `1px solid ${C.border}` }} />
                      <td style={{ padding: "2px 10px 8px", borderBottom: `1px solid ${C.border}` }} />
                      <td style={{ padding: "2px 10px 8px", borderBottom: `1px solid ${C.border}` }} />
                    </tr>
                  )}
                </Fragment>
              );
            })}
          </tbody>
        </table>
      </div>
      <div style={{ padding: "8px 16px", borderTop: `1px solid ${C.border}`, display: "flex", gap: 20, alignItems: "center" }}>
        <span style={{ fontSize: 10, color: C.t3, fontFamily: "'JetBrains Mono',monospace" }}>
          {wsStatus === "connected" ? "🟢 LIVE" : wsStatus === "connecting" ? "🟡 CONNECTING" : "⚫ OFFLINE"} — MT5 PRICES
        </span>
        <span style={{ fontSize: 11, fontFamily: "'JetBrains Mono',monospace", color: C.teal }}>{priceCount}/{watchlist.length} symbols with data</span>
      </div>
    </div>
  );
}

// ─── Threat View (v2) ──────────────────────────────────────────
function ThreatView({ accounts, version, onSelect }: { accounts: Account[]; version: string; onSelect: (a: Account) => void }) {
  const [selectedThreat, setSelectedThreat] = useState("all");
  const threats = [
    { id: "all", label: "All Threats", color: C.t1, icon: "◆" },
    { id: "ring", label: "Ring Trading", color: C.red, icon: "⟷" },
    { id: "latency", label: "Latency Arb", color: C.coral, icon: "⚡" },
    { id: "bonus", label: "Bonus Abuse", color: C.amber, icon: "💰" },
    { id: "bot", label: "Bot Farming", color: C.purple, icon: "🤖" },
  ];
  const filtered = useMemo(() => {
    if (selectedThreat === "all") return accounts.filter(a => Object.values(a.threats).some(v => v > 0.4));
    const key = selectedThreat as keyof Threats;
    return accounts.filter(a => a.threats[key] > 0.4).sort((a, b) => b.threats[key] - a.threats[key]);
  }, [accounts, selectedThreat]);

  if (version === "v1") {
    return (
      <div style={{ flex: 1, display: "flex", alignItems: "center", justifyContent: "center", flexDirection: "column", gap: 12 }}>
        <span style={{ fontSize: 40, opacity: 0.3 }}>◆</span>
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
                <td style={{ padding: "7px 10px", fontSize: 11, fontFamily: "'JetBrains Mono',monospace", color: C.t3, borderBottom: `1px solid ${C.border}` }}>{acc.primaryEA || "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// ─── Connection Status Types + Hook ─────────────────────────────
interface ConnectionStatus {
  connected: boolean; server: string; login: string;
  accountsInScope: number; uptimeSeconds: number; error: string | null;
}
interface ConnectionLogEntry { timestamp: string; level: string; message: string }
interface ScanSettings { historyDays: number; minDeposit: number; pollIntervalMs: number; criticalThreshold: number }

function useConnectionStatus() {
  const [status, setStatus] = useState<ConnectionStatus>({
    connected: false, server: "", login: "", accountsInScope: 0, uptimeSeconds: 0, error: null,
  });
  useEffect(() => {
    let active = true;
    const poll = async () => {
      try {
        const res = await fetch("/api/settings/connection/status");
        if (res.ok && active) setStatus(await res.json() as ConnectionStatus);
      } catch { /* backend not reachable */ }
    };
    poll();
    const id = setInterval(poll, 5000);
    return () => { active = false; clearInterval(id); };
  }, []);
  return status;
}

// ─── Settings View ──────────────────────────────────────────────
function SettingsView({ connectionStatus }: { connectionStatus: ConnectionStatus }) {
  const [server, setServer] = useState("");
  const [login, setLogin] = useState("");
  const [password, setPassword] = useState("");
  const [groupMask, setGroupMask] = useState("*");
  const [showPassword, setShowPassword] = useState(false);
  const [connecting, setConnecting] = useState(false);
  const [logs, setLogs] = useState<ConnectionLogEntry[]>([]);
  const [scan, setScan] = useState<ScanSettings>({ historyDays: 90, minDeposit: 0, pollIntervalMs: 5000, criticalThreshold: 70 });
  const [scanSaved, setScanSaved] = useState(false);
  const logRef = useRef<HTMLDivElement>(null);

  // Load initial config + scan settings + logs
  useEffect(() => {
    (async () => {
      try {
        const [cfgRes, scanRes, logRes] = await Promise.all([
          fetch("/api/settings/connection"),
          fetch("/api/settings/scan"),
          fetch("/api/settings/connection/logs"),
        ]);
        if (cfgRes.ok) {
          const cfg = await cfgRes.json() as { server: string; login: string; groupMask: string };
          if (cfg.server && cfg.server !== "simulator:0") { setServer(cfg.server); setLogin(cfg.login); setGroupMask(cfg.groupMask); }
        }
        if (scanRes.ok) setScan(await scanRes.json() as ScanSettings);
        if (logRes.ok) setLogs(await logRes.json() as ConnectionLogEntry[]);
      } catch { /* backend not reachable */ }
    })();
  }, []);

  // Poll logs
  useEffect(() => {
    const id = setInterval(async () => {
      try {
        const res = await fetch("/api/settings/connection/logs");
        if (res.ok) setLogs(await res.json() as ConnectionLogEntry[]);
      } catch { /* ignore */ }
    }, 3000);
    return () => clearInterval(id);
  }, []);

  const handleConnect = useCallback(async () => {
    setConnecting(true);
    try {
      const res = await fetch("/api/settings/connection", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ server, login, password: password || undefined, groupMask }),
      });
      if (res.ok) { setPassword(""); }
      else {
        const err = await res.json().catch(() => null) as { error?: string } | null;
        if (err?.error) alert(err.error);
      }
    } catch { /* ignore */ }
    finally { setConnecting(false); }
  }, [server, login, password, groupMask]);

  const handleDisconnect = useCallback(async () => {
    try { await fetch("/api/settings/connection/disconnect", { method: "POST" }); } catch { /* ignore */ }
  }, []);

  const handleScanSave = useCallback(async () => {
    try {
      const res = await fetch("/api/settings/scan", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(scan),
      });
      if (res.ok) { setScanSaved(true); setTimeout(() => setScanSaved(false), 2000); }
    } catch { /* ignore */ }
  }, [scan]);

  const labelS: CSSProperties = { fontSize: 9, fontWeight: 600, color: C.t3, textTransform: "uppercase", letterSpacing: "0.5px", marginBottom: 4 };
  const inputS: CSSProperties = {
    width: "100%", padding: "8px 10px", fontSize: 12, fontFamily: "'JetBrains Mono',Consolas,monospace",
    background: C.bg, border: `1px solid ${C.border}`, borderRadius: 6, color: C.t1, outline: "none",
  };
  const cardS: CSSProperties = {
    background: C.bg3, borderRadius: 10, border: `1px solid ${C.border}`, padding: 16,
  };
  const btnPrimary: CSSProperties = {
    padding: "8px 20px", fontSize: 12, fontWeight: 600, borderRadius: 6, border: "none", cursor: "pointer",
    background: C.teal, color: C.bg, fontFamily: "'JetBrains Mono',monospace",
  };
  const btnDanger: CSSProperties = {
    ...btnPrimary, background: C.red,
  };
  const numInputS: CSSProperties = {
    ...inputS, width: 120,
  };

  return (
    <div style={{ flex: 1, overflow: "auto", padding: 20, display: "flex", flexDirection: "column", gap: 16 }}>
      {/* Connection Status Banner */}
      <div style={{ ...cardS, display: "flex", alignItems: "center", gap: 16 }}>
        <div style={{
          width: 12, height: 12, borderRadius: "50%",
          background: connecting ? C.amber : connectionStatus.connected ? C.teal : C.red,
          boxShadow: connecting ? `0 0 12px ${C.amber}60` : connectionStatus.connected ? `0 0 12px ${C.teal}60` : `0 0 12px ${C.red}60`,
          animation: connecting ? "pulse 1.5s infinite" : "none",
        }} />
        <div style={{ flex: 1 }}>
          <div style={{ fontSize: 13, fontWeight: 600, color: C.t1 }}>
            {connecting ? "Connecting..." : connectionStatus.connected ? "Connected" : "Disconnected"}
          </div>
          {connectionStatus.connected && (
            <div style={{ fontSize: 11, color: C.t3, fontFamily: "'JetBrains Mono',monospace", marginTop: 2 }}>
              {connectionStatus.server} · login {connectionStatus.login} · {connectionStatus.accountsInScope} accounts · uptime {formatUptime(connectionStatus.uptimeSeconds)}
            </div>
          )}
          {connectionStatus.error && !connectionStatus.connected && (
            <div style={{ fontSize: 11, color: C.red, marginTop: 2 }}>{connectionStatus.error}</div>
          )}
        </div>
        {connectionStatus.connected ? (
          <button onClick={handleDisconnect} style={btnDanger}>DISCONNECT</button>
        ) : (
          <button onClick={handleConnect} disabled={connecting || !server || !login} style={{
            ...btnPrimary, opacity: connecting || !server || !login ? 0.5 : 1,
          }}>
            {connecting ? "CONNECTING..." : password ? "CONNECT" : "RECONNECT"}
          </button>
        )}
      </div>

      <div style={{ display: "flex", gap: 16, flex: 1, minHeight: 0 }}>
        {/* Left column: Connection + Scan */}
        <div style={{ flex: 1, display: "flex", flexDirection: "column", gap: 16 }}>
          {/* MT5 Manager Connection Form */}
          <div style={cardS}>
            <div style={{ fontSize: 12, fontWeight: 600, color: C.t1, marginBottom: 14 }}>MT5 Manager Connection</div>
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              <div>
                <div style={labelS}>Server Address</div>
                <input value={server} onChange={e => setServer(e.target.value)} placeholder="mt5-live.broker.com:443" style={inputS} />
              </div>
              <div>
                <div style={labelS}>Manager Login</div>
                <input value={login} onChange={e => setLogin(e.target.value)} placeholder="Manager ID number" style={inputS} />
              </div>
              <div>
                <div style={labelS}>Manager Password</div>
                <div style={{ position: "relative" }}>
                  <input
                    type={showPassword ? "text" : "password"} value={password}
                    onChange={e => setPassword(e.target.value)} placeholder="••••••••" style={inputS}
                  />
                  <button onClick={() => setShowPassword(v => !v)} style={{
                    position: "absolute", right: 8, top: "50%", transform: "translateY(-50%)",
                    background: "none", border: "none", cursor: "pointer", color: C.t3, fontSize: 14,
                  }}>{showPassword ? "🙈" : "👁"}</button>
                </div>
              </div>
              <div>
                <div style={labelS}>Group Mask</div>
                <input value={groupMask} onChange={e => setGroupMask(e.target.value)} placeholder="forex\retail*" style={inputS} />
                <div style={{ fontSize: 10, color: C.t3, marginTop: 4 }}>
                  Wildcard syntax: * matches any, \ separates groups (e.g. forex\retail\*)
                </div>
              </div>
            </div>
          </div>

          {/* Scan Settings */}
          <div style={cardS}>
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginBottom: 14 }}>
              <div style={{ fontSize: 12, fontWeight: 600, color: C.t1 }}>Scan Settings</div>
              <button onClick={handleScanSave} style={{ ...btnPrimary, padding: "5px 14px", fontSize: 11 }}>
                {scanSaved ? "✓ SAVED" : "SAVE"}
              </button>
            </div>
            <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12 }}>
              <div>
                <div style={labelS}>History Days</div>
                <input type="number" value={scan.historyDays} onChange={e => setScan(s => ({ ...s, historyDays: +e.target.value }))} style={numInputS} />
              </div>
              <div>
                <div style={labelS}>Min Deposit ($)</div>
                <input type="number" value={scan.minDeposit} onChange={e => setScan(s => ({ ...s, minDeposit: +e.target.value }))} style={numInputS} />
              </div>
              <div>
                <div style={labelS}>Poll Interval (ms)</div>
                <input type="number" value={scan.pollIntervalMs} onChange={e => setScan(s => ({ ...s, pollIntervalMs: +e.target.value }))} style={numInputS} />
              </div>
              <div>
                <div style={labelS}>Critical Threshold</div>
                <input type="number" value={scan.criticalThreshold} onChange={e => setScan(s => ({ ...s, criticalThreshold: +e.target.value }))} style={numInputS} />
              </div>
            </div>
          </div>
        </div>

        {/* Right column: Connection Log */}
        <div style={{ ...cardS, flex: 1, display: "flex", flexDirection: "column", minHeight: 300 }}>
          <div style={{ fontSize: 12, fontWeight: 600, color: C.t1, marginBottom: 10 }}>Connection Log</div>
          <div ref={logRef} style={{
            flex: 1, overflow: "auto", fontSize: 11, fontFamily: "'JetBrains Mono',Consolas,monospace",
            lineHeight: 1.7, color: C.t2,
          }}>
            {logs.length === 0 && (
              <div style={{ color: C.t3, textAlign: "center", marginTop: 40 }}>No connection events yet</div>
            )}
            {logs.map((entry, i) => {
              const icon = entry.level === "success" ? "✓" : entry.level === "error" ? "✗" : "ℹ";
              const iconColor = entry.level === "success" ? C.teal : entry.level === "error" ? C.red : C.t3;
              const ts = new Date(entry.timestamp);
              const timeStr = ts.toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
              return (
                <div key={i} style={{ display: "flex", gap: 8, padding: "2px 0" }}>
                  <span style={{ color: C.t3, flexShrink: 0 }}>{timeStr}</span>
                  <span style={{ color: iconColor, flexShrink: 0, width: 12, textAlign: "center" }}>{icon}</span>
                  <span style={{ color: entry.level === "error" ? C.red : C.t2 }}>{entry.message}</span>
                </div>
              );
            })}
          </div>
        </div>
      </div>
    </div>
  );
}

function formatUptime(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  return `${h}h ${m}m`;
}

// ─── App ───────────────────────────────────────────────────────
export default function App() {
  const [view, setView] = useState("market");
  const [version, setVersion] = useState("v1");
  const [selectedAccount, setSelectedAccount] = useState<Account | null>(null);
  const [isLive, setIsLive] = useState(false);
  const [flashRows, setFlashRows] = useState<Set<number>>(new Set());
  const [accounts, setAccounts] = useState<Account[]>([]);
  const connectionStatus = useConnectionStatus();

  // Fetch real accounts from API (always poll — show real data when available)
  useEffect(() => {
    const fetchAccounts = async () => {
      try {
        const res = await fetch("/api/accounts");
        if (!res.ok) return;
        const data = await res.json();
        if (!Array.isArray(data) || data.length === 0) return;
        const mapped: Account[] = data.map((a: any) => ({
          login: a.login,
          name: a.name ?? a.login.toString(),
          group: a.group ?? "",
          score: a.abuseScore ?? 0,
          sev: a.riskLevel === "Critical" ? "CRITICAL" : a.riskLevel === "High" ? "HIGH" : a.riskLevel === "Medium" ? "MEDIUM" : "LOW",
          deposits: 0,
          totalDeposited: a.totalDeposits ?? 0,
          bonuses: 0,
          volume: a.totalVolume ?? 0,
          commissions: a.totalCommission ?? 0,
          pnl: a.totalProfit ?? 0,
          tradeCount: a.totalTrades ?? 0,
          expertRatio: a.expertTradeRatio ?? 0,
          ib: "",
          primaryEA: 0,
          avgHoldSec: 0,
          winRate: 0,
          tradesPerHour: 0,
          timingCV: a.timingEntropyCV ?? 0,
          isRingMember: a.isRingMember ?? false,
          ringPartners: [],
          bonusToDepRatio: 0,
          lastActivity: a.lastScored ?? new Date().toISOString(),
          threats: { ring: a.isRingMember ? 0.8 : 0, latency: 0, bonus: 0, bot: a.expertTradeRatio > 0.5 ? 0.7 : 0 },
          routing: (a.abuseScore ?? 0) >= 60 ? "A-Book" : (a.abuseScore ?? 0) >= 35 ? "Review" : "B-Book",
        }));
        setAccounts(mapped.sort((a, b) => b.score - a.score));
      } catch { /* ignore fetch errors */ }
    };
    fetchAccounts();
    const interval = setInterval(fetchAccounts, 5000);
    return () => clearInterval(interval);
  }, []);

  useEffect(() => {
    const critLogins = accounts.filter(a => a.sev === "CRITICAL").map(a => a.login);
    setFlashRows(new Set(critLogins));
  }, [accounts]);

  const handleSelect = (acc: Account) => { setSelectedAccount(acc); };

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
      <Sidebar view={view} setView={(v) => { setView(v); setSelectedAccount(null); }} version={version} setVersion={setVersion} connected={connectionStatus.connected} />
      <div style={{ flex: 1, display: "flex", flexDirection: "column", overflow: "hidden" }}>
        <TopBar view={view} accounts={accounts} version={version} isLive={isLive} onToggleLive={() => setIsLive(v => !v)} />
        {selectedAccount ? (
          <AccountDetail account={selectedAccount} version={version} onBack={() => setSelectedAccount(null)} />
        ) : (
          <>
            {view === "grid" && <AccountGrid accounts={accounts} version={version} onSelect={handleSelect} flashRows={flashRows} />}
            {view === "live" && <LiveMonitor accounts={accounts} isLive={isLive} onSelect={handleSelect} />}
            {view === "market" && <MarketWatch isLive={isLive} />}
            {view === "threats" && <ThreatView accounts={accounts} version={version} onSelect={handleSelect} />}
            {view === "settings" && <SettingsView connectionStatus={connectionStatus} />}
          </>
        )}
        <div style={{
          padding: "6px 16px", borderTop: `1px solid ${C.border}`,
          display: "flex", alignItems: "center", justifyContent: "space-between",
          fontSize: 10, color: C.t3, fontFamily: "'JetBrains Mono',monospace",
        }}>
          <span>Rebate Abuse Detector {version === "v2" ? "v2.0" : "v1.0"} — BBC Corp</span>
          <span style={{ color: connectionStatus.connected ? C.teal : C.red }}>
            MT5: {connectionStatus.connected ? `${connectionStatus.server} — Connected` : "Disconnected"}
          </span>
          <span>{new Date().toLocaleDateString("en-GB")} {new Date().toLocaleTimeString("en-GB")}</span>
        </div>
      </div>
    </div>
  );
}
