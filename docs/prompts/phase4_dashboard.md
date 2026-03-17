# Phase 4: React Dashboard — WebSocket + Dealer UX

## Context
Read `CLAUDE.md` first. Phases 1-3 are complete with 100 tests passing:
- Phase 1: Solution, schema, MT5 connector, batch writers, three-phase sync
- Phase 2: Deal processing pipeline, position tracking, account repository
- Phase 3: P&L engine, correlation engine (full 4-stage), account scorer, exposure engine, bot fingerprinter, REST API

**The backend is fully operational.** The simulator generates ticks + deals → pipeline processes them → engines compute P&L, abuse scores, ring detection, exposure in real-time. REST endpoints are live:
- `GET /api/accounts` — scored accounts sorted by risk
- `GET /api/accounts/{login}` — detailed single account with all 26 metrics
- `GET /api/positions` — open positions with live P&L
- `GET /api/exposure` — net exposure by symbol + portfolio totals
- `GET /api/rings` — detected rings with member details
- `GET /health` — system health with pipeline state + compute stats

**Now we build the dealer dashboard** — the React frontend that displays all of this in real-time.

## Design Blueprint: v1.0 WinForms

The v1.0 WinForms application IS the design spec. Dealers are trained on its patterns. The React dashboard must feel immediately familiar:

