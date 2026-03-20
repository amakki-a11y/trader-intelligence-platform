# Trader Intelligence Platform (TIP) v2.0

## Current State
v2.0 Phases 1-6 complete + Sprints 1-5 hardening + server-switch reset + **Auth & Admin system** + **Live P&L**. 168 tests passing. JWT authentication with role-based authorization (admin/dealer/compliance). Separate tip_auth PostgreSQL database. Admin UI for user management and MT5 server management. BCrypt passwords, AES-256 encrypted MT5 credentials, refresh token rotation. Login page, forced password change, admin pages for users/servers/roles. Real-time position P&L via WebSocket (500ms broadcasts). Live equity/margin. MT5 TickStat session HIGH/LOW. Price-aware symbol resolver.

## What is TIP?
Brokerage operations platform for detecting trading abuse on MetaTrader 5. Successor to v1.0 RebateAbuseDetector.

## Architecture

**Channel Fan-Out Topology:**
MT5 -> DealSink/TickSink -> main Channel<T> -> ChannelFanOutService -> { DB writer channels, compute engine channels }

**Three-Phase Startup:**
1. Buffer: DealSink buffers incoming deals during backfill
2. Backfill: HistoryFetcher rate-limited catchup via SyncStateTracker checkpoints
3. Live: DealSink goes direct-to-channel, buffered deals replayed with dedup

**WebSocket Model:**
DealerHub singleton. Clients send `{ "subscribe": ["prices","accounts","positions","alerts","deals"] }`. Per-client filtering. Zero-delay tick push. Batched RAF updates on frontend (~60fps). Position P&L broadcast every 500ms from PnLEngineService. Account Detail subscribes to positions+deals for live P&L, new positions, and close detection.

**Resilience:**
CircuitBreaker<T> wraps DB writes (threshold=5, open=30s) and MT5 history (threshold=3, open=60s). ServiceHealthTracker monitors all BackgroundServices. GlobalExceptionMiddleware catches unhandled exceptions.

## Tech Stack
- **Backend:** .NET 8, ASP.NET Core, Channel<T> pipelines, Serilog, JWT Bearer Auth
- **Database:** TimescaleDB (PostgreSQL) — ticks, deals, accounts, profiles, scores
- **Auth Database:** PostgreSQL (tip_auth) — users, roles, mt5_servers, refresh_tokens
- **Frontend:** React 18 + TypeScript (Vite), inline CSS, recharts, lucide-react
- **Security:** BCrypt (work factor 12), HS256 JWT, AES-256 encryption, httpOnly cookies
- **Testing:** MSTest (168 tests)

## Project Structure
```
TIP.sln
├── src/TIP.Core/        — Pure business logic (zero external deps)
├── src/TIP.Connector/   — MT5 Manager API connection + data pipeline
├── src/TIP.Data/        — TimescaleDB access layer (Npgsql) + Auth DB repositories
├── src/TIP.Api/         — ASP.NET Core host + JWT auth + admin services
├── src/TIP.Tests/       — MSTest unit tests
├── web/                 — React + TypeScript dashboard (Vite)
└── docs/                — Schema, setup, changelog
```

## Dependency Graph
```
TIP.Connector → TIP.Core
TIP.Data      → TIP.Core
TIP.Api       → TIP.Connector, TIP.Core, TIP.Data
TIP.Tests     → TIP.Api, TIP.Connector, TIP.Core, TIP.Data
```

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

## Phase Completion

