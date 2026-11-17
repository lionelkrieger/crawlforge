namespace CrawlForge.Contracts.Enums;

/// <summary>
/// Finding severity vocabulary (FS-CRAWL-001 §7.5, preserved verbatim by
/// FS-CRAWL-002 §1.5): Error, Warning, Notice, Pass.
/// </summary>
public enum FindingSeverity
{
    /// <summary>A defect that must be fixed.</summary>
    Error = 0,

    /// <summary>A problem that should be fixed.</summary>
    Warning = 1,

    /// <summary>An informational observation.</summary>
    Notice = 2,

    /// <summary>A check that passed.</summary>
    Pass = 3,
}

/// <summary>
/// Finding taxonomy categories (schema catalog: findings.category). The three
/// additive categories Log Intelligence, AI Search (GEO), and Analytics &amp; Tagging
/// are defined by FS-CRAWL-002 Appendix A; additive categories may be appended.
/// </summary>
public enum AuditCategory
{
    /// <summary>Status, robots, canonicalization, and directive indexability checks.</summary>
    Indexability = 0,

    /// <summary>Content quality, duplication, readability, spelling, and n-gram topics.</summary>
    Content = 1,

    /// <summary>Link graph, orphan, anchor, and link-equity checks.</summary>
    Linking = 2,

    /// <summary>Structured data, i18n, mobile friendliness, and technology checks.</summary>
    StructuredData = 3,

    /// <summary>Accessibility rule families.</summary>
    Accessibility = 4,

    /// <summary>Rendered performance, Core Web Vitals, budgets, and code coverage.</summary>
    Performance = 5,

    /// <summary>Crawl-health checks such as WAF challenge detection and throttling.</summary>
    Crawl = 6,

    /// <summary>Custom extraction, custom search, and JavaScript snippet checks.</summary>
    Custom = 7,

    /// <summary>Additive: server-log intelligence (FS-CRAWL-002 §11).</summary>
    LogIntelligence = 8,

    /// <summary>Additive: AI-search / GEO readiness (FS-CRAWL-002 §13).</summary>
    AiSearchGeo = 9,

    /// <summary>Additive: MarTech, analytics, and tag integrity (FS-CRAWL-002 §14).</summary>
    AnalyticsTagging = 10,
}

/// <summary>
/// Six-zone DOM classification of a link's placement
/// (additive link_edges.dom_zone column, FS-CRAWL-002 §15.5).
/// Six named zones (header, navigation, main content, sidebar, footer, breadcrumb)
/// plus a residual Other region; zone evidence is stored alongside the classification.
/// </summary>
public enum DomZone
{
    /// <summary>Page header region.</summary>
    Header = 0,

    /// <summary>Primary navigation region.</summary>
    Navigation = 1,

    /// <summary>Main content region.</summary>
    MainContent = 2,

    /// <summary>Sidebar / complementary region.</summary>
    Sidebar = 3,

    /// <summary>Page footer region.</summary>
    Footer = 4,

    /// <summary>Unclassified or residual region.</summary>
    Other = 5,

    /// <summary>Additive: breadcrumb trail region (WP-03-A1; FS-CRAWL-002 §15.1/§15.2
    /// require a distinct breadcrumb zone with its own default weight 0.6).</summary>
    Breadcrumb = 6,
}

/// <summary>
/// The four text zones used by the n-gram and topical-density analyzer
/// (FS-CRAWL-002 §5: four-zone 1/2/3-gram TF/DF analysis).
/// </summary>
public enum TextZone
{
    /// <summary>Document title text.</summary>
    Title = 0,

    /// <summary>Heading text (h1–h6).</summary>
    Headings = 1,

    /// <summary>Scoped visible body text.</summary>
    Body = 2,

    /// <summary>Anchor text of links.</summary>
    AnchorText = 3,
}
