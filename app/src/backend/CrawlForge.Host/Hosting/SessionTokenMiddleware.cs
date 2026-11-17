using CrawlForge.Contracts.Security;
using Microsoft.AspNetCore.Http;

namespace CrawlForge.Host.Hosting;

/// <summary>
/// Loopback session-token gate (WP-06-01; header convention ratified for the
/// Host composition layer): every API, hub, and metrics request must present
/// the per-launch session token published by the loopback handshake. The token
/// is validated in constant time by <see cref="ILoopbackPortHandshake"/>; no
/// controller or hub code participates (composition-layer only).
/// </summary>
/// <remarks>
/// <para>
/// Exemptions: <c>/api/health</c> is the supervisor readiness probe and carries
/// no data. The stub <c>/hub</c> descriptor and <c>/api/diagnostics/version</c>
/// remain token-gated like every other data surface.
/// </para>
/// <para>
/// SignalR's WebSocket transport cannot set custom headers, so hub routes also
/// accept the standard SignalR <c>access_token</c> query value. The token never
/// leaves the machine and is never derived from machine identity (exclusion
/// rows 1/4).
/// </para>
/// </remarks>
public sealed class SessionTokenMiddleware
{
    /// <summary>Header carrying the per-launch session token.</summary>
    public const string HeaderName = "X-CrawlForge-Session";

    private readonly RequestDelegate _next;
    private readonly ILoopbackPortHandshake _handshake;

    /// <summary>Creates the gate.</summary>
    /// <param name="next">Next middleware.</param>
    /// <param name="handshake">Authoritative loopback handshake.</param>
    public SessionTokenMiddleware(RequestDelegate next, ILoopbackPortHandshake handshake)
    {
        _next = next;
        _handshake = handshake;
    }

    /// <summary>Validates the session token on gated routes.</summary>
    /// <param name="context">Request context.</param>
    /// <returns>A task that completes when the request is handled.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var gated =
            (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
             !path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)) ||
            path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/metrics", StringComparison.OrdinalIgnoreCase);

        if (gated)
        {
            var token = context.Request.Headers[HeaderName].FirstOrDefault()
                ?? context.Request.Query["access_token"].FirstOrDefault();
            if (token is null || !_handshake.ValidateSessionToken(token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Invalid or missing session token.");
                return;
            }
        }

        await _next(context);
    }
}
