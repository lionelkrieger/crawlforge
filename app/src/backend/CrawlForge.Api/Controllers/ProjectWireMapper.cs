using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Projects locked entity DTOs onto the frozen frontend wire shapes and back.
/// API-owned JSON payload layout (documented for the Host phase):
/// <list type="bullet">
/// <item>configuration behavior document in <c>ScopeRulesJson</c>: description,
/// respectRobotsTxt, virtualRobotsTxt, maxResponseBytes, rendering options;</item>
/// <item>AIMD parameters under <c>PolitenessJson.aimd</c>;</item>
/// <item>project tags in <c>TagsJson</c>; rewrite rules / host mappings in the
/// additive <c>RewriteRulesJson</c> / <c>HostMappingJson</c> columns.</item>
/// </list>
/// </summary>
internal static class ProjectWireMapper
{
    public static readonly AimdSettingsWire DefaultAimd = new(2.0, 0.5, 0.5, 4, 0.2);

    public static JsonObject ParseObject(string? json)
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

    public static IReadOnlyList<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static ProjectSummaryWire ToSummary(ProjectDto project) =>
        new(
            project.Id.ToString(),
            project.Name,
            project.Description,
            ParseStringList(project.TagsJson),
            project.ScopeSummary,
            project.CreatedAt,
            project.UpdatedAt);

    public static ProjectWire ToProject(ProjectDto project, IReadOnlyList<ContentScopeProfileWire>? profiles) =>
        new(
            project.Id.ToString(),
            project.Name,
            project.Description,
            ParseStringList(project.TagsJson),
            project.ScopeSummary,
            project.CreatedAt,
            project.UpdatedAt,
            ToContentScope(project.ContentScope, profiles),
            ToRewrite(project.RewriteRulesJson, project.HostMappingJson),
            ToConsent(project.ConsentModeEnabled, project.BeaconPatternsJson),
            ToZoneWeights(project.ZoneWeightsJson, project.SerpBudgets));

    public static ConfigurationSummaryWire ToConfigurationSummary(CrawlConfigurationDto configuration)
    {
        var behavior = ParseObject(configuration.ScopeRulesJson);
        return new ConfigurationSummaryWire(
            configuration.Id.ToString(),
            configuration.ProjectId.ToString(),
            configuration.Name,
            behavior["description"]?.GetValue<string>(),
            configuration.CreatedAt,
            configuration.UpdatedAt);
    }

