using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TIP.Data.Auth;

/// <summary>
/// Initializes the tip_auth database schema on first startup.
///
/// Design rationale:
/// - Uses IF NOT EXISTS to be safely re-entrant on every app startup.
/// - Seeds 3 default roles (admin, dealer, compliance) and 1 admin user.
/// - Admin default password is BCrypt-hashed in code, never stored as plain SQL.
/// - must_change_pwd = true forces password change on first login.
/// </summary>
public sealed class AuthDbInitializer
{
    private readonly ILogger<AuthDbInitializer> _logger;
    private readonly AuthDbConnectionFactory _dbFactory;

    /// <summary>Default admin password that must be changed on first login.</summary>
    private const string DefaultAdminPassword = "admin123";

    private const string CreateTablesSql = @"
CREATE TABLE IF NOT EXISTS roles (
    id              SERIAL PRIMARY KEY,
    name            TEXT NOT NULL UNIQUE,
    description     TEXT NOT NULL DEFAULT '',
    permissions     JSONB NOT NULL DEFAULT '[]',
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS users (
    id              SERIAL PRIMARY KEY,
    username        TEXT NOT NULL UNIQUE,
    email           TEXT NOT NULL UNIQUE,
    password_hash   TEXT NOT NULL,
    display_name    TEXT NOT NULL DEFAULT '',
    role_id         INTEGER NOT NULL REFERENCES roles(id),
    is_active       BOOLEAN NOT NULL DEFAULT TRUE,
    must_change_pwd BOOLEAN NOT NULL DEFAULT TRUE,
    failed_attempts INTEGER NOT NULL DEFAULT 0,
    locked_until    TIMESTAMPTZ,
    last_login      TIMESTAMPTZ,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS mt5_servers (
    id                          SERIAL PRIMARY KEY,
    name                        TEXT NOT NULL UNIQUE,
    address                     TEXT NOT NULL,
    manager_login               BIGINT NOT NULL,
    manager_password_encrypted  TEXT NOT NULL,
    group_mask                  TEXT NOT NULL DEFAULT '*',
    is_enabled                  BOOLEAN NOT NULL DEFAULT TRUE,
    is_connected                BOOLEAN NOT NULL DEFAULT FALSE,
    last_connected              TIMESTAMPTZ,
    created_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at                  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS user_server_access (
    user_id     INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    server_id   INTEGER NOT NULL REFERENCES mt5_servers(id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, server_id)
);

CREATE TABLE IF NOT EXISTS refresh_tokens (
    id          SERIAL PRIMARY KEY,
    user_id     INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash  TEXT NOT NULL UNIQUE,
    expires_at  TIMESTAMPTZ NOT NULL,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    revoked_at  TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_refresh_tokens_user ON refresh_tokens(user_id);
CREATE INDEX IF NOT EXISTS idx_refresh_tokens_expires ON refresh_tokens(expires_at);
";

    private const string SeedRolesSql = @"
INSERT INTO roles (name, description, permissions) VALUES
    ('admin', 'Full system access', '[""admin.users"", ""admin.servers"", ""admin.settings"", ""dashboard.view"", ""dashboard.actions"", ""accounts.view"", ""accounts.actions"", ""reports.view"", ""reports.export""]'),
    ('dealer', 'Trading desk operations', '[""dashboard.view"", ""dashboard.actions"", ""accounts.view"", ""accounts.actions"", ""reports.view""]'),
    ('compliance', 'Read-only monitoring and reports', '[""dashboard.view"", ""accounts.view"", ""reports.view"", ""reports.export""]')
ON CONFLICT (name) DO NOTHING;
";

    private const string CheckAdminExistsSql =
        "SELECT COUNT(*) FROM users WHERE username = 'admin'";

    private const string InsertAdminSql =
        "INSERT INTO users (username, email, password_hash, display_name, role_id, must_change_pwd) " +
        "VALUES ('admin', 'admin@tip.local', @password_hash, 'System Administrator', " +
        "(SELECT id FROM roles WHERE name = 'admin'), TRUE)";

    /// <summary>
    /// Initializes the auth database initializer.
    /// </summary>
    /// <param name="logger">Logger for initialization events.</param>
    /// <param name="dbFactory">Auth database connection factory.</param>
    public AuthDbInitializer(ILogger<AuthDbInitializer> logger, AuthDbConnectionFactory dbFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Creates all auth tables and seeds default data if not already present.
    /// Safe to call on every startup (uses IF NOT EXISTS and ON CONFLICT DO NOTHING).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Initializing auth database schema...");

        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

        // Create tables
        await using (var cmd = new NpgsqlCommand(CreateTablesSql, conn))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        _logger.LogInformation("Auth database tables created (or already exist)");

        // Seed roles
        await using (var cmd = new NpgsqlCommand(SeedRolesSql, conn))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        _logger.LogInformation("Default roles seeded");

        // Seed admin user if not exists
        long adminCount;
        await using (var cmd = new NpgsqlCommand(CheckAdminExistsSql, conn))
        {
            adminCount = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
        }

        if (adminCount == 0)
        {
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(DefaultAdminPassword, workFactor: 12);

            await using var cmd = new NpgsqlCommand(InsertAdminSql, conn);
            cmd.Parameters.AddWithValue("password_hash", passwordHash);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _logger.LogWarning("Default admin user created with password '{Password}' — CHANGE THIS ON FIRST LOGIN", DefaultAdminPassword);
        }
        else
        {
            _logger.LogInformation("Admin user already exists, skipping seed");
        }

        _logger.LogInformation("Auth database initialization complete");
    }
}
