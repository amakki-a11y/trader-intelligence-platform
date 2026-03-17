using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

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
/// - Thread-safe: AddTick can be called from multiple threads (simulator/real MT5 callbacks).
/// </summary>
public sealed class TickWriter : IDisposable
{
    private readonly ILogger<TickWriter> _logger;
    private readonly DbConnectionFactory _dbFactory;
    private readonly int _batchSize;
    private readonly int _flushIntervalMs;
    private readonly string _serverName;
    private readonly List<TickRow> _buffer;
    private readonly object _bufferLock = new();
    private long _totalWritten;
    private long _totalFlushed;

    /// <summary>
    /// SQL COPY command for bulk tick inserts via NpgsqlBinaryImporter.
    /// </summary>
    private const string CopySql =
        "COPY ticks (time, time_msc, symbol, bid, ask, server) FROM STDIN (FORMAT BINARY)";

    /// <summary>
    /// Initializes the tick writer with batching configuration.
    /// </summary>
    /// <param name="logger">Logger for write operations and errors.</param>
    /// <param name="dbFactory">Database connection factory.</param>
    /// <param name="batchSize">Number of ticks to accumulate before flushing (default 10000).</param>
    /// <param name="flushIntervalMs">Maximum time in ms before flushing a partial batch (default 1000).</param>
    /// <param name="serverName">MT5 server name tag for the server column.</param>
    public TickWriter(
        ILogger<TickWriter> logger,
        DbConnectionFactory dbFactory,
        int batchSize = 10000,
        int flushIntervalMs = 1000,
        string serverName = "")
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _batchSize = batchSize;
        _flushIntervalMs = flushIntervalMs;
        _serverName = serverName;
        _buffer = new List<TickRow>(_batchSize);
    }

    /// <summary>
    /// Total number of ticks added to the writer since startup.
    /// </summary>
    public long TotalWritten => Interlocked.Read(ref _totalWritten);

    /// <summary>
    /// Total number of flush operations completed since startup.
    /// </summary>
    public long TotalFlushed => Interlocked.Read(ref _totalFlushed);

    /// <summary>
    /// Current number of ticks buffered and waiting to be flushed.
    /// </summary>
    public int BufferedCount
    {
        get { lock (_bufferLock) { return _buffer.Count; } }
    }

    /// <summary>
    /// Batch size threshold that triggers a flush.
    /// </summary>
    public int BatchSize => _batchSize;

    /// <summary>
    /// Flush interval in milliseconds.
    /// </summary>
    public int FlushIntervalMs => _flushIntervalMs;

    /// <summary>
    /// Adds a tick to the internal buffer. When the buffer reaches batchSize, triggers a flush.
    /// Thread-safe — can be called from multiple producers.
    /// </summary>
    /// <param name="symbol">Trading instrument symbol.</param>
    /// <param name="bid">Bid price.</param>
    /// <param name="ask">Ask price.</param>
    /// <param name="timeMsc">Tick time in milliseconds since Unix epoch.</param>
    /// <returns>True if the batch is full and should be flushed.</returns>
    public bool AddTick(string symbol, double bid, double ask, long timeMsc)
    {
        var row = new TickRow
        {
            Time = DateTimeOffset.FromUnixTimeMilliseconds(timeMsc),
            TimeMsc = timeMsc,
            Symbol = symbol,
            Bid = bid,
            Ask = ask,
            Server = _serverName
        };

        Interlocked.Increment(ref _totalWritten);

        lock (_bufferLock)
        {
            _buffer.Add(row);
            return _buffer.Count >= _batchSize;
        }
    }

    /// <summary>
    /// Flushes all buffered ticks to TimescaleDB using the COPY protocol.
    /// Returns the number of ticks written in this flush.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of ticks written.</returns>
    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        List<TickRow> batch;

        lock (_bufferLock)
        {
            if (_buffer.Count == 0)
                return 0;

            batch = new List<TickRow>(_buffer);
            _buffer.Clear();
        }

        try
        {
            await using var conn = await _dbFactory.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var writer = await conn.BeginBinaryImportAsync(CopySql, cancellationToken)
                .ConfigureAwait(false);

            foreach (var row in batch)
            {
                await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(row.Time, NpgsqlDbType.TimestampTz, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(row.TimeMsc, NpgsqlDbType.Bigint, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(row.Symbol, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(row.Bid, NpgsqlDbType.Double, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(row.Ask, NpgsqlDbType.Double, cancellationToken).ConfigureAwait(false);
                await writer.WriteAsync(row.Server, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            }

            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);

            Interlocked.Increment(ref _totalFlushed);

            _logger.LogDebug(
                "Flushed {Count} ticks to TimescaleDB (total flushes: {TotalFlushed})",
                batch.Count, TotalFlushed);

            return batch.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Failed to flush {Count} ticks to TimescaleDB — re-buffering",
                batch.Count);

            // Re-buffer on failure so ticks aren't lost
            lock (_bufferLock)
            {
                _buffer.InsertRange(0, batch);
            }

            throw;
        }
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        // Nothing to dispose; DbConnectionFactory is owned by DI
    }

    /// <summary>
    /// Internal row struct for buffered tick data.
    /// </summary>
    private struct TickRow
    {
        public DateTimeOffset Time;
        public long TimeMsc;
        public string Symbol;
        public double Bid;
        public double Ask;
        public string Server;
    }

    // -------------------------------------------------------------------------
    // SQL Templates (target table: ticks)
    // -------------------------------------------------------------------------
    // COPY command for bulk insert (NpgsqlBinaryImporter):
    //   COPY ticks (time, time_msc, symbol, bid, ask, server)
    //   FROM STDIN (FORMAT BINARY)
    //
    // Single INSERT (fallback):
    //   INSERT INTO ticks (time, time_msc, symbol, bid, ask, server)
    //   VALUES (@time, @time_msc, @symbol, @bid, @ask, @server)
    //
    // Latest price per symbol (for MarketWatch cache warm-up):
    //   SELECT DISTINCT ON (symbol) symbol, bid, ask, time
    //   FROM ticks
    //   WHERE server = @server
    //   ORDER BY symbol, time DESC
}
