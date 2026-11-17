namespace CrawlForge.Tools.Governance;

/// <summary>
/// The complete governance rule catalog: 17 negative requirements
/// (FS-CRAWL-001 §3 rows 1-8 => NR-01..NR-08; FS-CRAWL-002 §1.6 E1-E9 => NR-E1..NR-E9)
/// plus the always-on network boundary (NET-01 listener, NET-02 outbound) and the
/// privacy-invariant identity-dimension lint (ID-01, FS-CRAWL-002 §1.4 precedence rule 8).
///
/// Detection philosophy: rules detect BEHAVIOR (API calls, declarations, imports,
/// package references, endpoints in transmission context) — never mere words in
/// comments or diagnostic declarations. Heuristic bases are documented per rule and
/// emitted in every report.
/// </summary>
public static class RuleCatalog
{
    public const string Nr01 = "NR-01";
    public const string Nr02 = "NR-02";
    public const string Nr03 = "NR-03";
    public const string Nr04 = "NR-04";
    public const string Nr05 = "NR-05";
    public const string Nr06 = "NR-06";
    public const string Nr07 = "NR-07";
    public const string Nr08 = "NR-08";
    public const string NrE1 = "NR-E1";
    public const string NrE2 = "NR-E2";
    public const string NrE3 = "NR-E3";
    public const string NrE4 = "NR-E4";
    public const string NrE5 = "NR-E5";
    public const string NrE6 = "NR-E6";
    public const string NrE7 = "NR-E7";
    public const string NrE8 = "NR-E8";
    public const string NrE9 = "NR-E9";
    public const string Net01 = "NET-01";
    public const string Net02 = "NET-02";
    public const string Id01 = "ID-01";

