# WP-09-02 Wiring Handoff — Cookie Jar, robots Findings, URL Normalization, Crawl Scope & WAF-to-AIMD Trigger

Builder: cf-wp-09-02 · Phase 09 wave 9A · Mission model: components + component tests only (AGENT-INSTRUCTIONS §5.6).
All production code lives in owned paths `CrawlForge.Crawler\{Canonicalization,Politeness,Waf,Limits}`; tests in `CrawlForge.Crawler.Tests\{Canonicalization,Politeness,Waf,Limits}`.
Verification: `dotnet test tests\CrawlForge.Crawler.Tests --filter "FullyQualifiedName~Limits|FullyQualifiedName~Waf|FullyQualifiedName~Politeness|FullyQualifiedName~Canonicalization"` → **271/271 passed**, SDK 10.0.404, `CF_AGENT_ID=cf-wp-09-02`, cwd `E:\Crawlforge\app`.

Engine line references below are against `CrawlForge.Host\Execution\AuditExecutionEngine.cs` as read this session (1692 lines). The coordinator owns all wiring (WP-09-11 serial wiring); nothing here is registered in DI or invoked by the running app yet.

---

## 1. Per-audit configuration binding (G27 root cause)

The engine currently uses the DI singleton `IUrlCanonicalizer` (default options) — G27 is that the configured options never reach it. Bind per audit:

```csharp
// In RunAsync, before seed canonicalization (engine ~:597):
var scopeParse = CrawlScopeConfigParser.Parse(configuration.ScopeRulesJson);
foreach (var note in scopeParse.Fallbacks)
    _logger.LogWarning("Audit {AuditId}: scope config fallback - {Note}", auditId, note);
if (scopeParse.Errors.Count > 0)
    throw new InvalidOperationException("Invalid crawl scope configuration: " + string.Join("; ", scopeParse.Errors));
var scopeConfig = scopeParse.Config;

var canonicalizer = new UrlCanonicalizer(scopeConfig.Canonicalization);   // per-audit, replaces _canonicalizer uses at :639 and in DiscoverLinksAsync
var scopeEvaluator = new CrawlScopeEvaluator(scopeConfig.Scope, seedHosts, scopeConfig.Resources);
```

- `CrawlScopeConfigParser.Parse` is total-parse (AimdSettingsParser discipline): null/empty/invalid JSON → baseline + Fallbacks notes; invalid regex patterns and non-positive `userAgentComparison.sampleSize` → hard `Errors` (reject the configuration at API validation too).
- Payload rides the **existing** `crawl_configurations.scope_rules_json` column — no schema change. Full shape documented in `CrawlScopeConfigParser.cs` header and proven in `CrawlScopeConfigParserTests.FullPayloadRoundTrips`.
- `canonicalization.stripCookielessSessionSegments` defaults **true** (CR-009 baseline): `/(S(...))/`, `/(A(...))/`, `/(F(...))/` and percent-encoded forms removed mid-path, root collapses to `/`, idempotent.
- Session-segment reporting (fixture trap "and reported"): use `canonicalizer.CanonicalizeDetailed(href, documentBase)`; when `outcome.RemovedSessionSegments.Count > 0`, emit `CanonicalizationFindingFactory.CreateSessionSegmentFinding(urlId, outcome)` (Notice/Indexability, key `canonicalization.session-segment-removed`). Recommend emitting once per canonical URL at record creation, deduped, not per discovering link.

## 2. Cookie jar + CookieJarMessageHandler hookup (engine :601-605)

```csharp
// Per-audit jar (default ON, §5.11):
var jar = new AuditCookieJar(new CookieJarOptions { Enabled = scopeConfig.CookieJar.Enabled });

// Merge user-supplied cookies (configuration.CookiesJson, decrypted through the
// existing sensitive-value path) per seed origin:
foreach (var seed in seeds)
    jar.MergeCookies(new Uri(seed), userCookiesForOrigin, CookieSource.UserSupplied);

// Wrap the crawl-target client so EVERY redirect hop captures Set-Cookie:
// HttpFetcher follows redirects manually (AllowAutoRedirect=false), so a
// DelegatingHandler sees each hop — the correct hook.
using var httpClient = Composition.CrawlTargetHttpClientFactory.Create(
    TimeSpan.FromSeconds(limits?.FetchTimeoutSeconds ?? 60),
    new CookieJarMessageHandler(jar));   // factory overload needed (cf-wp-09-01 owns Fetching)
```

