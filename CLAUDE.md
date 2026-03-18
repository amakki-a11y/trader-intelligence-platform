# Trader Intelligence Platform (TIP) v2.0

## Current state
v2.0 in development — Phases 1-5 complete + real MT5 integration live. Live Market Watch with 1171 symbols, zero-delay WebSocket prices, full SelectedAddAll pump. Data persisting to TimescaleDB. Credentials secured via User Secrets. Phase 6 next.

## What is TIP?
Brokerage operations platform for detecting trading abuse on MetaTrader 5. Successor to the v1.0 RebateAbuseDetector.

## Tech Stack
- **Backend:** .NET 8, ASP.NET Core, Channel<T> pipelines
- **Database:** TimescaleDB (PostgreSQL extension for time-series)
- **Frontend:** React 18 + TypeScript (Vite)
- **Testing:** MSTest
- **Logging:** Serilog (console + daily rolling file)

## Project Structure
```
TIP.sln
├── src/TIP.Core/        — Pure business logic (zero external deps)
├── src/TIP.Connector/   — MT5 Manager API connection + data pipeline
├── src/TIP.Data/        — TimescaleDB access layer (Npgsql)
├── src/TIP.Api/         — ASP.NET Core host
├── src/TIP.Tests/       — MSTest unit tests
├── web/                 — React + TypeScript dashboard (Vite)
└── docs/                — Schema and documentation
```

## Dependency Graph
```
TIP.Connector → TIP.Core
TIP.Data      → TIP.Core
TIP.Api       → TIP.Connector, TIP.Core, TIP.Data
TIP.Tests     → TIP.Api, TIP.Connector, TIP.Core, TIP.Data
```

## Build Order
1. [x] .NET 8 solution structure ✅ (completed 2026-03-16)
2. [x] TimescaleDB schema ✅ (completed 2026-03-16)
3. [x] MT5 Connector: CIMTDealSink + OnTick → Channel<T> ✅ (completed 2026-03-16)
4. [x] Batch tick/deal writers (Channel → TimescaleDB) ✅ (completed 2026-03-17)
5. [x] Three-phase sync with buffer pattern ✅ (completed 2026-03-17)
6. [x] Deal processing pipeline (DealProcessor + position tracking) ✅ (completed 2026-03-17)
7. [x] Compute engines (P&L, Correlation, AccountScorer, Exposure, BotFingerprinter) ✅ (completed 2026-03-17)
8. [x] React dashboard v2 + DealerHub WebSocket ✅ (rebuilt 2026-03-17)
9. [x] AI engines (StyleClassifier, BookRouter, SimulationEngine) ✅ (completed 2026-03-17)
10. [ ] ML-based classification

## Coding Rules
1. .NET 8 target, nullable enabled, warnings as errors
2. Every class has XML doc comments explaining purpose and design rationale
3. No file exceeds 800 lines
4. All using statements at file top (no global usings file)
5. Immutable records for events (DealEvent, TickEvent)
6. readonly record struct for TradeFingerprint
7. Regular classes for everything else
8. No `any` in TypeScript — strict mode
9. Every TODO must reference which Phase/Task it belongs to

---

## Progress Log

### Phase 1, Task 1: .NET 8 Solution Structure — ✅ DONE (2026-03-16)
- Created TIP.sln with 5 projects: TIP.Connector, TIP.Core, TIP.Data, TIP.Api, TIP.Tests
- Created web/ React + TypeScript placeholder (Vite)
- Created docs/schema.sql placeholder
- RuleEngine fully implemented (26 metrics, 6 operators, score capping)
- CorrelationEngine index structure implemented
- TradeFingerprint with bucket key logic implemented
- 12 unit tests passing (RuleEngine: 8, TradeFingerprint: 4)
- Program.cs working with Serilog, Channel<T>, CORS, /health endpoint
- Installed .NET 8 SDK (8.0.419) on the machine
- **Next up:** Phase 1, Task 2 — TimescaleDB schema

### Phase 1, Task 2: TimescaleDB Schema — ✅ DONE (2026-03-16)
- Created docs/schema.sql with 13 tables + 3 continuous aggregates
- Tables: ticks, deals, positions, accounts, symbols, sync_state, trader_profiles, score_history, trading_metrics, audit_log, alerts, symbol_mapping, correlation_pairs
- Hypertables: ticks (1-day chunks), deals (1-month), score_history (1-month), trading_metrics (1-month)
- Continuous aggregates: candles_1m, candles_1h, candles_1d
- Compression: ticks after 7 days, deals after 30 days, scores after 90 days
- Retention: ticks dropped after 90 days, everything else permanent
- Updated TIP.Data SQL templates
- **Next up:** Phase 1, Task 3 — MT5 Connector (CIMTDealSink + OnTick)

