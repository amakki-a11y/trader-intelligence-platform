using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TIP.Api.Services;

/// <summary>
/// Daily background service that resets PriceCache session data at the configured
/// forex session open time (default: 00:00 UTC). Without this, multi-day uptime
/// causes stale SessionOpenBid values and meaningless daily change percentages.
/// </summary>
public sealed class SessionResetService : BackgroundService
{
    private readonly PriceCache _priceCache;
    private readonly ILogger<SessionResetService> _logger;
    private readonly int _resetHourUtc;

    /// <summary>
    /// Initializes the session reset service.
    /// </summary>
    public SessionResetService(
        PriceCache priceCache,
        ILogger<SessionResetService> logger,
        IConfiguration configuration)
    {
        _priceCache = priceCache;
        _logger = logger;
        _resetHourUtc = configuration.GetValue("SessionResetUtcHour", 0);
    }

    /// <summary>
    /// Waits until the configured reset hour, resets sessions, then repeats daily.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SessionResetService started — reset at {Hour}:00 UTC daily", _resetHourUtc);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var nextReset = now.Date.AddHours(_resetHourUtc);
                if (nextReset <= now)
                    nextReset = nextReset.AddDays(1);

                var delay = nextReset - now;
                _logger.LogDebug("SessionResetService: next reset in {Delay}", delay);

                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);

                _priceCache.ResetAllSessions();
                _logger.LogInformation("PriceCache: session reset for {Count} symbols at {Time:u}",
                    _priceCache.Count, DateTime.UtcNow);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SessionResetService error — will retry next cycle");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