- **Factory change needed (cf-wp-09-01):** `CrawlTargetHttpClientFactory.Create(TimeSpan timeout, DelegatingHandler? outerHandler = null)` — insert the handler between the primary handler and HttpClient. Alternative without factory change: expose the primary handler and let the engine compose `new HttpClient(new CookieJarMessageHandler(jar, primaryHandler))`.
- `CookieJarMessageHandler` attaches `jar.GetCookieHeader(requestUri)` (removes any existing Cookie header first) and captures response `Set-Cookie` via raw `Headers.TryGetValues` (multi-value safe — `HttpFetchResult.Headers` joins values and is lossy for Set-Cookie).
- Cookieless mode: site cookies neither stored nor replayed; **user-supplied/authenticator cookies are still sent** (explicit credentials). The fixture cookie-gate loops cookieless because `fixture_gate` is site-set.
- §5.11 response-change recording: after each fetch call `jar.NoteResponse(entry.Url, Convert.ToHexString(SHA256.HashData(bodyBytes)))`. A change is recorded when the hash differs AND cookies were set between observations (`jar.ResponseChanges`).
- Cookieless redirect-loop finding: in the fetch-failure catch (engine :695), when the failure is the redirect-loop case, call
  `CookielessRedirectLoopDiagnostic.Create(urlId, entry.Url, redirectChainRecorder.Hops, observedSetCookieNames, jar.Enabled)` → Warning/Indexability key `crawl.cookieless-redirect-loop` (null unless cookieless + Set-Cookie observed + actual loop). **Coordination point (cf-wp-09-01):** `RedirectChainRecorder` should expose the hop list and the Set-Cookie names observed per hop; with the jar disabled the jar itself stores nothing, so the names must come from the recorder.
- Jar teardown: `jar.Clear()` at audit end (per-audit isolation).

### Jar read/merge surface for cf-wp-09-03 (CrawlRequestAuthenticator)

```csharp
IReadOnlyList<CookieRecord> GetCookies();                       // full snapshot, deterministic domain/path/name order, expired excluded
IReadOnlyDictionary<string, string> GetCookieMap(Uri requestUrl); // name→value in send order; first-wins per name (longest path wins)
void MergeCookies(Uri origin, IReadOnlyDictionary<string, string> cookies, CookieSource source = CookieSource.UserSupplied);
string? GetCookieHeader(Uri requestUrl);                        // RFC 6265 §5.4 serialization (same-name cookies all sent, longest path first)
void SetFromResponse(Uri responseUrl, IEnumerable<string> setCookieHeaders);
```

`CookieSource { Site = 0, UserSupplied = 1, Authenticator = 2 }`; `MergeCookies` throws on `CookieSource.Site` (site cookies only enter via Set-Cookie). 09-03's confirmed precedence (request > jar > user) composes correctly: `GetCookieMap` already includes merged user/authenticator cookies, and their Decorate layering overrides as documented. Authenticator cookies should be merged with `CookieSource.Authenticator` so cookieless mode still sends them.

### D2 security-attribute data (lead ruling: DATA only, no findings this phase)

`jar.SecurityObservations` → `CookieSecurityObservation(Name, Domain, Path, Secure, HttpOnly, SameSite, IsSession, ExpiresAt, SourceUrl, ObservedAt)` captured for every stored site cookie (incl. `__Secure-`/`__Host-` prefix hardening outcomes). The future D2 rules consume this surface; see §8 notation gap (missing `AuditCategory.Security`).

## 3. Robots findings + crawl-delay source (G07)

**Replace the engine's private robots dictionary with `RobotsCache`** (already implements `ICrawlDelaySource`):

```csharp
// engine :598 — drop robotsByHost; construct instead:
var robotsCache = new RobotsCache(
    fetcher, _robotsParser,
    virtualRobots: virtualRobotsStore,                       // existing virtual-robots precedence
    options: new RobotsCacheOptions { Scheme = seedScheme, CrawlerUserAgent = UserAgentToken },
    timeProvider: TimeProvider.System);

// engine :588-591 — pass the crawl-delay source (the G07 missing argument):
var aimd = new AimdPolitenessController(
    aimdOptions,
    auditId: auditId,
    adjustmentSink: new CompositeHostThrottleSink(throttleRecorder, throttleCoalescer),
    crawlDelaySource: robotsCache);
```

