# Trader Intelligence Platform

## What this is
A distributed brokerage operations platform that connects to MetaTrader 5 servers via the Manager API and provides real-time dealing desk intelligence — abuse detection, P&L calculation, book management through AI, compliance reporting, and multi-server monitoring.

**Core principle:** MT5 is a data source only. One connection in, raw data out. Every calculation, every aggregation, every dashboard update is powered by our server, our database, our code. MT5's job is done once it fires the callback.

**Vision:** A complete platform where a brokerage can rely on it for real-time P&L, abuser detection, trader profiling, A-Book/B-Book routing through AI with simulation, compliance reporting, and multi-server monitoring — all from one system, licensable to multiple brokers.

**Current state:** v1.0 complete (WinForms abuse scanner — see `rebate-abuse-detector` repo). v2.0 in development (distributed intelligence platform — this repo).

**Licensing model:** Designed to be rented/licensed to external brokers.

## Tech Stack

| Layer | Technology | Purpose |
|-------|-----------|--------|
| MT5 Connection | C# / .NET 8+ (Windows) | Manager API integration (native DLLs, callbacks) |
| Message Transport | System.Threading.Channels (Stage 1) → Redis Streams (Stage 2+) | In-process event streaming, upgradable to durable transport |
| Price Cache | ConcurrentDictionary (Stage 1) → Redis Hash (Stage 2+) | Latest price per symbol for instant snapshots |
| Application Server | C# / ASP.NET Core (.NET 8+) | Compute engines, REST API, WebSocket server |
| Database | TimescaleDB (PostgreSQL + extension) | Tick storage, deals, candles, positions, trader profiles |
| Frontend | React + TypeScript | Dealer dashboard (modeled after v1.0 WinForms UI) |
| Real-time Delivery | WebSocket (ASP.NET Core) | Live prices, deals, P&L, alerts to dashboard |
| Logging | Serilog (structured, daily rotation) | Audit trail, error tracking, compliance |

## Design Principles
- **MT5 is a data source, not a compute engine** — event-driven callbacks only, no polling, no repeated queries. Zero load on the MT5 server after initial sync.
- **Expandable architecture** — every feature is a pluggable module. New detection engines, new data sources, new UI panels can be added without rewriting existing code.
- **Live and instant** — zero perceptible delay on prices, deals, and dashboard updates. < 50ms tick-to-dashboard latency target.
- **Start simple, evolve to distributed** — Stage 1 runs as a monolith on one Windows VPS. Same code splits into microservices when scaling demands it.
- **Multi-tenant ready** — broker-specific configuration cleanly separated. No hardcoded assumptions.
- **Multi-server support** — connect to multiple MT5 servers simultaneously with symbol mapping.
- **Security first** — encrypted data at rest, no plaintext credentials, role-based access control, immutable audit trail.
- **Resilient operations** — auto-reconnect on connection loss, no silent failures, structured logging, database backup and corruption recovery.
- **Persist everything** — every tick and every deal stored permanently for audit, compliance, dispute resolution, and analytics.

## Evolution Path

### Stage 1: Monolith (start here)
One C# .NET 8 application on your Windows VPS. Connector + compute + API in one process. TimescaleDB runs locally (PostgreSQL). No Redis — use System.Threading.Channels in-memory. React dashboard connects via WebSocket from same machine.

### Stage 2: Split connector from compute
Extract MT5 connector into a Windows Service (thin pipe). Add Redis between connector and app server for durability. App server can now run on Linux VPS with TimescaleDB.

### Stage 3: Full distributed (the target architecture)
Windows Connector → Redis → App Server → TimescaleDB → React. Multi-server MT5 connections. Horizontal scaling. Each layer independently deployable.

**The code you write in Stage 1 moves unchanged into Stage 2 and 3.** The interfaces stay the same — only the transport changes.

## GitHub
- This repo: https://github.com/amakki-a11y/trader-intelligence-platform
- Legacy v1.0: https://github.com/amakki-a11y/rebate-abuse-detector

---

## Architecture Overview

```
MT5 SERVER (read-only connection per server)
    | OnTick callback        | OnDealAdd / OnDealUpdate
    | TickHistoryRequest     | DealRequest (initial sync)
    v                        v
MT5 CONNECTOR (C# .NET 8 BackgroundService)
    | Tick Listener (live)   | Deal Listener (live)   | History Fetcher (backfill)
    +------------------------+------------------------+
                      |
              Unified Event Stream (Channel<T> -> Redis later)
                      |
          +-----------+-----------+
          v                       v
    WebSocket Server        Compute Engines
    (live to React)         (P&L, Risk, Abuse)
          |                       |
          +-----------+-----------+
                      v
              TimescaleDB
    ticks | deals | positions | accounts | candles
    trader_profiles | score_history | audit_log
```

