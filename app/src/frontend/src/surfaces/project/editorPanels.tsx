/**
 * The 13 configuration-editor tab panels (WP-09-07, UI-04).
 *
 * Binding map (form model → engine consumption):
 *  - Scope / Rules / Directives / Resources / Authentication and cookies →
 *    `scopeRules` document (CrawlScopeConfigParser, scope_rules_json).
 *  - Crawl as and capture → `scopeRules.userAgent` + `userAgentComparison`,
 *    wire `respectRobotsTxt` / `virtualRobotsTxt`; rendering controls are
 *    disabled-with-note until the phase-10 rendering engine lands.
 *  - Limits → wire `limits` (null = unlimited) + `aimd` (§3.8 form group).
 *  - Analysis categories → `enabledCategories` (scoring snapshot convention).
 *  - Click discovery / Consent states → phase 11; Thresholds / Extraction /
 *    zone weights → phase 12 (disabled-with-note, CR-022).
 *
 * @module surfaces/project/editorPanels
 */

import { useState } from 'react';
import { ActionIcon, Button, Group, NumberInput, Select, Stack, Switch, Text, Textarea, TextInput } from '@mantine/core';
import type { ConfigurationFormValue, ScopeRulesDocument } from './configurationModel';
import {
  ANALYSIS_CATEGORIES,
  CRAWL_AS_PRESETS,
  presetFromUserAgent,
  toggleCategory,
  userAgentForPreset,
  validateAimd
} from './configurationModel';
import { DisabledSection, FieldSection, KeyValueEditor, LaterPhaseSection, LimitField, PatternListField, ToggleRow } from './editorFields';

/** Panel props: the whole form value + a whole-value change callback. */
export interface PanelProps {
  readonly value: ConfigurationFormValue;
  readonly onChange: (next: ConfigurationFormValue) => void;
}

/** Shallow-merges one scope-rules group into the form value. */
function withScope<K extends keyof ScopeRulesDocument>(value: ConfigurationFormValue, group: K, patch: Partial<ScopeRulesDocument[K]>): ConfigurationFormValue {
  return { ...value, scopeRules: { ...value.scopeRules, [group]: { ...value.scopeRules[group], ...patch } } };
}

// ── 1. Scope (default tab; >3 inputs) ───────────────────────────────────────

export function ScopePanel({ value, onChange }: PanelProps) {
  const scope = value.scopeRules.scope;
  return (
    <Stack gap="md" data-testid="config-panel-scope">
      <Textarea
        label="Start URLs"
        description="One URL per line. The crawl seeds; sitemaps and robots directives extend discovery."
        value={value.startUrls.join('\n')}
        placeholder="https://example.com/"
        autosize
        minRows={3}
        onChange={(event) => onChange({ ...value, startUrls: event.currentTarget.value.split(/\r?\n/).map((line) => line.trim()).filter((line) => line.length > 0) })}
        data-testid="config-start-urls"
      />
      <Select
        label="Host scope"
        description="Which hosts are in scope relative to the seeds."
        value={scope.mode}
        allowDeselect={false}
        data={[
          { value: 'hostOnly', label: 'Host only (exact seed hosts)' },
          { value: 'includeSubdomains', label: 'Include subdomains' },
          { value: 'wholeDomain', label: 'Whole registered domain' }
        ]}
        onChange={(next) => onChange(withScope(value, 'scope', { mode: (next ?? 'hostOnly') as typeof scope.mode }))}
        data-testid="config-scope-mode"
      />
      <PatternListField
        label="Include patterns"
        description="Regular expressions; a URL must match at least one when the list is non-empty."
        lines={scope.includePatterns}
        onChange={(next) => onChange(withScope(value, 'scope', { includePatterns: next }))}
        testid="config-include-patterns"
        placeholder="/products/.*"
      />
      <PatternListField
        label="Exclude patterns"
        description="Regular expressions evaluated after inclusion; any match skips the URL."
        lines={scope.excludePatterns}
        onChange={(next) => onChange(withScope(value, 'scope', { excludePatterns: next }))}
        testid="config-exclude-patterns"
        placeholder="/cart/.*"
      />
      <PatternListField
        label="Path prefixes"
        description="When non-empty the URL path must start with at least one prefix."
        lines={scope.pathPrefixes}
        onChange={(next) => onChange(withScope(value, 'scope', { pathPrefixes: next }))}
        testid="config-path-prefixes"
        placeholder="/docs/"
      />
      <PatternListField
        label="Asset domains"
        description="CDN hosts treated as internal for resource fetching."
        lines={scope.assetDomains}
        onChange={(next) => onChange(withScope(value, 'scope', { assetDomains: next }))}
        testid="config-asset-domains"
        placeholder="cdn.example.com"
      />
      <ToggleRow
        label="Check external links"
        description="Probe external link status without crawling beyond them."
        checked={scope.checkExternalLinks}
        onChange={(next) => onChange(withScope(value, 'scope', { checkExternalLinks: next }))}
        testid="config-check-external-links"
      />
      <NumberInput
        label="Regex timeout (ms)"
        description="Bounded pattern-evaluation timeout per URL."
        value={scope.regexTimeoutMs}
        allowDecimal={false}
        onChange={(next) => onChange(withScope(value, 'scope', { regexTimeoutMs: typeof next === 'number' ? next : 250 }))}
        data-testid="config-regex-timeout"
      />
    </Stack>
  );
}

