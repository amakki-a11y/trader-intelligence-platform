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

/** Base symbol names for default watchlist — resolved to server-specific names at runtime. */
export const DEFAULT_WATCHLIST_BASES = [
  "US30","XAUUSD","EURUSD","GBPUSD","USDJPY","NZDUSD",
  "AUDUSD","USDCAD","GBPJPY","EURJPY","EURGBP","BTCUSD",
];

/**
 * Resolves base symbol names to the best matching symbols available on the server.
 * Uses live price data to pick symbols that actually have feeds.
 *
 * Priority when prices available:
 *   Pick the variant with a live price feed (bid > 0), preferring shortest name.
 *
 * Priority without prices (fallback):
 *   .m suffix → dash suffix → exact match → shortest candidate
 *
 * Common server patterns:
 *   EURUSD (no feed), EURUSD- (feed), EURUSD.m (feed), EURUSD.e, EURUSD.s
 */
export function resolveWatchlist(
  bases: string[],
  available: Array<{ symbol: string }>,
  prices?: Record<string, { bid: number }>
): string[] {
  const resolved: string[] = [];

  for (const base of bases) {
    // Find all candidates that start with the base name
    const candidates = available
      .map(s => s.symbol)
      .filter(s => s.startsWith(base) && !s.includes("#") && !s.includes("_"));

    if (candidates.length === 0) {
      resolved.push(base); // fallback: keep base name
      continue;
    }

    // If we have price data, prefer symbols with live feeds
    if (prices) {
      const withFeed = candidates.filter(s => {
        const p = prices[s];
        return p !== undefined && p.bid > 0;
      });
      if (withFeed.length > 0) {
        // Prefer shortest name among those with feed
        withFeed.sort((a, b) => a.length - b.length);
        resolved.push(withFeed[0]!);
        continue;
      }
    }

    // No price data available — use suffix priority
    const dotM = candidates.find(s => s === base + ".m");
    if (dotM) { resolved.push(dotM); continue; }

    const dash = candidates.find(s => s === base + "-");
    if (dash) { resolved.push(dash); continue; }

    const exact = candidates.find(s => s === base);
    if (exact) { resolved.push(exact); continue; }

    // Shortest candidate
    candidates.sort((a, b) => a.length - b.length);
    resolved.push(candidates[0]!);
  }

  return resolved;
}

/** Legacy export for backward compat — uses dash suffix. */
export const DEFAULT_WATCHLIST = DEFAULT_WATCHLIST_BASES.map(b => b + "-");
