-- ============================================================================
-- Trader Intelligence Platform (TIP) — TimescaleDB Schema
-- ============================================================================
-- Target: PostgreSQL 16 + TimescaleDB extension
-- Database: tip
--
-- Storage Estimates:
-- 50 symbols × 5 ticks/sec = 250 ticks/sec = 21.6M ticks/day
-- Tick row: ~60 bytes → 1.3 GB/day uncompressed
-- After TimescaleDB compression (7-day policy): ~130 MB/day
-- After 90-day retention: max ~12 GB ticks on disk
-- Deals: ~50K/day × ~200 bytes = ~10 MB/day (kept permanently)
-- Candle aggregates: negligible storage
-- Total year 1 estimate: ~15 GB ticks + ~3.6 GB deals + ~1 GB other = ~20 GB
-- Recommended: 100 GB SSD minimum, 500 GB for growth
-- ============================================================================

-- Enable TimescaleDB
CREATE EXTENSION IF NOT EXISTS timescaledb;

-- ============================================================================
-- 1. CORE TABLES
-- ============================================================================

-- ticks: Every price tick from OnTick callback
-- Feeds: MarketWatch.tsx (latest prices), CandleChart.tsx (via continuous aggregates), TickWriter.cs
CREATE TABLE ticks (
    time        TIMESTAMPTZ       NOT NULL,
    time_msc    BIGINT            NOT NULL,
    symbol      TEXT              NOT NULL,
    bid         DOUBLE PRECISION  NOT NULL,
    ask         DOUBLE PRECISION  NOT NULL,
    server      TEXT              NOT NULL DEFAULT ''
);

-- deals: Every trade and balance operation from CIMTDealSink
-- Feeds: AbuseGrid.tsx, AccountDetail.tsx, CorrelationEngine.cs, RuleEngine.cs, DealRepository.cs
CREATE TABLE deals (
    deal_id     BIGINT            NOT NULL,
    login       BIGINT            NOT NULL,
    time        TIMESTAMPTZ       NOT NULL,
    time_msc    BIGINT            NOT NULL,
    symbol      TEXT              NOT NULL DEFAULT '',
    action      SMALLINT          NOT NULL,
    volume      DOUBLE PRECISION  NOT NULL DEFAULT 0,
    price       DOUBLE PRECISION  NOT NULL DEFAULT 0,
    profit      DOUBLE PRECISION  NOT NULL DEFAULT 0,
    commission  DOUBLE PRECISION  NOT NULL DEFAULT 0,
    swap        DOUBLE PRECISION  NOT NULL DEFAULT 0,
    fee         DOUBLE PRECISION  NOT NULL DEFAULT 0,
    reason      SMALLINT          NOT NULL DEFAULT 0,
    expert_id   BIGINT            NOT NULL DEFAULT 0,
    comment     TEXT              NOT NULL DEFAULT '',
    position_id BIGINT            NOT NULL DEFAULT 0,
    server      TEXT              NOT NULL DEFAULT ''
);

-- positions: Current open positions (live state table, not a hypertable)
-- Feeds: PositionsPanel.tsx, ExposureDashboard.tsx, PnLEngine.cs
CREATE TABLE positions (
    position_id    BIGINT            NOT NULL,
    login          BIGINT            NOT NULL,
    symbol         TEXT              NOT NULL,
    direction      SMALLINT          NOT NULL,
    volume         DOUBLE PRECISION  NOT NULL,
    open_price     DOUBLE PRECISION  NOT NULL,
    open_time      TIMESTAMPTZ       NOT NULL,
    current_price  DOUBLE PRECISION  NOT NULL DEFAULT 0,
    unrealized_pnl DOUBLE PRECISION  NOT NULL DEFAULT 0,
    swap           DOUBLE PRECISION  NOT NULL DEFAULT 0,
    margin         DOUBLE PRECISION  NOT NULL DEFAULT 0,
    server         TEXT              NOT NULL DEFAULT '',
    updated_at     TIMESTAMPTZ       NOT NULL DEFAULT NOW(),
    PRIMARY KEY (position_id, server)
);

