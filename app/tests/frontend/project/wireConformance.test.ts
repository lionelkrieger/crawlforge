// @vitest-environment jsdom
/**
 * Wire-conformance payloads for the project surfaces (WP-09-07).
 *
 * Real-producer → real-consumer: the fixtures below are the ACTUAL REST JSON
 * shapes (Api ProjectWireContracts.cs + ContractConformance seeds). These
 * tests feed them through the form binding and back, and export the payload
 * shapes the lead's ConfigEditorWireConformanceTests / ProjectOverviewWireTests
 * should assert against.
 */

import { describe, expect, it } from 'vitest';
import {
  ANALYSIS_CATEGORIES,
  CONFIGURATION_TABS,
  DEFAULT_AIMD,
  configurationFormErrors,
  configurationFromWire,
  configurationToCreateRequest,
  configurationToUpdateRequest,
  defaultConfigurationForm,
  presetFromUserAgent,
  toggleCategory,
  userAgentForPreset,
  validateAimd,
  validateLimit,
  type ConfigurationWireDto
} from '../../../src/frontend/src/surfaces/project/configurationModel';

/**
 * GET /api/configurations/{id} response `data` — AuditConfigurationWire
 * camelCase JSON exactly as the Api serializes it (System.Text.Json
 * camelCase policy; nulls preserved).
 */
const PRODUCER_CONFIGURATION_JSON = `{
  "id": "7",
  "projectId": "3",
  "name": "Full crawl",
  "description": "Fixture full crawl profile",
  "createdAt": "2026-11-14T09:00:00Z",
  "updatedAt": "2026-11-15T10:30:00Z",
  "startUrls": ["http://127.0.0.1:47311/"],
  "limits": { "maxUrls": 2, "maxDepth": 1, "maxDurationSeconds": 30, "maxResponseBytes": null },
  "aimd": { "initialRateR0": 2.0, "alpha": 0.5, "beta": 0.5, "kappa": 4, "ewmaTtfbWeight": 0.2 },
  "rendering": { "enabled": false, "deviceProfile": null, "infiniteScroll": false, "captureConsole": false },
  "respectRobotsTxt": true,
  "virtualRobotsTxt": null,
  "contentScope": null,
  "rewrite": null,
  "consent": null,
  "zoneWeights": null
}`;