### Phase 1, Task 3: MT5 Connector — ✅ DONE (2026-03-16)
- Created IMT5Api abstraction interface (7 methods + 3 events)
- Created RawTypes.cs (RawDeal, RawTick, RawUser, RawSymbol)
- Created MT5ApiSimulator — generates fake ticks (5 symbols, 200ms) and deals (1-5s) for dev/testing
- Created MT5ApiReal.cs behind #if MT5_API_AVAILABLE (ready for native DLLs)
- Updated MT5Connection.cs — full connect/subscribe/heartbeat/reconnect lifecycle
- Updated HistoryFetcher.cs — rate-limited backfill via IMT5Api
- Updated SyncStateTracker.cs — in-memory checkpoint tracking
- Updated Program.cs — full DI wiring with simulator toggle ("UseSimulator": true)
- 19 new unit tests (MT5Simulator: 12, DealSink: 7) — 31 total passing
- `dotnet run` now starts and generates live simulated tick/deal data
- **Next up:** Phase 1, Task 4 — Batch tick writer (Channel → TimescaleDB)

### Phase 1, Task 4: Batch Tick/Deal Writers — ✅ DONE (2026-03-17)
- Created DbConnectionFactory — NpgsqlDataSource wrapper with connection pooling
- Implemented TickWriter — COPY protocol bulk inserts, thread-safe batching, re-buffer on failure
- Implemented DealRepository — COPY bulk insert, INSERT ON CONFLICT, paginated SELECT
- Created TickWriterService — BackgroundService reading Channel<TickEvent> → TickWriter with periodic flush
- Created DealWriterService — BackgroundService reading Channel<DealEvent> → DealRepository with batch flush
- Updated Program.cs — full DI wiring, auto-detects TimescaleDB availability (db=false if CHANGE_ME)
- Updated TraderProfileRepository to use DbConnectionFactory
- Health endpoint now reports ticksIngested, tickFlushes, ticksBuffered, dbEnabled
- 12 new unit tests (TickWriter: 8, TickWriterService: 2, DealWriterService: 2) — 43 total passing
- `dotnet run` shows TickWriterService + DealWriterService started in logs
- **Next up:** Phase 1, Task 5 + Phase 2 — Pipeline Orchestration + Deal Processing

### Phase 1, Task 5 + Phase 2: Pipeline Orchestration + Deal Processing — ✅ DONE (2026-03-17)
- PipelineOrchestrator: three-phase startup (Buffer → Backfill → Live)
- DealProcessor: classifies deals (17 types), tracks position open/close/modify
- PositionRepository + AccountRepository: CRUD for positions and accounts
- HistoryFetcher: full implementation with rate limiting + progress logging every 10 logins
- SyncStateTracker: database-backed checkpoints with in-memory fallback
- DealSink: added Reset() for reconnect scenarios
- MT5Connection: integrated with PipelineOrchestrator for startup + reconnect
- Program.cs: wired DealProcessor, PipelineOrchestrator, PositionRepository, AccountRepository
- Health endpoint now reports pipeline state (state, backfilledDeals, bufferedReplayed, duplicatesSkipped)
- End-to-end pipeline: simulator → channels → writers → DB (or discard)
- Catch-up sync on restart via sync_state checkpoints
- 24 new tests (orchestrator: 6, deal processor: 10, sync tracker: 7, deal sink reset: 1) — 67 total passing
- **Phase 1 COMPLETE. Phase 2 COMPLETE.**
- **Next up:** Phase 3 — Compute Engines (P&L, Correlation, Rule Engine, Exposure)

### Phase 3: Compute Engines — ✅ DONE (2026-03-17)
- PnLEngine: real-time unrealized P&L via tick-driven recalculation, BUY/SELL formulas, contract size lookup
- CorrelationEngine: full 4-stage algorithm (Index → Match → Cluster → Score), union-find with path compression, live CheckDeal with adjacent bucket support, Prune for memory management
- AccountScorer: incremental per-deal scoring, timing entropy (CV=σ/μ), expert trade tracking, ring correlation integration, risk classification (Critical/High/Medium/Low)
- ExposureEngine: per-symbol net exposure aggregation from PnL results, portfolio totals
- BotFingerprinter: timing entropy + EA clustering + volume pattern analysis, weighted confidence scoring
- PnLEngineService: BackgroundService feeding ticks to PnLEngine, periodic stats + exposure recalc
- ComputeEngineService: BackgroundService feeding deals through DealProcessor → AccountScorer → CorrelationEngine → PnLEngine → ExposureEngine pipeline, alert logging on score changes
- ChannelFanOutService: explicit fan-out from main ingest channels to consumer channels (DB writers + compute engines)
- REST API controller: GET /api/accounts, /api/accounts/{login}, /api/positions, /api/exposure, /api/rings
- Program.cs: fan-out channel architecture, all engines registered as singletons, /health includes compute stats
- 33 new tests (PnLEngine: 6, CorrelationEngine: 10, AccountScorer: 8, ExposureEngine: 4, BotFingerprinter: 5) — 100 total passing
- `dotnet build` — zero warnings, zero errors
- **Phases 1-3 COMPLETE.**
- **Next up:** Phase 4 — React Dashboard