`RobotsCache.GetCrawlDelaySeconds(host)` serves the selecting group's `Crawl-delay`; the controller caps rate at 1/delay and enforces the spacing floor in `WaitAsync` (proven in `RobotsCrawlDelayAimdTests`). The crawl-delay is also surfaced as evidence (`crawlDelaySeconds`) in the robots-blocked finding — the AIMD crawlDelaySource flow the dispatch asked for.

**Robots-block branch (engine :681-687)** — blocked URLs never reach the offline analyzer, so emit at crawl time:

```csharp
var document = await robotsCache.GetDocumentAsync(entry.Host, ct);
var verdict = _robotsParser.Evaluate(document, entry.Url, UserAgentToken);
if (!verdict.Allowed)
{
    var record = RobotsBlockFindingFactory.Classify(NewUrlRecord(
        auditId, entry, CrawlStatusClass.NotCrawled, null, RobotsState.Disallowed));
    var urlId = (await _urlWrites.CreateAsync(record, ct)).Id;
    var robotsUrl = new Uri(new Uri(entry.Url), "/robots.txt").ToString();
    var finding = RobotsBlockFindingFactory.Create(urlId, entry.Url, verdict, document, UserAgentToken, robotsUrl);
    await _findings.EmitAsync(auditId, projectId, [finding], ct);
    continue;
}
```

- `Classify` sets `StatusClass=Blocked`, `BlockReason="robots"`, `RobotsState=Disallowed`, `IndexabilityFlags |= RobotsBlocked`, `IsIndexable=false` (spec state taxonomy; preserves other flags).
- `Create` evidence: `url, robotsState, userAgent, robotsUrl, matchedRule{allow,pathPattern}, userAgentGroup, crawlDelaySeconds` — the disallowing rule is the evidence. Key `indexability.robots-blocked`, Warning/Indexability — **same key/category as the offline `IndexabilityAnalyzer`** (one stable key, two emission paths). `CrawlFindingRuleKeys.RobotsBlocked` duplicates the string because Crawler cannot reference Audit; coordinator may dedupe against `IndexabilityRuleKeys` at wiring.
- `IsRobotsAllowedAsync` (engine :1390-1419) becomes redundant — delete or delegate to the cache.

## 4. WAF → AIMD trigger (G07 reorder) + five-facet evaluator (G26)

**Engine :708-715 currently records `IsChallenge:false` BEFORE the WAF evaluation.** Reorder:

```csharp
state.Counters.Fetched++;
var bodyBytes = fetch.Body.ToArray();
var waf = _wafEvaluator.Evaluate(new WafEvaluationInput(
    entry.Url, fetch.StatusCode, fetch.Headers,
    bodyBytes.Length > 0 ? Snippet(bodyBytes) : null));

// replaces the hardcoded-false HostResponseSample at :709-710:
WafAimdTrigger.Record(aimd, entry.Host, fetch, waf, DateTimeOffset.UtcNow);
```

`WafAimdTrigger.BuildObservation` carries TTFB, status, `verdict.IsChallenge`, Retry-After (delta-seconds or HTTP-date) and full-response latency into `AimdResponseObservation`. A corroborated challenge → §3.4 multiplicative decrease β=0.5 (rate AND concurrency), cooldown D=10s, trigger `waf-soft-block` through the **HOTFIX-01-locked** adjustment shape (`PreviousRate/NewRate/PreviousConcurrency/NewConcurrency` → hub `rateBefore/rateAfter/concurrencyBefore/concurrencyAfter`). 429/503 status triggers keep precedence over IsChallenge (locked controller law); Retry-After pauses max(retry-after, D).

**Double-recording coordination point (cf-wp-09-01):** `HttpFetcher` ALSO records an AIMD sample internally (`RecordAimdSample`, Fetching\HttpFetcher.cs :159/:414-427, `IsChallenge:false`). Two options:
- **Preferred:** inject `IWafChallengeEvaluator` into `HttpFetcher` (options or ctor); evaluate the final response before `RecordAimdSample` and record via `WafAimdTrigger.BuildObservation`. The engine then drops its own recording entirely (single transport-layer recording, per-hop fidelity).
- **Engine-only fallback:** construct `HttpFetcher` with `aimdController: null` (ctor accepts null — `_aimdController is null` guard at :416) and keep the engine-side `WafAimdTrigger.Record`. Loses per-hop samples; final-response-only AIMD.

