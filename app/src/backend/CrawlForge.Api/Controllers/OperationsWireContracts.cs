using System.Text.Json;

namespace CrawlForge.Api.Controllers;

/// <summary>Wire export creation request.</summary>
/// <param name="AuditId">Audit to export.</param>
/// <param name="Format">Output format key (xlsx, csv, html, pdf).</param>
/// <param name="Datasets">Selected dataset keys.</param>
/// <param name="DestinationId">Optional delivery destination.</param>
public sealed record ExportCreateRequest(string AuditId, string Format, IReadOnlyList<string> Datasets, string? DestinationId);

/// <summary>Wire export creation response.</summary>
/// <param name="ExportId">Export job identifier.</param>
/// <param name="State">Job state wire value.</param>
public sealed record ExportCreateResponse(string ExportId, string State);

/// <summary>Wire export job record (additive GET /api/exports/{id}).</summary>
/// <param name="Id">Export job identifier.</param>
/// <param name="AuditId">Exported audit identifier.</param>
/// <param name="Format">Output format wire value.</param>
/// <param name="State">Job state wire value.</param>
/// <param name="CreatedAt">Creation timestamp.</param>
/// <param name="FilePath">Local file path when completed.</param>
/// <param name="Datasets">Selected dataset keys.</param>
public sealed record ExportJobWire(string Id, string AuditId, string Format, string State, DateTimeOffset CreatedAt, string? FilePath, IReadOnlyList<string> Datasets);

/// <summary>Wire destination record.</summary>
/// <param name="Id">Destination identifier.</param>
/// <param name="Name">Destination name.</param>
/// <param name="Kind">Destination class wire key.</param>
/// <param name="Target">Target path/URL.</param>
/// <param name="Username">Stored username, when present.</param>
/// <param name="SecretMask">Masked credential indicator; never the clear secret.</param>
/// <param name="CreatedAt">Creation timestamp (API-surface metadata).</param>
/// <param name="UpdatedAt">Last-update timestamp (API-surface metadata).</param>
/// <param name="Enabled">Whether delivery is enabled (WP-06-A3; the delivery engine skips disabled destinations).</param>
public sealed record DestinationWire(string Id, string Name, string Kind, string Target, string? Username, string? SecretMask, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool Enabled);

/// <summary>Wire destination creation request.</summary>
/// <param name="Name">Destination name.</param>
/// <param name="Kind">Destination class wire key.</param>
/// <param name="Target">Target path/URL.</param>
/// <param name="Username">Optional username.</param>
/// <param name="Secret">Optional clear secret on write; never echoed back.</param>
public sealed record DestinationCreateRequest(string Name, string Kind, string Target, string? Username, string? Secret);

/// <summary>Wire destination update request.</summary>
/// <param name="Name">Optional name.</param>
/// <param name="Kind">Optional class key.</param>
/// <param name="Target">Optional target.</param>
/// <param name="Username">Optional username; null clears.</param>
/// <param name="Secret">Optional secret; empty clears.</param>
/// <param name="Enabled">Optional delivery toggle; absent keeps the stored state (WP-06-A3).</param>
public sealed record DestinationUpdateRequest(string? Name, string? Kind, string? Target, string? Username, string? Secret, bool? Enabled = null);

/// <summary>Wire destination test verdict.</summary>
/// <param name="Reachable">Whether the endpoint is reachable.</param>
/// <param name="CredentialsValid">Whether credentials were accepted.</param>
/// <param name="Message">Verdict detail.</param>
public sealed record DestinationTestResponse(bool Reachable, bool CredentialsValid, string? Message);

/// <summary>Wire delivery request (additive POST /api/destinations/{id}/deliver).</summary>
/// <param name="ExportId">Completed export job identifier.</param>
public sealed record DestinationDeliverRequest(string ExportId);

/// <summary>Wire delivery verdict.</summary>
/// <param name="ExportId">Delivered export job identifier.</param>
/// <param name="DestinationId">Target destination identifier.</param>
/// <param name="State">Delivery state wire value.</param>
/// <param name="Message">Outcome detail, when present.</param>
public sealed record DestinationDeliverResponse(string ExportId, string DestinationId, string State, string? Message);