// ── 2. Crawl as and capture ─────────────────────────────────────────────────

export function CrawlAsPanel({ value, onChange }: PanelProps) {
  const userAgent = value.scopeRules.userAgent;
  const comparison = value.scopeRules.userAgentComparison;
  const rendering = value.rendering;
  return (
    <Stack gap="md" data-testid="config-panel-crawl-as">
      <FieldSection title="Crawl identity" note="Presets bundle the user agent (§8.8); the custom string wins over the preset.">
        <Select
          label="Crawl as"
          value={presetFromUserAgent(userAgent)}
          allowDeselect={false}
          data={[...CRAWL_AS_PRESETS]}
          onChange={(next) => onChange({ ...value, scopeRules: { ...value.scopeRules, userAgent: userAgentForPreset((next ?? 'CrawlForge default') as typeof CRAWL_AS_PRESETS[number], userAgent) } })}
          data-testid="config-crawl-as-preset"
        />
        <TextInput
          label="Custom user agent"
          description="Overrides the preset identity when non-empty."
          value={userAgent.customUserAgent ?? ''}
          onChange={(event) => onChange(withScope(value, 'userAgent', { customUserAgent: event.currentTarget.value || null }))}
          data-testid="config-custom-user-agent"
        />
        <ToggleRow
          label="Simulate Googlebot Smartphone"
          description="Send the bundled Googlebot Smartphone identity instead of the crawler identity."
          checked={userAgent.simulateGooglebot}
          onChange={(next) => onChange(withScope(value, 'userAgent', { simulateGooglebot: next }))}
          testid="config-simulate-googlebot"
        />
      </FieldSection>
      <FieldSection title="Robots handling">
        <ToggleRow
          label="Respect robots.txt"
          description="Fetch, cache per host and obey allow/disallow rules and crawl-delay."
          checked={value.respectRobotsTxt}
          onChange={(next) => onChange({ ...value, respectRobotsTxt: next })}
          testid="config-respect-robots"
        />
        <Textarea
          label="Virtual robots.txt"
          description="Applied when the target serves no robots.txt."
          value={value.virtualRobotsTxt ?? ''}
          autosize
          minRows={2}
          onChange={(event) => onChange({ ...value, virtualRobotsTxt: event.currentTarget.value || null })}
          data-testid="config-virtual-robots"
        />
      </FieldSection>
      <FieldSection title="User-agent comparison (§5.15)" note="Fetch a sample both as Googlebot Smartphone and as a standard Chrome browser.">
        <ToggleRow
          label="Enable comparison"
          checked={comparison.enabled}
          onChange={(next) => onChange(withScope(value, 'userAgentComparison', { enabled: next }))}
          testid="config-ua-comparison-enabled"
        />
        <LimitField
          label="Comparison sample size"
          description="Unlimited compares every URL; the default sample is 50."
          value={comparison.sampleSize}
          onChange={(next) => onChange(withScope(value, 'userAgentComparison', { sampleSize: next }))}
          testid="config-ua-comparison-sample"
          overrideDefault={50}
        />
      </FieldSection>
      <LaterPhaseSection title="Capture mode" phase={10} feature="Headless rendering and capture modes" testid="config-rendering-section">
        <ToggleRow
          label="Enable rendering"
          checked={rendering.enabled}
          onChange={(next) => onChange({ ...value, rendering: { ...rendering, enabled: next } })}
          testid="config-rendering-enabled"
          disabled
        />
        <TextInput
          label="Device profile"
          value={rendering.deviceProfile ?? ''}
          onChange={(event) => onChange({ ...value, rendering: { ...rendering, deviceProfile: event.currentTarget.value || null } })}
          data-testid="config-rendering-device"
          disabled
        />
        <ToggleRow
          label="Infinite scroll"
          checked={rendering.infiniteScroll ?? false}
          onChange={(next) => onChange({ ...value, rendering: { ...rendering, infiniteScroll: next } })}
          testid="config-rendering-infinite-scroll"
          disabled
        />
        <ToggleRow
          label="Capture console"
          checked={rendering.captureConsole ?? false}
          onChange={(next) => onChange({ ...value, rendering: { ...rendering, captureConsole: next } })}
          testid="config-rendering-capture-console"
          disabled
        />
      </LaterPhaseSection>
    </Stack>
  );
}

