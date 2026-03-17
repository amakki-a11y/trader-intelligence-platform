using System;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Microsoft.Extensions.Logging;
using TIP.Api;
using TIP.Connector;
using TIP.Data;

// ── Serilog Bootstrap ─────────────────────────────────────────────────────────

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/tip-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Starting Trader Intelligence Platform API");

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // ── Channel<T> Pipelines ──────────────────────────────────────────────────

    var dealChannel = Channel.CreateUnbounded<DealEvent>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    var tickChannel = Channel.CreateUnbounded<TickEvent>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    builder.Services.AddSingleton(dealChannel);
    builder.Services.AddSingleton(dealChannel.Writer);
    builder.Services.AddSingleton(dealChannel.Reader);
    builder.Services.AddSingleton(tickChannel);
    builder.Services.AddSingleton(tickChannel.Writer);
    builder.Services.AddSingleton(tickChannel.Reader);

    // ── MT5 Connector ───────────────────────────────────────────────────────

    var mt5Config = builder.Configuration.GetSection("MT5");
    var useSimulator = mt5Config.GetValue<bool>("UseSimulator", true);

    // Register IMT5Api — simulator by default, real when DLLs available and configured
    if (useSimulator)
    {
        builder.Services.AddSingleton<IMT5Api, MT5ApiSimulator>();
        Log.Information("MT5 API: Using SIMULATOR mode");
    }
    else
    {
#if MT5_API_AVAILABLE
        builder.Services.AddSingleton<IMT5Api, MT5ApiReal>();
        Log.Information("MT5 API: Using REAL MT5 connection");
#else
        Log.Warning("MT5_API_AVAILABLE not defined — falling back to simulator");
        builder.Services.AddSingleton<IMT5Api, MT5ApiSimulator>();
#endif
    }

    // Read first server config (multi-server support in Stage 3)
    var serverSection = mt5Config.GetSection("Servers:0");
    var connectionConfig = new ConnectionConfig(
        ServerAddress: serverSection.GetValue<string>("ServerAddress") ?? "simulator:0",
        ManagerLogin: serverSection.GetValue<ulong>("ManagerLogin"),
        Password: serverSection.GetValue<string>("Password") ?? "",
        GroupMask: serverSection.GetValue<string>("GroupMask") ?? "*",
        HealthHeartbeatIntervalMs: serverSection.GetValue<int>("HealthHeartbeatIntervalMs", 30000));
    builder.Services.AddSingleton(connectionConfig);

    // Register connector services
    builder.Services.AddSingleton<DealSink>();
    builder.Services.AddSingleton<TickListener>();
    builder.Services.AddSingleton<SyncStateTracker>();
    builder.Services.AddSingleton<HistoryFetcher>();
    builder.Services.AddHostedService<MT5Connection>();

    // ── Services ──────────────────────────────────────────────────────────────

    builder.Services.AddControllers();

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.WithOrigins("http://localhost:5173")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        });
    });

    // ── Data Layer (TimescaleDB) ────────────────────────────────────────────

    var connString = builder.Configuration.GetConnectionString("TimescaleDB") ?? "";
    var dbEnabled = !string.IsNullOrEmpty(connString) && !connString.Contains("CHANGE_ME", StringComparison.Ordinal);

    if (dbEnabled)
    {
        Log.Information("TimescaleDB: ENABLED — connecting to database");
    }
    else
    {
        Log.Warning("TimescaleDB: DISABLED — connection string not configured (data will be logged only)");
    }

    var pipelineConfig = builder.Configuration.GetSection("Pipeline");
    var tickBatchSize = pipelineConfig.GetValue<int>("TickBatchSize", 10000);
    var tickFlushMs = pipelineConfig.GetValue<int>("TickFlushIntervalMs", 1000);
    var dealBatchSize = pipelineConfig.GetValue<int>("DealBatchSize", 500);
    var dealFlushMs = pipelineConfig.GetValue<int>("DealFlushIntervalMs", 2000);

    var dbFactory = new DbConnectionFactory(dbEnabled ? connString : "Host=localhost;Database=tip");
    builder.Services.AddSingleton(dbFactory);

    builder.Services.AddSingleton(sp => new TickWriter(
        sp.GetRequiredService<ILogger<TickWriter>>(),
        dbFactory,
        tickBatchSize,
        tickFlushMs));

    builder.Services.AddSingleton(sp => new DealRepository(
        sp.GetRequiredService<ILogger<DealRepository>>(),
        dbFactory));

    builder.Services.AddSingleton<TraderProfileRepository>();

    builder.Services.AddHostedService(sp => new TickWriterService(
        sp.GetRequiredService<ILogger<TickWriterService>>(),
        tickChannel.Reader,
        sp.GetRequiredService<TickWriter>(),
        dbEnabled));

    builder.Services.AddHostedService(sp => new DealWriterService(
        sp.GetRequiredService<ILogger<DealWriterService>>(),
        dealChannel.Reader,
        sp.GetRequiredService<DealRepository>(),
        dbEnabled,
        dealBatchSize,
        dealFlushMs));

    // TODO: Phase 3, Task 7 — Register RuleEngine, CorrelationEngine, PnLEngine, ExposureEngine
    // TODO: Phase 3, Task 11 — Register BotFingerprinter
    // TODO: Phase 5, Task 15 — Register SimulationEngine
    // TODO: Phase 5, Task 16 — Register StyleClassifier, BookRouter

    var app = builder.Build();

    // ── Middleware Pipeline ───────────────────────────────────────────────────

    app.UseSerilogRequestLogging();
    app.UseCors();
    app.UseWebSockets();
    app.MapControllers();

    // ── Health Check ──────────────────────────────────────────────────────────

    app.MapGet("/health", (IMT5Api api, TickListener ticks, TickWriter tw) => Results.Ok(new
    {
        status = "healthy",
        timestamp = DateTimeOffset.UtcNow,
        version = "2.0.0-alpha",
        mt5Connected = api.IsConnected,
        cachedSymbols = ticks.CachedSymbolCount,
        ticksIngested = tw.TotalWritten,
        tickFlushes = tw.TotalFlushed,
        ticksBuffered = tw.BufferedCount,
        dbEnabled
    }));

    // TODO: Phase 4, Task 13 — MapGet /api/ws for WebSocket connections (real-time dashboard feed)

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
