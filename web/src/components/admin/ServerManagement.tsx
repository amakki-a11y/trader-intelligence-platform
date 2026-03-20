import { useState, useEffect, useCallback, type CSSProperties } from "react";
import C from "../../styles/colors";
import { apiFetch } from "../../services/api";

interface ServerDto {
  id: number; name: string; address: string; managerLogin: number;
  groupMask: string; isEnabled: boolean; isConnected: boolean;
  lastConnected: string | null; createdAt: string; updatedAt: string;
}

/**
 * Admin: MT5 Server management page.
 * Table with add/edit/test/enable/disable controls.
 */
export default function ServerManagement() {
  const [servers, setServers] = useState<ServerDto[]>([]);
  const [showAdd, setShowAdd] = useState(false);
  const [newServer, setNewServer] = useState({ name: "", address: "", managerLogin: 0, password: "", groupMask: "*" });
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const fetchServers = useCallback(async () => {
    try {
      const res = await apiFetch("/api/admin/servers");
      if (res.ok) setServers(await res.json());
    } catch { /* ignore */ }
  }, []);

  useEffect(() => { fetchServers(); const i = setInterval(fetchServers, 5000); return () => clearInterval(i); }, [fetchServers]);

  const handleAdd = async () => {
    setError(""); setMessage("");
    const res = await apiFetch("/api/admin/servers", {
      method: "POST", body: JSON.stringify(newServer),
    });
    const data = await res.json();
    if (res.ok) {
      setMessage("Server added");
      setShowAdd(false);
      setNewServer({ name: "", address: "", managerLogin: 0, password: "", groupMask: "*" });
      fetchServers();
    } else setError(data.error || "Failed");
  };

  const handleToggle = async (id: number, enabled: boolean) => {
    const endpoint = enabled ? "disable" : "enable";
    const res = await apiFetch(`/api/admin/servers/${id}/${endpoint}`, { method: "POST" });
    if (res.ok) fetchServers();
    else { const d = await res.json(); setError(d.error || "Failed"); }
  };

  const handleDelete = async (id: number) => {
    const res = await apiFetch(`/api/admin/servers/${id}`, { method: "DELETE" });
    if (res.ok) fetchServers();
    else { const d = await res.json(); setError(d.error || "Failed"); }
  };

  const handleTest = async (id: number) => {
    setMessage("");
    const res = await apiFetch(`/api/admin/servers/${id}/test`, { method: "POST" });
    const data = await res.json();
    if (res.ok) setMessage(data.message);
    else setError(data.error || "Test failed");
  };

  const th: CSSProperties = {
    padding: "8px 12px", textAlign: "left", fontSize: 11, fontWeight: 600,
    color: C.t3, borderBottom: `1px solid ${C.border}`, whiteSpace: "nowrap",
  };
  const td: CSSProperties = {
    padding: "8px 12px", fontSize: 12, color: C.t2, borderBottom: `1px solid ${C.border}`,
  };
  const input: CSSProperties = {
    padding: "8px 10px", borderRadius: 4, border: `1px solid ${C.border}`,
    background: C.bg3, color: C.t1, fontSize: 13, width: "100%",
    fontFamily: "'DM Sans',system-ui,sans-serif", outline: "none", marginTop: 4,
  };
  const smallBtn = (bg: string): CSSProperties => ({
    padding: "4px 10px", borderRadius: 4, border: "none", cursor: "pointer",
    background: bg, color: C.bg, fontSize: 11, fontWeight: 600, marginRight: 4,
  });

  return (
    <div style={{ padding: 20, overflowY: "auto", height: "100%" }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: 16 }}>
        <h2 style={{ color: C.t1, fontSize: 16, fontWeight: 600 }}>MT5 Servers</h2>
        <button onClick={() => setShowAdd(true)} style={{
          padding: "8px 16px", borderRadius: 6, border: "none", cursor: "pointer",
          background: C.teal, color: C.bg, fontSize: 12, fontWeight: 600,
        }}>+ Add Server</button>
      </div>

      {message && <div style={{ padding: "8px 12px", borderRadius: 6, marginBottom: 12, background: "rgba(61,217,160,0.1)", border: "1px solid rgba(61,217,160,0.3)", color: C.teal, fontSize: 12 }}>{message}</div>}
      {error && <div style={{ padding: "8px 12px", borderRadius: 6, marginBottom: 12, background: "rgba(255,82,82,0.1)", border: "1px solid rgba(255,82,82,0.3)", color: C.red, fontSize: 12 }}>{error}</div>}

      {showAdd && (
        <div style={{ padding: 16, background: C.bg3, borderRadius: 8, marginBottom: 16, border: `1px solid ${C.border}` }}>
          <h3 style={{ color: C.t1, fontSize: 13, fontWeight: 600, marginBottom: 12 }}>Add MT5 Server</h3>
          <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12 }}>
            <div><label style={{ color: C.t3, fontSize: 11 }}>Name</label><input style={input} placeholder="Live-EU" value={newServer.name} onChange={e => setNewServer(p => ({ ...p, name: e.target.value }))} /></div>
            <div><label style={{ color: C.t3, fontSize: 11 }}>Address</label><input style={input} placeholder="89.21.67.56:443" value={newServer.address} onChange={e => setNewServer(p => ({ ...p, address: e.target.value }))} /></div>
            <div><label style={{ color: C.t3, fontSize: 11 }}>Manager Login</label><input style={input} type="number" value={newServer.managerLogin || ""} onChange={e => setNewServer(p => ({ ...p, managerLogin: Number(e.target.value) }))} /></div>
            <div><label style={{ color: C.t3, fontSize: 11 }}>Password</label><input style={input} type="password" value={newServer.password} onChange={e => setNewServer(p => ({ ...p, password: e.target.value }))} /></div>
            <div><label style={{ color: C.t3, fontSize: 11 }}>Group Mask</label><input style={input} placeholder="*" value={newServer.groupMask} onChange={e => setNewServer(p => ({ ...p, groupMask: e.target.value }))} /></div>
          </div>
          <div style={{ marginTop: 12, display: "flex", gap: 8 }}>
            <button onClick={handleAdd} style={smallBtn(C.teal)}>Add Server</button>
            <button onClick={() => setShowAdd(false)} style={smallBtn(C.t3)}>Cancel</button>
          </div>
        </div>
      )}

      <table style={{ width: "100%", borderCollapse: "collapse", background: C.card, borderRadius: 8 }}>
        <thead>
          <tr>
            <th style={th}>Status</th><th style={th}>Name</th><th style={th}>Address</th>
            <th style={th}>Login</th><th style={th}>Mask</th><th style={th}>Enabled</th>
            <th style={th}>Last Connected</th><th style={th}>Actions</th>
          </tr>
        </thead>
        <tbody>
          {servers.map(s => (
            <tr key={s.id}>
              <td style={td}>
                <span style={{
                  display: "inline-block", width: 8, height: 8, borderRadius: "50%",
                  background: s.isConnected ? C.teal : C.red,
                  boxShadow: s.isConnected ? `0 0 6px ${C.teal}60` : `0 0 6px ${C.red}60`,
                }} />
              </td>
              <td style={{ ...td, color: C.t1, fontWeight: 500 }}>{s.name}</td>
              <td style={{ ...td, fontFamily: "'JetBrains Mono',monospace", fontSize: 11 }}>{s.address}</td>
              <td style={td}>{s.managerLogin}</td>
              <td style={td}>{s.groupMask}</td>
              <td style={td}><span style={{ color: s.isEnabled ? C.teal : C.t3 }}>{s.isEnabled ? "Yes" : "No"}</span></td>
              <td style={td}>{s.lastConnected ? new Date(s.lastConnected).toLocaleString() : "Never"}</td>
              <td style={td}>
                <button onClick={() => handleToggle(s.id, s.isEnabled)} style={smallBtn(s.isEnabled ? C.amber : C.teal)}>
                  {s.isEnabled ? "Disable" : "Enable"}
                </button>
                <button onClick={() => handleTest(s.id)} style={smallBtn(C.blue)}>Test</button>
                {!s.isConnected && <button onClick={() => handleDelete(s.id)} style={smallBtn(C.red)}>Delete</button>}
              </td>
            </tr>
          ))}
          {servers.length === 0 && (
            <tr><td style={{ ...td, textAlign: "center", color: C.t3 }} colSpan={8}>No servers configured</td></tr>
          )}
        </tbody>
      </table>
    </div>
  );
}
