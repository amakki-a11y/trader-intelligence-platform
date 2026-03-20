import { useState, useEffect, useRef, useCallback } from "react";
import type { CSSProperties } from "react";
import C from "../styles/colors";
import type { ConnectionStatus, ConnectionLogEntry } from "../store/TipStore";
import { formatUptime } from "../store/TipStore";
import { useAuth } from "../contexts/AuthContext";
import { apiFetch } from "../services/api";
import UserManagement from "./admin/UserManagement";
import ServerManagement from "./admin/ServerManagement";
import RolesView from "./admin/RolesView";
import ChangePasswordPage from "./ChangePasswordPage";

interface SettingsViewProps {
  connectionStatus: ConnectionStatus;
}

type SettingsTab = "servers" | "users" | "roles" | "password";

function SettingsView({ connectionStatus }: SettingsViewProps) {
  const { hasPermission } = useAuth();
  const isAdmin = hasPermission("admin.users");
  const canManageServers = hasPermission("admin.servers");

  const [tab, setTab] = useState<SettingsTab>("servers");
  const [logs, setLogs] = useState<ConnectionLogEntry[]>([]);
  const logRef = useRef<HTMLDivElement>(null);

  // Load initial connection logs
  useEffect(() => {
    const controller = new AbortController();
    (async () => {
      try {
        const res = await apiFetch("/api/settings/connection/logs", { signal: controller.signal });
        if (res.ok) setLogs(await res.json() as ConnectionLogEntry[]);
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
        const res = await apiFetch("/api/settings/connection/logs", { signal: controller.signal });
        if (res.ok) setLogs(await res.json() as ConnectionLogEntry[]);
      } catch (err: unknown) {
        if (err instanceof DOMException && err.name === "AbortError") return;
      }
    }, 3000);
    return () => { clearInterval(id); controller.abort(); };
  }, []);

  const handleDisconnect = useCallback(async () => {
    try { await apiFetch("/api/settings/connection/disconnect", { method: "POST" }); }
    catch (err: unknown) { console.error("[SettingsView] disconnect failed:", err); }
  }, []);

  const cardS: CSSProperties = { background: C.bg3, borderRadius: 10, border: `1px solid ${C.border}`, padding: 16 };
  const btnDanger: CSSProperties = {
    padding: "8px 20px", fontSize: 12, fontWeight: 600, borderRadius: 6, border: "none", cursor: "pointer",
    background: C.red, color: C.bg, fontFamily: "'JetBrains Mono',monospace",
  };

  // Build tabs list based on permissions
  const tabs: { id: SettingsTab; label: string }[] = [];
  tabs.push({ id: "servers", label: "MT5 Servers" });
  if (isAdmin) tabs.push({ id: "users", label: "Users" });
  if (isAdmin) tabs.push({ id: "roles", label: "Roles" });
  tabs.push({ id: "password", label: "Change Password" });

  const tabBtnS = (active: boolean): CSSProperties => ({
    padding: "8px 16px", fontSize: 12, fontWeight: active ? 600 : 400, borderRadius: 6,
    border: active ? `1px solid ${C.teal}40` : `1px solid transparent`,
    background: active ? `${C.teal}12` : "transparent",
    color: active ? C.teal : C.t3, cursor: "pointer",
    fontFamily: "'DM Sans',system-ui,sans-serif", transition: "all 0.15s",
  });

  return (
    <div style={{ flex: 1, overflow: "auto", display: "flex", flexDirection: "column" }}>
      {/* Connection Status Banner */}
      <div style={{ padding: "12px 20px", borderBottom: `1px solid ${C.border}`, display: "flex", alignItems: "center", gap: 16 }}>
        <div style={{
          width: 10, height: 10, borderRadius: "50%",
          background: connectionStatus.connected ? C.teal : C.red,
          boxShadow: connectionStatus.connected ? `0 0 10px ${C.teal}60` : `0 0 10px ${C.red}60`,
        }} />
        <div style={{ flex: 1 }}>
          <span style={{ fontSize: 12, fontWeight: 600, color: C.t1 }}>
            {connectionStatus.connected ? "Connected" : "Disconnected"}
          </span>
          {connectionStatus.connected && (
            <span style={{ fontSize: 11, color: C.t3, fontFamily: "'JetBrains Mono',monospace", marginLeft: 10 }}>
              {connectionStatus.server} {"\u00B7"} login {connectionStatus.login} {"\u00B7"} {connectionStatus.accountsInScope} accounts {"\u00B7"} uptime {formatUptime(connectionStatus.uptimeSeconds)}
            </span>
          )}
        </div>
        {connectionStatus.connected && (
          <button onClick={handleDisconnect} style={{ ...btnDanger, padding: "5px 14px", fontSize: 11 }}>DISCONNECT</button>
        )}
      </div>

      {/* Tab Bar */}
      <div style={{ padding: "10px 20px 0", display: "flex", gap: 6, borderBottom: `1px solid ${C.border}`, paddingBottom: 10 }}>
        {tabs.map(t => (
          <button key={t.id} onClick={() => setTab(t.id)} style={tabBtnS(tab === t.id)}>{t.label}</button>
        ))}
      </div>

      {/* Tab Content */}
      <div style={{ flex: 1, overflow: "auto" }}>
        {tab === "servers" && (
          <div style={{ display: "flex", flexDirection: "column" }}>
            {/* MT5 Servers Table (admin can add/remove) */}
            {canManageServers && <ServerManagement />}

            {/* Connection Log */}
            <div style={{ padding: "0 20px 20px" }}>
              <div style={{ ...cardS, display: "flex", flexDirection: "column", minHeight: 200 }}>
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
        )}

        {tab === "users" && isAdmin && <UserManagement />}
        {tab === "roles" && isAdmin && <RolesView />}
        {tab === "password" && (
          <div style={{ maxWidth: 420, margin: "0 auto", paddingTop: 20 }}>
            <ChangePasswordPage embedded />
          </div>
        )}
      </div>
    </div>
  );
}

export default SettingsView;
