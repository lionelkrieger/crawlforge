/**
 * Route contract map: every {@link ApiRouteKey} bound to its path parameters,
 * request payload (query for GET/DELETE, body for POST/PUT), and response DTO.
 *
 * Compile-time assertions below guarantee the map and the frozen route
 * catalog cover exactly the same keys — a drift fails `tsc --noEmit`.
 *
 * @module contracts/api/contracts-map
 */

import type { EmptyRequest } from '../common/primitives';
import type { ListQuery } from '../common/pagination';
import type { ApiRouteKey } from './routes';

import type {
  AppSettings,
  DatabaseTestRequest,
  DatabaseTestResponse,
  DiagnosticArtifact,
  DiagnosticArtifactListQuery,
  DiagnosticPurgeRequest,
  DiagnosticPurgeResponse,
  HealthResponse,
  MetricsResponse,
  SettingsUpdateRequest,
  SystemCompactResponse,
  SystemHealthSnapshot,
  VersionResponse
} from './system';
import type {
  AuditConfiguration,
  ConfigurationCreateRequest,
  ConfigurationSummary,
  ConfigurationUpdateRequest,
  DeletionConfirmation,
  Project,
  ProjectCreateRequest,
  ProjectListQuery,
  ProjectSummary,
  ProjectUpdateRequest,
  SuppressionCreateRequest,
  SuppressionState
} from './projects';
import type {
  AuditCompareQuery,
  AuditCompareResponse,
  AuditDashboardSummary,
  AuditDetail,
  AuditListQuery,
  AuditScores,
  AuditStartRequest,
  AuditStartResponse,
  AuditStateResponse,
  AuditSummary,
  ChartSeriesQuery,
  ChartSeriesResponse,
  Finding,
  FindingListQuery,
  GraphData,
  GraphQuery,
  LinkEdge,
  LinkListQuery,
  ReauditRequest,
  ReauditResponse,
  UrlDetail,
  UrlListQuery,
  UrlRecordSummary
} from './audits';
import type {
  ContentCompareQuery,
  ContentCompareResponse,
  ExtractedContentQuery,
  ExtractedContentResponse,
  ExtractionSummaryResponse,
  SearchResultsQuery,
  SearchResultsResponse,
  SinglePageAnalysisRequest,
  SinglePageAnalysisResponse,
  SnippetResultsQuery,
  SnippetResultsResponse,
  TechnologyDetection,
  TechnologyListQuery
} from './content';
import type { ExportCreateRequest, ExportCreateResponse, ExportFileResponse } from './exports';
import type {
  Schedule,
  ScheduleCreateRequest,
  ScheduleRun,
  ScheduleRunListQuery,
  ScheduleRunNowResponse,
  ScheduleStateResponse,
  ScheduleUpdateRequest
} from './schedules';
import type {
  Destination,
  DestinationCreateRequest,
  DestinationListQuery,
  DestinationTestResponse,
  DestinationUpdateRequest
} from './destinations';
import type {
  DictionaryAddRequest,
  DictionaryEntry,
  DictionaryListQuery,
  DictionaryMutationResponse,
  DictionaryRemoveRequest
} from './dictionary';
import type {
  SavedQuery,
  SavedQueryCreateRequest,
  SavedQueryDeleteRequest,
  SavedQueryListQuery,
  SavedQueryUpdateRequest
} from './queries';
import type {
  LlmsGenerateRequest,
  LlmsGenerateResponse,
  LlmsValidateRequest,
  LlmsValidateResponse,
  RenderTestRequest,
  RenderTestResponse,
  RewritePreviewRequest,
  RewritePreviewResponse,
  RobotsTestRequest,
  RobotsTestResponse,
  ScopePreviewRequest,
  ScopePreviewResponse,
  SitemapGenerateRequest,
  SitemapGenerateResponse,
  StructuredDataCheckRequest,
  StructuredDataCheckResponse,
  UrlCheckRequest,
  UrlCheckResponse
} from './tools';
import type {
  WafBlock,
  WafBlockListQuery,
  WafBlocksSummary,
  WafSignature,
  WafSignatureCatalogue,
  WafSignatureCreateRequest,
  WafSignatureDeleteResponse,
  WafSignatureListQuery,
  WafSignatureUpdateRequest
} from './waf';
import type { HostListQuery, HostOverrideRequest, HostState } from './hosts';
import type {
  NgramAnalysisQuery,
  NgramCannibalizationCluster,
  NgramListQuery,
  NgramRow,
  TopicalGap
} from './ngrams';
import type { PluginRegistryEntry, PluginRescanResponse, PluginUpdateRequest } from './plugins';
import type {
  RedirectMatrixResult,
  RedirectMatrixResultsQuery,
  RedirectMatrixUploadRequest,
  RedirectMatrixUploadResponse
} from './redirect-matrix';
import type {
  DnsVerifyRequest,
  DnsVerifyResponse,
  LogCorrelationQuery,
  LogCorrelationResponse,
  LogImport,
  LogImportListQuery,
  LogImportRequest,
  LogImportStartResponse
} from './logs';
import type {
  EmbeddingTestRequest,
  EmbeddingTestResponse,
  LinkOpportunity,
  LinkOpportunityQuery,
  SemanticCluster,
  SemanticClusterQuery,
  SemanticsRunRequest,
  SemanticsRunResponse,
  SemanticsSimilarQuery,
  SimilarUrl
} from './semantics';
import type { BotGovernanceQuery, BotGovernanceRow, LlmsTxtListQuery, LlmsTxtValidation } from './geo';
import type { AnalyticsCoverageQuery, AnalyticsQuery, AnalyticsResponse, ContainerCoverage } from './analytics';

