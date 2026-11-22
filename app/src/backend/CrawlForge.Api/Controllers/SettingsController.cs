using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Contracts.Security;
using CrawlForge.Data.MySql;
using CrawlForge.Intelligence.Embeddings;
using Microsoft.AspNetCore.Mvc;

namespace CrawlForge.Api.Controllers;

/// <summary>Wire settings object (frozen frontend AppSettings shape). Local configuration only.</summary>
/// <param name="Theme">UI theme identifier.</param>
/// <param name="UiLanguage">UI locale tag.</param>
/// <param name="Database">Database provider selection and connection fields.</param>
/// <param name="Proxy">Outbound proxy configuration (user-owned).</param>
/// <param name="Mcp">Local automation-interface settings.</param>
/// <param name="Embeddings">Local embedding provider configuration.</param>
/// <param name="StatusBar">Status-bar advisory thresholds.</param>
public sealed record AppSettingsWire(
    string Theme,
    string UiLanguage,
    DatabaseSettingsWire Database,
    ProxySettingsWire Proxy,
    McpSettingsWire Mcp,
    EmbeddingSettingsWire Embeddings,
    StatusBarThresholdsWire StatusBar);

/// <summary>Database connection settings; password masked on read.</summary>
/// <param name="Provider">Provider wire value: sqlite or mysql.</param>
/// <param name="Host">External server host, when configured.</param>
/// <param name="Port">External server port, when configured.</param>
/// <param name="Database">External database name, when configured.</param>
/// <param name="Username">External user name, when configured.</param>
/// <param name="Password">Masked on read; clear only on update/test.</param>
public sealed record DatabaseSettingsWire(string Provider, string? Host, int? Port, string? Database, string? Username, string? Password);

/// <summary>Outbound proxy settings; password masked on read.</summary>
/// <param name="Enabled">Whether the proxy is enabled.</param>
/// <param name="Host">Proxy host.</param>
/// <param name="Port">Proxy port.</param>
/// <param name="Username">Proxy user name.</param>
/// <param name="Password">Masked on read.</param>
public sealed record ProxySettingsWire(bool Enabled, string? Host, int? Port, string? Username, string? Password);

/// <summary>Local automation-interface settings; the token is a displayable local credential.</summary>
/// <param name="Enabled">Whether the automation interface is enabled.</param>
/// <param name="Transport">Transport wire value: sse or stdio.</param>
/// <param name="Token">Local automation token, when generated.</param>
public sealed record McpSettingsWire(bool Enabled, string Transport, string? Token);

/// <summary>Embedding provider settings (loopback-only daemon or bundled model).</summary>
/// <param name="Provider">Provider wire value: onnx or loopback-daemon.</param>
/// <param name="Endpoint">Loopback daemon endpoint, when configured.</param>
/// <param name="Model">Model tag override, when configured.</param>
public sealed record EmbeddingSettingsWire(string Provider, string? Endpoint, string? Model);

/// <summary>Status-bar advisory thresholds (local preferences).</summary>
/// <param name="WorkingSetAdvisoryBytes">Working-set advisory threshold.</param>
/// <param name="WalAdvisoryBytes">WAL advisory threshold.</param>
public sealed record StatusBarThresholdsWire(long WorkingSetAdvisoryBytes, long WalAdvisoryBytes);

/// <summary>PUT /api/settings — any subset of settings fields; null keeps the stored value.</summary>
/// <param name="Theme">Optional theme.</param>
/// <param name="UiLanguage">Optional locale.</param>
/// <param name="Database">Optional database subset.</param>
/// <param name="Proxy">Optional proxy subset.</param>
/// <param name="Mcp">Optional automation-interface subset.</param>
/// <param name="Embeddings">Optional embedding subset.</param>
/// <param name="StatusBar">Optional status-bar subset.</param>
public sealed record SettingsUpdateRequest(
    string? Theme,
    string? UiLanguage,
    DatabaseUpdateRequest? Database,
    ProxyUpdateRequest? Proxy,
    McpUpdateRequest? Mcp,
    EmbeddingUpdateRequest? Embeddings,
    StatusBarUpdateRequest? StatusBar);

