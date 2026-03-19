# Trader Intelligence Platform (TIP) v2.0

## Current State
v2.0 Phases 1-6 complete + Sprints 1-5 hardening done. 165 tests passing. 2 real accounts scored from 59 historical deals. 3,428 MT5 symbols cached, live tick streaming. Tick-only pricing. Data persisting to TimescaleDB. Credentials in User Secrets.

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
DealerHub singleton. Clients send `{ "subscribe": ["prices","accounts","positions","alerts","deals"] }`. Per-client filtering. Zero-delay tick push. Batched RAF updates on frontend (~60fps).

**Resilience:**
CircuitBreaker<T> wraps DB writes (threshold=5, open=30s) and MT5 history (threshold=3, open=60s). ServiceHealthTracker monitors all BackgroundServices. GlobalExceptionMiddleware catches unhandled exceptions.

## Tech Stack
- **Backend:** .NET 8, ASP.NET Core, Channel<T> pipelines, Serilog
- **Database:** TimescaleDB (PostgreSQL) — ticks, deals, accounts, profiles, scores
- **Frontend:** React 18 + TypeScript (Vite), inline CSS, recharts, lucide-react
- **Testing:** MSTest (165 tests)

## Project Structure
```
TIP.sln
├── src/TIP.Core/        — Pure business logic (zero external deps)
├── src/TIP.Connector/   — MT5 Manager API connection + data pipeline
├── src/TIP.Data/        — TimescaleDB access layer (Npgsql)
├── src/TIP.Api/         — ASP.NET Core host
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

See `docs/CHANGELOG.md` for detailed per-phase progress log.

## What's Next
- **v2.1:** ML-based classification (deferred from v2.0 build order item 10)
- **v2.1:** Multi-server support (Stage 3 — config structure already supports Servers array)
- **v2.1:** Load testing under sustained 25K+ ticks/min

## Key Decisions Log
1. **Channel<T> over queues** — bounded channels with DropOldest prevent OOM; fan-out service splits to consumers
2. **Tick-only pricing** — removed all TickLast/TickStat polling; CIMTTickSink is the single source of truth
3. **SelectedAddAll()** — MT5 pump only streams "Selected" symbols; must call this before TickSubscribe
4. **RegisterSink before Subscribe** — CIMTDealSink.RegisterSink() MUST precede DealSubscribe or MT_RET_ERR_PARAMS
5. **Server-scoped warmup** — DealRepository filters by server address to prevent ghost accounts from other managers
6. **Inline CSS over Tailwind** — all frontend styling uses inline CSSProperties; tailwindcss removed in Sprint 5
7. **User Secrets for credentials** — MT5 password and DB password never in appsettings.json; CHANGE_ME placeholders only
8. **Three-phase startup** — Buffer/Backfill/Live ensures zero deal loss during reconnect with dedup via DealSink seenIds
