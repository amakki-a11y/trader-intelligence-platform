import { useState, useEffect, useMemo, useRef } from "react";
import type { CSSProperties } from "react";
import C, { sevColor } from "../styles/colors";
import type { Account, LiveEvent } from "../store/TipStore";
import { parseDealToLiveEvent, parseWsDealToLiveEvent } from "../utils/parsers";
import type { RawDealResponse } from "../utils/parsers";

/** Escape special regex characters in user input for safe use in RegExp/filter. */
function escapeRegex(str: string): string {
  return str.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
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

interface LiveMonitorProps {
  accounts: Account[];
  isLive: boolean;
  onSelect: (a: Account) => void;
}

function LiveMonitor({ accounts, isLive, onSelect }: LiveMonitorProps) {
  const [events, setEvents] = useState<LiveEvent[]>([]);
  const [wsStatus, setWsStatus] = useState<"connecting" | "connected" | "disconnected">("disconnected");
  const [sortCol, setSortCol] = useState<string>("id");
  const [sortDir, setSortDir] = useState(-1);
  const [filter, setFilter] = useState("");
  const logRef = useRef<HTMLDivElement>(null);
  const wsRef = useRef<WebSocket | null>(null);

  const toggleSort = (col: string) => {
    if (sortCol === col) setSortDir(d => d * -1);
    else { setSortCol(col); setSortDir(-1); }
  };

  const sortedEvents = useMemo(() => {
    let f = events;
    if (filter) {
      const escaped = escapeRegex(filter);
      const re = new RegExp(escaped, "i");
      f = f.filter(ev =>
        re.test(ev.login.toString()) || re.test(ev.symbol) ||
        re.test(ev.entry) || re.test(ev.action)
      );
    }
    return [...f].sort((a, b) => {
      const av = (a as unknown as Record<string, unknown>)[sortCol];
      const bv = (b as unknown as Record<string, unknown>)[sortCol];
      if (typeof av === "number" && typeof bv === "number") return (av - bv) * sortDir;
      return String(av).localeCompare(String(bv)) * sortDir;
    });
  }, [events, sortCol, sortDir, filter]);

  // FIX 2: exponential backoff, FIX 3: error logging, FIX 4: AbortController, FIX 5: timer cleanup
  useEffect(() => {
    if (!isLive) {
      if (wsRef.current) { wsRef.current.close(); wsRef.current = null; }
      setWsStatus("disconnected");
      return;
    }

    let reconnectTimer: ReturnType<typeof setTimeout>;
    let ws: WebSocket;
    let attempt = 0;
    const seenIds = new Set<number>();
    const controller = new AbortController();

    // Load recent deal history for scored accounts
    const loadRecent = async () => {
      try {
        const from = new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString();
        const acctRes = await fetch("/api/accounts", { signal: controller.signal });
        if (!acctRes.ok) return;
        const acctList = await acctRes.json() as Array<Record<string, unknown>>;
        if (!Array.isArray(acctList)) return;
        const allDeals: LiveEvent[] = [];
        for (const acc of acctList) {
          try {
            const res = await fetch(`/api/accounts/${acc.login}/deals?from=${from}`, { signal: controller.signal });
            if (!res.ok) continue;
            const data = await res.json() as RawDealResponse[];
            if (!Array.isArray(data)) continue;
            for (const d of data) {
              if (!d.dealId || seenIds.has(d.dealId)) continue;
              seenIds.add(d.dealId);
              allDeals.push(parseDealToLiveEvent(
                d,
                (acc.name as string) ?? (d.login)?.toString() ?? "",
                (acc.abuseScore as number) ?? 0,
                (acc.riskLevel as string) ?? "Low",
              ));
            }
          } catch (err: unknown) {
            if (err instanceof DOMException && err.name === "AbortError") return;
            console.error("[LiveMonitor] failed to load deals for account:", acc.login, err);
          }
        }
        allDeals.sort((a, b) => b.id - a.id);
        setEvents(allDeals.slice(0, 200));
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === "AbortError") return;
        console.error("[LiveMonitor] loadRecent failed:", err);
      }
    };
    loadRecent();

    // WebSocket for new deals in real-time with exponential backoff
    const connect = () => {
      setWsStatus("connecting");
      const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
      ws = new WebSocket(`${proto}//${window.location.host}/ws`);
      wsRef.current = ws;

      ws.onopen = () => {
        setWsStatus("connected");
        attempt = 0; // FIX 2: reset on success
        ws.send(JSON.stringify({ subscribe: ["deals"] }));
      };

      ws.onmessage = (evt) => {
        try {
          const msg = JSON.parse(evt.data) as { type: string; data: Record<string, unknown> };
          if (msg.type !== "deals" || !msg.data) return;
          const d = msg.data;
          const did = Number(d.dealId);
          if (!did || seenIds.has(did)) return;
          seenIds.add(did);
          const event = parseWsDealToLiveEvent(d);
          if (!event) return;
          setEvents(prev => {
            if (prev.some(e => e.id === did)) return prev;
            return [event, ...prev].slice(0, 200);
          });
        } catch { /* ignore parse errors */ }
      };

      ws.onclose = () => {
        setWsStatus("disconnected");
        wsRef.current = null;
        // FIX 2: exponential backoff
        const delay = Math.min(1000 * Math.pow(2, attempt), 30000);
        attempt++;
        console.log(`[LiveMonitor WS] reconnect attempt ${attempt} in ${delay}ms`);
        reconnectTimer = setTimeout(connect, delay);
      };

      ws.onerror = () => { ws.close(); };
    };

    connect();
    return () => {
      clearTimeout(reconnectTimer);
      controller.abort();
      if (wsRef.current) { wsRef.current.close(); wsRef.current = null; }
    };
  }, [isLive]);

  const lmCols = [
    { key: "time", label: "Time", w: 65 },
    { key: "login", label: "Login", w: 70 },
    { key: "entry", label: "Type", w: 65 },
    { key: "action", label: "Action", w: 60 },
    { key: "symbol", label: "Symbol", w: 75 },
    { key: "volume", label: "Vol / Amount", w: 90 },
    { key: "price", label: "Price", w: 80 },
    { key: "profit", label: "Profit", w: 75 },
    { key: "score", label: "Score", w: 80 },
  ];
  const lmTh: CSSProperties = {
    padding: "8px 6px", textAlign: "left", fontSize: 9,
    fontFamily: "'JetBrains Mono',monospace", fontWeight: 600, color: C.t3,
    borderBottom: `1px solid ${C.border}`, position: "sticky", top: 0, background: C.bg2, zIndex: 2,
    letterSpacing: "0.5px", textTransform: "uppercase", cursor: "pointer",
  };
  const lmTd: CSSProperties = {
    padding: "6px 6px", fontSize: 11, color: C.t2, borderBottom: `1px solid ${C.border}`,
    fontFamily: "'JetBrains Mono',monospace",
  };

  return (
    <div style={{ flex: 1, display: "flex", flexDirection: "column", overflow: "hidden" }}>
      <div style={{ padding: "10px 16px", borderBottom: `1px solid ${C.border}`, display: "flex", alignItems: "center", gap: 12 }}>
        <span style={{ width: 8, height: 8, borderRadius: "50%", background: wsStatus === "connected" ? C.teal : wsStatus === "connecting" ? C.amber : C.t3, animation: wsStatus === "connected" ? "pulse 1.5s infinite" : "none" }} />
        <span style={{ fontSize: 12, color: wsStatus === "connected" ? C.teal : wsStatus === "connecting" ? C.amber : C.t3, fontFamily: "'JetBrains Mono',monospace" }}>
          {wsStatus === "connected" ? "LIVE \u2014 WebSocket connected" : wsStatus === "connecting" ? "Connecting..." : "STOPPED"}
        </span>
        <div style={{ flex: 1 }} />
        <input value={filter} onChange={e => setFilter(e.target.value)} placeholder="Filter by login, symbol, type..."
          style={{ width: 220, background: C.bg3, border: `1px solid ${C.border}`, borderRadius: 6, padding: "5px 10px", color: C.t1, fontSize: 11, fontFamily: "'JetBrains Mono',monospace", outline: "none" }} />
        <span style={{ fontSize: 11, color: C.t3 }}>{sortedEvents.length} events</span>
      </div>
      <div ref={logRef} style={{ flex: 1, overflow: "auto" }}>
        {events.length === 0 ? (
          <div style={{ textAlign: "center", padding: 60, color: C.t3, fontSize: 13 }}>
            {isLive ? "Waiting for deals..." : "Click GO LIVE to start monitoring"}
          </div>
        ) : (
          <table style={{ width: "100%", borderCollapse: "collapse", tableLayout: "fixed" }}>
            <thead><tr>
              {lmCols.map(col => (
                <th key={col.key} onClick={() => toggleSort(col.key)}
                  style={{ ...lmTh, width: col.w }}>
                  {col.label} {sortCol === col.key ? (sortDir === 1 ? "\u2191" : "\u2193") : ""}
                </th>
              ))}
            </tr></thead>
            <tbody>
              {sortedEvents.map(ev => {
                const entryColor = ev.entry === "Open" ? C.blue : ev.entry === "Close" || ev.entry === "Close By" ? C.amber : ev.entry === "Deposit" ? C.green : ev.entry === "Withdrawal" ? C.red : ev.entry === "Bonus" ? C.purple : C.t3;
                const isMoney = ev.entry === "Deposit" || ev.entry === "Withdrawal" || ev.entry === "Bonus";
                return (
                  <tr key={ev.id} onClick={() => { const a = accounts.find(x => x.login === ev.login); if (a) onSelect(a); }}
                    style={{
                      cursor: "pointer",
                      background: ev.isCorrelated ? "rgba(255,82,82,0.06)" : "transparent",
                      transition: "background 0.15s",
                    }}
                    onMouseEnter={e => { e.currentTarget.style.background = "rgba(255,255,255,0.03)"; }}
                    onMouseLeave={e => { e.currentTarget.style.background = ev.isCorrelated ? "rgba(255,82,82,0.06)" : "transparent"; }}>
                    <td style={{ ...lmTd, fontSize: 10, color: C.t3 }}>{ev.time}</td>
                    <td style={{ ...lmTd, color: C.t1 }}>{ev.login}</td>
                    <td style={{ ...lmTd, color: entryColor, fontWeight: 600, fontSize: 10 }}>{ev.entry || "\u2014"}</td>
                    <td style={{ ...lmTd, color: ev.action === "BUY" ? C.blue : ev.action === "SELL" ? C.red : C.t2 }}>{ev.action}</td>
                    <td style={lmTd}>{ev.symbol}</td>
                    <td style={{ ...lmTd, fontWeight: isMoney ? 600 : 400, color: isMoney ? (ev.profit >= 0 ? C.green : C.red) : C.t2 }}>
                      {isMoney ? `${ev.profit >= 0 ? "+" : ""}${ev.profit.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}` : ev.volume}
                    </td>
                    <td style={{ ...lmTd, color: C.t3 }}>{!isMoney && ev.price ? ev.price : ""}</td>
                    <td style={{ ...lmTd, color: !isMoney ? (ev.profit >= 0 ? C.green : C.red) : C.t3 }}>
                      {!isMoney && ev.profit ? `${ev.profit >= 0 ? "+" : ""}${ev.profit.toFixed(2)}` : ""}
                    </td>
                    <td style={lmTd}>
                      <div style={{ display: "flex", alignItems: "center", gap: 4 }}>
                        {ev.scoreChange !== 0 && (
                          <span style={{ fontSize: 9, color: ev.scoreChange > 0 ? C.red : C.green }}>
                            {ev.scoreChange > 0 ? "\u25B2" : "\u25BC"}{Math.abs(ev.scoreChange).toFixed(1)}
                          </span>
                        )}
                        <ScoreBar score={Math.min(100, ev.score + ev.scoreChange)} width={45} />
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

export default LiveMonitor;