/// <summary>Database settings subset.</summary>
/// <param name="Provider">Optional provider selection.</param>
/// <param name="Host">Optional host.</param>
/// <param name="Port">Optional port.</param>
/// <param name="Database">Optional database name.</param>
/// <param name="Username">Optional user name.</param>
/// <param name="Password">Clear password, the mask to keep, or null to clear.</param>
public sealed record DatabaseUpdateRequest(string? Provider, string? Host, int? Port, string? Database, string? Username, string? Password);

/// <summary>Proxy settings subset.</summary>
/// <param name="Enabled">Optional enablement.</param>
/// <param name="Host">Optional host.</param>
/// <param name="Port">Optional port.</param>
/// <param name="Username">Optional user name.</param>
/// <param name="Password">Clear password, the mask to keep, or null to clear.</param>
public sealed record ProxyUpdateRequest(bool? Enabled, string? Host, int? Port, string? Username, string? Password);

/// <summary>Automation-interface settings subset.</summary>
/// <param name="Enabled">Optional enablement.</param>
/// <param name="Transport">Optional transport.</param>
/// <param name="Token">New token, or null to clear.</param>
public sealed record McpUpdateRequest(bool? Enabled, string? Transport, string? Token);

/// <summary>Embedding settings subset.</summary>
/// <param name="Provider">Optional provider selection.</param>
/// <param name="Endpoint">Optional loopback endpoint.</param>
/// <param name="Model">Optional model tag.</param>
public sealed record EmbeddingUpdateRequest(string? Provider, string? Endpoint, string? Model);

/// <summary>Status-bar settings subset.</summary>
/// <param name="WorkingSetAdvisoryBytes">Optional working-set threshold.</param>
/// <param name="WalAdvisoryBytes">Optional WAL threshold.</param>
public sealed record StatusBarUpdateRequest(long? WorkingSetAdvisoryBytes, long? WalAdvisoryBytes);

/// <summary>POST /api/settings/database/test — provider selection and connection fields.</summary>
/// <param name="Provider">Provider to test: sqlite or mysql.</param>
/// <param name="Host">External host (mysql).</param>
/// <param name="Port">External port (mysql).</param>
/// <param name="Database">External database name (mysql).</param>
/// <param name="Username">External user name (mysql).</param>
/// <param name="Password">Clear password or the mask to reuse the stored one.</param>
public sealed record DatabaseTestRequest(string Provider, string? Host, int? Port, string? Database, string? Username, string? Password);

/// <summary>Database connection test verdict.</summary>
/// <param name="Reachable">Whether the endpoint is reachable.</param>
/// <param name="Authenticated">Whether authentication succeeded.</param>
/// <param name="MigrationsCompatible">Whether the provider supports all migrations.</param>
/// <param name="Message">Human-readable verdict detail.</param>
public sealed record DatabaseTestResponse(bool Reachable, bool Authenticated, bool MigrationsCompatible, string? Message);

/// <summary>POST /api/settings/embedding/test — candidate loopback daemon endpoint.</summary>
/// <param name="Endpoint">Candidate endpoint; must resolve to loopback.</param>
/// <param name="Model">Optional model tag.</param>
public sealed record EmbeddingTestRequest(string Endpoint, string? Model);

/// <summary>Embedding connection/model verdict.</summary>
/// <param name="IsLoopback">Whether the endpoint is loopback-only.</param>
/// <param name="Reachable">Whether the provider is reachable.</param>
/// <param name="ModelAvailable">Whether the model is available.</param>
/// <param name="Message">Human-readable verdict detail.</param>
public sealed record EmbeddingTestResponse(bool IsLoopback, bool Reachable, bool ModelAvailable, string? Message);