// ── 3. Limits ───────────────────────────────────────────────────────────────

export function LimitsPanel({ value, onChange }: PanelProps) {
  const aimdErrors = validateAimd(value.aimd);
  const setAimd = (patch: Partial<typeof value.aimd>) => onChange({ ...value, aimd: { ...value.aimd, ...patch } });
  const number = (next: string | number, fallback: number) => (typeof next === 'number' ? next : fallback);
  return (
    <Stack gap="md" data-testid="config-panel-limits">
      <FieldSection title="Crawl ceilings" note="Unlimited by default (null sentinel). Overrides are explicit and never clamped; a running audit keeps its starting snapshot.">
        <LimitField
          label="Total page ceiling"
          description="Maximum URLs fetched in one audit."
          value={value.limits.maxUrls}
          onChange={(next) => onChange({ ...value, limits: { ...value.limits, maxUrls: next } })}
          testid="config-limit-max-urls"
        />
        <LimitField
          label="Crawl depth ceiling"
          description="Maximum link hops from any seed URL."
          value={value.limits.maxDepth}
          onChange={(next) => onChange({ ...value, limits: { ...value.limits, maxDepth: next } })}
          testid="config-limit-max-depth"
          overrideDefault={10}
        />
        <LimitField
          label="Audit duration ceiling (seconds)"
          description="Optional wall-clock cap for an audit."
          value={value.limits.maxDurationSeconds}
          onChange={(next) => onChange({ ...value, limits: { ...value.limits, maxDurationSeconds: next } })}
          testid="config-limit-max-duration"
          overrideDefault={3600}
        />
        <LimitField
          label="Response size ceiling (bytes)"
          description="Maximum bytes accepted per response."
          value={value.limits.maxResponseBytes}
          onChange={(next) => onChange({ ...value, limits: { ...value.limits, maxResponseBytes: next } })}
          testid="config-limit-max-response-bytes"
          overrideDefault={5_000_000}
        />
      </FieldSection>
      <FieldSection title="AIMD auto-adaptive politeness" note="Per-host rate and concurrency adapt from TTFB and response health (FS-CRAWL-002 §3). Validation mirrors §4.3.">
        <NumberInput
          label="Initial rate R0 (requests/sec)"
          value={value.aimd.initialRateR0}
          onChange={(next) => setAimd({ initialRateR0: number(next, 2) })}
          error={aimdErrors.initialRateR0}
          data-testid="config-aimd-r0"
        />
        <NumberInput
          label="Additive increase alpha"
          value={value.aimd.alpha}
          onChange={(next) => setAimd({ alpha: number(next, 0.5) })}
          error={aimdErrors.alpha}
          data-testid="config-aimd-alpha"
        />
        <NumberInput
          label="Multiplicative decrease beta"
          description="Must be in (0, 1)."
          value={value.aimd.beta}
          onChange={(next) => setAimd({ beta: number(next, 0.5) })}
          error={aimdErrors.beta}
          data-testid="config-aimd-beta"
        />
        <NumberInput
          label="Concurrency cap kappa"
          description="Maximum simultaneous fetch workers per host."
          value={value.aimd.kappa}
          allowDecimal={false}
          onChange={(next) => setAimd({ kappa: number(next, 4) })}
          error={aimdErrors.kappa}
          data-testid="config-aimd-kappa"
        />
        <NumberInput
          label="EWMA TTFB weight"
          description="Smoothing weight in (0, 1]."
          value={value.aimd.ewmaTtfbWeight}
          onChange={(next) => setAimd({ ewmaTtfbWeight: number(next, 0.2) })}
          error={aimdErrors.ewmaTtfbWeight}
          data-testid="config-aimd-ewma"
        />
      </FieldSection>
      <DisabledSection
        title="Additional ceilings"
        note="Per-domain page ceiling, URL throughput ceiling, request timeout, retry budget and redirect hop ceiling are enforced by the engine today; per-configuration fields await the WP-09-06 DTO extension (handoff)."
        testid="config-limits-pending"
      >
        <NumberInput label="Per-domain page ceiling" placeholder="Unlimited" data-testid="config-limit-per-domain" disabled />
        <NumberInput label="URL throughput ceiling (URLs/sec)" placeholder="Unlimited" data-testid="config-limit-throughput" disabled />
        <NumberInput label="Request timeout (seconds)" placeholder="Engine default" data-testid="config-limit-timeout" disabled />
        <NumberInput label="Retry budget" placeholder="Engine default" data-testid="config-limit-retries" disabled />
        <NumberInput label="Redirect hop ceiling" placeholder="Engine default (20)" data-testid="config-limit-redirect-hops" disabled />
      </DisabledSection>
    </Stack>
  );
}