**Challenge body retention (engine :748):** store bodies for challenges too, or `WafStoredBodyRedetector`/render-resolution have nothing to re-evaluate:

```csharp
if (bodyBytes.Length > 0 && (statusClass == CrawlStatusClass.Success || waf.IsChallenge))
```

**Evaluator upgrades (G26, all component-proven):**
- Five facets: `WafFacet { Header, Cookie, Body, Title, DomMarker, Script }`. Stage B bounded pre-parse (`StageBExtractor`, node limit `WafDetectionPolicy.StageBNodeLimit` default 2000, 250ms regex timeouts) extracts title, script src/inline, and start tags for `dom:tag#id.class[attr=value]` selectors.
- Marker grammar: `title:`/`dom:`/`script:` prefixes in BodyMarkers; `|high` flag on header/cookie markers marks high-specificity (rejected on other facets). Bundled asset loader accepts **additive** `titleMarkers`/`domMarkers`/`scriptMarkers` arrays (converted to prefixed BodyMarkers — locked `WafSignature` contract unchanged).
- Corroboration guard (§2.3.3): confidence ≥ threshold AND (≥2 independent facet types OR one `|high` provider-specific header/cookie marker). Generic title-only → suspected Notice, never blocks.
- Eligibility (§2.4.1): only 2xx + HTML (or absent) content-type may become `waf-challenge`; non-eligible matches are evidence annotations (`IsChallenge=false`, suspected, matched keys retained; evidence adds `eligible/statusEligible/contentTypeEligible`). Non-2xx challenge signatures never change the status class.
- Findings category corrected to **Indexability** (§2.4.5) in `WafDetectionRecorder` (challenge Warning, suspected Notice, render-resolved Notice).
- Evidence JSON keeps all existing fields (WafStoredBodyRedetector reads `observedHeaders`/`statusCode`) and adds `eligible/statusEligible/contentTypeEligible`, per-match `highSpecificity`, `policy.stageBNodeLimit`.
- **Bundled asset suggestion (coordinator-owned, not edited):** add `|high` to provider-specific markers (e.g. `cf-mitigated~challenge|0.6|high`, `x-datadome|0.6|high`, `cookie:_abck|0.6|high`) and title/dom/script markers per provider; current asset still detects all providers via 2+ facets.

## 5. Crawl scope, directives, resources, UA (engine DiscoverLinksAsync :1421+)

At each discovered-link enqueue decision:

```csharp
var outcome = canonicalizer.CanonicalizeDetailed(href, source.Url);
var verdict = scopeEvaluator.Evaluate(outcome.CanonicalUrl);
switch (verdict.Disposition)
{
    case CrawlScopeDisposition.Enqueue: /* frontier.Enqueue */ break;
    case CrawlScopeDisposition.RecordExternal: /* url record, not crawled (existing behavior) */ break;
    case CrawlScopeDisposition.CheckExternalStatus: /* status-only fetch, no body analysis (§5.13 toggle) */ break;
    // SkippedExcluded / SkippedResource / SkippedOutOfScope: do not enqueue; optionally record with reason
}
```

- Inclusion regex evaluated first (any-match), exclusion after, then path prefixes, then resource toggles (`ResourceScopeOptions`, all resource fetches default OFF, HTML always). Regex timeouts bounded (250ms default); invalid patterns rejected at construction/config parse.
- Directive toggles (§5.12 defaults): `DirectiveHandlingEvaluator.ShouldFollowLink(options, pageNofollow, linkNofollow)` (both default false), `ShouldCrawlPage` (noindex pages crawled by default), `ShouldEnqueueCanonical`/`ShouldEnqueueAlternates`/`ShouldFollowMetaRefresh` (default true), `ShouldFollowIframes` (default false). Detection/reporting of directives is unaffected.
- UA + headers (§5.1/§4.4): `HttpFetcherOptions.UserAgent = scopeConfig.UserAgent.Resolve()` (custom > Googlebot simulation > CrawlForge default); per-request `CrawlRequestProfile.Create(scopeConfig.UserAgent, scopeConfig.Headers, scopeConfig.Cookies).Apply(new HttpFetchRequest(entry.Url))` at engine :693 (request values win over profile).
- UA comparison (§5.15, OFF by default): post-crawl pass — `UserAgentComparisonEvaluator.SelectSample(successHtmlUrls, scopeConfig.UserAgentComparison)` (deterministic ordinal order, 50 or all); fetch each sampled URL twice (Googlebot Smartphone vs Chrome Desktop profiles); build `UaFetchObservation` (status, finalUrl, meta-robots, canonical, content hash, discovered links) from the parser; `Compare` → material differences (status/redirect/meta-robots/canonical/content-hash/links) → `UserAgentComparisonFindingFactory.Create` (Warning/Indexability, key `ua-comparison.material-difference`).