-- accounts: Account reference data from MT5 UserRequest
-- Feeds: AbuseGrid.tsx (Login, Name, Group columns), AccountDetail.tsx header
CREATE TABLE accounts (
    login             BIGINT            NOT NULL,
    name              TEXT              NOT NULL DEFAULT '',
    group_name        TEXT              NOT NULL DEFAULT '',
    leverage          INT               NOT NULL DEFAULT 0,
    balance           DOUBLE PRECISION  NOT NULL DEFAULT 0,
    equity            DOUBLE PRECISION  NOT NULL DEFAULT 0,
    margin            DOUBLE PRECISION  NOT NULL DEFAULT 0,
    free_margin       DOUBLE PRECISION  NOT NULL DEFAULT 0,
    currency          TEXT              NOT NULL DEFAULT 'USD',
    ib_agent          BIGINT            NOT NULL DEFAULT 0,
    registration_time TIMESTAMPTZ,
    server            TEXT              NOT NULL DEFAULT '',
    updated_at        TIMESTAMPTZ       NOT NULL DEFAULT NOW(),
    PRIMARY KEY (login, server)
);

-- symbols: Reference data for all tradeable instruments
-- Feeds: PnLEngine.cs (contract size for P&L calc), MarketWatch.tsx (digits for display)
CREATE TABLE symbols (
    symbol          TEXT              NOT NULL,
    description     TEXT              NOT NULL DEFAULT '',
    digits          INT               NOT NULL DEFAULT 5,
    contract_size   DOUBLE PRECISION  NOT NULL DEFAULT 100000,
    tick_size       DOUBLE PRECISION  NOT NULL DEFAULT 0.00001,
    tick_value      DOUBLE PRECISION  NOT NULL DEFAULT 1,
    currency_base   TEXT              NOT NULL DEFAULT '',
    currency_profit TEXT              NOT NULL DEFAULT '',
    server          TEXT              NOT NULL DEFAULT '',
    PRIMARY KEY (symbol, server)
);

-- sync_state: Three-phase sync checkpoint tracking
-- Feeds: SyncStateTracker.cs, HistoryFetcher.cs
CREATE TABLE sync_state (
    entity_type    TEXT        NOT NULL,
    entity_id      TEXT        NOT NULL,
    last_sync      TIMESTAMPTZ NOT NULL,
    records_synced BIGINT      NOT NULL DEFAULT 0,
    server         TEXT        NOT NULL DEFAULT '',
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (entity_type, entity_id, server)
);

-- ============================================================================
-- 2. INTELLIGENCE TABLES
-- ============================================================================

-- trader_profiles: Persistent trader identity and classification
-- Feeds: AccountDetail.tsx (header + AI panel), StyleClassifier.cs, BookRouter.cs
CREATE TABLE trader_profiles (
    login                BIGINT            NOT NULL,
    name                 TEXT              NOT NULL DEFAULT '',
    group_name           TEXT              NOT NULL DEFAULT '',
    server               TEXT              NOT NULL DEFAULT '',
    ib_agent             BIGINT            NOT NULL DEFAULT 0,
    trading_style        TEXT              NOT NULL DEFAULT 'Unknown',
    book_routing         TEXT              NOT NULL DEFAULT 'Unknown',
    routing_confidence   DOUBLE PRECISION  NOT NULL DEFAULT 0,
    first_seen           TIMESTAMPTZ       NOT NULL DEFAULT NOW(),
    last_updated         TIMESTAMPTZ       NOT NULL DEFAULT NOW(),
    PRIMARY KEY (login, server)
);