    public static AuditConfigurationWire ToConfiguration(CrawlConfigurationDto configuration, IReadOnlyList<ContentScopeProfileWire>? profiles)
    {
        var behavior = ParseObject(configuration.ScopeRulesJson);
        var politeness = ParseObject(configuration.PolitenessJson);
        var renderingOptions = behavior["rendering"] as JsonObject ?? [];

        var limits = new CrawlLimitsWire(
            configuration.LimitOverrides?.MaxUrls,
            configuration.LimitOverrides?.MaxDepth ?? configuration.Depth,
            configuration.LimitOverrides?.MaxDurationSeconds,
            behavior["maxResponseBytes"]?.GetValue<long>());

        var aimd = politeness["aimd"] is JsonObject aimdNode
            ? new AimdSettingsWire(
                aimdNode["initialRateR0"]?.GetValue<double>() ?? DefaultAimd.InitialRateR0,
                aimdNode["alpha"]?.GetValue<double>() ?? DefaultAimd.Alpha,
                aimdNode["beta"]?.GetValue<double>() ?? DefaultAimd.Beta,
                aimdNode["kappa"]?.GetValue<int>() ?? DefaultAimd.Kappa,
                aimdNode["ewmaTtfbWeight"]?.GetValue<double>() ?? DefaultAimd.EwmaTtfbWeight)
            : DefaultAimd;

        var rendering = new RenderingSettingsWire(
            configuration.RenderingMode != RenderMode.None,
            renderingOptions["deviceProfile"]?.GetValue<string>(),
            renderingOptions["infiniteScroll"]?.GetValue<bool>() ?? false,
            renderingOptions["captureConsole"]?.GetValue<bool>() ?? false);

        // WP-09-06 (WP-09-07 §3): additive editor fields projected from ScopeRulesJson.
        // scopeRules is the whole persisted document (behavior keys included);
        // enabledCategories lives under the same document (null = all enabled).
        System.Text.Json.JsonElement? scopeRules = behavior.Count > 0
            ? JsonSerializer.SerializeToElement(behavior)
            : null;
        IReadOnlyList<string>? enabledCategories = null;
        if (behavior["enabledCategories"] is JsonArray categoryNodes)
        {
            var names = new List<string>();
            foreach (var node in categoryNodes)
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            enabledCategories = names.Count > 0 ? names : null;
        }

        return new AuditConfigurationWire(
            configuration.Id.ToString(),
            configuration.ProjectId.ToString(),
            configuration.Name,
            behavior["description"]?.GetValue<string>(),
            configuration.CreatedAt,
            configuration.UpdatedAt,
            ParseStringList(configuration.SeedsJson),
            limits,
            aimd,
            rendering,
            behavior["respectRobotsTxt"]?.GetValue<bool>() ?? true,
            behavior["virtualRobotsTxt"]?.GetValue<string>(),
            ToContentScope(configuration.ContentScope, profiles),
            ToRewrite(configuration.RewriteRulesJson, configuration.HostMappingJson),
            ToConsent(configuration.ConsentModeEnabled, configuration.BeaconPatternsJson),
            ToZoneWeights(configuration.ZoneWeightsJson, configuration.SerpBudgets),
            scopeRules,
            enabledCategories,
            // WP-10-A1 (D-021): additive capture-mode projection. Null DTO
            // members stay null on the wire (baseline non-JS behaviour).
            configuration.CaptureMode is { } mode ? EnumWireNames.ToWireValue(mode) : null,
            ToCaptureSettings(configuration.CaptureSettings),
            ToRedirectChecks(configuration.RedirectChecks));
    }

    /// <summary>Projects the capture settings document onto the wire (null stays null).</summary>
    public static CaptureSettingsWire? ToCaptureSettings(CaptureSettings? settings) =>
        settings is null
            ? null
            : new CaptureSettingsWire(
                settings.AdaptiveSampleSizePerGroup,
                settings.MaterialChangeWordCountThreshold,
                settings.DualStorageWarningThresholdBytes,
                settings.RenderWaitQuietMs,
                settings.RenderWaitHardCapMs,
                settings.RenderingPoolSize,
                settings.PatternOverrides?.Select(o => new CapturePatternOverrideWire(
                    o.Pattern, EnumWireNames.ToWireValue(o.Capture))).ToList());

    /// <summary>Projects the CR-029 redirect toggles onto the wire (null stays null).</summary>
    public static RedirectCheckSettingsWire? ToRedirectChecks(RedirectCheckSettings? checks) =>
        checks is null
            ? null
            : new RedirectCheckSettingsWire(checks.CrossHost, checks.TemporaryStatus, checks.NonIndexableTarget, checks.HttpsDowngrade);

