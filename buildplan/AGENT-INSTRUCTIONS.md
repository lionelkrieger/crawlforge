# CrawlForge — Coding Agent Operating Manual & Concurrency Protocol

| Document control | Value |
|---|---|
| Identifier | CF-BUILD-OPS |
| Version | 2.0 |
| Applies to | Every agent (orchestrator and builders) dispatching or executing workpieces in `E:\Crawlforge\buildplan\phases\` (phase-08 onward; phases 00–07 are frozen history in `buildplan\v1.1\`) |
| Build root | `E:\Crawlforge\app\` |
| Specification root (read-only) | `E:\Crawlforge\architecture\` (26 domain files + README) |
| Orchestration ledger | `E:\Crawlforge\buildplan\index.yaml` |
| Amendment record | v2.0 (2026-11-17): plan rebuilt around end-to-end flows after runtime-probe-2 disproved the v1.1 completion claims (43/43 workpieces, 48/48 criteria declared complete while no audit could run from the UI and 16/24 surfaces were unreachable). Carried over unchanged from v1.1: orchestrator-only ledger (section 5), blocker reporting (section 7), CF_AGENT_ID build isolation + team execution (section 10), serialized contract/dependency amendments (section 11), orchestrator wave gates (section 5.5). New in v2.0: capability baseline with evidence levels (capability-baseline.yaml), fixture acceptance rule (section 12.1), contract-conformance rule (section 12.2), UI reachability rule (section 12.3), discovery protocol (section 12.4), done_when completion gate (section 5.6), serial shared-wiring workpiece per phase. The full v1.1 amendment history (WP-00-01 scope corrections, WP-02-D1/A1 through WP-07 defect ledger D1-D15, the ratified criterion-#40 composite gate) is preserved verbatim in buildplan\v1.1\AGENT-INSTRUCTIONS.md and remains binding precedent where referenced. HOTFIX-01 (commit db53378) and HOTFIX-01b (commit fc9ac0f) are recorded as completed workpieces WP-08-01/WP-08-02. |

This manual is **mandatory**. An agent that violates the Context-Isolation Rule, the File-Ownership Rule, the Clean-Room Guardrails, or the v2.0 Proof Rules (§12) invalidates its workpiece regardless of test results.

---

## 1. Mission & Directory Boundaries

CrawlForge is a standalone, offline-first desktop website crawler and auditor: an Electron shell supervising a local ASP.NET Core (.NET 10) backend, communicating over loopback REST + a real-time hub, with all persistence local (embedded SQLite by default, optional user-owned MySQL). There are **no accounts, no licenses, no entitlements, no telemetry, and no vendor services**.

Three strict boundaries exist in `E:\Crawlforge`:

1. `E:\Crawlforge\architecture\` — verified, **read-only** modular specifications. Never write, rename, or delete anything here.
2. `E:\Crawlforge\buildplan\` — orchestration files. **Read-only for builder agents.** Only the orchestrator writes the status/claim/timestamp fields defined in §5; phases and workpieces are restructured only through a formal orchestrator amendment (§11).
3. `E:\Crawlforge\app\` — the application under construction. Builder agents may create/modify files **only inside the `owned_paths` of their dispatched workpiece**.

The master files now live at `E:\Crawlforge\architecture\baseline\functional-specification.md` (FS-CRAWL-001) and `E:\Crawlforge\architecture\baseline\crawlforge-extensions-and-ui-stack.md` (FS-CRAWL-002). They are immutable history and **must never be opened by coding agents** (see §3). The living specification is the modular `E:\Crawlforge\architecture\` domain files (v2.0, change records CR-001…CR-018 applied); where baseline and living files differ, the living files govern.

---

## 2. Workspace Topology (target layout inside `E:\Crawlforge\app\`)

Phase 00 creates this skeleton; later workpieces fill only their owned subtrees.

```text
E:\Crawlforge\app\
│   CrawlForge.sln
│   Directory.Build.props                  <-- Server GC, nullable, analyzers, TFM net10.0, CF_AGENT_ID output isolation (§10.2)
│   package.json                           <-- npm workspaces root (frontend, electron, mcp-stdio-adapter)
│   tsconfig.base.json
│   .gitignore                             <-- bin/ obj/ TestResults/ node_modules/ (agent-scoped artifacts never committed)
│
├───src\
│   ├───backend\
│   │       CrawlForge.Contracts\          <-- LOCKED after Phase 00: C# interfaces, enums, DTOs
│   │       CrawlForge.Host\               <-- ASP.NET Core composition root, loopback binding, module discovery
│   │       CrawlForge.Security\           <-- ISecretProtector, port handshake, minidump crash handler
│   │       CrawlForge.Data.Sqlite\        <-- WAL provider + native extension loader
│   │       CrawlForge.Data.MySql\         <-- external provider + connection tester
│   │       CrawlForge.Data.Migrations\    <-- 40-object versioned migrations + deterministic seeding
│   │       CrawlForge.Data.Repositories\  <-- CQRS read/write repositories, aggregation, archive import/export
│   │       CrawlForge.Data.ContentStore\  <-- compressed content-artifact store + retention pruner
│   │       CrawlForge.Crawler\
│   │       │   Limits\                    <-- Unlimited Limits Resolver, robots.txt, virtual robots
│   │       │   Politeness\                <-- AIMD controller (R0, alpha, beta, kappa, EWMA TTFB)
│   │       │   Canonicalization\          <-- canonical URL + on-the-fly regex rewriter
│   │       │   Frontier\                  <-- priority frontier, per-host sub-queues, checkpoints
│   │       │   Fetching\                  <-- HTTP fetcher, redirect recorder, signed requests, pre-crawl validation
│   │       │   Waf\                       <-- 200-OK WAF/challenge fingerprinting pass
│   │       │   ListMode\                  <-- list ingestion + redirect migration matrix validator
│   │       │   Rendering\                 <-- headless Chromium pool (stealth, infinite-scroll, console)
│   │       │   Proxy\                     <-- built-in loopback intercepting proxy + upstream routing
│   │       │   Scheduling\                <-- scheduled audits background service
│   │       CrawlForge.Audit\
│   │       │   Pipeline\                  <-- 8-step orchestrator, tolerant DOM parser, element inventory
│   │       │   Scoping\                   <-- Scoped Visible Text Tree, content_scope_profiles
│   │       │   Indexability\              <-- status/soft-404, header-vs-HTML equivalency, response-vs-rendered
│   │       │   ContentQuality\            <-- shingles, Flesch, Hunspell, pixel-width SERP/social
│   │       │   LinkGraph\                 <-- DOM-zone classification, weighted PageRank / link equity
│   │       │   StructuredData\            <-- JSON-LD/Microdata, i18n, mobile, technology detection
│   │       │   Accessibility\             <-- a11y rule families
│   │       │   Performance\               <-- rendered CWV, budgets, code coverage, composition
│   │       │   Custom\                    <-- extraction, search, JS snippets
│   │       │   Scoring\                   <-- category/overall scores, finding suppression
│   │       CrawlForge.Intelligence\
│   │       │   NGrams\                    <-- 4-zone n-gram & topical density analyzer
│   │       │   Embeddings\                <-- local ONNX / loopback-daemon vectors, semantic linking
│   │       │   Geo\                       <-- llms.txt validation, AI bot governance matrix, generator
│   │       │   MarTech\                   <-- tag/container inventory, dataLayer, Consent Mode v2
│   │       │   LogAnalyzer\               <-- offline streaming log import, bot CIDR, correlation
│   │       CrawlForge.Api\
│   │       │   Controllers\               <-- all 88 loopback REST routes
│   │       │   Hubs\                      <-- SignalR hub: 19 events, throttling, 2s system-health
│   │       │   Metrics\                   <-- pull-only /metrics Prometheus exposition
│   │       CrawlForge.Export\             <-- Excel/CSV/HTML/PDF/XML sitemap + encrypted destinations
│   │       CrawlForge.Mcp\                <-- local MCP server: loopback SSE, 8 tools / 15 resources / 5 prompts
│   │
│   ├───frontend\                          <-- React + Vite renderer (Mantine v7/v8, Zustand)
│   │   └───src\
│   │       contracts\                     <-- LOCKED after Phase 00: TS API/Hub DTO types
│   │       shell\                         <-- AppShell, dockable split panes, Ctrl+K palette, status bar
│   │       theme\                         <-- dark-first Mantine theme
│   │       store\                         <-- Zustand normalized store + plugin hook bus
│   │       grid\                          <-- TanStack Table + Virtual grids (<=50 DOM rows)
│   │       charts\                        <-- Apache ECharts dashboards
│   │       graphs\                        <-- Sigma.js + Graphology (Web Worker layout)
│   │       surfaces\                      <-- explorers\ dashboards\ single-page\ (report surfaces)
│   │       tools\                         <-- standalone tools, SERP preview, Finder picker
│   │       plugins\                       <-- plugin scanner UI, sandboxed iframe bridge
│   │
│   ├───electron\                          <-- main process: window catalog, backend supervisor, preload
│   └───mcp-stdio-adapter\                 <-- packaged stdio child-process bridge to loopback API
│
├───assets\                                <-- bundled, offline-only catalogues (never fetched at runtime)
│       waf-signatures\  stopwords\  dictionaries\  font-metrics\  tech-fingerprints\
│       bot-cidr\  ai-agent-profiles\  onnx-model\  export-templates\  plugin-schema\
│
├───tests\
│       CrawlForge.Contracts.Tests\  CrawlForge.Security.Tests\
│       CrawlForge.Data.Sqlite.Tests\  CrawlForge.Data.MySql.Tests\
│       CrawlForge.Data.Migrations.Tests\  CrawlForge.Data.Repositories.Tests\
│       CrawlForge.Crawler.Tests\          <-- per-area subfolders: Limits\ Frontier\ Fetching\ Waf\ ...
│       CrawlForge.Audit.Tests\            <-- per-area subfolders: Pipeline\ Indexability\ ...
│       CrawlForge.Intelligence.Tests\     <-- per-area subfolders: NGrams\ Embeddings\ Geo\ ...
│       CrawlForge.Api.Tests\  CrawlForge.Export.Tests\  CrawlForge.Mcp.Tests\
│       frontend\                          <-- vitest: contracts\ shell\ grid\ charts\ tools\ plugins\
│       electron\
│       CrawlForge.E2E.Tests\  CrawlForge.Boundary.Tests\
│       CrawlForge.Scale.Benchmarks\  CrawlForge.Acceptance.Tests\
│
├───tools\
│       governance-analyzer\               <-- static negative-requirement verifier (Phase 00)
│       network-audit\                     <-- air-gap boundary harness (Phase 07)
│
├───packaging\                             <-- offline installers, asset manifest (Phase 07)
└───docs\acceptance\                       <-- sign-off reports (Phase 07)
```

Per-agent build artifacts live in each project's `obj\<CF_AGENT_ID>\` and `bin\<CF_AGENT_ID>\` (§10.2); they are transient and git-ignored.

---

## 3. Context-Isolation Rule (mandatory)

1. Before writing any code, load **only** the files listed in your workpiece's `context_files` array. Every entry is an exact path inside `E:\Crawlforge\architecture\`. Workpieces list 1–3 files; that is the complete specification surface for your task.
2. **Never open** `E:\Crawlforge\architecture\baseline\functional-specification.md` or `E:\Crawlforge\architecture\baseline\crawlforge-extensions-and-ui-stack.md` (the monolithic masters FS-CRAWL-001 / FS-CRAWL-002, frozen as history). They are reserved for auditors and change-record authors, not builders.
3. Do not read architecture folders or domain files outside your `context_files`. If you believe a task needs an additional file, **stop and raise a blocker** (§7) — do not self-expand context.
4. `E:\Crawlforge\architecture\README.md` may be consulted **only** to resolve a path ambiguity, never as a substitute for the domain files (it is a summary; the domain files are normative).
5. The living architecture files are the v2.0 specification: the masters' content plus applied change records CR-001…CR-018 (see `architecture\changes\INDEX.md`, read-only). Where a domain file quotes a cross-reference (e.g. "§9.2"), the content you need is already inside your listed files or in the locked contracts. Treat the contracts as the compiled form of cross-module agreements. If a domain file appears to conflict with a locked contract, that is a discovery (§12.4) — stop and report; never pick a side yourself.
6. The orchestrator-built **dispatch packet** (§5.1) is the builder's complete task definition. Builders load nothing beyond the packet, this manual, and their `context_files`.

---

## 4. File-Ownership & Parallel Safety Rule (mandatory)

1. Your workpiece's `owned_paths` are your **only** write boundary inside `E:\Crawlforge\app\`. Create/modify/delete files nowhere else — not in sibling projects, not in shared config, not in another agent's test folder.
2. Workpieces in the same `parallel_group` (wave) are guaranteed to have **zero `owned_paths` overlap**. If you discover an overlap, treat it as a plan defect: report a blocker (§7) and stop; never "coordinate" by writing into the other agent's tree.
3. **Contract lock.** `src\backend\CrawlForge.Contracts\` and `src\frontend\src\contracts\` are frozen at the end of Phase 00. From Phase 01 onward:
   - Code **against** the locked interfaces/DTOs/TS types. Implement them; do not edit them.
   - If a contract is missing, wrong, or insufficient for your task, **report a blocker** (§7) naming the exact contract member and the required change. The orchestrator routes contract changes through a serialized amendment workpiece (§11); mid-phase mutation of shared contracts is forbidden.
   - Additive-only evolution applies (precedence rule 3): new optional fields may be proposed, parent fields may never be renamed or repurposed.
4. Project files (`.csproj`) inside your owned project folder are yours; the solution file, `Directory.Build.props`, root `package.json`, and workspace configs belong to Phase 00 / Phase 07 / the orchestrator only. New project references are added to **your own** `.csproj`, never to shared build files. Mid-phase package/dependency additions go through a serialized dependency amendment (§11); builders never run `npm install` (§10.3).
5. Test placement: each workpiece owns a dedicated test subfolder (see `owned_paths`). Use the workpiece's `verification_commands` filter (e.g. `--filter FullyQualifiedName~Waf`) so parallel agents never run each other's failing tests. All `dotnet` commands run under your `CF_AGENT_ID` (§10.2) so build/test artifacts never collide.
6. Assets: bundled catalogues live in the `assets\` subfolder assigned to your workpiece. Never introduce a runtime download path for any asset (negative requirements E2, E4, E8).

---

## 5. State Machine Protocol (orchestrator dispatch → execute → verify → record)

Workpiece states: `pending → in_progress → verifying → completed | blocked`.

**Ledger authority (v1.1).** The orchestrator is the **only** writer of `index.yaml` and the phase YAMLs. Builder agents never edit any file under `E:\Crawlforge\buildplan\`. All state transitions are performed by the orchestrator on behalf of builders, based on builder reports. This removes last-writer claim collisions by construction.

### 5.1 Dispatch (replaces agent self-claim)

1. The orchestrator reads `index.yaml`. A phase is dispatchable when its `depends_on_phases` are all `completed` (Phase 00 has none). Within a phase, a workpiece is dispatchable when `status: pending` **and** every entry in `depends_on_workpieces` is `completed`.
2. The orchestrator respects `max_parallel_agents` — a **ceiling, not a target** (§10.4): count `in_progress` + `verifying` workpieces in that phase before dispatching.
3. The orchestrator writes the claim (single writer, no race): in the phase YAML set the workpiece's `status: in_progress`, `assigned_agent: <teammate-name>`, `started_at: <ISO-8601 UTC>`; mirror in `index.yaml` (phase `status` to `in_progress` on first claim, refresh `status_summary`). No other workpieces' entries are touched.
4. The orchestrator spawns one builder teammate per workpiece (naming convention `cf-wp-xx-yy`, §10.2) and sends a **dispatch packet** containing: the verbatim workpiece YAML entry (including `architecture_sections`, `capabilities` with baseline states, `ui_surfaces`, `fixture_traps`, `contract_tests`, `reopens`, `done_when`), this manual, the exact `context_files` list, `owned_paths`, `deliverables`, `verification_commands`, and the builder's agent id.
5. A builder that cannot proceed (missing context, discovered `owned_paths` overlap, plan defect) reports a blocker (§7) without writing anything outside its `owned_paths`. A workpiece is never executed by an agent that was not dispatched for it.

### 5.2 Executing (builder)

1. Load `context_files` (§3), then the locked contracts relevant to your `interfaces_implemented`.
2. Implement every item in `deliverables` inside `owned_paths` (§4). Write unit tests alongside the implementation in your owned test folder.
3. Set `CF_AGENT_ID=<your-teammate-name>` for **every** `dotnet` invocation (§10.2). Never run `npm install` (§10.3).
4. Keep the negative-requirement guardrails (§6) in force at all times; run the governance analyzer locally before reporting ready-to-verify.

### 5.3 Verifying and completing (builder reports, orchestrator records)

1. When implementation is done, the builder reports **ready-to-verify** to the orchestrator, which sets `status: verifying` in the phase YAML.
2. The builder runs **every** command in `verification_commands` exactly as written (backend commands under `CF_AGENT_ID`, §10.2). All must exit 0. Builders do **not** additionally build the full solution or run canonical workspace typechecks — the shared compilation surface is proven by the orchestrator's wave-end gates (§5.5).
3. The builder reports **verified** with each command's exit code and a test-summary excerpt, or **failed** with diagnostics. On failure the builder fixes within its owned paths and re-verifies. If the failure originates outside its boundary (contract defect, missing dependency, plan error), the builder reports a blocker (§7) and stops.
4. On a verified report the orchestrator sets `status: completed`, `completed_at: <ISO-8601 UTC>` **only when the workpiece's `done_when` condition has been proven** (§5.6). Until then the workpiece stays `verifying`. The orchestrator updates the workpiece's phase counters in `index.yaml`; and, for each criterion in `acceptance_criteria_refs` **owned by this workpiece** (see `acceptance_criteria_ledger`), sets that ledger entry's `status: completed` only after the criterion's own end-to-end evidence exists. A criterion owned by an earlier workpiece is never re-owned here. The orchestrator may re-run any verification command before accepting a report.
5. A phase becomes `completed` in `index.yaml` only when **all** its workpieces are `completed` **and** every wave gate (§5.5) has passed; then recompute `status_summary` and unlock dependent phases.

### 5.4 Field discipline

- Allowed ledger edits, **orchestrator only**: `status`, `assigned_agent`, `started_at`, `completed_at`, `blocker_reason`, `blocked_at`, `status_summary` counts, phase `status`, criterion `status`.
- Builder agents have **zero** write access to `buildplan\` and `architecture\`.
- Forbidden without a formal orchestrator amendment (§11): editing `workpiece_id`, `context_files`, `owned_paths`, `deliverables`, `depends_on_*`, `parallel_group`, `verification_commands`, `orchestrator_gates`, or any structural field.

### 5.5 Orchestrator wave gates

After every wave completes, the orchestrator runs that phase's `orchestrator_gates` block (schema in §9) on the **canonical** build surface (`CF_AGENT_ID` unset): full-solution build, governance analyzer, and — where defined — canonical workspace typechecks. A failed gate blocks dispatch of the next wave and is triaged as a blocker against the offending workpiece; completed workpieces are re-opened only when the gate proves a defect in them.

### 5.6 The done_when completion gate (v2.0, mandatory)

1. Every v2.0 workpiece carries a `done_when` field naming the **fixture-acceptance-harness run and UI path walk** that must pass. Unit tests, typechecks and code reading are necessary but **never sufficient**: a workpiece whose tests are green but whose `done_when` has not been executed stays `verifying`, never `completed`.
2. Engine workpieces deliver components; the phase's **final serial wiring workpiece** owns the shared surfaces (DI registration in `Host\Composition\`, execution wiring in `Host\Execution\`, `Audit\Pipeline\`, frontend `App.tsx`, `store\`, shell/navigation and shared explorer/specialist index files). `done_when` for engine and UI workpieces is therefore proven **at the phase gate, after the wiring workpiece lands** — the orchestrator runs the harness and the UI walker once per phase and records the result against every workpiece it completes.
3. The harness (`app\tests\fixture-acceptance\run_fixture_acceptance.py`, built by WP-08-04) starts the fixture site, launches the app through `launch-crawlforge.ps1`'s normal entry points, runs audits, compares results with `app\tests\fixture-site\expected-results.json`, checks `/__fixture/requests` (global assertion: `nonGet == []`), and walks UI navigation paths over CDP. Its verdicts — not builder assertions — are the completion evidence.
4. A `done_when` that fails is a defect in the owning workpiece (or a discovery, §12.4): the orchestrator re-opens the workpiece, never relaxes the condition. Relaxing a `done_when`, a trap expectation, or an acceptance criterion requires a product-owner change record through the source-of-truth loop — builders and orchestrator alike never edit the spec, the register, or the plan's structural fields.

---

## 6. Clean-Room & Negative-Requirement Guardrails (17 exclusions)

Source of truth: `E:\Crawlforge\architecture\01-foundation-and-governance\01-clean-room-and-exclusions.md` (FS-CRAWL-001 §3 rows 1–8 + FS-CRAWL-002 §1.6 E1–E9). These prevail over **every** other requirement. The Phase 00 static analyzer (`tools\governance-analyzer`) enforces them on every build; a violation fails the workpiece.

| # | Excluded capability | Hard rule for your code |
|---|---|---|
| 1 | Machine identity / OS fingerprinting | Zero reads of OS registration stores (registry), management instrumentation (WMI), platform I/O registries, hardware serials, or any machine identifier. No hardware-derived identity is generated, stored, or transmitted. Entropy secrets are app-generated random bytes only. |
| 2 | Licensing client & state model | No license/subscription/trial/seat concepts, entitlement state machines, cache files, activation/expiry/grace/recheck services, or identity-bearing vendor headers. |
| 3 | Account system | No sign-in, registration, password reset, or user profile. |
| 4 | Remote control plane / relay | No enrollment, cloud relay, heartbeat/liveness reporting, remote credential issuance/rotation/revocation. The loopback-only local MCP server with locally generated credentials is explicitly permitted. |
| 5 | Remote feature flags | No remote flag resolution or proxying; every feature is enabled locally by default; toggles are local-only. |
| 6 | Telemetry & remote diagnostics | No usage analytics, remote crash reporting, identity-dimension log enrichment, or automatic transmission of any diagnostic data. Local structured logs and local native minidumps are permitted; user-viewable and purgeable. |
| 7 | Server-gated functionality | No report-viewing authorization, server-provided audit configuration, vendor cloud logging/alerting, account-bound update feeds, or marketing hooks. User-configured destinations are not vendor services. |
| 8 | Entitlement-based limits | No limit, feature, or report varies with license/account/remote input. |
| E1 | Remote plugin registry/marketplace | Plugins are local user-authored artifacts only; no browsing/download/auto-update/remote code loading. |
| E2 | Remote signature/stop-word catalog services | WAF signatures, stop-words, fingerprints are bundled assets; runtime fetch forbidden; user additions are local. |
| E3 | Challenge solving or bypass | No CAPTCHA solving, challenge replay, token forging, or evasion automation — detection and reporting only. |
| E4 | Cloud reputation/blocklist services | WAF verdicts derive only from the local catalogue + observed response. |
| E5 | Plugin backend mutation | Plugins get read-only data + UI contribution rights; never crawl lifecycle or configuration mutations. |
| E6 | New identity/entitlement concepts | No plugin licensing, accounts, or feature gating. |
| E7 | Remote embedding/vector/LLM APIs | Embeddings only from the bundled offline ONNX model or a user-configured **loopback-only** daemon; non-loopback endpoints rejected at configuration. |
| E8 | Remote log enrichment/bot-verification | Bot CIDR ranges and agent profiles are bundled. DNS verification is the only optional network action: user-triggered, off by default, per-import consent, locally logged. |
| E9 | Remote GEO/llms.txt registries or publishing | No remote registry, auto-publishing, or AI-vendor submission; generation writes local files only. |

**Network boundary (always on):** backend and hub bind loopback only; the built-in intercepting proxy opens no non-loopback listener. Outbound traffic is confined to: user-configured crawl targets (or configured proxy), user-configured export destinations, the user-configured external database, and the optional consented DNS check. Nothing else — ever.

**Precedence invariants (FS-CRAWL-002 §1.4 rules 1–9)** apply to every extension you implement: exclusions prevail; boundary unchanged; additive contract evolution only; baseline-preserving defaults (unconfigured project reproduces baseline exactly); limits resolution unchanged; provider agnosticism (SQLite and MySQL implement every logical schema object); scale invariant (500k+ URLs, bounded memory); privacy invariant (no identity/machine/account dimensions in any event, metric, log, or plugin surface); loopback-local services and offline ingestion.

---

## 7. Blocker Escalation Protocol

1. The builder **stops writing** and sends the orchestrator a blocker report: one paragraph naming the exact file/contract member, expected vs. actual, and the proposed minimal fix. Builders do not edit the ledger.
2. The orchestrator sets the workpiece `status: blocked` in the phase YAML with `blocker_reason` and `blocked_at`, and mirrors `blocked` in `index.yaml` counters. A phase is never marked completed while any workpiece is blocked.
3. A blocker is never resolved by: editing shared contracts mid-wave, writing outside `owned_paths`, reading forbidden context, weakening a negative requirement, or skipping a verification command.
4. The orchestrator triages blockers into: contract amendment (§11), dependency amendment (§11), plan correction (orchestrator amendment), or dependency re-ordering. Builders resume only when the orchestrator resets the workpiece to `pending` and re-dispatches.

---

## 8. Verification & Quality Gates

- **Backend workpieces:** owned tests green via the exact `verification_commands` filter (run under `CF_AGENT_ID`), governance analyzer pass; canonical `dotnet build` clean (warnings-as-errors on nullable/analyzers) proven at the orchestrator wave gate (§5.5).
- **Frontend workpieces:** `npm run typecheck` against locked TS contracts (non-emitting, §10.3), owned `vitest` suites green, no `any`-typed escape hatches around contract DTOs.
- **Every workpiece:** no new outbound traffic classes; no identity dimensions; bundled assets only; deterministic defaults seeded where specified; every cross-layer payload it adds or changes has a contract-conformance test (§12.2); its `done_when` passes at the phase gate (§5.6).
- **Acceptance gates (v2.0):** all 58 acceptance criteria (`07-quality-and-verification\01-master-acceptance-criteria.md`) are verified end-to-end against the fixture site through the UI or MCP — never by unit tests alone. Criteria #1–#48 are re-verified in phase-15 (the v1.1 claims are reopened); #49–#58 are the v2.0 end-to-end criteria gated in their flow phases. Criterion #40 uses the **product-owner-ratified composite gate**: p50 ≤ 16.7 ms, dropped-frame rate ≤ 1%, single-vsync fraction ≥ 95%, DOM rows ≤ 50 (worker heap ≤ 512 MB + termination unchanged); the literal p95 ≤ 16.7 ms gate is retired. Open scope decisions (`02-open-scope-decisions.md`) are respected as written — agents never invent product-owner decisions.

---

## 9. Phase & Wave Overview (contract-first parallelism)

Phases 00–07 (v1.1) are frozen history in `buildplan\v1.1\phases\`; their workpiece IDs remain valid references for `reopens`. The v2.0 plan continues the numbering:

| Phase | File | Waves (zero file overlap inside a wave) | Max parallel agents |
|---|---|---|---|
| phase-08 | `phases/phase-08-hotfix-record-and-foundations.yaml` | 8A: HOTFIX-01/01b records → 8B: contract-conformance harness, fixture acceptance harness → 8C: serial wiring | 2 |
| phase-09 | `phases/phase-09-flow-1-http-audit-end-to-end.yaml` | 9A: crawl/audit engine gaps (5) → 9B: REST/hub + project/config/dashboard/explorer/export UI (5) → 9C: serial wiring | 5 |
| phase-10 | `phases/phase-10-flow-2-rendering-and-capture-modes.yaml` | 10A: CDP layer + capture/comparison engine → 10B: rendered UI → 10C: serial wiring | 3 |
| phase-11 | `phases/phase-11-flow-3-site-types-spa-and-safety.yaml` | 11A: site-type rules, click discovery, wait/consent → 11B: config/inventory UI → 11C: serial wiring | 3 |
| phase-12 | `phases/phase-12-flow-4-report-surfaces-and-specialist-analysis.yaml` | 12A: specialist engines (7) → 12B: surfaces, shell, plugins, visualizations (8) → 12C: serial wiring | 7 |
| phase-13 | `phases/phase-13-flow-5-mcp-steering-and-agent-data-contract.yaml` | 13A: steering tools, agent contract, settings/tokens/log → 13B: serial wiring (#57 gate) | 3 |
| phase-14 | `phases/phase-14-flow-6-google-integrations.yaml` | 14A: connections/OAuth → 14B: sync/audit uses + test double → 14C: serial wiring (#58 gate) | 3 |
| phase-15 | `phases/phase-15-rebaselining-and-acceptance.yaml` | 15A: re-verify #1–#48 → 15B: #49–#58 full matrix → 15C: acceptance report (serial) | 1 |
| phase-16 | `phases/phase-16-distribution-unscheduled.yaml` | 16A: packaging + release (UNSCHEDULED — release-time only, after owner sign-off) | 2 |

**Why this is safe:** the locked contracts from v1.1 phase-00 (plus serialized amendments) still govern every cross-module interface. v2.0 workpieces implement against them in disjoint folders, so parallel builders never conflict. What changes is the **proof**: each phase is an end-to-end flow, and its final serial wiring workpiece + the fixture acceptance harness prove the flow through the running product (§5.6, §12). Shared wiring files are owned **only** by the serial workpiece in each phase — engine/UI builders deliver components and report `verifying`; the orchestrator completes them at the phase gate.

**Wave-gate schema (v1.1).** Each phase YAML carries an `orchestrator_gates` block, executed by the orchestrator after the named waves complete, on the canonical surface (`CF_AGENT_ID` unset):

```yaml
orchestrator_gates:
  after_each_wave:            # commands run after EVERY wave in the phase
    - "<command>"
  after_waves:                # optional extra commands keyed by wave_id
    wave-0B:
      - "<command>"
