export interface DealResponse {
  dealId: number;
  login: number;
  symbol: string;
  action: number;
  entry: number;
  volume: number;
  price: number;
  profit: number;
  commission: number;
  swap: number;
  reason: number;
  expert: number;
  expertId: number;
  timeMsc: number;
  time: string;
}

export interface AccountResponse {
  login: number;
  name: string;
  group: string;
  abuseScore: number;
  riskLevel: string;
  totalTrades: number;
  totalVolume: number;
  totalCommission: number;
  totalProfit: number;
  totalDeposits: number;
  expertTradeRatio: number;
  isRingMember: boolean;
  ringCorrelationCount: number;
  scoreVelocity: number;
  server: string;
  timingEntropyCV: number;
  lastScored: string;
}

export interface PositionResponse {
  positionId: number;
  login: number;
  symbol: string;
  action: string;
  volume: number;
  priceOpen: number;
  priceCurrent: number;
  profit: number;
  swap: number;
  margin: number;
  time: string;
  sl: number;
  tp: number;
}

export interface AccountInfoResponse {
  login: number;
  balance: number;
  equity: number;
  leverage: number;
  name: string;
  group: string;
}

export interface TraderProfileResponse {
  login: number;
  tradingStyle: string;
  styleConfidence: number;
  bookRecommendation: string;
  bookConfidence: number;
  bookSummary: string;
  riskFlags: string[];
  styleSignals: string[];
}
