# TIP v2.0 Detailed Progress Log

## Phase 1, Task 1: .NET 8 Solution Structure (2026-03-16)
- Created TIP.sln with 5 projects: TIP.Connector, TIP.Core, TIP.Data, TIP.Api, TIP.Tests
- Created web/ React + TypeScript placeholder (Vite)
- Created docs/schema.sql placeholder
- RuleEngine fully implemented (26 metrics, 6 operators, score capping)
- CorrelationEngine index structure implemented
- TradeFingerprint with bucket key logic implemented
- 12 unit tests passing (RuleEngine: 8, TradeFingerprint: 4)
- Program.cs working with Serilog, Channel<T>, CORS, /health endpoint

## Phase 1, Task 2: TimescaleDB Schema (2026-03-16)
- Created docs/schema.sql with 13 tables + 3 continuous aggregates
- Hypertables: ticks (1-day chunks), deals (1-month), score_history (1-month), trading_metrics (1-month)
- Continuous aggregates: candles_1m, candles_1h, candles_1d
- Compression: ticks after 7 days, deals after 30 days, scores after 90 days
- Retention: ticks dropped after 90 days, everything else permanent

## Phase 1, Task 3: MT5 Connector (2026-03-16)
- Created IMT5Api abstraction interface (7 methods + 3 events)
- Created RawTypes.cs (RawDeal, RawTick, RawUser, RawSymbol)
- Created MT5ApiSimulator for dev/testing
- Created MT5ApiReal.cs behind #if MT5_API_AVAILABLE
- Updated MT5Connection.cs — full connect/subscribe/heartbeat/reconnect lifecycle
- Updated HistoryFetcher.cs — rate-limited backfill via IMT5Api
- Updated SyncStateTracker.cs — in-memory checkpoint tracking
- 19 new unit tests (MT5Simulator: 12, DealSink: 7) — 31 total passing

## Phase 1, Task 4: Batch Tick/Deal Writers (2026-03-17)
- Created DbConnectionFactory — NpgsqlDataSource wrapper with connection pooling
- Implemented TickWriter — COPY protocol bulk inserts, thread-safe batching, re-buffer on failure
- Implemented DealRepository — COPY bulk insert, INSERT ON CONFLICT, paginated SELECT
- Created TickWriterService — BackgroundService reading Channel<TickEvent> -> TickWriter with periodic flush
- Created DealWriterService — BackgroundService reading Channel<DealEvent> -> DealRepository with batch flush
- 12 new unit tests — 43 total passing

## Phase 1, Task 5 + Phase 2: Pipeline Orchestration + Deal Processing (2026-03-17)
- PipelineOrchestrator: three-phase startup (Buffer -> Backfill -> Live)
- DealProcessor: classifies deals (17 types), tracks position open/close/modify
- PositionRepository + AccountRepository: CRUD for positions and accounts
- HistoryFetcher: full implementation with rate limiting + progress logging every 10 logins
- SyncStateTracker: database-backed checkpoints with in-memory fallback
- DealSink: added Reset() for reconnect scenarios
- End-to-end pipeline: simulator -> channels -> writers -> DB (or discard)
- 24 new tests — 67 total passing

## Phase 3: Compute Engines (2026-03-17)
- PnLEngine: real-time unrealized P&L via tick-driven recalculation
- CorrelationEngine: full 4-stage algorithm (Index -> Match -> Cluster -> Score), union-find with path compression
- AccountScorer: incremental per-deal scoring, timing entropy, ring correlation integration
- ExposureEngine: per-symbol net exposure aggregation from PnL results
- BotFingerprinter: timing entropy + EA clustering + volume pattern analysis
- PnLEngineService + ComputeEngineService: BackgroundServices for tick/deal pipelines
- ChannelFanOutService: explicit fan-out from main ingest channels to consumer channels
- REST API controller: GET /api/accounts, /api/accounts/{login}, /api/positions, /api/exposure, /api/rings
- 33 new tests — 100 total passing

## Phase 4: Dashboard v2 (2026-03-17)
- DealerHub: WebSocket subscribe pattern with per-client filtering
- BroadcastModels: 4 sealed records for type-safe WS broadcasting
- Frontend rebuilt from scratch: TipStore (Context + useReducer), WebSocket client with auto-reconnect
- AbuseGrid: risk-colored rows, ScoreBadge with trend arrows, 500ms CRITICAL flash
- MarketWatch: live price table with flash on bid change
- AccountDetail: 4 tabs (Deal History, Rule Breakdown, Ring Connections, Trading Metrics)
- PositionsPanel: all open positions with live P&L
- ExposureDashboard: bar chart of net exposure by symbol

## Phase 4.5: Real MT5 Integration (2026-03-17)
- Connected to real MT5 server, auto-connects on startup via appsettings.json
- Real tick subscription via CIMTTickSink handler
- Pipeline fixes: GetUserLogins retry loop, HistoryFetcher date range fix
- Open Positions API, Account Info API, Account Enrichment
- Settings Page: ConnectionManager with connect/disconnect, connection log
- Live Monitor: loads last 24h deals via REST, then WebSocket for real-time
- Tested with 2 real accounts, 35 deals backfilled

