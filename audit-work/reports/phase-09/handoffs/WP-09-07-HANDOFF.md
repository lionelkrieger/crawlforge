# WP-09-07 MOUNTING HANDOFF — Project Overview, Configurations & Configuration Editor

Builder: cf-wp-09-01 (reused) · Wave 9B · Components + component tests ONLY.
All files live in the owned paths `app\src\frontend\src\surfaces\project\` and
`app\tests\frontend\project\`. Nothing is mounted yet — the lead owns the shell.

## 1. Exports (barrel: `surfaces/project/index.ts`)

| Export | Kind | Purpose |
|---|---|---|
| `ProjectOverview` | component | UI-02 project overview |
| `ConfigurationsList` | component | UI-03 configurations CRUD surface (embeds the editor for new/open) |
| `ConfigurationEditor` | component | UI-04 13-tab editor, self-contained, props-driven |
| `configurationModel` helpers | functions/types | `defaultConfigurationForm`, `configurationFromWire`, `configurationToCreateRequest`, `configurationToUpdateRequest`, `CONFIGURATION_TABS`, `TAB_IDS`, `ANALYSIS_CATEGORIES`, `DEFAULT_SCOPE_RULES`, `DEFAULT_AIMD`, `DEFAULT_LIMITS`, validation helpers |

### Props

```ts
ProjectOverviewProps {
  project: ProjectSummary;                    // contracts/api/projects
  audits: readonly AuditSummary[];            // contracts/api/audits
  configurations: readonly ConfigurationSummary[];
  storage?: { databaseLocation?, contentStoreLocation?, compressionEnabled? } | null;
  retention?: { keepLastAudits?: number|null, keepDays?: number|null } | null;
  onNavigate?: (section: 'configurations'|'integrations'|'segments'|'audits'|'logs') => void;
  onStartAudit?: (configurationId: string) => void;
  onOpenAudit?: (auditId: string) => void;
  startingAudit?: boolean; startError?: string|null;
}

ConfigurationsListProps {
  projectId: string;
  api: LoopbackApiClient | null;              // pass services.api (shell-owned client)
}