// ── 4. Rules ────────────────────────────────────────────────────────────────

export function RulesPanel({ value, onChange }: PanelProps) {
  const canonicalization = value.scopeRules.canonicalization;
  const rules = value.rewrite?.rules ?? [];
  const mappings = value.rewrite?.hostMappings ?? [];
  const [ruleRows, setRuleRows] = useState(() => rules.map((rule) => ({ ...rule })));
  const [mappingRows, setMappingRows] = useState(() => mappings.map((mapping) => ({ ...mapping })));

  const emitRewrite = (nextRules: typeof ruleRows, nextMappings: typeof mappingRows) => {
    setRuleRows(nextRules);
    setMappingRows(nextMappings);
    onChange({
      ...value,
      rewrite: {
        rules: nextRules.filter((rule) => rule.pattern.trim().length > 0).map((rule) => ({ pattern: rule.pattern, replacement: rule.replacement, enabled: rule.enabled ?? true })),
        hostMappings: nextMappings.filter((mapping) => mapping.fromHost.trim().length > 0).map((mapping) => ({ fromHost: mapping.fromHost, toHost: mapping.toHost }))
      }
    });
  };

  return (
    <Stack gap="md" data-testid="config-panel-rules">
      <FieldSection title="URL canonicalization (§5.2)">
        <Select
          label="Query parameter policy"
          value={canonicalization.queryPolicy}
          allowDeselect={false}
          data={[
            { value: 'keepAll', label: 'Keep all parameters' },
            { value: 'keepWhitelist', label: 'Keep whitelisted only' },
            { value: 'stripAll', label: 'Strip all parameters' }
          ]}
          onChange={(next) => onChange(withScope(value, 'canonicalization', { queryPolicy: (next ?? 'keepAll') as typeof canonicalization.queryPolicy }))}
          data-testid="config-query-policy"
        />
        <PatternListField
          label="Query whitelist"
          description="Parameter names kept under the whitelist policy."
          lines={canonicalization.queryWhitelist}
          onChange={(next) => onChange(withScope(value, 'canonicalization', { queryWhitelist: next }))}
          testid="config-query-whitelist"
          placeholder="page"
        />
        <Select
          label="Trailing slash"
          value={canonicalization.trailingSlash}
          allowDeselect={false}
          data={[
            { value: 'preserve', label: 'Preserve' },
            { value: 'append', label: 'Append' },
            { value: 'strip', label: 'Strip' }
          ]}
          onChange={(next) => onChange(withScope(value, 'canonicalization', { trailingSlash: (next ?? 'preserve') as typeof canonicalization.trailingSlash }))}
          data-testid="config-trailing-slash"
        />
        <ToggleRow
          label="Strip cookieless session segments"
          description="Remove session identifiers from paths when no cookie jar is present."
          checked={canonicalization.stripCookielessSessionSegments}
          onChange={(next) => onChange(withScope(value, 'canonicalization', { stripCookielessSessionSegments: next }))}
          testid="config-strip-session-segments"
        />
        <PatternListField
          label="Additional session parameters"
          description="Extra parameter names treated as session trackers."
          lines={canonicalization.additionalSessionParameters}
          onChange={(next) => onChange(withScope(value, 'canonicalization', { additionalSessionParameters: next }))}
          testid="config-session-parameters"
          placeholder="sid"
        />
      </FieldSection>
      <FieldSection title="URL rewrite rules (§10.7)" note="On-the-fly regex rewrites applied at request time; invalid patterns are rejected at configuration time.">
        {ruleRows.map((rule, index) => (
          <Group key={index} gap="xs" wrap="nowrap" align="flex-start">
            <TextInput
              value={rule.pattern}
              placeholder="Pattern (regex)"
              aria-label={`Rewrite pattern ${index + 1}`}
              onChange={(event) => emitRewrite(ruleRows.map((r, i) => (i === index ? { ...r, pattern: event.currentTarget.value } : r)), mappingRows)}
              data-testid={`config-rewrite-pattern-${index}`}
              style={{ flex: 2 }}
            />
            <TextInput
              value={rule.replacement}
              placeholder="Replacement"
              aria-label={`Rewrite replacement ${index + 1}`}
              onChange={(event) => emitRewrite(ruleRows.map((r, i) => (i === index ? { ...r, replacement: event.currentTarget.value } : r)), mappingRows)}
              data-testid={`config-rewrite-replacement-${index}`}
              style={{ flex: 2 }}
            />
            <Switch
              label="Enabled"
              checked={rule.enabled ?? true}
              onChange={(event) => emitRewrite(ruleRows.map((r, i) => (i === index ? { ...r, enabled: event.currentTarget.checked } : r)), mappingRows)}
              data-testid={`config-rewrite-enabled-${index}`}
            />
            <ActionIcon variant="subtle" color="red" aria-label={`Remove rewrite rule ${index + 1}`} onClick={() => emitRewrite(ruleRows.filter((_, i) => i !== index), mappingRows)} data-testid={`config-rewrite-remove-${index}`}>
              ×
            </ActionIcon>
          </Group>
        ))}
        <div>
          <Button variant="light" size="compact-xs" onClick={() => emitRewrite([...ruleRows, { pattern: '', replacement: '', enabled: true }], mappingRows)} data-testid="config-rewrite-add">
            Add rewrite rule
          </Button>
        </div>
      </FieldSection>
      <FieldSection title="Host mappings" note="Migration / canonical host equivalence (staging ↔ production).">
        {mappingRows.map((mapping, index) => (
          <Group key={index} gap="xs" wrap="nowrap" align="flex-start">
            <TextInput
              value={mapping.fromHost}
              placeholder="From host"
              aria-label={`Host mapping from ${index + 1}`}
              onChange={(event) => emitRewrite(ruleRows, mappingRows.map((m, i) => (i === index ? { ...m, fromHost: event.currentTarget.value } : m)))}
              data-testid={`config-hostmap-from-${index}`}
              style={{ flex: 1 }}
            />
            <TextInput
              value={mapping.toHost}
              placeholder="To host"
              aria-label={`Host mapping to ${index + 1}`}
              onChange={(event) => emitRewrite(ruleRows, mappingRows.map((m, i) => (i === index ? { ...m, toHost: event.currentTarget.value } : m)))}
              data-testid={`config-hostmap-to-${index}`}
              style={{ flex: 1 }}
            />
            <ActionIcon variant="subtle" color="red" aria-label={`Remove host mapping ${index + 1}`} onClick={() => emitRewrite(ruleRows, mappingRows.filter((_, i) => i !== index))} data-testid={`config-hostmap-remove-${index}`}>
              ×
            </ActionIcon>
          </Group>
        ))}
        <div>
          <Button variant="light" size="compact-xs" onClick={() => emitRewrite(ruleRows, [...mappingRows, { fromHost: '', toHost: '' }])} data-testid="config-hostmap-add">
            Add host mapping
          </Button>
        </div>
      </FieldSection>
      <LaterPhaseSection title="Content area scoping" phase={12} feature="Scoped Visible Text Tree include/exclude enforcement" testid="config-content-scope-section">
        <PatternListField
          label="Include selectors"
          lines={value.contentScope?.includeSelectors ?? []}
          onChange={(next) => onChange({ ...value, contentScope: { includeSelectors: next, excludeSelectors: value.contentScope?.excludeSelectors ?? [] } })}
          testid="config-content-include"
          disabled
        />
        <PatternListField
          label="Exclude selectors"
          lines={value.contentScope?.excludeSelectors ?? []}
          onChange={(next) => onChange({ ...value, contentScope: { includeSelectors: value.contentScope?.includeSelectors ?? [], excludeSelectors: next } })}
          testid="config-content-exclude"
          disabled
        />
      </LaterPhaseSection>
    </Stack>
  );
}

