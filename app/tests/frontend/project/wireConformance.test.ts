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
  REDIRECT_CHECKS_ALL_ENABLED,
  redirectChecksFromWire,
  redirectChecksToWire,
  toggleCategory,
  toggleRedirectCheck,
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
    // CR-029: absent redirectChecks document = all four checks ON.
    expect(form.redirectChecks).toEqual({ crossHost: true, temporaryStatus: true, nonIndexableTarget: true, httpsDowngrade: true });
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
      'aimd', 'captureMode', 'captureSettings', 'enabledCategories', 'limits', 'name', 'redirectChecks', 'rendering',
      'respectRobotsTxt', 'rewrite', 'scopeRules', 'startUrls', 'virtualRobotsTxt'
    ]);
    // CR-029: all-enabled collapses to the null sentinel (absent/null = all ON).
    expect(json.redirectChecks).toBeNull();
    // WP-10-04 (§5.6.1): new configurations default to non-js; the capture
    // settings document carries the engine defaults explicitly.
    expect(json.captureMode).toBe('non-js');
    expect(json.captureSettings).toEqual({
      adaptiveSampleSizePerGroup: 4,
      materialChangeWordCountThreshold: 0.2,
      dualStorageWarningThresholdBytes: 1073741824,
      renderWaitQuietMs: 500,
      renderWaitHardCapMs: 20000,
      renderingPoolSize: null,
      patternOverrides: []
    });
    expect(json.name).toBe('Default configuration');
    expect(json.startUrls).toEqual(['http://127.0.0.1:47311/']);
    expect(json.limits).toEqual({ maxUrls: null, maxDepth: null, maxDurationSeconds: null, maxResponseBytes: null });
  });

  it('exposes the 13 verbatim tabs and the 14 scoring categories', () => {
    expect(CONFIGURATION_TABS).toEqual([
      'Scope', 'Crawl as and capture', 'Limits', 'Rules', 'Directives',
      'Resources and external scope', 'Authentication and cookies',
      'Click discovery and site types', 'Consent states', 'Analysis categories',
      'Thresholds', 'Extraction and search', 'Advanced'
    ]);
    // WP-10-A1 (D-021): the three additive §7.4 categories append to the
    // locked baseline list (extension, never weakening).
    expect(ANALYSIS_CATEGORIES.map((category) => category.key)).toEqual([
      'Indexability', 'Content', 'Linking', 'StructuredData', 'Accessibility',
      'Performance', 'Crawl', 'Custom', 'LogIntelligence', 'AiSearchGeo', 'AnalyticsTagging',
      'Images', 'SecurityAndProtocol', 'Internationalization'
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

describe('CR-029 redirectChecks wire round-trip (WP-10-05)', () => {
  it('defaults to all four checks ON (form and absent/null wire)', () => {
    expect(defaultConfigurationForm().redirectChecks).toEqual(REDIRECT_CHECKS_ALL_ENABLED);
    expect(redirectChecksFromWire(undefined)).toEqual(REDIRECT_CHECKS_ALL_ENABLED);
    expect(redirectChecksFromWire(null)).toEqual(REDIRECT_CHECKS_ALL_ENABLED);
    expect(redirectChecksFromWire({})).toEqual(REDIRECT_CHECKS_ALL_ENABLED);
    // Missing individual fields default ON too (additive wire tolerance).
    expect(redirectChecksFromWire({ crossHost: false })).toEqual({
      crossHost: false, temporaryStatus: true, nonIndexableTarget: true, httpsDowngrade: true
    });
  });

  it('collapses all-enabled to null and emits explicit documents otherwise', () => {
    expect(redirectChecksToWire(REDIRECT_CHECKS_ALL_ENABLED)).toBeNull();
    expect(redirectChecksToWire({ ...REDIRECT_CHECKS_ALL_ENABLED, httpsDowngrade: false })).toEqual({
      crossHost: true, temporaryStatus: true, nonIndexableTarget: true, httpsDowngrade: false
    });
  });

  it('round-trips every single-toggle state through create and update payloads', () => {
    const cases = ['crossHost', 'temporaryStatus', 'nonIndexableTarget', 'httpsDowngrade'] as const;
    for (const key of cases) {
      const form = { ...defaultConfigurationForm('https://example.com/'), redirectChecks: toggleRedirectCheck(REDIRECT_CHECKS_ALL_ENABLED, key, false) };
      for (const payload of [configurationToCreateRequest(form), configurationToUpdateRequest(form)]) {
        const json = JSON.parse(JSON.stringify(payload)) as { redirectChecks: Record<string, boolean> };
        expect(json.redirectChecks).toEqual({ crossHost: true, temporaryStatus: true, nonIndexableTarget: true, httpsDowngrade: true, [key]: false });
        // Real consumer: the wire document binds back to the same form state.
        const readBack = configurationFromWire({ ...JSON.parse(PRODUCER_CONFIGURATION_JSON), redirectChecks: json.redirectChecks } as ConfigurationWireDto);
        expect(readBack.redirectChecks).toEqual(form.redirectChecks);
        expect(readBack.redirectChecks[key]).toBe(false);
      }
    }
  });

  it('each toggle flips independently and back to the null sentinel', () => {
    let checks = REDIRECT_CHECKS_ALL_ENABLED;
    const cases = ['crossHost', 'temporaryStatus', 'nonIndexableTarget', 'httpsDowngrade'] as const;
    for (const key of cases) {
      checks = toggleRedirectCheck(checks, key, false);
      expect(checks[key]).toBe(false);
      expect(redirectChecksToWire(checks)).not.toBeNull();
      checks = toggleRedirectCheck(checks, key, true);
      expect(checks[key]).toBe(true);
    }
    expect(redirectChecksToWire(checks)).toBeNull();
    // All four off is a valid explicit document (never the null sentinel).
    const allOff = cases.reduce((acc, key) => toggleRedirectCheck(acc, key, false), REDIRECT_CHECKS_ALL_ENABLED);
    expect(redirectChecksToWire(allOff)).toEqual({ crossHost: false, temporaryStatus: false, nonIndexableTarget: false, httpsDowngrade: false });
  });
});

describe('capture mode + settings wire round-trip (WP-10-04, §5.6.1)', () => {
  it('absent/null capture fields normalize to non-js + engine defaults', () => {
    const form = configurationFromWire({ name: 'c', startUrls: [] } as unknown as ConfigurationWireDto);
    expect(form.captureMode).toBe('non-js');
    expect(form.captureSettings.adaptiveSampleSize).toBe(4);
    expect(form.captureSettings.materialThresholdPercent).toBe(20);
    expect(form.captureSettings.dualStorageThresholdBytes).toBe(1073741824);
    expect(form.captureSettings.renderWaitQuietMs).toBe(500);
    expect(form.captureSettings.renderWaitHardCapMs).toBe(20000);
    expect(form.captureSettings.renderingPoolSize).toBeNull();
    expect(form.captureSettings.patternOverrides).toEqual([]);
  });

  it('round-trips every knob through create and update payloads (percent ↔ fraction)', () => {
    const form = {
      ...defaultConfigurationForm('https://example.com/'),
      captureMode: 'adaptive' as const,
      captureSettings: {
        adaptiveSampleSize: 5,
        materialThresholdPercent: 35,
        dualStorageThresholdBytes: 2147483648,
        renderWaitQuietMs: 750,
        renderWaitHardCapMs: 30000,
        renderingPoolSize: 3,
        patternOverrides: [{ pattern: '/blog/*', capture: 'rendered' as const }, { pattern: '/assets/*', capture: 'non-rendered' as const }]
      }
    };
    for (const payload of [configurationToCreateRequest(form), configurationToUpdateRequest(form)]) {
      const json = JSON.parse(JSON.stringify(payload)) as Record<string, unknown>;
      expect(json.captureMode).toBe('adaptive');
      expect(json.captureSettings).toEqual({
        adaptiveSampleSizePerGroup: 5,
        materialChangeWordCountThreshold: 0.35,
        dualStorageWarningThresholdBytes: 2147483648,
        renderWaitQuietMs: 750,
        renderWaitHardCapMs: 30000,
        renderingPoolSize: 3,
        patternOverrides: [{ pattern: '/blog/*', capture: 'rendered' }, { pattern: '/assets/*', capture: 'non-rendered' }]
      });
      // Real consumer: the wire document binds back to the same form state.
      const readBack = configurationFromWire(json as unknown as ConfigurationWireDto);
      expect(readBack.captureMode).toBe('adaptive');
      expect(readBack.captureSettings).toEqual(form.captureSettings);
    }
  });

  it('dual mode emits the storage warning threshold; pool size null stays auto', () => {
    const form = { ...defaultConfigurationForm(), captureMode: 'dual' as const };
    const json = JSON.parse(JSON.stringify(configurationToCreateRequest(form))) as { captureSettings: { renderingPoolSize: unknown; dualStorageWarningThresholdBytes: number } };
    expect(json.captureSettings.renderingPoolSize).toBeNull();
    expect(json.captureSettings.dualStorageWarningThresholdBytes).toBe(1073741824);
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