## 6. Finding keys to register (Contracts FindingRuleKeys + Audit AuditRuleCatalog)

| Key | Category | Severity | Producer |
|---|---|---|---|
| `canonicalization.session-segment-removed` | Indexability | Notice | CanonicalizationFindingFactory |
| `crawl.cookieless-redirect-loop` | Indexability | Warning | CookielessRedirectLoopDiagnostic |
| `ua-comparison.material-difference` | Indexability | Warning | UserAgentComparisonFindingFactory |
| `indexability.robots-blocked` | Indexability | Warning | RobotsBlockFindingFactory (key already exists in Audit; crawl-time path added) |

WAF keys (`waf-challenge-detected`, `suspected-challenge`, render-resolved) unchanged; category now Indexability.

## 7. DI registration recipe (EngineServicesModule.cs, coordinator-owned)

No new singletons required for the per-audit components (jar, canonicalizer, scope evaluator, robots cache are audit-scoped, constructed in RunAsync). Module-level additions:
- `services.AddSingleton<RobotsTxtParser>()` already present as `IRobotsTxtParser` (:106) — RobotsCache takes the concrete parser interface; no change.
- If the preferred HttpFetcher WAF injection is adopted: register `IWafChallengeEvaluator` into the fetcher construction path (already DI-registered :91).
- `CrawlTargetHttpClientFactory.Create` overload with `DelegatingHandler` (cf-wp-09-01).

## 8. SPEC-vs-CONTRACT notation gaps for the owner register (lead ruling: flag, do not amend)

1. **`AuditCategory.Security = 11` (additive request).** Spec §7.4 CR-017 "Security & protocol" category (incl. cookie security attributes, D2) has no locked enum member. Per lead ruling the jar captures `CookieSecurityObservation` DATA only this phase — no findings, no enum misuse. The D2 rules need `Security` (or an owner-approved mapping) before emission.
2. **`AuditCategory.Images`** — same gap reported by cf-wp-09-05; coordinate one additive enum amendment.
3. **`WafChallengeClass`**: spec §2.3 lists five classes; locked enum has four (`Unknown, Captcha, Interstitial, BotBlock`). Managed-challenge and rate-limit/soft-block currently map to Interstitial/BotBlock. Additive members requested.
4. **`WafProvider`**: no `Generic` member; generic signatures map to `Other` (documented mapping).
5. **`WafSignature` contract** lacks `TitleMarkers/DomMarkers/ScriptMarkers` arrays — worked around via the BodyMarkers prefix grammar + additive bundled-asset arrays (loader-side). A contract amendment would make the five facets first-class.

## 9. Contract-test payloads for the coordinator-owned conformance suites

**CrawlScopeConfigConformanceTests** (producer: UI/API `scope_rules_json` → consumer: `CrawlScopeConfigParser.Parse` → options drive canonicalizer/evaluator):
- Baseline: `null` → cookieJar.enabled=true, stripCookielessSessionSegments=true, queryPolicy=keepAll, mode=hostOnly, comparison disabled, directives per §5.12.
- Full shape: see `CrawlScopeConfigParserTests.FullPayloadRoundTrips` (canonicalization/scope/directives/resources/cookieJar/userAgent/userAgentComparison/headers/cookies).
- Hard errors: `{"scope":{"excludePatterns":["("]}}` → Errors non-empty; `{"userAgentComparison":{"enabled":true,"sampleSize":-3}}` → Errors.
- Fallbacks: invalid enum strings, non-boolean toggles, invalid JSON → baseline + notes.
- `sampleSize:null` → all URLs; end-to-end: parsed excludePatterns actually skip `/private/` in CrawlScopeEvaluator.