-- score_history: Abuse score snapshots over time
-- Feeds: AccountDetail.tsx (score history chart), AbuseGrid.tsx (trend arrows)
CREATE TABLE score_history (
    login             BIGINT            NOT NULL,
    scored_at         TIMESTAMPTZ       NOT NULL,
    abuse_score       DOUBLE PRECISION  NOT NULL,
    risk_level        TEXT              NOT NULL,
    is_ring_member    BOOLEAN           NOT NULL DEFAULT FALSE,
    correlation_count INT               NOT NULL DEFAULT 0,
    linked_logins     JSONB             NOT NULL DEFAULT '[]',
    triggered_rules   JSONB             NOT NULL DEFAULT '{}',
    server            TEXT              NOT NULL DEFAULT ''
);

-- trading_metrics: Periodic style fingerprint snapshots
-- Feeds: AccountDetail.tsx (metric cards), StyleClassifier.cs, BookRouter.cs
CREATE TABLE trading_metrics (
    login               BIGINT            NOT NULL,
    period_start        TIMESTAMPTZ       NOT NULL,
    period_end          TIMESTAMPTZ       NOT NULL,
    avg_hold_seconds    DOUBLE PRECISION  NOT NULL DEFAULT 0,
    trades_per_day      DOUBLE PRECISION  NOT NULL DEFAULT 0,
    avg_volume_lots     DOUBLE PRECISION  NOT NULL DEFAULT 0,
    win_rate            DOUBLE PRECISION  NOT NULL DEFAULT 0,
    expert_trade_ratio  DOUBLE PRECISION  NOT NULL DEFAULT 0,
    timing_entropy_cv   DOUBLE PRECISION  NOT NULL DEFAULT 0,
    pnl_per_trade       DOUBLE PRECISION  NOT NULL DEFAULT 0,
    scalp_count         INT               NOT NULL DEFAULT 0,
    total_trades        INT               NOT NULL DEFAULT 0,
    deposit_total       DOUBLE PRECISION  NOT NULL DEFAULT 0,
    withdrawal_total    DOUBLE PRECISION  NOT NULL DEFAULT 0,
    net_pnl             DOUBLE PRECISION  NOT NULL DEFAULT 0,
    top_symbols         JSONB             NOT NULL DEFAULT '[]',
    server              TEXT              NOT NULL DEFAULT ''
);

-- ============================================================================
-- 3. OPERATIONAL TABLES
-- ============================================================================

-- audit_log: Immutable dealer action log (APPEND-ONLY — no updates or deletes)
-- Feeds: AccountDetail.tsx (audit tab), compliance report generator, dealer action buttons
CREATE TABLE audit_log (
    id              BIGSERIAL         PRIMARY KEY,
    logged_at       TIMESTAMPTZ       NOT NULL DEFAULT NOW(),
    dealer_login    TEXT              NOT NULL,
    action_type     TEXT              NOT NULL,
    target_login    BIGINT            NOT NULL,
    reason          TEXT              NOT NULL DEFAULT '',
    previous_score  DOUBLE PRECISION,
    current_score   DOUBLE PRECISION,
    metadata        JSONB             NOT NULL DEFAULT '{}'
);

-- alerts: Investigation workflow tracking
-- Feeds: AlertQueue.tsx (investigation workflow), time-to-action tracking
CREATE TABLE alerts (
    id               BIGSERIAL         PRIMARY KEY,
    login            BIGINT            NOT NULL,
    server           TEXT              NOT NULL DEFAULT '',
    state            TEXT              NOT NULL DEFAULT 'New',
    severity         TEXT              NOT NULL,
    abuse_score      DOUBLE PRECISION  NOT NULL,
    triggered_rules  JSONB             NOT NULL DEFAULT '{}',
    assigned_to      TEXT,
    created_at       TIMESTAMPTZ       NOT NULL DEFAULT NOW(),
    updated_at       TIMESTAMPTZ       NOT NULL DEFAULT NOW(),
    resolved_at      TIMESTAMPTZ,
    resolution_notes TEXT
);

-- symbol_mapping: Cross-server symbol normalization
-- Feeds: CorrelationEngine.cs (cross-server matching), MarketWatch.tsx (display)
CREATE TABLE symbol_mapping (
    canonical_symbol TEXT              NOT NULL,
    server           TEXT              NOT NULL,
    server_symbol    TEXT              NOT NULL,
    pip_multiplier   DOUBLE PRECISION  NOT NULL DEFAULT 1,
    PRIMARY KEY (server, server_symbol)
);

