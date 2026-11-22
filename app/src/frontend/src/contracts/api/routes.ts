/**
 * Frozen loopback REST route catalog — the contract lock for all 88 route
 * groups of the REST contract (51 baseline rows of FS-CRAWL-001 §10.2 +
 * 37 additive rows of FS-CRAWL-002 Appendix B). Multi-method spec rows
 * expand to one entry per method+path operation (109 operations total).
 *
 * This catalog is declarative contract metadata (no behavior): it locks the
 * exact method/path surface and feeds the typed client contract map.
 * Runtime code in the contracts package is limited to type guards.
 *
 * @module contracts/api/routes
 */

import type { HttpMethod } from '../common/primitives';

/** One method+path operation of the REST contract. */
export interface ApiRouteDefinition {
  readonly method: HttpMethod;
  /** Path template; `{name}` segments are path parameters. */
  readonly path: string;
  /** Spec row identifier (88 unique groups: 51 baseline + 37 additive). */
  readonly group: string;
  /** True for FS-CRAWL-002 Appendix B additive routes. */
  readonly additive: boolean;
  /** False for the two non-JSON routes: /metrics text and export file stream. */
  readonly enveloped: boolean;
}

/**
 * The complete route surface. Keys are stable route identifiers consumed by
 * the typed client (`ApiRouteContracts` maps each key to its DTO contract).
 */
