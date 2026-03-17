using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TIP.Data;

/// <summary>
/// Repository for account reference data in the TimescaleDB "accounts" table.
///
/// Design rationale:
/// - Account data is refreshed from MT5 UserRequest during backfill and periodically.
/// - Upsert semantics ensure we always have the latest account state without duplicates.
/// - Read operations support per-server queries for multi-server deployments.
/// </summary>
public sealed class AccountRepository
{
    private readonly ILogger<AccountRepository> _logger;
    private readonly DbConnectionFactory _dbFactory;

    private const string UpsertSql =
        "INSERT INTO accounts (login, name, group_name, leverage, balance, equity, ib_agent, server) " +
        "VALUES (@login, @name, @group_name, @leverage, @balance, @equity, @ib_agent, @server) " +
        "ON CONFLICT (login, server) DO UPDATE SET " +
        "name = EXCLUDED.name, group_name = EXCLUDED.group_name, leverage = EXCLUDED.leverage, " +
        "balance = EXCLUDED.balance, equity = EXCLUDED.equity, ib_agent = EXCLUDED.ib_agent";

    private const string SelectByLoginSql =
        "SELECT login, name, group_name, leverage, balance, equity, ib_agent, server " +
        "FROM accounts WHERE login = @login AND server = @server";

    private const string SelectByServerSql =
        "SELECT login, name, group_name, leverage, balance, equity, ib_agent, server " +
        "FROM accounts WHERE server = @server";

    /// <summary>
    /// Initializes the account repository.
    /// </summary>
    /// <param name="logger">Logger for query operations.</param>
    /// <param name="dbFactory">Database connection factory.</param>
    public AccountRepository(ILogger<AccountRepository> logger, DbConnectionFactory dbFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Upsert account info from MT5 UserRequest.
    /// </summary>
    /// <param name="account">Account record to upsert.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task Upsert(AccountRecord account, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(UpsertSql, conn);

        cmd.Parameters.AddWithValue("login", (long)account.Login);
        cmd.Parameters.AddWithValue("name", account.Name);
        cmd.Parameters.AddWithValue("group_name", account.GroupName);
        cmd.Parameters.AddWithValue("leverage", account.Leverage);
        cmd.Parameters.AddWithValue("balance", account.Balance);
        cmd.Parameters.AddWithValue("equity", account.Equity);
        cmd.Parameters.AddWithValue("ib_agent", (long)account.IbAgent);
        cmd.Parameters.AddWithValue("server", account.Server);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        _logger.LogDebug("Upserted account {Login} on server {Server}", account.Login, account.Server);
    }

    /// <summary>
    /// Get account by login.
    /// </summary>
    /// <param name="server">MT5 server identifier.</param>
    /// <param name="login">Account login number.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Account record, or null if not found.</returns>
    public async Task<AccountRecord?> GetByLogin(string server, ulong login, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectByLoginSql, conn);

        cmd.Parameters.AddWithValue("login", (long)login);
        cmd.Parameters.AddWithValue("server", server);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return ReadAccount(reader);
        }

        return null;
    }

    /// <summary>
    /// Get all accounts for a server.
    /// </summary>
    /// <param name="server">MT5 server identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All accounts on the specified server.</returns>
    public async Task<IReadOnlyList<AccountRecord>> GetByServer(string server, CancellationToken ct = default)
    {
        var results = new List<AccountRecord>();

        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectByServerSql, conn);

        cmd.Parameters.AddWithValue("server", server);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadAccount(reader));
        }

        _logger.LogDebug("GetByServer: {Count} accounts on server {Server}", results.Count, server);
        return results;
    }

    /// <summary>
    /// Reads an account record from a data reader row.
    /// </summary>
    private static AccountRecord ReadAccount(Npgsql.NpgsqlDataReader reader)
    {
        return new AccountRecord
        {
            Login = (ulong)reader.GetInt64(0),
            Name = reader.GetString(1),
            GroupName = reader.GetString(2),
            Leverage = reader.GetInt32(3),
            Balance = reader.GetDouble(4),
            Equity = reader.GetDouble(5),
            IbAgent = (ulong)reader.GetInt64(6),
            Server = reader.GetString(7)
        };
    }
}

/// <summary>
/// Record mapping to the "accounts" table in TimescaleDB.
/// Represents MT5 account reference data.
/// </summary>
public sealed class AccountRecord
{
    /// <summary>MT5 account login number.</summary>
    public ulong Login { get; set; }

    /// <summary>Account holder name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Account group (e.g., "real\standard").</summary>
    public string GroupName { get; set; } = "";

    /// <summary>Account leverage.</summary>
    public int Leverage { get; set; }

    /// <summary>Account balance in deposit currency.</summary>
    public double Balance { get; set; }

    /// <summary>Account equity (balance + unrealized P&amp;L).</summary>
    public double Equity { get; set; }

    /// <summary>IB parent login (0 if no agent).</summary>
    public ulong IbAgent { get; set; }

    /// <summary>MT5 server identifier.</summary>
    public string Server { get; set; } = "";
}
