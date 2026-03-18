# Developer Setup

## Prerequisites

- .NET 8 SDK
- PostgreSQL 16 + TimescaleDB extension
- Node.js 18+ and npm
- Access to MT5 server credentials

## Database Setup

1. Install PostgreSQL 16 and TimescaleDB
2. Create the database and user:
   ```sql
   CREATE DATABASE tip;
   CREATE USER tip_user WITH PASSWORD 'your_password';
   GRANT ALL PRIVILEGES ON DATABASE tip TO tip_user;
   ```
3. Run the schema:
   ```bash
   psql -U tip_user -d tip -f docs/schema.sql
   ```

## Credentials Setup (required before running)

This project uses [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) to keep credentials out of source control.

Run from the `src/TIP.Api` directory:

```bash
dotnet user-secrets set "ConnectionStrings:TimescaleDB" "Host=localhost;Port=5432;Database=tip;Username=tip_user;Password=YOUR_DB_PASSWORD;Include Error Detail=true"
dotnet user-secrets set "MT5:Servers:0:Password" "YOUR_MT5_PASSWORD"
```

Verify secrets are stored:

```bash
dotnet user-secrets list
```

## Running

### Backend
```bash
cd src/TIP.Api && dotnet run
```

### Frontend
```bash
cd web && npm install && npm run dev
```

The dashboard is available at `http://localhost:5173`.
The API is available at `http://localhost:5000`.

## Verifying Data Persistence

After the app has been running for a few minutes with a live MT5 connection:

```sql
SELECT count(*) FROM deals;           -- Should be > 0 after backfill
SELECT count(*) FROM ticks;            -- Should be > 0 if market is open
SELECT count(*) FROM accounts;         -- Should be > 0 after deal processing
SELECT count(*) FROM trader_profiles;  -- Should be > 0 after first intelligence cycle (5 min)
SELECT count(*) FROM score_history;    -- Should be > 0 after first intelligence cycle
```
