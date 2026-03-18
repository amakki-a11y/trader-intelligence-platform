using System;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TIP.Api;
using TIP.Api.Hubs;
using TIP.Connector;
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

    // ── Channel<T> Pipelines ──────────────────────────────────────────────────

    // Main ingest channels — DealSink and TickListener write here, fan-out service reads
    var dealChannel = Channel.CreateUnbounded<DealEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    var tickChannel = Channel.CreateUnbounded<TickEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    // Fan-out consumer channels
    var dealWriterChannel = Channel.CreateUnbounded<DealEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });

    var computeDealChannel = Channel.CreateUnbounded<DealEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });

    var tickWriterChannel = Channel.CreateUnbounded<TickEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });

    var pnlTickChannel = Channel.CreateUnbounded<TickEvent>(new UnboundedChannelOptions
    {
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

    // Register connector services
    builder.Services.AddSingleton<DealSink>();
    builder.Services.AddSingleton<TickListener>();
    builder.Services.AddSingleton(sp => new SyncStateTracker(
        sp.GetRequiredService<ILogger<SyncStateTracker>>(),
        dbEnabled ? connString : null));
    builder.Services.AddSingleton<HistoryFetcher>();
    builder.Services.AddSingleton<PipelineOrchestrator>();
    builder.Services.AddHostedService<MT5Connection>();

    // Register deal processor (pure logic, no I/O)
    builder.Services.AddSingleton<DealProcessor>();

    // Register repositories
    builder.Services.AddSingleton(sp => new TickWriter(
        sp.GetRequiredService<ILogger<TickWriter>>(),
        dbFactory,
        tickBatchSize,
        tickFlushMs));

    builder.Services.AddSingleton(sp => new DealRepository(
        sp.GetRequiredService<ILogger<DealRepository>>(),
        dbFactory));

    builder.Services.AddSingleton<TraderProfileRepository>();
    builder.Services.AddSingleton<PositionRepository>();
    builder.Services.AddSingleton<AccountRepository>();

    builder.Services.AddHostedService(sp => new TickWriterService(
        sp.GetRequiredService<ILogger<TickWriterService>>(),
        tickWriterChannel.Reader,
        sp.GetRequiredService<TickWriter>(),
        dbEnabled));

    builder.Services.AddHostedService(sp => new DealWriterService(
        sp.GetRequiredService<ILogger<DealWriterService>>(),
        dealWriterChannel.Reader,
        sp.GetRequiredService<DealRepository>(),
        dbEnabled,
        dealBatchSize,
        dealFlushMs));

    // Fan-out service: reads main channels, writes to all consumer channels
    builder.Services.AddHostedService(sp => new ChannelFanOutService(
        sp.GetRequiredService<ILogger<ChannelFanOutService>>(),
        dealChannel.Reader,
        new[] { dealWriterChannel.Writer, computeDealChannel.Writer },
        tickChannel.Reader,
        new[] { tickWriterChannel.Writer, pnlTickChannel.Writer }));

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
    builder.Services.AddSingleton<PnLEngine>();
    builder.Services.AddSingleton<ExposureEngine>();
    builder.Services.AddSingleton<AccountScorer>();
    builder.Services.AddSingleton<BotFingerprinter>();
    builder.Services.AddSingleton<PriceCache>();
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
        sp.GetRequiredService<PriceCache>()));

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
        dbEnabled));

    var app = builder.Build();

    // ── Middleware Pipeline ───────────────────────────────────────────────────

    app.UseSerilogRequestLogging();
    app.UseCors();
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
        CorrelationEngine correlationEngine) => Results.Ok(new
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
            indexedFingerprints = correlationEngine.IndexedCount
        }
    }));

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

    // ── Seed Price Cache from MT5 ──────────────────────────────────────────
    // After pipeline connects, seed the price cache with last known symbol prices
    _ = Task.Run(async () =>
    {
        var priceCache = app.Services.GetRequiredService<PriceCache>();
        var mt5Api = app.Services.GetRequiredService<IMT5Api>();
        var orchestrator = app.Services.GetRequiredService<PipelineOrchestrator>();
        var logger = app.Services.GetRequiredService<ILogger<PriceCache>>();

        // Wait for pipeline to reach at least Buffering (MT5 connected)
        while (orchestrator.State < PipelineOrchestratorState.Buffering)
        {
            await Task.Delay(1000);
        }

        try
        {
            // Phase 1: Try batch TickLast — gets all symbols with pump data in one call
            var batchTicks = mt5Api.GetTickLastBatch();
            var seeded = 0;
            foreach (var tick in batchTicks)
            {
                if (tick.Bid > 0)
                {
                    priceCache.Seed(tick.Symbol, tick.Bid, tick.Ask, tick.TimeMsc);
                    seeded++;
                }
            }

            logger.LogInformation("PriceCache: Batch TickLast seeded {Count} symbols", seeded);

            // Phase 2: For remaining symbols, try TickStat (has data even without recent ticks)
            var allSymbols = mt5Api.GetSymbols();
            var statSeeded = 0;
            foreach (var rawSym in allSymbols)
            {
                // Skip if already seeded from batch
                if (priceCache.Get(rawSym.Symbol) != null) continue;

                try
                {
                    // Try TickLast first
                    var tick = mt5Api.GetTickLast(rawSym.Symbol);
                    if (tick != null && tick.Bid > 0)
                    {
                        priceCache.Seed(tick.Symbol, tick.Bid, tick.Ask, tick.TimeMsc);
                        statSeeded++;
                        continue;
                    }

                    // Fall back to TickStat
                    var stat = mt5Api.GetTickStat(rawSym.Symbol);
                    if (stat != null && stat.Bid > 0)
                    {
                        priceCache.Seed(stat.Symbol, stat.Bid, stat.Ask, stat.TimeMsc);
                        statSeeded++;
                    }
                }
                catch
                {
                    // Skip symbols without any price data
                }
            }

            logger.LogInformation(
                "PriceCache: Seeded {Total} symbols total ({Batch} from batch, {Stat} from individual TickLast/TickStat)",
                seeded + statSeeded, seeded, statSeeded);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PriceCache: Failed to seed from MT5 symbols");
        }

        // Phase 3: Periodic refresh — poll every 30s for symbols that don't have live tick data
        while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(30000, app.Lifetime.ApplicationStopping);

                var batchRefresh = mt5Api.GetTickLastBatch();
                var refreshed = 0;
                foreach (var tick in batchRefresh)
                {
                    if (tick.Bid > 0)
                    {
                        priceCache.Update(tick.Symbol, tick.Bid, tick.Ask, tick.TimeMsc);
                        refreshed++;
                    }
                }

                if (refreshed > priceCache.Count)
                    logger.LogInformation("PriceCache: Batch refresh updated {Count} symbols (cache has {CacheCount})",
                        refreshed, priceCache.Count);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "PriceCache: Refresh cycle failed");
            }
        }
    });

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
