import C from "../../styles/colors";

function Skeleton({ width, height = 16 }: { width?: number | string; height?: number | string }) {
  return (
    <div style={{
      width: width ?? "100%", height, borderRadius: 4,
      background: `linear-gradient(90deg, ${C.bg3} 25%, rgba(255,255,255,0.04) 50%, ${C.bg3} 75%)`,
      backgroundSize: "200% 100%",
      animation: "skeletonPulse 1.5s ease-in-out infinite",
    }} />
  );
}

export default Skeleton;
