import type { CSSProperties } from "react";
import C from "../../styles/colors";

interface SidebarProps {
  view: string;
  setView: (v: string) => void;
  connected: boolean;
  user?: { role: string; permissions: string[]; displayName: string } | null;
  onLogout?: () => void;
}

function Sidebar({ view, setView, connected, onLogout }: SidebarProps) {
  const navItems = [
    { id: "market", icon: "\u{1F4CA}", label: "Market Watch" },
    { id: "grid", icon: "\u25A6", label: "Accounts" },
    { id: "live", icon: "\u25C9", label: "Live Monitor" },
    { id: "threats", icon: "\u25C6", label: "Threat View" },
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
      {onLogout && (
        <button onClick={onLogout} style={{
          width: 40, height: 40, borderRadius: 8, border: "none", cursor: "pointer",
          background: "transparent", color: C.t3, fontSize: 16,
          display: "flex", alignItems: "center", justifyContent: "center",
        }} title="Logout">{"\u{1F6AA}"}</button>
      )}
      <button onClick={() => setView("settings")} style={btnS(view === "settings")} title="Settings">{"\u2699"}</button>
      <div style={{
        width: 8, height: 8, borderRadius: "50%", marginBottom: 16,
        background: connected ? C.teal : C.red,
        boxShadow: connected ? `0 0 8px ${C.teal}60` : `0 0 8px ${C.red}60`,
      }} />
    </div>
  );
}

export default Sidebar;
