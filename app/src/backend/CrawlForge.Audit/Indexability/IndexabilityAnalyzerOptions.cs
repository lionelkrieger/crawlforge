namespace CrawlForge.Audit.Indexability;

/// <summary>
/// Indexability analyzer composition options (WP-09-05 soft-404 dedup).
/// </summary>
/// <param name="EmitSoft404Findings">
/// When true, the analyzer emits <c>indexability.soft-404</c> findings itself
/// using the injected <see cref="Contracts.Audit.ISoft404Detector"/>. Defaults
/// to <b>false</b>: in the audit pipeline the dedicated
/// <c>ISoft404Detector</c> step (<c>AuditPipeline.RunAsync</c>) is the SOLE
/// emission path for soft-404 findings, guaranteeing one finding per URL
/// (probe-2 measured a double emission when both paths emitted). Enable only
/// for standalone compositions that run the indexability analyzer WITHOUT the
/// pipeline's dedicated soft-404 step.
/// </param>
public sealed record IndexabilityAnalyzerOptions(bool EmitSoft404Findings = false)
{
    /// <summary>Default options: soft-404 emission delegated to the pipeline's dedicated detector step.</summary>
    public static IndexabilityAnalyzerOptions Default { get; } = new();
}