    /// <summary>
    /// Parses a capture-mode wire token (case-insensitive; null/empty = baseline).
    /// Returns false for an unknown token so the controller can emit a field error.
    /// </summary>
    public static bool TryParseCaptureMode(string? text, out CaptureMode? mode)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            mode = null;
            return true;
        }

        if (EnumWireNames.TryParse<CaptureMode>(text, out var parsed))
        {
            mode = parsed;
            return true;
        }

        mode = null;
        return false;
    }

    /// <summary>Materializes the capture settings document; absent knobs take engine defaults.</summary>
    public static CaptureSettings ToCaptureSettingsDocument(CaptureSettingsWire wire)
    {
        var defaults = CaptureSettings.Default;
        return new CaptureSettings(
            wire.AdaptiveSampleSizePerGroup ?? defaults.AdaptiveSampleSizePerGroup,
            wire.MaterialChangeWordCountThreshold ?? defaults.MaterialChangeWordCountThreshold,
            wire.DualStorageWarningThresholdBytes ?? defaults.DualStorageWarningThresholdBytes,
            wire.RenderWaitQuietMs ?? defaults.RenderWaitQuietMs,
            wire.RenderWaitHardCapMs ?? defaults.RenderWaitHardCapMs,
            wire.RenderingPoolSize,
            wire.PatternOverrides is null || wire.PatternOverrides.Count == 0
                ? null
                : wire.PatternOverrides.Select(o => new CapturePatternOverride(
                    o.Pattern,
                    EnumWireNames.TryParse<PatternCaptureMode>(o.Capture, out var capture)
                        ? capture
                        : throw new ArgumentException($"Unknown pattern capture token '{o.Capture}'.", nameof(wire)))).ToList());
    }

    /// <summary>Materializes the CR-029 toggle document; absent toggles default ON.</summary>
    public static RedirectCheckSettings ToRedirectChecksDocument(RedirectCheckSettingsWire wire) =>
        new(wire.CrossHost ?? true, wire.TemporaryStatus ?? true, wire.NonIndexableTarget ?? true, wire.HttpsDowngrade ?? true);

    public static ContentScopeSettingsWire? ToContentScope(ContentScopeDefinition? scope, IReadOnlyList<ContentScopeProfileWire>? profiles) =>
        scope is null && profiles is null
            ? null
            : new ContentScopeSettingsWire(scope?.IncludeSelectors ?? [], scope?.ExcludeSelectors ?? [], profiles);

    public static RewriteSettingsWire? ToRewrite(string? rulesJson, string? hostMappingsJson)
    {
        if (rulesJson is null && hostMappingsJson is null)
        {
            return null;
        }

        var rules = DeserializeRules(rulesJson);
        var mappings = DeserializeMappings(hostMappingsJson);
        return new RewriteSettingsWire(rules, mappings);
    }

    public static ConsentSettingsWire? ToConsent(bool? consentModeEnabled, string? beaconPatternsJson) =>
        consentModeEnabled is null && beaconPatternsJson is null
            ? null
            : new ConsentSettingsWire(consentModeEnabled ?? false, beaconPatternsJson is not null);

    public static ZoneWeightSettingsWire? ToZoneWeights(string? zoneWeightsJson, SerpBudgetSettings? serpBudgets)
    {
        if (zoneWeightsJson is null && serpBudgets is null)
        {
            return null;
        }

        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in ParseObject(zoneWeightsJson))
        {
            if (property.Value is not null)
            {
                weights[property.Key] = property.Value.GetValue<double>();
            }
        }

        return new ZoneWeightSettingsWire(weights, serpBudgets is not null);
    }

    public static string SerializeRules(IReadOnlyList<UrlRewriteRuleWire> rules) =>
        JsonSerializer.Serialize(rules.Select(r => new UrlRewriteRule(r.Pattern, r.Replacement, r.Enabled)).ToList(), ApiJsonOptions.Default);

    public static string SerializeMappings(IReadOnlyList<HostMappingWire> mappings) =>
        JsonSerializer.Serialize(mappings.Select(m => new HostMappingEntry(m.FromHost, m.ToHost)).ToList(), ApiJsonOptions.Default);

    public static string SerializeAimd(AimdSettingsWire aimd)
    {
        var politeness = new JsonObject
        {
            ["aimd"] = new JsonObject
            {
                ["initialRateR0"] = aimd.InitialRateR0,
                ["alpha"] = aimd.Alpha,
                ["beta"] = aimd.Beta,
                ["kappa"] = aimd.Kappa,
                ["ewmaTtfbWeight"] = aimd.EwmaTtfbWeight,
            },
        };
        return politeness.ToJsonString();
    }

    private static IReadOnlyList<UrlRewriteRuleWire> DeserializeRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var rules = JsonSerializer.Deserialize<List<UrlRewriteRule>>(json, ApiJsonOptions.Default) ?? [];
            return rules.Select(r => new UrlRewriteRuleWire(r.Pattern, r.Replacement, r.Enabled)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<HostMappingWire> DeserializeMappings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<HostMappingEntry>>(json, ApiJsonOptions.Default) ?? [];
            return entries.Select(e => new HostMappingWire(e.FromHost, e.ToHost)).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Applies additive extension settings onto a project row (null keeps stored values).</summary>
    public static ProjectDto ApplyExtensions(
        ProjectDto project,
        ContentScopeSettingsWire? contentScope,
        RewriteSettingsWire? rewrite,
        ConsentSettingsWire? consent,
        ZoneWeightSettingsWire? zoneWeights)
    {
        if (contentScope is not null)
        {
            project = project with { ContentScope = new ContentScopeDefinition(contentScope.IncludeSelectors, contentScope.ExcludeSelectors) };
        }

        if (rewrite is not null)
        {
            project = project with
            {
                RewriteRulesJson = SerializeRules(rewrite.Rules),
                HostMappingJson = SerializeMappings(rewrite.HostMappings),
            };
        }

        if (consent is not null)
        {
            project = project with
            {
                ConsentModeEnabled = consent.ConsentModeV2Enabled,
                BeaconPatternsJson = consent.BeaconDetectionEnabled ? project.BeaconPatternsJson ?? "[]" : null,
            };
        }

        if (zoneWeights is not null)
        {
            project = project with
            {
                ZoneWeightsJson = JsonSerializer.Serialize(zoneWeights.ZoneWeights),
                SerpBudgets = zoneWeights.SerpMetricsEnabled
                    ? project.SerpBudgets ?? new SerpBudgetSettings(600, 960, "default")
                    : null,
            };
        }

        return project;
    }

    /// <summary>Applies additive extension settings onto a configuration row (null keeps stored values).
    /// WP-10-A1: the trailing capture fields follow the same null-keeps-stored convention;
    /// a non-null capture document REPLACES the stored document (absent knobs take defaults).</summary>
    public static CrawlConfigurationDto ApplyExtensions(
        CrawlConfigurationDto configuration,
        ContentScopeSettingsWire? contentScope,
        RewriteSettingsWire? rewrite,
        ConsentSettingsWire? consent,
        ZoneWeightSettingsWire? zoneWeights,
        string? captureMode = null,
        CaptureSettingsWire? captureSettings = null,
        RedirectCheckSettingsWire? redirectChecks = null)
    {
        if (contentScope is not null)
        {
            configuration = configuration with { ContentScope = new ContentScopeDefinition(contentScope.IncludeSelectors, contentScope.ExcludeSelectors) };
        }

        if (rewrite is not null)
        {
            configuration = configuration with
            {
                RewriteRulesJson = SerializeRules(rewrite.Rules),
                HostMappingJson = SerializeMappings(rewrite.HostMappings),
            };
        }

        if (consent is not null)
        {
            configuration = configuration with
            {
                ConsentModeEnabled = consent.ConsentModeV2Enabled,
                BeaconPatternsJson = consent.BeaconDetectionEnabled ? configuration.BeaconPatternsJson ?? "[]" : null,
            };
        }

        if (zoneWeights is not null)
        {
            configuration = configuration with
            {
                ZoneWeightsJson = JsonSerializer.Serialize(zoneWeights.ZoneWeights),
                SerpBudgets = zoneWeights.SerpMetricsEnabled
                    ? configuration.SerpBudgets ?? new SerpBudgetSettings(600, 960, "default")
                    : null,
            };
        }

        // WP-10-A1 (D-021): capture-mode fields. The controller validates the
        // mode token and pattern-capture tokens before calling; null keeps the
        // stored value, non-null replaces it.
        if (captureMode is not null)
        {
            configuration = configuration with
            {
                CaptureMode = EnumWireNames.TryParse<CaptureMode>(captureMode, out var mode) ? mode : configuration.CaptureMode,
            };
        }

        if (captureSettings is not null)
        {
            configuration = configuration with { CaptureSettings = ToCaptureSettingsDocument(captureSettings) };
        }

        if (redirectChecks is not null)
        {
            configuration = configuration with { RedirectChecks = ToRedirectChecksDocument(redirectChecks) };
        }

        return configuration;
    }

    /// <summary>Host-mapping storage entry (API-owned payload shape).</summary>
    /// <param name="FromHost">Source host.</param>
    /// <param name="ToHost">Target host.</param>
    public sealed record HostMappingEntry(string FromHost, string ToHost);
}