---

## MT5 Connector — Critical Design Rules
- **NO polling** — exclusively event-driven callbacks (OnTick, OnDealAdd, OnDealUpdate)
- **NO computation** — raw data forwarding only
- **NO business logic** — this is a data pipe
- Rate-limit history requests: max 2 concurrent, 500ms delay between batches
- Graceful reconnection: exponential backoff (1s, 2s, 4s, 8s... max 60s)
- On reconnect: automatically trigger catch-up sync to fill any gap
- Health heartbeat: emit status every 10 seconds
- CIMTDeal lifetime rule: copy ALL deal fields inside the callback immediately — the native object is invalid after the callback returns

### Manager API Methods Used
```
// Live streaming (callbacks — zero polling)
CIMTDealSink.OnDealAdd      -> new deals (instant)
CIMTDealSink.OnDealUpdate   -> deal modifications
OnTick                       -> live price ticks

// History backfill (one-time or catch-up)
TickHistoryRequest           -> historical ticks per symbol + date range
DealRequest                  -> historical deals per login + date range

// Reference data
SymbolTotal / SymbolNext     -> symbol list
UserRequest                  -> login/account details
OnlineRequest                -> connected clients with IP addresses
```

---

## Three-Phase Sync Strategy

### Phase 1: Initial Backfill (first deployment)
- For each symbol: TickHistoryRequest chunked by day, oldest to newest
- For each login: DealRequest chunked by date range
- Rate limit: max 2 concurrent requests, 500ms delay between chunks
- Batch INSERT with ON CONFLICT DO NOTHING for deduplication
- Track progress in sync_state table

### Phase 2: Catch-Up (every restart)
- Query sync_state for latest synced timestamp per entity
- Request only data after that timestamp from MT5
- Fill the gap, then transition to Phase 3

### Phase 3: Live Stream (steady state)
- OnTick -> Channel -> Batch Tick Writer -> TimescaleDB
- OnDealAdd -> Channel -> Deal Processor -> TimescaleDB
- **No more MT5 queries after this point** — everything flows through callbacks
- sync_state updated continuously as checkpoint

### Startup Sequence (the buffer pattern)
1. Connect to MT5, record cutoffTime = now
2. Subscribe deal sink in BUFFER mode (incoming deals stored in list, not processed)
3. Background thread: load history up to cutoffTime
4. History complete -> run correlation engine on historical data
5. Replay buffered deals (skip duplicates via HashSet of seen deal IDs)
6. Switch sink to LIVE mode -> all new deals processed immediately
7. Zero gap, zero duplicates, seamless transition

---

## Four-Threat Detection Model
The platform detects 4 interconnected abuse patterns:
1. **Ring trading** — coordinated accounts trading opposite directions to guarantee rebates
2. **Latency arbitrage** — exploiting price feed delays for risk-free profit
3. **Bonus abuse** — deposit->bonus->min-trade->withdraw cycle across accounts
4. **Bot farming** — EA generating micro-trades purely for commission volume

