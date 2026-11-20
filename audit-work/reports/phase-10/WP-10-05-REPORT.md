# WP-10-05 completion report — phase 10, wave A

- **Builder:** `cf-wp-10-05`
- **Workpiece:** WP-10-05 (D-021 category moves + scoring, five WAF classes, CR-024 canonical bit, CR-029 redirect toggles, CR-030 deprecated terms, CR-031 image bytes, locked rule keys, wire-conformance contract test, UI-04 analysis-tab toggles)
- **Baseline:** git HEAD `0d3b89b` + coordinator step-0 commits (contracts re-locked `2cb4311`/`0307fe8`, criterion #56 `1e30ab9`); coordinator baseline (ANALYSIS_CATEGORIES 14, guards, FirstRunSeed 14 toggles, REST captureMode/captureSettings/redirectChecks binding, schema v5) verified present and NOT reverted.
- **Status:** COMPLETE. All owned verification green. No commits made (builder rule). Wiring handoff below for the coordinator.
- **Coordinator rulings applied:** `REPLY-TO-WP-10-05-ALT-FINDING.md` (alt takeover approved), `REPLY-TO-WP-10-05-FINDINGS-2.md` (§1 scoped one-file test extension GRANTED and applied; §2 alt design approved; §3 CR-031 artifact-path ruling — no contract gap; §4 legacy redirect constant retained; §5 CS0050 blocker resolved by WP-10-01, retry green).

---

## 1. Files changed

### Owned production code
| File | Change |
|---|---|
| `app/src/backend/CrawlForge.Crawler/Waf/WafBlockPolicy.cs` | **NEW** — five-class soft/hard policy (`WafBlockResponse`, `Classify`, `IsSoftBlock`, `IsHardBlock`, `Resolve`). |
| `app/src/backend/CrawlForge.Crawler/Waf/WafAimdTrigger.cs` | Added `ResolveAction(verdict)` + `RecordVerdict(controller, host, fetch, verdict, observedAt)` (hard block → HostStop WITHOUT recording; soft/unclassified → real AIMD challenge observation). `Record`/`BuildObservation` primitives unchanged (phase-09 `WafAimdTriggerTests` stay green). |
| `app/src/backend/CrawlForge.Crawler/Waf/WafSignatureCatalogue.cs` | Challenge-class asset parse switched `Enum.TryParse` → `EnumWireNames.TryParse<WafChallengeClass>` (hyphenated wire tokens load; legacy PascalCase labels still parse — wire=lowercase CLR for interstitial/botblock). |
| `app/src/backend/CrawlForge.Crawler/Fetching/RedirectAppropriatenessEvaluator.cs` | **REWRITTEN** — flags `None=0/CrossHost=1/TemporaryStatus=2/NonIndexableTarget=4/HttpsDowngrade=8/All=15`; `FromSettings(RedirectCheckSettings?)` (null→All); `RedirectTargetIndexabilityFacts`; `Evaluate(chain, checks, targetFacts)` with CR-024-parity case 3; reasons name case + hop + target. |
| `app/src/backend/CrawlForge.Crawler/Fetching/RedirectFindingAnalyzer.cs` | **REWRITTEN** — `AnalyzeChain(auditId, urlId, originalUrl, chain, finalUrl=null, finalStatusCode=null, checks=All, targetFacts=null)` (engine call source-compatible); per-case findings with locked keys + full evidence; chain Warning kept; violation/fetch-failure analyzers unchanged. |
| `app/src/backend/CrawlForge.Crawler/Fetching/RedirectRuleKeys.cs` | **REWRITTEN** — four case aliases to `FindingRuleKeys`; `ForCase`/`CaseName`; legacy `Inappropriate` constant RETAINED for `EngineServicesModule.cs:270` compile compat (never emitted; drop at reseed). |
| `app/src/backend/CrawlForge.Audit/StructuredData/DeprecatedRichResultList.cs` | **NEW** — bundled VERSIONED list (`Version="2026.11"`): HowTo retired 2023-09-13, FAQPage restricted 2023-08-08; `Find` via `StructuredDataVocabulary.LocalName` (qualified names resolve); never fetched at runtime. |
| `app/src/backend/CrawlForge.Audit/StructuredData/StructuredDataBlockDetail.cs` | Additive optional `DeprecatedRichResultEntry? DeprecatedEntry = null`. |
| `app/src/backend/CrawlForge.Audit/StructuredData/StructuredDataValidator.cs` | Retired types never rich-result-eligible (entry carried on detail); Microdata `itemprop` keys normalized via `LocalName` (URL/prefixed terms resolve like RDFa — CR-030). RDFa extractor already normalized at HEAD (verified, no change). |
| `app/src/backend/CrawlForge.Audit/StructuredData/StructuredDataRuleKeys.cs` | `StructuredDataDeprecatedTerm` aliased to `FindingRuleKeys.StructuredDataDeprecatedTerm`. |
| `app/src/backend/CrawlForge.Audit/StructuredData/StructuredDataRuleAnalyzer.cs` | `SupportedRuleKeys` += deprecated-term; per-listed-block Warning with full evidence (type/format/ordinal/status/since/listVersion/richResultEligible/note/reason); retired types get a special-cased ineligible reason. |
| `app/src/backend/CrawlForge.Audit/StructuredData/HreflangAnalyzer.cs` | Finding category → `AuditCategory.Internationalization` (D-021). |
| `app/src/backend/CrawlForge.Audit/StructuredData/HreflangReciprocityValidator.cs` | Finding category → `AuditCategory.Internationalization` (D-021; hreflang-no-return trap producer). |
| `app/src/backend/CrawlForge.Audit/Indexability/IndexabilityStateEvaluator.cs` | `EvaluateCanonical`: single canonical elsewhere → `IndexabilityFlags.CanonicalElsewhere` (16); multiple targets → bit when any ≠ page; invalid/unresolvable → reason only, NO bit (CR-024). |
| `app/src/backend/CrawlForge.Audit/ContentQuality/ImageRuleAnalyzer.cs` | **REWRITTEN** — ALL findings `AuditCategory.Images`; alt takeover reusing internal `ImageAltRule` + `AccessibilityRuleContext` (zero drift, identical camelCase `AccessibilityEvidence`, Error severity); CR-031 transferred-bytes measurement (data-uri offline; stored NetworkWaterfall artifacts via injected `IReadRepository<ContentArtifactDto>` + `IContentStore`); unknown size → `image.oversized` Notice `measurement="not-measured"` (NEVER pass); `ImageRuleKeys.Oversized/Format` now alias `FindingRuleKeys.ImageOversized/ImageFormat` (present in re-locked catalog); `ImageAlt` = exact `AccessibilityRuleKeys.ImageAlt` string. |
| `app/src/frontend/src/surfaces/project/configurationModel.ts` | `RedirectChecksFormValue` + `REDIRECT_CHECKS_ALL_ENABLED` + `REDIRECT_CHECK_CASES` + `redirectChecksFromWire/ToWire` + `toggleRedirectCheck`; form field `redirectChecks`; defaults all-true; fromWire absent/null/missing-field → all ON; create/update emit null when all-enabled else explicit four-field document. |
| `app/src/frontend/src/surfaces/project/editorPanels.tsx` | `CategoriesPanel` += "Inappropriate redirect checks (CR-029)" FieldSection with four ToggleRows (`config-redirect-check-{crossHost,temporaryStatus,nonIndexableTarget,httpsDowngrade}`). CrawlAsPanel untouched (extensible for coordinator's WP-10-02 capture tab). |

### Owned tests
| File | Change |
|---|---|
| `app/tests/CrawlForge.Crawler.Tests/Waf/WafBlockPolicyTests.cs` | **NEW** — five-class classify theories; soft/hard membership; Unknown conservative; wire tokens (6); `RecordVerdict` soft→real AIMD MD (`waf-soft-block`) for both soft classes, hard→HostStop no adjustment (3 classes); healthy normal; Retry-After pause; evaluator classifies new soft classes via `InMemoryWafCatalogue`; `WafCatalogueWireTokenTests` loads hyphenated + legacy tokens through the REAL `WafSignatureCatalogue` over a temp asset. |
| `app/tests/CrawlForge.Crawler.Tests/Fetching/RedirectFindingAnalyzerTests.cs` | **REWRITTEN** — per-case keys/evidence (case, hopCount, chain, finalUrl, triggeringHop, reasons); toggle-flip test (4 findings → each `All & ~flag` leaves exactly 3, own key absent); `FromSettings` null/AllEnabled/AllDisabled/per-field; null-settings end-to-end all ON; case-3 theories (meta/header noindex, robots, canonical-elsewhere fire; self/absent/trailing-slash silent; no terminal facts silent); legacy key never emitted; violation/fetch-failure kept. |
| `app/tests/CrawlForge.Audit.Tests/Indexability/IndexabilityStateEvaluatorTests.cs` | CanonicalElsewhere section: bit==16; elsewhere theories (absolute/relative/case+slash); self/absent no bit; multiple-with-elsewhere; invalid canonicals NO bit; composes with Noindex (exact flags); Link-header canonical path. Existing AllDisqualifiers expectation updated. |
| `app/tests/CrawlForge.Audit.Tests/StructuredData/StructuredDataDeprecatedTermsTests.cs` | **NEW** — list versioned + qualified-name resolution; HowTo valid-but-ineligible; FAQPage restricted stays eligible; Microdata URL/prefixed itemprops resolve; RDFa HowTo retired; analyzer locked-key Warning + evidence; invalid+deprecated coexist; SupportedRuleKeys contains locked key. |
| `app/tests/CrawlForge.Audit.Tests/StructuredData/HreflangAnalyzerTests.cs` | Category assertion → Internationalization. |
| `app/tests/CrawlForge.Audit.Tests/StructuredData/HreflangReciprocityRuleAnalyzerTests.cs` | Category assertion → Internationalization (hreflang-no-return phase-gate stand-in). |
| `app/tests/CrawlForge.Audit.Tests/StructuredData/StructuredDataRuleAnalyzerTests.cs` | SupportedRuleKeys list += `StructuredDataDeprecatedTerm`. |
| `app/tests/CrawlForge.Audit.Tests/Scoring/AuditScoreWireConformanceTests.cs` | **NEW** — required `contract_tests` item (see §3.8). |
| `app/tests/CrawlForge.Audit.Tests/Scoring/ImagesCategoryComponentTests.cs` | **NEW** — D-021 missing-alt-under-Images + CR-031 component stand-ins with FAKE stored artifacts (see §3.6/§3.9). |
| `app/tests/frontend/project/wireConformance.test.ts` | Create-payload exact-keys list += `redirectChecks`; absent-document all-ON assertion; NEW describe: defaults, null-collapse, per-toggle create+update round-trip through real consumer, independent flips + all-off document. |
| `app/tests/frontend/project/configurationEditor.test.tsx` | NEW describe: four toggles render ON in Analysis categories tab; each toggle flips exactly its own case on the emitted wire (others stay true) and back to null; explicit wire document loads with one case disabled. |

### Coordinator-granted extension (REPLY-TO-WP-10-05-FINDINGS-2 §1 — ONLY this file, ONLY the five itemized assertions)
| File | Change |
|---|---|
| `app/tests/CrawlForge.Audit.Tests/ContentQuality/ImageRuleAnalyzerTests.cs` | (1) `RealFixtureImages_EmitLegacyFormatNotices`: Performance→Images; `DoesNotContain(Oversized)` → the 2 not-measured Notices (CR-031 behavior correction, cited in comment). (2) `OversizedBytes_EmitWarning_WithMeasurement`: Performance→Images. (3) `ModernFormats_EmitNothing` and (4) `UnknownFormat_EmitsNothing`: `Assert.Empty` → one `image.oversized` Notice `measurement="not-measured"` (CR-031: never a silent pass). (5) `SmallDataUri_EmitsFormatOnly`: accounts for the alt-takeover `accessibility.image-alt` (Error, Images) on the alt-less img. Stale class-doc category sentence updated to D-021. No deletions, no weakening beyond spec-mandated behavior. Verified 24/24 green. |

**NOT touched:** Contracts, frontend contracts, architecture, buildplan, register, decisions-log, SOURCE-OF-TRUTH-LOOP, expected-results.json, CODEMAP, sln, DI/composition, Host/Execution, Audit/Pipeline, App.tsx, store, shell, IssueExplorer.tsx, capture tab, other builders' code. (`LoopbackInterceptingProxy.cs`, `Rendering/*`, `Proxy/*` working-tree changes are WP-10-01/02's, not mine.)

---

## 2. Deliverable-by-deliverable

### 2.1 D-021 category moves — all three SCORED
- **Images:** every `ImageRuleAnalyzer` finding (oversized, format, alt) stamps `AuditCategory.Images`.
- **Internationalization:** `HreflangAnalyzer` + `HreflangReciprocityValidator` stamp `AuditCategory.Internationalization`.
- **SecurityAndProtocol:** NO cookie-security/protocol producer exists anywhere in the backend (verified by exhaustive grep — no `security.*`/`protocol.*`/cookie-attribute rule keys or analyzers). There is nothing to move; the category is enum-member 12, seeded enabled in FirstRunSeed, and SCORED end-to-end (proven by `AuditScoreWireConformanceTests` with a representative finding). Future producers must stamp `SecurityAndProtocol`; the §7.4 row (mixed content, non-HTTPS, insecure forms, security headers, HTTP→HTTPS redirect, cookie attributes, information disclosure) is unbuilt scope outside this workpiece's owned paths.
- **Scoring is enum-driven — verified, not hardcoded:** `AuditScoringService.ComputeAsync` iterates `Enum.GetValues<AuditCategory>()` (14 automatic); `ParseEnabledCategories` absent/null/empty → all 14; `FindingEmitter.cs:60` persists `draft.Category` VERBATIM (catalog only enriches priority/difficulty evidence, :139–144), so producer stamps are authoritative immediately. No production scoring change was needed.

### 2.2 Five WAF classes — soft→AIMD, hard→host stop
- `WafBlockPolicy.Classify`: ManagedChallenge/RateLimitSoftBlock → `AimdThrottle`; Captcha/Interstitial/BotBlock → `HostStop`; Unknown → `AimdThrottle` (conservative phase-09 baseline — never an implicit host stop).
- `WafAimdTrigger.RecordVerdict` is the engine-facing API: soft/unclassified record the real challenge observation (`HostResponseSample.IsChallenge=true` → controller MD on rate+concurrency, trigger `waf-soft-block`, cooldown D, Retry-After pause max(retry-after, D)); hard returns `HostStop` WITHOUT touching the controller (host stop takes precedence; engine owns the stop).
- Wire tokens: NEW classes hyphenated via `EnumWireNames` (`managed-challenge`, `rate-limit-soft-block`); EXISTING `interstitial`/`botblock` wires LOCKED lowercase (PascalCase CLR names). Catalogue asset parse now uses `EnumWireNames.TryParse` — hyphenated asset entries load; legacy PascalCase labels still parse (case-insensitive wire match).
- Bundled asset `app/assets/waf-signatures/waf-catalogue.json` currently carries only hard classes + Unknown (8 entries verified). Soft-class signature entries = coordinator/asset-owner handoff (§4.6); the parse path is ready and proven by `WafCatalogueWireTokenTests` through the real catalogue.

### 2.3 CanonicalElsewhere bit (CR-024)
`IndexabilityStateEvaluator.EvaluateCanonical` sets `IndexabilityFlags.CanonicalElsewhere` (16) when the resolved canonical points elsewhere (single target ≠ normalized page; multiple targets with any ≠ page). Invalid/unresolvable canonicals add a reason but NO bit (not evidence of elsewhere). Composes with Noindex/Nofollow/RobotsBlocked/NonIndexableStatus; normalization TrimEnd('/') + case-insensitive host. Link-header canonical path covered.

### 2.4 CR-029 four redirect toggles
- Evaluator flags extended (`NonIndexableTarget=4`, `HttpsDowngrade=8`, `All=15`, stale comment fixed); `FromSettings(RedirectCheckSettings?)` maps the locked wire record (null → All).
- Case 3 uses CR-024 SEARCH-indexability parity: final status non-200, robots blocked, meta/X-Robots-Tag noindex, canonical elsewhere (self/absent/trailing-slash-equivalent canonical = indexable).
- `RedirectFindingAnalyzer.AnalyzeChain` emits one finding per triggered case with locked keys `redirects.inappropriate-{cross-host,temporary-status,non-indexable-target,https-downgrade}`; evidence names the case, hop count, full chain, final URL/status, the triggering hop (ordinal, from, to, status) and human reasons. Chain Warning (2+ hops) retained.
- Engine call `AuditExecutionEngine.cs:1333–1334` stays source-compatible (Host build: 0 warnings/0 errors). Settings + target-facts wiring = coordinator (§4.3); until wired, the status-only terminal fallback fires case 3 on non-200 targets and all four checks default ON.
- Frontend: `redirectChecks` form field + wire round-trip (null sentinel when all-enabled, explicit document otherwise); four ToggleRows in UI-04 "Analysis categories" tab; component tests show EACH toggle flipping exactly its own case on the emitted wire and back.

### 2.5 CR-030 deprecated terms + retired/restricted rich-result list
- `DeprecatedRichResultList` embedded in `Audit/StructuredData`, VERSIONED (`2026.11`), entries carry Type/Status/Since/Note; HowTo retired (2023-09-13), FAQPage restricted (2023-08-08). NEVER fetched at runtime (no network code path).
- Retired types: valid blocks become rich-result-INELIGIBLE with the entry on the detail and a retired-specific reason; restricted types stay eligible.
- Microdata `itemprop` URL/prefixed terms resolve via `StructuredDataVocabulary.LocalName` exactly like RDFa (RDFa already normalized at HEAD — verified).
- `structured-data.deprecated-term` Warning (locked `FindingRuleKeys` alias) per listed block with evidence: url, format, ordinal, type, status, since, listVersion, richResultEligible, note, reason.

### 2.6 CR-031 oversized-image transferred bytes
- Measured sources: inline data-URI decoded bytes (`bytesSource="data-uri"`, fully offline); stored `ContentArtifactKind.NetworkWaterfall` artifacts (`bytesSource="network-waterfall"`) read through injected `IReadRepository<ContentArtifactDto>` + `IContentStore` (paged ListQuery auditId+urlId+kind=3; JSON array of `RenderedNetworkEntry` OR object wrapper `networkWaterfall`/`entries`, case-insensitive; `TransferredBytes>0` first-wins; src resolved against `Url.CanonicalUrl`).
- measured > threshold → Warning `measurement="measured"` + bytesSource + bytes; measured ≤ threshold → NO finding (genuine pass); unknown → Notice `measurement="not-measured"` with reason — NEVER a Pass-severity finding, NEVER silent.
- **Coordinator ruling §3 applied:** `ProxyCapturedRequest` lacks bytes (locked) — CR-031 http-mode measurement flows via NetworkWaterfall-kind PROXY artifacts (coordinator wiring convention, `REPLY-TO-WP-10-01-PROXY-ARTIFACTS.md`), not via the context record. No contract members added. My reader accepts the array form the coordinator instructed WP-10-01 to persist.
- DI change (new optional ctor params) = handoff §4.1; tests use FAKE stored artifacts (no real DB/store needed).

### 2.7 Locked rule keys
Builder-local keys replaced with `FindingRuleKeys` constants wherever they exist there: four redirect case keys, `structured-data.deprecated-term`, `image.oversized`, `image.format` (the latter two appeared in the re-locked catalog and are now aliased). `accessibility.image-alt` kept as the EXACT `AccessibilityRuleKeys.ImageAlt` string per coordinator ruling (not a FindingRuleKeys member — stability wins). `redirects.loop`/`redirects.chain` stay local (not in FindingRuleKeys).

### 2.8 Contract test `AuditScoreWireConformanceTests` (owned `Audit.Tests/Scoring/`)
- `AuditCategory` 14 members, locked values 11/12/13, lowercase wires, full round-trip through `EnumWireNames` (the single wire authority Api's `ApiEnumConverter` delegates to).
- `WafChallengeClass` five classes + Unknown, hyphenated new wires, locked interstitial/botblock, full round-trip.
- `IndexabilityFlags.CanonicalElsewhere == 16`, composes (Noindex|CanonicalElsewhere == 17).
- Enum-driven scoring: absent/null snapshot → 15 drafts (14 categories + overall), all 100 with no findings; enabledCategories accepts new members by name AND number.
- REAL producer → persistence → scoring → read-back: image-alt (Images/Error) + hreflang-invalid-code (Internationalization/Warning) + SecurityAndProtocol finding → scores 75/92.5/92.5 (capacity 40) → `AuditScoreRecorder.RecordAsync` → `GetScoresAsync` category ints map back to locked tokens.
- URL wire payload: CanonicalElsewhere bit round-trips through `SqlRepository<UrlRecordDto>`; IsIndexable=false.
- Locked redirect case key strings + deprecated-term key asserted verbatim.

### 2.9 Phase-09 trap stand-ins (component tests)
- **missing-alt under Images:** `ImagesCategoryComponentTests.MissingAlt_EmitsLockedKeyUnderImages_WithAccessibilityEvidence` (exact key, Error, full accessibility evidence shape) + exemption theories + input/area coverage + rendered-only + all-family-category test.
- **hreflang-no-return under Internationalization:** `HreflangReciprocityRuleAnalyzerTests` category assertion (real reciprocity validator, fixture trap markup).
- The phase gate re-runs 17/17 + UI-11/UI-12 after coordinator wiring; these stand in until then (WAVE-10A-NOTES §184).

---

## 3. Verification — exact commands and output

All dotnet commands run with `$env:CF_AGENT_ID='cf-wp-10-05'` and `$env:MSBUILDDISABLENODEREUSE='1'` from `E:\Crawlforge\app`. Only filtered test projects; never the whole solution.

### 3.1 Audit.Tests (owned folders + granted image-test extension)
```
dotnet test tests\CrawlForge.Audit.Tests --filter "FullyQualifiedName~StructuredData|FullyQualifiedName~Scoring|FullyQualifiedName~IndexabilityStateEvaluator|FullyQualifiedName~ImageRuleAnalyzer" --nologo -v minimal
```
```
Passed!  - Failed:     0, Passed:   326, Skipped:     0, Total:   326, Duration: 4 s - CrawlForge.Audit.Tests.dll (net10.0)
[exit code: 0]
```
(302 StructuredData|Scoring|IndexabilityStateEvaluator + 24 ImageRuleAnalyzer. Earlier run before the granted extension: 302/302 after the SupportedRuleKeys fix; first run surfaced exactly that one owned-list assertion, fixed.)

### 3.2 Crawler.Tests (Waf + RedirectFinding)
```
dotnet test tests\CrawlForge.Crawler.Tests --filter "FullyQualifiedName~Waf|FullyQualifiedName~RedirectFinding" --nologo -v minimal
```
```
Passed!  - Failed:     0, Passed:   136, Skipped:     0, Total:   136, Duration: 4 s - CrawlForge.Crawler.Tests.dll (net10.0)
[exit code: 0]
```
First attempt was blocked by WP-10-01's uncommitted `FakeRenderHarness.cs(29,37) CS0050` (NOT my code; reported, left untouched). WP-10-01 fixed it in their lane; retry green. Includes phase-09 `WafAimdTriggerTests`, `WafEvaluatorTests`, `WafCatalogueTests` — all still passing.

### 3.3 Host compile (engine call-site source compatibility)
```
dotnet build src\backend\CrawlForge.Host --nologo -v minimal
```
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
[exit code: 0]
```
Proves `AuditExecutionEngine.cs:1333` AnalyzeChain call and `EngineServicesModule.cs:270` legacy-constant seed compile against the rewritten analyzer/keys.

### 3.4 Frontend tests (from `app/src/frontend`)
```
npm run test -- --run tests/frontend/project --testTimeout=30000
```
```
 ✓ ../../tests/frontend/project/configurationsList.test.tsx (10 tests)
 ✓ ../../tests/frontend/project/configurationEditor.test.tsx (21 tests)
   ✓ CR-029 redirect check toggles (WP-10-05, analysis tab) (3)
     ✓ renders all four checks ON by default in the Analysis categories tab
     ✓ each toggle flips exactly its own case on the wire (its finding on/off)
     ✓ loads an explicit wire document with one case disabled
 ✓ ../../tests/frontend/project/projectOverview.test.tsx
 ✓ ../../tests/frontend/project/wireConformance.test.ts
 Test Files  4 passed (4)
      Tests  56 passed (56)
[exit code: 0]
```
No contention flakes encountered; no retries needed.

### 3.5 Frontend typecheck + build (from `app/src/frontend`)
```
npm run typecheck   → tsc --noEmit [exit code: 0]
npm run build       → vite v8.3.8 … ✓ 1586 modules transformed … ✓ built in 2.16s [exit code: 0]
```
(Pre-existing >500 kB chunk-size advisory only; not a failure.)

---

## 4. WIRING HANDOFF (coordinator applies — all outside my owned paths)

### 4.1 DI — ImageRuleAnalyzer artifact services + alt-rule disable
`app/src/backend/CrawlForge.Host/Composition/EngineServicesModule.cs`

**:183** (inside the `IAuditRuleAnalyzer` factory) — pass the registered artifact services (both already in DI: `CoreServicesModule.cs:86` `IContentStore`, `:102` `AddRepository<ContentArtifactDto>`):
```csharp
var images = new ImageRuleAnalyzer(
    artifacts: p.GetRequiredService<IReadRepository<ContentArtifactDto>>(),
    contentStore: p.GetRequiredService<IContentStore>());
```
Without this, CR-031 network images stay "not measured" (honest fallback; data-URIs still measured).

**:176** — disable the production accessibility alt rule so the finding is emitted EXACTLY ONCE (under Images by ImageRuleAnalyzer):
```csharp
var accessibility = new AccessibilityAnalyzer(new AccessibilityAnalyzerOptions
{
    RuleOverrides = new Dictionary<string, AccessibilityRuleOverride>(StringComparer.Ordinal)
    {
        [AccessibilityRuleKeys.ImageAlt] = new(Enabled: false), // D-021 takeover (WP-10-05)
    },
});
```
**Single-page path:** `SinglePageAnalyzer` (registered :225) runs the SHARED `IAuditPipeline` → the same `AuditRuleHost` (:186–197). There is NO second AccessibilityAnalyzer construction anywhere (verified by grep) — the one disable at :176 covers both audit and single-page paths. Until disabled, alt findings appear twice (Accessibility + Images).

### 4.2 Rule-catalog reseed
`AuditRuleCatalog` seeds live in TWO places: constructor `app/src/backend/CrawlForge.Audit/Pipeline/RuleDefinition.cs:50–85` and composition `EngineServicesModule.cs:258+`. `FindingEmitter` persists the producer's category verbatim (:60); the catalog supplies priority/difficulty evidence (:139–144) — reseed is for metadata/UI completeness, category correctness already flows from producers.

**Add (key / category / severity / priority / difficulty):**
| Key | Category | Severity | Priority | Difficulty |
|---|---|---|---|---|
| `redirects.inappropriate-cross-host` | Indexability | Warning | 60 | low |
| `redirects.inappropriate-temporary-status` | Indexability | Warning | 60 | low |
| `redirects.inappropriate-non-indexable-target` | Indexability | Warning | 60 | low |
| `redirects.inappropriate-https-downgrade` | Indexability | Warning | 60 | low |
| `structured-data.deprecated-term` | StructuredData | Warning | 55 | low |
| `image.oversized` | Images | Warning | 60 | low |
| `image.format` | Images | Warning | 50 | low |
| `accessibility.image-alt` | Images | Error | 85 | low |

(Priorities mirror the legacy `redirects.inappropriate` seed 60/low at EngineServicesModule:270–272 and phase-09 conventions; adjustable by coordinator. `image.oversized`/`image.format` also emit Notice severity variants — catalog DefaultSeverity is the Warning ceiling. `accessibility.image-alt` may already be seeded under Accessibility by the accessibility family — reseed/replace category to Images.)

**Drop:** legacy `redirects.inappropriate` seed (EngineServicesModule:270–272) once nothing references it; the constant stays in `RedirectRuleKeys` until then (compile compat).

**Move (D-021, match producers):** `i18n.hreflang-*` seeds → Internationalization; `i18n.hreflang-conflict` producer `HeaderHtmlEquivalencyAnalyzer.cs:237` currently stamps `AuditCategory.StructuredData` → change to `Internationalization`, and catalog seed `RuleDefinition.cs:84` (`HreflangHeaderHtmlConflict`, currently Indexability) → Internationalization. Both files unowned by me.

### 4.3 Engine — redirect settings + target facts
`app/src/backend/CrawlForge.Host/Execution/AuditExecutionEngine.cs:1333–1334` currently:
```csharp
var redirectFindings = ctx.RedirectFindings.AnalyzeChain(
    auditId, urlId, entry.Url, chain, outcome.FinalUrl, outcome.FinalStatusCode);
```
Wire to:
```csharp
var redirectFindings = ctx.RedirectFindings.AnalyzeChain(
    auditId, urlId, entry.Url, chain, outcome.FinalUrl, outcome.FinalStatusCode,
    checks: RedirectInappropriatenessChecksExtensions.FromSettings(configuration.RedirectChecks),
    targetFacts: /* RedirectTargetIndexabilityFacts from the FINAL target's fetched record:
                    FinalStatusCode, FinalUrl, RobotsBlocked, MetaRobotsNoindex,
                    HeaderRobotsNoindex, CanonicalHrefs */);
```
Types: `CrawlForge.Crawler.Fetching.RedirectInappropriatenessChecksExtensions.FromSettings(RedirectCheckSettings?)` (null→All), `RedirectTargetIndexabilityFacts(int? FinalStatusCode, string? FinalUrl=null, bool RobotsBlocked=false, bool MetaRobotsNoindex=false, bool HeaderRobotsNoindex=false, IReadOnlyList<string>? CanonicalHrefs=null)`. The call precedes document parse today — case-3 noindex/canonical facts need the terminal target's parsed directives (when the terminal is the page itself, its own parse later in the loop can re-evaluate; coordinator decides). Until wired: all four checks ON (null convention) and case 3 fires on non-200 terminal status via the built-in fallback.

### 4.4 Engine — WAF five-class politeness wiring
`AuditExecutionEngine.cs:1315–1328` (WAF record path) currently records/reports only (exclusion E3). Add the D-021 response:
```csharp
var response = WafAimdTrigger.RecordVerdict(state.Aimd, host, fetch, waf, DateTimeOffset.UtcNow);
if (response == WafBlockResponse.HostStop) { /* stop the host via the frontier — no further fetches; blocked URL never re-fetched */ }
```
Types: `CrawlForge.Crawler.Waf.WafAimdTrigger.RecordVerdict(AimdPolitenessController, string host, HttpFetchResult, WafEvaluationResult, DateTimeOffset)` → `WafBlockResponse`; `WafBlockPolicy.Resolve/Classify/IsSoftBlock/IsHardBlock`. `state.Aimd` is the per-audit controller (:836–841). Soft blocks produce the existing locked host-throttle event shape (trigger `waf-soft-block`) through the controller's adjustment sink — no new wire shape. Detection/reporting (:1317–1327) stays as-is. Host-stop mechanism (frontier host exclusion) is coordinator-owned.

### 4.5 IssueExplorer wire category list (one line)
`app/src/frontend/src/surfaces/explorers/issues/IssueExplorer.tsx:57–60`:
```ts
export const CATEGORY_WIRE_KEYS = [
  'indexability', 'content', 'linking', 'structureddata', 'accessibility',
  'performance', 'crawl', 'custom', 'logintelligence', 'aisearchgeo', 'analyticstagging',
  'images', 'securityandprotocol', 'internationalization'   // += D-021 (WP-10-05 handoff)
] as const;
```

### 4.6 WAF catalogue asset upgrade
`app/assets/waf-signatures/waf-catalogue.json` — add soft-class signature entries with `"challengeClass": "managed-challenge"` / `"rate-limit-soft-block"` (hyphenated wires; parse path ready and test-proven). Current 8 entries are hard classes + Unknown only. Asset ownership = coordinator/asset owner.

### 4.7 Score registration
NONE needed — scoring is enum-driven (verified §2.1); `AuditScoringService`/`AuditScoreRecorder` already handle all 14 categories and persist category ints. UI-11/UI-12 score surfaces carry 14 categories via the committed ANALYSIS_CATEGORIES; dashboard label mapping (`ReportDashboard.tsx:202/:223+`) is coordinator-owned per STEP-4-WIRING-PLAN §6.

### 4.8 WP-10-02 capture tab
CrawlAsPanel left extensible (untouched). Coordinator applies 02's capture-tab handoff after this workpiece; `configurationModel.ts` already round-trips `captureMode`/`captureSettings` wire fields via the locked `CaptureConfigurationFields` (my `redirectChecks` additions sit alongside; create-payload exact-keys test updated accordingly).

---

## 5. Blockers / deviations

1. **WP-10-01 CS0050 compile blocker — RESOLVED.** First Crawler.Tests run failed on `Rendering/Harness/FakeRenderHarness.cs(29,37)` (01's uncommitted WIP; `BrowserLease` internal). Reported to coordinator, left untouched; 01 fixed it in their lane; retry 136/136 green.
2. **SecurityAndProtocol has no producer** (verified exhaustive grep). Nothing to move; category scored and wire-ready via conformance test. The §7.4 security row is unbuilt scope outside WP-10-05 ownership — flag for a future workpiece.
3. **CR-031 http-mode proxy bytes** — per coordinator ruling §3, NOT a contract gap: measurement flows via NetworkWaterfall-kind proxy artifacts (01 wiring convention), not the locked `ProxyCapturedRequest` record. My reader accepts the instructed JSON-array shape (and wrappers). If 01's final storage shape differs, the reader's wrapper tolerance covers `networkWaterfall`/`entries` object forms; coordinator verifies at wiring.
4. **Granted test extension applied** — `ImageRuleAnalyzerTests.cs` five itemized assertions updated exactly per REPLY-TO-WP-10-05-FINDINGS-2 §1 (diff reviewable against that list); 24/24 green. Stand-in tests remain in owned `Scoring/` as planned.
5. **Legacy `redirects.inappropriate` constant retained** (compile compat for EngineServicesModule:270); never emitted; drop the seed at reseed (§4.2), then the constant can go.
6. **No commits** (builder rule); working tree carries my changes plus other builders' WIP and coordinator ledger edits — lane-checked, only owned paths + the one granted file modified by me.
7. **done_when fixture/UI checks** run at the PHASE GATE after coordinator wiring (AGENT-INSTRUCTIONS §5.6); component tests stand in until then — all green now.
