using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TIP.Data.Auth;

/// <summary>
/// Repository for user CRUD operations against the tip_auth database.
///
/// Design rationale:
/// - Supports login, user management (admin), and password changes.
/// - BCrypt hashing done in service layer, repository stores/retrieves hashes.
/// - failed_attempts and locked_until support account lockout after 10 failures.
/// </summary>
public sealed class UserRepository
{
    private readonly ILogger<UserRepository> _logger;
    private readonly AuthDbConnectionFactory _dbFactory;

    private const string SelectColumns =
        "u.id, u.username, u.email, u.password_hash, u.display_name, u.role_id, " +
        "u.is_active, u.must_change_pwd, u.failed_attempts, u.locked_until, u.last_login, " +
        "u.created_at, u.updated_at, r.name AS role_name, r.permissions::text AS permissions_json";

    private const string SelectByUsernameSql =
        "SELECT " + SelectColumns + " FROM users u JOIN roles r ON u.role_id = r.id WHERE u.username = @username";

    private const string SelectByIdSql =
        "SELECT " + SelectColumns + " FROM users u JOIN roles r ON u.role_id = r.id WHERE u.id = @id";

    private const string SelectAllSql =
        "SELECT " + SelectColumns + " FROM users u JOIN roles r ON u.role_id = r.id ORDER BY u.id";

    private const string InsertSql =
        "INSERT INTO users (username, email, password_hash, display_name, role_id, must_change_pwd) " +
        "VALUES (@username, @email, @password_hash, @display_name, @role_id, @must_change_pwd) RETURNING id";

    private const string UpdateSql =
        "UPDATE users SET email = @email, display_name = @display_name, role_id = @role_id, " +
        "is_active = @is_active, updated_at = NOW() WHERE id = @id";

    private const string UpdatePasswordSql =
        "UPDATE users SET password_hash = @password_hash, must_change_pwd = FALSE, " +
        "updated_at = NOW() WHERE id = @id";

    private const string ResetPasswordSql =
        "UPDATE users SET password_hash = @password_hash, must_change_pwd = TRUE, " +
        "failed_attempts = 0, locked_until = NULL, updated_at = NOW() WHERE id = @id";

    private const string UpdateLastLoginSql =
        "UPDATE users SET last_login = NOW(), failed_attempts = 0, locked_until = NULL, " +
        "updated_at = NOW() WHERE id = @id";

    private const string IncrementFailedAttemptsSql =
        "UPDATE users SET failed_attempts = failed_attempts + 1, updated_at = NOW() WHERE id = @id RETURNING failed_attempts";

    private const string LockAccountSql =
        "UPDATE users SET locked_until = @locked_until, updated_at = NOW() WHERE id = @id";

    private const string DeactivateSql =
        "UPDATE users SET is_active = FALSE, updated_at = NOW() WHERE id = @id";