**RobotsBlockedWireTests** (producer: RobotsBlockFindingFactory over real RobotsTxtParser verdict → consumer: FindingEmitter → findings table → REST DTO):
- Fixture robots body `User-agent: *\nDisallow: /private/\nSitemap: ...`; URL `http://127.0.0.1:47314/private/blocked.html`.
- Assert: rule_key `indexability.robots-blocked`, category Indexability(0), severity Warning(1); evidence fields `url/robotsState/userAgent/robotsUrl/matchedRule{allow:false,pathPattern:"/private/"}/userAgentGroup/crawlDelaySeconds`.
- Classify → url_records: status_class Blocked(5), block_reason `robots`, robots_state Disallowed(2), indexability_flags & 4, is_indexable 0.
- Crawl-delay variant: `Crawl-delay: 7` → evidence `"crawlDelaySeconds":7` AND AIMD rate cap 1/7 via RobotsCache→controller.

**WafAimdTriggerConformanceTests** (producer: real WafChallengeEvaluator verdict + WafAimdTrigger → consumer: AimdPolitenessController → HostThrottleAdjustment → hub event):
- Corroborated 200 HTML challenge (Cloudflare interstitial facts) → `IsChallenge=true` → adjustment trigger `waf-soft-block`, PreviousRate/NewRate = β halves, PreviousConcurrency/NewConcurrency halved (floor 1), TtfbSampleMilliseconds carried, Timestamp = observation instant.
- Locked hub mapping: `rateBefore=PreviousRate, rateAfter=NewRate, concurrencyBefore=PreviousConcurrency, concurrencyAfter=NewConcurrency` — shape unchanged (HOTFIX-01).
- Non-2xx challenge (403 + full signatures) → `IsChallenge=false`, suspected annotation, NO waf-soft-block adjustment (status class unchanged).
- 429 + Retry-After: 30 → triggers [http-429, retry-after], pause max(30, D=10).
- Single generic title facet → suspected Notice, never blocked; high-specificity single header (`|high`) → challenge.

## 10. Files changed

Production (owned):
- Canonicalization: `CrawlFindingRuleKeys.cs` (new), `CanonicalizationFindingFactory.cs` (new), `CanonicalizationOptions.cs` (edited: StripCookielessSessionSegments), `UrlCanonicalizer.cs` (edited: segment removal + CanonicalizeDetailed), `CrawlScopeOptions.cs` (new), `CrawlScopeEvaluator.cs` (new), `UserAgentProfiles.cs` (new), `UserAgentComparison.cs` (new), `CrawlScopeConfigParser.cs` (new).
- Politeness: `AuditCookieJar.cs` (new), `CookieJarMessageHandler.cs` (new), `CookielessRedirectLoopDiagnostic.cs` (new), `RobotsBlockFindingFactory.cs` (new).
- Waf: `WafMarker.cs` (rewritten: 5 facets + |high + dom selectors), `StageBExtractor.cs` (new), `WafChallengeEvaluator.cs` (edited: eligibility, Stage B facets, guard, suspected evidence), `WafDetectionPolicy.cs` (edited: StageBNodeLimit), `WafSignatureCatalogue.cs` (edited: additive asset facet arrays), `WafDetectionRecorder.cs` (edited: Indexability category), `WafAimdTrigger.cs` (new: trigger + RetryAfterParser).

Tests (owned):
- Canonicalization (new folder): UrlCanonicalizerSessionSegmentTests, CrawlScopeEvaluatorTests, DirectiveHandlingTests, UserAgentTests, CrawlScopeConfigParserTests.
- Politeness (new folder): CookieGateHandler (scripted fixture-trap double), AuditCookieJarTests, CookielessRedirectLoopDiagnosticTests, RobotsBlockFindingFactoryTests, RobotsCrawlDelayAimdTests.
- Waf: WafFiveFacetTests (new), WafAimdTriggerTests (new), WafEvaluatorTests (updated: Non200 → annotation-only per §2.4.1), WafPersistenceTests (updated: category → Indexability per §2.4.5).

Spec-driven test corrections (G26 reopen, owned Waf folder): Non200ChallengeIsDetected → Non200ChallengeIsAnnotationOnly; persistence category Crawl → Indexability. No assertions weakened; unflagged single-facet guard tests unchanged.

## 11. Blockers / out-of-scope needs

- cf-wp-09-01: CrawlTargetHttpClientFactory DelegatingHandler overload; RedirectChainRecorder hop/Set-Cookie exposure; HttpFetcher WAF injection (preferred) or null-aimd fallback decision.
- Coordinator: bundled WAF asset marker upgrades (|high + facet arrays); finding-key registration; enum amendment requests (§8) through the owner register; the three conformance suites (§9).
- No blockers in owned paths; verification green.
