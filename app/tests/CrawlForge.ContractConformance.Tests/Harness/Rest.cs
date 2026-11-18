using System.Net.Http.Json;
using System.Text.Json;

namespace CrawlForge.ContractConformance.Tests.Harness;

/// <summary>One REST exchange captured for conformance inspection.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Concrete request path (with query).</param>
/// <param name="Status">HTTP status code.</param>
/// <param name="ContentType">Response content type (media type).</param>
/// <param name="Body">Raw response body.</param>
/// <param name="Root">Parsed JSON root (default when the body is not JSON).</param>
public sealed record RestExchange(string Method, string Path, int Status, string ContentType, string Body, JsonElement Root)
{
    /// <summary>True when the body parsed as a JSON object.</summary>
    public bool IsJsonObject => Root.ValueKind == JsonValueKind.Object;
}

/// <summary>
/// REST helpers for the conformance harness: session-authenticated exchanges
/// and the uniform-envelope assertions of FS-CRAWL-001 §10.1/§10.4 as locked
/// by the API serializers (ApiJsonOptions: data always present, pagination /
/// error omitted when null, pagination.total, error.code stable set,
/// fieldErrors as a field→reasons map, camelCase, lowercase enums).
/// </summary>
public static class Rest
{
    /// <summary>Stable machine-readable error codes (Contracts ApiErrorCodes).</summary>
    public static readonly IReadOnlySet<string> ErrorCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "validation", "not-found", "conflict", "invalid-state", "unsupported", "internal"
    };

    /// <summary>Performs one exchange against the live backend and parses the body.</summary>
    /// <param name="http">Session-authenticated client.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Concrete path with optional query.</param>
    /// <param name="body">Optional JSON request body.</param>
    /// <param name="cancellationToken">Request token.</param>
    /// <returns>The captured exchange.</returns>
    public static async Task<RestExchange> SendAsync(
        HttpClient http, string method, string path, object? body = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        JsonElement root = default;
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) && text.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Leave root default; the envelope assertion reports the violation.
            }
        }

        return new RestExchange(method, path, (int)response.StatusCode, contentType, text, root);
    }

    /// <summary>GET + envelope data extraction (throws on protocol violations).</summary>
    /// <param name="http">Session-authenticated client.</param>
    /// <param name="path">Concrete path.</param>
    /// <param name="cancellationToken">Request token.</param>
    /// <returns>The envelope data element.</returns>
    public static async Task<JsonElement> GetDataAsync(HttpClient http, string path, CancellationToken cancellationToken = default)
    {
        var exchange = await SendAsync(http, "GET", path, null, cancellationToken);
        AssertSuccessEnvelope(exchange);
        return exchange.Root.GetProperty("data");
    }

    /// <summary>POST + envelope data extraction (throws on protocol violations).</summary>
    /// <param name="http">Session-authenticated client.</param>
    /// <param name="path">Concrete path.</param>
    /// <param name="body">JSON request body.</param>
    /// <param name="cancellationToken">Request token.</param>
    /// <returns>The envelope data element.</returns>
    public static async Task<JsonElement> PostDataAsync(HttpClient http, string path, object body, CancellationToken cancellationToken = default)
    {
        var exchange = await SendAsync(http, "POST", path, body, cancellationToken);
        AssertSuccessEnvelope(exchange);
        return exchange.Root.GetProperty("data");
    }

    /// <summary>
    /// Asserts the uniform envelope on a successful exchange: JSON object,
    /// data present and non-null, error absent, pagination (when present)
    /// carries offset/limit/total, camelCase root properties.
    /// </summary>
    /// <param name="exchange">The captured exchange.</param>
    public static void AssertSuccessEnvelope(RestExchange exchange)
    {
        var violations = CollectEnvelopeViolations(exchange, expectSuccess: true);
        AssertNoViolations(exchange, violations);
    }

    /// <summary>
    /// Asserts the uniform envelope on an error exchange: data null, error
    /// object with a stable code + message, optional field→reasons map.
    /// </summary>
    /// <param name="exchange">The captured exchange.</param>
    /// <param name="expectedCode">Optional expected error code.</param>
    public static void AssertErrorEnvelope(RestExchange exchange, string? expectedCode = null)
    {
        var violations = CollectEnvelopeViolations(exchange, expectSuccess: false);
        if (expectedCode is not null
            && exchange.IsJsonObject
            && exchange.Root.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out var code)
            && code.GetString() != expectedCode)
        {
            violations.Add($"expected error.code '{expectedCode}', got '{code.GetString()}'");
        }

        AssertNoViolations(exchange, violations);
    }

    /// <summary>
    /// Collects every uniform-envelope violation of one exchange (used by the
    /// full-route sweep so a single failing route does not mask the others).
    /// </summary>
    /// <param name="exchange">The captured exchange.</param>
    /// <param name="expectSuccess">True asserts the success shape; false the error shape.</param>
    /// <returns>Human-readable violations (empty = conformant).</returns>
    public static List<string> CollectEnvelopeViolations(RestExchange exchange, bool expectSuccess)
    {
        var violations = new List<string>();
        if (!exchange.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            violations.Add($"non-JSON content type '{exchange.ContentType}'");
            return violations;
        }

        if (!exchange.IsJsonObject)
        {
            violations.Add("body is not a JSON object");
            return violations;
        }

        var root = exchange.Root;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Length > 0 && char.IsUpper(property.Name[0]))
            {
                violations.Add($"root property '{property.Name}' is not camelCase");
            }
        }

        if (!root.TryGetProperty("data", out var data))
        {
            violations.Add("missing 'data' (envelope must always carry data)");
        }
        else if (expectSuccess && data.ValueKind == JsonValueKind.Null)
        {
            violations.Add("success response has null data");
        }
        else if (!expectSuccess && data.ValueKind != JsonValueKind.Null)
        {
            violations.Add("error response must carry null data");
        }

        if (expectSuccess && root.TryGetProperty("error", out var successError) && successError.ValueKind != JsonValueKind.Null)
        {
            violations.Add("success response carries an error object");
        }

        if (root.TryGetProperty("pagination", out var pagination) && pagination.ValueKind != JsonValueKind.Null)
        {
            if (pagination.ValueKind != JsonValueKind.Object)
            {
                violations.Add("pagination is not an object");
            }
            else
            {
                if (!pagination.TryGetProperty("total", out var total) || total.ValueKind != JsonValueKind.Number)
                {
                    violations.Add("pagination.total missing or not numeric");
                }

                if (!pagination.TryGetProperty("offset", out _))
                {
                    violations.Add("pagination.offset missing");
                }

                if (!pagination.TryGetProperty("limit", out _))
                {
                    violations.Add("pagination.limit missing");
                }
            }
        }

        if (!expectSuccess)
        {
            if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                violations.Add("error response missing error object");
            }
            else
            {
                if (!error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String
                    || !ErrorCodes.Contains(code.GetString() ?? ""))
                {
                    violations.Add($"error.code '{code.GetString()}' outside the stable set");
                }

                if (!error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(message.GetString()))
                {
                    violations.Add("error.message missing or empty");
                }

                if (error.TryGetProperty("fieldErrors", out var fieldErrors) && fieldErrors.ValueKind != JsonValueKind.Null)
                {
                    if (fieldErrors.ValueKind != JsonValueKind.Object)
                    {
                        violations.Add("fieldErrors is not a field→reasons map");
                    }
                    else
                    {
                        foreach (var field in fieldErrors.EnumerateObject())
                        {
                            if (field.Value.ValueKind != JsonValueKind.Array)
                            {
                                violations.Add($"fieldErrors['{field.Name}'] is not an array of reasons");
                                continue;
                            }

                            foreach (var reason in field.Value.EnumerateArray())
                            {
                                if (reason.ValueKind != JsonValueKind.String)
                                {
                                    violations.Add($"fieldErrors['{field.Name}'] carries a non-string reason");
                                }
                            }
                        }
                    }
                }
            }
        }

        return violations;
    }

    private static void AssertNoViolations(RestExchange exchange, List<string> violations)
    {
        if (violations.Count > 0)
        {
            var body = exchange.Body.Length <= 400 ? exchange.Body : exchange.Body[..400];
            throw new Xunit.Sdk.XunitException(
                $"{exchange.Method} {exchange.Path} → {exchange.Status}: {string.Join("; ", violations)}. Body: {body}");
        }
    }
}