// ── 5. Directives ───────────────────────────────────────────────────────────

export function DirectivesPanel({ value, onChange }: PanelProps) {
  const directives = value.scopeRules.directives;
  const set = (patch: Partial<typeof directives>) => onChange(withScope(value, 'directives', patch));
  return (
    <Stack gap="md" data-testid="config-panel-directives">
      <FieldSection title="Directive handling (§5.12)" note="Googlebot-following defaults. Detection and reporting of directives are unaffected — these toggles govern discovery and crawl decisions only.">
        <ToggleRow label="Follow links on nofollow pages" description="Page-level rel=nofollow." checked={directives.followPageNofollow} onChange={(next) => set({ followPageNofollow: next })} testid="config-follow-page-nofollow" />
        <ToggleRow label="Follow individual nofollow links" description="Link-level rel=nofollow." checked={directives.followLinkNofollow} onChange={(next) => set({ followLinkNofollow: next })} testid="config-follow-link-nofollow" />
        <ToggleRow label="Crawl noindex pages" description="Fetch pages marked noindex so they can be reported." checked={directives.crawlNoindexPages} onChange={(next) => set({ crawlNoindexPages: next })} testid="config-crawl-noindex" />
        <ToggleRow label="Use canonical targets as discovery" checked={directives.useCanonicalDiscovery} onChange={(next) => set({ useCanonicalDiscovery: next })} testid="config-canonical-discovery" />
        <ToggleRow label="Use hreflang and pagination discovery" description="rel=alternate hreflang and rel next/previous." checked={directives.useHreflangAndPaginationDiscovery} onChange={(next) => set({ useHreflangAndPaginationDiscovery: next })} testid="config-hreflang-discovery" />
        <ToggleRow label="Follow meta refresh targets" checked={directives.followMetaRefresh} onChange={(next) => set({ followMetaRefresh: next })} testid="config-meta-refresh" />
        <ToggleRow label="Follow iframe sources" checked={directives.followIframes} onChange={(next) => set({ followIframes: next })} testid="config-follow-iframes" />
      </FieldSection>
    </Stack>
  );
}

