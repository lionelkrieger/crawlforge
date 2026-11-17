namespace CrawlForge.Tools.Governance;

/// <summary>
/// Curated detection data for all governance rules. Identifier patterns are camelCase
/// token sequences matched against declarations, invocations and type references
/// (never comments, never anonymous diagnostic declarations). Vendor hosts and endpoint
/// paths are matched in transmission context unless noted. Package lists cover NuGet
/// (.csproj PackageReference) and npm (package.json dependency keys).
/// </summary>
internal static class PatternLists
{
    /// <summary>Identifier token-sequence patterns per rule (declarations / invocations / type refs).</summary>
    public static IReadOnlyDictionary<string, string[][]> IdentifierPatterns { get; } =
        new Dictionary<string, string[][]>(StringComparer.Ordinal)
        {
            [RuleCatalog.Nr01] =
            [
                ["machine", "id"], ["machine", "guid"], ["machine", "name"], ["hardware", "id"], ["hwid"],
                ["device", "id"], ["installation", "id"], ["install", "id"], ["bios", "serial"],
                ["cpu", "id"], ["processor", "id"], ["mac", "address"], ["physical", "address"],
                ["serial", "number"], ["motherboard", "serial"], ["baseboard", "serial"],
                ["machine", "fingerprint"], ["hardware", "fingerprint"], ["os", "fingerprint"],
                ["platform", "serial"], ["platform", "uuid"], ["system", "uuid"], ["smbios"],
            ],
            [RuleCatalog.Nr02] =
            [
                ["license"], ["licence"], ["entitlement"],
                // Bare "subscription" is NOT matched: the real-time hub uses pub/sub
                // event subscriptions (SubscriptionPayload). Licensing subscriptions
                // carry tier/plan/state/billing vocabulary.
                ["subscription", "tier"], ["subscription", "plan"], ["subscription", "status"],
                ["subscription", "state"], ["subscription", "expiry"], ["subscription", "expires"],
                ["subscription", "billing"], ["subscription", "renew"], ["subscription", "renewal"],
                ["subscription", "license"], ["subscription", "seat"], ["subscription", "trial"],
                ["paid", "subscription"],
                ["activation", "key"], ["activation", "code"], ["activation", "service"],
                ["trial", "period"], ["trial", "expiry"], ["trial", "license"],
                ["seat", "count"], ["seat", "limit"], ["license", "grace"],
                ["expiry", "recheck"], ["license", "recheck"], ["deactivation"],
                ["license", "cache"], ["license", "server"], ["vendor", "header"], ["identity", "header"],
            ],
            [RuleCatalog.Nr03] =
            [
                ["account"], ["sign", "in"], ["sign", "up"], ["login"], ["logout"],
                ["password", "reset"], ["user", "profile"], ["user", "registration"],
                ["register", "user"], ["user", "register"],
            ],
            [RuleCatalog.Nr04] =
            [
                ["enroll"], ["cloud", "relay"], ["remote", "relay"], ["relay", "bridge"],
                ["relay", "server"], ["relay", "client"], ["control", "relay"],
                ["remote", "heartbeat"], ["heartbeat", "report"], ["report", "heartbeat"],
                ["remote", "liveness"],
                ["liveness", "report"], ["remote", "credential"], ["credential", "issuance"],
                ["remote", "revocation"], ["revocation", "channel"], ["cloud", "bridge"], ["remote", "bridge"],
            ],
            [RuleCatalog.Nr05] =
            [
                ["remote", "flag"], ["cloud", "flag"], ["flag", "proxy"],
                ["fetch", "flags"], ["download", "flags"], ["resolve", "remote", "flags"],
            ],
            [RuleCatalog.Nr06] =
            [
                ["telemetry", "client"], ["remote", "telemetry"], ["telemetry", "upload"],
                ["telemetry", "transmit"], ["usage", "analytics"],
                ["analytics", "track"], ["crash", "upload"], ["send", "crash"], ["remote", "crash"],
                ["transmit", "diagnostic"], ["upload", "diagnostic"], ["send", "diagnostic"], ["auto", "transmit"],
            ],
            [RuleCatalog.Nr07] =
            [
                ["report", "authorization"], ["report", "auth"], ["server", "audit", "config"],
                ["remote", "audit", "config"], ["server", "provided", "config"], ["update", "feed"],
                ["account", "update"], ["marketing", "hook"], ["newsletter"], ["vendor", "cloud"],
                ["cloud", "alert"], ["cloud", "logging"], ["cloud", "audit"],
                ["marketing", "endpoint"], ["marketing", "subscribe"],
            ],
            [RuleCatalog.Nr08] =
            [
                ["license", "limit"], ["license", "quota"], ["account", "quota"], ["account", "limit"],
                ["plan", "limit"], ["tier", "limit"], ["seat", "limit"], ["quota", "plan"],
                ["feature", "gate"], ["gated", "feature"], ["licensed", "feature"], ["license", "tier"],
                ["subscription", "tier"], ["remote", "limit"], ["limit", "remote"], ["entitlement", "limit"],
                ["check", "entitlement"], ["validate", "license"], ["get", "entitlement"],
            ],
            [RuleCatalog.NrE1] =
            [
                ["plugin", "marketplace"], ["remote", "plugin"], ["plugin", "download"],
                ["download", "plugin"], ["plugin", "auto", "update"], ["plugin", "store"],
                ["fetch", "plugin"], ["remote", "code"], ["load", "remote", "code"],
                ["remote", "plugin", "registry"],
            ],
            [RuleCatalog.NrE2] =
            [
                ["fetch", "signature"], ["download", "signature"], ["fetch", "stopword"],
                ["download", "stopword"], ["fetch", "stop", "word"], ["download", "stop", "word"],
                ["fetch", "fingerprint"], ["download", "fingerprint"], ["remote", "catalog"],
                ["catalog", "fetch"], ["catalog", "download"], ["sync", "catalog"],
                ["remote", "signature"], ["remote", "stopword"],
            ],
            [RuleCatalog.NrE3] =
            [
                ["solve", "captcha"], ["captcha", "solve"], ["captcha", "solver"],
                ["bypass", "challenge"], ["challenge", "bypass"], ["bypass", "waf"], ["waf", "bypass"],
                ["challenge", "replay"], ["replay", "challenge"], ["forge", "token"], ["token", "forge"],
                ["token", "forging"], ["evasion", "automation"], ["evasion", "tool"], ["evasion", "script"],
                ["evade", "waf"], ["evade", "detection"], ["anti", "captcha"], ["captcha", "bypass"],
            ],
            [RuleCatalog.NrE4] =
            [
                ["reputation"], ["remote", "blocklist"], ["fetch", "blocklist"], ["download", "blocklist"],
                ["cloud", "blocklist"], ["blocklist", "api"], ["reputation", "api"],
                ["ip", "reputation"], ["domain", "reputation"],
            ],
            [RuleCatalog.NrE5] =
            [
                ["plugin", "mutation"], ["plugin", "mutate"], ["plugin", "write", "api"],
                ["plugin", "backend", "write"], ["plugin", "crawl", "control"], ["plugin", "lifecycle", "control"],
                ["grant", "plugin", "write"], ["plugin", "write", "access"], ["plugin", "command"],
                ["execute", "plugin", "command"], ["plugin", "config", "write"],
            ],
            [RuleCatalog.NrE6] =
            [
                ["plugin", "license"], ["plugin", "account"], ["plugin", "entitlement"],
                ["plugin", "subscription"], ["plugin", "activation"], ["plugin", "feature", "gate"],
                ["plugin", "gating"], ["gated", "plugin"], ["plugin", "sign", "in"],
                ["plugin", "trial"], ["plugin", "seat"],
            ],
            [RuleCatalog.NrE7] =
            [
                ["openai"], ["anthropic"], ["cloud", "embedding"], ["remote", "embedding"],
                ["hosted", "llm"], ["remote", "llm"], ["cloud", "llm"], ["llm", "api", "client"],
                ["remote", "vector"], ["cloud", "vector"], ["pinecone"], ["weaviate"],
                ["cohere", "client"], ["mistral", "client"], ["groq", "client"], ["together", "client"],
                ["voyage", "client"], ["jina", "client"],
            ],
            [RuleCatalog.NrE8] =
            [
                ["remote", "enrichment"], ["log", "enrichment", "api"], ["log", "enrichment", "service"],
                ["fetch", "bot", "list"], ["download", "bot", "list"], ["remote", "bot", "list"],
                ["bot", "verification", "remote"], ["remote", "bot", "verification"], ["bot", "verification", "api"],
                ["verify", "bot", "remote"], ["ip", "enrichment"], ["remote", "log", "enrich"],
                ["enrich", "remote"], ["bot", "list", "api"],
            ],
            [RuleCatalog.NrE9] =
            [
                ["llms", "registry"], ["geo", "registry"], ["remote", "geo"], ["publish", "llms"],
                ["llms", "publish"], ["auto", "publish"], ["ai", "vendor", "submission"],
                ["submit", "ai", "vendor"], ["vendor", "submission"], ["submit", "llms"],
                ["llms", "submission"], ["geo", "publish"], ["registry", "submit"], ["publish", "registry"],
            ],
        };