### Phase 4: Dashboard v2 — React + WebSocket + Dealer UX — ✅ REBUILT (2026-03-17)
- **Backend — DealerHub**: replaced WebSocketHub with DealerHub implementing IWebSocketBroadcaster interface. Subscribe pattern: clients send `{ "subscribe": ["prices","accounts","positions","alerts"] }`. Per-client subscription filtering. Throttling: prices 500ms/symbol, positions 1s global, scores/alerts immediate.
- **Backend — BroadcastModels**: 4 sealed records (AccountSummaryDto, SymbolPriceDto, PositionSummaryDto, AlertMessageDto) for type-safe WS broadcasting.
- **Backend — ComputeEngineService + PnLEngineService**: updated to use IWebSocketBroadcaster with new DTO types.
- **Backend — Program.cs**: DealerHub registered as singleton + IWebSocketBroadcaster, WebSocket endpoint at /ws.
- **Frontend — Complete rebuild**: deleted old web/src/, rebuilt from scratch with Tailwind CSS (@tailwindcss/vite plugin).
- **Frontend — TipStore**: global state via Context + useReducer (not per-component state). Actions: PRICE_UPDATE, ACCOUNT_UPDATE, ACCOUNTS_LOADED, POSITION_UPDATE, POSITIONS_LOADED, ALERT, WS_STATUS.
- **Frontend — WebSocket client**: auto-reconnect, subscribe pattern, dispatches to TipStore reducer.
- **Frontend — Tailwind theme**: custom tip-* colors (bg, surface, border, text, muted, accent) and risk-* colors (critical red, high orange, medium yellow, low green), price-up/down colors.
- **Frontend — AbuseGrid (MOST IMPORTANT)**: risk-colored rows, ScoreBadge with trend arrows (up/down/stable), 500ms CRITICAL flash (CSS @keyframes animation), double-click drill-down to AccountDetail, filter by risk level.
- **Frontend — MarketWatch**: live price table with 300ms green/red flash on bid change, spread, change, change%.
- **Frontend — AccountDetail**: 4 tabs (Deal History, Rule Breakdown, Ring Connections, Trading Metrics). Rule breakdown shows all 10 scoring rules with hit/miss indicators.
- **Frontend — PositionsPanel**: all open positions with live P&L, aggregate totals.
- **Frontend — ExposureDashboard**: bar chart (recharts) of net exposure by symbol, summary cards, detailed table.
- **Frontend — Layout**: Sidebar with nav links (lucide-react icons), Header with WS status + alert count, Outlet pattern.
- **Frontend — Routing**: react-router-dom v7, 6 routes (/, /market, /accounts, /accounts/:login, /positions, /exposure).
- **Frontend packages**: tailwindcss, @tailwindcss/vite, react-router-dom, recharts, lucide-react, clsx
- `npx tsc --noEmit` — zero errors
- `npx vite build` — success
- `dotnet build` — zero warnings, zero errors
- **Phases 1-4 COMPLETE.**
- **Next up:** Phase 5 — AI engines (StyleClassifier, BookRouter, SimulationEngine)

