# Phase 4: React Dashboard — WebSocket + Dealer UX

## Context
Read `CLAUDE.md` first. Phases 1-3 are complete:
- P1: Solution scaffolded, TimescaleDB schema, MT5 Connector + simulator, batch writers, three-phase sync
- P2: Deal pipeline orchestration, DealProcessor, position tracking
- P3: Compute engines — P&L, CorrelationEngine (full 4-stage ring detection), AccountScorer (26 metrics), ExposureEngine, BotFingerprinter. REST API endpoints: /api/accounts, /api/positions, /api/exposure, /api/rings. 100 tests passing.

The backend is complete. The simulator generates live tick + deal data. The compute engines score accounts, detect rings, calculate P&L, and measure exposure in real-time. REST endpoints serve all this data.

**Now we build the dealer-facing React dashboard that displays it all.**

## v1.0 WinForms = Design Blueprint

The v1.0 `rebate-abuse-detector` WinForms app is the UX reference. Dealers are trained on its patterns. The React dashboard must feel immediately familiar:
- Same color scheme: red (CRITICAL), orange (HIGH), yellow (MEDIUM), white (LOW)
- Same 500ms flash for CRITICAL rows
- Same grid columns, sort order (CRITICAL first, then score descending)
- Same double-click drill-down to account detail
- Same event log panel with colored entries

## Tech Stack for Frontend

| Tool | Purpose |
|------|---------|
| React 18 | UI framework |
| TypeScript (strict) | No `any` types anywhere |
| Vite | Build tool (already in web/package.json) |
| Tailwind CSS | Utility-first styling |
| shadcn/ui | Component primitives (table, dialog, tabs, badge) |
| Recharts | Charts (score history, exposure) |
| Vitest | Frontend tests |

## What to Build

### 1. Project Setup (`web/`)

The `web/` folder already has a placeholder. Update it:

1. Install dependencies:
   ```
   npm install tailwindcss @tailwindcss/vite postcss autoprefixer
   npm install recharts lucide-react
   npm install -D @types/node vitest @testing-library/react @testing-library/jest-dom
   ```
   For shadcn/ui, use the manual approach (copy components) or install via CLI if available.

2. Configure Tailwind with a dark theme matching the v1.0 aesthetic:
   ```css
   /* Dark trading terminal palette */
   --bg-primary: #0C0F14;
   --bg-secondary: #141820;
   --bg-card: #1E2430;
   --text-primary: #F0EDE6;
   --text-secondary: #9BA3B0;
   --text-muted: #636B78;
   --risk-critical: #FF5252;
   --risk-high: #FF7B6B;
   --risk-medium: #FFBA42;
   --risk-low: #3DD9A0;
   --accent-teal: #3DD9A0;
   --accent-purple: #9B8AFF;
   --accent-blue: #5B9EFF;
   ```

3. Set up the app shell:
   - Dark theme by default
   - Sidebar navigation (collapsible)
   - Header with connection status indicator + pipeline state
   - Main content area for views

### 2. WebSocket Client (`web/src/services/websocket.ts`)

Create a WebSocket client that:
- Connects to `ws://localhost:5000/ws` (or configured URL)
- Auto-reconnects with exponential backoff (1s → 60s)
- Dispatches typed messages to subscribers
- Shows connection status in the UI header

Message types from the server:
```typescript
interface TickMessage {
  type: 'tick';
  symbol: string;
  bid: number;
  ask: number;
  timeMsc: number;
}

interface DealMessage {
  type: 'deal';
  dealId: number;
  login: number;
  symbol: string;
  action: number;
  volume: number;
  price: number;
  profit: number;
}

interface ScoreUpdateMessage {
  type: 'score_update';
  login: number;
  score: number;
  previousScore: number;
  riskLevel: 'Critical' | 'High' | 'Medium' | 'Low';
}

interface PnLUpdateMessage {
  type: 'pnl_update';
  positionId: number;
  login: number;
  symbol: string;
  unrealizedPnl: number;
}

interface AlertMessage {
  type: 'alert';
  login: number;
  message: string;
  severity: 'Critical' | 'High' | 'Medium' | 'Low';
  timestamp: string;
}
```

**ALSO create the server-side WebSocket hub:**

Update `src/TIP.Api/` to add a WebSocket endpoint at `/ws` that:
- Accepts WebSocket connections
- Subscribes to compute engine events (score changes, P&L updates, new deals)
- Broadcasts messages to all connected clients as JSON
- Sends periodic tick updates (throttled to every 500ms per symbol to avoid overwhelming the client)
- Sends deal events as they happen
- Sends score updates when AccountScorer produces new scores
- Sends P&L snapshots every 2 seconds

Create `src/TIP.Api/WebSocketHub.cs` for this.

### 3. Abuse Detection Grid (`web/src/components/AbuseGrid.tsx`) — PRIMARY VIEW

This is the main view dealers see. It's a table of all accounts sorted by risk.

**Data source:** `GET /api/accounts` on initial load, then WebSocket `score_update` messages for live updates.