- Color-coded rows: CRITICAL (red + 500ms flash), HIGH (orange), MEDIUM (yellow), LOW (white)
- Score column: 0-100 with trend arrow (↑↓→) and velocity coloring
- Double-click drill-down to account detail
- Live updates — grid rows update in-place, no page refresh
- Event log panel with colored entries
- Data-dense professional aesthetic — trading terminal, not consumer app
- Dark theme default (background #0C0F14)
- Desktop-first, multi-monitor optimized

## What to Build

### 1. Project Setup — `web/`

The `web/` folder already has a minimal Vite + React + TypeScript setup. Expand it:

**Install dependencies:**
```bash
cd web
npm install react-router-dom
npm install recharts                    # Charts for score history, P&L
npm install @tanstack/react-table       # High-performance data grid
npm install clsx                        # Conditional classnames
npm install --save-dev @types/react-router-dom
```

**Project structure:**
```
web/
├── index.html
├── package.json
├── tsconfig.json
├── vite.config.ts                     # Proxy /api → localhost:5000
├── src/
│   ├── main.tsx                       # App entry + router
│   ├── App.tsx                        # Layout + routing
│   ├── styles/
│   │   ├── globals.css                # Dark theme, CSS variables, risk colors
│   │   └── components.css             # Component-specific styles
│   ├── hooks/
│   │   ├── useWebSocket.ts            # WebSocket connection + auto-reconnect
│   │   ├── useApi.ts                  # REST API fetch wrapper
│   │   └── usePolling.ts              # Polling fallback (until WebSocket push)
│   ├── types/
│   │   └── index.ts                   # TypeScript interfaces matching API responses
│   ├── components/
│   │   ├── Layout.tsx                 # App shell: sidebar nav + header + main content
│   │   ├── StatusBar.tsx              # Bottom bar: connection status, pipeline state, stats
│   │   ├── AbuseGrid.tsx              # THE primary view — scored accounts grid
│   │   ├── AccountDetail.tsx          # Drill-down: metrics, rules, ring, deals, chart
│   │   ├── MarketWatch.tsx            # Live prices grid
│   │   ├── PositionsPanel.tsx         # Open positions with real-time P&L
│   │   ├── ExposureDashboard.tsx      # Net exposure by symbol
│   │   ├── RingViewer.tsx             # Detected rings visualization
│   │   └── EventLog.tsx              # Real-time event feed
│   └── utils/
│       ├── formatters.ts              # Number/date/currency formatting
│       └── constants.ts               # Risk colors, thresholds, refresh intervals
```

### 2. Design System — `src/styles/globals.css`

```css
:root {
  /* Background layers */
  --bg-primary: #0C0F14;
  --bg-secondary: #141820;
  --bg-tertiary: #1A1F2A;
  --bg-card: #1E2430;
  
  /* Text */
  --text-primary: #F0EDE6;
  --text-secondary: #9BA3B0;
  --text-muted: #636B78;
  
  /* Risk level colors — NON-NEGOTIABLE (from v1.0) */
  --risk-critical: #FF5252;
  --risk-critical-bg: rgba(255, 82, 82, 0.08);
  --risk-critical-border: rgba(255, 82, 82, 0.25);
  --risk-high: #FF7B6B;
  --risk-high-bg: rgba(255, 123, 107, 0.08);
  --risk-medium: #FFBA42;
  --risk-medium-bg: rgba(255, 186, 66, 0.08);
  --risk-low: rgba(255, 255, 255, 0.04);
  
  /* Accent colors */
  --accent-teal: #3DD9A0;
  --accent-purple: #9B8AFF;
  --accent-blue: #5B9EFF;
  
  /* P&L colors */
  --pnl-positive: #66BB6A;
  --pnl-negative: #FF5252;
  
  /* Borders */
  --border: rgba(255, 255, 255, 0.08);
  --border-hover: rgba(255, 255, 255, 0.15);
  
  /* Typography */
  --font-body: 'DM Sans', -apple-system, sans-serif;
  --font-mono: 'JetBrains Mono', 'Fira Code', monospace;
  
  /* Spacing */
  --header-height: 48px;
  --sidebar-width: 220px;
  --statusbar-height: 32px;
}

/* Import fonts */
@import url('https://fonts.googleapis.com/css2?family=DM+Sans:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap');

* { margin: 0; padding: 0; box-sizing: border-box; }

body {
  background: var(--bg-primary);
  color: var(--text-primary);
  font-family: var(--font-body);
  font-size: 13px;
  line-height: 1.5;
  overflow: hidden; /* App manages its own scrolling */
}

/* 500ms CRITICAL flash animation — NON-NEGOTIABLE */
@keyframes criticalFlash {
  0%, 100% { background-color: var(--risk-critical-bg); }
  50% { background-color: rgba(255, 82, 82, 0.20); }
}

.risk-critical { animation: criticalFlash 500ms infinite; }
.risk-high { background-color: var(--risk-high-bg); }
.risk-medium { background-color: var(--risk-medium-bg); }
.risk-low { background-color: var(--risk-low); }

/* Monospace for numbers */
.mono { font-family: var(--font-mono); font-size: 12px; }

/* Price flash */
@keyframes priceUp { from { color: var(--pnl-positive); } }
@keyframes priceDown { from { color: var(--pnl-negative); } }
.price-up { animation: priceUp 500ms ease-out; }
.price-down { animation: priceDown 500ms ease-out; }
```

### 3. TypeScript Types — `src/types/index.ts`

Match the API response shapes exactly:

```typescript
export interface Account {
  login: number;
  abuseScore: number;
  riskLevel: 'Critical' | 'High' | 'Medium' | 'Low';
  totalTrades: number;
  totalVolume: number;
  totalProfit: number;
  isRingMember: boolean;
  timingEntropyCV: number;
  expertTradeRatio: number;
  lastScored: string;
}

export interface AccountDetail extends Account {
  name: string;
  group: string;
  server: string;
  previousScore: number;
  metrics: AccountMetrics;
  ring: RingInfo;
}

export interface AccountMetrics {
  totalTrades: number;
  totalVolume: number;
  totalCommission: number;
  totalProfit: number;
  totalDeposits: number;
  totalWithdrawals: number;
  totalBonuses: number;
  depositCount: number;
  soCompensationCount: number;
  commissionToVolumeRatio: number;
  profitToCommissionRatio: number;
  avgHoldSeconds: number;
  winRateOnShortTrades: number;
  scalpCount: number;
  slippageDirectionBias: number;
  bonusToDepositRatio: number;
  timingEntropyCV: number;
  expertTradeRatio: number;
  avgVolumeLots: number;
  tradesPerHour: number;
  uniqueExpertIds: number;
}

export interface RingInfo {
  isRingMember: boolean;
  ringCorrelationCount: number;
  linkedLogins: number[];
}

export interface Position {
  positionId: number;
  login: number;
  symbol: string;
  direction: 'BUY' | 'SELL';
  volume: number;
  openPrice: number;
  currentPrice: number;
  unrealizedPnL: number;
  swap: number;
  calculatedAt: string;
}

export interface SymbolExposure {
  symbol: string;
  longVolume: number;
  shortVolume: number;
  netVolume: number;
  longPositionCount: number;
  shortPositionCount: number;
  unrealizedPnL: number;
  flaggedAccountCount: number;
}

export interface ExposureResponse {
  symbols: SymbolExposure[];
  totals: {
    totalLong: number;
    totalShort: number;
    netExposure: number;
    symbolCount: number;
  };
}

export interface HealthResponse {
  status: string;
  timestamp: string;
  database?: { connected: boolean };
  pipeline?: { state: string; backfilledDeals: number };
  compute?: {
    accountsScored: number;
    criticalCount: number;
    highCount: number;
    mediumCount: number;
    lowCount: number;
    ringsDetected: number;
    openPositions: number;
    totalUnrealizedPnL: number;
  };
}

export type RiskLevel = 'Critical' | 'High' | 'Medium' | 'Low';
```

### 4. API Hook — `src/hooks/useApi.ts`

```typescript
const API_BASE = '/api';

export function useApi() {
  async function fetchAccounts(risk?: RiskLevel): Promise<Account[]>;
  async function fetchAccountDetail(login: number): Promise<AccountDetail>;
  async function fetchPositions(login?: number, symbol?: string): Promise<Position[]>;
  async function fetchExposure(): Promise<ExposureResponse>;
  async function fetchRings(): Promise<RingsResponse>;
  async function fetchHealth(): Promise<HealthResponse>;
}
```

### 5. Polling Hook — `src/hooks/usePolling.ts`

Until WebSocket push is implemented, poll the REST endpoints:
```typescript
export function usePolling<T>(
  fetcher: () => Promise<T>,
  intervalMs: number = 2000
): { data: T | null; loading: boolean; error: string | null }
```

Use 2-second polling for accounts grid, 1-second for positions/prices, 5-second for exposure.

### 6. WebSocket Hub — Backend Addition

Create `src/TIP.Api/WebSocketHub.cs`:

ASP.NET Core WebSocket endpoint at `/ws` that:
1. Accepts WebSocket connections from the React dashboard
2. Pushes events as JSON messages:
   - `{ type: "tick", data: { symbol, bid, ask, timeMsc } }`
   - `{ type: "deal", data: { dealId, login, symbol, action, volume, ... } }`
   - `{ type: "score", data: { login, abuseScore, riskLevel, previousScore } }`
   - `{ type: "position", data: { positionId, login, symbol, unrealizedPnL, ... } }`
   - `{ type: "alert", data: { login, message, severity } }`
3. Broadcasts to all connected clients
4. Handles disconnect/reconnect gracefully

Register in Program.cs:
```csharp
app.Map("/ws", async (HttpContext context, WebSocketHub hub) => { ... });
```

### 7. WebSocket Hook — `src/hooks/useWebSocket.ts`

```typescript
export function useWebSocket(url: string = 'ws://localhost:5000/ws') {
  // Auto-reconnect with exponential backoff
  // Parse incoming JSON messages by type
  // Expose: connected, lastTick, lastDeal, lastScore, lastAlert
  // Callback registration: onTick, onDeal, onScore, onAlert
}
```

### 8. Layout — `src/components/Layout.tsx`

App shell with:
- **Sidebar** (left, 220px): Navigation links — Abuse Detection, Market Watch, Positions, Exposure, Rings
- **Header** (top, 48px): "Trader Intelligence Platform" title, connection indicator (green dot = connected), quick stats (CRITICAL count badge)
- **Main content** (fills remaining space): React Router renders active view
- **Status bar** (bottom, 32px): Pipeline state, accounts scored count, last tick timestamp, WebSocket status

### 9. AbuseGrid — `src/components/AbuseGrid.tsx` (THE PRIMARY VIEW)

This is the most important component — the v1.0 grid translated to React.

**Columns:**
| Column | Width | Content |
|--------|-------|---------|
| Login | 80px | Account number (monospace) |
| Score | 70px | 0-100, bold, colored by risk level |
| Risk | 80px | CRITICAL/HIGH/MEDIUM/LOW badge |
| Trend | 50px | Arrow: ↑ (score increasing), ↓ (decreasing), → (stable) + delta |
| Trades | 70px | Total trade count |
| Volume | 90px | Total volume in lots |
| P&L | 100px | Total profit, green/red colored |
| Ring | 50px | 🔗 icon if ring member, click shows linked logins |
| Entropy | 70px | TimingEntropyCV value (low = bot suspect) |
| EA % | 60px | ExpertTradeRatio as percentage |

**Row behavior:**
- CRITICAL rows (score ≥ 70): red background, **500ms flash animation** (CSS `criticalFlash`)
- HIGH rows (score ≥ 50): orange background
- MEDIUM rows (score ≥ 30): yellow background
- LOW rows (score < 30): default dark background
- Sort: CRITICAL first, then by score descending within each level
- **Double-click row** → navigate to `/account/{login}` (AccountDetail)
- Rows update in-place when new data arrives (no full re-render)
- Show total count in header: "847 accounts — 12 CRITICAL · 28 HIGH · 64 MEDIUM"

**Auto-refresh:** Poll `GET /api/accounts` every 2 seconds OR receive WebSocket `score` events.

### 10. AccountDetail — `src/components/AccountDetail.tsx`

Full drill-down view when dealer clicks a row. Route: `/account/:login`

**Layout (grid-based):**
```
┌─────────────────────────────────────────────────┐
│ HEADER: Login 50042 · "John Smith" · Group: real│
│ Score: 78 CRITICAL (↑ from 72) · Ring Member    │
├────────────────────┬────────────────────────────┤
│ METRICS CARDS      │ SCORE HISTORY CHART        │
│ (2x4 grid)         │ (Recharts line chart)       │
│ Hold: 3.2s         │                            │
│ Win Rate: 82%      │ [score over time]          │
│ Trades/hr: 47      │                            │
│ Entropy: 0.05      │                            │
│ EA Ratio: 98%      │                            │
│ Volume: 0.01       │                            │
│ Bonus/Dep: 1.2     │                            │
│ Deposits: $1,200   │                            │
├────────────────────┴────────────────────────────┤
│ RING CONNECTIONS                                 │
│ Linked to: 50041, 50043 (48 correlated trades)  │
│ Shared EA: Magic 77201                          │
├─────────────────────────────────────────────────┤
│ RULE BREAKDOWN                                   │
│ ✓ TotalTrades > 100: +15 pts                    │
│ ✓ ExpertTradeRatio > 0.9: +12 pts               │
│ ✓ TimingEntropyCV < 0.1: +10 pts                │
│ ✗ BonusToDepositRatio > 0.5: 0 pts              │
│ ... (all 26 rules listed)                        │
│ TOTAL: 78 / 100                                  │
├─────────────────────────────────────────────────┤
│ DEAL HISTORY (table, paginated)                  │
│ Time | Deal | Action | Symbol | Vol | P&L | ...  │
└─────────────────────────────────────────────────┘
```

- **Back button** → return to AbuseGrid
- **Metrics cards:** 2x4 grid of key stats with color coding (red if suspicious)
- **Score history:** Recharts LineChart showing score over time (from future `/api/accounts/{login}/history` endpoint, for now show current + previous as 2 points)
- **Ring connections:** List linked logins as clickable links to their detail views
- **Rule breakdown:** Show all 26 rules, which fired (✓) with points, which didn't (✗). Show total.
- **Deal history:** Table of recent deals from `GET /api/accounts/{login}` (future: paginated from DB)

### 11. MarketWatch — `src/components/MarketWatch.tsx`

Live price grid from WebSocket ticks or `/health` tick data.

**Columns:** Symbol, Bid, Ask, Spread, Change, Change%
- **Green flash** on bid/ask increase (CSS `.price-up`)
- **Red flash** on bid/ask decrease (CSS `.price-down`)
- Compact, data-dense layout
- Sort by symbol name
- Initial data: WebSocket `tick` events or poll a new endpoint

**Backend addition needed:** Add `GET /api/prices` endpoint to AnalyticsController that returns `TickListener.GetAllPrices()` as JSON.

### 12. PositionsPanel — `src/components/PositionsPanel.tsx`

Live position grid from `GET /api/positions`.

**Columns:** Login, Symbol, Direction (BUY green / SELL red), Volume, Open Price, Current, P&L (green/red), Swap
- **Auto-refresh:** Poll every 1 second or WebSocket `position` events
- **Click login** → navigate to AccountDetail
- **Summary header:** "234 positions · Total P&L: -$15,432.50"
- **Aggregation rows:** Group by symbol, show subtotals

### 13. ExposureDashboard — `src/components/ExposureDashboard.tsx`

From `GET /api/exposure`.

- **Bar chart** (Recharts): Long (green) vs Short (red) volume per symbol
- **Table below:** Symbol, Long Vol, Short Vol, Net, P&L, Flagged Accounts
- **Portfolio totals** at top: Total Long, Total Short, Net Exposure
- Auto-refresh every 5 seconds

### 14. RingViewer — `src/components/RingViewer.tsx`

From `GET /api/rings`.

- **Ring members list:** Each ring as a card showing member logins, correlation count, shared ExpertIDs
- **Click a login** → navigate to AccountDetail
- **Summary:** "3 rings detected · 9 accounts involved"
- Visual: simple connected-dots diagram showing which logins are linked (SVG or CSS-based, not a full graph library)

### 15. EventLog — `src/components/EventLog.tsx`

Real-time scrolling event feed. Dark background, colored entries.

- WebSocket events rendered as log lines:
  - 🔴 `[CRITICAL] Account 50042 score: 78 (↑6)` — red text
  - 🟠 `[DEAL] 50042 BUY XAUUSD 0.50 lots @ 2,341.50` — orange text
  - 🟢 `[TICK] EURUSD 1.08542 / 1.08545` — green text (dimmed)
  - 🔵 `[RING] Correlation detected: 50042 ↔ 50043 on XAUUSD` — blue text
- Max 200 entries, oldest dropped
- Auto-scroll to bottom (with pause on manual scroll-up)
- Filterable: show/hide ticks, deals, scores, alerts

### 16. Vite Config — `vite.config.ts`

```typescript
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5000',
      '/ws': { target: 'ws://localhost:5000', ws: true },
      '/health': 'http://localhost:5000'
    }
  }
});
```

---

## Backend Additions Required

### New endpoint: `GET /api/prices`
Add to AnalyticsController:
```csharp
[HttpGet("prices")]
public IActionResult GetPrices()
{
    var prices = _tickListener.GetAllPrices();
    return Ok(prices.Select(p => new {
        p.Value.Symbol, p.Value.Bid, p.Value.Ask,
        spread = p.Value.Ask - p.Value.Bid,
        p.Value.TimeMsc, p.Value.ReceivedAt
    }));
}
```

### WebSocket Hub: `src/TIP.Api/WebSocketHub.cs`
- Accept connections at `/ws`
- Subscribe to compute engine events
- Broadcast JSON messages to all connected clients
- Message types: tick, deal, score, position, alert

---

## Coding Rules — Frontend

1. **TypeScript strict mode** — no `any` types, ever
2. **Functional components only** — hooks, no class components
3. **No inline styles** — use CSS classes from globals.css
4. **CSS variables for all colors** — no hardcoded hex in components
5. **Monospace font for all numbers** — prices, volumes, scores, P&L
6. **Risk colors are non-negotiable** — must match v1.0 exactly
7. **500ms CRITICAL flash is non-negotiable** — CSS animation, not JS interval
8. **Desktop-first layout** — minimum 1200px width assumed
9. **No external CSS framework** (no Tailwind, no Bootstrap) — custom CSS matching v1.0 dark theme aesthetic
10. **Every component in its own file** — no barrel exports, no index.ts re-exports
11. **Format numbers consistently:** volume to 2 decimals, prices to symbol digits, P&L with $ and sign

## Update CLAUDE.md After Completion

1. Mark Phase 4 done in Build Order

2. Add to the Progress Log:
   ```markdown
   ### Phase 4: React Dashboard — ✅ DONE (YYYY-MM-DD)
   - Layout: sidebar nav + header + status bar + main content area
   - AbuseGrid: color-coded rows, 500ms CRITICAL flash, trend arrows, double-click drill-down
   - AccountDetail: metrics cards, rule breakdown, ring connections, deal history, score chart
   - MarketWatch: live prices with green/red flash on movement
   - PositionsPanel: open positions with real-time P&L, login click-through
   - ExposureDashboard: bar chart + table of net exposure by symbol
   - RingViewer: detected rings with member links
   - EventLog: real-time scrolling feed with color-coded entries
   - StatusBar: pipeline state, connection indicator, stats
   - WebSocket hub: server-push for ticks, deals, scores, alerts
   - useWebSocket hook: auto-reconnect, message parsing
   - Dark theme design system matching v1.0 aesthetic
   - REST + WebSocket data flow working end-to-end
   - **Phases 1-4 COMPLETE. MVP SHIPPED.**
   - **Next up:** Phase 5 — Intelligence Layer (AI routing, simulation, compliance)
   ```

3. Update "Current state" to: "v2.0 MVP complete — Phases 1-4 done. Phase 5 (intelligence layer) next."

## Acceptance Criteria

1. `npm run build` in `web/` succeeds with zero errors
2. `npm run dev` starts Vite dev server on port 5173
3. Dashboard loads in < 2 seconds showing AbuseGrid as default view
4. AbuseGrid displays accounts from `/api/accounts` with correct risk colors
5. CRITICAL rows flash at 500ms interval (CSS animation)
6. Double-click a row → navigates to AccountDetail with all metrics
7. MarketWatch shows live prices with flash-on-change
8. PositionsPanel shows positions with green/red P&L coloring
9. ExposureDashboard shows bar chart + table from `/api/exposure`
10. EventLog scrolls with incoming events
11. Sidebar navigation works between all views
12. Status bar shows connection status + pipeline state
13. WebSocket connection established (or polling fallback works)
14. All number formatting consistent (mono font, correct decimals)
15. CLAUDE.md updated — Phase 4 complete, MVP shipped
