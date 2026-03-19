using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using TIP.Core.Models;

namespace TIP.Data;

/// <summary>
/// Repository for deal records in the TimescaleDB "deals" hypertable.
/// Provides CRUD operations and paginated queries for deal history.
///
/// Design rationale:
/// - Uses raw Npgsql (not EF Core) for maximum control over query performance
///   and to leverage TimescaleDB-specific features (hypertable, compression).
/// - BulkInsert uses COPY protocol for backfill scenarios.
/// - Paginated queries use keyset pagination (WHERE time_msc > @cursor) instead of
///   OFFSET for consistent performance regardless of page depth.
/// </summary>
public sealed class DealRepository
{
    private readonly ILogger<DealRepository> _logger;
    private readonly DbConnectionFactory _dbFactory;
    private readonly string _serverName;
    private long _totalInserted;

    /// <summary>
    /// SQL COPY command for bulk deal inserts.
    /// </summary>
    private const string CopySql =
        "COPY deals (deal_id, login, time, time_msc, symbol, action, volume, price, " +
        "profit, commission, swap, fee, reason, expert_id, comment, position_id, entry, server) " +
        "FROM STDIN (FORMAT BINARY)";

    /// <summary>
    /// SQL INSERT with ON CONFLICT for single deal upserts.
    /// </summary>
    private const string InsertSql =
        "INSERT INTO deals (deal_id, login, time, time_msc, symbol, action, volume, price, " +
        "profit, commission, swap, fee, reason, expert_id, comment, position_id, entry, server) " +
        "VALUES (@deal_id, @login, @time, @time_msc, @symbol, @action, @volume, @price, " +
        "@profit, @commission, @swap, @fee, @reason, @expert_id, @comment, @position_id, @entry, @server) " +
        "ON CONFLICT (deal_id, server, time) DO NOTHING";

    /// <summary>
    /// SQL SELECT for deal history by login with pagination.
    /// </summary>
    private const string SelectByLoginSql =
        "SELECT deal_id, login, time, time_msc, symbol, action, volume, price, " +
        "profit, commission, swap, fee, reason, expert_id, comment, position_id, entry, server " +
        "FROM deals " +
        "WHERE login = @login AND time >= @from AND time <= @to " +
        "ORDER BY time DESC " +
        "LIMIT @limit OFFSET @offset";