```

`max_parallel_agents` is a **ceiling, not a target**: the orchestrator may stagger dispatch or run fewer concurrent builders (§10.4) without any plan change.

---

## 10. Team Execution Protocol (v1.1)

### 10.1 Roles

- **Orchestrator (team lead):** sole ledger writer; computes dispatchable workpieces and claims them; spawns and dispatches builders; runs wave gates (§5.5); triages blockers; performs amendments (§11); re-dispatches failed agents. Writes only ledger fields under `buildplan\` and files inside amendment workpieces it owns.
- **Builder (teammate):** implements exactly one dispatched workpiece; writes only inside its `owned_paths`; reports `ready-to-verify` / `verified` / `failed` / `blocked` to the orchestrator with evidence (exit codes, test summaries). Never writes to `buildplan\` or `architecture\`.

### 10.2 Build isolation (CF_AGENT_ID)

- Phase 00's `Directory.Build.props` scopes MSBuild outputs per agent: when `CF_AGENT_ID` is set, every project uses `obj\$(CF_AGENT_ID)\` and `bin\$(CF_AGENT_ID)\` (relative paths resolve per project). Unset = canonical `obj\` / `bin\`.
- Every builder sets `CF_AGENT_ID=<its-teammate-name>` for **all** `dotnet` commands (build, test, run). Concurrent agents therefore never share `obj`/`bin`/restore artifacts, even when compiling the same solution or the same referenced projects.
- The orchestrator's wave gates run with `CF_AGENT_ID` **unset** (canonical surface).
- Teammate naming: `cf-wp-xx-yy` matching the workpiece id (e.g. `cf-wp-01-03`); `assigned_agent` records that exact name.

### 10.3 Dependency discipline

- `npm install` runs only in WP-00-01 or by the orchestrator. Builders never install packages.
- A new NuGet/npm dependency needed mid-phase is a **blocker** → serialized dependency amendment (§11). Root `package.json`, lockfiles, `Directory.Build.props`, and the solution file remain Phase 00 / Phase 07 / orchestrator property.
- Frontend verification is non-emitting (`tsc --noEmit`); no shared incremental cache writes (no shared `.tsbuildinfo`).

### 10.4 Throughput & agent failure

- Caps are ceilings: the orchestrator may stagger spawns or run fewer concurrent builders under rate-limit pressure; this requires no plan change.
- **Agent failure ≠ workpiece failure:** if a builder dies or stalls, the orchestrator resets the workpiece to `pending` and re-dispatches to a fresh teammate. Partial files inside `owned_paths` are inherited by the replacement (ownership is unchanged); `assigned_agent` is updated; nothing else mutates.

---

## 11. Contract & Dependency Amendment Protocol (v1.1)

1. Amendments are dedicated serialized workpieces created by orchestrator amendment: `WP-<phase>-A<n>` (contract) or `WP-<phase>-D<n>` (dependency), with `owned_paths` limited to the shared surface being changed (e.g. `CrawlForge.Contracts\` + its test folder) and explicit deliverables and `verification_commands`.
2. While an amendment runs, the orchestrator holds new dispatch in that phase; running builders finish their current verification or hold at a safe point. No builder writes against a contract surface mid-amendment.
3. After the amendment passes its verification and the wave gate, the orchestrator re-locks the contracts, resets affected blocked workpieces to `pending`, and re-dispatches. Additive-only evolution (precedence rule 3) applies to every amendment.
4. Amendments are recorded in the phase YAML and `index.yaml` like any workpiece; they count against `max_parallel_agents` (they run alone).

---

## 12. The v2.0 Proof Rules (mandatory)

These four rules exist because v1.1 checked workpieces in isolation with unit tests that supplied their own inputs, and declared the product complete while it could not run an audit from its own UI. They prevail over any convenience argument.

### 12.1 Fixture acceptance rule

- The definition of done is the **living architecture** (`E:\Crawlforge\architecture\`), proven against the **fixture site** (`app\tests\fixture-site\`, 31 traps in `expected-results.json`). A capability counts as working only when a real audit against the fixture produces the expected trap verdicts **through its bound UI surface** (or through MCP/API for the exempt capabilities listed in the UI surface map).
- Every workpiece's `done_when` names the harness run and UI path walk that prove it. The harness runs through the **normal entry points** (`launch-crawlforge.ps1` build steps + Electron shell + REST/hub as the UI uses them) — diagnostic workarounds, direct engine calls with hand-fed inputs, and seeded databases are not acceptance evidence.
- After every fixture audit the harness checks `GET /__fixture/requests`: `nonGet` must be empty (crawl safety, criterion #54). The `never-place-bet` trap must never be clicked or submitted.
- Trap expectations and criteria are spec artifacts. If the product and `expected-results.json` disagree, that is a **discovery** (§12.4) — never edit the expectations to make a run green.

### 12.2 Contract-conformance rule

- Every cross-layer payload — REST request/response, hub event, Electron IPC message, MCP tool/resource/prompt shape, storage-to-engine row — has a **conformance test** in `app\tests\CrawlForge.ContractConformance.Tests` asserting the exact wire shape against the locked contracts (both directions where applicable).
- A workpiece that adds or changes a payload lists it in `contract_tests` and delivers the test in the same workpiece. The known REST counter-key mismatch (engine `counters_json` keys vs `AuditWireMapper.ParseCounters`) is tracked as a `KnownGap` test owned by WP-09-06; phase-08 gates exclude exactly that gap, every later gate runs the full suite.
- Unit tests that construct their own inputs prove logic, not integration. They never substitute for a conformance test or a harness run.

### 12.3 UI reachability rule

- A surface is **reachable** only when a navigation path from the main window (per `06-frontend-shell-and-ui\04-ui-surface-map.md`) leads to it, and **functional** only when its bound capabilities can be configured, triggered, and viewed there with real data. A mounted component showing placeholder or empty-state-only content is not complete.
- The harness's UI walker drives the real Electron renderer over CDP and records each surface's configure/trigger/view result. Every in-scope surface must be made reachable and functional by some workpiece (checked by `check_buildplan.py`); the binding table in the UI map is authoritative for which capability belongs to which surface.
- Builders of UI workpieces test navigation, not just rendering: if your surface needs a route, a nav entry, or a store slice owned by the serial wiring workpiece, deliver the component and report the wiring need in your handoff — do not edit shared shell files yourself.

### 12.4 Discovery protocol (SOURCE-OF-TRUTH-LOOP §8)

- When a builder finds that the spec is ambiguous, the spec conflicts with a locked contract, a trap expectation is wrong, or reality differs from the plan: **stop, write nothing outside owned_paths, and report a discovery** to the orchestrator with exact file/section citations and observed evidence.
- The orchestrator records it in `audit-work\decisions-log.md` workflow: discoveries that change the specification become **change records** (`architecture\changes\CR-xxx`) approved by the product owner and applied to the living architecture **before** building continues; plan corrections go through orchestrator amendments (§11). Builders never edit the architecture, the register, the baseline, or the plan.
- A discovery is not a failure. Concealing one (working around it, guessing, weakening a test) invalidates the workpiece.

---

*End of operating manual. Authority order: negative requirements → living architecture domain files (with applied CRs) → locked contracts → this manual → workpiece YAML. Completion authority: the fixture acceptance harness and UI walker (§5.6, §12), never builder self-declaration.*
