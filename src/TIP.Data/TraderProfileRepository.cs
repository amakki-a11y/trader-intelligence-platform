using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
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
/// - Falls back to no-op when DB is unavailable (dbEnabled=false).
/// </summary>
public sealed class TraderProfileRepository
{
    private readonly ILogger<TraderProfileRepository> _logger;
    private readonly DbConnectionFactory _dbFactory;

    /// <summary>
    /// Initializes the trader profile repository.
    /// </summary>
    /// <param name="logger">Logger for query operations.</param>
    /// <param name="dbFactory">Database connection factory.</param>
    public TraderProfileRepository(ILogger<TraderProfileRepository> logger, DbConnectionFactory dbFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Upserts a trader profile — inserts if new, updates if existing.
    /// Uses ON CONFLICT (login, server) DO UPDATE for atomic upsert.
    /// </summary>
    /// <param name="profile">Profile to upsert.</param>
    public async Task UpsertAsync(TraderProfile profile)
    {
        try
        {
            await using var conn = await _dbFactory.OpenConnectionAsync().ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO trader_profiles (login, name, group_name, server, ib_agent,
                    trading_style, book_routing, routing_confidence, first_seen, last_updated)
                VALUES (@login, @name, @group_name, @server, @ib_agent,
                    @trading_style, @book_routing, @routing_confidence, NOW(), NOW())
                ON CONFLICT (login, server) DO UPDATE SET
                    name = EXCLUDED.name,
                    group_name = EXCLUDED.group_name,
                    ib_agent = EXCLUDED.ib_agent,
                    trading_style = EXCLUDED.trading_style,
                    book_routing = EXCLUDED.book_routing,
                    routing_confidence = EXCLUDED.routing_confidence,
                    last_updated = NOW()", conn);

            cmd.Parameters.AddWithValue("login", (long)profile.Login);
            cmd.Parameters.AddWithValue("name", profile.Name);
            cmd.Parameters.AddWithValue("group_name", profile.Group);
            cmd.Parameters.AddWithValue("server", profile.Server);
            cmd.Parameters.AddWithValue("ib_agent", (long)profile.IBAgentLogin);
            cmd.Parameters.AddWithValue("trading_style", profile.Style.ToString());
            cmd.Parameters.AddWithValue("book_routing", profile.Routing.ToString());
            cmd.Parameters.AddWithValue("routing_confidence", profile.RoutingConfidence);

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to upsert trader profile for login {Login}", profile.Login);
        }
    }

    /// <summary>
    /// Gets a trader profile by login number.
    /// </summary>
    /// <param name="login">MT5 account login.</param>
    /// <returns>Profile if found, null otherwise.</returns>
    public async Task<TraderProfile?> GetByLoginAsync(ulong login)
    {
        try
        {
            await using var conn = await _dbFactory.OpenConnectionAsync().ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(
                "SELECT login, name, group_name, server, ib_agent, trading_style, book_routing, routing_confidence, first_seen, last_updated FROM trader_profiles WHERE login = @login LIMIT 1", conn);
            cmd.Parameters.AddWithValue("login", (long)login);

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
                return null;

            return new TraderProfile
            {
                Login = (ulong)reader.GetInt64(0),
                Name = reader.GetString(1),
                Group = reader.GetString(2),
                Server = reader.GetString(3),
                IBAgentLogin = (ulong)reader.GetInt64(4),
                Style = Enum.TryParse<TradingStyle>(reader.GetString(5), out var s) ? s : TradingStyle.Unknown,
                Routing = Enum.TryParse<BookRouting>(reader.GetString(6), out var r) ? r : BookRouting.Unknown,
                RoutingConfidence = reader.GetDouble(7),
                FirstSeen = reader.GetDateTime(8),
                LastUpdated = reader.GetDateTime(9)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get trader profile for login {Login}", login);
            return null;
        }
    }

    /// <summary>
    /// Inserts a score history snapshot for an account.
    /// </summary>
    /// <param name="login">MT5 account login.</param>
    /// <param name="score">Current abuse score.</param>
    /// <param name="riskLevel">Current risk classification.</param>
    /// <param name="isRingMember">Whether account is in a trading ring.</param>
    /// <param name="server">MT5 server identifier.</param>
    public async Task InsertScoreHistoryAsync(ulong login, double score, string riskLevel, bool isRingMember, string server = "")
    {
        try
        {
            await using var conn = await _dbFactory.OpenConnectionAsync().ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO score_history (login, scored_at, abuse_score, risk_level,
                    is_ring_member, correlation_count, linked_logins, triggered_rules, server)
                VALUES (@login, NOW(), @abuse_score, @risk_level,
                    @is_ring_member, 0, '[]'::jsonb, '[]'::jsonb, @server)", conn);

            cmd.Parameters.AddWithValue("login", (long)login);
            cmd.Parameters.AddWithValue("abuse_score", score);
            cmd.Parameters.AddWithValue("risk_level", riskLevel);
            cmd.Parameters.AddWithValue("is_ring_member", isRingMember);
            cmd.Parameters.AddWithValue("server", server);

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to insert score history for login {Login}", login);
        }
    }

    /// <summary>
    /// Gets score history entries for an account (newest first).
    /// </summary>
    /// <param name="login">MT5 account login.</param>
    /// <param name="limit">Maximum entries to return.</param>
    /// <returns>List of score history entries.</returns>
    public async Task<List<(DateTimeOffset Time, double Score, string RiskLevel)>> GetScoreHistoryAsync(ulong login, int limit = 100)
    {
        var result = new List<(DateTimeOffset, double, string)>();
        try
        {
            await using var conn = await _dbFactory.OpenConnectionAsync().ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(
                "SELECT scored_at, abuse_score, risk_level FROM score_history WHERE login = @login ORDER BY scored_at DESC LIMIT @limit", conn);
            cmd.Parameters.AddWithValue("login", (long)login);
            cmd.Parameters.AddWithValue("limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                result.Add((reader.GetDateTime(0), reader.GetDouble(1), reader.GetString(2)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get score history for login {Login}", login);
        }
        return result;
    }
}