// ── 6. Resources and external scope ─────────────────────────────────────────

export function ResourcesPanel({ value, onChange }: PanelProps) {
  const resources = value.scopeRules.resources;
  const set = (patch: Partial<typeof resources>) => onChange(withScope(value, 'resources', patch));
  return (
    <Stack gap="md" data-testid="config-panel-resources">
      <FieldSection title="Resource fetching (§5.13)" note="HTML documents are always fetched — they are the audit subject. Every other type obeys its toggle; off types are recorded as metadata only.">
        <ToggleRow label="Fetch JavaScript files" checked={resources.fetchScripts} onChange={(next) => set({ fetchScripts: next })} testid="config-fetch-scripts" />
        <ToggleRow label="Fetch stylesheets" checked={resources.fetchStylesheets} onChange={(next) => set({ fetchStylesheets: next })} testid="config-fetch-stylesheets" />
        <ToggleRow label="Fetch images" checked={resources.fetchImages} onChange={(next) => set({ fetchImages: next })} testid="config-fetch-images" />
        <ToggleRow label="Fetch fonts" checked={resources.fetchFonts} onChange={(next) => set({ fetchFonts: next })} testid="config-fetch-fonts" />
        <ToggleRow label="Fetch other file types" description="PDFs and other documents." checked={resources.fetchOtherFiles} onChange={(next) => set({ fetchOtherFiles: next })} testid="config-fetch-other" />
      </FieldSection>
      <Text size="xs" c="dimmed">
        External link checking and asset domains live on the Scope tab.
      </Text>
    </Stack>
  );
}

// ── 7. Authentication and cookies ───────────────────────────────────────────

