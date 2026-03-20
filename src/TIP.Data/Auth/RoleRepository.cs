using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TIP.Data.Auth;

/// <summary>
/// Repository for reading roles from the tip_auth database.
///
/// Design rationale:
/// - Roles are read-only in v2.0 (custom role creation deferred to v2.1).
/// - Permissions stored as JSONB array for flexible permission checks.
/// </summary>
public sealed class RoleRepository
{
    private readonly ILogger<RoleRepository> _logger;
    private readonly AuthDbConnectionFactory _dbFactory;

    private const string SelectAllSql =
        "SELECT id, name, description, permissions::text, created_at, updated_at FROM roles ORDER BY id";

    private const string SelectByIdSql =
        "SELECT id, name, description, permissions::text, created_at, updated_at FROM roles WHERE id = @id";

    private const string SelectByNameSql =
        "SELECT id, name, description, permissions::text, created_at, updated_at FROM roles WHERE name = @name";

    /// <summary>
    /// Initializes the role repository.
    /// </summary>
    /// <param name="logger">Logger for query operations.</param>
    /// <param name="dbFactory">Auth database connection factory.</param>
    public RoleRepository(ILogger<RoleRepository> logger, AuthDbConnectionFactory dbFactory)
    {
        _logger = logger;
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Gets all roles.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All roles in the system.</returns>
    public async Task<IReadOnlyList<RoleRecord>> GetAllAsync(CancellationToken ct = default)
    {
        var results = new List<RoleRecord>();

        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectAllSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(ReadRole(reader));
        }

        return results;
    }

    /// <summary>
    /// Gets a role by ID.
    /// </summary>
    /// <param name="id">Role ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Role record, or null if not found.</returns>
    public async Task<RoleRecord?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectByIdSql, conn);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRole(reader) : null;
    }

    /// <summary>
    /// Gets a role by name.
    /// </summary>
    /// <param name="name">Role name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Role record, or null if not found.</returns>
    public async Task<RoleRecord?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        await using var conn = await _dbFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(SelectByNameSql, conn);
        cmd.Parameters.AddWithValue("name", name);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRole(reader) : null;
    }

    /// <summary>
    /// Reads a role record from a data reader row.
    /// </summary>
    private static RoleRecord ReadRole(NpgsqlDataReader reader)
    {
        return new RoleRecord
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            Description = reader.GetString(2),
            PermissionsJson = reader.GetString(3),
            CreatedAt = reader.GetDateTime(4),
            UpdatedAt = reader.GetDateTime(5)
        };
    }
}

/// <summary>
/// Record mapping to the "roles" table in tip_auth.
/// </summary>
public sealed class RoleRecord
{
    /// <summary>Auto-incrementing role ID.</summary>
    public int Id { get; set; }

    /// <summary>Role name (admin, dealer, compliance).</summary>
    public string Name { get; set; } = "";

    /// <summary>Human-readable role description.</summary>
    public string Description { get; set; } = "";

    /// <summary>JSONB array of permission strings, serialized as text.</summary>
    public string PermissionsJson { get; set; } = "[]";

    /// <summary>When the role was created.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the role was last updated.</summary>
    public DateTime UpdatedAt { get; set; }
}