-- correlation_pairs: Ring detection evidence
-- Feeds: AccountDetail.tsx (ring connections graph), CorrelationEngine.cs
CREATE TABLE correlation_pairs (
    login_a          BIGINT            NOT NULL,
    login_b          BIGINT            NOT NULL,
    symbol           TEXT              NOT NULL,
    match_count      INT               NOT NULL DEFAULT 0,
    shared_expert_ids JSONB            NOT NULL DEFAULT '[]',
    shared_ib        BOOLEAN           NOT NULL DEFAULT FALSE,
    first_seen       TIMESTAMPTZ       NOT NULL DEFAULT NOW(),
    last_seen        TIMESTAMPTZ       NOT NULL DEFAULT NOW(),
    server           TEXT              NOT NULL DEFAULT '',
    PRIMARY KEY (login_a, login_b, server),
    CONSTRAINT correlation_pairs_ordered CHECK (login_a < login_b)
);

-- ============================================================================
-- 4. HYPERTABLE CREATION
-- ============================================================================

SELECT create_hypertable('ticks', 'time', chunk_time_interval => INTERVAL '1 day');
SELECT create_hypertable('deals', 'time', chunk_time_interval => INTERVAL '1 month');
SELECT create_hypertable('score_history', 'scored_at', chunk_time_interval => INTERVAL '1 month');
SELECT create_hypertable('trading_metrics', 'period_start', chunk_time_interval => INTERVAL '1 month');

-- Unique constraint on deals (must be created after hypertable conversion)
-- deal_id is unique per server only
CREATE UNIQUE INDEX deals_deal_id_server_unique ON deals (deal_id, server, time);

-- ============================================================================
-- 5. INDEXES
-- ============================================================================

-- ticks indexes
CREATE INDEX ix_ticks_symbol_time ON ticks (symbol, time DESC);

-- deals indexes
CREATE INDEX ix_deals_login_time ON deals (login, time DESC);
CREATE INDEX ix_deals_symbol_time_msc ON deals (symbol, time_msc);
CREATE INDEX ix_deals_expert_id ON deals (expert_id) WHERE expert_id != 0;
CREATE INDEX ix_deals_position_id ON deals (position_id) WHERE position_id != 0;

-- positions indexes
CREATE INDEX ix_positions_login ON positions (login);
CREATE INDEX ix_positions_symbol ON positions (symbol);

-- accounts indexes
CREATE INDEX ix_accounts_group_name ON accounts (group_name);

-- score_history indexes
CREATE INDEX ix_score_history_login_scored_at ON score_history (login, scored_at DESC);

-- trading_metrics indexes
CREATE INDEX ix_trading_metrics_login_period ON trading_metrics (login, period_start DESC);

-- audit_log indexes
CREATE INDEX ix_audit_log_target_login ON audit_log (target_login, logged_at DESC);
CREATE INDEX ix_audit_log_dealer_login ON audit_log (dealer_login, logged_at DESC);
CREATE INDEX ix_audit_log_logged_at ON audit_log (logged_at DESC);

-- alerts indexes
CREATE INDEX ix_alerts_state_severity ON alerts (state, severity, created_at);
CREATE INDEX ix_alerts_assigned_to ON alerts (assigned_to, state);
CREATE INDEX ix_alerts_login_server ON alerts (login, server);

-- symbol_mapping indexes
CREATE INDEX ix_symbol_mapping_canonical ON symbol_mapping (canonical_symbol);

-- correlation_pairs indexes
CREATE INDEX ix_correlation_pairs_login_a ON correlation_pairs (login_a);
CREATE INDEX ix_correlation_pairs_login_b ON correlation_pairs (login_b);

-- ============================================================================
-- 6. CONTINUOUS AGGREGATES (OHLCV candles from ticks)
-- ============================================================================

