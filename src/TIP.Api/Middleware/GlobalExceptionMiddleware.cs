using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TIP.Api.Middleware;

/// <summary>
/// ASP.NET Core middleware that catches all unhandled exceptions from controllers,
/// logs them with full stack traces via Serilog, and returns a safe 500 response
/// without leaking internal details to the client.
///
/// Design rationale:
/// - Registered BEFORE all other middleware so no exception escapes unlogged.
/// - Uses Activity.Current?.Id for distributed tracing correlation.
/// - Never exposes stack traces or internal error details to HTTP clients.
/// </summary>
public sealed class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;

    /// <summary>
    /// Initializes the global exception middleware.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">Logger for unhandled exception recording.</param>
    public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Invokes the next middleware and catches any unhandled exceptions.
    /// </summary>
    /// <param name="context">HTTP context for the current request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected — not an error
            context.Response.StatusCode = 499; // Client Closed Request
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception {Method} {Path}",
                context.Request.Method, context.Request.Path);

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 500;
                context.Response.ContentType = "application/json";

                var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
                var errorResponse = JsonSerializer.Serialize(new
                {
                    error = "Internal server error",
                    traceId
                });

                await context.Response.WriteAsync(errorResponse).ConfigureAwait(false);
            }
        }
    }
}