    /// <summary>Identity dimensions banned from events/metrics/logs/plugin surfaces (ID-01).</summary>
    public static string[][] IdentityDimensions { get; } =
    [
        ["machine", "id"], ["machine", "name"], ["machine", "guid"], ["device", "id"], ["device", "name"],
        ["hardware", "id"], ["hwid"], ["installation", "id"], ["install", "id"],
        ["account", "id"], ["account", "name"], ["user", "id"], ["user", "name"], ["user", "domain"],
        ["license", "key"], ["license", "id"], ["entitlement", "id"], ["subscription", "id"], ["seat", "id"],
        ["mac", "address"], ["physical", "address"], ["serial", "number"], ["bios", "serial"],
        ["cpu", "id"], ["processor", "id"],
        ["machine", "fingerprint"], ["hardware", "fingerprint"], ["install", "fingerprint"],
        ["device", "fingerprint"], ["os", "fingerprint"], ["os", "version"], ["os", "build"],
    ];

    /// <summary>Machine-identity string literals (registry keys, WMI classes, IOKit properties).</summary>
    public static string[] MachineIdentityLiterals { get; } =
    [
        "MachineGuid", "SOFTWARE\\Microsoft\\Cryptography", "Win32_BIOS", "Win32_BaseBoard",
        "Win32_Processor", "Win32_OperatingSystem", "Win32_ComputerSystem", "Win32_NetworkAdapter",
        "Win32_DiskDrive", "root\\cimv2", "winmgmts", "ioreg", "IOServiceMatching", "IORegistryEntry",
        "IOPlatformSerialNumber", "IOPlatformUUID", "PlatformExpertDevice", "SerialNumber", "ProcessorId",
    ];