-- candles_1m: 1-minute candles from ticks
-- Feeds: CandleChart.tsx (1-minute view)
CREATE MATERIALIZED VIEW candles_1m
WITH (timescaledb.continuous) AS
SELECT
    time_bucket('1 minute', time) AS bucket,
    symbol,
    FIRST(bid, time)  AS open,
    MAX(bid)           AS high,
    MIN(bid)           AS low,
    LAST(bid, time)    AS close,
    COUNT(*)           AS tick_count
FROM ticks
GROUP BY bucket, symbol
WITH NO DATA;

-- candles_1h: 1-hour candles from 1-minute candles
-- Feeds: CandleChart.tsx (1-hour view)
CREATE MATERIALIZED VIEW candles_1h
WITH (timescaledb.continuous) AS
SELECT
    time_bucket('1 hour', bucket) AS bucket,
    symbol,
    FIRST(open, bucket)  AS open,
    MAX(high)             AS high,
    MIN(low)              AS low,
    LAST(close, bucket)   AS close,
    SUM(tick_count)       AS tick_count
FROM candles_1m
GROUP BY time_bucket('1 hour', bucket), symbol
WITH NO DATA;

-- candles_1d: Daily candles from 1-hour candles
-- Feeds: CandleChart.tsx (daily view)
CREATE MATERIALIZED VIEW candles_1d
WITH (timescaledb.continuous) AS
SELECT
    time_bucket('1 day', bucket) AS bucket,
    symbol,
    FIRST(open, bucket)  AS open,
    MAX(high)             AS high,
    MIN(low)              AS low,
    LAST(close, bucket)   AS close,
    SUM(tick_count)       AS tick_count
FROM candles_1h
GROUP BY time_bucket('1 day', bucket), symbol
WITH NO DATA;

-- ============================================================================
-- 7. COMPRESSION POLICIES
-- ============================================================================

-- ticks: compress after 7 days
ALTER TABLE ticks SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'symbol',
    timescaledb.compress_orderby = 'time DESC'
);
SELECT add_compression_policy('ticks', INTERVAL '7 days');

-- deals: compress after 30 days
ALTER TABLE deals SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'login',
    timescaledb.compress_orderby = 'time DESC'
);
SELECT add_compression_policy('deals', INTERVAL '30 days');

-- score_history: compress after 90 days
ALTER TABLE score_history SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'login',
    timescaledb.compress_orderby = 'scored_at DESC'
);
SELECT add_compression_policy('score_history', INTERVAL '90 days');

-- trading_metrics: compress after 90 days
ALTER TABLE trading_metrics SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'login',
    timescaledb.compress_orderby = 'period_start DESC'
);
SELECT add_compression_policy('trading_metrics', INTERVAL '90 days');

-- ============================================================================
-- 8. RETENTION POLICIES
-- ============================================================================

-- ticks: drop chunks older than 90 days (summarized into candles)
SELECT add_retention_policy('ticks', INTERVAL '90 days');

-- deals: NO retention — kept permanently (compliance: 5-7 year requirement)
-- score_history: NO retention — kept permanently (compliance)
-- trading_metrics: NO retention — kept permanently (compliance)
-- audit_log: NO retention — kept permanently (regulatory requirement)

-- ============================================================================
-- 9. CONTINUOUS AGGREGATE REFRESH POLICIES
-- ============================================================================

SELECT add_continuous_aggregate_policy('candles_1m',
    start_offset    => INTERVAL '10 minutes',
    end_offset      => INTERVAL '1 minute',
    schedule_interval => INTERVAL '1 minute');

SELECT add_continuous_aggregate_policy('candles_1h',
    start_offset    => INTERVAL '2 hours',
    end_offset      => INTERVAL '10 minutes',
    schedule_interval => INTERVAL '1 hour');

SELECT add_continuous_aggregate_policy('candles_1d',
    start_offset    => INTERVAL '2 days',
    end_offset      => INTERVAL '1 hour',
    schedule_interval => INTERVAL '1 day');
