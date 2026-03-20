using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace TIP.Data.Auth;

/// <summary>
/// Factory for creating Npgsql connections to the tip_auth PostgreSQL database.
///
/// Design rationale:
/// - Separate from DbConnectionFactory (TimescaleDB) because auth data has different
///   backup/security requirements and is plain PostgreSQL (no hypertables).
/// - Uses NpgsqlDataSource for built-in connection pooling.
/// </summary>
public sealed class AuthDbConnectionFactory : IDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// The connection string used by this factory.
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Initializes the factory with a tip_auth connection string.
    /// </summary>
    /// <param name="connectionString">Npgsql connection string for tip_auth database.</param>
    public AuthDbConnectionFactory(string connectionString)
    {
        ConnectionString = connectionString;
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>
    /// Opens a new connection to the auth database.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An open NpgsqlConnection.</returns>
    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        return await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a new connection (not yet opened).
    /// </summary>
    /// <returns>A closed NpgsqlConnection that must be opened before use.</returns>
    public NpgsqlConnection CreateConnection()
    {
        return _dataSource.CreateConnection();
    }

    /// <summary>
    /// Disposes the underlying data source and its connection pool.
    /// </summary>
    public void Dispose()
    {
        _dataSource.Dispose();
    }
}