    /// <summary>Shell/tooling strings that read OS identity stores (TS/JS child-process or script context).</summary>
    public static string[] OsIdentityToolingLiterals { get; } =
    [
        "reg query", "reg add", "reg delete", "regedit", "wmic", "Get-WmiObject", "Get-CimInstance",
        "system_profiler", "MachineGuid", "SOFTWARE\\Microsoft\\Cryptography",
    ];

    /// <summary>Identity-bearing vendor header literals (NR-02).</summary>
    public static string[] VendorHeaderLiterals { get; } =
    [
        "X-License", "X-Entitlement", "X-Subscription", "X-Activation", "X-Seat", "X-Trial",
    ];

    /// <summary>
    /// Vendor hosts flagged in transmission/endpoint context (NR-06, E3, E4, E8).
    /// Detection catalogues (e.g. MarTech tag inventory) store these as data and are exempt
    /// in pure-data context; E7 hosts are stricter (flagged in any literal context).
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> ContextualVendorHosts { get; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [RuleCatalog.Nr06] =
            [
                "google-analytics.com", "analytics.google.com", "googletagmanager.com", "sentry.io",
                "bugsnag.com", "rollbar.com", "raygun.io", "raygun.com", "datadoghq.com", "newrelic.com",
                "nr-data.net", "mixpanel.com", "amplitude.com", "posthog.com", "segment.io", "segment.com",
                "fullstory.com", "logrocket.com", "logrocket.io", "heapanalytics.com", "appcenter.ms",
                "crashlytics.com", "doubleclick.net", "facebook.net", "analytics.tiktok.com", "bat.bing.com",
                "clarity.ms", "mouseflow.com", "hotjar.com", "crazyegg.com", "cloudflareinsights.com",
                "usefathom.com", "plausible.io", "matomo.cloud", "piwik.pro",
            ],
            [RuleCatalog.NrE3] =
            [
                "2captcha.com", "anti-captcha.com", "anticaptcha.com", "capsolver.com", "azcaptcha.com",
                "deathbycaptcha.com", "imagetyperz.com",
            ],
            [RuleCatalog.NrE4] =
            [
                "abuseipdb.com", "virustotal.com", "spamhaus.org", "projecthoneypot.org",
                "ipqualityscore.com", "fraudlabspro.com", "botvrij.eu",
            ],
            [RuleCatalog.NrE8] =
            [
                "ipinfo.io", "ipapi.co", "ipstack.com", "ipdata.co", "ipgeolocation.io", "abstractapi.com",
            ],
        };

