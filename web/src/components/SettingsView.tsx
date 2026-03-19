import { useState, useEffect, useRef, useCallback } from "react";
import type { CSSProperties } from "react";
import C from "../styles/colors";
import type { ConnectionStatus, ConnectionLogEntry, ScanSettings } from "../store/TipStore";
import { formatUptime } from "../store/TipStore";

interface SettingsViewProps {
  connectionStatus: ConnectionStatus;
}

function SettingsView({ connectionStatus }: SettingsViewProps) {
  const [server, setServer] = useState("");
  const [login, setLogin] = useState("");
  const passwordRef = useRef<HTMLInputElement>(null);
  const [groupMask, setGroupMask] = useState("*");
  const [showPassword, setShowPassword] = useState(false);
  const [connecting, setConnecting] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [logs, setLogs] = useState<ConnectionLogEntry[]>([]);
  const [scan, setScan] = useState<ScanSettings>({ historyDays: 90, minDeposit: 0, pollIntervalMs: 5000, criticalThreshold: 70 });
  const [scanSaved, setScanSaved] = useState(false);
  const logRef = useRef<HTMLDivElement>(null);

  /** Read password from uncontrolled input ref — never stored in React state */
  const getPassword = () => passwordRef.current?.value ?? "";
  const clearPassword = () => { if (passwordRef.current) passwordRef.current.value = ""; };
  const hasPassword = () => (passwordRef.current?.value ?? "").length > 0;

  // FIX 3+4: Load initial config with error logging and AbortController
  useEffect(() => {
    const controller = new AbortController();
    (async () => {
      try {
        const [cfgRes, scanRes, logRes] = await Promise.all([
          fetch("/api/settings/connection", { signal: controller.signal }),
          fetch("/api/settings/scan", { signal: controller.signal }),
          fetch("/api/settings/connection/logs", { signal: controller.signal }),
        ]);
        if (cfgRes.ok) {
          const cfg = await cfgRes.json() as { server: string; login: string; groupMask: string };
          if (cfg.server && cfg.server !== "simulator:0") { setServer(cfg.server); setLogin(cfg.login); setGroupMask(cfg.groupMask); }
        }
        if (scanRes.ok) setScan(await scanRes.json() as ScanSettings);
        if (logRes.ok) setLogs(await logRes.json() as ConnectionLogEntry[]);
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === "AbortError") return;
        console.error("[SettingsView] init fetch failed:", err);
      }
    })();
    return () => controller.abort();
  }, []);

  // FIX 5: Poll logs with cleanup
  useEffect(() => {
    const controller = new AbortController();
    const id = setInterval(async () => {
      try {
        const res = await fetch("/api/settings/connection/logs", { signal: controller.signal });
        if (res.ok) setLogs(await res.json() as ConnectionLogEntry[]);
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === "AbortError") return;
        console.error("[SettingsView] log poll failed:", err);
      }
    }, 3000);
    return () => { clearInterval(id); controller.abort(); };
  }, []);

  const handleConnect = useCallback(async () => {
    setConnecting(true);
    try {
      const pw = getPassword();
      const res = await fetch("/api/settings/connection", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ server, login, password: pw || undefined, groupMask }),
      });
      if (res.ok) { clearPassword(); }
      else {
        const err = await res.json().catch(() => null) as { error?: string } | null;
        if (err?.error) alert(err.error);
      }
    } catch (err: unknown) {
      console.error("[SettingsView] connect failed:", err);
    }
    finally { setConnecting(false); }
  }, [server, login, groupMask]);

  const handleDisconnect = useCallback(async () => {
    try { await fetch("/api/settings/connection/disconnect", { method: "POST" }); }
    catch (err: unknown) { console.error("[SettingsView] disconnect failed:", err); }
  }, []);

  const handleSave = useCallback(async () => {
    setSaving(true);
    try {
      const pw = getPassword();
      const res = await fetch("/api/settings/connection/save", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ server, login, password: pw, groupMask }),
      });
      if (res.ok) { setSaved(true); clearPassword(); setTimeout(() => setSaved(false), 2000); }
      else {
        const err = await res.json().catch(() => null) as { error?: string } | null;
        if (err?.error) alert(err.error);
      }
    } catch (err: unknown) {
      console.error("[SettingsView] save failed:", err);
    }
    finally { setSaving(false); }
  }, [server, login, groupMask]);

  const handleScanSave = useCallback(async () => {
    try {
      const res = await fetch("/api/settings/scan", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(scan),
      });
      if (res.ok) { setScanSaved(true); setTimeout(() => setScanSaved(false), 2000); }
    } catch (err: unknown) {
      console.error("[SettingsView] scan save failed:", err);
    }
  }, [scan]);

  const labelS: CSSProperties = { fontSize: 9, fontWeight: 600, color: C.t3, textTransform: "uppercase", letterSpacing: "0.5px", marginBottom: 4 };
  const inputS: CSSProperties = {
    width: "100%", padding: "8px 10px", fontSize: 12, fontFamily: "'JetBrains Mono',Consolas,monospace",
    background: C.bg, border: `1px solid ${C.border}`, borderRadius: 6, color: C.t1, outline: "none",
  };
  const cardS: CSSProperties = { background: C.bg3, borderRadius: 10, border: `1px solid ${C.border}`, padding: 16 };
  const btnPrimary: CSSProperties = {
    padding: "8px 20px", fontSize: 12, fontWeight: 600, borderRadius: 6, border: "none", cursor: "pointer",
    background: C.teal, color: C.bg, fontFamily: "'JetBrains Mono',monospace",
  };
  const btnDanger: CSSProperties = { ...btnPrimary, background: C.red };
  const numInputS: CSSProperties = { ...inputS, width: 120 };

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
              {connectionStatus.server} {"\u00B7"} login {connectionStatus.login} {"\u00B7"} {connectionStatus.accountsInScope} accounts {"\u00B7"} uptime {formatUptime(connectionStatus.uptimeSeconds)}
            </div>
          )}
          {connectionStatus.error && !connectionStatus.connected && (
            <div style={{ fontSize: 11, color: C.red, marginTop: 2 }}>{connectionStatus.error}</div>
          )}
        </div>
        {connectionStatus.connected && (
          <button onClick={handleDisconnect} style={btnDanger}>DISCONNECT</button>
        )}
      </div>

      <div style={{ display: "flex", gap: 16, flex: 1, minHeight: 0 }}>
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
                    ref={passwordRef}
                    type={showPassword ? "text" : "password"}
                    placeholder={"••••••••"} style={inputS}
                  />
                  <button onClick={() => setShowPassword(v => !v)} style={{
                    position: "absolute", right: 8, top: "50%", transform: "translateY(-50%)",
                    background: "none", border: "none", cursor: "pointer", color: C.t3, fontSize: 14,
                  }}>{showPassword ? "\u{1F648}" : "\u{1F441}"}</button>
                </div>
              </div>
              <div>
                <div style={labelS}>Group Mask</div>
                <input value={groupMask} onChange={e => setGroupMask(e.target.value)} placeholder="forex\retail*" style={inputS} />
                <div style={{ fontSize: 10, color: C.t3, marginTop: 4 }}>
                  Wildcard syntax: * matches any, \ separates groups (e.g. forex\retail\*)
                </div>
              </div>
              <div style={{ display: "flex", gap: 10, marginTop: 4 }}>
                <button onClick={handleSave} disabled={saving || !server || !login}
                  style={{ ...btnPrimary, background: C.blue, opacity: saving || !server || !login ? 0.5 : 1, flex: 1 }}>
                  {saving ? "SAVING..." : saved ? "SAVED" : "SAVE CREDENTIALS"}
                </button>
                <button onClick={handleConnect} disabled={connecting || !server || !login}
                  style={{ ...btnPrimary, opacity: connecting || !server || !login ? 0.5 : 1, flex: 1 }}>
                  {connecting ? "CONNECTING..." : hasPassword() ? "CONNECT" : "RECONNECT"}
                </button>
              </div>
            </div>
          </div>

          {/* Scan Settings */}
          <div style={cardS}>
            <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginBottom: 14 }}>
              <div style={{ fontSize: 12, fontWeight: 600, color: C.t1 }}>Scan Settings</div>
              <button onClick={handleScanSave} style={{ ...btnPrimary, padding: "5px 14px", fontSize: 11 }}>
                {scanSaved ? "\u2713 SAVED" : "SAVE"}
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
              const icon = entry.level === "success" ? "\u2713" : entry.level === "error" ? "\u2717" : "\u2139";
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

export default SettingsView;