ConfigurationEditorProps {
  value: ConfigurationFormValue;              // wire DTO fields + additive scopeRules/enabledCategories
  onChange: (next: ConfigurationFormValue) => void;
  projectId?: string;
}
```

No shell imports anywhere; the editor fetches nothing and renders synchronously
(walker dumps 500 ms after the modal opens — safe).

## 2. Where the lead mounts (walker contract, ui_walker.py read-only)

- **UI-02** — on `project-row-{id}` click (and dblclick / command-palette pick)
  in HubDashboard, render `<ProjectOverview …/>`. Root testid
  **`project-overview`** satisfies the walker outright; the dump also contains
  the exact-text buttons **`Configurations`** and **`Integrations`**
  (`overview-nav-configurations`, `overview-nav-integrations`). Wire
  `onNavigate('configurations')` → ConfigurationsList surface;
  `onStartAudit` → `audits.start` (`POST /api/audits {configurationId}`);
  `onOpenAudit` → the audit run view. Feed `audits` from
  `audits.list?projectId={id}` and `configurations` from `configurations.list`.
- **UI-03** — the walker checks the **HUB** dump for "Configurations"
  (text/buttons/testids). That hub affordance is LEAD wiring (HubDashboard is
  shell-owned): add a hub button/section labelled exactly `Configurations`
  opening `<ConfigurationsList projectId=… api={services.api}/>`. The list
  itself carries the `Configurations` heading (`configurations-heading`), the
  exact-text **`New configuration`** button (`configurations-new`) and per-row
  **`Edit`** / `Duplicate` / `Delete` affordances.
- **UI-04** — embed `<ConfigurationEditor value={form} onChange={setForm}/>`
  inside the existing `create-project-modal` (lead-owned). Initialize `form`
  with `defaultConfigurationForm(seedUrlFromProjectUrlInput)`. The walker
  clicks `new-project-button`, waits for `create-project-modal`, waits 500 ms,
  then requires ≥1 of the 13 tab names and >3 visible inputs:
  - all **13 tab labels render VERBATIM** as `role=tab` buttons at all times
    (Mantine Tabs list; inactive panels unmount but labels stay);
  - the **default Scope panel renders 7+ real visible inputs** (start-URLs
    textarea, host-scope select, include/exclude/path-prefix/asset-domain
    textareas, regex-timeout number input) plus the editor name/description
    inputs and the modal's own fields — far above the >3 bar.
  The product path UI-03 → UI-04 also works standalone: ConfigurationsList's
  `New configuration` / `Edit` opens the same editor inline with Save/Cancel.

### Exact testids

- Overview: `project-overview`, `overview-project-name`,
  `overview-nav-{configurations|integrations|segments|audits|logs}`,
  `overview-section-*`, `overview-configuration-{id}`,
  `overview-start-configuration` (Select), `overview-start-audit` (Button),
  `overview-start-error`, `overview-audits-table`,
  `overview-audit-row-{id}`, `overview-audit-state-{id}`,
  `overview-open-audit-{id}`, `overview-audits-empty`,
  `overview-storage-summary`, `overview-retention-policy` (contains the
  literal "Retention policy" — UI-07 marker), `overview-retention-note`,
  `overview-integrations-note`, `overview-connect-gsc`, `overview-connect-ga4`.
- List: `configurations-list`, `configurations-heading`, `configurations-new`,
  `configurations-search`, `configurations-table`, `configurations-empty`,
  `configurations-loading`, `configurations-error`, `configurations-offline`,
  `configuration-row-{id}`, `configuration-name-{id}`,
  `configuration-edit-{id}`, `configuration-duplicate-{id}`,
  `configuration-delete-{id}` → `configuration-delete-confirm-{id}` (two-step),
  `configurations-editor-pane`, `configuration-save`, `configuration-cancel`.
- Editor: `configuration-editor`, `config-name`, `config-description`,
  `config-editor-tabs`, `config-tab-{scope|crawl-as|limits|rules|directives|
  resources|auth|click-discovery|consent|categories|thresholds|extraction|advanced}`,
  `config-panel-{same ids}`, `config-start-urls`, `config-scope-mode`,
  `config-include-patterns`, `config-exclude-patterns`, `config-path-prefixes`,
  `config-asset-domains`, `config-check-external-links`, `config-regex-timeout`,
  `config-crawl-as-preset`, `config-custom-user-agent`,
  `config-simulate-googlebot`, `config-respect-robots`, `config-virtual-robots`,
  `config-ua-comparison-enabled`, `config-ua-comparison-sample{-unlimited|-override}`,
  `config-rendering-section[-note]`, `config-rendering-enabled`,
  `config-limit-{max-urls|max-depth|max-duration|max-response-bytes}{-unlimited|-override}`,
  `config-aimd-{r0|alpha|beta|kappa|ewma}`, `config-limits-pending[-note]`,
  `config-query-policy`, `config-query-whitelist`, `config-trailing-slash`,
  `config-strip-session-segments`, `config-session-parameters`,
  `config-rewrite-{add|pattern-i|replacement-i|enabled-i|remove-i}`,
  `config-hostmap-{add|from-i|to-i|remove-i}`, `config-content-scope-section`,
  `config-follow-page-nofollow`, `config-follow-link-nofollow`,
  `config-crawl-noindex`, `config-canonical-discovery`,
  `config-hreflang-discovery`, `config-meta-refresh`, `config-follow-iframes`,
  `config-fetch-{scripts|stylesheets|images|fonts|other}`,
  **`config-cookie-jar`** (criterion #56 toggle),
  `config-user-cookies-{add|name-i|value-i|remove-i}`,
  `config-custom-headers-{add|name-i|value-i|remove-i}`,
  `config-form-login-section[-note]`, `config-click-discovery-section[-note]`,
  `config-site-types-section[-note]`, `config-consent-section[-note]`,
  `config-consent-mode-v2`, `config-beacon-detection`,
  `config-category-{Indexability|Content|Linking|StructuredData|Accessibility|
  Performance|Crawl|Custom|LogIntelligence|AiSearchGeo|AnalyticsTagging}`,
  `config-thresholds-section[-note]`, `config-extraction-section[-note]`,
  `config-search-section[-note]`, `config-zone-weights-section[-note]`,
  `config-proxy-section[-note]`, `config-list-mode-note`.

## 3. Cookie-jar DTO field (criterion #56) — DISCOVERED

The engine reads **`scopeRules.cookieJar.enabled`** from
`crawl_configurations.scope_rules_json`:
`CrawlScopeConfigParser.ParseCookieJar` (Crawler\Canonicalization\CrawlScopeConfigParser.cs:258)
→ `CookieJarOptions.Enabled` (Crawler\Politeness\AuditCookieJar.cs:12,
**default true**). The toggle binds exactly that path; OFF crawls cookieless
(user-supplied cookies are still sent — explicit credentials, §5.11).

**The current REST DTO does NOT carry it.** `AuditConfigurationWire`
(Api\Controllers\ProjectWireContracts.cs:158) has no scopeRules member; the
controller only writes behavior keys (description/respectRobotsTxt/
virtualRobotsTxt/maxResponseBytes/rendering) into ScopeRulesJson. Per the
brief, the DTO extension is a HANDOFF item (no backend edits here):

### DTO extension needed (lead / WP-09-06)
1. Add additive `scopeRules` (JSON object, camelCase, shape =
   `ScopeRulesDocument` in configurationModel.ts — copied verbatim from the
   parser's documented contract) to `AuditConfigurationWire`,
   `ConfigurationCreateRequest`, `ConfigurationUpdateRequest`.
2. Controller: merge `scopeRules` into `ScopeRulesJson` **preserving the
   behavior keys** (shallow merge at the top level; the parser is total and
   tolerant, invalid values fall back with notes, invalid regexes are hard
   errors at config time). Return it on GET.
3. Add additive `enabledCategories` (`string[]?`, null = all) to the same
   three contracts; persist alongside (e.g. in ScopeRulesJson or a column).
   `AuditsController` snapshot freeze (:144–151) should prefer the
   configuration-level value over the app-settings `AuditTogglesJson`
   fallback (`ResolveEnabledCategoriesAsync`, :371). Engine convention:
   `AuditScoringService.ParseEnabledCategories` (enum names, case-insensitive).
4. Engine consumption at audit start is the WP-09-02 wiring handoff
   (scope_rules_json → CrawlScopeConfigParser → CookieJarMessageHandler +
   fetcher `IFetchHook.AllowRevisit`; see WP-09-01-HANDOFF.md §8 for the
   cookie-gate + recorder composition).

Until then the components already EMIT `scopeRules`/`enabledCategories` on
create/update (additive JSON members are ignored by the current model binder —
safe) and tolerate them on read (`configurationFromWire`).

## 4. apiClient / store additions needed

**None.** ConfigurationsList uses the existing typed routes
(`configurations.list/create/get/update/delete`) through the `api` prop — pass
`services.api`. ProjectOverview is pure props; the lead supplies data from the
store or `audits.list` / `configurations.list` / `projects.get`. No new store
slice is required; if the lead wants cached overview data, that is shell scope.

## 5. Tab → engine/phase map (disabled-with-note, CR-022)

| Tab | Binding | State |
|---|---|---|
| Scope | scopeRules.scope + startUrls | functional (WP-09-02 engine) |
| Crawl as and capture | scopeRules.userAgent/userAgentComparison, respectRobotsTxt/virtualRobotsTxt | functional; **rendering controls disabled — phase 10** |
| Limits | wire limits (null sentinel) + aimd | functional; additional §4.2 ceilings disabled — per-config DTO fields pending (WP-09-06 handoff; engine enforces them today) |
| Rules | scopeRules.canonicalization + wire rewrite | functional; **content-area scoping disabled — phase 12** (WP-12-06 enforcement) |
| Directives | scopeRules.directives | functional |
| Resources and external scope | scopeRules.resources | functional |
| Authentication and cookies | scopeRules.cookieJar/cookies/headers | functional; **form-login fields disabled — DTO pending** (WP-09-03 engine delivered) |
| Click discovery and site types | — | **disabled — phase 11** (WP-11-01/02) |
| Consent states | wire consent | **disabled — phase 11** (WP-11-03) |
| Analysis categories | enabledCategories | functional (scoring convention delivered; DTO additive handoff §3) |
| Thresholds | — | **disabled — phase 12** (WP-12-05 prioritisation) |
| Extraction and search | — | **disabled — phase 12** (WP-12-07) |
| Advanced | wire zoneWeights; proxy | **zone weights disabled — phase 12** (WP-12-04); per-config proxy disabled — DTO pending |

Disabled sections use a native `<fieldset disabled>` AND explicit `disabled`
props (jsdom does not reflect fieldset semantics on `input.disabled`; real
browsers do both). Notes name the feature and phase — never fake placeholders.

## 6. Conformance payloads (lead's ConfigEditorWireConformanceTests / ProjectOverviewWireTests)

- `tests/frontend/project/wireConformance.test.ts` — real producer JSON
  (AuditConfigurationWire camelCase, copied from the Api contracts +
  ContractConformance seed) → `configurationFromWire` → editor →
  `configurationToUpdateRequest`/`configurationToCreateRequest` → JSON. Asserts
  the exact key sets, null-sentinel limits, engine baselines, additive-member
  tolerance, AIMD/limit validation, category convention, preset mapping.
- `tests/frontend/project/helpers.tsx` — exported fixtures:
  `configurationWireFixture`, `configurationSummaryFixture`,
  `projectSummaryFixture`, `auditSummaryFixture`, stateful
  `configurationRoutes()` fake (real route paths + envelope shapes).
- Producer→consumer component proof: `configurationEditor.test.tsx` round-trip
  suite + `configurationsList.test.tsx` CRUD suite (POST/PUT bodies carry
  `scopeRules.cookieJar`).

## 7. Verification (green)

- `cd app\src\frontend && npm run build` → vite v8.3.8, 846 modules,
  ✓ built in 538ms (chunk-size warning pre-existing, informational).
- `cd app && npm run test --workspaces --if-present -- --run tests/frontend/project`
  → **Test Files 4 passed (4), Tests 49 passed (49)** (src/frontend workspace
  discovers tests/frontend via its vitest config; other workspaces: no match,
  exit 0). NOTE: the brief's literal `npm run test -- --run tests/frontend/project`
  fails at the ROOT npm layer (`--run` parsed as an npm config flag,
  EUNKNOWNCONFIG) — the `--workspaces --if-present --` form above is the working
  equivalent; flagging for the lead, no scripts edited.
- `tsc --noEmit`: zero errors in surfaces/project + tests/frontend/project.
  Pre-existing/sibling errors outside owned paths (report-only):
  shell/ErrorBoundary.tsx TS4114 ×3; tests/frontend/report/helpers.ts +
  suppression.test.ts (report builder's scope).

## 8. Interpretations / ambiguities (loop §8)

- Limits tab shows the four wire ceilings + AIMD (kappa = concurrency cap,
  R0 = request rate → CAP-A-019/020). The remaining §4.2 parameters exist in
  the engine but not the per-config DTO → disabled-with-note + §3 handoff,
  never clamped, never magic-number unlimited.
- "Crawl as" presets (§8.8) bundle UA only; viewport/capture-mode bundling
  arrives with the phase-10 rendering engine (device profile control disabled).
  Googlebot Desktop / Chrome strings are local constants (engine bundles
  Smartphone + Chrome; UserAgentProfiles.cs).
- Analysis categories use the PascalCase enum-name keys of the existing
  AuditTogglesJson / ParseEnabledCategories convention (case-insensitive
  parser), null = all enabled.
- Retention display is props-driven; pruning controls are phase-12 (WP-12-10)
  — no schedule fields touched (CR-026).