### Phase 4.5: Real MT5 Integration — ✅ DONE (2026-03-17)
- **MT5 Connection**: Connected to real MT5 server (89.21.67.56:443), auto-connects on startup via appsettings.json
- **Real Tick Subscription**: Added CIMTTickSink handler in MT5ApiReal for live price streaming
- **Pipeline Fixes**: GetUserLogins retry loop (5 attempts, 2s delay) for pump sync, skipped tick backfill (no historical tick API), HistoryFetcher date range fix (cutoffTime.AddDays(-90))
- **ConnectionManager Fix**: SetConnected fallback after StartPipeline completes, password guard ("CHANGE_ME") skips auto-connect
- **Open Positions API**: Added IMT5Api.GetPositions(login) → MT5 PositionGet, RawPosition type, /api/accounts/{login}/positions endpoint
- **Account Info API**: Added /api/accounts/{login}/info (balance, equity, leverage from MT5), /api/accounts/{login}/deals (real deal history)
- **Account Enrichment**: /api/accounts now returns Name and Group from MT5 API lookup
- **Frontend — Real Data**: Account Scanner fetches from /api/accounts (always polls, no connection guard), AccountDetail fetches balance/equity/leverage from /api/accounts/{login}/info, deal history from /api/accounts/{login}/deals, open positions from /api/accounts/{login}/positions
- **Settings Page**: ConnectionManager with connect/disconnect, connection log, scan settings
- **Debug Endpoint**: /api/settings/connection/debug for troubleshooting MT5 groups/logins
- **Live Monitor — Real Deals**: Live Monitor loads last 24h of real deals from all scored accounts via REST on GO LIVE, then subscribes to WebSocket "deals" channel for real-time new deals. Dedup via seenIds set.
- **Deal Event Broadcasting**: ComputeEngineService broadcasts DealEventDto via IWebSocketBroadcaster on every deal processed. DealerHub routes to "deals" subscribers.
- **BroadcastModels**: Added DealEventDto (dealId, login, symbol, action, volume, price, profit, score, scoreChange, isCorrelated, severity, timeMsc) and ConnectionStatusDto.
- Tested with 2 real accounts (86672693 "Test", 86672696 "test") in group Test\Mak — 35 deals backfilled, open positions showing, real balance/equity displayed
- `dotnet build` — zero warnings, zero errors
- `npx tsc --noEmit` — zero errors

### Phase 5: AI Engines — StyleClassifier, BookRouter, SimulationEngine — ✅ DONE (2026-03-17)
- **StyleClassifier** (TIP.Core/Engines): rule-based trading style classification (Scalper/DayTrader/Swing/EA/Manual/Mixed/Unknown). Multi-signal scoring: hold times, trade frequency, scalp ratio, expert ratio, timing entropy, unique EAs. Returns StyleResult with style, confidence (0-1), and signals list.
- **BookRouter** (TIP.Core/Engines): book routing recommendation engine (ABook/BBook/Hybrid). Weighted scoring: abuse score, ring membership, trading style, profitability, timing precision, volume, expert ratio. Returns BookResult with recommendation, confidence, summary, and risk flags.
- **SimulationEngine** (TIP.Core/Engines): P&L replay for what-if routing analysis. Simulates A-Book (commission only), B-Book (internalize — broker takes opposite side + spread capture), Hybrid (split by volume threshold). Returns timeline for charting + comparison with recommendation.
- **IntelligenceService** (TIP.Api): BackgroundService running every 5 minutes. Classifies style + routes book for all scored accounts. Upserts TraderProfile to DB. Logs "Intelligence cycle complete" with A/B/Hybrid counts.
- **TraderProfileRepository** (TIP.Data): full implementation — UpsertAsync, GetByLoginAsync, InsertScoreHistoryAsync, GetScoreHistoryAsync. Uses Npgsql with ON CONFLICT upsert semantics.
- **IntelligenceController** (TIP.Api): REST endpoints — GET /api/intelligence/profiles (all), GET /api/intelligence/profiles/{login} (single profile with style + book), GET /api/intelligence/profiles/{login}/simulate (3-way routing comparison with timelines).
- **IntelligenceDtos** (TIP.Api/Models): TraderProfileDto, ScoreHistoryDto, SimulationComparisonDto, SimulationResultDto, TimelinePointDto.
- **Frontend — AI Routing tab**: 5th tab in AccountDetail. Style card with badge + confidence bar + signals list. Book recommendation card with badge + confidence + summary + risk flags (red for CRITICAL/Ring). Simulation chart (recharts LineChart) showing cumulative broker P&L for all 3 modes. Summary table: Broker P&L, Commission, Spread Capture, Client P&L, Trades for each mode.
- **Tests**: StyleClassifierTests (8 tests), BookRouterTests (8 tests), SimulationEngineTests (6 tests) — 22 new tests
- `dotnet build` — zero warnings, zero errors
- `npx tsc --noEmit` — zero errors
- All 143 tests passing (121 existing + 22 new)
- **Phases 1-5 COMPLETE.**
- **Next up:** Phase 6 — Hardening, security, load testing

