import { useState, useEffect, useCallback, type CSSProperties } from "react";
import C from "../../styles/colors";
import { apiFetch } from "../../services/api";

interface UserDto {
  id: number; username: string; email: string; displayName: string;
  roleId: number; roleName: string; isActive: boolean;
  mustChangePassword: boolean; lastLogin: string | null;
  createdAt: string; serverIds: number[];
}
interface RoleDto { id: number; name: string; description: string; permissions: string[]; }

/**
 * Admin: User management page. Table + create/edit modals.
 */
export default function UserManagement() {
  const [users, setUsers] = useState<UserDto[]>([]);
  const [roles, setRoles] = useState<RoleDto[]>([]);
  const [showCreate, setShowCreate] = useState(false);
  const [newUser, setNewUser] = useState({ username: "", email: "", displayName: "", roleId: 1 });
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");

  const fetchUsers = useCallback(async () => {
    try {
      const res = await apiFetch("/api/admin/users");
      if (res.ok) setUsers(await res.json());
    } catch { /* ignore */ }
  }, []);

  const fetchRoles = useCallback(async () => {
    try {
      const res = await apiFetch("/api/admin/roles");
      if (res.ok) setRoles(await res.json());
    } catch { /* ignore */ }
  }, []);

  useEffect(() => { fetchUsers(); fetchRoles(); }, [fetchUsers, fetchRoles]);

  const handleCreate = async () => {
    setError(""); setMessage("");
    const res = await apiFetch("/api/admin/users", {
      method: "POST", body: JSON.stringify(newUser),
    });
    const data = await res.json();
    if (res.ok) {
      setMessage(`User created! Temporary password: ${data.tempPassword}`);
      setShowCreate(false);
      setNewUser({ username: "", email: "", displayName: "", roleId: 1 });
      fetchUsers();
    } else {
      setError(data.error || "Failed");
    }
  };

  const handleResetPassword = async (id: number) => {
    const res = await apiFetch(`/api/admin/users/${id}/reset-password`, { method: "POST" });
    const data = await res.json();
    if (res.ok) setMessage(`Password reset! Temporary: ${data.tempPassword}`);
    else setError(data.error || "Failed");
  };

  const handleDeactivate = async (id: number) => {
    const res = await apiFetch(`/api/admin/users/${id}`, { method: "DELETE" });
    if (res.ok) fetchUsers();
    else { const d = await res.json(); setError(d.error || "Failed"); }
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
        <h2 style={{ color: C.t1, fontSize: 16, fontWeight: 600 }}>User Management</h2>
        <button onClick={() => setShowCreate(true)} style={{
          padding: "8px 16px", borderRadius: 6, border: "none", cursor: "pointer",
          background: C.teal, color: C.bg, fontSize: 12, fontWeight: 600,
        }}>+ Add User</button>
      </div>

      {message && <div style={{ padding: "8px 12px", borderRadius: 6, marginBottom: 12, background: "rgba(61,217,160,0.1)", border: "1px solid rgba(61,217,160,0.3)", color: C.teal, fontSize: 12 }}>{message}</div>}
      {error && <div style={{ padding: "8px 12px", borderRadius: 6, marginBottom: 12, background: "rgba(255,82,82,0.1)", border: "1px solid rgba(255,82,82,0.3)", color: C.red, fontSize: 12 }}>{error}</div>}

      {showCreate && (
        <div style={{ padding: 16, background: C.bg3, borderRadius: 8, marginBottom: 16, border: `1px solid ${C.border}` }}>
          <h3 style={{ color: C.t1, fontSize: 13, fontWeight: 600, marginBottom: 12 }}>Create User</h3>
          <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12 }}>
            <div>
              <label style={{ color: C.t3, fontSize: 11 }}>Username</label>
              <input style={input} value={newUser.username} onChange={e => setNewUser(p => ({ ...p, username: e.target.value }))} />
            </div>
            <div>
              <label style={{ color: C.t3, fontSize: 11 }}>Email</label>
              <input style={input} value={newUser.email} onChange={e => setNewUser(p => ({ ...p, email: e.target.value }))} />
            </div>
            <div>
              <label style={{ color: C.t3, fontSize: 11 }}>Display Name</label>
              <input style={input} value={newUser.displayName} onChange={e => setNewUser(p => ({ ...p, displayName: e.target.value }))} />
            </div>
            <div>
              <label style={{ color: C.t3, fontSize: 11 }}>Role</label>
              <select style={{ ...input, cursor: "pointer" }} value={newUser.roleId} onChange={e => setNewUser(p => ({ ...p, roleId: Number(e.target.value) }))}>
                {roles.map(r => <option key={r.id} value={r.id}>{r.name}</option>)}
              </select>
            </div>
          </div>
          <div style={{ marginTop: 12, display: "flex", gap: 8 }}>
            <button onClick={handleCreate} style={smallBtn(C.teal)}>Create</button>
            <button onClick={() => setShowCreate(false)} style={smallBtn(C.t3)}>Cancel</button>
          </div>
        </div>
      )}

      <table style={{ width: "100%", borderCollapse: "collapse", background: C.card, borderRadius: 8 }}>
        <thead>
          <tr>
            <th style={th}>ID</th><th style={th}>Username</th><th style={th}>Email</th>
            <th style={th}>Role</th><th style={th}>Active</th><th style={th}>Last Login</th>
            <th style={th}>Actions</th>
          </tr>
        </thead>
        <tbody>
          {users.map(u => (
            <tr key={u.id}>
              <td style={td}>{u.id}</td>
              <td style={{ ...td, color: C.t1, fontWeight: 500 }}>{u.username}</td>
              <td style={td}>{u.email}</td>
              <td style={td}>
                <span style={{
                  padding: "2px 8px", borderRadius: 4, fontSize: 10, fontWeight: 600,
                  background: u.roleName === "admin" ? "rgba(155,138,255,0.15)" : u.roleName === "dealer" ? "rgba(61,217,160,0.15)" : "rgba(91,158,255,0.15)",
                  color: u.roleName === "admin" ? C.purple : u.roleName === "dealer" ? C.teal : C.blue,
                }}>{u.roleName}</span>
              </td>
              <td style={td}>
                <span style={{ color: u.isActive ? C.teal : C.red }}>{u.isActive ? "Yes" : "No"}</span>
              </td>
              <td style={td}>{u.lastLogin ? new Date(u.lastLogin).toLocaleString() : "Never"}</td>
              <td style={td}>
                <button onClick={() => handleResetPassword(u.id)} style={smallBtn(C.amber)}>Reset Pwd</button>
                {u.isActive && <button onClick={() => handleDeactivate(u.id)} style={smallBtn(C.red)}>Deactivate</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