describe('configuration editor ↔ REST DTO conformance', () => {
  it('binds the real producer JSON without loss', () => {
    const dto = JSON.parse(PRODUCER_CONFIGURATION_JSON) as ConfigurationWireDto;
    const form = configurationFromWire(dto);
    expect(form.name).toBe('Full crawl');
    expect(form.startUrls).toEqual(['http://127.0.0.1:47311/']);
    expect(form.limits).toEqual({ maxUrls: 2, maxDepth: 1, maxDurationSeconds: 30, maxResponseBytes: null });
    expect(form.aimd).toEqual(DEFAULT_AIMD);
    expect(form.respectRobotsTxt).toBe(true);
    // Absent additive scopeRules → exact engine baselines (CrawlScopeConfig.Default).
    expect(form.scopeRules.cookieJar.enabled).toBe(true);
    expect(form.scopeRules.directives.crawlNoindexPages).toBe(true);
    expect(form.scopeRules.directives.followPageNofollow).toBe(false);
    expect(form.scopeRules.canonicalization.queryPolicy).toBe('keepAll');
    expect(form.scopeRules.canonicalization.trailingSlash).toBe('preserve');
    expect(form.scopeRules.scope.mode).toBe('hostOnly');
    expect(form.scopeRules.scope.regexTimeoutMs).toBe(250);
    expect(form.scopeRules.userAgentComparison.sampleSize).toBe(50);
    expect(form.enabledCategories).toBeNull();
  });

  it('round-trips the wire fields through the update payload', () => {
    const dto = JSON.parse(PRODUCER_CONFIGURATION_JSON) as ConfigurationWireDto;
    const payload = configurationToUpdateRequest(configurationFromWire(dto));
    const json = JSON.parse(JSON.stringify(payload)) as Record<string, unknown>;
    expect(json.name).toBe('Full crawl');
    expect(json.startUrls).toEqual(['http://127.0.0.1:47311/']);
    expect(json.limits).toEqual({ maxUrls: 2, maxDepth: 1, maxDurationSeconds: 30, maxResponseBytes: null });
    expect(json.aimd).toEqual({ initialRateR0: 2, alpha: 0.5, beta: 0.5, kappa: 4, ewmaTtfbWeight: 0.2 });
    expect(json.rendering).toEqual({ enabled: false, deviceProfile: null, infiniteScroll: false, captureConsole: false });
    expect(json.respectRobotsTxt).toBe(true);
    // Additive engine documents (WP-09-06 DTO-extension handoff).
    expect(json.scopeRules).toBeDefined();
    expect((json.scopeRules as Record<string, unknown>).cookieJar).toEqual({ enabled: true });
    expect(json.enabledCategories).toBeNull();
  });

  it('tolerates a future producer that already carries scopeRules', () => {
    const dto = {
      ...(JSON.parse(PRODUCER_CONFIGURATION_JSON) as ConfigurationWireDto),
      scopeRules: { cookieJar: { enabled: false }, scope: { mode: 'includeSubdomains' } },
      enabledCategories: ['Indexability', 'Linking']
    } as unknown as ConfigurationWireDto;
    const form = configurationFromWire(dto);
    expect(form.scopeRules.cookieJar.enabled).toBe(false);
    expect(form.scopeRules.scope.mode).toBe('includeSubdomains');
    // Untouched groups keep the baseline.
    expect(form.scopeRules.scope.regexTimeoutMs).toBe(250);
    expect(form.scopeRules.directives.followMetaRefresh).toBe(true);
    expect(form.enabledCategories).toEqual(['Indexability', 'Linking']);
  });

  it('create payload matches the ConfigurationCreateRequest contract plus additive members', () => {
    const form = defaultConfigurationForm('http://127.0.0.1:47311/');
    const payload = configurationToCreateRequest(form);
    const json = JSON.parse(JSON.stringify(payload)) as Record<string, unknown>;
    expect(Object.keys(json).sort()).toEqual([
      'aimd', 'enabledCategories', 'limits', 'name', 'rendering',
      'respectRobotsTxt', 'rewrite', 'scopeRules', 'startUrls', 'virtualRobotsTxt'
    ]);
    expect(json.name).toBe('Default configuration');
    expect(json.startUrls).toEqual(['http://127.0.0.1:47311/']);
    expect(json.limits).toEqual({ maxUrls: null, maxDepth: null, maxDurationSeconds: null, maxResponseBytes: null });
  });

  it('exposes the 13 verbatim tabs and the 11 scoring categories', () => {
    expect(CONFIGURATION_TABS).toEqual([
      'Scope', 'Crawl as and capture', 'Limits', 'Rules', 'Directives',
      'Resources and external scope', 'Authentication and cookies',
      'Click discovery and site types', 'Consent states', 'Analysis categories',
      'Thresholds', 'Extraction and search', 'Advanced'
    ]);
    expect(ANALYSIS_CATEGORIES.map((category) => category.key)).toEqual([
      'Indexability', 'Content', 'Linking', 'StructuredData', 'Accessibility',
      'Performance', 'Crawl', 'Custom', 'LogIntelligence', 'AiSearchGeo', 'AnalyticsTagging'
    ]);
  });
});