### Pre-Phase 6: Data Persistence + Security Fixes — ✅ DONE (2026-03-17)
- **Fixed tick writing to TimescaleDB** — tick pipeline is correctly wired (dbEnabled=true); ticks will persist when market is open and MT5 TickSink receives data
- **Fixed account metadata persistence** — AccountRepository.Upsert now called in ComputeEngineService on every deal processed; accounts table populated (2 rows verified)
- **Fixed IntelligenceService DB persistence** — trader_profiles and score_history now populated (2 rows each verified); added score_history saving via InsertScoreHistoryAsync
- **Added logging to IntelligenceService** — cycle logs "starting — N accounts", per-account style/book debug logs, exception stack traces not swallowed
- **Secured credentials** — MT5 password and DB password moved to .NET User Secrets via `dotnet user-secrets`
- **appsettings.json now contains only CHANGE_ME placeholders** — safe to commit
- **Added UserSecretsId to TIP.Api.csproj** — explicit user secrets loading in Program.cs
- **Updated .gitignore** — added secrets.json and appsettings.Development.json exclusions
- **Added docs/SETUP.md** — developer onboarding instructions (DB setup, credentials, running)
- Verified: deals=40, accounts=2, trader_profiles=2, score_history=2, ticks=0 (market closed)
- `dotnet build` — zero warnings, zero errors
- 143 tests passing

### Phase 6 Prep: Live Market Watch + Zero-Delay Price Pipeline — ✅ DONE (2026-03-18)
- **PriceCache** (TIP.Api): thread-safe ConcurrentDictionary singleton. Stores CachedPrice (bid, ask, spread, change, changePercent, previousBid, sessionOpenBid). Updated on every tick by PnLEngineService. Read by REST endpoints and WebSocket broadcast.
- **MarketController** (TIP.Api): REST endpoints — GET /api/market/prices (filtered or all), /api/market/status, /api/market/symbols (search all MT5 symbols), /api/market/tick-last (TickLast + TickStat fallback, seeds PriceCache), /api/market/tick-batch (batch pump ticks).
- **SelectedAddAll** — ROOT CAUSE FIX: MT5 Manager API pump only streams ticks for "Selected" symbols. Added `SelectedAddAll()` call in PipelineOrchestrator after Connect() and before TickSubscribe(). This tells the pump to stream ALL symbols. Went from 3 symbols to **1,171 symbols** with live prices.
- **RegisterSink** — CRITICAL FIX: `CIMTDealSink.RegisterSink()` and `CIMTTickSink.RegisterSink()` MUST be called before `DealSubscribe`/`TickSubscribe`. Without this, subscriptions return `MT_RET_ERR_PARAMS` and no ticks/deals flow.
- **IMT5Api expanded**: Added `GetTickLast(symbol)`, `GetTickStat(symbol)` (MTTickStat — session stats), `GetTickLastBatch()` (batch pump ticks), `SelectedAddAll()`. All implemented in MT5ApiReal and MT5ApiSimulator.
- **RawTickStat type**: session-level stats from MTTickStat (bid_high, bid_low, ask_high, ask_low, price_open, price_close, datetime_msc).
- **TickSinkHandler dual overloads**: Both `OnTick(string symbol, MTTickShort tick)` and `OnTick(int feeder, MTTick tick)` overridden in MT5ApiReal for compatibility with different MT5 server versions.
- **DealWriterService duplicate fix**: Catches PostgresException SqlState 23505 on COPY bulk insert, falls back to individual INSERT ON CONFLICT DO NOTHING.
- **PipelineOrchestrator hardened**: 3s PUMP wait after Connect(), SelectedAddAll() before subscribing, 3-attempt retry loop for deal/tick subscriptions with 2s delay, detailed logging.
- **Zero-delay WebSocket prices**: Removed 500ms per-symbol throttle from DealerHub. Every tick from MT5 is pushed instantly to all subscribed browsers.
- **Frontend Market Watch rebuilt**:
  - Default watchlist of 12 key symbols (not all 1171)
  - Add Symbol: search dropdown searches all MT5 symbols by name or description
  - Remove Symbol: × button on each row
  - One-time REST snapshot on load, then pure WebSocket for all live updates (no polling)
  - Auto-reconnect WebSocket with 1s retry
  - Symbols with prices sorted to top by default
  - "No feed" shown for symbols without data
- **Price seeding on startup**: Batch TickLast + individual TickLast/TickStat per symbol + 30s periodic refresh loop.
- **MT5 symbol names on this server**: use `-` suffix (XAUUSD-, EURUSD-, US30-) and also have `.m` and `.c` variants.
- Tested: 1,171 symbols streaming, 25,000+ ticks/min ingested, zero-delay dashboard updates
- `dotnet build` — zero warnings, zero errors
- `npx tsc --noEmit` — zero errors
