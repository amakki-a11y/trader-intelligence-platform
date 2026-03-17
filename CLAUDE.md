# Trader Intelligence Platform (TIP) v2.0

## Current state
v2.0 in development — Phase 1 in progress (solution scaffolded, schema done, MT5 connector next).

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
TIP.Tests     → TIP.Connector, TIP.Core, TIP.Data
```

## Build Order
1. [x] .NET 8 solution structure ✅ (completed 2026-03-16)
2. [x] TimescaleDB schema ✅ (completed 2026-03-16)
3. [ ] MT5 connection pipeline
4. [ ] React dashboard
5. [ ] AI engines (StyleClassifier, BookRouter, SimulationEngine)
6. [ ] ML-based classification

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
