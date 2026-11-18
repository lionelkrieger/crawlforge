using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace CrawlForge.ContractConformance.Tests.Harness;

/// <summary>One frozen REST catalog operation (frontend API_ROUTES entry).</summary>
/// <param name="Key">Catalog key (e.g. audits.list).</param>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Route template (/api/... with {id} placeholders).</param>
/// <param name="Group">Route group.</param>
/// <param name="Additive">True for additive (post-baseline) operations.</param>
/// <param name="Enveloped">False for binary/text routes exempt from the JSON envelope.</param>
public sealed record RouteOperation(string Key, string Method, string Path, string Group, bool Additive, bool Enveloped);

/// <summary>
/// Parses the frozen frontend REST route catalog (contracts/api/routes.ts) and
/// discovers the backend's actual controller routes by reflection, so the
/// harness can cross-check catalog ↔ implementation coverage.
/// </summary>
public static partial class RouteCatalog
{
    [GeneratedRegex("'([^']+)'\\s*:\\s*\\{\\s*method:\\s*'([^']+)',\\s*path:\\s*'([^']+)',\\s*group:\\s*'([^']+)',\\s*additive:\\s*(true|false),\\s*enveloped:\\s*(true|false)\\s*\\}", RegexOptions.Singleline)]
    private static partial Regex EntryRegex();

    /// <summary>Parses every API_ROUTES entry from the frozen catalog file.</summary>
    /// <returns>Catalog operations in file order.</returns>
    public static IReadOnlyList<RouteOperation> ParseFrozenCatalog()
    {
        var text = File.ReadAllText(RepoPaths.FrontendRoutesTs);
        var operations = new List<RouteOperation>();
        foreach (Match match in EntryRegex().Matches(text))
        {
            operations.Add(new RouteOperation(
                match.Groups[1].Value,
                match.Groups[2].Value,
                match.Groups[3].Value,
                match.Groups[4].Value,
                match.Groups[5].Value == "true",
                match.Groups[6].Value == "true"));
        }

        return operations;
    }

    /// <summary>
    /// Discovers the backend's concrete routes by reflection over the Api
    /// assembly's controllers (same technique as the E2E envelope sweep).
    /// Route constraints ({id:long}) are normalized to {id}.
    /// </summary>
    /// <returns>(Method, normalized template) pairs.</returns>
    public static IReadOnlyList<(string Method, string Template)> DiscoverBackendRoutes()
    {
        var assembly = typeof(CrawlForge.Api.Controllers.AuditsController).Assembly;
        var routes = new List<(string, string)>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(ControllerBase).IsAssignableFrom(type))
            {
                continue;
            }

            var routeAttribute = type.GetCustomAttribute<RouteAttribute>();
            var isApiController = type.GetCustomAttribute<ApiControllerAttribute>() is not null;
            if (routeAttribute is null && !isApiController)
            {
                continue;
            }

            var prefix = routeAttribute?.Template.Trim('/') ?? "";
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var httpAttribute in method.GetCustomAttributes<HttpMethodAttribute>())
                {
                    var template = httpAttribute.Template ?? "";
                    // Absolute action templates (/metrics) bypass the controller prefix.
                    var combined = template.StartsWith('/')
                        ? template
                        : string.IsNullOrEmpty(template) ? "/" + prefix : "/" + prefix + "/" + template.Trim('/');
                    routes.Add((httpAttribute.HttpMethods.First(), Normalize(combined)));
                }
            }
        }

        return routes;
    }

    /// <summary>Strips route constraints and lowercases nothing else ({id:long} → {id}).</summary>
    /// <param name="template">Route template.</param>
    /// <returns>Normalized template.</returns>
    public static string Normalize(string template) =>
        Regex.Replace(template, "\\{([^}:]+):[^}]+\\}", "{$1}");
}
