# Trader Intelligence Platform

A distributed brokerage operations platform connecting to MetaTrader 5 servers for real-time dealing desk intelligence.

## What it does
- **Real-time abuse detection** — ring trading, latency arbitrage, bonus abuse, bot farming
- **Live price streaming** — tick-by-tick from MT5 via event-driven callbacks
- **P&L calculation** — server-side, per-position, per-tick updates
- **Trader profiling** — trading style fingerprinting with persistent history
- **AI book routing** — A-Book/B-Book/Hybrid suggestions with simulation mode
- **Compliance tools** — audit trail, SAR/STR reports, regulatory evidence export

## Tech Stack
- **Backend:** C# / .NET 8+ (ASP.NET Core)
- **Database:** TimescaleDB (PostgreSQL)
- **Frontend:** React + TypeScript
- **MT5 Integration:** Manager API (native C++ DLLs, event-driven)
- **Real-time:** WebSocket (ASP.NET Core)

## Architecture
MT5 is a data source only. One read-only connection per server, event-driven callbacks, zero polling. All computation happens on our server.

See [CLAUDE.md](CLAUDE.md) for the complete project specification.

## Status
- **v1.0** — WinForms abuse scanner (complete, see [legacy repo](https://github.com/amakki-a11y/rebate-abuse-detector))
- **v2.0** — Distributed intelligence platform (in development)

## License
Proprietary — BBC Corp
