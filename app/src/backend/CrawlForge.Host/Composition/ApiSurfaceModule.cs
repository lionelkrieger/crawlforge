using CrawlForge.Api.Controllers;
using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Api;
using CrawlForge.Host.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CrawlForge.Host.Composition;

/// <summary>
/// API surface module (WP-06-01): registers the accepted Api library as an
/// application part with the locked JSON conventions, adds SignalR with the
/// same conventions, and maps controllers, the real-time hub, and the local
/// metrics exposition. Controllers never publish hub events (WP-05-01 handoff);
/// event ownership stays with the hub core and engine publishers.
/// </summary>
public sealed class ApiSurfaceModule : IHostModule
{
    /// <inheritdoc />
    public string Name => "api-surface";

    /// <inheritdoc />
    public int Order => 30;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        var jsonSource = ApiJsonOptions.Create();

        services
            .AddControllers()
            .AddApplicationPart(typeof(ApiControllerBase).Assembly)
            .AddEnvelopeValidation()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = jsonSource.PropertyNamingPolicy;
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = jsonSource.PropertyNameCaseInsensitive;
                options.JsonSerializerOptions.DefaultIgnoreCondition = jsonSource.DefaultIgnoreCondition;
                foreach (var converter in jsonSource.Converters)
                {
                    options.JsonSerializerOptions.Converters.Add(converter);
                }
            });

        services
            .AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = jsonSource.PropertyNamingPolicy;
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive = jsonSource.PropertyNameCaseInsensitive;
                options.PayloadSerializerOptions.DefaultIgnoreCondition = jsonSource.DefaultIgnoreCondition;
                foreach (var converter in jsonSource.Converters)
                {
                    options.PayloadSerializerOptions.Converters.Add(converter);
                }
            });
    }

    /// <inheritdoc />
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapControllers();
        endpoints.MapHub<CrawlForgeHub>(CrawlForgeHub.HubRoutePath);
        // /api/health, /api/version, and /metrics are owned by the Api library's
        // HealthController/MetricsController — no duplicate minimal endpoints.
    }
}
