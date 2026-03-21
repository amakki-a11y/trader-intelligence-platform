import C from "../../styles/colors";
import type { Threats } from "../../store/TipStore";

function ThreatBars({ threats }: { threats: Threats }) {
  const items = [
    { key: "ring" as const, label: "Ring", color: C.red },
    { key: "latency" as const, label: "Lat", color: C.coral },
    { key: "bonus" as const, label: "Bon", color: C.amber },
    { key: "bot" as const, label: "Bot", color: C.purple },
  ];
  return (
    <div style={{ display: "flex", gap: 3, alignItems: "end", height: 24 }}>
      {items.map(({ key, label, color }) => {
        const v = threats[key];
        return (
          <div key={key} style={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 1 }}>
            <div style={{
              width: 14, height: Math.max(2, v * 22), borderRadius: 2,
              background: v > 0.5 ? color : "rgba(255,255,255,0.08)", transition: "height 0.4s",
            }} />
            <span style={{ fontSize: 7, color: C.t3, fontFamily: "'JetBrains Mono',monospace" }}>{label}</span>
          </div>
        );
      })}
    </div>
  );
}

export default ThreatBars;
