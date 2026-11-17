using CrawlForge.Contracts.Crawl;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Contracts.Entities;

/// <summary>
/// Recurring audit rule (schema catalog: schedule_definitions). Local only;
/// first-run state contains no schedules.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="ProjectId">Owning project reference.</param>
/// <param name="ConfigurationId">Configuration reference.</param>
/// <param name="RecurrenceRule">Recurrence rule expression.</param>
/// <param name="IsEnabled">Whether the schedule is enabled.</param>
/// <param name="LimitOverrides">Optional limit overrides for scheduled runs.</param>
/// <param name="CompletionActionsJson">Completion actions payload.</param>
/// <param name="NextDueAt">Next-due timestamp (UTC), when enabled.</param>
public sealed record ScheduleDefinitionDto(
    long Id,
    long ProjectId,
    long ConfigurationId,
    string RecurrenceRule,
    bool IsEnabled,
    CrawlLimitOverrides? LimitOverrides,
    string? CompletionActionsJson,
    DateTimeOffset? NextDueAt);

/// <summary>
/// Schedule firing history row (schema catalog: schedule_runs). Pruned by
/// retention.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="ScheduleDefinitionId">Owning schedule reference.</param>
/// <param name="AuditId">Started audit reference; null on failure.</param>
/// <param name="TriggerType">How the firing was triggered.</param>
/// <param name="Outcome">Firing outcome.</param>
/// <param name="StartedAt">Firing start timestamp (UTC).</param>
/// <param name="FinishedAt">Firing finish timestamp (UTC), when finished.</param>
public sealed record ScheduleRunDto(
    long Id,
    long ScheduleDefinitionId,
    long? AuditId,
    ScheduleTriggerType TriggerType,
    ScheduleRunOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// User-configured delivery target (schema catalog: export_destinations).
/// Off by default; none seeded. Credentials are encrypted at rest and never
/// returned in clear by the REST surface.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="Name">Destination name.</param>
/// <param name="Class">Destination class.</param>
/// <param name="EndpointDetailsJson">Endpoint details payload.</param>
/// <param name="EncryptedCredentialsJson">Encrypted credential payload.</param>
/// <param name="IsEnabled">Whether the destination is enabled.</param>
public sealed record ExportDestinationDto(
    long Id,
    string Name,
    ExportDestinationClass Class,
    string EndpointDetailsJson,
    string? EncryptedCredentialsJson,
    bool IsEnabled);

/// <summary>
/// Accepted spelling (schema catalog: user_dictionary). Suppresses spelling
/// findings; empty at first run.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="Language">Language tag of the dictionary.</param>
/// <param name="Word">Accepted word.</param>
/// <param name="AddedAt">Addition timestamp (UTC).</param>
public sealed record UserDictionaryEntryDto(
    long Id,
    string Language,
    string Word,
    DateTimeOffset AddedAt);

/// <summary>
/// Local agent invocation history row (schema catalog: automation_action_log).
/// User-viewable and purgeable; pruned by retention. No identity dimensions.
/// </summary>
/// <param name="Id">Surrogate key.</param>
/// <param name="Timestamp">Invocation timestamp (UTC).</param>
/// <param name="Transport">Transport the invocation arrived on.</param>
/// <param name="TargetName">Tool/resource name invoked.</param>
/// <param name="ArgumentsSummary">Bounded argument summary.</param>
/// <param name="Outcome">Invocation outcome.</param>
public sealed record AutomationActionLogDto(
    long Id,
    DateTimeOffset Timestamp,
    AutomationTransportKind Transport,
    string TargetName,
    string ArgumentsSummary,
    AutomationOutcome Outcome);
