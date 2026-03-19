/** Global color constants used throughout the TIP dashboard. */
const C = {
  bg: "#0B0E13", bg2: "#111620", bg3: "#171D28", card: "#1C2333",
  border: "rgba(255,255,255,0.07)", borderHi: "rgba(255,255,255,0.14)",
  t1: "#EBE8E0", t2: "#8F99A8", t3: "#555F70",
  teal: "#3DD9A0", tealBg: "rgba(61,217,160,0.07)",
  purple: "#9B8AFF", purpleBg: "rgba(155,138,255,0.07)",
  coral: "#FF7B6B", coralBg: "rgba(255,123,107,0.07)",
  amber: "#FFBA42", amberBg: "rgba(255,186,66,0.07)",
  red: "#FF5252", redBg: "rgba(255,82,82,0.07)",
  green: "#66BB6A", blue: "#5B9EFF",
};

export default C;

export const sevColor = (s: number): string =>
  s >= 70 ? "#FF5252" : s >= 50 ? "#FF7B6B" : s >= 30 ? "#FFBA42" : "#3DD9A0";

export const DEFAULT_WATCHLIST = [
  "US30-","XAUUSD-","EURUSD-","GBPUSD-","USDJPY-","NZDUSD-",
  "AUDUSD-","USDCAD-","GBPJPY-","EURJPY-","EURGBP-","BTCUSD-",
];
