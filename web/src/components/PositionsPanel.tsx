import C from "../styles/colors";

/**
 * PositionsPanel — placeholder for a standalone open positions view.
 * Currently, open positions are shown inside AccountDetail.
 * This component exists as a routing target for future expansion.
 */
function PositionsPanel() {
  return (
    <div style={{ flex: 1, display: "flex", alignItems: "center", justifyContent: "center", flexDirection: "column", gap: 12 }}>
      <span style={{ fontSize: 40, opacity: 0.3 }}>{"\u{1F4C8}"}</span>
      <span style={{ fontSize: 14, color: C.t3 }}>Positions Panel</span>
      <span style={{ fontSize: 12, color: C.t3 }}>Open positions are currently shown in Account Detail view</span>
    </div>
  );
}

export default PositionsPanel;
