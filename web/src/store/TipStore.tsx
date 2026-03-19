/**
 * Shared types used across TIP dashboard components.
 * No React context/reducer store exists in the current architecture;
 * state lives in App.tsx and is passed via props.
 * This module re-exports the shared local types for components.
 */

export interface Threats { ring: number; latency: number; bonus: number; bot: number }

export interface Account {
  login: number; name: string; group: string; score: number; sev: string;
  deposits: number; totalDeposited: number; bonuses: number; volume: number; commissions: number;
  pnl: number; tradeCount: number; expertRatio: number; ib: string; primaryEA: number;
  avgHoldSec: number; winRate: number; tradesPerHour: number; timingCV: number;
  isRingMember: boolean; ringPartners: number[]; bonusToDepRatio: number; lastActivity: string;
  threats: Threats; routing: string;
}

export interface Deal {
  ticket: number; time: string; login: number; symbol: string; action: string;
  volume: number; price: number; profit: number; commission: number; swap: number;
  reason: string; expertId: number; holdSec: number; entry: string;
}

export interface OpenTrade {
  ticket: number; time: string; symbol: string; action: string; volume: number;
  openPrice: number; currentPrice: number; profit: number; swap: number; sl: number; tp: number;
}

export interface MoneyOp { id: number; time: string; type: string; amount: number; method: string; status: string }

export interface MarketDataPoint { symbol: string; bid: number; ask: number; spread: number; change24h: number; digits: number }

export interface LiveEvent {
  id: number; time: string; login: number; name: string; symbol: string; action: string;
  volume: number; price: number; profit: number; score: number; scoreChange: number; isCorrelated: boolean;
  correlated: number | null; severity: string; entry: string;
}

export interface VolumeData { buy: number; sell: number; net: number; topBuyer: { login: number; volume: number }; topSeller: { login: number; volume: number } }

export interface ConnectionStatus {
  connected: boolean; server: string; login: string;
  accountsInScope: number; uptimeSeconds: number; error: string | null;
}

export interface ConnectionLogEntry { timestamp: string; level: string; message: string }

export interface ScanSettings { historyDays: number; minDeposit: number; pollIntervalMs: number; criticalThreshold: number }

export function mapAccountResponse(a: import("../types/api").AccountResponse): Account {
  return {
    login: a.login,
    name: a.name ?? a.login.toString(),
    group: a.group ?? "",
    score: a.abuseScore ?? 0,
    sev: a.riskLevel === "Critical" ? "CRITICAL" : a.riskLevel === "High" ? "HIGH" : a.riskLevel === "Medium" ? "MEDIUM" : "LOW",
    deposits: 0,
    totalDeposited: a.totalDeposits ?? 0,
    bonuses: 0,
    volume: a.totalVolume ?? 0,
    commissions: a.totalCommission ?? 0,
    pnl: a.totalProfit ?? 0,
    tradeCount: a.totalTrades ?? 0,
    expertRatio: a.expertTradeRatio ?? 0,
    ib: "",
    primaryEA: 0,
    avgHoldSec: 0,
    winRate: 0,
    tradesPerHour: 0,
    timingCV: a.timingEntropyCV ?? 0,
    isRingMember: a.isRingMember ?? false,
    ringPartners: [],
    bonusToDepRatio: 0,
    lastActivity: a.lastScored ?? new Date().toISOString(),
    threats: { ring: a.isRingMember ? 0.8 : 0, latency: 0, bonus: 0, bot: (a.expertTradeRatio ?? 0) > 0.5 ? 0.7 : 0 },
    routing: (a.abuseScore ?? 0) >= 60 ? "A-Book" : (a.abuseScore ?? 0) >= 35 ? "Review" : "B-Book",
  };
}

export function formatUptime(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  return `${h}h ${m}m`;
}