    /// <summary>Cloud LLM/embedding/vector vendor hosts — flagged in ANY literal context (NR-E7).</summary>
    public static string[] CloudLlmVendorHosts { get; } =
    [
        "api.openai.com", "api.anthropic.com", "generativelanguage.googleapis.com",
        "api.cohere.ai", "api.cohere.com", "api.mistral.ai", "api.together.xyz", "api.groq.com",
        "openai.azure.com", "bedrock-runtime", "api.voyageai.com", "api.jina.ai", "api.deepseek.com",
        "dashscope.aliyuncs.com", "api.perplexity.ai", "api-inference.huggingface.co", "api.fireworks.ai",
        "api.endpoints.anyscale.com", "pinecone.io", "weaviate.cloud", "cloud.qdrant.io", "zillizcloud.com",
        "api.replicate.com", "api.ai21.com", "api.cerebras.ai", "api.sambanova.ai",
    ];

    /// <summary>
    /// Endpoint path segment sequences flagged in transmission context outside approved
    /// boundary scopes. Matched against the URL path split on '/'.
    /// </summary>
    public static IReadOnlyDictionary<string, string[][]> EndpointPathPatterns { get; } =
        new Dictionary<string, string[][]>(StringComparer.Ordinal)
        {
            [RuleCatalog.Nr02] =
            [
                ["license"], ["licenses"], ["activate"], ["activation"], ["entitlements"],
                ["subscription"], ["subscriptions"], ["trial"], ["seats"],
            ],
            [RuleCatalog.Nr03] =
            [
                ["login"], ["signin"], ["sign-in"], ["signup"], ["sign-up"], ["register"],
                ["registration"], ["password-reset"], ["account"], ["accounts"],
            ],
            [RuleCatalog.Nr04] =
            [
                ["enroll"], ["enrollment"], ["heartbeat"], ["liveness"], ["relay"],
                ["credentials", "issue"], ["credentials", "rotate"], ["revoke"], ["revocation"],
            ],
            [RuleCatalog.Nr05] =
            [
                ["flags"], ["feature-flags"], ["featureflags"], ["flags", "resolve"],
                ["config", "remote"], ["remote-config"],
            ],
            [RuleCatalog.Nr06] =
            [
                ["collect"], ["track"], ["telemetry"], ["analytics"], ["crash"], ["crashes"],
                ["ingest"], ["pageview"], ["beacon"],
            ],
            [RuleCatalog.Nr07] =
            [
                ["marketing"], ["newsletter"], ["update-feed"], ["updates", "feed"], ["alerts", "cloud"],
                ["audit", "config"],
            ],
            [RuleCatalog.NrE1] =
            [
                ["plugins", "download"], ["plugins", "marketplace"], ["marketplace", "plugins"],
                ["plugin-registry"], ["plugins", "registry"],
            ],
            [RuleCatalog.NrE2] =
            [
                ["signatures"], ["stopwords"], ["stop-words"], ["fingerprints"], ["waf-signatures"],
            ],
            [RuleCatalog.NrE4] =
            [
                ["reputation"], ["blocklist"], ["blocklists"], ["blacklist"], ["ip-reputation"],
                ["domain-reputation"],
            ],
            [RuleCatalog.NrE8] =
            [
                ["enrich"], ["enrichment"], ["bot-list"], ["botlist"], ["bots", "verify"],
                ["verify-bot"], ["ip-info"],
            ],
            [RuleCatalog.NrE9] =
            [
                ["llms.txt", "registry"], ["geo", "registry"], ["submit", "llms"], ["llms", "submit"],
                ["ai-vendor", "submit"], ["registry", "llms"],
            ],
        };

