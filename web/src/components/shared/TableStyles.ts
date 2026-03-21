import type { CSSProperties } from "react";
import C from "../../styles/colors";

export const thStyle: CSSProperties = {
  padding: "8px 8px", textAlign: "left", fontSize: 9,
  fontFamily: "'JetBrains Mono',monospace", fontWeight: 600, color: C.t3,
  borderBottom: `1px solid ${C.border}`, position: "sticky", top: 0, background: C.bg2, zIndex: 2,
  letterSpacing: "0.5px", textTransform: "uppercase",
};

export const tdStyle: CSSProperties = {
  padding: "6px 8px", fontSize: 11, color: C.t2, borderBottom: `1px solid ${C.border}`,
  fontFamily: "'JetBrains Mono',monospace",
};

export const tdMono: CSSProperties = {
  ...tdStyle,
  textAlign: "right",
  fontFamily: "'JetBrains Mono',monospace",
};
