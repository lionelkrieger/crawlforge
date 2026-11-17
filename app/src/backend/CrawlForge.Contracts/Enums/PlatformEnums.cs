namespace CrawlForge.Contracts.Enums;

/// <summary>
/// Configured database provider (schema catalog: application_settings database
/// provider selection). Every logical schema object is implemented by both
/// providers (provider agnosticism, precedence rule 6).
/// </summary>
public enum DatabaseProviderKind
{
    /// <summary>Embedded file-based provider (default, seeded at first run).</summary>
    Sqlite = 0,

    /// <summary>Optional user-owned external client-server provider.</summary>
    MySql = 1,
}

/// <summary>
/// Content-store artifact kinds (schema catalog: content_artifacts.artifact kind).
/// </summary>
public enum ContentArtifactKind
{
    /// <summary>Raw response body.</summary>
    Body = 0,

    /// <summary>Rendered page screenshot.</summary>
    Screenshot = 1,

    /// <summary>Rendered DOM snapshot.</summary>
    RenderedDom = 2,
}

/// <summary>
/// Typed value kinds for custom content extraction
/// (schema catalog: extracted_content.value type; FS-CRAWL-001 §7.9).
/// </summary>
public enum ExtractedValueType
{
    /// <summary>Free text value.</summary>
    Text = 0,

    /// <summary>Numeric value.</summary>
    Number = 1,

    /// <summary>Boolean value.</summary>
    Boolean = 2,

    /// <summary>Date/time value.</summary>
    DateTime = 3,
}

/// <summary>
/// Export output formats (FS-CRAWL-001 §8.4/§8.5: spreadsheet, CSV, HTML bundle,
/// PDF, XML sitemap).
/// </summary>
public enum ExportFormat
{
    /// <summary>Excel spreadsheet.</summary>
    Excel = 0,

    /// <summary>Comma-separated values.</summary>
    Csv = 1,

    /// <summary>HTML report bundle.</summary>
    Html = 2,

    /// <summary>PDF report.</summary>
    Pdf = 3,

    /// <summary>XML sitemap (or sitemap index).</summary>
    XmlSitemap = 4,
}

/// <summary>
/// Classes of user-configured export delivery targets (schema catalog:
/// export_destinations.class). Destinations are user-owned, off by default,
/// never vendor services (exclusion row 7). Additive classes may be appended.
/// </summary>
public enum ExportDestinationClass
{
    /// <summary>A local filesystem folder.</summary>
    LocalFolder = 0,

    /// <summary>An FTP endpoint configured by the user.</summary>
    Ftp = 1,

    /// <summary>An SFTP endpoint configured by the user.</summary>
    Sftp = 2,

    /// <summary>An HTTP(S) webhook endpoint configured by the user.</summary>
    Webhook = 3,

    /// <summary>A destination class outside the named labels.</summary>
    Other = 4,
}

/// <summary>
/// Lifecycle status of an export job (REST: POST /api/exports returns an
/// identifier immediately; progress flows over the hub).
/// </summary>
public enum ExportJobStatus
{
    /// <summary>Job accepted but not started.</summary>
    Queued = 0,

    /// <summary>Export generation in progress.</summary>
    Running = 1,

    /// <summary>Export file ready for download or delivery.</summary>
    Completed = 2,

    /// <summary>Export generation failed.</summary>
    Failed = 3,
}

/// <summary>
/// Trigger type of a schedule firing (schema catalog: schedule_runs.trigger type).
/// </summary>
public enum ScheduleTriggerType
{
    /// <summary>Fired by the recurrence rule.</summary>
    Scheduled = 0,

    /// <summary>Fired by an explicit user "run now" action.</summary>
    Manual = 1,

    /// <summary>Additive: catch-up firing for recurrences missed while the app was closed (FS-CRAWL-002 §6.2/§6.3).</summary>
    CatchUp = 2,
}

/// <summary>
/// Outcome of a schedule firing (schema catalog: schedule_runs.outcome).
/// </summary>
public enum ScheduleRunOutcome
{
    /// <summary>An audit was started successfully.</summary>
    Success = 0,

    /// <summary>The firing failed to start an audit.</summary>
    Failed = 1,

    /// <summary>The firing was skipped (for example a run was already active).</summary>
    Skipped = 2,
}

/// <summary>
/// Kinds of local diagnostic artifacts (REST: /api/diagnostics/artifacts).
/// Artifacts are local-only, user-viewable and purgeable; they are never
/// transmitted automatically (exclusion row 6).
/// </summary>
public enum DiagnosticArtifactKind
{
    /// <summary>Local structured log file.</summary>
    Log = 0,

    /// <summary>Locally generated native minidump.</summary>
    Minidump = 1,
}

/// <summary>
/// Transports of the local automation interface recorded in the action log
/// (schema catalog: automation_action_log.transport). All transports are
/// loopback-local (exclusion row 4 permits the local MCP server).
/// </summary>
public enum AutomationTransportKind
{
    /// <summary>Standard-input/output child-process bridge.</summary>
    Stdio = 0,

    /// <summary>Loopback server-sent events transport.</summary>
    Sse = 1,

    /// <summary>Loopback HTTP transport.</summary>
    Http = 2,
}

/// <summary>
/// Outcome of a local automation invocation (schema catalog:
/// automation_action_log.outcome).
/// </summary>
public enum AutomationOutcome
{
    /// <summary>Invocation completed successfully.</summary>
    Success = 0,

    /// <summary>Invocation failed.</summary>
    Failed = 1,
}

/// <summary>
/// Permission grants of a local UI plugin (schema catalog: ui_plugins.grants).
/// Plugins receive read-only data and UI contribution rights only — never crawl
/// lifecycle or configuration mutations (exclusion E5), and no licensing,
/// accounts, or feature gating exists (exclusion E6).
/// </summary>
[Flags]
public enum PluginPermission
{
    /// <summary>No grants.</summary>
    None = 0,

    /// <summary>Read-only access to report data.</summary>
    ReadData = 1,

    /// <summary>Right to contribute UI surfaces.</summary>
    ContributeUi = 2,
}

/// <summary>
/// Verdict of a migration redirect-matrix row (additive findings
/// redirect-matrix.*, FS-CRAWL-002 §10).
/// </summary>
public enum RedirectMatrixVerdict
{
    /// <summary>Source redirects to the expected target.</summary>
    Pass = 0,

    /// <summary>Final target differs from the expected target.</summary>
    TargetMismatch = 1,

    /// <summary>Redirect chain exceeded the hop limit.</summary>
    ChainExceeded = 2,

    /// <summary>Final response status was not 200.</summary>
    FinalNon200 = 3,

    /// <summary>Canonical URL of the final page disagrees with the expected target.</summary>
    CanonicalMismatch = 4,
}
