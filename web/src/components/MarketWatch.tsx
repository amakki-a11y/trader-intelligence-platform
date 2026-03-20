import { Fragment, useState, useEffect, useMemo, useRef, useCallback } from "react";
import C, { DEFAULT_WATCHLIST_BASES, resolveWatchlist } from "../styles/colors";
import type { MarketDataPoint, VolumeData } from "../store/TipStore";
import { getAccessToken, apiFetch } from "../services/api";

function MarketWatch({ isLive: _isLive }: { isLive: boolean }) {
  void _isLive;
  const [watchlist, setWatchlist] = useState<string[]>([]);
  const [allSymbols, setAllSymbols] = useState<Array<{ symbol: string; description: string }>>([]);
  const [marketData, setMarketData] = useState<Record<string, MarketDataPoint>>({});
  const [addInput, setAddInput] = useState("");
  const [showAdd, setShowAdd] = useState(false);
  const [sortCol, setSortCol] = useState<string | null>(null);
  const [sortDir, setSortDir] = useState(-1);
  const [advanced, setAdvanced] = useState(false);
  const [wsStatus, setWsStatus] = useState<"disconnected" | "connecting" | "connected" | "reconnecting">("disconnected");
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

  // Load symbols + prices together, then resolve watchlist using live price data
  useEffect(() => {
    const controller = new AbortController();
    (async () => {
      try {
        const [symsRes, pricesRes] = await Promise.all([
          apiFetch("/api/market/symbols", { signal: controller.signal }),
          apiFetch("/api/market/prices", { signal: controller.signal }),
        ]);
        const syms: Array<{ symbol: string; description: string }> = symsRes.ok ? await symsRes.json() : [];
        const pricesArr: Array<{ symbol: string; bid: number; ask: number; spread: number; changePercent: number; timeMsc: number; digits: number }> = pricesRes.ok ? await pricesRes.json() : [];

        setAllSymbols(syms);

        // Build price lookup for resolver
        const priceLookup: Record<string, { bid: number }> = {};
        const newMarketData: Record<string, MarketDataPoint> = {};
        for (const p of pricesArr) {
          priceLookup[p.symbol] = { bid: p.bid };
          if (p.bid > 0) {
            newMarketData[p.symbol] = { symbol: p.symbol, bid: p.bid, ask: p.ask, spread: p.spread, change24h: p.changePercent, digits: p.digits ?? 5 };
          }
        }
        setMarketData(prev => ({ ...prev, ...newMarketData }));

        // Resolve watchlist using live prices to pick symbols with actual feeds
        if (syms.length > 0) {
          const resolved = resolveWatchlist(DEFAULT_WATCHLIST_BASES, syms, priceLookup);
          setWatchlist(resolved);
        }
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === "AbortError") return;
        console.error("[MarketWatch] init fetch failed:", err);
      }
    })();
    return () => controller.abort();
  }, []);

  const pendingPricesRef = useRef<Record<string, MarketDataPoint>>({});
  const rafIdRef = useRef<number>(0);

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

  // Volume periodic fetch
  useEffect(() => {
    const controller = new AbortController();
    const fetchVolume = async () => {
      try {
        const res = await apiFetch("/api/market/volume", { signal: controller.signal });
        if (!res.ok) return;
        const data = await res.json() as Array<{ symbol: string; buyVolume: number; sellVolume: number; netVolume: number; topBuyer: { login: number; volume: number }; topSeller: { login: number; volume: number } }>;
        const vols: Record<string, VolumeData> = {};
        for (const v of data) {
          vols[v.symbol] = { buy: v.buyVolume, sell: v.sellVolume, net: v.netVolume, topBuyer: v.topBuyer, topSeller: v.topSeller };
        }
        setVolumeBySymbol(vols);
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === "AbortError") return;
        console.error("[MarketWatch] volume fetch failed:", err);
      }
    };
    fetchVolume();
    const volumeInterval = setInterval(fetchVolume, 5000);
    return () => { clearInterval(volumeInterval); controller.abort(); };
  }, []);

  // Session high/low from MT5 TickStat API (real session data, not backend-calculated)
  useEffect(() => {
    if (watchlist.length === 0) return;
    const controller = new AbortController();
    const fetchSessionHL = async () => {
      try {
        const syms = watchlist.join(",");
        const res = await apiFetch(`/api/market/session-stats?symbols=${encodeURIComponent(syms)}`, { signal: controller.signal });
        if (!res.ok) return;
        const stats = await res.json() as Array<{ symbol: string; high: number; low: number; bid: number; ask: number }>;
        const hl: Record<string, { high: number; low: number }> = {};
        for (const s of stats) {
          if (s.high > 0) hl[s.symbol] = { high: s.high, low: s.low };
        }
        setSessionHighLow(hl);
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === "AbortError") return;
        console.error("[MarketWatch] session stats fetch failed:", err);
      }
    };
    fetchSessionHL();
    const hlInterval = setInterval(fetchSessionHL, 5000);
    return () => { clearInterval(hlInterval); controller.abort(); };
  }, [watchlist]);

  // FIX 2+5: WebSocket with exponential backoff and full cleanup
  useEffect(() => {
    let reconnectTimer: ReturnType<typeof setTimeout>;
    let staleCheckTimer: ReturnType<typeof setInterval>;
    let ws: WebSocket;
    let lastMessageAt = Date.now();
    let attempt = 0;

    const connect = () => {
      setWsStatus("connecting");
      const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
      const token = getAccessToken();
      const tokenParam = token ? `?token=${encodeURIComponent(token)}` : "";
      ws = new WebSocket(`${proto}//${window.location.host}/ws${tokenParam}`);
      wsRef.current = ws;
      lastMessageAt = Date.now();

      ws.onopen = () => {
        setWsStatus("connected");
        attempt = 0; // FIX 2: reset on success
        ws.send(JSON.stringify({ subscribe: ["prices"] }));
      };

      ws.onmessage = (ev) => {
        lastMessageAt = Date.now();
        try {
          const msg = JSON.parse(ev.data) as { type: string; data: { symbol: string; bid: number; ask: number; spread: number; changePercent: number; timeMsc: number; digits: number } };
          if (msg.type === "prices" && msg.data) {
            const p = msg.data;
            if (p.bid > 0) {
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
        // FIX 2: exponential backoff
        const delay = Math.min(1000 * Math.pow(2, attempt), 30000);
        attempt++;
        console.log(`[MarketWatch WS] reconnect attempt ${attempt} in ${delay}ms`);
        reconnectTimer = setTimeout(connect, delay);
      };
      ws.onerror = () => { ws.close(); };
    };

    staleCheckTimer = setInterval(() => {
      if (wsRef.current && wsRef.current.readyState === WebSocket.OPEN && Date.now() - lastMessageAt > 30000) {
        console.warn("[WS] Stale connection detected (no message for 30s) \u2014 forcing reconnect");
        setWsStatus("reconnecting");
        wsRef.current.close();
      }
    }, 5000);

    connect();
    return () => {
      clearTimeout(reconnectTimer);
      clearInterval(staleCheckTimer);
      ws?.close();
      wsRef.current = null;
      setWsStatus("disconnected");
      if (rafIdRef.current) { cancelAnimationFrame(rafIdRef.current); rafIdRef.current = 0; }
    };
  }, [flushPrices]);

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
        }}><span style={{ fontSize: 11 }}>{advanced ? "\u2611" : "\u2610"}</span> ADVANCED</button>
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
              }}>{h.l} {sortCol === h.k ? (sortDir === 1 ? "\u2191" : "\u2193") : ""}</th>
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
                    <td style={{ padding: "10px 10px", fontSize: 13, fontFamily: "'JetBrains Mono',monospace", color: bid > 0 ? C.red : C.t3, borderBottom: bdr, opacity: bid > 0 ? 1 : 0.4 }}>{ask > 0 ? formatPrice(ask, digits) : "\u2014"}</td>
                    <td style={{ padding: "10px 10px", fontSize: 11, fontFamily: "'JetBrains Mono',monospace", color: C.t3, borderBottom: bdr }}>{spread ? formatPrice(spread, digits) : "\u2014"}</td>
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
                    <td style={{ padding: "10px 10px", borderBottom: bdr }}><span style={{ fontSize: 11, fontFamily: "'JetBrains Mono',monospace", color: changeColor }}>{change >= 0 ? "\u25B2" : "\u25BC"} {Math.abs(change).toFixed(2)}%</span></td>
                    <td style={{ padding: "10px 6px", borderBottom: bdr, textAlign: "center" }}>
                      <button onClick={() => handleRemove(sym)} style={{ background: "none", border: "none", color: C.t3, fontSize: 14, cursor: "pointer", opacity: 0.5, lineHeight: 1 }} title="Remove symbol"
                        onMouseEnter={e => { e.currentTarget.style.opacity = "1"; }} onMouseLeave={e => { e.currentTarget.style.opacity = "0.5"; }}>{"\u00D7"}</button>
                    </td>
                  </tr>
                  {advanced && (
                    <tr style={{ background: "rgba(155,138,255,0.03)" }}>
                      <td style={{ padding: "2px 10px 8px", borderBottom: `1px solid ${C.border}`, fontSize: 9, color: C.t3, fontFamily: "'JetBrains Mono',monospace" }} />
                      <td style={{ padding: "2px 10px 8px", borderBottom: `1px solid ${C.border}` }}><span style={{ fontSize: 9, color: C.t3, letterSpacing: "0.5px" }}>LOW </span><span style={{ fontSize: 10, fontFamily: "'JetBrains Mono',monospace", color: C.amber }}>{adv.low > 0 ? formatPrice(adv.low, digits) : "\u2014"}</span></td>
                      <td style={{ padding: "2px 10px 8px", borderBottom: `1px solid ${C.border}` }}><span style={{ fontSize: 9, color: C.t3, letterSpacing: "0.5px" }}>HIGH </span><span style={{ fontSize: 10, fontFamily: "'JetBrains Mono',monospace", color: C.teal }}>{adv.high > 0 ? formatPrice(adv.high, digits) : "\u2014"}</span></td>
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
          {wsStatus === "connected" ? "\u{1F7E2} LIVE" : wsStatus === "reconnecting" ? "\u{1F7E0} RECONNECTING" : wsStatus === "connecting" ? "\u{1F7E1} CONNECTING" : "\u26AB OFFLINE"} \u2014 MT5 PRICES
        </span>
        <span style={{ fontSize: 11, fontFamily: "'JetBrains Mono',monospace", color: C.teal }}>{priceCount}/{watchlist.length} symbols with data</span>
      </div>
    </div>
  );
}

export default MarketWatch;
