using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TIP.Data.Auth;

/// <summary>
/// Repository for MT5 server configuration CRUD in the tip_auth database.
///
/// Design rationale:
/// - MT5 manager passwords are AES-256 encrypted before storage.
/// - Encryption/decryption happens in the service layer; this repository stores ciphertext.
/// - is_connected is a runtime flag updated by the backend connection manager.
/// </summary>
public sealed class MT5ServerRepository
{
    private readonly ILogger<MT5ServerRepository> _logger;
    private readonly AuthDbConnectionFactory _dbFactory;

    private const string SelectAllSql =
        "SELECT id, name, address, manager_login, manager_password_encrypted, group_mask, " +
        "is_enabled, is_connected, last_connected, created_at, updated_at " +
        "FROM mt5_servers ORDER BY id";

    private const string SelectByIdSql =
        "SELECT id, name, address, manager_login, manager_password_encrypted, group_mask, " +
        "is_enabled, is_connected, last_connected, created_at, updated_at " +
        "FROM mt5_servers WHERE id = @id";

    private const string SelectEnabledSql =
        "SELECT id, name, address, manager_login, manager_password_encrypted, group_mask, " +
        "is_enabled, is_connected, last_connected, created_at, updated_at " +
        "FROM mt5_servers WHERE is_enabled = TRUE ORDER BY id";

    private const string InsertSql =
        "INSERT INTO mt5_servers (name, address, manager_login, manager_password_encrypted, group_mask) " +
        "VALUES (@name, @address, @manager_login, @manager_password_encrypted, @group_mask) RETURNING id";

    private const string UpdateSql =
        "UPDATE mt5_servers SET name = @name, address = @address, manager_login = @manager_login, " +
        "manager_password_encrypted = @manager_password_encrypted, group_mask = @group_mask, " +
        "updated_at = NOW() WHERE id = @id";

    private const string DeleteSql =
        "DELETE FROM mt5_servers WHERE id = @id";

    private const string SetEnabledSql =
        "UPDATE mt5_servers SET is_enabled = @is_enabled, updated_at = NOW() WHERE id = @id";

    private const string SetConnectedSql =
        "UPDATE mt5_servers SET is_connected = @is_connected, " +
        "last_connected = CASE WHEN @is_connected THEN NOW() ELSE last_connected END, " +
        "updated_at = NOW() WHERE id = @id";