export function AuthPanel({ value, onChange }: PanelProps) {
  return (
    <Stack gap="md" data-testid="config-panel-auth">
      <FieldSection title="Cookie jar (§5.11)" note="The jar retains and replays site-set cookies per audit. Turning it off crawls cookieless; user-supplied cookies below are explicit credentials and are sent either way.">
        <ToggleRow
          label="Enable cookie jar"
          description="Retain site-set cookies (Set-Cookie) and replay them on subsequent requests. Required for cookie-gated sites."
          checked={value.scopeRules.cookieJar.enabled}
          onChange={(next) => onChange(withScope(value, 'cookieJar', { enabled: next }))}
          testid="config-cookie-jar"
        />
      </FieldSection>
      <KeyValueEditor
        label="User cookies"
        description="Name/value pairs merged into every in-scope request (§5.1)."
        value={value.scopeRules.cookies}
        onChange={(next) => onChange(withScope(value, 'cookies', next))}
        testid="config-user-cookies"
        namePlaceholder="Cookie name"
        valuePlaceholder="Cookie value"
      />
      <KeyValueEditor
        label="Custom request headers"
        description="Sent on every request; an explicit User-Agent header wins over the crawl identity."
        value={value.scopeRules.headers}
        onChange={(next) => onChange(withScope(value, 'headers', next))}
        testid="config-custom-headers"
        namePlaceholder="Header name"
        valuePlaceholder="Header value"
      />
      <DisabledSection
        title="Form login"
        note="The form-login authenticator engine is delivered (WP-09-03); per-configuration login fields await the WP-09-06 DTO extension (handoff)."
        testid="config-form-login-section"
      >
        <TextInput label="Login URL" data-testid="config-login-url" disabled />
        <TextInput label="Username field selector" data-testid="config-login-username-selector" disabled />
        <TextInput label="Password field selector" data-testid="config-login-password-selector" disabled />
      </DisabledSection>
    </Stack>
  );
}

// ── 8. Click discovery and site types (phase 11) ────────────────────────────

export function ClickDiscoveryPanel({ value: _value, onChange: _onChange }: PanelProps) {
  return (
    <Stack gap="md" data-testid="config-panel-click-discovery">
      <LaterPhaseSection title="Click discovery" phase={11} feature="Non-crawlable navigation inventory (WP-11-02)" testid="config-click-discovery-section">
        <ToggleRow label="Inventory clickable elements" checked={false} onChange={() => undefined} testid="config-click-inventory" disabled />
        <ToggleRow label="Record click handlers" checked={false} onChange={() => undefined} testid="config-click-handlers" disabled />
      </LaterPhaseSection>
      <LaterPhaseSection title="Site-type rules" phase={11} feature="Hash routes, client redirects, postbacks, ViewState, rendered soft-404 (WP-11-01)" testid="config-site-types-section">
        <ToggleRow label="Hash-route handling" checked={false} onChange={() => undefined} testid="config-site-hash-routes" disabled />
        <ToggleRow label="Client-side redirect detection" checked={false} onChange={() => undefined} testid="config-site-client-redirects" disabled />
        <ToggleRow label="Postback handling" checked={false} onChange={() => undefined} testid="config-site-postbacks" disabled />
        <ToggleRow label="ViewState handling" checked={false} onChange={() => undefined} testid="config-site-viewstate" disabled />
        <ToggleRow label="Rendered soft-404 detection" checked={false} onChange={() => undefined} testid="config-site-soft-404" disabled />
      </LaterPhaseSection>
    </Stack>
  );
}

// ── 9. Consent states (phase 11) ────────────────────────────────────────────

export function ConsentPanel({ value, onChange }: PanelProps) {
  const consent = value.consent ?? { consentModeV2Enabled: false, beaconDetectionEnabled: false };
  return (
    <Stack gap="md" data-testid="config-panel-consent">
      <LaterPhaseSection title="Consent states" phase={11} feature="Consent-state rendering and MarTech detection (WP-11-03)" testid="config-consent-section">
        <ToggleRow
          label="Consent Mode v2 detection"
          checked={consent.consentModeV2Enabled}
          onChange={(next) => onChange({ ...value, consent: { ...consent, consentModeV2Enabled: next } })}
          testid="config-consent-mode-v2"
          disabled
        />
        <ToggleRow
          label="Beacon detection"
          checked={consent.beaconDetectionEnabled}
          onChange={(next) => onChange({ ...value, consent: { ...consent, beaconDetectionEnabled: next } })}
          testid="config-beacon-detection"
          disabled
        />
      </LaterPhaseSection>
    </Stack>
  );
}

// ── 10. Analysis categories ─────────────────────────────────────────────────