    /// <summary>
    /// Embedding/LLM API path sequences (NR-E7): flagged in transmission context when the
    /// host is NON-loopback. Loopback daemons (Ollama/LM Studio class) are explicitly permitted.
    /// </summary>
    public static string[][] EmbeddingLlmPathPatterns { get; } =
    [
        ["embeddings"], ["embed"], ["chat", "completions"], ["completions"], ["messages"],
        ["responses"], ["generate"], ["chat"],
    ];

    /// <summary>C# method names that transmit data over HTTP (endpoint transmission context).</summary>
    public static string[] HttpSendMethods { get; } =
    [
        "GetAsync", "PostAsync", "PutAsync", "DeleteAsync", "PatchAsync", "SendAsync",
        "GetStringAsync", "GetByteArrayAsync", "GetStreamAsync", "PostAsJsonAsync", "PutAsJsonAsync",
        "GetFromJsonAsync", "DownloadFile", "DownloadData", "DownloadString", "DownloadFileAsync",
        "DownloadDataTaskAsync", "DownloadStringTaskAsync", "UploadFile", "UploadData", "UploadString",
        "UploadValues", "OpenRead", "GetResponse", "GetResponseAsync", "OpenWrite",
    ];

    /// <summary>C# outbound client types (NET-02 construction detection).</summary>
    public static string[] OutboundClientTypes { get; } =
    [
        "HttpClient", "WebClient", "HttpWebRequest", "SocketsHttpHandler", "HttpClientHandler",
        "Socket", "TcpClient", "UdpClient", "SmtpClient", "ClientWebSocket", "FtpWebRequest",
    ];

    /// <summary>C# telemetry invocation names whose arguments are identity-scanned (ID-01).</summary>
    public static string[] TelemetryMethodNames { get; } =
    [
        "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical",
        "BeginScope", "WriteEvent", "TrackEvent", "TrackTrace", "TrackMetric", "TrackException",
        "TrackPageView", "TrackDependency", "SetTag", "AddTag", "Enrich", "WithProperty", "ForContext",
        "PostMessage", "Emit", "Publish",
    ];

    /// <summary>TS/JS telemetry call names whose arguments are identity-scanned (ID-01).</summary>
    public static string[] TextTelemetryCallNames { get; } =
    [
        "track", "trackEvent", "trackPageView", "gtag", "fbq", "ga", "postMessage", "emit", "publish",
        "setTag", "addTag", "captureException", "captureMessage",
    ];

    /// <summary>Crawl-lifecycle / configuration mutation methods banned inside plugin surfaces (NR-E5).</summary>
    public static string[][] PluginMutationPatterns { get; } =
    [
        ["start", "crawl"], ["stop", "crawl"], ["cancel", "crawl"], ["pause", "crawl"], ["resume", "crawl"],
        ["start", "audit"], ["stop", "audit"], ["cancel", "audit"], ["pause", "audit"], ["resume", "audit"],
        ["update", "configuration"], ["write", "configuration"], ["save", "configuration"],
        ["update", "settings"], ["write", "settings"], ["save", "settings"],
        ["update", "config"], ["write", "config"],
        ["create", "project"], ["update", "project"], ["delete", "project"],
    ];

