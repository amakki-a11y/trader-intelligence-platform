import { useState, useEffect, useMemo, useRef } from "react";
import type { CSSProperties } from "react";
import C, { sevColor } from "../styles/colors";
import type { Account, Deal, OpenTrade, MoneyOp } from "../store/TipStore";
import type { DealResponse, PositionResponse } from "../types/api";
import { apiFetch, getAccessToken } from "../services/api";
import { parseDeal } from "../utils/parsers";
import type { RawDealResponse } from "../utils/parsers";
import AIRoutingPanel from "./AIRoutingPanel";

function Badge({ color, children }: { color: string; children: React.ReactNode }) {
  return (
    <span style={{
      display: "inline-block", fontFamily: "'JetBrains Mono',monospace",
      fontSize: 10, fontWeight: 600, letterSpacing: "0.5px",
      color, background: color + "14", border: `1px solid ${color}40`,
      borderRadius: 4, padding: "2px 8px",
    }}>{children}</span>
  );
}

function RoutingBadge({ routing }: { routing: string }) {
  const colors: Record<string, string> = { "A-Book": C.red, "Review": C.amber, "B-Book": C.green };
  return <Badge color={colors[routing] ?? C.t3}>{routing}</Badge>;
}

interface AccountDetailProps {
  account: Account;
  version: string;
  onBack: () => void;
}

