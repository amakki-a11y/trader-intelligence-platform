using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TIP.Connector;

/// <summary>
/// Tracks synchronization checkpoints for catch-up sync on reconnection.
/// Database-backed with in-memory fallback if DB is unavailable.
///
/// Design rationale:
/// - After a disconnect, we need to know the last successfully synced timestamp
///   for each entity type (deals, ticks) and entity (login, symbol) so we can
///   resume backfill from the correct point without re-fetching everything.
/// - In-memory ConcurrentDictionary provides fast reads during pipeline operation.
/// - On startup, checkpoints are loaded from the sync_state table into memory.
/// - On checkpoint update, we write to memory immediately; DB persistence happens
///   in batch via FlushToDatabase() at the end of backfill.
/// - If DB is unavailable, falls back to in-memory only (fresh start on restart).
/// </summary>
public sealed class SyncStateTracker
{
    private readonly ILogger<SyncStateTracker> _logger;
    private readonly ConcurrentDictionary<(string EntityType, string EntityId), CheckpointEntry> _checkpoints = new();
    private readonly string? _connectionString;
    private bool _dbAvailable;

    /// <summary>
    /// Initializes the sync state tracker with optional DB connection.
    /// </summary>
    /// <param name="logger">Logger for checkpoint operations.</param>
    /// <param name="connectionString">Npgsql connection string, or null/empty to use in-memory only.</param>
    public SyncStateTracker(ILogger<SyncStateTracker> logger, string? connectionString = null)
    {
        _logger = logger;
        _connectionString = connectionString;
        _dbAvailable = !string.IsNullOrEmpty(connectionString) &&
                       !connectionString.Contains("CHANGE_ME", StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets the last sync timestamp for the given entity type and ID.
    /// Returns null if no checkpoint exists (meaning a full backfill is needed).
    /// </summary>
    /// <param name="entityType">Type of entity (e.g., "deal_login", "tick_symbol").</param>
    /// <param name="entityId">Entity identifier (e.g., login number or symbol name).</param>
    /// <returns>Last synced timestamp, or null if never synced.</returns>
    public DateTimeOffset? GetLastSyncTimestamp(string entityType, string entityId)
    {
        return _checkpoints.TryGetValue((entityType, entityId), out var entry)
            ? entry.Timestamp
            : null;
    }

    /// <summary>
    /// Gets the last sync timestamp for the given entity type, ID, and server.
    /// Returns null if no checkpoint exists.
    /// </summary>
    /// <param name="entityType">Type of entity (e.g., "deal_login", "tick_symbol").</param>
    /// <param name="entityId">Entity identifier (e.g., login number or symbol name).</param>
    /// <param name="server">MT5 server identifier.</param>
    /// <returns>Last synced timestamp, or null if never synced.</returns>
    public DateTimeOffset? GetLastSyncTimestamp(string entityType, string entityId, string server)
    {
        var key = string.IsNullOrEmpty(server) ? entityId : $"{server}:{entityId}";
        return _checkpoints.TryGetValue((entityType, key), out var entry)
            ? entry.Timestamp
            : null;
    }

    /// <summary>
    /// Updates the checkpoint for the given entity type and ID.
    /// Called after a batch of events has been successfully persisted.
    /// </summary>
    /// <param name="entityType">Type of entity (e.g., "deal_login", "tick_symbol").</param>
    /// <param name="entityId">Entity identifier (e.g., login number or symbol name).</param>
    /// <param name="timestamp">Timestamp of the last successfully synced event.</param>
    public void UpdateCheckpoint(string entityType, string entityId, DateTimeOffset timestamp)
    {
        _checkpoints[(entityType, entityId)] = new CheckpointEntry(timestamp, Dirty: true);

        _logger.LogDebug(
            "Checkpoint updated: {EntityType}/{EntityId} → {Timestamp}",
            entityType, entityId, timestamp);
    }

    /// <summary>
    /// Updates the checkpoint for the given entity type, ID, and server.
    /// </summary>
    /// <param name="entityType">Type of entity.</param>
    /// <param name="entityId">Entity identifier.</param>
    /// <param name="server">MT5 server identifier.</param>
    /// <param name="timestamp">Timestamp of the last successfully synced event.</param>
    public void UpdateCheckpoint(string entityType, string entityId, string server, DateTimeOffset timestamp)
    {
        var key = string.IsNullOrEmpty(server) ? entityId : $"{server}:{entityId}";
        _checkpoints[(entityType, key)] = new CheckpointEntry(timestamp, Dirty: true);

        _logger.LogDebug(
            "Checkpoint updated: {EntityType}/{EntityId}@{Server} → {Timestamp}",
            entityType, entityId, server, timestamp);
    }

    /// <summary>
    /// Loads all checkpoints from the sync_state database table into memory.
    /// Falls back to in-memory only if DB is unavailable.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task LoadFromDatabase(CancellationToken ct = default)
    {
        if (!_dbAvailable || string.IsNullOrEmpty(_connectionString))
        {
            _logger.LogInformation("SyncStateTracker: DB not available — using in-memory checkpoints only");
            return;
        }

        try
        {
            await using var conn = new Npgsql.NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = new Npgsql.NpgsqlCommand(
                "SELECT entity_type, entity_id, last_sync, records_synced, server FROM sync_state", conn);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

            var count = 0;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var entityType = reader.GetString(0);
                var entityId = reader.GetString(1);
                var lastSync = reader.GetFieldValue<DateTimeOffset>(2);
                var server = reader.IsDBNull(4) ? "" : reader.GetString(4);

                var key = string.IsNullOrEmpty(server) ? entityId : $"{server}:{entityId}";
                _checkpoints[(entityType, key)] = new CheckpointEntry(lastSync, Dirty: false);
                count++;
            }

            _logger.LogInformation("SyncStateTracker: loaded {Count} checkpoints from database", count);
        }
        catch (Exception ex)
        {
            _dbAvailable = false;
            _logger.LogWarning(ex, "SyncStateTracker: failed to load from DB — falling back to in-memory");
        }
    }

    /// <summary>
    /// Persists all dirty checkpoints to the sync_state database table.
    /// Uses INSERT ON CONFLICT UPDATE for idempotent upserts.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task FlushToDatabase(CancellationToken ct = default)
    {
        if (!_dbAvailable || string.IsNullOrEmpty(_connectionString))
        {
            return;
        }

        try
        {
            await using var conn = new Npgsql.NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var flushed = 0;

            foreach (var kvp in _checkpoints)
            {
                if (!kvp.Value.Dirty)
                    continue;

                var (entityType, rawId) = kvp.Key;
                var server = "";
                var entityId = rawId;

                // Parse server:entityId format
                var colonIdx = rawId.IndexOf(':');
                if (colonIdx > 0)
                {
                    server = rawId[..colonIdx];
                    entityId = rawId[(colonIdx + 1)..];
                }

                await using var cmd = new Npgsql.NpgsqlCommand(
                    "INSERT INTO sync_state (entity_type, entity_id, last_sync, server) " +
                    "VALUES (@entity_type, @entity_id, @last_sync, @server) " +
                    "ON CONFLICT (entity_type, entity_id) DO UPDATE SET " +
                    "last_sync = EXCLUDED.last_sync, server = EXCLUDED.server", conn);

                cmd.Parameters.AddWithValue("entity_type", entityType);
                cmd.Parameters.AddWithValue("entity_id", entityId);
                cmd.Parameters.AddWithValue("last_sync", kvp.Value.Timestamp);
                cmd.Parameters.AddWithValue("server", server);

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                // Mark as clean
                _checkpoints[kvp.Key] = kvp.Value with { Dirty = false };
                flushed++;
            }

            if (flushed > 0)
            {
                _logger.LogInformation("SyncStateTracker: flushed {Count} checkpoints to database", flushed);
            }
        }
        catch (Exception ex)
        {
            _dbAvailable = false;
            _logger.LogWarning(ex, "SyncStateTracker: failed to flush to DB — checkpoints preserved in memory");
        }
    }

    /// <summary>
    /// Gets the total number of tracked checkpoints.
    /// </summary>
    public int CheckpointCount => _checkpoints.Count;
}

/// <summary>
/// Internal checkpoint entry with dirty tracking for DB persistence.
/// </summary>
/// <param name="Timestamp">Last sync timestamp.</param>
/// <param name="Dirty">True if not yet persisted to DB.</param>
internal sealed record CheckpointEntry(DateTimeOffset Timestamp, bool Dirty);
