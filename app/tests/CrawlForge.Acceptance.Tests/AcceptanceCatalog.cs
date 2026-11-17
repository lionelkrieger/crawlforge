namespace CrawlForge.Acceptance.Tests;

/// <summary>
/// WP-07-04 criterion-by-criterion evidence catalog. Every pointer was verified
/// against the wave-7A handoffs (WP-07-01-HANDOFF.md 80/80, benchmark-report.md,
/// tools/network-audit/README.md) and the owning suites' sources. Paths are
/// relative to the app root. A <see cref="EvidencePointer.Member"/> is a test
/// method or class name that must appear in the referenced source file; a null
/// member means the artifact itself is the evidence (existence + marker).
/// </summary>
internal static class AcceptanceCatalog
{
    internal sealed record EvidencePointer(string Path, string? Member = null, string? Marker = null);

    internal sealed record Criterion(int Number, string Title, EvidencePointer[] Pointers);

    private const string E2E = "tests/CrawlForge.E2E.Tests/";
    private const string Boundary = "tests/CrawlForge.Boundary.Tests/";
    private const string Audit = "tests/CrawlForge.Audit.Tests/";
    private const string Intelligence = "tests/CrawlForge.Intelligence.Tests/";
    private const string Crawler = "tests/CrawlForge.Crawler.Tests/";
    private const string Api = "tests/CrawlForge.Api.Tests/";
    private const string Host = "tests/CrawlForge.Host.Tests/";
    private const string Frontend = "tests/frontend/";
    private const string Scale = "tests/CrawlForge.Scale.Benchmarks/artifacts/";
    private const string NetAudit = "tools/network-audit/evidence/";