## Phase 5: AI Engines (2026-03-17)
- StyleClassifier: rule-based trading style classification (Scalper/DayTrader/Swing/EA/Manual/Mixed/Unknown)
- BookRouter: book routing recommendation engine (ABook/BBook/Hybrid)
- SimulationEngine: P&L replay for what-if routing analysis (A-Book/B-Book/Hybrid)
- IntelligenceService: BackgroundService running every 5 minutes
- TraderProfileRepository: full implementation with ON CONFLICT upsert semantics
- IntelligenceController: REST endpoints for profiles, simulation
- Frontend AI Routing tab: style/book cards, simulation chart
- 22 new tests — 143 total passing

## Pre-Phase 6: Data Persistence + Security Fixes (2026-03-17)
- Fixed tick writing, account metadata persistence, IntelligenceService DB persistence
- Secured credentials via .NET User Secrets
- Added docs/SETUP.md

## Phase 6 Prep: Live Market Watch + Zero-Delay Price Pipeline (2026-03-18)
- PriceCache: thread-safe ConcurrentDictionary singleton
- SelectedAddAll: ROOT CAUSE FIX for MT5 pump symbol streaming (3 -> 1,171 symbols)
- RegisterSink: CRITICAL FIX for CIMTDealSink/CIMTTickSink registration
- IMT5Api expanded: GetTickLast, GetTickStat, GetTickLastBatch, SelectedAddAll
- Zero-delay WebSocket prices (removed 500ms throttle)
- Frontend Market Watch rebuilt with default watchlist, add/remove symbols
- 1,171 symbols streaming, 25,000+ ticks/min ingested

## Phase 6.1: Price Pipeline Overhaul + Startup Warmup (2026-03-18)
- SymbolCache: singleton loaded once on startup (3427 symbols with digits/description/contractSize)
- Removed ALL TickLast/TickStat/batch seeding — tick-only pricing
- Frontend price formatting with actual MT5 digits per symbol
- WebSocket batched RAF updates (~60fps)
- ComputeEngineService DB warmup: 226,889 deals -> 235 scored accounts
- PnLEngine position loading: 15 positions across 3 symbols

## Phase 6, Task 29: Error Handling + Circuit Breakers (2026-03-18)
- CircuitBreaker<T>: generic thread-safe (Closed/Open/HalfOpen)
- ServiceHealthTracker: singleton tracking per-service health
- DB writes + MT5 history wrapped with circuit breakers
- MT5Connection max retries (10 consecutive failures -> CRITICAL)
- GlobalExceptionMiddleware: catches all unhandled exceptions
- BackgroundService crash guards with consecutive error tracking
- DealProcessor input validation
- CorrelationEngine memory guard (maxFingerprints 500,000)
- Frontend ErrorBoundary + WS stale connection detection
- 18 new tests — 161 total passing

## Pre-Task 30: Server-Scoped DB Warmup Fix (2026-03-18)
- Fixed ghost account problem (server-scoped deal filtering)
- DealerHub ObjectDisposedException fix
- CorrelationEngine.IndexedCount thread safety
- BackgroundService cancellation fixes
- 2 new tests — 163 total passing

## Phase 6.2: Full App Walkthrough + SCAN Fix (2026-03-18)
- DealWriterService serverName fix
- SCAN button wired end-to-end
- POST /api/accounts/scan endpoint
- AccountScorer.Reset()

## Phase 6.3: Deal Entry Type Pipeline (2026-03-19)
- MT5 Entry field captured and propagated through full pipeline
- Database: ALTER TABLE deals ADD COLUMN entry
- Frontend LiveMonitor + AccountDetail History show entry type with color coding

## Phase 6.3b: LiveMonitor + History Table UX (2026-03-19)
- LiveMonitor: proper table with sortable column headers + filter
- AccountDetail: history sorting, up to 100 deals
- LiveEvent enriched with price and profit fields

## Sprint 5: Polish + Technical Debt (2026-03-19)
- Moved progress log to docs/CHANGELOG.md, rewrote CLAUDE.md under 150 lines
- Removed tailwindcss + @tailwindcss/vite (unused; all styling is inline CSS)
- Added global index.css (resets, scrollbar, focus styles, table defaults)
- Moved password from useState to useRef in SettingsView (never enters React state)
- Added ConnectionManagerConcurrencyTests (DisconnectRequested thread safety)
- Added EntityType constants to SyncStateTracker (replacing magic strings)
- Added connection string empty-check in Program.cs
- Added regex escaping in LiveMonitor filter
- Added error logging to useWebSocket.onerror
- Created shared parseDeal() utility in web/src/utils/parsers.ts
- 164 tests passing