/// <summary>Wire schedule completion action.</summary>
/// <param name="Kind">Action kind key.</param>
/// <param name="Parameters">Action-specific parameters, when present.</param>
public sealed record ScheduleCompletionActionWire(string Kind, IReadOnlyDictionary<string, JsonElement>? Parameters);

/// <summary>Wire schedule record.</summary>
/// <param name="Id">Schedule identifier.</param>
/// <param name="ProjectId">Owning project identifier.</param>
/// <param name="ConfigurationId">Configuration reference.</param>
/// <param name="Name">Display name (API-surface metadata).</param>
/// <param name="Enabled">Whether the schedule is enabled.</param>
/// <param name="Cron">Local recurrence expression (wire name cron).</param>
/// <param name="NextDueAt">Next-due timestamp, when enabled.</param>
/// <param name="LastRunAt">Latest firing start, when any.</param>
/// <param name="CompletionActions">Configured completion actions.</param>
/// <param name="CreatedAt">Creation timestamp (API-surface metadata).</param>
/// <param name="UpdatedAt">Last-update timestamp (API-surface metadata).</param>
public sealed record ScheduleWire(
    string Id,
    string ProjectId,
    string ConfigurationId,
    string Name,
    bool Enabled,
    string? Cron,
    DateTimeOffset? NextDueAt,
    DateTimeOffset? LastRunAt,
    IReadOnlyList<ScheduleCompletionActionWire> CompletionActions,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Wire schedule creation request.</summary>
/// <param name="ConfigurationId">Configuration reference.</param>
/// <param name="Name">Display name.</param>
/// <param name="Enabled">Initial enablement; default true.</param>
/// <param name="Cron">Local recurrence expression; null/empty = manual only.</param>
/// <param name="CompletionActions">Optional completion actions.</param>
public sealed record ScheduleCreateRequest(string ConfigurationId, string Name, bool? Enabled, string? Cron, IReadOnlyList<ScheduleCompletionActionWire>? CompletionActions);

/// <summary>Wire schedule update request.</summary>
/// <param name="ConfigurationId">Optional configuration reference.</param>
/// <param name="Name">Optional display name.</param>
/// <param name="Enabled">Optional enablement.</param>
/// <param name="Cron">Optional recurrence expression; empty clears.</param>
/// <param name="CompletionActions">Optional replacement actions.</param>
public sealed record ScheduleUpdateRequest(string? ConfigurationId, string? Name, bool? Enabled, string? Cron, IReadOnlyList<ScheduleCompletionActionWire>? CompletionActions);

/// <summary>Wire schedule enable/disable verdict.</summary>
/// <param name="ScheduleId">Schedule identifier.</param>
/// <param name="Enabled">New enablement.</param>
/// <param name="NextDueAt">New next-due timestamp, when enabled.</param>
public sealed record ScheduleStateResponse(string ScheduleId, bool Enabled, DateTimeOffset? NextDueAt);

/// <summary>Wire run-now verdict.</summary>
/// <param name="AuditId">Started audit identifier.</param>
public sealed record ScheduleRunNowResponse(string AuditId);

/// <summary>Wire completion-action result.</summary>
/// <param name="Kind">Action kind key.</param>
/// <param name="Succeeded">Whether the action succeeded.</param>
/// <param name="Detail">Outcome detail.</param>
public sealed record CompletionActionResultWire(string Kind, bool Succeeded, string? Detail);

/// <summary>Wire schedule run-history row.</summary>
/// <param name="Id">Run identifier.</param>
/// <param name="ScheduleId">Owning schedule identifier.</param>
/// <param name="AuditId">Started audit identifier; empty when the firing failed.</param>
/// <param name="TriggerType">Trigger wire value.</param>
/// <param name="StartedAt">Firing start.</param>
/// <param name="CompletedAt">Firing finish, when finished.</param>
/// <param name="Outcome">Outcome wire value.</param>
/// <param name="CompletionActionResults">Action results (hub-published; empty in history rows).</param>
public sealed record ScheduleRunWire(
    string Id,
    string ScheduleId,
    string AuditId,
    string TriggerType,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string Outcome,
    IReadOnlyList<CompletionActionResultWire> CompletionActionResults);

/// <summary>Wire saved query record.</summary>
/// <param name="Id">Query identifier.</param>
/// <param name="Name">Query name.</param>
/// <param name="RuleTree">Query rule tree.</param>
/// <param name="CreatedAt">Creation timestamp (API-surface metadata).</param>
/// <param name="UpdatedAt">Last-update timestamp (API-surface metadata).</param>
public sealed record SavedQueryWire(string Id, string Name, JsonElement RuleTree, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Wire saved-query creation request.</summary>
/// <param name="Name">Query name.</param>
/// <param name="RuleTree">Query rule tree.</param>
public sealed record SavedQueryCreateRequest(string Name, JsonElement RuleTree);

/// <summary>Wire saved-query update request (identifier in body).</summary>
/// <param name="Id">Query identifier.</param>
/// <param name="Name">Optional name.</param>
/// <param name="RuleTree">Optional rule tree.</param>
public sealed record SavedQueryUpdateRequest(string Id, string? Name, JsonElement? RuleTree);

/// <summary>Wire dictionary entry.</summary>
/// <param name="Word">Accepted word.</param>
/// <param name="Language">Language tag.</param>
/// <param name="AddedAt">Addition timestamp.</param>
public sealed record DictionaryEntryWire(string Word, string Language, DateTimeOffset AddedAt);

/// <summary>Wire dictionary add request.</summary>
/// <param name="Word">Word to accept.</param>
/// <param name="Language">Language tag.</param>
public sealed record DictionaryAddRequest(string Word, string Language);

/// <summary>Wire dictionary remove request.</summary>
/// <param name="Word">Word to remove.</param>
/// <param name="Language">Optional language restriction.</param>
public sealed record DictionaryRemoveRequest(string Word, string? Language);

/// <summary>Wire dictionary mutation verdict.</summary>
/// <param name="Word">Affected word.</param>
/// <param name="Language">Affected language.</param>
/// <param name="Added">True after an add.</param>
/// <param name="Removed">True after a removal.</param>
public sealed record DictionaryMutationResponse(string Word, string Language, bool? Added, bool? Removed);

/// <summary>Wire plugin registry entry.</summary>
/// <param name="Id">Plugin identifier.</param>
/// <param name="Name">Plugin name.</param>
/// <param name="Version">Plugin version.</param>
/// <param name="Description">Manifest description, when present.</param>
/// <param name="Directory">Local discovery directory.</param>
/// <param name="State">Load state wire value.</param>
/// <param name="Enabled">Whether the plugin is enabled.</param>
/// <param name="Permissions">Granted permission wire keys.</param>
/// <param name="LoadError">Load error, when failed.</param>
/// <param name="DiscoveredAt">Registration timestamp.</param>
public sealed record PluginRegistryEntryWire(
    string Id,
    string Name,
    string Version,
    string? Description,
    string Directory,
    string State,
    bool Enabled,
    IReadOnlyList<string> Permissions,
    string? LoadError,
    DateTimeOffset DiscoveredAt);

/// <summary>Wire plugin update request.</summary>
/// <param name="Enabled">Optional enablement.</param>
/// <param name="GrantPermissions">Permissions to grant.</param>
/// <param name="RevokePermissions">Permissions to revoke.</param>
public sealed record PluginUpdateRequest(bool? Enabled, IReadOnlyList<string>? GrantPermissions, IReadOnlyList<string>? RevokePermissions);

/// <summary>Wire plugin rescan verdict.</summary>
/// <param name="Added">Newly registered plugins.</param>
/// <param name="Removed">Registry entries whose directory vanished.</param>
/// <param name="Plugins">Effective registry after the scan.</param>
public sealed record PluginRescanResponse(int Added, int Removed, IReadOnlyList<PluginRegistryEntryWire> Plugins);