export function CategoriesPanel({ value, onChange }: PanelProps) {
  const enabled = new Set(value.enabledCategories ?? ANALYSIS_CATEGORIES.map((category) => category.key));
  return (
    <Stack gap="md" data-testid="config-panel-categories">
      <FieldSection
        title="Analysis categories"
        note="Enabled per configuration, not application-wide; a disabled category contributes no score. Written into the audit snapshot at start (lead wiring)."
      >
        {ANALYSIS_CATEGORIES.map((category) => (
          <ToggleRow
            key={category.key}
            label={category.label}
            checked={enabled.has(category.key)}
            onChange={(next) => onChange({ ...value, enabledCategories: toggleCategory(value.enabledCategories, category.key, next) })}
            testid={`config-category-${category.key}`}
          />
        ))}
      </FieldSection>
    </Stack>
  );
}

// ── 11. Thresholds (phase 12) ───────────────────────────────────────────────

export function ThresholdsPanel({ value: _value, onChange: _onChange }: PanelProps) {
  return (
    <Stack gap="md" data-testid="config-panel-thresholds">
      <LaterPhaseSection title="Issue thresholds" phase={12} feature="Per-project thresholds and finding prioritisation (WP-12-05)" testid="config-thresholds-section">
        <NumberInput label="Error threshold" placeholder="Engine default" data-testid="config-threshold-error" disabled />
        <NumberInput label="Warning threshold" placeholder="Engine default" data-testid="config-threshold-warning" disabled />
        <NumberInput label="Prioritisation weight" placeholder="Engine default" data-testid="config-threshold-weight" disabled />
      </LaterPhaseSection>
    </Stack>
  );
}

// ── 12. Extraction and search (phase 12) ────────────────────────────────────

export function ExtractionPanel({ value: _value, onChange: _onChange }: PanelProps) {
  return (
    <Stack gap="md" data-testid="config-panel-extraction">
      <LaterPhaseSection title="Custom extraction" phase={12} feature="Extraction rules, dynamic columns and JS snippets (WP-12-07)" testid="config-extraction-section">
        <Textarea label="Extraction rules" placeholder="One CSS selector rule per line" data-testid="config-extraction-rules" disabled />
        <ToggleRow label="Capture JavaScript snippets" checked={false} onChange={() => undefined} testid="config-extraction-snippets" disabled />
      </LaterPhaseSection>
      <LaterPhaseSection title="Custom search" phase={12} feature="Saved search queries over crawled content (WP-12-07)" testid="config-search-section">
        <Textarea label="Search queries" placeholder="One query per line" data-testid="config-search-queries" disabled />
      </LaterPhaseSection>
    </Stack>
  );
}

// ── 13. Advanced ────────────────────────────────────────────────────────────

export function AdvancedPanel({ value, onChange }: PanelProps) {
  const zoneWeights = value.zoneWeights ?? { zoneWeights: {}, serpMetricsEnabled: false };
  return (
    <Stack gap="md" data-testid="config-panel-advanced">
      <LaterPhaseSection title="Link equity zone weights" phase={12} feature="DOM-zone weighted equity and SERP metrics (WP-12-04)" testid="config-zone-weights-section">
        <ToggleRow
          label="SERP metrics"
          checked={zoneWeights.serpMetricsEnabled ?? false}
          onChange={(next) => onChange({ ...value, zoneWeights: { ...zoneWeights, serpMetricsEnabled: next } })}
          testid="config-serp-metrics"
          disabled
        />
        <NumberInput label="Main content weight" placeholder="1.0" data-testid="config-zone-weight-main" disabled />
        <NumberInput label="Footer weight" placeholder="0.4" data-testid="config-zone-weight-footer" disabled />
      </LaterPhaseSection>
      <DisabledSection
        title="Per-configuration proxy"
        note="The intercepting proxy engine is delivered (WP-09-03); application-wide proxy settings live in Settings › Proxy. Per-configuration overrides await the WP-09-06 DTO extension (handoff)."
        testid="config-proxy-section"
      >
        <TextInput label="Proxy URL" placeholder="http://127.0.0.1:8080" data-testid="config-proxy-url" disabled />
        <ToggleRow label="Sign requests" checked={false} onChange={() => undefined} testid="config-proxy-signing" disabled />
      </DisabledSection>
      <Text size="xs" c="dimmed" data-testid="config-list-mode-note">
        List-mode audits (explicit URL lists) are chosen at audit start, not per configuration.
      </Text>
    </Stack>
  );
}
