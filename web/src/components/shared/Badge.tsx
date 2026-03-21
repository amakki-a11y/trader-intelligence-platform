import type { ReactNode } from "react";

interface BadgeProps {
  color: string;
  children: ReactNode;
  small?: boolean;
}

function Badge({ color, children, small }: BadgeProps) {
  return (
    <span style={{
      display: "inline-block", fontFamily: "'JetBrains Mono',monospace",
      fontSize: small ? 9 : 10, fontWeight: 600, letterSpacing: "0.5px",
      color, background: color + "14", border: `1px solid ${color}40`,
      borderRadius: 4, padding: small ? "1px 6px" : "2px 8px",
    }}>{children}</span>
  );
}

export default Badge;