    internal static readonly Criterion[] All =
    [
        new(1, "First launch offline completes initialization and opens the main hub",
        [
            new(Host + "HostScaffoldTests.cs", "Scaffold_FirstLaunch_CompletesOffline_AndOpensMainHubRoute"),
            new(Boundary + "AirGap/FirstLaunchTests.cs", "FirstLaunchTests"),
            new(NetAudit + "first-launch.md"),
        ]),
        new(2, "No OS identity stores read at any point",
        [
            new(Boundary + "Identity/IdentityStoreProofTests.cs", "IdentityStoreProofTests"),
            new(NetAudit + "identity-runtime-proof.md"),
            new(NetAudit + "identity-static-proof.md"),
        ]),
        new(3, "No outbound traffic idle/reporting; audit/destination/external-db traffic only to configured endpoints",
        [
            new(Boundary + "AirGap/IdleZeroOutboundTests.cs", "IdleZeroOutboundTests"),
            new(Boundary + "Traffic/AuditTrafficTests.cs", "AuditTrafficTests"),
            new(Boundary + "Traffic/ExternalDbAddressingTests.cs", "ExternalDbAddressingTests"),
            new(NetAudit + "idle-reporting.md"),
            new(NetAudit + "audit-traffic.md"),
        ]),
        new(4, "Fresh audit without overrides runs unlimited ceilings on local defaults",
        [
            new(E2E + "AuditLifecycleE2ETests.cs", "FreshAudit_NoOverrides_RunsWithUnlimitedPageCeilings"),
        ]),
        new(5, "No license, account, or entitlement concept in any payload, settings row, or screen",
        [
            new(Boundary + "Entitlement/NoEntitlementConceptTests.cs", "NoEntitlementConceptTests"),
            new(NetAudit + "no-entitlement.md"),
        ]),
        new(6, "Pause/resume/cancel/crash recovery preserve data; checkpoint resume",
        [
            new(E2E + "AuditLifecycleE2ETests.cs", "PauseResume_PreservesCheckpoint_AndCompletes"),
            new(E2E + "AuditLifecycleE2ETests.cs", "Cancel_MidRun_RetainsPartialData"),
            new(E2E + "AuditLifecycleE2ETests.cs", "CrashRecovery_RestartResumesFromCheckpoint"),
        ]),
        new(7, "Report surfaces render offline; exports produce valid local files",
        [
            new(E2E + "ExportValidityE2ETests.cs", "XlsxExport_IsAValidWorkbook_WithDatasetSheetsAndRows"),
            new(E2E + "ExportValidityE2ETests.cs", "PdfExport_IsAValidPdf_WithAtLeastOnePage"),
            new(E2E + "ExportValidityE2ETests.cs", "CsvExport_IsAZipOfRfc4180Csvs_OnePerDataset"),
            new(E2E + "ExportValidityE2ETests.cs", "HtmlExport_IsASelfContainedHtmlReport"),
            new(E2E + "OfflineReauditE2ETests.cs", "ReAudit_Offline_ContentComparison_RendersFromStoredAggregates"),
        ]),
        new(8, "All features available without remote resolution; toggles local-only",
        [
            new(Boundary + "Assets/BundledAssetManifestTests.cs", "BundledAssetManifestTests"),
            new(E2E + "PluginLifecycleE2ETests.cs", "FullLifecycle_Discover_Grant_Revoke_CommandsRejected_RescanIdempotent_VanishRemoves"),
            new(NetAudit + "bundled-assets.md"),
        ]),
        new(9, "Migrations idempotent and versioned; deterministic settings defaults after fresh install",
        [
            new(E2E + "MySqlParityE2ETests.cs", "MySqlMigrations_AreIdempotent_AndDialectPure"),
            new(Host + "HostScaffoldTests.cs", "Scaffold_FirstLaunch_CompletesOffline_AndOpensMainHubRoute"),
        ]),
        new(10, "Single-page analysis returns findings without creating a crawl project",
        [
            new(E2E + "SinglePageAnalysisE2ETests.cs", "SinglePage_AnalyzesOneUrl_WithoutCreatingProjectOrAudit"),
        ]),
        new(11, "Scheduled audit fires unattended with run history; next-due survives restarts",
        [
            new(E2E + "AuditLifecycleE2ETests.cs", "ScheduledRun_FiresUnattended_WithRunHistory_AndStableNextDue"),
        ]),
        new(12, "Missed schedule runs exactly once as catch-up",
        [
            new(E2E + "AuditLifecycleE2ETests.cs", "MissedSchedule_RunsExactlyOnce_AsCatchUp"),
        ]),
        new(13, "Spelling findings: token evidence, page language, user-dictionary suppression",
        [
            new(Audit + "ContentQuality/ContentQualityAnalyzerTests.cs", "Spelling_Findings_Honor_User_Dictionary"),
            new("tests/CrawlForge.Acceptance.Tests/TargetedRemainderTests.cs", "Spelling_TokenEvidence_AndDictionarySuppression"),
        ]),
        new(14, "Destination export uses only user credentials; disabling restores zero non-crawl outbound",
        [
            new(Boundary + "Traffic/DestinationTransferTests.cs", "DestinationTransferTests"),
            new(NetAudit + "destination-transfer.md"),
        ]),
        new(15, "Content comparison offline from content store; re-audit reuses stored bodies",
        [
            new(E2E + "OfflineReauditE2ETests.cs", "ReAudit_Offline_ContentComparison_RendersFromStoredAggregates"),
            new(E2E + "OfflineReauditE2ETests.cs", "ReAudit_Offline_ReproducesDatasetFindingsAndScores_WithZeroNetwork"),
        ]),
        new(16, "MCP handshake over SSE + stdio; invalid tokens rejected; loopback-only; rotation invalidates",
        [
            new(Boundary + "Mcp/McpBoundaryTests.cs", "McpBoundaryTests"),
            new(E2E + "McpDelegationE2ETests.cs", "InvalidToken_IsRefusedWith401"),
            new(E2E + "McpDelegationE2ETests.cs", "TokenRotation_InvalidatesPriorClientsImmediately"),
            new(E2E + "McpDelegationE2ETests.cs", "StdioTransportHeader_IsRecordedInTheActionLog"),
            new(NetAudit + "mcp-boundary.md"),
        ]),
        new(17, "Credentials encrypted at rest; never derived from hardware/OS identity",
        [
            new(Boundary + "Secrets/EncryptionAtRestTests.cs", "EncryptionAtRestTests"),
            new(NetAudit + "encryption-at-rest.md"),
            new(NetAudit + "secret-protector-properties.md"),
            new(Crawler + "Fetching/SignedRequestTests.cs", "SignedRequestTests"),
        ]),
        new(18, "External client-server DB: migration + identical report output; archive revert; connection test",
        [
            new(E2E + "MySqlParityE2ETests.cs", "FullAudit_OnMySql_ProducesIdenticalReportOutput_AsSqlite"),
            new(E2E + "MySqlParityE2ETests.cs", "ArchiveSwitch_ToMySql_AndExplicitRevert_ToEmbedded"),
            new(E2E + "MySqlParityE2ETests.cs", "ConnectionTest_ReportsLiveVerdicts_AndRecordsServerVersion"),
        ]),
        new(19, "Mandated server GC profile verifiable through version/diagnostics",
        [
            new(Host + "HostScaffoldTests.cs", "Scaffold_Diagnostics_ReportsActiveServerGcProfile"),
            new(Scale + "results/diagnostics-gc.json"),
            new(Scale + "benchmark-report.md", null, "Server GC"),
        ]),
        new(20, "Metrics exposition on loopback only; no identity dimensions; nothing pushed",
        [
            new(Boundary + "Diagnostics/MetricsBoundaryTests.cs", "MetricsBoundaryTests"),
            new(NetAudit + "metrics-boundary.md"),
            new(Scale + "results/metrics-parity.json"),
        ]),
        new(21, "Abnormal termination produces local minidump; artifacts viewable/purgeable; never transmitted",
        [
            new(Boundary + "Diagnostics/MinidumpLocalityTests.cs", "MinidumpLocalityTests"),
            new(NetAudit + "minidump-locality.md"),
        ]),
        new(22, "Proxy routing honored; intercepting proxy captures rendered traffic; no non-loopback listener",
        [
            new(Crawler + "Rendering/Proxy/ProxyRoutingUnitTests.cs", "ProxyRoutingUnitTests"),
            new(Crawler + "Rendering/Proxy/ProxyForwardTests.cs", "ProxyForwardTests"),
            new(Boundary + "Listeners/LoopbackListenerSweepTests.cs", "LoopbackListenerSweepTests"),
            new(NetAudit + "listener-sweep.md"),
        ]),
        new(23, "Local agent delegated flow via MCP only; every invocation in the local action log",
        [
            new(E2E + "McpDelegationE2ETests.cs", "DelegatedFlow_StartAndReAuditViaMcp_LogsEveryInvocation"),
            new(E2E + "McpDelegationE2ETests.cs", "ToolLevelFailures_AreLoggedAsFailed"),
        ]),
        new(24, "Custom extraction: typed values, scoping, caps, dynamic columns, offline re-extraction, failure isolation",
        [
            new(E2E + "OfflineReauditE2ETests.cs", "LiveAudit_CustomExtractionRules_PersistExtractedContent"),
            new(E2E + "OfflineReauditE2ETests.cs", "ReAudit_Offline_CustomExtractionSearchAndJs_RerunFromStoredBodies"),
            new(Audit + "Custom/CustomContentExtractorTests.cs", "CustomContentExtractorTests"),
        ]),
        new(25, "Accessibility violations with locators/remediation; aggregates; scoring; render-off flags",
        [
            new(Audit + "Accessibility/AccessibilityAnalyzerTests.cs", "AccessibilityAnalyzerTests"),
            new(Audit + "Accessibility/AccessibilityRuleTests.cs", "AccessibilityRuleTests"),
        ]),
        new(26, "Lab CWV per page; budget exceedance findings; coverage/composition; offline against target only",
        [
            new(Audit + "Accessibility/PerformanceAnalyzerTests.cs", "PerformanceAnalyzerTests"),
            new(Scale + "results/cwv-capture.json"),
            new(Scale + "benchmark-report.md", null, "Core Web Vitals"),
        ]),
        new(27, "Structured data JSON-LD + Microdata validation; rich-result eligibility; error taxonomy",
        [
            new(Audit + "StructuredData/StructuredDataValidatorTests.cs", "MissingContext_ProducesTaxonomyError"),
            new("tests/CrawlForge.Acceptance.Tests/TargetedRemainderTests.cs", "StructuredData_InvalidJsonLd_EmitsTaxonomyFinding"),
        ]),
        new(28, "Readability/reading-time/text-to-HTML from visible text; soft-404 distinct from hard errors",
        [
            new(Audit + "ContentQuality/ReadabilityCalculatorTests.cs", "ReadabilityCalculatorTests"),
            new(Audit + "Indexability/Soft404DetectorTests.cs", "Soft404DetectorTests"),
            new(Audit + "Indexability/IndexabilityAnalyzerTests.cs", "Soft404Page_EmitsDistinctWarning_WhenDetectorInjected"),
            new("tests/CrawlForge.Acceptance.Tests/TargetedRemainderTests.cs", "Soft404_SuccessStatusErrorContent_FlaggedDistinctly"),
        ]),
        new(29, "Technology detection only from bundled catalogue; per-URL confidence; no remote fetch",
        [
            new(Audit + "StructuredData/TechnologyDetectorTests.cs", "CatalogueLoads_WithVersionAndAllCategories"),
            new(Audit + "StructuredData/TechnologyDetectorTests.cs", "MissingAsset_ThrowsFileNotFound"),
            new("assets/tech-fingerprints"),
            new("tests/CrawlForge.Acceptance.Tests/TargetedRemainderTests.cs", "TechnologyCatalogue_IsLocal_AndDetectsFromBundledFingerprints"),
        ]),
        new(30, "Custom search items: positive/negative terms, scoping, counts/snippets, offline re-run",
        [
            new(E2E + "OfflineReauditE2ETests.cs", "ReAudit_Offline_CustomExtractionSearchAndJs_RerunFromStoredBodies"),
            new(Audit + "Custom/CustomContentSearchTests.cs", "CustomContentSearchTests"),
        ]),
        new(31, "Custom JS snippets: timeout, isolated wrapper, typed dynamic columns, per-page failure isolation",
        [
            new(Audit + "Custom/JintSnippetRunnerTests.cs", "InfiniteLoop_TimesOut_Isolated"),
            new(Audit + "Custom/JintSnippetRunnerTests.cs", "ThrownSnippetError_Isolated_OtherSnippetsRun"),
            new(E2E + "OfflineReauditE2ETests.cs", "ReAudit_Offline_CustomExtractionSearchAndJs_RerunFromStoredBodies"),
        ]),
        new(32, "Scores per category/overall; suppressions excluded, reversible, comparable across audits",
        [
            new(Audit + "Scoring/AuditScoringServiceTests.cs", "Scores_AreComparableAcrossAudits"),
            new(Audit + "Scoring/FindingSuppressionServiceTests.cs", "Suppress_TriggersOfflineRecompute_FlagsAndScores"),
            new(Audit + "Pipeline/FindingEmitterTests.cs", "SuppressionsAreProjectScoped"),
            new("tests/CrawlForge.Acceptance.Tests/TargetedRemainderTests.cs", "Scoring_ComparableAcrossAudits_AndSuppressionRecomputes"),
        ]),
        new(33, "Standalone tools without a full audit: robots tester, sitemap generator, nested query builder",
        [
            new(Api + "Controllers/ToolsRouteTests.cs", "ToolsRouteTests"),
            new(Frontend + "tools/queryBuilder.test.tsx"),
            new("tests/CrawlForge.Acceptance.Tests/TargetedRemainderTests.cs", "StandaloneTools_OperateWithoutAFullAudit"),
        ]),
        new(34, "Signed requests: in-scope-only headers, expiry tracking, encrypted material; sample-crawl behaviors",
        [
            new(Crawler + "Fetching/SignedRequestTests.cs", "Sign_OutOfScopeRequest_ReturnedUnchanged"),
            new(Crawler + "Fetching/SignedRequestTests.cs", "Sign_InScopeRequest_Applied"),
            new("tests/CrawlForge.Acceptance.Tests/TargetedRemainderTests.cs", "SignedRequest_AppliesOnlyInScope"),
        ]),
        new(35, "WAF 200-OK challenge blocked with provider attribution; corroboration guard; offline re-detection",
        [
            new(E2E + "WafDetectionE2ETests.cs", "ChallengePage200Ok_IsBlocked_WithProviderAttribution"),
            new(E2E + "WafDetectionE2ETests.cs", "SingleFacetHit_CorroborationGuard_LeavesPageUnblocked"),
            new(E2E + "OfflineReauditE2ETests.cs", "ReAudit_Offline_WafDetectionsCopiedAndBlockMetricsRederived"),
            new("assets/waf-signatures"),
        ]),
        new(36, "AIMD adaptation: additive increase to ceiling, multiplicative decrease on pressure, hub events, state preserved",
        [
            new(E2E + "AimdAdaptationE2ETests.cs", "AimdAdaptationE2ETests"),
            new(E2E + "AimdHostThrottleE2ETests.cs", "LiveAudit_429Pressure_PersistsHistory_EmitsHubEvents_WritesMetricSummary"),
        ]),
        new(37, "Boilerplate scoping: scoped text drives metrics; invalid selectors rejected; offline re-derivation; baseline parity",
        [
            new(Audit + "Pipeline/ScopeResolverTests.cs", "ScopeResolverTests"),
            new(Audit + "Pipeline/ContentScopeValidatorTests.cs", "ContentScopeValidatorTests"),
            new(E2E + "OfflineReauditE2ETests.cs", "ReAudit_Offline_ScopedWordCountMetricRederived"),
        ]),
        new(38, "N-gram cannibalization: cluster findings, per-zone TF/site DF, REST + MCP surfaces, stop-words, bounded discard",
        [
            new(Intelligence + "NGrams/NGramAnalyzerTests.cs", "NGramAnalyzerTests"),
            new(Intelligence + "NGrams/NGramRuleAnalyzerTests.cs", "NGramRuleAnalyzerTests"),
            new(Intelligence + "NGrams/NGramTokenizerTests.cs", "Ngrams_Do_Not_Cross_Sentence_Boundaries"),
            new("tests/CrawlForge.Acceptance.Tests/TargetedRemainderTests.cs", "Ngrams_OrdersZonesAndBounds_HonoredOnRestSurface"),
            new("assets/stopwords"),
        ]),
        new(39, "Local plugin lifecycle: hooks, coalesced updates, zero network without grant, failure isolation",
        [
            new(E2E + "PluginLifecycleE2ETests.cs", "FullLifecycle_Discover_Grant_Revoke_CommandsRejected_RescanIdempotent_VanishRemoves"),
            new(E2E + "PluginLifecycleE2ETests.cs", "Discovery_NetworkDeclarations_NeverBecomeActiveGrants"),
            new(Boundary + "Plugins/PluginNetworkBoundaryTests.cs", "PluginNetworkBoundaryTests"),
            new(NetAudit + "plugin-network-vitest.md"),
        ]),
        new(40, "60 FPS virtual scrolling at 500k rows across five explorers (ratified composite gate)",
        [
            new(Scale + "results/browser-scroll-urls-beginframe.json"),
            new(Scale + "results/browser-scroll-issues-beginframe.json"),
            new(Scale + "results/browser-scroll-links-beginframe.json"),
            new(Scale + "results/browser-scroll-extraction-beginframe.json"),
            new(Scale + "results/browser-scroll-ngrams-beginframe.json"),
            new(Scale + "results/browser-worker-memory.json"),
            new(Scale + "benchmark-report.md", null, "PRODUCT-OWNER AMENDMENT"),
            new(Frontend + "grid/bench/scroll.benchmark.test.ts"),
        ]),
        new(41, "Live memory telemetry: 2s heartbeat, status bar, Compact/Trim + WAL checkpoint, /metrics parity, local-only",
        [
            new(E2E + "HubSequencingE2ETests.cs", "SystemHealth_HeartbeatEvery2Seconds_OnlyWhileShellConnected"),
            new(Scale + "results/compact-trim.json"),
            new(Scale + "results/heartbeat-accuracy.json"),
            new(Scale + "results/metrics-parity.json"),
            new(Frontend + "shell/statusBar.test.tsx"),
        ]),
        new(42, "Point-and-click CSS selector generation with XPath alternative, match preview, send-to-rule/scope, offline",
        [
            new(Frontend + "tools/picker.test.tsx", "SelectorPicker"),
            new(Frontend + "tools/selectorEngine.test.ts", "captureSelector"),
        ]),
        new(43, "List Mode ingestion/policies; five-tier redirect matrix; deterministic rewrites; host mapping",
        [
            new(E2E + "ListModeE2ETests.cs", "Ingestion_ValidatesDedupesAndPersistsListSeeds"),
            new(E2E + "ListModeE2ETests.cs", "ListOnlyPolicy_ForcesDepthZeroAndDisablesDiscovery"),
            new(E2E + "ListModeE2ETests.cs", "ListBoundedPolicy_EnqueuesOnListAndSkipsOffListDiscoveries"),
            new(E2E + "ListModeE2ETests.cs", "RedirectMatrix_FiveTierVerdicts_PersistFindingsAndIdempotentAggregates"),
            new(E2E + "ListModeE2ETests.cs", "RedirectMatrix_Revalidation_DoesNotDuplicateFindings"),
        ]),
        new(44, "Offline log import/correlation: formats, gzip, bot CIDR, consent-gated DNS, orphans/budget-waste/uncrawled",
        [
            new(Intelligence + "LogAnalyzer/LogImportServiceTests.cs", "LogImportServiceTests"),
            new(Intelligence + "LogAnalyzer/LogCorrelationEngineTests.cs", "LogCorrelationEngineTests"),
            new(Boundary + "Consent/LogsRestSurfaceTests.cs", "LogsRestSurfaceTests"),
            new(NetAudit + "dns-consent.md"),
            new("assets/bot-cidr"),
        ]),
        new(45, "Local embeddings: bundled ONNX/loopback daemon, semantic cannibalization/link bands, failure isolation, no egress",
        [
            new(Intelligence + "Embeddings/BundledOnnxProviderTests.cs", "EmbeddingsAreDeterministic"),
            new(Boundary + "Consent/ConsentAndEmbeddingTests.cs", "ConsentAndEmbeddingTests"),
            new(NetAudit + "embedding-loopback.md"),
            new("assets/onnx-model"),
        ]),
        new(46, "llms.txt probing/validation; AI governance matrix across bundled agent profiles; local generator",
        [
            new(Intelligence + "Geo/LlmsTxtValidationTests.cs", "LlmsTxtValidationTests"),
            new(Intelligence + "Geo/LlmsTxtGeneratorTests.cs", "LlmsTxtGeneratorTests"),
            new(Intelligence + "Geo/GovernanceMatrixTests.cs", "GovernanceCoverage_RecordsPerPurposeAndOverall"),
            new("assets/ai-agent-profiles"),
        ]),
        new(47, "Analytics/GTM/GA4/Consent Mode v2 inventory + findings; explorer + MCP coverage resource",
        [
            new(Intelligence + "MarTech/MarTechAuditorTests.cs", "MarTechAuditorTests"),
            new(Intelligence + "MarTech/ConsentModeAnalyzerTests.cs", "ConsentModeAnalyzerTests"),
            new(Intelligence + "MarTech/DataLayerInspectorTests.cs", "DataLayerInspectorTests"),
            new(Api + "Controllers/AnalyticsGeoRouteTests.cs", "AnalyticsGeoRouteTests"),
        ]),
        new(48, "Zone-weighted equity, SERP pixel truncation, header/HTML equivalency conflicts with dual evidence",
        [
            new(Audit + "LinkGraph/WeightedLinkEquityCalculatorTests.cs", "WeightedEquityDiffersFromUnweightedUnderNonUniformZoneWeights"),
            new(Audit + "LinkGraph/WeightedLinkEquityCalculatorTests.cs", "InContentLinksCarryTheHighestDefaultWeight"),
            new(Audit + "LinkGraph/LinkZoneClassifierTests.cs", "ZonePrecedenceAppliesAcrossTheAncestorChain"),
            new(Audit + "ContentQuality/SerpPixelMeasurerTests.cs", "SerpPixelMeasurerTests"),
            new(Audit + "Indexability/HeaderHtmlEquivalencyAnalyzerTests.cs", "HeaderHtmlEquivalencyAnalyzerTests"),
            new("tests/CrawlForge.Acceptance.Tests/TargetedRemainderTests.cs", "HeaderHtmlConflict_EmitsErrorWithDualEvidence"),
            new("tests/CrawlForge.Acceptance.Tests/TargetedRemainderTests.cs", "SerpPixels_OverBudgetTitle_EmitsTruncation"),
            new("tests/CrawlForge.Acceptance.Tests/TargetedRemainderTests.cs", "ZoneWeightedEquity_LinkEdgesCarryZonesAndWeights"),
            new("assets/font-metrics"),
        ]),
    ];
}