    /// <summary>Banned NuGet packages: (name-or-prefix, rule, isPrefix).</summary>
    public static (string Name, string RuleId, bool IsPrefix)[] BannedNuGetPackages { get; } =
    [
        ("Microsoft.Win32.Registry", RuleCatalog.Nr01, false),
        ("System.Management", RuleCatalog.Nr01, false),
        ("Microsoft.ApplicationInsights", RuleCatalog.Nr06, true),
        ("Sentry", RuleCatalog.Nr06, true),
        ("Raygun", RuleCatalog.Nr06, true),
        ("Rollbar", RuleCatalog.Nr06, true),
        ("Bugsnag", RuleCatalog.Nr06, true),
        ("Datadog", RuleCatalog.Nr06, true),
        ("NewRelic", RuleCatalog.Nr06, true),
        ("Mixpanel", RuleCatalog.Nr06, true),
        ("Amplitude", RuleCatalog.Nr06, true),
        ("PostHog", RuleCatalog.Nr06, true),
        ("Segment.Analytics", RuleCatalog.Nr06, true),
        ("OpenTelemetry.Exporter.OpenTelemetryProtocol", RuleCatalog.Nr06, false),
        ("Serilog.Sinks.Seq", RuleCatalog.Nr06, false),
        ("Serilog.Sinks.ApplicationInsights", RuleCatalog.Nr06, false),
        ("Serilog.Sinks.Sentry", RuleCatalog.Nr06, false),
        ("Microsoft.AppCenter.Crashes", RuleCatalog.Nr06, false),
        ("Microsoft.AppCenter.Analytics", RuleCatalog.Nr06, false),
        ("LaunchDarkly", RuleCatalog.Nr05, true),
        ("Unleash", RuleCatalog.Nr05, true),
        ("Flagsmith", RuleCatalog.Nr05, true),
        ("ConfigCat", RuleCatalog.Nr05, true),
        ("Optimizely", RuleCatalog.Nr05, true),
        ("Splitio", RuleCatalog.Nr05, true),
        ("Microsoft.Extensions.Configuration.AzureAppConfiguration", RuleCatalog.Nr05, false),
        ("Microsoft.Azure.AppConfiguration.AspNetCore", RuleCatalog.Nr05, false),
        ("Microsoft.AspNetCore.Identity", RuleCatalog.Nr03, true),
        ("IdentityServer4", RuleCatalog.Nr03, true),
        ("Duende", RuleCatalog.Nr03, true),
        ("OpenIddict", RuleCatalog.Nr03, true),
        ("Microsoft.Identity.Client", RuleCatalog.Nr03, true),
        ("Microsoft.Identity.Web", RuleCatalog.Nr03, true),
        ("AWSSDK.CloudWatch", RuleCatalog.Nr07, true),
        ("Google.Cloud.Logging", RuleCatalog.Nr07, true),
        ("Microsoft.Azure.Management.Monitor", RuleCatalog.Nr07, false),
        ("OpenAI", RuleCatalog.NrE7, false),
        ("Azure.AI.OpenAI", RuleCatalog.NrE7, false),
        ("Anthropic.SDK", RuleCatalog.NrE7, false),
        ("Cohere", RuleCatalog.NrE7, false),
        ("Pinecone.NET", RuleCatalog.NrE7, false),
        ("Weaviate.Client", RuleCatalog.NrE7, false),
        ("Qdrant.Client", RuleCatalog.NrE7, false),
        ("Microsoft.Extensions.AI.OpenAI", RuleCatalog.NrE7, false),
        ("Microsoft.SemanticKernel", RuleCatalog.NrE7, true),
    ];