    /// <summary>
    /// Initializes the deal repository.
    /// </summary>
    /// <param name="logger">Logger for query operations.</param>
    /// <param name="dbFactory">Database connection factory.</param>
    /// <param name="serverName">Default MT5 server name.</param>
    public DealRepository(ILogger<DealRepository> logger, DbConnectionFactory dbFactory, string serverName = "")
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _serverName = serverName;
    }

    /// <summary>
    /// Total deals inserted since startup.
    /// </summary>
    public long TotalInserted => Interlocked.Read(ref _totalInserted);

    /// <summary>
    /// Bulk inserts deals using the COPY protocol for high-throughput backfill.
    /// </summary>
    /// <param name="deals">Deal records to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of deals inserted.</returns>
    public async Task<int> BulkInsertAsync(IReadOnlyList<DealRecord> deals, CancellationToken cancellationToken = default)
    {
        if (deals.Count == 0)
            return 0;

        await using var conn = await _dbFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var writer = await conn.BeginBinaryImportAsync(CopySql, cancellationToken).ConfigureAwait(false);

        foreach (var deal in deals)
        {
            await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync((long)deal.DealId, NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync((long)deal.Login, NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(DateTimeOffset.FromUnixTimeMilliseconds(deal.TimeMsc), NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(deal.TimeMsc, NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(deal.Symbol, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync((short)deal.Action, NpgsqlDbType.Smallint, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(deal.Volume, NpgsqlDbType.Double, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(deal.Price, NpgsqlDbType.Double, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(deal.Profit, NpgsqlDbType.Double, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(deal.Commission, NpgsqlDbType.Double, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(deal.Swap, NpgsqlDbType.Double, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(deal.Fee, NpgsqlDbType.Double, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync((short)deal.Reason, NpgsqlDbType.Smallint, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync((long)deal.ExpertId, NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(deal.Comment, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync((long)deal.PositionId, NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync((short)deal.Entry, NpgsqlDbType.Smallint, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(deal.Server, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        }

        await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);

        Interlocked.Add(ref _totalInserted, deals.Count);

        _logger.LogDebug("Bulk inserted {Count} deals via COPY", deals.Count);
        return deals.Count;
    }

    /// <summary>
    /// Inserts a single deal record with ON CONFLICT DO NOTHING for idempotency.
    /// </summary>
    /// <param name="deal">Deal record to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task InsertAsync(DealRecord deal, CancellationToken cancellationToken = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);

        cmd.Parameters.AddWithValue("deal_id", (long)deal.DealId);
        cmd.Parameters.AddWithValue("login", (long)deal.Login);
        cmd.Parameters.AddWithValue("time", DateTimeOffset.FromUnixTimeMilliseconds(deal.TimeMsc));
        cmd.Parameters.AddWithValue("time_msc", deal.TimeMsc);
        cmd.Parameters.AddWithValue("symbol", deal.Symbol);
        cmd.Parameters.AddWithValue("action", (short)deal.Action);
        cmd.Parameters.AddWithValue("volume", deal.Volume);
        cmd.Parameters.AddWithValue("price", deal.Price);
        cmd.Parameters.AddWithValue("profit", deal.Profit);
        cmd.Parameters.AddWithValue("commission", deal.Commission);
        cmd.Parameters.AddWithValue("swap", deal.Swap);
        cmd.Parameters.AddWithValue("fee", deal.Fee);
        cmd.Parameters.AddWithValue("reason", (short)deal.Reason);
        cmd.Parameters.AddWithValue("expert_id", (long)deal.ExpertId);
        cmd.Parameters.AddWithValue("comment", deal.Comment);
        cmd.Parameters.AddWithValue("position_id", (long)deal.PositionId);
        cmd.Parameters.AddWithValue("entry", (short)deal.Entry);
        cmd.Parameters.AddWithValue("server", deal.Server);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _totalInserted);
    }

    /// <summary>
    /// Retrieves deals for a specific login within a time range, with pagination.
    /// Uses keyset pagination for consistent performance.
    /// </summary>
    /// <param name="login">Account login to query.</param>
    /// <param name="from">Start of time range.</param>
    /// <param name="toTime">End of time range.</param>
    /// <param name="limit">Maximum number of deals to return.</param>
    /// <param name="offset">Number of deals to skip (for pagination).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of matching deal records.</returns>
    public async Task<List<DealRecord>> GetByLoginAsync(
        ulong login,
        DateTimeOffset from,
        DateTimeOffset toTime,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DealRecord>();

        await using var conn = await _dbFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectByLoginSql, conn);

        cmd.Parameters.AddWithValue("login", (long)login);
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", toTime);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("offset", offset);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new DealRecord
            {
                DealId = (ulong)reader.GetInt64(0),
                Login = (ulong)reader.GetInt64(1),
                TimeMsc = reader.GetInt64(3),
                Symbol = reader.GetString(4),
                Action = reader.GetInt16(5),
                Volume = reader.GetDouble(6),
                Price = reader.GetDouble(7),
                Profit = reader.GetDouble(8),
                Commission = reader.GetDouble(9),
                Swap = reader.GetDouble(10),
                Fee = reader.GetDouble(11),
                Reason = reader.GetInt16(12),
                ExpertId = (ulong)reader.GetInt64(13),
                Comment = reader.GetString(14),
                PositionId = (ulong)reader.GetInt64(15),
                Entry = reader.GetInt16(16),
                Server = reader.GetString(17)
            });
        }

        _logger.LogDebug(
            "GetByLogin: {Count} deals for login {Login}, range {From}-{To}",
            results.Count, login, from, toTime);

        return results;
    }

    /// <summary>
    /// Fetches all deals from the database ordered by time ascending.
    /// Used on startup to warm up the AccountScorer with historical deals.
    /// </summary>
    public async Task<List<DealRecord>> GetAllDealsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<DealRecord>();
        var filterByServer = !string.IsNullOrEmpty(_serverName);

        var sql = "SELECT deal_id, login, time, time_msc, symbol, action, volume, price, " +
                  "profit, commission, swap, fee, reason, expert_id, comment, position_id, entry, server " +
                  "FROM deals" +
                  (filterByServer ? " WHERE server = @server" : "") +
                  " ORDER BY time ASC";

        await using var conn = await _dbFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(sql, conn);

        if (filterByServer)
            cmd.Parameters.AddWithValue("server", _serverName);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new DealRecord
            {
                DealId = (ulong)reader.GetInt64(0),
                Login = (ulong)reader.GetInt64(1),
                TimeMsc = reader.GetInt64(3),
                Symbol = reader.GetString(4),
                Action = reader.GetInt16(5),
                Volume = reader.GetDouble(6),
                Price = reader.GetDouble(7),
                Profit = reader.GetDouble(8),
                Commission = reader.GetDouble(9),
                Swap = reader.GetDouble(10),
                Fee = reader.GetDouble(11),
                Reason = reader.GetInt16(12),
                ExpertId = (ulong)reader.GetInt64(13),
                Comment = reader.GetString(14),
                PositionId = (ulong)reader.GetInt64(15),
                Entry = reader.GetInt16(16),
                Server = reader.GetString(17)
            });
        }

        _logger.LogInformation("GetAllDeals: loaded {Count} deals from database for server '{Server}'",
            results.Count, filterByServer ? _serverName : "ALL");
        return results;
    }
}