function AccountDetail({ account, version, onBack }: AccountDetailProps) {
  const [deals, setDeals] = useState<Deal[]>([]);
  const [tab, setTab] = useState("history");
  const [dealSortCol, setDealSortCol] = useState("ticket");
  const [dealSortDir, setDealSortDir] = useState(-1);
  const [fetchError, setFetchError] = useState<string | null>(null);
  const toggleDealSort = (col: string) => {
    if (dealSortCol === col) setDealSortDir(d => d * -1);
    else { setDealSortCol(col); setDealSortDir(-1); }
  };
  const sortedDeals = useMemo(() => {
    return [...deals].sort((a, b) => {
      const av = (a as unknown as Record<string, unknown>)[dealSortCol];
      const bv = (b as unknown as Record<string, unknown>)[dealSortCol];
      if (typeof av === "number" && typeof bv === "number") return (av - bv) * dealSortDir;
      return String(av).localeCompare(String(bv)) * dealSortDir;
    });
  }, [deals, dealSortCol, dealSortDir]);
  const [acctInfoExpanded, setAcctInfoExpanded] = useState(false);
  const [dateFrom, setDateFrom] = useState(() => { const d = new Date(); d.setDate(d.getDate() - 90); return d.toISOString().slice(0, 10); });
  const [dateTo, setDateTo] = useState(() => new Date().toISOString().slice(0, 10));
  const [loadTrigger, setLoadTrigger] = useState(0); // incremented to force refetch

  const [acctInfo, setAcctInfo] = useState({
    balance: 0, equity: 0, margin: 0, freeMargin: 0,
    marginLevel: "0%", leverage: "1:100",
    credit: 0, registration: "", lastLogin: "", server: "", currency: "USD",
  });

  // Fetch live account info from MT5 (balance, margin, credit, registration, etc.)
  useEffect(() => {
    const controller = new AbortController();
    apiFetch(`/api/accounts/${account.login}/info`, { signal: controller.signal })
      .then(r => r.json())
      .then(d => {
        const data = d as Record<string, unknown>;
        if (data.error) return;

        const regTime = data.registrationTime as number;
        const lastTime = data.lastAccessTime as number;

        setAcctInfo(prev => ({
          ...prev,
          balance: (data.balance as number) ?? 0,
          equity: (data.equity as number) ?? 0,
          margin: (data.margin as number) ?? 0,
          freeMargin: (data.freeMargin as number) ?? 0,
          credit: (data.credit as number) ?? 0,
          leverage: data.leverage ? `1:${data.leverage}` : prev.leverage,
          currency: (data.currency as string) ?? "USD",
          registration: regTime > 0 ? new Date(regTime * 1000).toLocaleDateString("en-GB", { day: "2-digit", month: "short", year: "numeric" }) : "",
          lastLogin: lastTime > 0 ? new Date(lastTime * 1000).toLocaleString("en-GB", { day: "2-digit", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit" }) : "",
        }));
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === "AbortError") return;
        console.error("[AccountDetail] info fetch failed:", err);
      });
    return () => controller.abort();
  }, [account.login]);

  // Fetch deal history — triggered on initial load and when LOAD button clicked
  useEffect(() => {
    const controller = new AbortController();
    setFetchError(null);
    apiFetch(`/api/accounts/${account.login}/deals?from=${dateFrom}&to=${dateTo}`, { signal: controller.signal })
      .then(r => r.json())
      .then((data: DealResponse[]) => {
        if (!Array.isArray(data)) return;
        setDeals(data.map(d => parseDeal(d as unknown as RawDealResponse)));
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === "AbortError") return;
        console.error("[AccountDetail] deals fetch failed:", err);
        setFetchError("Failed to load deal history");
      });
    return () => controller.abort();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [account.login, loadTrigger]);

  const [openTrades, setOpenTrades] = useState<OpenTrade[]>([]);
  const wsRef = useRef<WebSocket | null>(null);

  // Initial position load from REST API
  useEffect(() => {
    const controller = new AbortController();
    (async () => {
      try {
        const res = await apiFetch(`/api/accounts/${account.login}/positions`, { signal: controller.signal });
        if (!res.ok) return;
        const data = await res.json();
        if (!Array.isArray(data)) return;
        setOpenTrades(data.map((p: PositionResponse) => ({
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
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === "AbortError") return;
        console.error("[AccountDetail] positions fetch failed:", err);
      }
    })();
    return () => controller.abort();
  }, [account.login]);

  // Refetch positions from REST (used after deal events for accurate state)
  const refetchPositions = async () => {
    try {
      const res = await apiFetch(`/api/accounts/${account.login}/positions`);
      if (!res.ok) return;
      const data = await res.json();
      if (!Array.isArray(data)) return;
      setOpenTrades(data.map((p: PositionResponse) => ({
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

  // WebSocket for live position P&L + deal events (close/partial close)
  useEffect(() => {
    let ws: WebSocket;
    let reconnectTimer: ReturnType<typeof setTimeout>;
    let attempt = 0;

    const connect = () => {
      const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
      const token = getAccessToken();
      const tokenParam = token ? `?token=${encodeURIComponent(token)}` : "";
      ws = new WebSocket(`${proto}//${window.location.host}/ws${tokenParam}`);
      wsRef.current = ws;

      ws.onopen = () => {
        attempt = 0;
        // Subscribe to positions (live P&L) and deals (open/close events)
        ws.send(JSON.stringify({ subscribe: ["positions", "deals"] }));
      };

      ws.onmessage = (ev) => {
        try {
          const msg = JSON.parse(ev.data) as { type: string; data: Record<string, unknown> };

          // Live P&L updates for existing/new positions
          if (msg.type === "positions") {
            const p = msg.data as { positionId: number; login: number; symbol: string; direction: number; volume: number; openPrice: number; currentPrice: number; unrealizedPnl: number; swap: number };
            if (p.login !== account.login) {
              return;
            }

            setOpenTrades(prev => {
              const idx = prev.findIndex(t => t.ticket === p.positionId);
              if (idx >= 0) {
                // Update existing — P&L, current price, volume (handles partial close)
                const updated = [...prev];
                updated[idx] = {
                  ...updated[idx]!,
                  volume: p.volume,
                  currentPrice: p.currentPrice,
                  profit: p.unrealizedPnl,
                  swap: p.swap,
                };
                return updated;
              }
              // New position — add it
              return [...prev, {
                ticket: p.positionId,
                time: new Date().toISOString(),
                symbol: p.symbol,
                action: p.direction === 0 ? "BUY" : "SELL",
                volume: p.volume,
                openPrice: p.openPrice,
                currentPrice: p.currentPrice,
                profit: p.unrealizedPnl,
                swap: p.swap,
                sl: 0,
                tp: 0,
              }];
            });
          }

          // Deal events — on open/close/partial close, refetch positions for accurate state
          if (msg.type === "deals") {
            const d = msg.data as { login: number; entry: string };
            if (d.login === account.login) {
              const entry = d.entry.toUpperCase();
              // IN = new position, OUT/OUT_BY = full/partial close
              if (entry === "IN" || entry === "OUT" || entry === "OUT_BY" || entry === "INOUT") {
                // Delay to let PnLEngine process the deal
                setTimeout(() => refetchPositions(), 500);
              }
            }
          }
        } catch { /* ignore parse errors */ }
      };

      ws.onclose = () => {
        wsRef.current = null;
        const delay = Math.min(1000 * Math.pow(2, attempt), 15000);
        attempt++;
        reconnectTimer = setTimeout(connect, delay);
      };
      ws.onerror = () => ws.close();
    };

    connect();
    return () => {
      clearTimeout(reconnectTimer);
      ws?.close();
      wsRef.current = null;
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
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
  const totalOpenPnl = openTrades.reduce((s, t) => s + t.profit, 0);
  const totalOpenSwap = openTrades.reduce((s, t) => s + t.swap, 0);
  const floatingPnl = totalOpenPnl + totalOpenSwap;

  // Live equity = balance + floating P&L (updates with every position change)
  const liveEquity = acctInfo.balance + floatingPnl;

  const infoItems = [
    { label: "Balance", val: "$" + acctInfo.balance.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }), color: C.t1 },
    { label: "Equity", val: "$" + liveEquity.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }), color: liveEquity >= acctInfo.balance ? C.green : C.red },
    { label: "Floating P&L", val: (floatingPnl >= 0 ? "+$" : "-$") + Math.abs(floatingPnl).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }), color: floatingPnl >= 0 ? C.green : C.red },
    { label: "Margin", val: "$" + acctInfo.margin.toLocaleString(), color: C.amber },
    { label: "Free Margin", val: "$" + (liveEquity - acctInfo.margin).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 }), color: C.t1 },
    { label: "Margin Level", val: acctInfo.margin > 0 ? ((liveEquity / acctInfo.margin) * 100).toFixed(0) + "%" : "0%", color: C.teal },
    { label: "Leverage", val: acctInfo.leverage, color: C.purple },
    { label: "Credit", val: "$" + acctInfo.credit.toLocaleString(), color: C.amber },
    { label: "Currency", val: acctInfo.currency, color: C.t1 },
    { label: "Registered", val: acctInfo.registration, color: C.t3 },
    { label: "Last Login", val: acctInfo.lastLogin, color: C.t3 },
  ];

  return (
    <div style={{ flex: 1, overflow: "auto", padding: 20 }}>
      <button onClick={onBack} style={{ background: "none", border: `1px solid ${C.border}`, borderRadius: 6, color: C.t2, fontSize: 12, padding: "5px 12px", cursor: "pointer", marginBottom: 16 }}>{"\u2190"} Back to grid</button>
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
          <span style={{ fontSize: 10, color: C.t3, transition: "transform 0.2s", display: "inline-block", transform: acctInfoExpanded ? "rotate(180deg)" : "rotate(0deg)" }}>{"\u25BC"}</span>
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
      {/* Stats date range */}
      <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 10 }}>
        <span style={{ fontSize: 10, color: C.t3, textTransform: "uppercase", fontWeight: 600, letterSpacing: "0.5px" }}>Statistics</span>
        <span style={{ fontSize: 10, color: C.t3 }}>From</span>
        <input type="date" value={dateFrom} onChange={e => setDateFrom(e.target.value)} style={dateInputStyle} />
        <span style={{ fontSize: 10, color: C.t3 }}>Till</span>
        <input type="date" value={dateTo} onChange={e => setDateTo(e.target.value)} style={dateInputStyle} />
        <button onClick={() => setLoadTrigger(n => n + 1)} style={{ padding: "4px 12px", borderRadius: 5, border: `1px solid ${C.teal}40`, background: C.tealBg, color: C.teal, fontSize: 10, fontWeight: 600, fontFamily: "'JetBrains Mono',monospace", cursor: "pointer" }}>LOAD</button>
        <span style={{ fontSize: 9, color: C.t3, fontStyle: "italic" }}>Applies to stats &amp; history</span>
      </div>
      {/* Stats cards — computed from loaded deals for the selected date range */}
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(150px, 1fr))", gap: 10, marginBottom: 20 }}>
        {(() => {
          // Compute stats from deals within the loaded date range
          const tradingDeals = deals.filter(d => !["BALANCE", "CREDIT", "BONUS"].includes(d.action));
          const closingDeals = tradingDeals.filter(d => d.entry === "Close" || d.entry === "Close By");
          const tradeCount = closingDeals.length;
          const totalVolume = tradingDeals.reduce((s, d) => s + d.volume, 0);
          const totalComm = tradingDeals.reduce((s, d) => s + d.commission, 0);
          const netPnl = closingDeals.reduce((s, d) => s + d.profit, 0);
          const depositOps = deals.filter(d => d.action === "BALANCE" && d.profit > 0);
          const totalDeposited = depositOps.reduce((s, d) => s + d.profit, 0);
          const bonusDeals = deals.filter(d => d.action === "BONUS" || d.action === "CREDIT");
          const totalBonuses = bonusDeals.reduce((s, d) => s + Math.abs(d.profit), 0);
          const eaDeals = tradingDeals.filter(d => d.expertId && d.expertId > 0);
          const eaRatio = tradingDeals.length > 0 ? eaDeals.length / tradingDeals.length : 0;
          const winDeals = closingDeals.filter(d => d.profit > 0);
          const winRate = closingDeals.length > 0 ? winDeals.length / closingDeals.length : 0;

          return [
            { label: "Trades", val: String(tradeCount), color: C.t1 },
            { label: "Volume", val: totalVolume.toLocaleString(undefined, { maximumFractionDigits: 2 }) + " lots", color: C.t1 },
            { label: "Commissions", val: "$" + Math.abs(totalComm).toLocaleString(undefined, { maximumFractionDigits: 2 }), color: C.amber },
            { label: "Net P&L", val: (netPnl >= 0 ? "+$" : "-$") + Math.abs(netPnl).toLocaleString(undefined, { maximumFractionDigits: 2 }), color: netPnl >= 0 ? C.green : C.red },
            { label: "Deposits", val: depositOps.length + "\u00D7 ($" + totalDeposited.toLocaleString(undefined, { maximumFractionDigits: 0 }) + ")", color: C.t1 },
            { label: "Bonuses", val: "$" + totalBonuses.toLocaleString(undefined, { maximumFractionDigits: 2 }), color: C.amber },
            { label: "EA Trade %", val: (eaRatio * 100).toFixed(0) + "%", color: eaRatio > 0.8 ? C.coral : C.t1 },
            { label: "Win Rate", val: (winRate * 100).toFixed(0) + "%", color: C.t1 },
          ];
        })().map(({ label, val, color }) => (
          <div key={label} style={{ background: C.bg3, borderRadius: 8, padding: "10px 14px", border: `1px solid ${C.border}` }}>
            <div style={{ fontSize: 10, color: C.t3, marginBottom: 4, textTransform: "uppercase", letterSpacing: "0.5px" }}>{label}</div>
            <div style={{ fontSize: 15, fontWeight: 600, color, fontFamily: "'JetBrains Mono',monospace" }}>{val}</div>
          </div>
        ))}
      </div>
      {/* v2 Threat breakdown */}
      {version === "v2" && (<div style={{ marginBottom: 20 }}>
        <div style={{ fontSize: 10, color: C.t3, marginBottom: 10, textTransform: "uppercase", letterSpacing: "0.5px", fontWeight: 600 }}>Threat Breakdown</div>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(4, 1fr)", gap: 10 }}>
          {([["Ring Trading", account.threats.ring, C.red], ["Latency Arb", account.threats.latency, C.coral], ["Bonus Abuse", account.threats.bonus, C.amber], ["Bot Farming", account.threats.bot, C.purple]] as [string, number, string][]).map(([label, val, color]) => (
            <div key={label} style={{ background: val > 0.5 ? color + "12" : C.bg3, borderRadius: 8, padding: "12px 14px", border: `1px solid ${val > 0.5 ? color + "40" : C.border}` }}>
              <div style={{ fontSize: 10, color: C.t3, marginBottom: 6 }}>{label}</div>
              <div style={{ fontSize: 22, fontWeight: 700, color: val > 0.5 ? color : C.t3, fontFamily: "'JetBrains Mono',monospace" }}>{(val * 100).toFixed(0)}%</div>
              <div style={{ height: 3, borderRadius: 2, background: "rgba(255,255,255,0.06)", marginTop: 6 }}><div style={{ width: `${val * 100}%`, height: "100%", borderRadius: 2, background: color }} /></div>
            </div>))}
        </div>
      </div>)}
      {/* Ring info */}
      {account.isRingMember && (<div style={{ background: C.redBg, border: "1px solid rgba(255,82,82,0.25)", borderRadius: 8, padding: 16, marginBottom: 20 }}>
        <div style={{ fontSize: 12, fontWeight: 600, color: C.red, marginBottom: 6 }}>Ring Detected</div>
        <div style={{ fontSize: 12, color: C.t2 }}>
          Linked accounts: {account.ringPartners.map(p => <span key={p} style={{ fontFamily: "'JetBrains Mono',monospace", color: C.t1, marginRight: 8 }}>{p}</span>)}
          <br />Shared EA (magic: {account.primaryEA}), same IB ({account.ib}), correlated trades detected.
        </div>
      </div>)}
      {/* Tabs */}
      <div style={{ display: "flex", gap: 6, marginBottom: 14 }}>
        <button onClick={() => setTab("open")} style={tabStyle("open")}>Open Trades ({openTrades.length})</button>
        <button onClick={() => setTab("history")} style={tabStyle("history")}>History ({deals.length})</button>
        <button onClick={() => setTab("deposits")} style={tabStyle("deposits")}>Deposits & Withdrawals ({moneyOps.length})</button>
        <button onClick={() => setTab("ai")} style={tabStyle("ai")}>AI Routing</button>
      </div>
      {/* Error banner */}
      {fetchError && <div style={{ padding: 10, marginBottom: 10, background: C.redBg, border: `1px solid ${C.red}40`, borderRadius: 6, color: C.red, fontSize: 11 }}>{fetchError}</div>}
      {/* Open Trades */}
      {tab === "open" && (<div style={{ overflow: "auto", maxHeight: 340 }}>
        {openTrades.length === 0 ? (<div style={{ padding: 30, textAlign: "center", color: C.t3, fontSize: 12 }}>No open positions</div>) : (
          <table style={{ width: "100%", borderCollapse: "collapse" }}>
            <thead><tr>{["Ticket","Time","Symbol","Action","Vol","Open Price","Current","Profit","Swap","S/L","T/P"].map(h => <th key={h} style={thStyle}>{h}</th>)}</tr></thead>
            <tbody>
              {openTrades.map(t => (<tr key={t.ticket}>
                <td style={tdStyle}>{t.ticket}</td>
                <td style={tdStyle}>{new Date(t.time).toLocaleString("en-GB", { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit" })}</td>
                <td style={tdStyle}>{t.symbol}</td>
                <td style={{ ...tdStyle, color: t.action === "BUY" ? C.blue : C.red }}>{t.action}</td>
                <td style={tdStyle}>{t.volume}</td><td style={tdStyle}>{t.openPrice}</td><td style={tdStyle}>{t.currentPrice}</td>
                <td style={{ ...tdStyle, color: t.profit >= 0 ? C.green : C.red, fontWeight: 600 }}>{t.profit >= 0 ? "+" : ""}{t.profit}</td>
                <td style={tdStyle}>{t.swap}</td><td style={{ ...tdStyle, color: C.red }}>{t.sl}</td><td style={{ ...tdStyle, color: C.green }}>{t.tp}</td>
              </tr>))}
              <tr><td colSpan={7} style={{ ...tdStyle, textAlign: "right", fontSize: 10, color: C.t3 }}>TOTAL P&L:</td>
                <td style={{ ...tdStyle, fontWeight: 700, color: totalOpenPnl >= 0 ? C.green : C.red }}>{totalOpenPnl >= 0 ? "+" : ""}{totalOpenPnl.toFixed(2)}</td><td colSpan={3} style={tdStyle} /></tr>
            </tbody>
          </table>)}
      </div>)}
      {/* History */}
      {tab === "history" && (() => {
        const histCols = [
          { key: "ticket", label: "Ticket" }, { key: "time", label: "Time" },
          { key: "entry", label: "Type" }, { key: "symbol", label: "Symbol" },
          { key: "action", label: "Action" }, { key: "volume", label: "Vol" },
          { key: "price", label: "Price" }, { key: "profit", label: "Profit" },
          { key: "commission", label: "Comm" }, { key: "reason", label: "Reason" },
          { key: "expertId", label: "EA" },
        ];
        return (
        <div style={{ overflow: "auto", maxHeight: 340 }}>
          <table style={{ width: "100%", borderCollapse: "collapse" }}>
            <thead><tr>{histCols.map(h => (
              <th key={h.key} onClick={() => toggleDealSort(h.key)}
                style={{ ...thStyle, cursor: "pointer" }}>
                {h.label} {dealSortCol === h.key ? (dealSortDir === 1 ? "\u2191" : "\u2193") : ""}
              </th>
            ))}</tr></thead>
            <tbody>
              {sortedDeals.slice(0, 100).map(d => {
                const entryColor = d.entry === "Open" ? C.blue : d.entry === "Close" || d.entry === "Close By" ? C.amber : d.entry === "Deposit" ? C.green : d.entry === "Withdrawal" ? C.red : d.entry === "Bonus" ? C.purple : C.t3;
                return (
                <tr key={d.ticket}>
                  <td style={tdStyle}>{d.ticket}</td>
                  <td style={tdStyle}>{new Date(d.time).toLocaleString("en-GB", { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit" })}</td>
                  <td style={{ ...tdStyle, color: entryColor, fontWeight: 600, fontSize: 10 }}>{d.entry || "\u2014"}</td>
                  <td style={tdStyle}>{d.symbol}</td>
                  <td style={{ ...tdStyle, color: d.action === "BUY" ? C.blue : d.action === "SELL" ? C.red : C.t2 }}>{d.action}</td>
                  <td style={tdStyle}>{d.volume}</td><td style={tdStyle}>{d.price}</td>
                  <td style={{ ...tdStyle, color: d.profit >= 0 ? C.green : C.red }}>{d.profit >= 0 ? "+" : ""}{d.profit}</td>
                  <td style={tdStyle}>${d.commission}</td><td style={tdStyle}>{d.reason}</td>
                  <td style={tdStyle}>{d.expertId || "\u2014"}</td>
                </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        );
      })()}
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
                    <td style={tdStyle}><span style={{ color: C.green }}>{"\u2713"}</span> {op.status}</td>
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

export default AccountDetail;
