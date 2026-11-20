# Dispatch: WP-10-05 (wave-10A) — subagent prompt

You are builder `cf-wp-10-05` for phase 10 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a full-stack builder on audit rules. Your owned paths cover WAF detection, the redirect evaluator and analyzer files, `CrawlForge.Audit\StructuredData\`, the image rule analyzer, the indexability state evaluator, `CrawlForge.Audit\Scoring\` and `frontend\src\surfaces\project\`, with their tests. The exact list is in your YAML.

**Task.** Use the contract members WP-10-A1 added.

- **Categories:** move image rules to Images, cookie-security and protocol findings to Security & protocol, and hreflang findings to Internationalization, and score all three.
- **WAF classes:** classify all five; soft blocks (rate-limit-soft-block, managed-challenge) throttle through AIMD, and hard blocks stop the host.
- **Canonical bit:** set it when the canonical points elsewhere.
- **Redirects (CR-029):** extend the redirect check to the four cases, each a toggle on the UI-04 analysis tab, all on by default.
- **Deprecated terms and Microdata (CR-030):** add deprecated-term findings from the bundled vocabulary plus a retired rich-result list (embed the versioned list inside `Audit\StructuredData\`; HowTo is one entry), and resolve Microdata `itemprop` URL and prefixed terms like RDFa.
- **Image size (CR-031):** make the oversized-image rule report "not measured" when no transferred size is available.
- **Rule keys:** replace the builder-local rule keys with the new `FindingRuleKeys` constants. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** Phase-09 notation gaps 1 to 5 and 7 to 10 in `WIRING-NOTES.md`, and CR-029, CR-030 and CR-031. If the coordinator's criterion #56 check failed, it also routes the UI-04 editor fix to you; ask the coordinator.

**Boundaries.** Score registration and the rule-catalog call are wiring, so list them in your handoff. Do not touch rendering or capture code. Keep every phase-09 trap passing: your component tests must cover missing-alt under Images and hreflang-no-return under Internationalization.

Your agent id is `cf-wp-10-05`; your private fixture port, if you need one, is 47343.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, `CrawlForge.Contracts\` or `frontend\src\contracts\` (contracts are locked after the WP-10-A1 amendment); those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Other builders may be working at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

## Coordinator notes (authority: living architecture > locked contracts > brief/YAML)

FIRST read `E:\Crawlforge\audit-work\reports\phase-10\WAVE-10A-NOTES.md` — the locked-surface catalogue and your specific notes. Critical points:

1. **Criterion #56 PASSED** at step 0 (commit 1e30ab9, 17/17 checks) — NO UI-04 editor fix is routed to you. (This answers the brief's "ask the coordinator".)
2. Contracts are RE-LOCKED (commits 2cb4311 + 0307fe8). A1 handoff items for you: `IssueExplorer.tsx` wire category list += images/securityandprotocol/internationalization (NOTE: IssueExplorer lives in surfaces/explorers — OUTSIDE your owned surfaces/project; put that one-line change in your HANDOFF, the coordinator applies it); `RedirectAppropriatenessEvaluator` flags += NonIndexableTarget=4, HttpsDowngrade=8, All=15 (+ fix the stale comment); `RedirectFindingAnalyzer.CheckName` += "non-indexable-target"/"https-downgrade"; scoring is enum-driven and picks up 14 categories automatically — verify, don't hardcode; replace ALL builder-local rule keys with `FindingRuleKeys` constants.
3. ALREADY DONE by the coordinator (verify, do NOT redo or revert): `ANALYSIS_CATEGORIES` extended to 14 in configurationModel.ts (labels verbatim from AUDIT_CATEGORY_LABELS); wireConformance.test.ts + configurationEditor.test.tsx guards extended; FirstRunSeed toggles 14; REST captureMode/captureSettings/redirectChecks binding LIVE (CaptureConfigurationRouteTests); schema v5 columns. Your surfaces/project + tests/frontend/project ownership starts from this committed baseline.
4. CR-029: the four toggles bind to `redirectChecks` on the configuration wire (UI-04 "Analysis categories" tab — CategoriesPanel). `RedirectCheckSettings` (crossHost/temporaryStatus/nonIndexableTarget/httpsDowngrade) defaults all true; absent/null document = all ON. Each finding names the case, the triggering hop and the final target; keys `redirects.inappropriate-{cross-host,temporary-status,non-indexable-target,https-downgrade}`. Component tests must show each toggle flipping its finding. The form value needs a `redirectChecks` field + wire round-trip in configurationModel.ts (yours).
5. CR-030: bundled, VERSIONED retired/restricted rich-result list embedded in `Audit/StructuredData` (HowTo is one entry); NEVER fetched at runtime (offline-first). Microdata `itemprop` URL and prefixed terms resolve like RDFa. Key: `structured-data.deprecated-term`.
6. CR-031: oversized-image uses transferred bytes where captured — rendered waterfall `RenderedNetworkEntry.TransferredBytes` (via the NetworkWaterfall artifact, `ContentArtifactKind.NetworkWaterfall=3`) or the proxy size field in http mode; otherwise report "not measured" — NEVER "pass". `ImageFact.TransferredBytes` already exists; you may inject `IReadRepository<ContentArtifactDto>` + `IContentStore` into ImageRuleAnalyzer (precedent: UrlStructureAnalyzer takes IReadRepository<UrlRecordDto>) — list the DI change in your handoff. The coordinator wires the waterfall artifact production (WP-10-01/02); code against the artifact kind + entry shape and test with stored fake artifacts.
7. WAF: five classes per 03/02 §2.2; soft (managed-challenge, rate-limit-soft-block) → AIMD throttle; hard (captcha, interstitial, botblock) → host stop. Wire tokens: NEW members are hyphenated via EnumWireNames (`managed-challenge`, `rate-limit-soft-block`); EXISTING `interstitial`/`botblock` wires are LOCKED even though the spec spells them js-interstitial/bot-block — that deviation is a recorded loop-§8 owner observation; do NOT rename. The engine's WAF record path + AIMD/host-stop wiring is the coordinator's — hand off the evaluator surface.
8. Category moves (D-021): image rules → Images; cookie-security/protocol → SecurityAndProtocol; hreflang → Internationalization; all three SCORED. `IndexabilityStateEvaluator` sets `IndexabilityFlags.CanonicalElsewhere` (16) when the canonical points elsewhere (CR-024). Keep every phase-09 trap passing: component tests cover missing-alt under Images + hreflang-no-return under Internationalization (the phase gate re-runs 17/17 + UI-11/UI-12 category assertions).
9. Rule-catalog registration (AuditRuleCatalog seeds) and score registration are COORDINATOR wiring — list every rule key + category + severity + priority your producers emit in your handoff so the catalog matches.
10. The working tree carries uncommitted coordinator ledger/evidence edits (`buildplan/`, `audit-work/reports/phase-10/`, `app/tools/network-audit/evidence/`) — ignore them in lane checks. A canonical gate run may be in progress: run ONLY your filtered test projects with CF_AGENT_ID set; never the whole solution. Frontend: `npm run test -- --run tests/frontend/project --testTimeout=30000` from app/src/frontend; vitest contention flakes are documented — retry quietly before reporting failure. WP-10-02's capture-tab UI changes will be applied BY THE COORDINATOR to surfaces/project AFTER you complete — do not implement them; keep CrawlAsPanel extensible.

## Your workpiece YAML (verbatim)

```yaml
  - workpiece_id: "WP-10-05"
    title: "Phase-09 Notation-Gap Carryovers (D-021) — Categories, WAF Classes, Redirect Cases, Deprecated Structured Data"
    phase_id: "phase-10"
    status: "pending"
    parallel_group: "wave-10A"
    depends_on_workpieces: ["WP-10-A1"]
    context_files:
      - "E:\\Crawlforge\\architecture\\04-audit-and-analysis-engine\\01-pipeline-and-content-scoping.md"
      - "E:\\Crawlforge\\architecture\\04-audit-and-analysis-engine\\02-core-seo-and-indexability-rules.md"
      - "E:\\Crawlforge\\architecture\\03-crawl-engine\\02-frontier-fetching-and-waf.md"
      - "E:\\Crawlforge\\audit-work\\reports\\phase-09\\WIRING-NOTES.md"
    architecture_sections:
      - file: "E:\\Crawlforge\\architecture\\04-audit-and-analysis-engine\\01-pipeline-and-content-scoping.md"
        sections: ["7.4"]
      - file: "E:\\Crawlforge\\architecture\\04-audit-and-analysis-engine\\02-core-seo-and-indexability-rules.md"
        sections: ["7.13"]
      - file: "E:\\Crawlforge\\architecture\\03-crawl-engine\\02-frontier-fetching-and-waf.md"
        sections: ["2.2"]
    capabilities:
      - { id: "CAP-B-051", baseline_state: "unwired" }      # CR-029 four redirect cases
      - { id: "CAP-B-067", baseline_state: "unwired" }      # CR-030 deprecated terms
      - { id: "CAP-B2-070", baseline_state: "unwired" }     # CR-030 Microdata term resolution
      - { id: "CAP-B-056", baseline_state: "missing_engine" } # CR-031 not-measured reporting
      - { id: "CAP-D2-X01", baseline_state: "partial" }     # five WAF challenge classes
      - { id: "CAP-K-X02", baseline_state: "missing_ui" }   # category set and redirect toggles
    ui_surfaces: ["UI-04", "UI-11", "UI-12"]
    fixture_traps: ["structured-data-errors", "missing-alt", "redirect-chain", "hreflang-no-return"]
    contract_tests:
      - payload: "category, WAF class and canonical-bit values on finding, score and URL wire payloads (uses the WP-10-A1 members)"
        test: "AuditScoreWireConformanceTests"
    reopens:
      - workpiece_id: "WP-09-05"
        evidence: "WIRING-NOTES notation gaps 1-4, 6-10: spec categories, canonical bit and redirect/structured-data checks absent or provisional"
      - workpiece_id: "WP-09-02"
        evidence: "WIRING-NOTES notation gap 5: WafChallengeClass has 4 of the 5 spec classes"
    owned_paths:
      - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Crawler\\Waf\\"
      - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Crawler\\Fetching\\RedirectAppropriatenessEvaluator.cs"
      - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Crawler\\Fetching\\RedirectFindingAnalyzer.cs"
      - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Crawler\\Fetching\\RedirectRuleKeys.cs"
      - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Audit\\StructuredData\\"
      - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Audit\\ContentQuality\\ImageRuleAnalyzer.cs"
      - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Audit\\Indexability\\IndexabilityStateEvaluator.cs"
      - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Audit\\Scoring\\"
      - "E:\\Crawlforge\\app\\src\\frontend\\src\\surfaces\\project\\"
      - "E:\\Crawlforge\\app\\tests\\CrawlForge.Crawler.Tests\\Waf\\"
      - "E:\\Crawlforge\\app\\tests\\CrawlForge.Crawler.Tests\\Fetching\\RedirectFindingAnalyzerTests.cs"
      - "E:\\Crawlforge\\app\\tests\\CrawlForge.Audit.Tests\\StructuredData\\"
      - "E:\\Crawlforge\\app\\tests\\CrawlForge.Audit.Tests\\Scoring\\"
      - "E:\\Crawlforge\\app\\tests\\CrawlForge.Audit.Tests\\Indexability\\IndexabilityStateEvaluatorTests.cs"
      - "E:\\Crawlforge\\app\\tests\\frontend\\project\\"
    deliverables:
      - "Images, Security & protocol and Internationalization are real audit categories with scores; image rules and hreflang findings move to them (D-021 item 1)"
      - "Five WAF challenge classes per architecture 03/02 (adds managed-challenge and rate-limit-soft-block); soft blocks throttle via AIMD, hard blocks stop the host (D-021 item 2)"
      - "IndexabilityFlags canonical bit set when the canonical points elsewhere (CR-024)"
      - "Inappropriate redirects: cross-host, temporary, non-indexable target, HTTPS-to-HTTP downgrade, each a toggle on the UI-04 analysis tab, all on by default (CR-029)"
      - "Deprecated structured-data terms from the bundled vocabulary plus a bundled, versioned retired rich-result list embedded in Audit/StructuredData; Microdata itemprop URL and prefixed terms resolve like RDFa (CR-030)"
      - "Oversized-image rule reports non-inline images as not measured when no transferred size is available (CR-031)"
      - "Builder-local rule keys from phase 09 replaced by the FindingRuleKeys constants added in WP-10-A1"
    verification_commands:
      - "dotnet test E:\\Crawlforge\\app\\tests\\CrawlForge.Crawler.Tests --filter FullyQualifiedName~Waf|FullyQualifiedName~RedirectFinding"
      - "dotnet test E:\\Crawlforge\\app\\tests\\CrawlForge.Audit.Tests --filter FullyQualifiedName~StructuredData|FullyQualifiedName~Scoring|FullyQualifiedName~IndexabilityStateEvaluator"
      - "cd E:\\Crawlforge\\app\\src\\frontend && npm run build"
    done_when: "Phase-gate HTTP fixture run keeps 17/17 phase-09 traps passing; UI-11 dashboard shows Images, Security & protocol and Internationalization scores; missing-alt appears under Images and hreflang-no-return under Internationalization; the four redirect toggles in UI-04 each switch their finding on and off in a re-run audit."
    acceptance_criteria_refs: [27, 49]
```