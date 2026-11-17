namespace CrawlForge.Security.Diagnostics;

/// <summary>
/// Severity levels of local structured log events (FS-CRAWL-001 §10.5). Levels
/// are local technical dimensions only — no identity dimension exists.
/// </summary>
public enum LocalLogLevel
{
    /// <summary>Fine-grained tracing.</summary>
    Trace = 0,

    /// <summary>Developer diagnostics.</summary>
    Debug = 1,

    /// <summary>Normal operational events.</summary>
    Information = 2,

    /// <summary>Unexpected but recoverable conditions.</summary>
    Warning = 3,

    /// <summary>Failures of an operation.</summary>
    Error = 4,

    /// <summary>Fatal conditions terminating a subsystem or the process.</summary>
    Critical = 5,
}