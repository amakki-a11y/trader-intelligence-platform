using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TIP.Core.Models;

namespace TIP.Data;

/// <summary>
/// Repository for trader profiles in TimescaleDB.
/// Provides CRUD operations for the trader_profiles table.
///
/// Design rationale:
/// - Trader profiles are updated incrementally as new trades arrive and style/routing
///   classifications change. Upsert semantics (INSERT ON CONFLICT UPDATE) prevent
///   duplicate profiles while allowing atomic updates.
/// - Read operations support filtering by group, server, style, and routing for
///   dashboard views and bulk analysis.
/// </summary>
public class TraderProfileRepository
{
    private readonly ILogger<TraderProfileRepository> _logger;
    private readonly string _connectionString;

    /// <summary>
    /// Initializes the trader profile repository.
    /// </summary>
    /// <param name="logger">Logger for query operations.</param>
    /// <param name="connectionString">TimescaleDB connection string.</param>
    public TraderProfileRepository(ILogger<TraderProfileRepository> logger, string connectionString)
    {
        _logger = logger;
        _connectionString = connectionString;
    }

    // TODO: Phase 5, Task 14 — Upsert(TraderProfile profile)
    // TODO: Phase 5, Task 14 — GetByLogin(ulong login) → TraderProfile?
    // TODO: Phase 5, Task 14 — GetByGroup(string group, int limit, int offset) → List<TraderProfile>
    // TODO: Phase 5, Task 14 — GetByStyle(TradingStyle style) → List<TraderProfile>
    // TODO: Phase 5, Task 14 — GetByRouting(BookRouting routing) → List<TraderProfile>
}