    /// <summary>
    /// Initializes the MT5 server repository.
    /// </summary>
    /// <param name="logger">Logger for query operations.</param>
    /// <param name="dbFactory">Auth database connection factory.</param>
    public MT5ServerRepository(ILogger<MT5ServerRepository> logger, AuthDbConnectionFactory dbFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Gets all MT5 server configs.
    /// </summary>
    public async Task<IReadOnlyList<MT5ServerRecord>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<MT5ServerRecord>();

        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectAllSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadServer(reader));
        }

        return results;
    }

    /// <summary>
    /// Gets a server by ID.
    /// </summary>
    public async Task<MT5ServerRecord?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectByIdSql, conn);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadServer(reader) : null;
    }

    /// <summary>
    /// Gets all enabled servers (for startup connection).
    /// </summary>
    public async Task<IReadOnlyList<MT5ServerRecord>> GetEnabledAsync(CancellationToken ct = default)
    {
        var results = new List<MT5ServerRecord>();

        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectEnabledSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadServer(reader));
        }

        return results;
    }

    /// <summary>
    /// Creates a new MT5 server config. Returns the generated ID.
    /// </summary>
    public async Task<int> CreateAsync(MT5ServerRecord server, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);

        cmd.Parameters.AddWithValue("name", server.Name);
        cmd.Parameters.AddWithValue("address", server.Address);
        cmd.Parameters.AddWithValue("manager_login", server.ManagerLogin);
        cmd.Parameters.AddWithValue("manager_password_encrypted", server.ManagerPasswordEncrypted);
        cmd.Parameters.AddWithValue("group_mask", server.GroupMask);

        var id = (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        _logger.LogInformation("Created MT5 server '{Name}' ({Address}) with ID {Id}", server.Name, server.Address, id);
        return id;
    }

    /// <summary>
    /// Updates an MT5 server config.
    /// </summary>
    public async Task UpdateAsync(MT5ServerRecord server, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(UpdateSql, conn);

        cmd.Parameters.AddWithValue("id", server.Id);
        cmd.Parameters.AddWithValue("name", server.Name);
        cmd.Parameters.AddWithValue("address", server.Address);
        cmd.Parameters.AddWithValue("manager_login", server.ManagerLogin);
        cmd.Parameters.AddWithValue("manager_password_encrypted", server.ManagerPasswordEncrypted);
        cmd.Parameters.AddWithValue("group_mask", server.GroupMask);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Updated MT5 server '{Name}' (ID {Id})", server.Name, server.Id);
    }

    /// <summary>
    /// Deletes an MT5 server config (also cascades to user_server_access).
    /// </summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(DeleteSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Deleted MT5 server ID {Id}", id);
    }

    /// <summary>
    /// Enables or disables a server.
    /// </summary>
    public async Task SetEnabledAsync(int id, bool isEnabled, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SetEnabledSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("is_enabled", isEnabled);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Set MT5 server ID {Id} enabled = {Enabled}", id, isEnabled);
    }

    /// <summary>
    /// Updates the runtime connection status.
    /// </summary>
    public async Task SetConnectedAsync(int id, bool isConnected, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SetConnectedSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("is_connected", isConnected);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets server addresses assigned to a user (for data filtering).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetServerAddressesForUserAsync(int userId, CancellationToken ct = default)
    {
        var results = new List<string>();

        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT s.address FROM mt5_servers s " +
            "JOIN user_server_access usa ON s.id = usa.server_id " +
            "WHERE usa.user_id = @user_id AND s.is_enabled = TRUE", conn);
        cmd.Parameters.AddWithValue("user_id", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    /// <summary>
    /// Reads an MT5 server record from a data reader row.
    /// </summary>
    private static MT5ServerRecord ReadServer(NpgsqlDataReader reader)
    {
        return new MT5ServerRecord
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            Address = reader.GetString(2),
            ManagerLogin = reader.GetInt64(3),
            ManagerPasswordEncrypted = reader.GetString(4),
            GroupMask = reader.GetString(5),
            IsEnabled = reader.GetBoolean(6),
            IsConnected = reader.GetBoolean(7),
            LastConnected = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
            CreatedAt = reader.GetDateTime(9),
            UpdatedAt = reader.GetDateTime(10)
        };
    }
}

/// <summary>
/// Record mapping to the "mt5_servers" table in tip_auth.
/// </summary>
public sealed class MT5ServerRecord
{
    /// <summary>Auto-incrementing server ID.</summary>
    public int Id { get; set; }

    /// <summary>Display label (e.g., "Live-EU").</summary>
    public string Name { get; set; } = "";

    /// <summary>Server address and port (e.g., "89.21.67.56:443").</summary>
    public string Address { get; set; } = "";

    /// <summary>Manager API login number.</summary>
    public long ManagerLogin { get; set; }

    /// <summary>AES-256 encrypted manager password (Base64 ciphertext).</summary>
    public string ManagerPasswordEncrypted { get; set; } = "";

    /// <summary>MT5 group mask filter (e.g., "*" for all groups).</summary>
    public string GroupMask { get; set; } = "*";

    /// <summary>Whether the server is enabled for connection.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Runtime connection status (set by connection manager).</summary>
    public bool IsConnected { get; set; }

    /// <summary>Last successful connection time.</summary>
    public DateTime? LastConnected { get; set; }

    /// <summary>When the server config was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the server config was last updated.</summary>
    public DateTime UpdatedAt { get; set; }
}
