/**
 * Shared deal parsing utilities used by LiveMonitor, AccountDetail, and
 * any other component that needs to map raw REST/WebSocket deal data
 * into display-ready format.
 */

import type { Deal, LiveEvent } from "../store/TipStore";

/** Maps MT5 action enum to display string. */
export const ACTION_MAP: Record<number, string> = {
  0: "BUY",
  1: "SELL",
  2: "BALANCE",
  3: "CREDIT",
  4: "CHARGE",
  5: "CORRECTION",
  6: "BONUS",
};

/** Maps MT5 entry enum to display string. */
export const ENTRY_MAP: Record<number, string> = {
  0: "Open",
  1: "Close",
  2: "Reverse",
  3: "Close By",
};

/** Maps WebSocket entry string codes to display labels. */
export const WS_ENTRY_MAP: Record<string, string> = {
  IN: "Open",
  OUT: "Close",
  INOUT: "Reverse",
  OUT_BY: "Close By",
};

/** Raw deal shape from REST /api/accounts/{login}/deals endpoint. */
export interface RawDealResponse {
  dealId: number;
  login: number;
  time: string;
  symbol: string | null;
  action: number;
  volume: number | null;
  price: number | null;
  profit: number;
  commission: number | null;
  swap: number | null;
  reason: number | null;
  expertId: number | null;
  entry: number;
}

/**
 * Derives the human-readable entry label for a deal.
 * Handles BALANCE (Deposit/Withdrawal) and BONUS specially.
 */
export function deriveEntryLabel(action: number, entry: number, profit: number): string {
  if (action === 2) return profit >= 0 ? "Deposit" : "Withdrawal";
  if (action === 6) return "Bonus";
  return ENTRY_MAP[entry] ?? "";
}

/**
 * Parses a raw REST deal response into a typed Deal object.
 */
export function parseDeal(d: RawDealResponse): Deal {
  return {
    ticket: d.dealId,
    time: d.time,
    login: d.login,
    symbol: d.symbol ?? "",
    action: ACTION_MAP[d.action] ?? `ACTION_${d.action}`,
    volume: d.volume ?? 0,
    price: d.price ?? 0,
    profit: d.profit ?? 0,
    commission: d.commission ?? 0,
    swap: d.swap ?? 0,
    reason: d.reason?.toString() ?? "",
    expertId: d.expertId ?? 0,
    holdSec: 0,
    entry: deriveEntryLabel(d.action, d.entry, d.profit),
  };
}

/**
 * Parses a raw REST deal response into a LiveEvent for the LiveMonitor.
 */
export function parseDealToLiveEvent(
  d: RawDealResponse,
  accountName: string,
  accountScore: number,
  riskLevel: string,
): LiveEvent {
  return {
    id: d.dealId,
    time: d.time ? new Date(d.time).toLocaleTimeString("en-GB") : "",
    login: d.login,
    name: accountName,
    symbol: d.symbol ?? "",
    action: ACTION_MAP[d.action] ?? `ACTION_${d.action}`,
    volume: d.volume ?? 0,
    price: d.price ?? 0,
    profit: d.profit ?? 0,
    score: accountScore,
    scoreChange: 0,
    isCorrelated: false,
    correlated: null,
    severity: riskLevel === "Critical" ? "CRITICAL" : riskLevel === "High" ? "HIGH" : riskLevel === "Medium" ? "MEDIUM" : "LOW",
    entry: deriveEntryLabel(d.action, d.entry, d.profit),
  };
}

/**
 * Parses a WebSocket deal event into a LiveEvent for the LiveMonitor.
 */
export function parseWsDealToLiveEvent(d: Record<string, unknown>): LiveEvent | null {
  const did = Number(d.dealId);
  if (!did) return null;

  const time = d.timeMsc
    ? new Date(d.timeMsc as number).toLocaleTimeString("en-GB")
    : new Date().toLocaleTimeString("en-GB");

  const entryRaw = (d.entry as string) ?? "";
  const act = (d.action as string) ?? "";
  const wsEntry = act === "BALANCE"
    ? ((d.profit as number) >= 0 ? "Deposit" : "Withdrawal")
    : act === "BONUS"
      ? "Bonus"
      : (WS_ENTRY_MAP[entryRaw] ?? entryRaw);

  return {
    id: did,
    time,
    login: d.login as number,
    name: (d.login as number)?.toString() ?? "",
    symbol: (d.symbol as string) ?? "",
    action: act,
    volume: (d.volume as number) ?? 0,
    price: (d.price as number) ?? 0,
    profit: (d.profit as number) ?? 0,
    score: (d.score as number) ?? 0,
    scoreChange: (d.scoreChange as number) ?? 0,
    isCorrelated: (d.isCorrelated as boolean) ?? false,
    correlated: null,
    severity: (d.severity as string) ?? "Low",
    entry: wsEntry,
  };
}
