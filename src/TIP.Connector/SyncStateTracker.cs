using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace TIP.Connector;

/// <summary>
/// Tracks synchronization checkpoints for catch-up sync on reconnection.
///
/// Design rationale:
/// - After a disconnect, we need to know the last successfully synced timestamp
///   for each entity type (deals, ticks) and entity (login, symbol) so we can
///   resume backfill from the correct point without re-fetching everything.
/// - Uses an in-memory ConcurrentDictionary for now; will be persisted to the
///   sync_state table in TimescaleDB once the data layer is connected.
/// </summary>
public sealed class SyncStateTracker
{
    private readonly ILogger<SyncStateTracker> _logger;
    private readonly ConcurrentDictionary<(string EntityType, string EntityId), DateTimeOffset> _checkpoints = new();

    /// <summary>
    /// Initializes the sync state tracker.
    /// </summary>
    /// <param name="logger">Logger for checkpoint operations.</param>
    public SyncStateTracker(ILogger<SyncStateTracker> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the last sync timestamp for the given entity type and ID.
    /// Returns null if no checkpoint exists (meaning a full backfill is needed).
    /// </summary>
    /// <param name="entityType">Type of entity (e.g., "deals", "ticks").</param>
    /// <param name="entityId">Entity identifier (e.g., login number or symbol name).</param>
    /// <returns>Last synced timestamp, or null if never synced.</returns>
    public DateTimeOffset? GetLastSyncTimestamp(string entityType, string entityId)
    {
        // TODO: Phase 2, Task 5 — Query sync_state table: SELECT last_sync FROM sync_state WHERE entity_type = @type AND entity_id = @id

        return _checkpoints.TryGetValue((entityType, entityId), out var timestamp)
            ? timestamp
            : null;
    }

    /// <summary>
    /// Updates the checkpoint for the given entity type and ID.
    /// Called after a batch of events has been successfully persisted.
    /// </summary>
    /// <param name="entityType">Type of entity (e.g., "deals", "ticks").</param>
    /// <param name="entityId">Entity identifier (e.g., login number or symbol name).</param>
    /// <param name="timestamp">Timestamp of the last successfully synced event.</param>
    public void UpdateCheckpoint(string entityType, string entityId, DateTimeOffset timestamp)
    {
        // TODO: Phase 2, Task 5 — Upsert sync_state table: INSERT INTO sync_state ... ON CONFLICT DO UPDATE

        _checkpoints[(entityType, entityId)] = timestamp;

        _logger.LogDebug(
            "Checkpoint updated: {EntityType}/{EntityId} → {Timestamp}",
            entityType, entityId, timestamp);
    }
}
