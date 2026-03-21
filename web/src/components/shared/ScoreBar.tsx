import { sevColor } from "../../styles/colors";

interface ScoreBarProps {
  score: number;
  width?: number;
}

function ScoreBar({ score, width = 80 }: ScoreBarProps) {
  const pct = Math.min(100, Math.max(0, score));
  const color = sevColor(score);
  return (
    <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
      <div style={{ width, height: 5, borderRadius: 3, background: "rgba(255,255,255,0.06)" }}>
        <div style={{ width: `${pct}%`, height: "100%", borderRadius: 3, background: color, transition: "width 0.5s" }} />
      </div>
      <span style={{ fontFamily: "'JetBrains Mono',monospace", fontSize: 12, fontWeight: 600, color, minWidth: 24 }}>{score}</span>
    </div>
  );
}

export default ScoreBar;