/**
 * Uniform per-route contract: path parameters, request payload, response DTO.
 * `TParams` is an empty record for routes without path parameters.
 */
export interface RouteContract<
  TRequest,
  TResponse,
  TParams extends Record<string, string> = Record<string, never>
> {
  readonly request: TRequest;
  readonly response: TResponse;
  readonly params: TParams;
}

/** Path parameters for routes addressing a single entity by id. */
export type IdParams = { readonly id: string };

/** Complete route-key -> DTO contract mapping (108 operations / 88 spec rows). */
export interface ApiRouteContracts {
  // Baseline — system
  'health.get': RouteContract<EmptyRequest, HealthResponse>;
  'version.get': RouteContract<EmptyRequest, VersionResponse>;
  'metrics.get': RouteContract<EmptyRequest, MetricsResponse>;
  'settings.get': RouteContract<EmptyRequest, AppSettings>;
  'settings.update': RouteContract<SettingsUpdateRequest, AppSettings>;
  'settings.databaseTest': RouteContract<DatabaseTestRequest, DatabaseTestResponse>;
  'diagnostics.listArtifacts': RouteContract<DiagnosticArtifactListQuery, readonly DiagnosticArtifact[]>;
  'diagnostics.purgeArtifacts': RouteContract<DiagnosticPurgeRequest, DiagnosticPurgeResponse>;
  // Baseline — projects & configurations
  'projects.list': RouteContract<ProjectListQuery, readonly ProjectSummary[]>;
  'projects.create': RouteContract<ProjectCreateRequest, Project>;
  'projects.get': RouteContract<EmptyRequest, Project, IdParams>;
  'projects.update': RouteContract<ProjectUpdateRequest, Project, IdParams>;
  'projects.delete': RouteContract<EmptyRequest, DeletionConfirmation, IdParams>;
  'configurations.list': RouteContract<ListQuery, readonly ConfigurationSummary[], IdParams>;
  'configurations.create': RouteContract<ConfigurationCreateRequest, AuditConfiguration, IdParams>;
  'configurations.get': RouteContract<EmptyRequest, AuditConfiguration, IdParams>;
  'configurations.update': RouteContract<ConfigurationUpdateRequest, AuditConfiguration, IdParams>;
  'configurations.delete': RouteContract<EmptyRequest, DeletionConfirmation, IdParams>;
  // Baseline — audits
  'audits.start': RouteContract<AuditStartRequest, AuditStartResponse>;
  'audits.list': RouteContract<AuditListQuery, readonly AuditSummary[]>;
  'audits.get': RouteContract<EmptyRequest, AuditDetail, IdParams>;
  'audits.pause': RouteContract<EmptyRequest, AuditStateResponse, IdParams>;
  'audits.resume': RouteContract<EmptyRequest, AuditStateResponse, IdParams>;
  'audits.cancel': RouteContract<EmptyRequest, AuditStateResponse, IdParams>;
  'audits.reaudit': RouteContract<ReauditRequest, ReauditResponse, IdParams>;
  'audits.listUrls': RouteContract<UrlListQuery, readonly UrlRecordSummary[], IdParams>;
  'audits.getUrl': RouteContract<EmptyRequest, UrlDetail, IdParams & { readonly urlId: string }>;
  'audits.listLinks': RouteContract<LinkListQuery, readonly LinkEdge[], IdParams>;
  'audits.listFindings': RouteContract<FindingListQuery, readonly Finding[], IdParams>;
  'audits.summary': RouteContract<EmptyRequest, AuditDashboardSummary, IdParams>;
  'audits.chartSeries': RouteContract<ChartSeriesQuery, ChartSeriesResponse, IdParams & { readonly metric: string }>;
  'audits.graph': RouteContract<GraphQuery, GraphData, IdParams>;
  'audits.compare': RouteContract<AuditCompareQuery, AuditCompareResponse>;
  'analysis.singlePage': RouteContract<SinglePageAnalysisRequest, SinglePageAnalysisResponse>;
  // Baseline — exports
  'exports.create': RouteContract<ExportCreateRequest, ExportCreateResponse>;
  'exports.download': RouteContract<EmptyRequest, ExportFileResponse, IdParams>;
  // Baseline — schedules
  'schedules.list': RouteContract<ListQuery, readonly Schedule[], IdParams>;
  'schedules.create': RouteContract<ScheduleCreateRequest, Schedule, IdParams>;
  'schedules.get': RouteContract<EmptyRequest, Schedule, IdParams>;
  'schedules.update': RouteContract<ScheduleUpdateRequest, Schedule, IdParams>;
  'schedules.delete': RouteContract<EmptyRequest, DeletionConfirmation, IdParams>;
  'schedules.enable': RouteContract<EmptyRequest, ScheduleStateResponse, IdParams>;
  'schedules.disable': RouteContract<EmptyRequest, ScheduleStateResponse, IdParams>;
  'schedules.runNow': RouteContract<EmptyRequest, ScheduleRunNowResponse, IdParams>;
  'schedules.listRuns': RouteContract<ScheduleRunListQuery, readonly ScheduleRun[], IdParams>;
  // Baseline — destinations
  'destinations.list': RouteContract<DestinationListQuery, readonly Destination[]>;
  'destinations.create': RouteContract<DestinationCreateRequest, Destination>;
  'destinations.update': RouteContract<DestinationUpdateRequest, Destination, IdParams>;
  'destinations.delete': RouteContract<EmptyRequest, DeletionConfirmation, IdParams>;
  'destinations.test': RouteContract<EmptyRequest, DestinationTestResponse, IdParams>;
  // Baseline — dictionary
  'dictionary.list': RouteContract<DictionaryListQuery, readonly DictionaryEntry[]>;
  'dictionary.add': RouteContract<DictionaryAddRequest, DictionaryMutationResponse>;
  'dictionary.remove': RouteContract<DictionaryRemoveRequest, DictionaryMutationResponse>;
  // Baseline — content & analysis
  'audits.compareContent': RouteContract<ContentCompareQuery, ContentCompareResponse>;
  'audits.listExtractedContent': RouteContract<ExtractedContentQuery, ExtractedContentResponse, IdParams>;
  'audits.extractionSummary': RouteContract<EmptyRequest, ExtractionSummaryResponse, IdParams>;
  'audits.listSearchResults': RouteContract<SearchResultsQuery, SearchResultsResponse, IdParams>;
  'audits.listSnippetResults': RouteContract<SnippetResultsQuery, SnippetResultsResponse, IdParams>;
  'audits.listTechnologies': RouteContract<TechnologyListQuery, readonly TechnologyDetection[], IdParams>;
  'audits.scores': RouteContract<EmptyRequest, AuditScores, IdParams>;
  // Baseline — suppressions
  'suppressions.create': RouteContract<SuppressionCreateRequest, SuppressionState, IdParams>;
  'suppressions.delete': RouteContract<EmptyRequest, SuppressionState, IdParams & { readonly key: string }>;
  // Baseline — standalone tools
  'tools.robotsTest': RouteContract<RobotsTestRequest, RobotsTestResponse>;
  'tools.urlCheck': RouteContract<UrlCheckRequest, UrlCheckResponse>;
  'tools.structuredDataCheck': RouteContract<StructuredDataCheckRequest, StructuredDataCheckResponse>;
  'tools.renderTest': RouteContract<RenderTestRequest, RenderTestResponse>;
  'tools.sitemapGenerate': RouteContract<SitemapGenerateRequest, SitemapGenerateResponse>;
  // Baseline — saved queries
  'queries.list': RouteContract<SavedQueryListQuery, readonly SavedQuery[]>;
  'queries.create': RouteContract<SavedQueryCreateRequest, SavedQuery>;
  'queries.update': RouteContract<SavedQueryUpdateRequest, SavedQuery>;
  'queries.delete': RouteContract<SavedQueryDeleteRequest, DeletionConfirmation>;