    /// <summary>Banned npm packages: (name-or-prefix, rule, isPrefix).</summary>
    public static (string Name, string RuleId, bool IsPrefix)[] BannedNpmPackages { get; } =
    [
        ("winreg", RuleCatalog.Nr01, false),
        ("registry-js", RuleCatalog.Nr01, false),
        ("native-reg", RuleCatalog.Nr01, false),
        ("applicationinsights", RuleCatalog.Nr06, false),
        ("@microsoft/applicationinsights", RuleCatalog.Nr06, true),
        ("@sentry/", RuleCatalog.Nr06, true),
        ("sentry-expo", RuleCatalog.Nr06, false),
        ("@datadog/", RuleCatalog.Nr06, true),
        ("dd-trace", RuleCatalog.Nr06, false),
        ("rollbar", RuleCatalog.Nr06, false),
        ("bugsnag", RuleCatalog.Nr06, false),
        ("@bugsnag/", RuleCatalog.Nr06, true),
        ("raygun", RuleCatalog.Nr06, false),
        ("mixpanel", RuleCatalog.Nr06, false),
        ("mixpanel-browser", RuleCatalog.Nr06, false),
        ("amplitude-js", RuleCatalog.Nr06, false),
        ("@amplitude/", RuleCatalog.Nr06, true),
        ("posthog-js", RuleCatalog.Nr06, false),
        ("posthog-node", RuleCatalog.Nr06, false),
        ("analytics.js", RuleCatalog.Nr06, false),
        ("@segment/", RuleCatalog.Nr06, true),
        ("react-ga", RuleCatalog.Nr06, false),
        ("react-ga4", RuleCatalog.Nr06, false),
        ("vue-analytics", RuleCatalog.Nr06, false),
        ("@fullstory/", RuleCatalog.Nr06, true),
        ("logrocket", RuleCatalog.Nr06, false),
        ("@logrocket/", RuleCatalog.Nr06, true),
        ("newrelic", RuleCatalog.Nr06, false),
        ("@newrelic/", RuleCatalog.Nr06, true),
        ("@opentelemetry/exporter-trace-otlp", RuleCatalog.Nr06, true),
        ("@opentelemetry/exporter-metrics-otlp", RuleCatalog.Nr06, true),
        ("launchdarkly-js-client-sdk", RuleCatalog.Nr05, false),
        ("launchdarkly-react-client-sdk", RuleCatalog.Nr05, false),
        ("@launchdarkly/", RuleCatalog.Nr05, true),
        ("unleash-client", RuleCatalog.Nr05, false),
        ("unleash-proxy-client", RuleCatalog.Nr05, false),
        ("flagsmith", RuleCatalog.Nr05, false),
        ("flagsmith-react", RuleCatalog.Nr05, false),
        ("@splitsoftware/", RuleCatalog.Nr05, true),
        ("optimizely", RuleCatalog.Nr05, false),
        ("@optimizely/", RuleCatalog.Nr05, true),
        ("configcat", RuleCatalog.Nr05, false),
        ("configcat-js", RuleCatalog.Nr05, false),
        ("passport", RuleCatalog.Nr03, false),
        ("passport-local", RuleCatalog.Nr03, false),
        ("next-auth", RuleCatalog.Nr03, false),
        ("@auth0/", RuleCatalog.Nr03, true),
        ("@clerk/", RuleCatalog.Nr03, true),
        ("@supabase/", RuleCatalog.Nr03, true),
        ("firebase", RuleCatalog.Nr03, false),
        ("firebase-admin", RuleCatalog.Nr03, false),
        ("firebase/", RuleCatalog.Nr03, true),
        ("@firebase/auth", RuleCatalog.Nr03, false),
        ("openai", RuleCatalog.NrE7, false),
        ("@anthropic-ai/sdk", RuleCatalog.NrE7, false),
        ("@google/generative-ai", RuleCatalog.NrE7, false),
        ("@google-cloud/vertexai", RuleCatalog.NrE7, false),
        ("cohere-ai", RuleCatalog.NrE7, false),
        ("@mistralai/mistralai", RuleCatalog.NrE7, false),
        ("groq-sdk", RuleCatalog.NrE7, false),
        ("@azure/openai", RuleCatalog.NrE7, false),
        ("@aws-sdk/client-bedrock-runtime", RuleCatalog.NrE7, false),
        ("voyageai", RuleCatalog.NrE7, false),
        ("@pinecone-database/pinecone", RuleCatalog.NrE7, false),
        ("weaviate-client", RuleCatalog.NrE7, false),
        ("weaviate-ts-client", RuleCatalog.NrE7, false),
        ("@qdrant/js-client-rest", RuleCatalog.NrE7, false),
        ("@zilliz/milvus2-sdk-node", RuleCatalog.NrE7, false),
        ("@huggingface/inference", RuleCatalog.NrE7, false),
        ("fireworks-ai", RuleCatalog.NrE7, false),
        ("2captcha", RuleCatalog.NrE3, false),
        ("2captcha-api", RuleCatalog.NrE3, false),
        ("anti-captcha", RuleCatalog.NrE3, false),
        ("anticaptcha", RuleCatalog.NrE3, false),
        ("capsolver", RuleCatalog.NrE3, false),
        ("azcaptcha", RuleCatalog.NrE3, false),
        ("abuseipdb", RuleCatalog.NrE4, false),
        ("virustotal", RuleCatalog.NrE4, false),
        ("virustotal-api", RuleCatalog.NrE4, false),
        ("ipqualityscore", RuleCatalog.NrE4, false),
        ("ipinfo", RuleCatalog.NrE8, false),
        ("node-ipinfo", RuleCatalog.NrE8, false),
        ("ipapi", RuleCatalog.NrE8, false),
        ("ipstack", RuleCatalog.NrE8, false),
        ("ipdata", RuleCatalog.NrE8, false),
    ];