    /// <summary>
    /// Initializes the user repository.
    /// </summary>
    /// <param name="logger">Logger for query operations.</param>
    /// <param name="dbFactory">Auth database connection factory.</param>
    public UserRepository(ILogger<UserRepository> logger, AuthDbConnectionFactory dbFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Gets a user by username (used for login).
    /// </summary>
    public async Task<UserRecord?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectByUsernameSql, conn);
        cmd.Parameters.AddWithValue("username", username);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadUser(reader) : null;
    }

    /// <summary>
    /// Gets a user by ID.
    /// </summary>
    public async Task<UserRecord?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectByIdSql, conn);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadUser(reader) : null;
    }

    /// <summary>
    /// Gets all users.
    /// </summary>
    public async Task<IReadOnlyList<UserRecord>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<UserRecord>();

        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectAllSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadUser(reader));
        }

        return results;
    }

    /// <summary>
    /// Creates a new user. Returns the generated user ID.
    /// </summary>
    public async Task<int> CreateAsync(UserRecord user, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);

        cmd.Parameters.AddWithValue("username", user.Username);
        cmd.Parameters.AddWithValue("email", user.Email);
        cmd.Parameters.AddWithValue("password_hash", user.PasswordHash);
        cmd.Parameters.AddWithValue("display_name", user.DisplayName);
        cmd.Parameters.AddWithValue("role_id", user.RoleId);
        cmd.Parameters.AddWithValue("must_change_pwd", user.MustChangePassword);

        var id = (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        _logger.LogInformation("Created user {Username} with ID {Id}", user.Username, id);
        return id;
    }

    /// <summary>
    /// Updates user profile (email, display name, role, active status).
    /// </summary>
    public async Task UpdateAsync(UserRecord user, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(UpdateSql, conn);

        cmd.Parameters.AddWithValue("id", user.Id);
        cmd.Parameters.AddWithValue("email", user.Email);
        cmd.Parameters.AddWithValue("display_name", user.DisplayName);
        cmd.Parameters.AddWithValue("role_id", user.RoleId);
        cmd.Parameters.AddWithValue("is_active", user.IsActive);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Updated user {Username} (ID {Id})", user.Username, user.Id);
    }

    /// <summary>
    /// Updates user password and clears must_change_pwd flag.
    /// </summary>
    public async Task UpdatePasswordAsync(int userId, string passwordHash, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(UpdatePasswordSql, conn);

        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("password_hash", passwordHash);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Password updated for user ID {Id}", userId);
    }

    /// <summary>
    /// Resets user password (admin action). Sets must_change_pwd = true, clears lockout.
    /// </summary>
    public async Task ResetPasswordAsync(int userId, string passwordHash, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(ResetPasswordSql, conn);

        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("password_hash", passwordHash);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Password reset for user ID {Id} (admin action)", userId);
    }

    /// <summary>
    /// Records a successful login (updates last_login, clears failed attempts).
    /// </summary>
    public async Task RecordLoginAsync(int userId, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(UpdateLastLoginSql, conn);
        cmd.Parameters.AddWithValue("id", userId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Increments failed login attempts. Returns the new count.
    /// </summary>
    public async Task<int> IncrementFailedAttemptsAsync(int userId, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(IncrementFailedAttemptsSql, conn);
        cmd.Parameters.AddWithValue("id", userId);

        var count = (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        return count;
    }

    /// <summary>
    /// Locks the account until a specified time.
    /// </summary>
    public async Task LockAccountAsync(int userId, DateTimeOffset lockedUntil, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(LockAccountSql, conn);

        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("locked_until", lockedUntil);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogWarning("Account locked for user ID {Id} until {LockedUntil}", userId, lockedUntil);
    }

    /// <summary>
    /// Deactivates a user (soft delete).
    /// </summary>
    public async Task DeactivateAsync(int userId, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(DeactivateSql, conn);
        cmd.Parameters.AddWithValue("id", userId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Deactivated user ID {Id}", userId);
    }

    /// <summary>
    /// Gets server IDs assigned to a user.
    /// </summary>
    public async Task<IReadOnlyList<int>> GetUserServerIdsAsync(int userId, CancellationToken ct = default)
    {
        var results = new List<int>();

        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            "SELECT server_id FROM user_server_access WHERE user_id = @user_id", conn);
        cmd.Parameters.AddWithValue("user_id", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(reader.GetInt32(0));
        }

        return results;
    }

    /// <summary>
    /// Sets the server access list for a user (replaces existing).
    /// </summary>
    public async Task SetUserServersAsync(int userId, IReadOnlyList<int> serverIds, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

        // Delete existing
        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM user_server_access WHERE user_id = @user_id", conn))
        {
            cmd.Parameters.AddWithValue("user_id", userId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Insert new
        foreach (var serverId in serverIds)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO user_server_access (user_id, server_id) VALUES (@user_id, @server_id) " +
                "ON CONFLICT DO NOTHING", conn);
            cmd.Parameters.AddWithValue("user_id", userId);
            cmd.Parameters.AddWithValue("server_id", serverId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Set {Count} server assignments for user ID {Id}", serverIds.Count, userId);
    }

    /// <summary>
    /// Reads a user record from a data reader row.
    /// </summary>
    private static UserRecord ReadUser(NpgsqlDataReader reader)
    {
        return new UserRecord
        {
            Id = reader.GetInt32(0),
            Username = reader.GetString(1),
            Email = reader.GetString(2),
            PasswordHash = reader.GetString(3),
            DisplayName = reader.GetString(4),
            RoleId = reader.GetInt32(5),
            IsActive = reader.GetBoolean(6),
            MustChangePassword = reader.GetBoolean(7),
            FailedAttempts = reader.GetInt32(8),
            LockedUntil = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            LastLogin = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
            CreatedAt = reader.GetDateTime(11),
            UpdatedAt = reader.GetDateTime(12),
            RoleName = reader.GetString(13),
            PermissionsJson = reader.GetString(14)
        };
    }
}

/// <summary>
/// Record mapping to the "users" table in tip_auth with joined role data.
/// </summary>
public sealed class UserRecord
{
    /// <summary>Auto-incrementing user ID.</summary>
    public int Id { get; set; }

    /// <summary>Login username.</summary>
    public string Username { get; set; } = "";

    /// <summary>User email address.</summary>
    public string Email { get; set; } = "";

    /// <summary>BCrypt password hash.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>Display name for UI.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Foreign key to roles table.</summary>
    public int RoleId { get; set; }

    /// <summary>Whether the user account is active.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Whether user must change password on next login.</summary>
    public bool MustChangePassword { get; set; } = true;

    /// <summary>Number of consecutive failed login attempts.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>Account locked until this time (null = not locked).</summary>
    public DateTime? LockedUntil { get; set; }

    /// <summary>Last successful login time.</summary>
    public DateTime? LastLogin { get; set; }

    /// <summary>Account creation time.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Last update time.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Joined role name (admin, dealer, compliance).</summary>
    public string RoleName { get; set; } = "";

    /// <summary>Joined role permissions JSON array.</summary>
    public string PermissionsJson { get; set; } = "[]";
}