**Columns:**
| Column | Source | Notes |
|--------|--------|-------|
| Login | AccountAnalysis.Login | Clickable → drill-down |
| Name | AccountAnalysis.Name | |
| Group | AccountAnalysis.Group | |
| Score | AccountAnalysis.AbuseScore | 0-100 with color badge |
| Risk | AccountAnalysis.RiskLevel | CRITICAL/HIGH/MEDIUM/LOW badge |
| Trend | Score delta | ↑ (rising), ↓ (falling), → (stable) with color |
| Trades | AccountAnalysis.TotalTrades | |
| Volume | AccountAnalysis.TotalVolume | Formatted in lots |
| Commission | AccountAnalysis.TotalCommission | Currency formatted |
| P&L | AccountAnalysis.TotalProfit | Green/red colored |
| Deposits | AccountAnalysis.TotalDeposits | Currency formatted |
| Ring | AccountAnalysis.IsRingMember | Badge if true, shows linked count |
| Server | AccountAnalysis.Server | |

**Row behavior:**
- **CRITICAL (score ≥ 70):** Red background + **500ms flash animation** (CSS keyframe alternating opacity)
- **HIGH (score ≥ 50):** Orange-tinted background
- **MEDIUM (score ≥ 30):** Yellow-tinted background  
- **LOW (score < 30):** Default dark background
- **Sort:** CRITICAL first, then by score descending
- **Click row:** Opens AccountDetail panel/modal
- **Live update:** When `score_update` WebSocket message arrives, update row in-place with brief highlight animation

**500ms CRITICAL flash (NON-NEGOTIABLE):**
```css
@keyframes critical-flash {
  0%, 100% { background-color: rgba(255, 82, 82, 0.15); }
  50% { background-color: rgba(255, 82, 82, 0.35); }
}
.row-critical {
  animation: critical-flash 500ms infinite;
}
```

**Event log panel (below or beside the grid):**
- Dark background, scrollable
- Colored entries: red for CRITICAL, orange for score changes, green for new deals
- Shows: timestamp, login, event description
- Auto-scrolls to latest entry
- Max 200 entries in memory (ring buffer)

### 4. Account Detail (`web/src/components/AccountDetail.tsx`)

Opens when dealer clicks a row in AbuseGrid. Shows everything about one account.

**Data source:** `GET /api/accounts/{login}`

**Sections:**

**Header bar:**
- Login, Name, Group
- Large score badge with risk level color
- Trend indicator (↑↓→) with velocity
- Book routing suggestion badge (A/B/Hybrid) — placeholder for Phase 5

**Tab: Deal History**
- Table: Time, Deal ID, Action (BUY/SELL/BALANCE/BONUS with color), Symbol, Volume, Price, Profit, Commission, Reason, Comment
- Sortable columns
- Paginated (load 100 at a time from REST API)

**Tab: Rule Breakdown**
- List all 23+ rules
- For each: metric name, current value, threshold, operator, weight, points scored
- Visual bar showing points contribution to total score
- Total score at bottom with risk level badge

**Tab: Ring Connections**
- If IsRingMember: show linked accounts as a list/card layout
- Each linked account: login, name, correlation count, shared ExpertIDs
- Click a linked account → navigate to its AccountDetail

**Tab: Trading Metrics**
- Cards layout:
  - Avg Hold Time: X seconds
  - Win Rate (short trades): X%
  - Trades/Hour: X
  - Expert Trade Ratio: X%
  - Timing Entropy (CV): X (with human/bot indicator)
  - Volume: X lots avg
  - Unique EAs: X
  - Deposits: $X | Withdrawals: $X | Net: $X

**Tab: Score History**
- Recharts line chart showing score over time
- X-axis: date, Y-axis: 0-100
- Color bands: red zone (≥70), orange zone (50-70), yellow zone (30-50), green zone (<30)
- Placeholder — data comes from `score_history` table (Phase 5 will populate)

### 5. Market Watch (`web/src/components/MarketWatch.tsx`)

**Data source:** WebSocket `tick` messages

**Columns:** Symbol, Bid, Ask, Spread (computed), Change, Change%

**Behavior:**
- Green flash on price up, red flash on price down (CSS transition 300ms)
- Spread = Ask - Bid, formatted to symbol digits
- Change = current bid - first bid of session
- Compact, data-dense layout — inspired by MT5/Bloomberg terminals
- Sort by symbol name alphabetically

### 6. Open Positions (`web/src/components/PositionsPanel.tsx`)

**Data source:** `GET /api/positions` on load, WebSocket `pnl_update` for live P&L

**Columns:** Login, Symbol, Direction (BUY/SELL badge), Volume, Open Price, Current Price, P&L (green/red), Swap, Margin

**Features:**
- P&L updates in real-time via WebSocket
- Negative P&L in red, positive in green
- Aggregation row at bottom: total positions, total P&L, total margin
- Click login → opens AccountDetail

### 7. Exposure Dashboard (`web/src/components/ExposureDashboard.tsx`)

**Data source:** `GET /api/exposure`

