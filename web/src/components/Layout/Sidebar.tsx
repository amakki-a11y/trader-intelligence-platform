import { useState } from "react";
import type { CSSProperties } from "react";
import C from "../../styles/colors";
import { BarChart3, Users, Radio, ShieldAlert, LogOut, Settings } from "lucide-react";

interface SidebarProps {
  view: string;
  setView: (v: string) => void;
  connected: boolean;
  critCount: number;
  user?: { role: string; permissions: string[]; displayName: string } | null;
  onLogout?: () => void;
}

function Sidebar({ view, setView, connected, critCount, onLogout }: SidebarProps) {
  const navItems = [
    { id: "market", icon: BarChart3, label: "Market Watch" },
    { id: "grid", icon: Users, label: "Accounts" },
    { id: "live", icon: Radio, label: "Live Monitor" },
    { id: "threats", icon: ShieldAlert, label: "Threat View" },
  ];

  const [hoveredId, setHoveredId] = useState<string | null>(null);

  const btnS = (active: boolean, id: string): CSSProperties => ({
    width: 40, height: 40, borderRadius: 8, border: "none", cursor: "pointer",
    background: active ? "rgba(61,217,160,0.12)" : "transparent",
    color: active ? C.teal : C.t3, fontSize: 18,
    display: "flex", alignItems: "center", justifyContent: "center", transition: "all 0.15s",
    position: "relative",
    borderLeft: active ? `2px solid ${C.teal}` : "2px solid transparent",
    marginLeft: id ? 0 : undefined,
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
      {navItems.map(item => {
        const Icon = item.icon;
        return (
          <div key={item.id} style={{ position: "relative" }}
            onMouseEnter={() => setHoveredId(item.id)}
            onMouseLeave={() => setHoveredId(null)}>
            <button onClick={() => setView(item.id)} style={btnS(view === item.id, item.id)} title={item.label}>
              <Icon size={18} />
              {item.id === "grid" && critCount > 0 && (
                <span style={{
                  position: "absolute", top: 4, right: 4, minWidth: 14, height: 14, borderRadius: 7,
                  background: C.red, color: "#fff", fontSize: 8, fontWeight: 700,
                  display: "flex", alignItems: "center", justifyContent: "center",
                  fontFamily: "'JetBrains Mono',monospace", padding: "0 3px",
                }}>{critCount}</span>
              )}
            </button>
            {hoveredId === item.id && (
              <div style={{
                position: "absolute", left: "100%", top: "50%", transform: "translateY(-50%)",
                marginLeft: 8, background: C.bg2, border: `1px solid ${C.border}`,
                borderRadius: 6, padding: "4px 10px", whiteSpace: "nowrap", zIndex: 100,
                fontSize: 11, color: C.t1, boxShadow: "0 4px 12px rgba(0,0,0,0.3)",
              }}>{item.label}</div>
            )}
          </div>
        );
      })}
      <div style={{ flex: 1 }} />
      {onLogout && (
        <button onClick={onLogout} style={{
          width: 40, height: 40, borderRadius: 8, border: "none", cursor: "pointer",
          background: "transparent", color: C.t3, fontSize: 16,
          display: "flex", alignItems: "center", justifyContent: "center",
        }} title="Logout"><LogOut size={18} /></button>
      )}
      <button onClick={() => setView("settings")} style={btnS(view === "settings", "settings")} title="Settings"><Settings size={18} /></button>
      <div style={{
        width: 8, height: 8, borderRadius: "50%", marginBottom: 16,
        background: connected ? C.teal : C.red,
        boxShadow: connected ? `0 0 8px ${C.teal}60` : `0 0 8px ${C.red}60`,
      }} />
    </div>
  );
}

export default Sidebar;