| Phase | Description | Date | Tests |
|-------|-------------|------|-------|
| 1 | Solution + Schema + Connector + Writers + Pipeline | 2026-03-16/17 | 67 |
| 2 | Deal Processing (merged with Phase 1.5) | 2026-03-17 | 67 |
| 3 | Compute Engines (PnL, Correlation, Scorer, Exposure) | 2026-03-17 | 100 |
| 4 | React Dashboard + DealerHub WebSocket | 2026-03-17 | 100 |
| 4.5 | Real MT5 Integration | 2026-03-17 | 100 |
| 5 | AI Engines (StyleClassifier, BookRouter, Simulation) | 2026-03-17 | 143 |
| 6 | Hardening: circuit breakers, error handling, security | 2026-03-18/19 | 165 |
| S1-5 | Sprints 1-5: critical fixes, backend, frontend, hardening, polish | 2026-03-19 | 165 |
| — | Server-switch reset flow + auto-resolved watchlist | 2026-03-19 | 168 |
| Auth | JWT auth, RBAC, admin UI, user/server management | 2026-03-20 | 168 |
| UI | Settings consolidation, v1/v2 removal, MT5 TickStat session stats, symbol resolver fix | 2026-03-20 | 168 |
| Live | WebSocket P&L (500ms), live equity/margin, deal-aware position lifecycle, date-filtered stats | 2026-03-20 | 168 |

See `docs/CHANGELOG.md` for detailed per-phase progress log.

## What's Next
- **v2.1:** ML-based classification (deferred from v2.0 build order item 10)
- **v2.1:** Multi-server simultaneous connections (DB + UI ready, ConnectionManager refactor pending)
- **v2.1:** Load testing under sustained 25K+ ticks/min
- **v2.1:** Server-scoped data filtering per user (backend filtering by JWT server_id claims)

## Key Decisions Log
1. **Channel<T> over queues** — bounded channels with DropOldest prevent OOM; fan-out service splits to consumers
2. **Tick-only pricing + TickStat for session stats** — live prices from CIMTTickSink only (no TickLast polling). Session HIGH/LOW from MT5 TickStat API via `/api/market/session-stats` endpoint for accurate full-session data
3. **SelectedAddAll()** — MT5 pump only streams "Selected" symbols; must call this before TickSubscribe
4. **RegisterSink before Subscribe** — CIMTDealSink.RegisterSink() MUST precede DealSubscribe or MT_RET_ERR_PARAMS
5. **Server-scoped warmup** — DealRepository filters by server address to prevent ghost accounts from other managers
6. **Inline CSS over Tailwind** — all frontend styling uses inline CSSProperties; tailwindcss removed in Sprint 5
7. **User Secrets for credentials** — MT5 password and DB password never in appsettings.json; CHANGE_ME placeholders only
8. **Three-phase startup** — Buffer/Backfill/Live ensures zero deal loss during reconnect with dedup via DealSink seenIds
9. **Server-switch reset** — PipelineOrchestrator clears all engine state (AccountScorer, PnL, Correlation, Exposure, PriceCache, server names) before connecting to new server. Pre-live warmup reloads positions + replays deals from DB. Callbacks bridge TIP.Connector → TIP.Api without violating dependency graph
10. **Auto-resolved watchlist** — Market Watch resolves base symbol names (EURUSD, US30) to server-specific names at runtime using live price data. Prefers symbols with active tick feeds (e.g., EURUSD- over EURUSD when only the dash variant has a feed). Components stay mounted via CSS display:none to preserve state across navigation
11. **Separate auth database** — tip_auth is plain PostgreSQL (not TimescaleDB) for users, roles, servers, tokens. Different backup/security requirements than timeseries data
12. **JWT + httpOnly cookies** — Access token (15min) in memory, refresh token (7 days) in httpOnly cookie. Token rotation on every refresh. BCrypt work factor 12 for passwords
13. **AES-256 encrypted MT5 passwords** — Manager API passwords encrypted at rest in tip_auth. Key from User Secrets or environment variable, never in config files
14. **Role-based access** — admin/dealer/compliance roles with JSONB permissions array. [RequirePermission] attribute on controllers. Admin UI for user/server management
15. **WebSocket-driven position lifecycle** — Account Detail subscribes to positions+deals channels. P&L updates every 500ms (PnLEngineService broadcast). New positions appear instantly. Partial closes update volume. Full closes detected via deal events (entry=Out/OutBy) trigger REST refetch. Equity = balance + floating P&L, recalculated live
16. **Date-filtered stats** — Account Detail stats cards (Trades, Volume, Commissions, P&L, Deposits) computed from loaded deals for selected date range, not from all-time scorer data
