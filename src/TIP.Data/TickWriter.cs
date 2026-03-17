using System;
using Microsoft.Extensions.Logging;

namespace TIP.Data;

/// <summary>
/// High-throughput tick writer using PostgreSQL COPY protocol for bulk inserts into TimescaleDB.
///
/// Design rationale:
/// - COPY protocol is 10-50x faster than individual INSERTs for time-series data.
/// - Batches ticks in memory until batchSize is reached or flushIntervalMs elapses,
///   whichever comes first, then writes the entire batch in a single COPY operation.
/// - Separate from DealRepository because tick ingestion has fundamentally different
///   performance characteristics (100K+ ticks/second vs ~100 deals/second).
/// </summary>
public class TickWriter
{
    private readonly ILogger<TickWriter> _logger;
    private readonly string _connectionString;
    private readonly int _batchSize;
    private readonly int _flushIntervalMs;

    /// <summary>
    /// Initializes the tick writer with batching configuration.
    /// </summary>
    /// <param name="logger">Logger for write operations and errors.</param>
    /// <param name="connectionString">TimescaleDB connection string.</param>
    /// <param name="batchSize">Number of ticks to accumulate before flushing (default 10000).</param>
    /// <param name="flushIntervalMs">Maximum time in ms before flushing a partial batch (default 1000).</param>
    public TickWriter(
        ILogger<TickWriter> logger,
        string connectionString,
        int batchSize = 10000,
        int flushIntervalMs = 1000)
    {
        _logger = logger;
        _connectionString = connectionString;
        _batchSize = batchSize;
        _flushIntervalMs = flushIntervalMs;
    }

    // TODO: Phase 2, Task 6 — Implement AddTick(TickEvent tick) — adds to internal buffer
    // TODO: Phase 2, Task 6 — Implement FlushAsync() — writes batch via NpgsqlBinaryImporter (COPY protocol)
    // TODO: Phase 2, Task 6 — Implement StartBackgroundFlusher(CancellationToken) — timer-based flush loop
}
