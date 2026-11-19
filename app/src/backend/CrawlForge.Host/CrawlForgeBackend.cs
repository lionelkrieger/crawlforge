using System.Net;
using System.Text.Json;
using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Security;
using CrawlForge.Host.Configuration;
using CrawlForge.Host.Diagnostics;
using CrawlForge.Host.Features;
using CrawlForge.Host.Handshake;
using CrawlForge.Host.Hosting;
using CrawlForge.Host.Initialization;
using CrawlForge.Security.Handshake;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace CrawlForge.Host;

/// <summary>
/// CrawlForge backend composition root (WP-00-01 skeleton).
///
/// Startup sequence (process-model spec §2.3, skeleton form):
///   1. First-launch initialization against local storage (no network).
///   2. Lifecycle participants / background services start.
///   3. Kestrel opens the loopback-only listener (configured port first, OS-assigned
///      ephemeral fallback).
///   4. Readiness is signalled through the local port-descriptor handshake.
///
/// Shutdown: lifecycle participants drain in reverse order, then Kestrel drains
/// in-flight requests within the shutdown timeout. Later phases add migrations,
/// seeding, and the real hub/controllers through <see cref="IHostModule"/> without
/// editing this file.
/// </summary>
public sealed class CrawlForgeBackend : IAsyncDisposable
{
    private readonly BackendLifecycleCoordinator _lifecycle;
    private readonly CrawlForgePaths _paths;
    private readonly ILoopbackPortHandshake _handshake;
    private readonly ILogger<CrawlForgeBackend> _logger;
    private bool _stopped;

    private CrawlForgeBackend(
        WebApplication app,
        BackendLifecycleCoordinator lifecycle,
        CrawlForgePaths paths,
        ILoopbackPortHandshake handshake,
        string sessionToken,
        ILogger<CrawlForgeBackend> logger,
        FirstLaunchResult firstLaunch)
    {
        App = app;
        _lifecycle = lifecycle;
        _paths = paths;
        _handshake = handshake;
        SessionToken = sessionToken;
        _logger = logger;
        FirstLaunch = firstLaunch;
    }

    public WebApplication App { get; }

    public FirstLaunchResult FirstLaunch { get; }

    /// <summary>The actual loopback port Kestrel bound (resolved after start).</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Per-launch session token published by the loopback handshake. The shell
    /// presents it on every gated request (X-CrawlForge-Session).
    /// </summary>
    public string SessionToken { get; }

    public string BaseAddress => $"http://127.0.0.1:{Port}";

    public static async Task<CrawlForgeBackend> StartAsync(
        string[]? args = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);

