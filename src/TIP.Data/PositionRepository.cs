using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace TIP.Data;

/// <summary>
/// Repository for open position tracking in the TimescaleDB "positions" table.
///
/// Design rationale:
/// - Positions are upserted on open/modify and deleted on close, keeping the table
///   as a live snapshot of all currently open positions across all servers.
/// - Used by PnLEngine (unrealized P&amp;L), ExposureEngine (net exposure per symbol),
///   and the dashboard (live position grid).
/// - All operations are async with CancellationToken for graceful shutdown.
/// </summary>
public sealed class PositionRepository
{
    private readonly ILogger<PositionRepository> _logger;
    private readonly DbConnectionFactory _dbFactory;

    private const string UpsertSql =
        "INSERT INTO positions (position_id, login, symbol, direction, volume, open_price, open_time, " +
        "current_price, unrealized_pnl, swap, server) " +
        "VALUES (@position_id, @login, @symbol, @direction, @volume, @open_price, @open_time, " +
        "@current_price, @unrealized_pnl, @swap, @server) " +
        "ON CONFLICT (position_id, server) DO UPDATE SET " +
        "volume = EXCLUDED.volume, current_price = EXCLUDED.current_price, " +
        "unrealized_pnl = EXCLUDED.unrealized_pnl, swap = EXCLUDED.swap";

    private const string DeleteSql =
        "DELETE FROM positions WHERE position_id = @position_id AND server = @server";

    private const string SelectByLoginSql =
        "SELECT position_id, login, symbol, direction, volume, open_price, open_time, " +
        "current_price, unrealized_pnl, swap, server " +
        "FROM positions WHERE login = @login";

    private const string SelectBySymbolSql =
        "SELECT position_id, login, symbol, direction, volume, open_price, open_time, " +
        "current_price, unrealized_pnl, swap, server " +
        "FROM positions WHERE symbol = @symbol";

    private const string SelectAllSql =
        "SELECT position_id, login, symbol, direction, volume, open_price, open_time, " +
        "current_price, unrealized_pnl, swap, server " +
        "FROM positions";

    /// <summary>
    /// Initializes the position repository.
    /// </summary>
    /// <param name="logger">Logger for query operations.</param>
    /// <param name="dbFactory">Database connection factory.</param>
    public PositionRepository(ILogger<PositionRepository> logger, DbConnectionFactory dbFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Insert or update an open position.
    /// </summary>
    /// <param name="position">Position record to upsert.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task Upsert(PositionRecord position, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(UpsertSql, conn);

        cmd.Parameters.AddWithValue("position_id", position.PositionId);
        cmd.Parameters.AddWithValue("login", (long)position.Login);
        cmd.Parameters.AddWithValue("symbol", position.Symbol);
        cmd.Parameters.AddWithValue("direction", (short)position.Direction);
        cmd.Parameters.AddWithValue("volume", position.Volume);
        cmd.Parameters.AddWithValue("open_price", position.OpenPrice);
        cmd.Parameters.AddWithValue("open_time", position.OpenTime);
        cmd.Parameters.AddWithValue("current_price", position.CurrentPrice);
        cmd.Parameters.AddWithValue("unrealized_pnl", position.UnrealizedPnl);
        cmd.Parameters.AddWithValue("swap", position.Swap);
        cmd.Parameters.AddWithValue("server", position.Server);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger.LogDebug("Upserted position {PositionId} for login {Login}", position.PositionId, position.Login);
    }

    /// <summary>
    /// Remove a closed position.
    /// </summary>
    /// <param name="server">MT5 server identifier.</param>
    /// <param name="positionId">Position ticket to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task Delete(string server, long positionId, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(DeleteSql, conn);

        cmd.Parameters.AddWithValue("position_id", positionId);
        cmd.Parameters.AddWithValue("server", server);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger.LogDebug("Deleted position {PositionId} from server {Server}", positionId, server);
    }

    /// <summary>
    /// Get all open positions for a login.
    /// </summary>
    /// <param name="login">Account login to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of open positions for the login.</returns>
    public async Task<IReadOnlyList<PositionRecord>> GetByLogin(ulong login, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectByLoginSql, conn);

        cmd.Parameters.AddWithValue("login", (long)login);

        return await ReadPositions(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Get all open positions for a symbol (for exposure calculation).
    /// </summary>
    /// <param name="symbol">Trading symbol to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of open positions for the symbol.</returns>
    public async Task<IReadOnlyList<PositionRecord>> GetBySymbol(string symbol, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectBySymbolSql, conn);

        cmd.Parameters.AddWithValue("symbol", symbol);

        return await ReadPositions(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Get all open positions (for P&amp;L engine startup).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All open positions across all servers.</returns>
    public async Task<IReadOnlyList<PositionRecord>> GetAll(CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectAllSql, conn);

        return await ReadPositions(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads position records from a command result set.
    /// </summary>
    private static async Task<List<PositionRecord>> ReadPositions(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<PositionRecord>();

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new PositionRecord
            {
                PositionId = reader.GetInt64(0),
                Login = (ulong)reader.GetInt64(1),
                Symbol = reader.GetString(2),
                Direction = reader.GetInt16(3),
                Volume = reader.GetDouble(4),
                OpenPrice = reader.GetDouble(5),
                OpenTime = reader.GetFieldValue<DateTimeOffset>(6),
                CurrentPrice = reader.GetDouble(7),
                UnrealizedPnl = reader.GetDouble(8),
                Swap = reader.GetDouble(9),
                Server = reader.GetString(10)
            });
        }

        return results;
    }
}

/// <summary>
/// Record mapping to the "positions" table in TimescaleDB.
/// Represents a currently open trading position.
/// </summary>
public sealed class PositionRecord
{
    /// <summary>Position ticket number.</summary>
    public long PositionId { get; set; }

    /// <summary>Account login that owns this position.</summary>
    public ulong Login { get; set; }

    /// <summary>Trading instrument symbol.</summary>
    public string Symbol { get; set; } = "";

    /// <summary>Position direction: 0=BUY, 1=SELL.</summary>
    public int Direction { get; set; }

    /// <summary>Open volume in lots.</summary>
    public double Volume { get; set; }

    /// <summary>Position open price.</summary>
    public double OpenPrice { get; set; }

    /// <summary>When the position was opened.</summary>
    public DateTimeOffset OpenTime { get; set; }

    /// <summary>Current market price for P&amp;L calculation.</summary>
    public double CurrentPrice { get; set; }

    /// <summary>Unrealized profit/loss in deposit currency.</summary>
    public double UnrealizedPnl { get; set; }

    /// <summary>Accumulated swap/rollover charge.</summary>
    public double Swap { get; set; }

    /// <summary>MT5 server identifier.</summary>
    public string Server { get; set; } = "";
}