**Content:**
- Bar chart (Recharts) showing net exposure per symbol
- Green bars for net long, red bars for net short
- Table below: Symbol, Long Volume, Short Volume, Net Volume, # Positions, Unrealized P&L, Flagged Accounts
- Total portfolio exposure summary at top

### 8. Navigation Shell (`web/src/App.tsx`)

Sidebar navigation with these views:
1. **Abuse Detection** (default) — AbuseGrid + event log
2. **Market Watch** — live prices
3. **Positions** — open positions with P&L
4. **Exposure** — exposure dashboard
5. **Account Detail** — opens on row click (overlay/panel)

**Header bar (always visible):**
- "Trader Intelligence Platform" branding
- Connection status: 🟢 Connected / 🔴 Disconnected / 🟡 Reconnecting
- Pipeline state: Idle / Buffering / Backfilling / Live
- Stats: X accounts scored | X CRITICAL | X rings detected
- Fetch these from `/health` endpoint every 10 seconds

### 9. Server-Side: WebSocket Hub (`src/TIP.Api/WebSocketHub.cs`)

Since the REST endpoints already exist, the WebSocket hub adds real-time push:

```csharp
public sealed class WebSocketHub : BackgroundService
{
    // Accept WebSocket connections at /ws
    // Subscribe to compute engine events
    // Broadcast to all connected clients
    // Throttle ticks: max 1 update per symbol per 500ms
    // Send score updates immediately
    // Send P&L snapshots every 2 seconds
    // Send deal events as they happen
}
```

Update `Program.cs` to:
- Map `/ws` endpoint for WebSocket connections
- Register WebSocketHub as hosted service
- Create channels/events that compute engines can publish to for the hub to consume

### 10. Frontend Tests (`web/src/__tests__/`)

1. `AbuseGrid.test.tsx` — renders accounts, sorts by score, applies risk colors
2. `MarketWatch.test.tsx` — renders prices, updates on tick
3. `WebSocket.test.ts` — connects, reconnects, dispatches messages
4. `AccountDetail.test.tsx` — renders tabs, shows rule breakdown

## Design System Rules (NON-NEGOTIABLE)

1. **Risk colors carry from v1.0:**
   - CRITICAL (≥70): `#FF5252` red
   - HIGH (≥50): `#FF7B6B` coral/orange
   - MEDIUM (≥30): `#FFBA42` amber/yellow
   - LOW (<30): `#3DD9A0` teal/green (for "safe" indicator)
   - Default text on dark: `#F0EDE6`

2. **500ms CRITICAL flash is NON-NEGOTIABLE** — dealers are trained to see it

3. **Dark theme default** — background `#0C0F14`, cards `#1E2430`

4. **Desktop-first** — min-width 1200px. Dealers use multi-monitor setups.

5. **Data-dense** — this is a trading terminal, not a consumer app. Small fonts (13-14px body), compact rows, no wasted space.

6. **Zero page refreshes** — all updates via WebSocket

7. **Dashboard load < 2 seconds** — initial data via REST, then WebSocket for updates

8. **No `any` in TypeScript** — strict mode, every prop typed

## Update CLAUDE.md After Completion

1. Mark Phase 4 done in Build Order:
   ```
   8. [x] React dashboard ✅ (completed YYYY-MM-DD)
   ```

2. Add to the Progress Log:
   ```markdown
   ### Phase 4: React Dashboard — ✅ DONE (YYYY-MM-DD)
   - WebSocket hub broadcasting ticks, deals, scores, P&L to React client
   - AbuseGrid: color-coded rows, 500ms CRITICAL flash, score + trend, live WebSocket updates
   - AccountDetail: deal history, rule breakdown, ring connections, trading metrics tabs
   - MarketWatch: live prices with green/red flash
   - PositionsPanel: open positions with real-time P&L
   - ExposureDashboard: per-symbol net exposure bar chart + table
   - App shell: dark theme, sidebar nav, connection status header, pipeline state
   - Tailwind CSS dark trading terminal design system
   - X frontend tests
   - **Phases 1-4 COMPLETE — MVP SHIPPED.**
   - **Next up:** Phase 5 — Intelligence Layer (AI routing, profiles, compliance)
   ```

3. Update "Current state" to: `MVP complete. Phases 1-4 done. Phase 5 (intelligence layer) next.`

## Acceptance Criteria

1. `npm run build` in `web/` succeeds with zero errors
2. `npm test` passes all frontend tests
3. `dotnet build` still zero warnings (backend unchanged or improved)
4. `dotnet run` + `npm run dev` → full working dashboard:
   - AbuseGrid shows scored accounts with risk colors
   - CRITICAL rows flash at 500ms
   - Clicking a row opens AccountDetail
   - MarketWatch shows live prices from simulator
   - Positions shows P&L updating in real-time
   - Exposure shows per-symbol chart
   - WebSocket connection status visible in header
5. Dark theme consistent across all views
6. No `any` types in TypeScript
7. CLAUDE.md updated — Phase 4 done, MVP complete
