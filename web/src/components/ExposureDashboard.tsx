import C from "../styles/colors";

/**
 * ExposureDashboard — placeholder for a standalone exposure view.
 * Currently, exposure data is available via the /api/exposure endpoint.
 * This component exists as a routing target for future expansion.
 */
function ExposureDashboard() {
  return (
    <div style={{ flex: 1, display: "flex", alignItems: "center", justifyContent: "center", flexDirection: "column", gap: 12 }}>
      <span style={{ fontSize: 40, opacity: 0.3 }}>{"\u{1F4CA}"}</span>
      <span style={{ fontSize: 14, color: C.t3 }}>Exposure Dashboard</span>
      <span style={{ fontSize: 12, color: C.t3 }}>Exposure analysis coming in a future update</span>
    </div>
  );
}

export default ExposureDashboard;
