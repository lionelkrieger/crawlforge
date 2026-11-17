# WP-05-01 REST Controller Surface — Handoff

Builder: cf-wp-01-01. Scope: `CrawlForge.Api/Controllers` + `CrawlForge.Api.Tests/Controllers`.
Status: **108/108 operations, 88/88 spec-row groups, 145/145 controller tests green, governance analyzer PASS** (`--root .../Controllers --fail-on-violation`).

Counting rule (per lead): **groups are spec ROWS; a multi-operation row counts once.** The catalog merges some paths (e.g. `GET/POST /waf-signatures`, `PUT/DELETE /waf-signatures/{key}`) that the spec lists as separate rows; counting spec rows yields 88. Operations count every HTTP verb (108).

## Authority order
negative requirements → assigned domain files → locked contracts (frozen TS catalog `app/src/frontend/src/contracts/api/routes.ts` outranks spec tables) → manual → workpiece YAML. Locked C# DTOs/services untouched; both Api csprojs orchestrator-managed (never edited); no new packages.

## Wire bridge (ApiJsonOptions)
- Envelope `data` always present (even null); `pagination`/`error` omitted when null (never written as null). Pagination meta writes `total` (not `totalCount`).
- `fieldErrors` is a field→string[] map. Error codes: `not-found`, `validation`, `conflict`, `invalid-state`, `unsupported`, `internal`.
- Enums: `ApiEnumConverter<T>` writes **lowercase, no hyphen** (`WafChallengeClass.BotBlock`→`botblock`, `RedirectMatrixVerdict.TargetMismatch`→`targetmismatch`); the single ratified special case is `ScheduleTriggerType.CatchUp`→`catch-up`. The frozen TS catalog types these as open `string`, so lowercase-no-hyphen is conformant. Query-string parsing uses `ApiEnumConverter<T>.TryParse` (case-insensitive). **Do not assume kebab-case.**
- `EntityId`/`IsoTimestamp` are strings on the wire. offset/limit/sort/order(asc|desc); `MaxPageSize`=1000.
- **STJ constructor binding is CASE-SENSITIVE → all storage-payload (de)serialization MUST use `ApiJsonOptions.Default`.** Default `JsonSerializer` is only safe for round-tripping your own freshly-serialized payloads (e.g. test doubles).

## Repository signatures (confirmed)
`IReadRepository<T>.GetByIdAsync(long, ct)`→`Task<T?>` (NOT GetAsync); `.ListAsync(ListQuery, ct)`→`Task<PagedResult<T>>`. `IWriteRepository<T>.CreateAsync(TDto)`→`Task<TDto>` (use `.Id`); `UpdateAsync`→`Task<TDto>`; `DeleteAsync(long)`→`Task<bool>` (write repo ONLY). `ListQuery(Offset, Limit, Filters, SortBy, SortDescending)`.
SqlRepository filters: equality only, DTO property names case-insensitive; **bool filters must be "true"/"false"**; enum filters numeric strings; soft-delete auto-excluded; limit clamp 1..10000; search/LIKE done in the API layer. `CrawlStatusClass.Success`=1.

## Aliases (spec rows absent from the catalog)
Thin additive aliases preserve parent semantics; documented in the controllers that own them (settings/system surfaces). No alias diverges from its parent's envelope/error behavior.

