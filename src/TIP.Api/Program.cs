using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TIP.Api;
using TIP.Api.Hubs;
using TIP.Connector;
using TIP.Core.Configuration;
using TIP.Core.Engines;
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

    // Ensure user secrets are loaded (overrides appsettings.json placeholders)
    builder.Configuration.AddUserSecrets(typeof(DealerHub).Assembly, optional: true);

    // ── Scoring Configuration ─────────────────────────────────────────────────
    builder.Services.Configure<ScoringConfig>(builder.Configuration.GetSection("Scoring"));

    // ── Channel<T> Pipelines ──────────────────────────────────────────────────

    // Main ingest channels — bounded to prevent OOM under load
    var dealChannel = Channel.CreateBounded<DealEvent>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    var tickChannel = Channel.CreateBounded<TickEvent>(new BoundedChannelOptions(100_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });

    // Fan-out consumer channels — bounded
    var dealWriterChannel = Channel.CreateBounded<DealEvent>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true
    });

    var computeDealChannel = Channel.CreateBounded<DealEvent>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true
    });

    var tickWriterChannel = Channel.CreateBounded<TickEvent>(new BoundedChannelOptions(50_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true
    });

    var pnlTickChannel = Channel.CreateBounded<TickEvent>(new BoundedChannelOptions(50_000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true
    });

    builder.Services.AddSingleton(dealChannel);
    builder.Services.AddSingleton(dealChannel.Writer);
    builder.Services.AddSingleton(tickChannel);
    builder.Services.AddSingleton(tickChannel.Writer);

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
    builder.Services.AddSingleton<ConnectionManager>();

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

    // ── Resilience: Circuit Breakers + Health Tracker ─────────────────────
    var serviceHealthTracker = new TIP.Core.Resilience.ServiceHealthTracker();
    builder.Services.AddSingleton(serviceHealthTracker);

    builder.Services.AddSingleton(sp => new TIP.Core.Resilience.CircuitBreaker<int>(
        "database", failureThreshold: 5, openDuration: TimeSpan.FromSeconds(30),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("CircuitBreaker.Database")));

    builder.Services.AddSingleton(sp => new TIP.Core.Resilience.CircuitBreaker<List<RawDeal>>(
        "mt5-history", failureThreshold: 3, openDuration: TimeSpan.FromSeconds(60),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("CircuitBreaker.MT5History")));

    // Register connector services
    builder.Services.AddSingleton<DealSink>();
    builder.Services.AddSingleton<TickListener>();
    builder.Services.AddSingleton(sp => new SyncStateTracker(
        sp.GetRequiredService<ILogger<SyncStateTracker>>(),
        dbEnabled ? connString : null));
    builder.Services.AddSingleton(sp => new HistoryFetcher(
        sp.GetRequiredService<ILogger<HistoryFetcher>>(),
        sp.GetRequiredService<IMT5Api>(),
        sp.GetRequiredService<System.Threading.Channels.ChannelWriter<DealEvent>>(),
        sp.GetRequiredService<System.Threading.Channels.ChannelWriter<TickEvent>>(),
        sp.GetRequiredService<SyncStateTracker>(),
        sp.GetRequiredService<TIP.Core.Resilience.CircuitBreaker<List<RawDeal>>>()));
    builder.Services.AddSingleton<PipelineOrchestrator>();
    builder.Services.AddHostedService<MT5Connection>();

    // Register deal processor (pure logic, no I/O)
    builder.Services.AddSingleton(sp => new DealProcessor(
        sp.GetRequiredService<ILogger<DealProcessor>>()));

    // Register repositories
    builder.Services.AddSingleton(sp => new TickWriter(
        sp.GetRequiredService<ILogger<TickWriter>>(),
        dbFactory,
        tickBatchSize,
        tickFlushMs));

    builder.Services.AddSingleton(sp => new DealRepository(
        sp.GetRequiredService<ILogger<DealRepository>>(),
        dbFactory,
        connectionConfig.ServerAddress));

    builder.Services.AddSingleton<TraderProfileRepository>();
    builder.Services.AddSingleton<PositionRepository>();
    builder.Services.AddSingleton<AccountRepository>();

    builder.Services.AddHostedService(sp => new TickWriterService(
        sp.GetRequiredService<ILogger<TickWriterService>>(),
        tickWriterChannel.Reader,
        sp.GetRequiredService<TickWriter>(),
        sp.GetRequiredService<TIP.Core.Resilience.CircuitBreaker<int>>(),
        serviceHealthTracker,
        dbEnabled));

    builder.Services.AddHostedService(sp => new DealWriterService(
        sp.GetRequiredService<ILogger<DealWriterService>>(),
        dealWriterChannel.Reader,
        sp.GetRequiredService<DealRepository>(),
        sp.GetRequiredService<TIP.Core.Resilience.CircuitBreaker<int>>(),
        serviceHealthTracker,
        dbEnabled,
        dealBatchSize,
        dealFlushMs,
        connectionConfig.ServerAddress));

    // Fan-out service: reads main channels, writes to all consumer channels
    builder.Services.AddHostedService(sp => new ChannelFanOutService(
        sp.GetRequiredService<ILogger<ChannelFanOutService>>(),
        dealChannel.Reader,
        new[] { dealWriterChannel.Writer, computeDealChannel.Writer },
        tickChannel.Reader,
        new[] { tickWriterChannel.Writer, pnlTickChannel.Writer }));

    // ── Rate Limiting (FIX 7) ───────────────────────────────────────────────

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = 429;
        options.AddFixedWindowLimiter("scan", opt =>
        {
            opt.PermitLimit = 2;
            opt.Window = TimeSpan.FromMinutes(1);
            opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            opt.QueueLimit = 0;
        });
        options.AddFixedWindowLimiter("api", opt =>
        {
            opt.PermitLimit = 30;
            opt.Window = TimeSpan.FromMinutes(1);
            opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            opt.QueueLimit = 0;
        });
    });

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

    // ── Compute Engines (Phase 3) ────────────────────────────────────────────

    // Default rule set for scoring
    var defaultRules = new[]
    {
        new Rule(RuleMetric.TotalTrades, RuleOperator.GreaterThan, 1000, 10, "High trade count"),
        new Rule(RuleMetric.CommissionToVolumeRatio, RuleOperator.LessThan, 0.5, 15, "Low commission-to-volume ratio"),
        new Rule(RuleMetric.ProfitToCommissionRatio, RuleOperator.LessThan, 0.1, 15, "Near-zero profit-to-commission ratio"),
        new Rule(RuleMetric.TimingEntropyCV, RuleOperator.LessThan, 0.1, 20, "Robotic timing precision"),
        new Rule(RuleMetric.ExpertTradeRatio, RuleOperator.GreaterThan, 0.95, 10, "Almost all EA trades"),
        new Rule(RuleMetric.TradesPerHour, RuleOperator.GreaterThan, 50, 15, "Superhuman trade frequency"),
        new Rule(RuleMetric.BonusToDepositRatio, RuleOperator.GreaterThan, 0.5, 10, "High bonus-to-deposit ratio"),
        new Rule(RuleMetric.IsRingMember, RuleOperator.Equal, 1.0, 20, "Part of correlated trading ring"),
        new Rule(RuleMetric.RingCorrelationCount, RuleOperator.GreaterThan, 5, 10, "High ring correlation count"),
        new Rule(RuleMetric.AvgVolumeLots, RuleOperator.LessOrEqual, 0.01, 5, "Micro lot farming"),
    };

    builder.Services.AddSingleton(new RuleEngine(defaultRules));
    builder.Services.AddSingleton<CorrelationEngine>();
    builder.Services.AddSingleton(sp =>
    {
        var symbolCache = sp.GetRequiredService<SymbolCache>();
        return new PnLEngine(
            sp.GetRequiredService<ILogger<PnLEngine>>(),
            symbol => symbolCache.GetContractSize(symbol));
    });
    builder.Services.AddSingleton<ExposureEngine>();
    builder.Services.AddSingleton<AccountScorer>();
    builder.Services.AddSingleton<BotFingerprinter>();
    builder.Services.AddSingleton<PriceCache>();
    builder.Services.AddSingleton<SymbolCache>();
    builder.Services.AddSingleton<DealerHub>();
    builder.Services.AddSingleton<IWebSocketBroadcaster>(sp => sp.GetRequiredService<DealerHub>());

    // ── Background Compute Services ──────────────────────────────────────

    builder.Services.AddHostedService(sp => new PnLEngineService(
        sp.GetRequiredService<ILogger<PnLEngineService>>(),
        pnlTickChannel.Reader,
        sp.GetRequiredService<PnLEngine>(),
        sp.GetRequiredService<PipelineOrchestrator>(),
        sp.GetRequiredService<ExposureEngine>(),
        sp.GetRequiredService<IWebSocketBroadcaster>(),
        sp.GetRequiredService<PriceCache>(),
        sp.GetRequiredService<SymbolCache>(),
        serviceHealthTracker));

    builder.Services.AddHostedService(sp => new ComputeEngineService(
        sp.GetRequiredService<ILogger<ComputeEngineService>>(),
        computeDealChannel.Reader,
        sp.GetRequiredService<DealProcessor>(),
        sp.GetRequiredService<AccountScorer>(),
        sp.GetRequiredService<CorrelationEngine>(),
        sp.GetRequiredService<PnLEngine>(),
        sp.GetRequiredService<ExposureEngine>(),
        sp.GetRequiredService<PipelineOrchestrator>(),
        sp.GetRequiredService<IWebSocketBroadcaster>(),
        sp.GetRequiredService<AccountRepository>(),
        sp.GetRequiredService<TIP.Data.DealRepository>(),
        serviceHealthTracker,
        dbEnabled));

    // ── Intelligence Engines (Phase 5) ──────────────────────────────────────

    builder.Services.AddSingleton<StyleClassifier>();
    builder.Services.AddSingleton<BookRouter>();
    builder.Services.AddSingleton<SimulationEngine>();

    builder.Services.AddHostedService(sp => new IntelligenceService(
        sp.GetRequiredService<ILogger<IntelligenceService>>(),
        sp.GetRequiredService<AccountScorer>(),
        sp.GetRequiredService<StyleClassifier>(),
        sp.GetRequiredService<BookRouter>(),
        sp.GetRequiredService<PipelineOrchestrator>(),
        sp.GetRequiredService<TraderProfileRepository>(),
        serviceHealthTracker,
        dbEnabled));

    // ── Startup Warmup (symbol cache + positions) ────────────────────────
    builder.Services.AddHostedService<TIP.Api.Services.StartupWarmupService>();

    // ── Session Reset (daily price cache reset) ──────────────────────────
    builder.Services.AddHostedService<TIP.Api.Services.SessionResetService>();

    var app = builder.Build();

    // ── Middleware Pipeline ───────────────────────────────────────────────────

    app.UseMiddleware<TIP.Api.Middleware.GlobalExceptionMiddleware>();
    app.UseSerilogRequestLogging();
    app.UseCors();
    app.UseRateLimiter();
    app.UseWebSockets();
    app.MapControllers();

    // ── Health Check ──────────────────────────────────────────────────────────

    app.MapGet("/health", (
        IMT5Api api,
        TickListener ticks,
        TickWriter tw,
        PipelineOrchestrator orchestrator,
        PnLEngine pnlEngine,
        ExposureEngine exposureEngine,
        AccountScorer accountScorer,
        CorrelationEngine correlationEngine,
        TIP.Core.Resilience.CircuitBreaker<int> dbCb,
        TIP.Core.Resilience.CircuitBreaker<List<RawDeal>> mt5Cb) =>
    {
        // Build service health snapshot
        var allMetrics = serviceHealthTracker.GetAll();
        var serviceStats = new Dictionary<string, object>();
        foreach (var kvp in allMetrics)
        {
            serviceStats[kvp.Key] = new
            {
                consecutiveErrors = kvp.Value.GetConsecutiveErrors(),
                totalProcessed = kvp.Value.GetTotalProcessed()
            };
        }

        return Results.Ok(new
        {
            status = "healthy",
            timestamp = DateTimeOffset.UtcNow,
            version = "2.0.0-alpha",
            mt5Connected = api.IsConnected,
            cachedSymbols = ticks.CachedSymbolCount,
            ticksIngested = tw.TotalWritten,
            tickFlushes = tw.TotalFlushed,
            ticksBuffered = tw.BufferedCount,
            dbEnabled,
            circuits = new
            {
                database = dbCb.State.ToString(),
                mt5History = mt5Cb.State.ToString()
            },
            services = serviceStats,
            pipeline = new
            {
                state = orchestrator.State.ToString(),
                backfilledDeals = orchestrator.BackfilledDeals,
                backfilledTicks = orchestrator.BackfilledTicks,
                bufferedReplayed = orchestrator.BufferedReplayed,
                duplicatesSkipped = orchestrator.DuplicatesSkipped
            },
            compute = new
            {
                trackedPositions = pnlEngine.TrackedPositionCount,
                totalUnrealizedPnL = pnlEngine.TotalUnrealizedPnL,
                exposureSymbols = exposureEngine.SymbolCount,
                scoredAccounts = accountScorer.AccountCount,
                riskCounts = accountScorer.GetRiskCounts(),
                correlationPairs = correlationEngine.PairCount,
                indexedFingerprints = correlationEngine.IndexedCount,
                maxFingerprints = correlationEngine.MaxFingerprints
            }
        });
    });

    // ── WebSocket Endpoint ────────────────────────────────────────────────

    app.Map("/ws", async (HttpContext context, DealerHub hub) =>
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var ws = await context.WebSockets.AcceptWebSocketAsync();
        await hub.HandleConnection(ws, context.RequestAborted);
    });

    // Symbol cache + position loading handled by StartupWarmupService (registered as IHostedService)

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