### Three Detection Engines
All feed into one unified AbuseScore (0-100):
1. **Enhanced Rule Engine** — ~23 single-account metrics (extends v1.0's 11 rules + 12 new)
2. **Correlation Engine** — cross-account ring detection via 4-stage algorithm (fingerprint -> index -> sliding window match -> cluster)
3. **Bot Fingerprinter** — timing entropy (CV), ExpertID clustering, volume pattern analysis

### AI-Powered Intelligence Layer
- **Book routing suggestions** — A-Book/B-Book/Hybrid based on trader profile
- **Continuous behavior monitoring** — AI watches every trade, alerts on pattern changes
- **Demo simulation mode** — "What if we had A-Booked this client 30 days ago?" with P&L replay
- **AI architecture:** (1) Statistical analysis (local), (2) Rule-based heuristics (local), (3) LLM features (API, optional), (4) Simulation engine (local). App must function fully without API access.

---

## Dealer Dashboard (React) — UI Design Spec
The v1.0 WinForms interface is the design blueprint. The React dashboard must feel immediately familiar to dealers who used v1.0:

- **Grid with color-coded rows:** CRITICAL (red flash at 500ms), HIGH (orange), MEDIUM (yellow), LOW (white)
- **Score column:** 0-100 abuse score with trend arrow and velocity coloring
- **Risk level:** CRITICAL >= 70, HIGH >= 50, MEDIUM >= 30, LOW < 30
- **Double-click drill-down:** Account detail view with deal history, rule breakdown, ring connections, AI recommendation
- **Sort by severity:** CRITICAL first, then score descending
- **Live updates:** Grid rows update in-place as new deals arrive — no page refresh
- **Event log:** Colored entries (dark background) showing real-time deal activity

### Additional views (beyond v1.0):
- Market Watch (live prices grid from OnTick)
- Open Positions with real-time P&L (calculated server-side)
- Risk / Exposure dashboard (net exposure per symbol, per group, per book)
- Historical charts (candles from TimescaleDB continuous aggregates)
- Alert work queue (New -> In Review -> Escalated -> Resolved)
- Compliance report generator (SAR/STR evidence packages)

---

## Database Schema (TimescaleDB)

### Core tables: ticks, deals, positions, accounts, symbols, sync_state
(Full SQL schema in /docs/schema.sql)

### Continuous aggregates (auto OHLCV)
- candles_1m (from ticks), candles_1h (from 1m), candles_1d (from 1h)

### Trader Intelligence tables:
- **trader_profiles** — identity, trading style, book routing, confidence score, server address
- **score_history** — abuse score evolution over time with triggered rules
- **trading_metrics** — periodic style fingerprint (hold time, win rate, entropy, P&L)
- **audit_log** — immutable dealer action log (append-only, exportable for compliance)
- **symbol_mapping** — cross-server symbol normalization (canonical -> server-specific)

### Storage Estimates:
- 50 symbols x 5 ticks/sec = ~21.6M ticks/day
- ~1.7 GB/day uncompressed -> ~170 MB/day after TimescaleDB compression (7-day policy)
- ~62 GB/year compressed. Plan for 500 GB SSD minimum.

---

## Security & Data Protection
- **Encryption at rest:** TimescaleDB with encrypted tablespace or DPAPI
- **Credential storage:** Windows Credential Manager or environment variables, never in code
- **Role-based access:** Viewer, Dealer, Compliance, Admin
- **Audit trail:** Every dealer action logged immutably, exportable for regulatory audits
- **Database backup:** Daily pg_dump + continuous WAL archiving
- **WebSocket auth:** JWT on connection handshake
- **API security:** Rate-limited, authenticated, input-validated

## Compliance & Regulatory
- **Audit trail:** Required by CySEC, FCA, ASIC, FINRA for AML compliance
- **SAR/STR reporting:** Generate evidence packages per flagged account
- **Record retention:** 5-7 years minimum (deal history, scores, audit logs)
- **MiFID II/III:** Trading metrics and book routing decisions exportable as best execution evidence
- **AML monitoring:** Abuse detection signals overlap with AML red flags

## Connection Resilience
- **Auto-reconnect:** Exponential backoff (1s -> 60s max), visible status indicator
- **Gap recovery:** On reconnect, catch-up sync fills missed deals/ticks
- **Per-server health:** Independent status per MT5 connection (Connected / Reconnecting / Failed)
- **No silent failures:** Every exception logged with stack trace and context
- **Database safety:** WAL mode, integrity checks on startup, daily backups

## Performance Targets

| Metric | Target |
|--------|--------|
| Tick-to-dashboard latency | < 50ms |
| Deal event processing | < 100ms end-to-end |
| Tick batch write throughput | > 5,000 ticks/second |
| P&L recalculation | < 10ms per position |
| Historical tick query (1 day) | < 500ms |
| Dashboard initial load | < 2 seconds |
| MT5 server CPU impact | Near zero |

---

## Build Order

### Phase 1: Foundation (Weeks 1-2)
1. [ ] .NET 8 solution structure (TIP.Connector, TIP.Core, TIP.Api, TIP.Web)
2. [ ] TimescaleDB schema (ticks, deals, positions, accounts, symbols, sync_state)
3. [ ] MT5 Connector: CIMTDealSink + OnTick callbacks -> Channel<T>
4. [ ] Batch tick writer (Channel -> TimescaleDB bulk INSERT)
5. [ ] Three-phase sync with buffer pattern

### Phase 2: Deal Pipeline (Weeks 3-4)
6. [ ] Deal processor: Channel -> deals table + position state updates
7. [ ] History backfill (DealRequest per login, TickHistoryRequest per symbol)
8. [ ] sync_state tracking and catch-up logic
9. [ ] Connection resilience (auto-reconnect, health monitoring)

### Phase 3: Compute Engines (Weeks 5-6)
10. [ ] P&L engine (tick price x position data)
11. [ ] Correlation engine (4-stage ring detection — ported from v1.0 design)
12. [ ] Enhanced rule engine (23 metrics — ported from v1.0 + new)
13. [ ] Exposure calculator (net exposure by symbol, group, book)
14. [ ] TimescaleDB continuous aggregates (1m, 1h, 1d candles)

### Phase 4: Dashboard (Weeks 7-8)
15. [ ] React project with WebSocket client
16. [ ] Market Watch (live price grid — modeled on v1.0 grid)
17. [ ] Deal Blotter (live + historical)
18. [ ] Abuse Detection grid (color-coded rows, score, risk level — v1.0 design)
19. [ ] Account detail drill-down (deal history, rule breakdown, ring links)
20. [ ] Open Positions with real-time P&L

### Phase 5: Intelligence Layer (Weeks 9-12)
21. [ ] Trader profile database (style fingerprinting, score history)
22. [ ] Trading style classification engine
23. [ ] AI book routing suggestions (A-Book / B-Book / Hybrid)
24. [ ] AI simulation mode (P&L replay with alternative routing)
25. [ ] Alert work queue (investigation workflow)
26. [ ] Compliance report generator (SAR/STR packages)
27. [ ] Dealer action buttons (Disable Trading, Change Group via MT5 API)

### Phase 6: Hardening (Weeks 13-14)
28. [ ] Unit test suite (MSTest covering all detection engines)
29. [ ] Error handling and circuit breakers
30. [ ] Security audit (credentials, auth, TLS, input validation)
31. [ ] Load testing (100+ symbols, 10k ticks/sec)
32. [ ] Backup and disaster recovery testing
33. [ ] Multi-server support + symbol mapping

---

## Project Structure
```
trader-intelligence-platform/
|-- CLAUDE.md                          # This file — project bible
|-- docs/
|   |-- schema.sql                     # TimescaleDB full schema
|   |-- architecture.md                # Detailed architecture guide
|   |-- live_data_pipeline.html        # Interactive pipeline walkthrough
|   +-- four_threats_unified.html      # Interactive threat analysis
|-- src/
|   |-- TIP.Connector/                 # MT5 connection + data pipe
|   |   |-- MT5Connection.cs
|   |   |-- DealSink.cs
|   |   |-- TickListener.cs
|   |   |-- HistoryFetcher.cs
|   |   |-- ConnectionManager.cs
|   |   +-- SyncStateTracker.cs
|   |-- TIP.Core/                      # Business logic (no UI, no infra)
|   |   |-- Engines/
|   |   |   |-- RuleEngine.cs
|   |   |   |-- CorrelationEngine.cs
|   |   |   |-- BotFingerprinter.cs
|   |   |   |-- PnLEngine.cs
|   |   |   |-- ExposureEngine.cs
|   |   |   +-- SimulationEngine.cs
|   |   |-- Models/
|   |   |   |-- TradeFingerprint.cs
|   |   |   |-- AccountAnalysis.cs
|   |   |   |-- TraderProfile.cs
|   |   |   +-- DealRecord.cs
|   |   +-- AI/
|   |       |-- StyleClassifier.cs
|   |       +-- BookRouter.cs
|   |-- TIP.Data/                      # Database access layer
|   |   |-- TimescaleContext.cs
|   |   |-- TickWriter.cs
|   |   |-- DealRepository.cs
|   |   +-- TraderProfileRepo.cs
|   |-- TIP.Api/                       # ASP.NET Core host
|   |   |-- Program.cs
|   |   |-- WebSocketHub.cs
|   |   |-- Controllers/
|   |   +-- BackgroundServices/
|   +-- TIP.Tests/
|-- web/                               # React frontend
|   |-- src/components/
|   |   |-- MarketWatch.tsx
|   |   |-- AbuseGrid.tsx
|   |   |-- DealBlotter.tsx
|   |   |-- AccountDetail.tsx
|   |   |-- PositionsPanel.tsx
|   |   +-- AlertQueue.tsx
|   +-- package.json
+-- .gitignore
```

## Legacy Reference
The v1.0 WinForms application (rebate-abuse-detector repo) contains:
- Working MT5 Manager API connection logic (Connect, DealRequest, UserRequest)
- 11-rule configurable scoring engine with 25 metrics
- Correlation engine algorithm design (4-stage: fingerprint -> index -> match -> cluster)
- Proven dealer UX patterns (grid, colors, drill-down, flash alerts)
- All detection logic carries forward conceptually into this platform
