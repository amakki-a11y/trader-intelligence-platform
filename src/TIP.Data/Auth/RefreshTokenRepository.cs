using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TIP.Data.Auth;

/// <summary>
/// Repository for refresh token management in the tip_auth database.
///
/// Design rationale:
/// - Refresh tokens are SHA256-hashed before storage (never stored plain).
/// - Token rotation: old token revoked when new one issued.
/// - Expired/revoked tokens cleaned up periodically.
/// </summary>
public sealed class RefreshTokenRepository
{
    private readonly ILogger<RefreshTokenRepository> _logger;
    private readonly AuthDbConnectionFactory _dbFactory;

    private const string InsertSql =
        "INSERT INTO refresh_tokens (user_id, token_hash, expires_at) " +
        "VALUES (@user_id, @token_hash, @expires_at) RETURNING id";

    private const string SelectByHashSql =
        "SELECT id, user_id, token_hash, expires_at, created_at, revoked_at " +
        "FROM refresh_tokens WHERE token_hash = @token_hash";

    private const string RevokeSql =
        "UPDATE refresh_tokens SET revoked_at = NOW() WHERE token_hash = @token_hash";

    private const string RevokeAllForUserSql =
        "UPDATE refresh_tokens SET revoked_at = NOW() WHERE user_id = @user_id AND revoked_at IS NULL";

    private const string CleanupExpiredSql =
        "DELETE FROM refresh_tokens WHERE expires_at < NOW() OR (revoked_at IS NOT NULL AND revoked_at < NOW() - INTERVAL '1 day')";

    /// <summary>
    /// Initializes the refresh token repository.
    /// </summary>
    /// <param name="logger">Logger for token operations.</param>
    /// <param name="dbFactory">Auth database connection factory.</param>
    public RefreshTokenRepository(ILogger<RefreshTokenRepository> logger, AuthDbConnectionFactory dbFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Stores a new refresh token hash.
    /// </summary>
    /// <param name="userId">User ID the token belongs to.</param>
    /// <param name="tokenHash">SHA256 hash of the refresh token.</param>
    /// <param name="expiresAt">Token expiration time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The inserted token record ID.</returns>
    public async Task<int> CreateAsync(int userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);

        cmd.Parameters.AddWithValue("user_id", userId);
        cmd.Parameters.AddWithValue("token_hash", tokenHash);
        cmd.Parameters.AddWithValue("expires_at", expiresAt);

        var id = (int)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        _logger.LogDebug("Created refresh token for user {UserId}", userId);
        return id;
    }

    /// <summary>
    /// Gets a refresh token by its hash. Returns null if not found.
    /// </summary>
    /// <param name="tokenHash">SHA256 hash of the refresh token.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Token record, or null if not found.</returns>
    public async Task<RefreshTokenRecord?> GetByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectByHashSql, conn);
        cmd.Parameters.AddWithValue("token_hash", tokenHash);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;

        return new RefreshTokenRecord
        {
            Id = reader.GetInt32(0),
            UserId = reader.GetInt32(1),
            TokenHash = reader.GetString(2),
            ExpiresAt = reader.GetDateTime(3),
            CreatedAt = reader.GetDateTime(4),
            RevokedAt = reader.IsDBNull(5) ? null : reader.GetDateTime(5)
        };
    }

    /// <summary>
    /// Revokes a specific refresh token by its hash.
    /// </summary>
    /// <param name="tokenHash">SHA256 hash of the refresh token to revoke.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RevokeAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(RevokeSql, conn);
        cmd.Parameters.AddWithValue("token_hash", tokenHash);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes all active refresh tokens for a user (used on logout or security event).
    /// </summary>
    /// <param name="userId">User whose tokens to revoke.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RevokeAllForUserAsync(int userId, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(RevokeAllForUserSql, conn);
        cmd.Parameters.AddWithValue("user_id", userId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Revoked all refresh tokens for user ID {UserId}", userId);
    }

    /// <summary>
    /// Cleans up expired and old revoked tokens.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of tokens deleted.</returns>
    public async Task<int> CleanupExpiredAsync(CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(CleanupExpiredSql, conn);
        var count = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired/revoked refresh tokens", count);
        }
        return count;
    }
}

/// <summary>
/// Record mapping to the "refresh_tokens" table in tip_auth.
/// </summary>
public sealed class RefreshTokenRecord
{
    /// <summary>Token ID.</summary>
    public int Id { get; set; }

    /// <summary>User ID the token belongs to.</summary>
    public int UserId { get; set; }

    /// <summary>SHA256 hash of the actual token.</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>When the token expires.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>When the token was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the token was revoked (null = still active).</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>Whether this token is still valid (not revoked and not expired).</summary>
    public bool IsValid => RevokedAt == null && ExpiresAt > DateTime.UtcNow;
}