        // Loopback-only Kestrel with the spec §2.2 port strategy. The Security
        // handshake is authoritative: it probes the configured port first,
        // falls back to an OS-ephemeral loopback port, generates the per-launch
        // session token, and publishes the descriptor the shell discovers.
        var configuredPort = builder.Configuration.GetValue($"{CrawlForgeOptions.SectionName}:Port", 0);
        var configuredDataDirectory = builder.Configuration.GetValue<string?>(
            $"{CrawlForgeOptions.SectionName}:DataDirectory", null);
        var earlyPaths = new CrawlForgePaths(Options.Create(new CrawlForgeOptions
        {
            Port = configuredPort,
            DataDirectory = configuredDataDirectory
        }));
        var handshake = new LoopbackPortHandshake(
            Path.Combine(earlyPaths.DataDirectory, "loopback-handshake.json"),
            configuredPort);
        var published = await handshake.StartAsync(cancellationToken);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, published.Port));
        builder.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(30));

        builder.Services.Configure<CrawlForgeOptions>(
            builder.Configuration.GetSection(CrawlForgeOptions.SectionName));
        builder.Services.AddSingleton<CrawlForgePaths>();
        builder.Services.AddSingleton<FirstLaunchInitializer>();
        builder.Services.AddSingleton<IFeatureAvailability, LocalFeatureAvailability>();
        builder.Services.AddSingleton<BackendLifecycleCoordinator>();
        builder.Services.AddSingleton<ILoopbackPortHandshake>(handshake);

        // Module discovery hook: later phases register controllers/hub/metrics through
        // IHostModule implementations in their own assemblies — no shared-file edits.
        var modules = HostModuleDiscovery.Discover();
        foreach (var module in modules)
        {
            builder.Services.AddSingleton(module);
            module.ConfigureServices(builder.Services);
        }

        configureBuilder?.Invoke(builder);

        var app = builder.Build();

        // Composition-layer session gate: controllers/hubs stay untouched.
        app.UseMiddleware<SessionTokenMiddleware>();

        // Core loopback surfaces. /api/health is owned by the Api library's
        // HealthController (mapped below); the stub descriptor routes remain
        // for supervisor compatibility.
        app.MapGet("/hub", () => Results.Json(new { name = "crawlforge-main", status = "ready", protocol = "hub-stub" }));
        app.MapGet("/api/diagnostics/version", () => Results.Json(DiagnosticsSurface.CreateReport()));

        // Shell-initiated graceful stop (§2.3): session-gated by the middleware,
        // triggers the same drain path as Ctrl+C (lifecycle reverse order, then
        // Kestrel within the shutdown timeout).
        app.MapPost("/api/system/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            lifetime.StopApplication();
            return Results.Json(new { stopping = true });
        });

        foreach (var module in modules)
        {
            module.ConfigureEndpoints(app);
        }

        var paths = app.Services.GetRequiredService<CrawlForgePaths>();
        var initializer = app.Services.GetRequiredService<FirstLaunchInitializer>();
        var lifecycle = app.Services.GetRequiredService<BackendLifecycleCoordinator>();
        var logger = app.Services.GetRequiredService<ILogger<CrawlForgeBackend>>();

        // Native minidump on unhandled crash (§2.5): local artifact only, never
        // transmitted; the recovery screen lists and purges it.
        var crashHandler = app.Services.GetRequiredService<ICrashDumpHandler>();
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            try
            {
                crashHandler.CaptureMinidumpAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Minidump capture failed during crash handling.");
            }
        };

        var firstLaunch = await initializer.InitializeAsync(cancellationToken);
        await lifecycle.StartAllAsync(cancellationToken);
        await app.StartAsync(cancellationToken);

        var backend = new CrawlForgeBackend(
            app, lifecycle, paths, handshake, published.SessionToken, logger, firstLaunch);
        backend.Port = ResolveActualPort(app);
        await PortDescriptor.WriteAsync(paths, backend.Port, cancellationToken);
        logger.LogInformation(
            "CrawlForge backend ready at {BaseAddress} (first run: {FirstRun}).",
            backend.BaseAddress,
            firstLaunch.FirstRun);
        // WP-09-11 (G05): the backend-lifecycle ready signal reaches connected
        // shells over the hub (the shell reconnects on backend restart).
        await PublishLifecycleAsync(app, "ready", cancellationToken);
        return backend;
    }

    /// <summary>Waits for a graceful stop request (Ctrl+C / shell-initiated shutdown).</summary>
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) =>
        App.WaitForShutdownAsync(cancellationToken);

    /// <summary>
    /// Graceful stop: drain lifecycle participants first, then let Kestrel drain
    /// in-flight connections within the configured shutdown timeout.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        // WP-09-11 (G05): announce shutdown BEFORE draining participants so the
        // hub is still up and connected shells see the lifecycle transition.
        await PublishLifecycleAsync(App, "shutdown", cancellationToken);
        await _lifecycle.StopAllAsync(cancellationToken);
        await App.StopAsync(cancellationToken);
        // Retire the handshake: delete the descriptor and invalidate the token
        // so a stale supervisor can never reuse a dead launch's credentials.
        await _handshake.StopAsync(cancellationToken);
        _logger.LogInformation("CrawlForge backend stopped gracefully.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await App.DisposeAsync();
    }

    /// <summary>
    /// Publishes the backend-lifecycle event (G05 producer). Failure-isolated:
    /// a hub dispatch problem never blocks startup or graceful shutdown.
    /// </summary>
    private static async Task PublishLifecycleAsync(WebApplication app, string kind, CancellationToken cancellationToken)
    {
        try
        {
            var hub = app.Services.GetRequiredService<IRealtimeHub>();
            await hub.PublishAsync(new HubEvent(
                HubEventCatalog.BackendLifecycle,
                JsonSerializer.Serialize(new { kind })), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            app.Services.GetRequiredService<ILogger<CrawlForgeBackend>>()
                .LogWarning(ex, "backend-lifecycle {Kind} dispatch failed.", kind);
        }
    }

    private static int ResolveActualPort(WebApplication app)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not report any bound addresses.");
        var address = addresses.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not report any bound addresses.");
        return new Uri(address).Port;
    }
}