describe('validation and conventions', () => {
  it('AIMD validation mirrors 03/01 §3.3/§4.3', () => {
    expect(validateAimd(DEFAULT_AIMD)).toEqual({});
    expect(validateAimd({ ...DEFAULT_AIMD, beta: 1 })).toHaveProperty('beta');
    expect(validateAimd({ ...DEFAULT_AIMD, beta: 0 })).toHaveProperty('beta');
    expect(validateAimd({ ...DEFAULT_AIMD, ewmaTtfbWeight: 0 })).toHaveProperty('ewmaTtfbWeight');
    expect(validateAimd({ ...DEFAULT_AIMD, ewmaTtfbWeight: 1 })).toEqual({});
    expect(validateAimd({ ...DEFAULT_AIMD, kappa: 0 })).toHaveProperty('kappa');
    expect(validateAimd({ ...DEFAULT_AIMD, kappa: 2.5 })).toHaveProperty('kappa');
    expect(validateAimd({ ...DEFAULT_AIMD, initialRateR0: -1 })).toHaveProperty('initialRateR0');
    expect(validateAimd({ ...DEFAULT_AIMD, alpha: 0 })).toHaveProperty('alpha');
  });

  it('limit overrides accept any positive integer or the null sentinel (never clamped)', () => {
    expect(validateLimit(null)).toBeNull();
    expect(validateLimit(undefined)).toBeNull();
    expect(validateLimit(1)).toBeNull();
    expect(validateLimit(999_999_999)).toBeNull();
    expect(validateLimit(0)).not.toBeNull();
    expect(validateLimit(-5)).not.toBeNull();
    expect(validateLimit(1.5)).not.toBeNull();
  });

  it('form errors gate save on name, seeds, AIMD and limits', () => {
    const form = defaultConfigurationForm();
    expect(configurationFormErrors(form)).toHaveProperty('startUrls');
    const valid = { ...form, startUrls: ['https://example.com/'] };
    expect(configurationFormErrors(valid)).toEqual({});
    expect(configurationFormErrors({ ...valid, aimd: { ...valid.aimd, beta: 2 } })).toHaveProperty('beta');
    expect(configurationFormErrors({ ...valid, limits: { ...valid.limits, maxUrls: 0 } })).toHaveProperty('limits.maxUrls');
    expect(configurationFormErrors({ ...valid, name: '  ' })).toHaveProperty('name');
  });

  it('category toggles follow the null all-enabled snapshot convention', () => {
    expect(toggleCategory(null, 'Content', false)).not.toContain('Content');
    const withoutContent = toggleCategory(null, 'Content', false)!;
    expect(toggleCategory(withoutContent, 'Content', true)).toBeNull();
    expect(toggleCategory(['Indexability'], 'Linking', true)).toEqual(['Indexability', 'Linking']);
  });

  it('crawl-as presets map to the engine user-agent fields', () => {
    const baseline = { customUserAgent: null, simulateGooglebot: false };
    expect(presetFromUserAgent(baseline)).toBe('CrawlForge default');
    expect(presetFromUserAgent(userAgentForPreset('Googlebot Smartphone', baseline))).toBe('Googlebot Smartphone');
    expect(userAgentForPreset('Googlebot Smartphone', baseline).simulateGooglebot).toBe(true);
    expect(presetFromUserAgent(userAgentForPreset('Chrome desktop', baseline))).toBe('Chrome desktop');
    expect(presetFromUserAgent(userAgentForPreset('Googlebot Desktop', baseline))).toBe('Googlebot Desktop');
    expect(presetFromUserAgent({ customUserAgent: 'MyBot/1.0', simulateGooglebot: false })).toBe('Custom');
  });
});

describe('project overview wire payloads (ProjectOverviewWireTests fixtures)', () => {
  it('consumes the real project/audit/configuration summary shapes', async () => {
    const { projectSummaryFixture, auditSummaryFixture, configurationSummaryFixture } = await import('./helpers');
    const project = JSON.parse(JSON.stringify(projectSummaryFixture)) as Record<string, unknown>;
    expect(Object.keys(project).sort()).toEqual(['createdAt', 'description', 'id', 'name', 'scopeSummary', 'tags', 'updatedAt']);
    const audit = JSON.parse(JSON.stringify(auditSummaryFixture('11'))) as Record<string, unknown>;
    expect(Object.keys(audit).sort()).toEqual(['completedAt', 'configurationId', 'counters', 'id', 'phase', 'projectId', 'startedAt', 'state']);
    expect((audit.counters as Record<string, unknown>).fetched).toBe(120);
    const configuration = JSON.parse(JSON.stringify(configurationSummaryFixture('7', 'Full crawl'))) as Record<string, unknown>;
    expect(Object.keys(configuration).sort()).toEqual(['createdAt', 'description', 'id', 'name', 'projectId', 'updatedAt']);
  });
});
