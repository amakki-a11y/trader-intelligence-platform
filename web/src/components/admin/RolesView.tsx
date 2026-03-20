import { useState, useEffect, type CSSProperties } from "react";
import C from "../../styles/colors";
import { apiFetch } from "../../services/api";

interface RoleDto { id: number; name: string; description: string; permissions: string[]; }

/**
 * Admin: Read-only view of roles and their permissions.
 */
export default function RolesView() {
  const [roles, setRoles] = useState<RoleDto[]>([]);

  useEffect(() => {
    const load = async () => {
      try {
        const res = await apiFetch("/api/admin/roles");
        if (res.ok) setRoles(await res.json());
      } catch { /* ignore */ }
    };
    load();
  }, []);

  const card: CSSProperties = {
    padding: 16, background: C.card, borderRadius: 8,
    border: `1px solid ${C.border}`, marginBottom: 12,
  };

  const badge = (color: string): CSSProperties => ({
    display: "inline-block", padding: "3px 10px", borderRadius: 4,
    fontSize: 10, fontWeight: 600, marginRight: 6, marginBottom: 4,
    background: `${color}20`, color, fontFamily: "'JetBrains Mono',monospace",
  });

  const roleColor = (name: string) =>
    name === "admin" ? C.purple : name === "dealer" ? C.teal : C.blue;

  return (
    <div style={{ padding: 20, overflowY: "auto", height: "100%" }}>
      <h2 style={{ color: C.t1, fontSize: 16, fontWeight: 600, marginBottom: 16 }}>Roles & Permissions</h2>

      {roles.map(r => (
        <div key={r.id} style={card}>
          <div style={{ display: "flex", alignItems: "center", gap: 10, marginBottom: 8 }}>
            <span style={{
              padding: "4px 12px", borderRadius: 6, fontSize: 13, fontWeight: 700,
              background: `${roleColor(r.name)}20`, color: roleColor(r.name),
              textTransform: "capitalize",
            }}>{r.name}</span>
            <span style={{ color: C.t3, fontSize: 12 }}>{r.description}</span>
          </div>
          <div style={{ display: "flex", flexWrap: "wrap", gap: 4 }}>
            {r.permissions.map(p => (
              <span key={p} style={badge(roleColor(r.name))}>{p}</span>
            ))}
          </div>
        </div>
      ))}

      {roles.length === 0 && (
        <div style={{ color: C.t3, fontSize: 13, textAlign: "center", marginTop: 40 }}>Loading roles...</div>
      )}
    </div>
  );
}