/// <summary>GET /api/settings/browser — UI-21 browser section (WP-10-04,
/// FS-CRAWL-001 §5.6.3). Local-only resolution identity: bundled pinned Chrome
/// for Testing → user-configured path → local Chrome → local Edge. No runtime
/// download exists (D-003); the pin-manifest version is reported even before
/// the binary is physically bundled (owner packaging item O-006).</summary>
/// <param name="Available">Whether a local browser resolved.</param>
/// <param name="ExecutablePath">Absolute local executable path, when resolved.</param>
/// <param name="Source">Resolution source wire key: bundled / user-configured / local-chrome / local-edge.</param>
/// <param name="Product">Browser product label.</param>
/// <param name="Version">Resolved browser version.</param>
/// <param name="PinnedVersion">The manifest pin when a bundled build resolved, else null.</param>
/// <param name="PinManifestVersion">Bundled pin-manifest version when present (even if the binary is not yet bundled).</param>
/// <param name="Message">Actionable guidance when no browser resolved.</param>
public sealed record BrowserInfoWire(
    bool Available,
    string? ExecutablePath,
    string? Source,
    string? Product,
    string? Version,
    string? PinnedVersion,
    string? PinManifestVersion,
    string? Message);

/// <summary>
/// Settings surface (FS-CRAWL-001 §10.2, criterion #8): local-only toggles with
/// guaranteed defaults; outbound credentials are encrypted at rest and never
/// returned in clear; every feature works without remote resolution. No license,
/// account, or entitlement payload exists anywhere on this surface.
/// </summary>
[Route("api/settings")]
public sealed class SettingsController : ApiControllerBase
{
    /// <summary>Password mask returned on reads; accepted on writes to keep the stored secret.</summary>
    public const string PasswordMask = "********";

    private const long DefaultWorkingSetAdvisoryBytes = 1_610_612_736;
    private const long DefaultWalAdvisoryBytes = 268_435_456;

    private readonly IReadRepository<ApplicationSettingsDto> _settingsRead;
    private readonly IWriteRepository<ApplicationSettingsDto> _settingsWrite;
    private readonly IDbProvider _provider;
    private readonly ISecretProtector _secrets;

    /// <summary>Creates the controller.</summary>
    /// <param name="settingsRead">Settings read repository.</param>
    /// <param name="settingsWrite">Settings write repository.</param>
    /// <param name="provider">Configured database provider (embedded test path).</param>
    /// <param name="secrets">Secret protector for outbound credentials.</param>
    public SettingsController(
        IReadRepository<ApplicationSettingsDto> settingsRead,
        IWriteRepository<ApplicationSettingsDto> settingsWrite,
        IDbProvider provider,
        ISecretProtector secrets)
    {
        _settingsRead = settingsRead;
        _settingsWrite = settingsWrite;
        _provider = provider;
        _secrets = secrets;
    }