export const API_ROUTES = {
  // ── Baseline: FS-CRAWL-001 §10.2 (51 spec rows, 71 operations) ──────────
  'health.get': { method: 'GET', path: '/api/health', group: 'health', additive: false, enveloped: true },
  'version.get': { method: 'GET', path: '/api/version', group: 'version', additive: false, enveloped: true },
  'metrics.get': { method: 'GET', path: '/metrics', group: 'metrics', additive: false, enveloped: false },
  'settings.get': { method: 'GET', path: '/api/settings', group: 'settings-read', additive: false, enveloped: true },
  'settings.update': { method: 'PUT', path: '/api/settings', group: 'settings-update', additive: false, enveloped: true },
  'settings.databaseTest': { method: 'POST', path: '/api/settings/database/test', group: 'database-test', additive: false, enveloped: true },
  'diagnostics.listArtifacts': { method: 'GET', path: '/api/diagnostics/artifacts', group: 'diagnostic-artifacts', additive: false, enveloped: true },
  'diagnostics.purgeArtifacts': { method: 'DELETE', path: '/api/diagnostics/artifacts', group: 'diagnostic-artifacts', additive: false, enveloped: true },
  'projects.list': { method: 'GET', path: '/api/projects', group: 'projects-list', additive: false, enveloped: true },
  'projects.create': { method: 'POST', path: '/api/projects', group: 'projects-create', additive: false, enveloped: true },
  'projects.get': { method: 'GET', path: '/api/projects/{id}', group: 'projects-item', additive: false, enveloped: true },
  'projects.update': { method: 'PUT', path: '/api/projects/{id}', group: 'projects-item', additive: false, enveloped: true },
  'projects.delete': { method: 'DELETE', path: '/api/projects/{id}', group: 'projects-item', additive: false, enveloped: true },
  'configurations.list': { method: 'GET', path: '/api/projects/{id}/configurations', group: 'configurations-project', additive: false, enveloped: true },
  'configurations.create': { method: 'POST', path: '/api/projects/{id}/configurations', group: 'configurations-project', additive: false, enveloped: true },
  'configurations.get': { method: 'GET', path: '/api/configurations/{id}', group: 'configurations-item', additive: false, enveloped: true },
  'configurations.update': { method: 'PUT', path: '/api/configurations/{id}', group: 'configurations-item', additive: false, enveloped: true },
  'configurations.delete': { method: 'DELETE', path: '/api/configurations/{id}', group: 'configurations-item', additive: false, enveloped: true },
  'audits.start': { method: 'POST', path: '/api/audits', group: 'audits-start', additive: false, enveloped: true },
  'audits.list': { method: 'GET', path: '/api/audits', group: 'audits-list', additive: false, enveloped: true },
  'audits.get': { method: 'GET', path: '/api/audits/{id}', group: 'audits-item', additive: false, enveloped: true },
  'audits.pause': { method: 'POST', path: '/api/audits/{id}/pause', group: 'audits-lifecycle', additive: false, enveloped: true },
  'audits.resume': { method: 'POST', path: '/api/audits/{id}/resume', group: 'audits-lifecycle', additive: false, enveloped: true },
  'audits.cancel': { method: 'POST', path: '/api/audits/{id}/cancel', group: 'audits-lifecycle', additive: false, enveloped: true },
  'audits.reaudit': { method: 'POST', path: '/api/audits/{id}/reaudit', group: 'audits-reaudit', additive: false, enveloped: true },
  'audits.listUrls': { method: 'GET', path: '/api/audits/{id}/urls', group: 'audits-urls', additive: false, enveloped: true },
  'audits.getUrl': { method: 'GET', path: '/api/audits/{id}/urls/{urlId}', group: 'audits-url-detail', additive: false, enveloped: true },
  'audits.listLinks': { method: 'GET', path: '/api/audits/{id}/links', group: 'audits-links', additive: false, enveloped: true },
  'audits.listFindings': { method: 'GET', path: '/api/audits/{id}/findings', group: 'audits-findings', additive: false, enveloped: true },
  'audits.summary': { method: 'GET', path: '/api/audits/{id}/summary', group: 'audits-summary', additive: false, enveloped: true },
  'audits.chartSeries': { method: 'GET', path: '/api/audits/{id}/charts/{metric}', group: 'audits-charts', additive: false, enveloped: true },
  'audits.graph': { method: 'GET', path: '/api/audits/{id}/graph', group: 'audits-graph', additive: false, enveloped: true },
  'audits.compare': { method: 'GET', path: '/api/audits/compare', group: 'audits-compare', additive: false, enveloped: true },
  'analysis.singlePage': { method: 'POST', path: '/api/analysis/single-page', group: 'single-page-analysis', additive: false, enveloped: true },
  'exports.create': { method: 'POST', path: '/api/exports', group: 'exports-create', additive: false, enveloped: true },
  'exports.download': { method: 'GET', path: '/api/exports/{id}/file', group: 'exports-file', additive: false, enveloped: false },
  'schedules.list': { method: 'GET', path: '/api/projects/{id}/schedules', group: 'schedules-project', additive: false, enveloped: true },
  'schedules.create': { method: 'POST', path: '/api/projects/{id}/schedules', group: 'schedules-project', additive: false, enveloped: true },
  'schedules.get': { method: 'GET', path: '/api/schedules/{id}', group: 'schedules-item', additive: false, enveloped: true },
  'schedules.update': { method: 'PUT', path: '/api/schedules/{id}', group: 'schedules-item', additive: false, enveloped: true },
  'schedules.delete': { method: 'DELETE', path: '/api/schedules/{id}', group: 'schedules-item', additive: false, enveloped: true },
  'schedules.enable': { method: 'POST', path: '/api/schedules/{id}/enable', group: 'schedules-toggle', additive: false, enveloped: true },
  'schedules.disable': { method: 'POST', path: '/api/schedules/{id}/disable', group: 'schedules-toggle', additive: false, enveloped: true },
  'schedules.runNow': { method: 'POST', path: '/api/schedules/{id}/run-now', group: 'schedules-run-now', additive: false, enveloped: true },
  'schedules.listRuns': { method: 'GET', path: '/api/schedules/{id}/runs', group: 'schedules-runs', additive: false, enveloped: true },
  'destinations.list': { method: 'GET', path: '/api/destinations', group: 'destinations-list-create', additive: false, enveloped: true },
  'destinations.create': { method: 'POST', path: '/api/destinations', group: 'destinations-list-create', additive: false, enveloped: true },
  'destinations.update': { method: 'PUT', path: '/api/destinations/{id}', group: 'destinations-item', additive: false, enveloped: true },
  'destinations.delete': { method: 'DELETE', path: '/api/destinations/{id}', group: 'destinations-item', additive: false, enveloped: true },
  'destinations.test': { method: 'POST', path: '/api/destinations/{id}/test', group: 'destinations-test', additive: false, enveloped: true },
  'dictionary.list': { method: 'GET', path: '/api/dictionary', group: 'dictionary', additive: false, enveloped: true },
  'dictionary.add': { method: 'POST', path: '/api/dictionary', group: 'dictionary', additive: false, enveloped: true },
  'dictionary.remove': { method: 'DELETE', path: '/api/dictionary', group: 'dictionary', additive: false, enveloped: true },
  'audits.compareContent': { method: 'GET', path: '/api/audits/compare/content', group: 'audits-compare-content', additive: false, enveloped: true },
  'audits.listExtractedContent': { method: 'GET', path: '/api/audits/{id}/extracted-content', group: 'audits-extracted-content', additive: false, enveloped: true },
  'audits.extractionSummary': { method: 'GET', path: '/api/audits/{id}/extracted-content/summary', group: 'audits-extraction-summary', additive: false, enveloped: true },
  'audits.listSearchResults': { method: 'GET', path: '/api/audits/{id}/search-results', group: 'audits-search-results', additive: false, enveloped: true },
  'audits.listSnippetResults': { method: 'GET', path: '/api/audits/{id}/snippet-results', group: 'audits-snippet-results', additive: false, enveloped: true },
  'audits.listTechnologies': { method: 'GET', path: '/api/audits/{id}/technologies', group: 'audits-technologies', additive: false, enveloped: true },
  'audits.scores': { method: 'GET', path: '/api/audits/{id}/scores', group: 'audits-scores', additive: false, enveloped: true },
  'suppressions.create': { method: 'POST', path: '/api/projects/{id}/suppressions', group: 'suppressions', additive: false, enveloped: true },
  'suppressions.delete': { method: 'DELETE', path: '/api/projects/{id}/suppressions/{key}', group: 'suppressions', additive: false, enveloped: true },
  'tools.robotsTest': { method: 'POST', path: '/api/tools/robots-test', group: 'tools-robots-test', additive: false, enveloped: true },
  'tools.urlCheck': { method: 'POST', path: '/api/tools/url-check', group: 'tools-url-check', additive: false, enveloped: true },
  'tools.structuredDataCheck': { method: 'POST', path: '/api/tools/structured-data-check', group: 'tools-structured-data-check', additive: false, enveloped: true },
  'tools.renderTest': { method: 'POST', path: '/api/tools/render-test', group: 'tools-render-test', additive: false, enveloped: true },
  'tools.sitemapGenerate': { method: 'POST', path: '/api/tools/sitemap-generate', group: 'tools-sitemap-generate', additive: false, enveloped: true },
  'queries.list': { method: 'GET', path: '/api/queries', group: 'queries', additive: false, enveloped: true },
  'queries.create': { method: 'POST', path: '/api/queries', group: 'queries', additive: false, enveloped: true },
  'queries.update': { method: 'PUT', path: '/api/queries', group: 'queries', additive: false, enveloped: true },
  'queries.delete': { method: 'DELETE', path: '/api/queries', group: 'queries', additive: false, enveloped: true },

  // ── Additive: FS-CRAWL-002 Appendix B (37 rows, 37 operations) ──────────
  'waf.listBlocks': { method: 'GET', path: '/api/audits/{id}/waf-blocks', group: 'waf-blocks', additive: true, enveloped: true },
  'waf.blocksSummary': { method: 'GET', path: '/api/audits/{id}/waf-blocks/summary', group: 'waf-blocks-summary', additive: true, enveloped: true },
  'waf.listSignatures': { method: 'GET', path: '/api/waf-signatures', group: 'waf-signatures-list', additive: true, enveloped: true },
  'waf.createSignature': { method: 'POST', path: '/api/waf-signatures', group: 'waf-signatures-create', additive: true, enveloped: true },
  'waf.updateSignature': { method: 'PUT', path: '/api/waf-signatures/{key}', group: 'waf-signatures-update', additive: true, enveloped: true },
  'waf.deleteSignature': { method: 'DELETE', path: '/api/waf-signatures/{key}', group: 'waf-signatures-delete', additive: true, enveloped: true },
  'hosts.list': { method: 'GET', path: '/api/audits/{id}/hosts', group: 'hosts', additive: true, enveloped: true },
  'hosts.override': { method: 'POST', path: '/api/audits/{id}/hosts/{host}/override', group: 'hosts-override', additive: true, enveloped: true },
  'ngrams.list': { method: 'GET', path: '/api/audits/{id}/ngrams', group: 'ngrams', additive: true, enveloped: true },
  'ngrams.cannibalization': { method: 'GET', path: '/api/audits/{id}/ngrams/cannibalization', group: 'ngrams-cannibalization', additive: true, enveloped: true },
  'ngrams.gaps': { method: 'GET', path: '/api/audits/{id}/ngrams/gaps', group: 'ngrams-gaps', additive: true, enveloped: true },
  'tools.scopePreview': { method: 'POST', path: '/api/tools/scope-preview', group: 'tools-scope-preview', additive: true, enveloped: true },
  'plugins.list': { method: 'GET', path: '/api/plugins', group: 'plugins-list', additive: true, enveloped: true },
  'plugins.update': { method: 'PUT', path: '/api/plugins/{id}', group: 'plugins-update', additive: true, enveloped: true },
  'plugins.rescan': { method: 'POST', path: '/api/plugins/rescan', group: 'plugins-rescan', additive: true, enveloped: true },
  'plugins.delete': { method: 'DELETE', path: '/api/plugins/{id}', group: 'plugins-delete', additive: true, enveloped: true },
  'system.health': { method: 'GET', path: '/api/system/health', group: 'system-health', additive: true, enveloped: true },
  'system.compact': { method: 'POST', path: '/api/system/compact', group: 'system-compact', additive: true, enveloped: true },
  'matrix.upload': { method: 'POST', path: '/api/audits/{id}/redirect-matrix', group: 'redirect-matrix-upload', additive: true, enveloped: true },
  'matrix.results': { method: 'GET', path: '/api/audits/{id}/redirect-matrix/results', group: 'redirect-matrix-results', additive: true, enveloped: true },
  'tools.rewritePreview': { method: 'POST', path: '/api/tools/rewrite-preview', group: 'tools-rewrite-preview', additive: true, enveloped: true },
  'logs.import': { method: 'POST', path: '/api/projects/{id}/logs/import', group: 'logs-import', additive: true, enveloped: true },
  'logs.list': { method: 'GET', path: '/api/projects/{id}/logs', group: 'logs-list', additive: true, enveloped: true },
  'logs.delete': { method: 'DELETE', path: '/api/logs/{id}', group: 'logs-delete', additive: true, enveloped: true },
  'logs.correlation': { method: 'GET', path: '/api/audits/{id}/logs/correlation', group: 'logs-correlation', additive: true, enveloped: true },
  'logs.verifyDns': { method: 'POST', path: '/api/logs/{id}/verify-dns', group: 'logs-verify-dns', additive: true, enveloped: true },
  'semantics.run': { method: 'POST', path: '/api/audits/{id}/semantics/run', group: 'semantics-run', additive: true, enveloped: true },
  'semantics.similar': { method: 'GET', path: '/api/audits/{id}/semantics/similar', group: 'semantics-similar', additive: true, enveloped: true },
  'semantics.linkOpportunities': { method: 'GET', path: '/api/audits/{id}/semantics/link-opportunities', group: 'semantics-link-opportunities', additive: true, enveloped: true },
  'semantics.clusters': { method: 'GET', path: '/api/audits/{id}/semantics/clusters', group: 'semantics-clusters', additive: true, enveloped: true },
  'settings.embeddingTest': { method: 'POST', path: '/api/settings/embedding/test', group: 'embedding-test', additive: true, enveloped: true },
  'settings.browser': { method: 'GET', path: '/api/settings/browser', group: 'settings-browser', additive: true, enveloped: true },
  'geo.llmsTxt': { method: 'GET', path: '/api/audits/{id}/geo/llms-txt', group: 'geo-llms-txt', additive: true, enveloped: true },
  'geo.governance': { method: 'GET', path: '/api/audits/{id}/geo/governance', group: 'geo-governance', additive: true, enveloped: true },
  'tools.llmsGenerate': { method: 'POST', path: '/api/tools/llms-generate', group: 'tools-llms-generate', additive: true, enveloped: true },
  'tools.llmsValidate': { method: 'POST', path: '/api/tools/llms-validate', group: 'tools-llms-validate', additive: true, enveloped: true },
  'analytics.list': { method: 'GET', path: '/api/audits/{id}/analytics', group: 'analytics', additive: true, enveloped: true },
  'analytics.coverage': { method: 'GET', path: '/api/audits/{id}/analytics/coverage', group: 'analytics-coverage', additive: true, enveloped: true }
} as const satisfies Record<string, ApiRouteDefinition>;

/** Stable identifier of one method+path operation. */
export type ApiRouteKey = keyof typeof API_ROUTES;

/** Spec row identifier (88 unique groups). */
export type ApiRouteGroup = (typeof API_ROUTES)[ApiRouteKey]['group'];

/** HTTP method of a route. */
export type ApiRouteMethod<K extends ApiRouteKey> = (typeof API_ROUTES)[K]['method'];

/** Path template of a route. */
export type ApiRoutePath<K extends ApiRouteKey> = (typeof API_ROUTES)[K]['path'];

/** True when the route returns the uniform JSON envelope. */
export type ApiRouteEnveloped<K extends ApiRouteKey> = (typeof API_ROUTES)[K]['enveloped'];

/** True when the route is an FS-CRAWL-002 additive route. */
export type ApiRouteAdditive<K extends ApiRouteKey> = (typeof API_ROUTES)[K]['additive'];

/** Route keys whose responses are not JSON-enveloped (metrics text, export file). */
export type RawRouteKey = {
  [K in ApiRouteKey]: ApiRouteEnveloped<K> extends false ? K : never;
}[ApiRouteKey];