    /// <summary>SDK namespace prefixes detected in C# using directives.</summary>
    public static (string Prefix, string RuleId)[] BannedUsingPrefixes { get; } =
    [
        ("Microsoft.Win32", RuleCatalog.Nr01),
        ("System.Management", RuleCatalog.Nr01),
        ("Microsoft.ApplicationInsights", RuleCatalog.Nr06),
        ("Sentry", RuleCatalog.Nr06),
        ("Raygun", RuleCatalog.Nr06),
        ("Rollbar", RuleCatalog.Nr06),
        ("Bugsnag", RuleCatalog.Nr06),
        ("Datadog", RuleCatalog.Nr06),
        ("NewRelic", RuleCatalog.Nr06),
        ("Mixpanel", RuleCatalog.Nr06),
        ("Amplitude", RuleCatalog.Nr06),
        ("PostHog", RuleCatalog.Nr06),
        ("Segment", RuleCatalog.Nr06),
        ("Microsoft.AppCenter", RuleCatalog.Nr06),
        ("LaunchDarkly", RuleCatalog.Nr05),
        ("Unleash", RuleCatalog.Nr05),
        ("Flagsmith", RuleCatalog.Nr05),
        ("ConfigCat", RuleCatalog.Nr05),
        ("Optimizely", RuleCatalog.Nr05),
        ("Splitio", RuleCatalog.Nr05),
        ("Microsoft.AspNetCore.Identity", RuleCatalog.Nr03),
        ("IdentityServer4", RuleCatalog.Nr03),
        ("Duende", RuleCatalog.Nr03),
        ("OpenIddict", RuleCatalog.Nr03),
        ("Microsoft.Identity.Client", RuleCatalog.Nr03),
        ("Microsoft.Identity.Web", RuleCatalog.Nr03),
        ("OpenAI", RuleCatalog.NrE7),
        ("Azure.AI.OpenAI", RuleCatalog.NrE7),
        ("Anthropic", RuleCatalog.NrE7),
        ("Pinecone", RuleCatalog.NrE7),
        ("Weaviate", RuleCatalog.NrE7),
        ("Qdrant", RuleCatalog.NrE7),
    ];

    /// <summary>TS/JS module specifiers whose import indicates an outbound client (NET-02).</summary>
    public static string[] OutboundTextModules { get; } =
    [
        "axios", "got", "ky", "superagent", "node-fetch", "undici", "request", "needle",
        "http", "https", "net", "tls", "dgram", "ws", "eventsource",
    ];
}