    /// <summary>Reads the full settings object with secrets masked.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Settings envelope.</returns>
    [HttpGet]
    public async Task<ObjectResult> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        return OkEnvelope(ToWire(settings));
    }

    /// <summary>Updates any subset of settings fields and returns the updated object.</summary>
    /// <param name="request">Subset payload; null fields keep stored values.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated settings envelope.</returns>
    [HttpPut]
    public async Task<ObjectResult> UpdateSettingsAsync([FromBody] SettingsUpdateRequest request, CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var storage = ParseObject(settings.StorageOptionsJson);

        if (request.Theme is not null)
        {
            settings = settings with { Theme = request.Theme };
        }

        if (request.UiLanguage is not null)
        {
            settings = settings with { Locale = request.UiLanguage };
        }

        if (request.Database is not null)
        {
            settings = ApplyDatabaseUpdate(settings, request.Database);
        }

        if (request.Proxy is not null)
        {
            storage["proxy"] = ApplyProxyUpdate(storage["proxy"] as JsonObject ?? [], request.Proxy);
        }

        if (request.Mcp is not null)
        {
            storage["mcp"] = ApplyMcpUpdate(storage["mcp"] as JsonObject ?? [], request.Mcp);
        }

        if (request.Embeddings is not null)
        {
            settings = ApplyEmbeddingUpdate(settings, request.Embeddings);
        }

        if (request.StatusBar is not null)
        {
            var thresholds = ParseObject(settings.StatusBarThresholdsJson);
            if (request.StatusBar.WorkingSetAdvisoryBytes is { } workingSet)
            {
                thresholds["workingSetAdvisoryBytes"] = workingSet;
            }

            if (request.StatusBar.WalAdvisoryBytes is { } wal)
            {
                thresholds["walAdvisoryBytes"] = wal;
            }

            settings = settings with { StatusBarThresholdsJson = thresholds.ToJsonString() };
        }

        settings = settings with { StorageOptionsJson = storage.ToJsonString() };
        var updated = await _settingsWrite.UpdateAsync(settings, cancellationToken);
        return OkEnvelope(ToWire(updated));
    }

    /// <summary>Tests database reachability, authentication, and migration compatibility.</summary>
    /// <param name="request">Provider selection and connection fields.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verdict envelope.</returns>
    [HttpPost("database/test")]
    public async Task<ObjectResult> TestDatabaseAsync([FromBody] DatabaseTestRequest request, CancellationToken cancellationToken)
    {
        if (string.Equals(request.Provider, "sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var verdict = await _provider.TestConnectionAsync(cancellationToken);
            return OkEnvelope(new DatabaseTestResponse(verdict.IsReachable, verdict.AuthenticationOk, verdict.MigrationCompatible, verdict.Message));
        }

        if (!string.Equals(request.Provider, "mysql", StringComparison.OrdinalIgnoreCase))
        {
            return ValidationError($"Unknown database provider '{request.Provider}'.", [new ApiFieldError("provider", "Use 'sqlite' or 'mysql'.")]);
        }

        var fieldErrors = new List<ApiFieldError>();
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            fieldErrors.Add(new ApiFieldError("host", "The external database host is required."));
        }

        if (string.IsNullOrWhiteSpace(request.Database))
        {
            fieldErrors.Add(new ApiFieldError("database", "The external database name is required."));
        }

        if (string.IsNullOrWhiteSpace(request.Username))
        {
            fieldErrors.Add(new ApiFieldError("username", "The external database user is required."));
        }

        if (fieldErrors.Count > 0)
        {
            return ValidationError("The external database connection is incomplete.", fieldErrors);
        }

        var password = request.Password;
        if (password == PasswordMask)
        {
            var settings = await LoadSettingsAsync(cancellationToken);
            var connection = ParseObject(settings.DatabaseConnectionJson);
            password = UnprotectOrEmpty(connection["passwordProtected"]?.GetValue<string>());
        }

        try
        {
            var mysqlSettings = new MySqlDatabaseSettings
            {
                Host = request.Host!,
                Port = request.Port ?? MySqlDatabaseSettings.DefaultPort,
                Database = request.Database!,
                UserId = request.Username!,
                Password = password ?? string.Empty,
            };
            mysqlSettings.Validate();
            var mysqlProvider = new MySqlDbProvider(mysqlSettings);
            var verdict = await mysqlProvider.TestConnectionAsync(cancellationToken);
            return OkEnvelope(new DatabaseTestResponse(verdict.IsReachable, verdict.AuthenticationOk, verdict.MigrationCompatible, verdict.Message));
        }
        catch (ArgumentException error)
        {
            return ValidationError(error.Message);
        }
    }

    /// <summary>
    /// Tests a candidate loopback embedding daemon. Loopback validation runs
    /// BEFORE any connection attempt; non-loopback endpoints are rejected and
    /// never contacted (exclusion E7).
    /// </summary>
    /// <param name="request">Candidate endpoint and optional model tag.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verdict envelope.</returns>
    [HttpPost("embedding/test")]
    public async Task<ObjectResult> TestEmbeddingAsync([FromBody] EmbeddingTestRequest request, CancellationToken cancellationToken)
    {
        var candidate = new EmbeddingProviderSettings(EmbeddingProviderKind.LoopbackDaemon, request.Endpoint, request.Model);
        if (!EmbeddingProviderFactory.TryValidateSettings(candidate, out var error))
        {
            return ValidationError(error ?? "The embedding endpoint must be loopback-only.", [new ApiFieldError("endpoint", "Only loopback endpoints are permitted.")]);
        }

        // The provider creates its own loopback-DNS-pinned client (defense in
        // depth: the endpoint is re-validated before every request); no
        // general-purpose outbound client is constructed at the API layer.
        var provider = EmbeddingProviderFactory.Create(candidate, BundledModelDirectory);
        try
        {
            var verdict = await provider.TestConnectionAsync(cancellationToken);
            return OkEnvelope(new EmbeddingTestResponse(true, verdict.IsReachable, verdict.ModelAvailable, verdict.Message));
        }
        finally
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    private static string BundledModelDirectory => Path.Combine(AppContext.BaseDirectory, "assets", "onnx-model");

    /// <summary>Reads the local rendering-browser identity for the UI-21
    /// browser section (§5.6.3). Filesystem probes only — no network, no
    /// registry/WMI, no runtime download (D-003/CR-007).</summary>
    /// <returns>Browser info envelope.</returns>
    [HttpGet("browser")]
    public ObjectResult GetBrowserInfo()
    {
        var manifest = Crawler.Rendering.BrowserProvisioning.TryLoadPinManifest();
        var browser = Crawler.Rendering.BrowserProvisioning.TryResolve();
        if (browser is null)
        {
            return OkEnvelope(new BrowserInfoWire(
                false, null, null, null, null, null, manifest?.Version,
                "No local browser resolved. Install Chrome or Edge, or configure a browser executable path. CrawlForge never downloads a browser at runtime."));
        }

        var source = browser.Source switch
        {
            Crawler.Rendering.BrowserSource.Bundled => "bundled",
            Crawler.Rendering.BrowserSource.UserConfigured => "user-configured",
            Crawler.Rendering.BrowserSource.LocalChrome => "local-chrome",
            Crawler.Rendering.BrowserSource.LocalEdge => "local-edge",
            _ => "unknown",
        };
        return OkEnvelope(new BrowserInfoWire(
            true, browser.ExecutablePath, source, browser.Product, browser.Version,
            browser.PinnedVersion, manifest?.Version, null));
    }

    private async Task<ApplicationSettingsDto> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var page = await _settingsRead.ListAsync(new(Offset: 0, Limit: 1), cancellationToken);
        return page.Items.Count > 0
            ? page.Items[0]
            : throw new InvalidOperationException("Application settings row is missing; first-run seeding did not run.");
    }

    private AppSettingsWire ToWire(ApplicationSettingsDto settings)
    {
        var connection = ParseObject(settings.DatabaseConnectionJson);
        var storage = ParseObject(settings.StorageOptionsJson);
        var proxy = storage["proxy"] as JsonObject ?? [];
        var mcp = storage["mcp"] as JsonObject ?? [];
        var thresholds = ParseObject(settings.StatusBarThresholdsJson);

        var database = new DatabaseSettingsWire(
            settings.DatabaseProvider == DatabaseProviderKind.MySql ? "mysql" : "sqlite",
            connection["host"]?.GetValue<string>(),
            connection["port"]?.GetValue<int>(),
            connection["database"]?.GetValue<string>(),
            connection["username"]?.GetValue<string>(),
            connection["passwordProtected"] is null ? null : PasswordMask);

        var proxyWire = new ProxySettingsWire(
            proxy["enabled"]?.GetValue<bool>() ?? false,
            proxy["host"]?.GetValue<string>(),
            proxy["port"]?.GetValue<int>(),
            proxy["username"]?.GetValue<string>(),
            proxy["passwordProtected"] is null ? null : PasswordMask);

        var mcpWire = new McpSettingsWire(
            mcp["enabled"]?.GetValue<bool>() ?? false,
            mcp["transport"]?.GetValue<string>() ?? "sse",
            UnprotectOrNull(mcp["tokenProtected"]?.GetValue<string>()));

        var embedding = settings.EmbeddingProvider;
        var embeddingWire = new EmbeddingSettingsWire(
            embedding?.Kind == EmbeddingProviderKind.LoopbackDaemon ? "loopback-daemon" : "onnx",
            embedding?.LoopbackEndpoint,
            embedding?.ModelTag);

        var statusBar = new StatusBarThresholdsWire(
            thresholds["workingSetAdvisoryBytes"]?.GetValue<long>() ?? DefaultWorkingSetAdvisoryBytes,
            thresholds["walAdvisoryBytes"]?.GetValue<long>() ?? DefaultWalAdvisoryBytes);

        return new AppSettingsWire(settings.Theme, settings.Locale, database, proxyWire, mcpWire, embeddingWire, statusBar);
    }

    private ApplicationSettingsDto ApplyDatabaseUpdate(ApplicationSettingsDto settings, DatabaseUpdateRequest request)
    {
        var connection = ParseObject(settings.DatabaseConnectionJson);
        var provider = settings.DatabaseProvider;

        if (request.Provider is not null)
        {
            provider = string.Equals(request.Provider, "mysql", StringComparison.OrdinalIgnoreCase)
                ? DatabaseProviderKind.MySql
                : DatabaseProviderKind.Sqlite;
        }

        if (request.Host is not null)
        {
            connection["host"] = request.Host;
        }

        if (request.Port is not null)
        {
            connection["port"] = request.Port;
        }

        if (request.Database is not null)
        {
            connection["database"] = request.Database;
        }

        if (request.Username is not null)
        {
            connection["username"] = request.Username;
        }

        if (request.Password is not null && request.Password != PasswordMask)
        {
            if (request.Password.Length == 0)
            {
                connection.Remove("passwordProtected");
            }
            else
            {
                connection["passwordProtected"] = _secrets.Protect(request.Password);
            }
        }

        return settings with
        {
            DatabaseProvider = provider,
            DatabaseConnectionJson = connection.ToJsonString(),
        };
    }

    private JsonObject ApplyProxyUpdate(JsonObject proxy, ProxyUpdateRequest request)
    {
        if (request.Enabled is not null)
        {
            proxy["enabled"] = request.Enabled;
        }

        if (request.Host is not null)
        {
            proxy["host"] = request.Host;
        }

        if (request.Port is not null)
        {
            proxy["port"] = request.Port;
        }

        if (request.Username is not null)
        {
            proxy["username"] = request.Username;
        }

        if (request.Password is not null && request.Password != PasswordMask)
        {
            if (request.Password.Length == 0)
            {
                proxy.Remove("passwordProtected");
            }
            else
            {
                proxy["passwordProtected"] = _secrets.Protect(request.Password);
            }
        }

        return proxy;
    }

    private JsonObject ApplyMcpUpdate(JsonObject mcp, McpUpdateRequest request)
    {
        if (request.Enabled is not null)
        {
            mcp["enabled"] = request.Enabled;
        }

        if (request.Transport is not null)
        {
            mcp["transport"] = request.Transport;
        }

        if (request.Token is not null)
        {
            if (request.Token.Length == 0)
            {
                mcp.Remove("tokenProtected");
            }
            else
            {
                mcp["tokenProtected"] = _secrets.Protect(request.Token);
            }
        }

        return mcp;
    }

    private static ApplicationSettingsDto ApplyEmbeddingUpdate(ApplicationSettingsDto settings, EmbeddingUpdateRequest request)
    {
        var current = settings.EmbeddingProvider ?? new EmbeddingProviderSettings(EmbeddingProviderKind.BundledOnnx, null, null);
        var kind = request.Provider switch
        {
            "loopback-daemon" => EmbeddingProviderKind.LoopbackDaemon,
            "onnx" => EmbeddingProviderKind.BundledOnnx,
            _ => current.Kind,
        };

        return settings with
        {
            EmbeddingProvider = new EmbeddingProviderSettings(
                kind,
                request.Endpoint ?? current.LoopbackEndpoint,
                request.Model ?? current.ModelTag),
        };
    }

    private string UnprotectOrEmpty(string? protectedText) =>
        protectedText is null ? string.Empty : _secrets.Unprotect(protectedText);

    private string? UnprotectOrNull(string? protectedText) =>
        protectedText is null ? null : _secrets.Unprotect(protectedText);

    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
