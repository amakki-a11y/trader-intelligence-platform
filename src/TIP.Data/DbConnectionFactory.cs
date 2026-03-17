using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace TIP.Data;

/// <summary>
/// Factory for creating Npgsql connections to TimescaleDB.
///
/// Design rationale:
/// - Centralizes connection creation so connection string management and
///   NpgsqlDataSource lifecycle are in one place.
/// - NpgsqlDataSource provides built-in connection pooling and multiplexing.
/// - All repositories and writers share the same data source for efficient
///   connection reuse.
/// </summary>
public sealed class DbConnectionFactory : IDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>
    /// The connection string used by this factory.
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Initializes the factory with a TimescaleDB connection string.
    /// </summary>
    /// <param name="connectionString">Npgsql connection string for TimescaleDB.</param>
    public DbConnectionFactory(string connectionString)
    {
        ConnectionString = connectionString;
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>
    /// Opens a new connection to TimescaleDB.
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
