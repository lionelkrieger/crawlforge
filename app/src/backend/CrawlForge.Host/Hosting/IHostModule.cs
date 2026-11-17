using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CrawlForge.Host.Hosting;

/// <summary>
/// Host-local module discovery hook (WP-00-01 infrastructure; intentionally NOT part of
/// the shared CrawlForge.Contracts surface). Later phases contribute controllers, the
/// real-time hub, metrics, and MCP surfaces by implementing this interface in their own
/// assemblies; the composition root discovers them without shared-file edits.
/// </summary>
public interface IHostModule
{
    /// <summary>Stable module name used for ordering and local diagnostics.</summary>
    string Name { get; }

    /// <summary>Lower values are configured first. Defaults to 0.</summary>
    int Order => 0;

    /// <summary>Registers the module's services before the host is built.</summary>
    void ConfigureServices(IServiceCollection services)
    {
    }

    /// <summary>Maps the module's endpoints after the host is built.</summary>
    void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
    {
    }
}