    public static IReadOnlyList<GovernanceRule> All { get; } =
    [
        new(Nr01,
            "Machine identity / OS fingerprinting",
            "FS-CRAWL-001 §3 row 1",
            GovernanceSeverity.Error,
            "Behavioral: registry/WMI/IO-registry API usage (usings, object creations, invocations), "
            + "hardware-serial/machine-GUID string literals (e.g. MachineGuid, Win32_BIOS, IOPlatformSerialNumber), "
            + "identity-shaped declarations/invocations (machineId, hardwareId, deviceId, installationId, MAC/serial reads), "
            + "Environment.MachineName/OSVersion member access. Comments and local random-ID generation (Guid.NewGuid, "
            + "RandomNumberGenerator) are ignored. Environment.SpecialFolder.* path-enum accesses are directory locations, "
            + "not identity reads, and are NOT flagged. Identifier-name matching is heuristic (camelCase token sequences)."),
        new(Nr02,
            "Licensing client & state model",
            "FS-CRAWL-001 §3 row 2",
            GovernanceSeverity.Error,
            "Heuristic identifier tokens: license/licence/entitlement/activation-key/trial-period/seat-count/"
            + "grace-period/recheck and licensing subscription compounds (subscription+tier/plan/status/billing/...) "
            + "in declarations, invocations and type references; identity-bearing vendor header literals "
            + "(X-License-*, X-Entitlement-*); license/entitlement endpoint paths in transmission context; license-cache file "
            + "names. Bare 'subscription' is NOT matched (the loopback hub uses pub/sub event subscriptions). "
            + "User-destination credentials are not vendor headers and are not flagged."),
        new(Nr03,
            "Account system",
            "FS-CRAWL-001 §3 row 3",
            GovernanceSeverity.Error,
            "Behavioral: identity-provider packages/usings (ASP.NET Identity, IdentityServer, Duende, OpenIddict, passport, "
            + "next-auth, Auth0, Clerk, Supabase, Firebase auth); sign-in/sign-up/login/logout/password-reset/user-profile/"
            + "account declarations and invocations; Environment.UserName/UserDomainName reads; auth endpoint paths in "
            + "transmission context. Bare 'UserName' on user-destination credential objects is NOT flagged (permitted). "
            + "Environment.SpecialFolder.* path-enum accesses (UserProfile/ApplicationData/LocalApplicationData/...) are "
            + "directory locations, not account reads, and are NOT flagged."),
        new(Nr04,
            "Remote control plane / relay",
            "FS-CRAWL-001 §3 row 4",
            GovernanceSeverity.Error,
            "Heuristic identifier tokens: enroll*, cloud/remote relay, remote heartbeat/liveness reporting, remote credential "
            + "issuance/rotation/revocation, remote bridge; enrollment/heartbeat/liveness/relay endpoint paths in transmission "
            + "context. The local system-health heartbeat and loopback MCP token rotation are NOT flagged (bare 'heartbeat' "
            + "and loopback endpoints are exempt)."),
        new(Nr05,
            "Remote feature flags",
            "FS-CRAWL-001 §3 row 5",
            GovernanceSeverity.Error,
            "Behavioral: remote flag SDK packages (LaunchDarkly, Unleash, Flagsmith, Split, Optimizely, ConfigCat, Azure App "
            + "Configuration); remote-flag declarations/invocations (RemoteFlag*, FetchFlags, ResolveRemoteFlags); flag-service "
            + "endpoint paths in transmission context. Local toggle surfaces (LocalFeatureAvailability, FeatureToggles) and "
            + "diagnostic declarations such as 'remoteFlagResolution = false' are NOT flagged."),
        new(Nr06,
            "Telemetry & remote diagnostics",
            "FS-CRAWL-001 §3 row 6",
            GovernanceSeverity.Error,
            "Behavioral: analytics/crash-reporting SDK packages and usings (Application Insights, Sentry, Raygun, Rollbar, "
            + "Bugsnag, Datadog, New Relic, Mixpanel, Amplitude, PostHog, Segment, FullStory, LogRocket, Crashlytics, OTLP "
            + "exporters); tracking invocations (TrackEvent/TrackPageView/CaptureException/gtag/fbq/dataLayer.push); analytics "
            + "vendor hosts in any literal; collection endpoint paths (/collect, /track, /telemetry, /crash) in transmission "
            + "context outside approved boundary scopes. Local ILogger/Serilog file logs, local minidumps (MiniDumpWriteDump) "
            + "and pull-only Prometheus exposition are NOT flagged."),
        new(Nr07,
            "Server-gated functionality",
            "FS-CRAWL-001 §3 row 7",
            GovernanceSeverity.Error,
            "Heuristic identifier tokens: report-viewing authorization, server-provided audit configuration, account-bound "
            + "update feeds, marketing hooks, newsletter hooks, vendor cloud logging/alerting; vendor cloud-monitoring packages; "
            + "marketing/newsletter/update-feed endpoint paths in transmission context. User-configured destinations are not "
            + "vendor services and are not flagged."),
        new(Nr08,
            "Entitlement-based limits",
            "FS-CRAWL-001 §3 row 8",
            GovernanceSeverity.Error,
            "Heuristic identifier tokens combining limits/quotas/features with license/account/plan/tier/seat/remote inputs "
            + "(LicenseLimit, AccountQuota, PlanLimit, FeatureGate, CheckEntitlement, ValidateLicense). Local unlimited-limits "
            + "resolution and local toggles are NOT flagged."),
        new(NrE1,
            "Remote plugin registry / marketplace",
            "FS-CRAWL-002 §1.6 E1",
            GovernanceSeverity.Error,
            "Behavioral: plugin marketplace/registry/download/auto-update declarations and invocations; remote code loading "
            + "(dynamic import of http(s) URLs, Assembly.LoadFrom with http(s) argument); plugin-marketplace endpoint paths in "
            + "transmission context. Local plugin directory scanning and local Assembly.Load discovery are NOT flagged."),
        new(NrE2,
            "Remote signature / stop-word catalog services",
            "FS-CRAWL-002 §1.6 E2",
            GovernanceSeverity.Error,
            "Behavioral: fetch/download/sync of signatures/stop-words/fingerprints/catalogs (declarations and invocations); "
            + "catalog asset paths (signatures, stopwords, fingerprints) in transmission context outside approved boundary "
            + "scopes. Bundled-asset File.ReadAllText loads and local user additions are NOT flagged."),
        new(NrE3,
            "Challenge solving or bypass",
            "FS-CRAWL-002 §1.6 E3",
            GovernanceSeverity.Error,
            "Behavioral: captcha-solving/challenge-bypass/token-forging/evasion-automation declarations and invocations; "
            + "captcha-solver service packages and hosts (2captcha, anti-captcha, CapSolver, AZCaptcha). WAF/challenge "
            + "DETECTION vocabulary (ChallengePage, WafChallenge, CaptchaDetected) is NOT flagged."),
        new(NrE4,
            "Cloud reputation / blocklist services",
            "FS-CRAWL-002 §1.6 E4",
            GovernanceSeverity.Error,
            "Behavioral: reputation/blocklist-consultation declarations and invocations; reputation vendor hosts (AbuseIPDB, "
            + "VirusTotal, Spamhaus, Project Honey Pot, IPQualityScore) in any literal; reputation/blocklist endpoint paths in "
            + "transmission context outside approved boundary scopes. Bundled CIDR/catalog matching is NOT flagged."),
        new(NrE5,
            "Plugin backend mutation",
            "FS-CRAWL-002 §1.6 E5",
            GovernanceSeverity.Error,
            "Behavioral: plugin-mutation/write-access declarations and invocations anywhere; inside plugin surfaces (path "
            + "contains 'plugin'), invocations of crawl-lifecycle or configuration mutation methods (Start/Stop/Pause/Resume/"
            + "CancelCrawl, Update/Write/SaveConfiguration, Create/Update/DeleteProject). Read-only plugin data hooks "
            + "(onLoad/onTabActivate/onDataUpdate/onCrawlComplete) are NOT flagged."),
        new(NrE6,
            "New identity / entitlement concepts in plugins",
            "FS-CRAWL-002 §1.6 E6",
            GovernanceSeverity.Error,
            "Heuristic identifier tokens: plugin licensing/accounts/entitlements/subscriptions/activation/feature-gating/"
            + "trials/seats in declarations and invocations. The free, local, ungated plugin surface is NOT flagged."),
        new(NrE7,
            "Remote embedding / vector / LLM APIs",
            "FS-CRAWL-002 §1.6 E7",
            GovernanceSeverity.Error,
            "Behavioral: cloud LLM/embedding/vector vendor hosts (OpenAI, Anthropic, Google Generative Language, Cohere, "
            + "Mistral, Together, Groq, Azure OpenAI, Bedrock, Voyage, Jina, DeepSeek, DashScope, Perplexity, Hugging Face "
            + "inference, Fireworks, Pinecone, Weaviate Cloud, Qdrant Cloud, Zilliz) in any literal; vendor SDK packages; "
            + "cloud-embedding/hosted-LLM declarations; embedding/completion endpoint paths on NON-loopback hosts in "
            + "transmission context. Bundled ONNX and loopback daemons (127.0.0.1/::1/localhost, e.g. Ollama :11434) are NOT "
            + "flagged."),
        new(NrE8,
            "Remote log enrichment / bot-verification services",
            "FS-CRAWL-002 §1.6 E8",
            GovernanceSeverity.Error,
            "Behavioral: remote log-enrichment/bot-list/bot-verification declarations and invocations; IP-enrichment vendor "
            + "hosts (ipinfo, ipapi, ipstack, ipdata, MaxMind) in any literal; enrichment endpoint paths in transmission "
            + "context outside approved boundary scopes. Bundled CIDR/agent-profile assets and the consented DNS check "
            + "(System.Net.Dns / dns.resolve) are NOT flagged."),
        new(NrE9,
            "Remote GEO / llms.txt registries or publishing",
            "FS-CRAWL-002 §1.6 E9",
            GovernanceSeverity.Error,
            "Behavioral: remote llms.txt/GEO registry, auto-publishing and AI-vendor submission declarations/invocations; "
            + "registry/submission endpoint paths in transmission context. Local llms.txt generation (File.WriteAllText) and "
            + "crawl-target llms.txt probing through the crawler are NOT flagged."),
        new(Net01,
            "Non-loopback listener",
            "FS-CRAWL-001 §2.6 network boundary; FS-CRAWL-002 §1.4 rule 2",
            GovernanceSeverity.Error,
            "Behavioral: TcpListener/IPEndPoint/Socket binds and Kestrel Listen calls with IPAddress.Any/IPv6Any/None, "
            + "ListenAnyIP, wildcard UseUrls (http://* or http://+), 0.0.0.0/:: literals in binding context; TS server.listen "
            + "with 0.0.0.0/::/* host or without a host argument (Node default binds all interfaces — heuristic). Loopback "
            + "binds (IPAddress.Loopback, 127.0.0.1, ::1, localhost) are NOT flagged."),
        new(Net02,
            "Outbound call outside approved network boundary",
            "FS-CRAWL-001 §2.6 network boundary; FS-CRAWL-002 §1.4 rule 2",
            GovernanceSeverity.Error,
            "Heuristic call-site lint: outbound client construction/usage (HttpClient, WebClient, HttpWebRequest, Socket, "
            + "TcpClient, UdpClient; TS fetch/axios/XMLHttpRequest/http.request/net.connect/WebSocket/EventSource) is flagged "
            + "unless (a) the target is provably loopback or same-origin relative, or (b) the call site sits in an approved "
            + "boundary scope (crawl target/proxy, user destination, user external DB, consented DNS) per boundary-allowlist.json "
            + "(namespace prefixes, type-name patterns, path fragments — configurable via --boundary-config)."),
        new(Id01,
            "Identity dimension in event/metric/log/plugin surface",
            "FS-CRAWL-002 §1.4 precedence rule 8 (privacy invariant); FS-CRAWL-001 §3 rows 1, 6",
            GovernanceSeverity.Error,
            "Behavioral: at telemetry call sites (ILogger.Log*, BeginScope, EventSource.WriteEvent, Track*, counter/histogram/"
            + "meter Record/Add, Activity.SetTag, hub SendAsync, Serilog Enrich/WithProperty; TS console.*, track/gtag/fbq, "
            + "postMessage/emit, metric record/observe) arguments are scanned for identity dimensions (machine/device/hardware/"
            + "installation/account/user/license/entitlement/subscription IDs, MAC/serial/CPU identifiers, OS version, machine "
            + "fingerprints) as identifiers, {Template} placeholders and object keys. Plugin surfaces additionally flag identity "
            + "keys/declarations anywhere. WAF/site fingerprints and loopback addresses are NOT identity dimensions. "
            + "Environment.SpecialFolder.* path-enum expressions are stripped from payload scans (directory locations, "
            + "never identity dimensions)."),
    ];

    public static GovernanceRule Get(string id) =>
        All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentException($"Unknown rule id '{id}'.", nameof(id));
}
