using System;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using TIP.Connector;

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

    // TODO: Phase 2, Task 3 — Register MT5Connection as hosted service
    // TODO: Phase 2, Task 3 — Register DealSink, TickListener, HistoryFetcher, SyncStateTracker
    // TODO: Phase 2, Task 6 — Register DealRepository, TickWriter, TraderProfileRepository
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

    app.MapGet("/health", () => Results.Ok(new
    {
        status = "healthy",
        timestamp = DateTimeOffset.UtcNow,
        version = "2.0.0-alpha"
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