## Settings / behavior-doc layouts
- Settings are **local-only**; ZERO license/account/entitlement payloads (criterion #8). Subset-merge on PATCH: only supplied fields change; unknown fields rejected.
- `apiSurface` sidecar (RATIFIED): display metadata (name/timestamps) for schedules/destinations/queries stored in `application_settings.ui_preferences_json` key `"apiSurface"`. Pruned on delete. Fallbacks: name=`"Schedule {id}"` (resp. Destination/Query), timestamps=UnixEpoch. Self-composed `ApiSurfaceMetadataStore(settingsReads, settingsWrites)`.
- Host-observes-status: host explorer reads the live `IAimdPolitenessController` checkpoint; status is observed, not commanded.

## Schedules / runs
- Recurrence is a **local grammar, NOT cron**: `interval:<timespan>` / `calendar:HH:mm[;days=...]` via `RecurrenceRule.TryParse`+`GetNextOccurrence`. The wire field `cron` carries this grammar; empty=manual-only. **Phase-06 UI must NOT send cron expressions.**
- `RunOutcome` map: Success→`completed`, Failed→`failed`, Skipped→`cancelled`; failed auditId=""; `completionActionResults=[]` (deferred durable-history candidate).
- Schedule delete prunes its runs (`IWriteRepository<ScheduleRunDto>`) AND its apiSurface sidecar entry.

## Exports / destinations / queries / dictionary / plugins
- Export download streams real temp bytes; `XmlSitemap` has no distinct wire key (delegates `ExportEngine(XmlSitemap, ["sitemap"])`); sitemap `urlCount`=isIndexable+Success count; 409 when audit incomplete; llms output `{AppBase}/exports/llms`.
- Queries surface key stored in apiSurface sidecar; dictionary create returns **409** on duplicate key.
- Plugins: state = `plugin.IsEnabled ? plugin.LoadState ?? "pending" : "disabled"` (disabled overrides LoadState). Command-style plugin contributions rejected; plugins are local `plugin.json` artifacts under a fixed `PluginRoot` (`{AppBase}/plugins`); rescan tests share that root (single class + cleanup). `System.IO.File` qualified (collides with `ControllerBase.File`).

## Comparison projection
`AuditComparisonReader` (real) projects deltas; content compare (`/audits/compare/content`) diffs `document_head` (title/metaDescription/metaRobots/viewport/charset/canonicalUrl), `headings.outline[].text`, and `text_statistics.visibleWordCount`. `hostMapping=true` aligns by path when no exact canonical match.

## Explorer wire decisions (audit report)
URL status class derived from FinalStatusCode else stored StatusClass (Blocked→2xx, FetchError→5xx, NotCrawled→1xx). Titles from `document_head.title`. Link edges carry DomZone + zone-evidence weight. WAF summary keys are lowercase enum wire values.

## Intelligence explorers (this increment)
- **WAF**: block list joins `waf_detections`→`url_records` (UNIQUE per audit+url). Summary `blockRate`=blocked/fetched (audit counters). Signature catalogue = bundled + user; user `pattern` stored as a single body marker (`BodyMarkersJson=[pattern]`, header markers `[]`); projection joins header+body markers with `;`. `description` has **no column** → always null (sidecar deferred). Delete reports `bundledDefaultRestored` when a bundled entry shares the key.
- **Hosts**: `rollingBaselineRate`=CurrentRate (no separate baseline column); `throttled` from last `HostThrottleEventDto` direction (NewRate<PreviousRate); override is **rate-only** (`pinnedConcurrency=null`, concurrency accepted but ignored); history `concurrencyBefore/After=0`, `ttfbSampleMs=null` (not in the event row).
- **N-grams**: `NGramReadModel` (real). `minCount`→`MinDf` (site) approximation; site count=TotalFrequency, density=MeanTermFrequency, urlId=null; url count=Count, density=TermFrequency. Cannibalization `occurrences`=(long)round(TermFrequency); gaps `observedCount`=AnchorDf, `expectedCount`=Threshold, `gapScore`=clamp((Threshold−AnchorDf)/Threshold,0,1). Zone filter omitted ⇒ null (NOT Body). Gaps ignore zone (anchor-derived).
- **Analytics**: tags grouped by (Vendor, ContainerId, DetectionSource); consent heuristic (vendor/placement contains "consent"; beacon=DetectionSource "proxy"); coverage=distinct container URLs / indexable pages. Findings category `AnalyticsTagging`.
- **GEO**: llms-txt via `IGeoReadinessAnalyzer.AnalyzeAsync` HostResults (url=`https://{host}/llms.txt`, checkedAt=now, issues=Issues+BrokenLinks). Governance via `IAiBotGovernanceMatrix.EvaluateAsync`; host/robotsTxtAllowed/notes from evidence JSON; `llmsTxtAllowed=null` (not computed).
- **Redirect matrix**: matrix is audit-scoped (`matrixId`=auditId; no separate table). Upload replaces prior mappings, persists `RedirectMappingDto(Id,AuditId,SourceUrl,ExpectedTargetUrl,Ordinal)`; `autoSeedAudit` starts an audit (`IAuditExecutionService`) + ingests sources (`IListModeIngestor`, allowDiscovery=false). Results from `IRedirectMatrixValidator`; verdict lowercase-no-hyphen.
- **Semantics**: run is synchronous → `jobId`=auditId, state=`completed` (no job table; `rerun` advisory). similar/link-opportunities from `SemanticLinkOpportunityDto` (anchorSuggestion=first AnchorCandidatesJson); clusters grouped from `SemanticClusterDto` (centroid=max CentroidSimilarity, label=null).
- **Logs**: import derives BaseUri from the project's first configuration `SeedsJson` (fallback `https://localhost`); VerificationMode=CidrOnly (E8, zero outbound). Format keys combined/common→ApacheNginxCombined, w3c→IisW3c, cloudflare→CloudflareExport, custom→Custom. State Pending→queued, Importing→running, Completed→completed, Failed→failed. `completedAt`=ImportedAt when terminal (no separate column); `correlation=null` per import; `dnsVerificationConsented`=VerificationMode!=CidrOnly. Correlation summary calls each view (limit 1) for TotalCount; importId defaults to project's latest import; rows join url_records by canonicalUrl, lastSeen from evidence. verify-dns refuses without consent (started=false), else `VerifySampleAsync`.
- **Content explorers**: extraction columns keyed by ordinal string; values are the stored typed JSON (`TypedValueJson`) preserved as JsonElement; summary discards=IsInvalid, truncations=IsTruncated. Search overview sums MatchCount; snippet=first SnippetsJson. Snippet values from first object of RowsJson. Technologies name=TechnologyKey, version/evidence=null.

## Test harness
`ControllerTestHost`: real SQLite temp provider + SchemaMigrator + SqlRepository<T> for CRUD; real NGramReadModel/AuditComparisonReader/ScopePreviewService/UrlRewriteEngine/AngleSharpDomParser/RobotsTxtParser/StructuredDataValidator/WafChallengeEvaluator/FindingSuppressionService. Engine doubles in `ControllerDoubles.cs`. **Repository-backed doubles** (catalogue/import service agree with the real repos): FakeWafSignatureCatalogue, FakeLogImportService — both eager-resolved at host startup. Added FakeAimdPolitenessController, FakeListModeIngestor. xunit parallelism per-class; plugin rescan tests share fixed PluginRoot (one class + cleanup). Controllers do NOT publish hub events (sibling WP-05-02 owns Hubs/Metrics).

## Limitations (deferred amendment candidates)
- structured-data `properties` EMPTY; llms host-filter; single-page scoped-selectors.
- url-check single-hop chain.
- hosts: rollingBaseline=CurrentRate; concurrency pin ignored (rate-only); history concurrency=0/ttfb=null.
- ngrams: minCount→MinDf approximation; occurrences=(long)TF cast; gapScore derived.
- geo: llmsTxtAllowed=null.
- waf signatures: description no column (null); pattern=single body marker.
- semantics: jobId=auditId (no job table); rerun advisory; cluster label=null.
- logs: completedAt=ImportedAt; per-import correlation=null; BaseUri derived from first config seed.
- content compare: text diff compares visibleWordCount (full body text not materialized); technologies version/evidence=null.
- analytics: consent rollup is heuristic (vendor/placement/source).