  // Additive — WAF (FS-CRAWL-002 §2.7)
  'waf.listBlocks': RouteContract<WafBlockListQuery, readonly WafBlock[], IdParams>;
  'waf.blocksSummary': RouteContract<EmptyRequest, WafBlocksSummary, IdParams>;
  'waf.listSignatures': RouteContract<WafSignatureListQuery, WafSignatureCatalogue>;
  'waf.createSignature': RouteContract<WafSignatureCreateRequest, WafSignature>;
  'waf.updateSignature': RouteContract<WafSignatureUpdateRequest, WafSignature, { readonly key: string }>;
  'waf.deleteSignature': RouteContract<EmptyRequest, WafSignatureDeleteResponse, { readonly key: string }>;
  // Additive — host AIMD (§3.7)
  'hosts.list': RouteContract<HostListQuery, readonly HostState[], IdParams>;
  'hosts.override': RouteContract<HostOverrideRequest, HostState, IdParams & { readonly host: string }>;
  // Additive — n-grams (§5.7)
  'ngrams.list': RouteContract<NgramListQuery, readonly NgramRow[], IdParams>;
  'ngrams.cannibalization': RouteContract<NgramAnalysisQuery, readonly NgramCannibalizationCluster[], IdParams>;
  'ngrams.gaps': RouteContract<NgramAnalysisQuery, readonly TopicalGap[], IdParams>;
  // Additive — scope preview (§4.6)
  'tools.scopePreview': RouteContract<ScopePreviewRequest, ScopePreviewResponse>;
  // Additive — plugins (§6.7)
  'plugins.list': RouteContract<ListQuery, readonly PluginRegistryEntry[]>;
  'plugins.update': RouteContract<PluginUpdateRequest, PluginRegistryEntry, IdParams>;
  'plugins.rescan': RouteContract<EmptyRequest, PluginRescanResponse>;
  'plugins.delete': RouteContract<EmptyRequest, DeletionConfirmation, IdParams>;
  // Additive — system health (§7.2)
  'system.health': RouteContract<EmptyRequest, SystemHealthSnapshot>;
  'system.compact': RouteContract<EmptyRequest, SystemCompactResponse>;
  // Additive — redirect matrix (§10.7)
  'matrix.upload': RouteContract<RedirectMatrixUploadRequest, RedirectMatrixUploadResponse, IdParams>;
  'matrix.results': RouteContract<RedirectMatrixResultsQuery, readonly RedirectMatrixResult[], IdParams>;
  'tools.rewritePreview': RouteContract<RewritePreviewRequest, RewritePreviewResponse>;
  // Additive — log analyzer (§11.7)
  'logs.import': RouteContract<LogImportRequest, LogImportStartResponse, IdParams>;
  'logs.list': RouteContract<LogImportListQuery, readonly LogImport[], IdParams>;
  'logs.delete': RouteContract<EmptyRequest, DeletionConfirmation, IdParams>;
  'logs.correlation': RouteContract<LogCorrelationQuery, LogCorrelationResponse, IdParams>;
  'logs.verifyDns': RouteContract<DnsVerifyRequest, DnsVerifyResponse, IdParams>;
  // Additive — semantics (§12.8)
  'semantics.run': RouteContract<SemanticsRunRequest, SemanticsRunResponse, IdParams>;
  'semantics.similar': RouteContract<SemanticsSimilarQuery, readonly SimilarUrl[], IdParams>;
  'semantics.linkOpportunities': RouteContract<LinkOpportunityQuery, readonly LinkOpportunity[], IdParams>;
  'semantics.clusters': RouteContract<SemanticClusterQuery, readonly SemanticCluster[], IdParams>;
  'settings.embeddingTest': RouteContract<EmbeddingTestRequest, EmbeddingTestResponse>;
  // Additive — GEO (§13.5)
  'geo.llmsTxt': RouteContract<LlmsTxtListQuery, readonly LlmsTxtValidation[], IdParams>;
  'geo.governance': RouteContract<BotGovernanceQuery, readonly BotGovernanceRow[], IdParams>;
  'tools.llmsGenerate': RouteContract<LlmsGenerateRequest, LlmsGenerateResponse>;
  'tools.llmsValidate': RouteContract<LlmsValidateRequest, LlmsValidateResponse>;
  // Additive — MarTech analytics (§14.5)
  'analytics.list': RouteContract<AnalyticsQuery, AnalyticsResponse, IdParams>;
  'analytics.coverage': RouteContract<AnalyticsCoverageQuery, readonly ContainerCoverage[], IdParams>;
}

/** Request payload type of a route (query for GET/DELETE, body for POST/PUT). */
export type ApiRouteRequest<K extends ApiRouteKey> = ApiRouteContracts[K]['request'];

/** Response DTO type of a route (inside the envelope when enveloped). */
export type ApiRouteResponse<K extends ApiRouteKey> = ApiRouteContracts[K]['response'];

/** Path parameter record of a route. */
export type ApiRouteParams<K extends ApiRouteKey> = ApiRouteContracts[K]['params'];

/**
 * Compile-time lock: every catalog key has a contract entry.
 * Resolves to `true` only when coverage is complete.
 */
export type AssertAllRoutesCovered = [Exclude<ApiRouteKey, keyof ApiRouteContracts>] extends [never]
  ? true
  : never;

/**
 * Compile-time lock: every contract entry corresponds to a catalog key
 * (no orphan contracts). Resolves to `true` only when exact.
 */
export type AssertNoOrphanContracts = [Exclude<keyof ApiRouteContracts, ApiRouteKey>] extends [never]
  ? true
  : never;
