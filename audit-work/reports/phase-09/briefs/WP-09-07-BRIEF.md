# WP-09-07 BUILDER BRIEF — Project Overview, Configurations & Configuration Editor UI (wave 9B)

You are builder **cf-wp-09-01** reused for WP-09-07 (assigned_agent records the actual name). Read this whole brief, then the context files, then implement.

## Mission model (AGENT-INSTRUCTIONS §5.6)
Components + component tests ONLY. You build surface components in surfaces\project\ (NEW folder). The shared shell (App.tsx, shell\, store\, HubDashboard.tsx, create-project-modal) is LEAD-OWNED (WP-09-11) — the lead mounts your components and adds hub affordances. Nothing is reachable until wiring; done_when UI-walk proof happens at the PHASE GATE. Your job: wire-ready components with the EXACT testids/markers below + vitest component tests + a mounting handoff.

## Hard rules
- Write ONLY inside owned_paths. NEVER edit: architecture\, audit-work\register\, buildplan\, decisions-log.md, SOURCE-OF-TRUTH-LOOP.md, fixture-site\expected-results.json, frontend shared shell (App.tsx, shell\*, store\*, contracts\*, explorers\ shared files, surfaces\dashboards\, surfaces\explorers\, surfaces\export\, surfaces\single-page\), backend code, harness files (ui_walker.py is the acceptance oracle — never weaken or edit it).
- No git commits. No new npm/NuGet dependencies (React 19 + Mantine 8 + Zustand already installed). No app launches. No fixture-harness runs. No CDP.
- Frontend loads over file:// — Vite base stays './'; no router assumptions beyond the existing ?route= convention (lead handles routing).
- Spec is the only source of truth; ambiguity → document interpretation + report (loop §8).
- Sibling breakage outside your paths → report to lead, don't edit.
- CR-026: do NOT touch schedule fields/code (recurrence rename is WP-12-10).
- Environment: `$env:CF_AGENT_ID='cf-wp-09-07'` for builds/tests; scratch %LOCALAPPDATA%\CrawlForge-cf-wp-09-07. NEVER touch %APPDATA%\CrawlForge or the owner's "Airpower" project. SDK pinned 10.0.404 for any dotnet use (never .NET 11 preview).

## owned_paths
- E:\Crawlforge\app\src\frontend\src\surfaces\project\   (NEW)
- E:\Crawlforge\app\tests\frontend\project\             (NEW)

## Full workpiece YAML (authoritative)
```yaml
- workpiece_id: "WP-09-07"
  title: "Project Overview, Configurations & Configuration Editor UI (13 Tabs)"
  phase_id: "phase-09"
  status: "pending"
  parallel_group: "wave-9B"
  depends_on_workpieces: ["WP-08-04"]
  context_files:
    - "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\02-virtualization-and-explorers.md"
    - "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\04-ui-surface-map.md"
    - "E:\\Crawlforge\\architecture\\03-crawl-engine\\01-limits-and-aimd-politeness.md"
  architecture_sections:
    - file: "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\02-virtualization-and-explorers.md"
      sections: ["8.8"]
    - file: "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\04-ui-surface-map.md"
      sections: ["1", "4"]
    - file: "E:\\Crawlforge\\architecture\\03-crawl-engine\\01-limits-and-aimd-politeness.md"
      sections: ["4.2", "3.8"]
  capabilities:
    - { id: "CAP-K-X01", baseline_state: "missing_ui" }
    - { id: "CAP-K-X02", baseline_state: "missing_ui" }
    - { id: "CAP-K-X03", baseline_state: "missing_ui" }
    - { id: "CAP-A-001", baseline_state: "missing_ui" }
    - { id: "CAP-A-002", baseline_state: "missing_ui" }
    - { id: "CAP-A-016", baseline_state: "missing_ui" }
    - { id: "CAP-A-017", baseline_state: "missing_ui" }
    - { id: "CAP-A-018", baseline_state: "missing_ui" }
    - { id: "CAP-A-019", baseline_state: "missing_ui" }
    - { id: "CAP-A-020", baseline_state: "missing_ui" }
  ui_surfaces: ["UI-02", "UI-03", "UI-04", "UI-10"]
  fixture_traps: ["noindex", "cookie-gate", "crawler-trap-filter"]
  contract_tests:
    - payload: "configuration editor form <-> REST configuration DTO (all 13 tabs)"
      test: "ConfigEditorWireConformanceTests (frontend contract types vs REST)"
    - payload: "project overview summary (audit counts, last-run status, storage)"
      test: "ProjectOverviewWireTests"
  reopens:
    - workpiece_id: "WP-06-02"
      evidence: "probe-2: UI-02/UI-03 not reachable, UI-04 has 0/13 tabs; G18 config CRUD invisible"
  owned_paths:
    - "E:\\Crawlforge\\app\\src\\frontend\\src\\surfaces\\project\\"
    - "E:\\Crawlforge\\app\\tests\\frontend\\project\\"
  deliverables:
    - "UI-02 project overview: audit list with status, start-audit action, storage/retention summary, links to Configurations/Integrations/Segments/Audits/Logs"
    - "UI-03 configurations list: create/open/duplicate/delete via REST CRUD"
    - "UI-04 configuration editor with all 13 tabs (Scope; Crawl as and capture; Limits; Rules; Directives; Resources and external scope; Authentication and cookies; Click discovery and site types; Consent states; Analysis categories; Thresholds; Extraction and search; Advanced) bound to the configuration DTO; tabs whose engine lands in later phases render their controls disabled-with-note until then, never as fake placeholders"
    - "Limits tab: unlimited-by-default ceilings with explicit override; AIMD parameters; per-configuration analysis category toggles and per-project thresholds"
  verification_commands:
    - "cd E:\\Crawlforge\\app\\src\\frontend && npm run build"
    - "cd E:\\Crawlforge\\app && npm run test -- --run tests/frontend/project"
  done_when: "Phase-gate UI walk: Main hub › Projects › select project › Configurations › open configuration reaches UI-04 with 13 tabs; changing the cookie-jar toggle and re-running the fixture audit flips cookie-gate between pass and redirect-loop (#56); changing a limit changes crawl behavior."
  acceptance_criteria_refs: [4, 56]
```

## ACCEPTANCE ORACLE — exact walker contract (ui_walker.py, read-only)
The phase-gate UI walker is the acceptance oracle. Your components must satisfy it AS WRITTEN (the lead does the mounting that makes these reachable):
- **UI-02**: walker clicks `project-row-{id}` (existing HubDashboard testid), then the dump must contain text/button/testid "Configurations" AND "Integrations", OR testid `project-overview`. → Your ProjectOverview component: root testid `project-overview`, sections with headings "Configurations" and "Integrations" (plus Segments/Audits/Logs links), audit list with status + start-audit action, storage/retention summary.
- **UI-03**: walker calls ensure_hub() FIRST and checks the HUB dump for "Configurations" (text/buttons/testids). The hub affordance itself is LEAD wiring (HubDashboard is shell-owned). Your ConfigurationsList component must carry a "Configurations" heading and list create/open/duplicate/delete actions bound to REST CRUD (WP-09-06 owns the endpoints; code against 05/01 §10.4 DTO).
- **UI-04**: walker clicks `new-project-button` → waits for `create-project-modal` (both existing HubDashboard testids, lead-owned) → waits 500ms → dump must contain ≥1 of the 13 tab names AND >3 inputs. → The lead will EMBED your ConfigurationEditor inside the create-project-modal. Your editor must: render all 13 tab labels EXACTLY as listed (Mantine Tabs labels count as buttons/text); the DEFAULT tab (Scope) must render >3 inputs (seeds textarea/input, include/exclude pattern inputs, host scope controls etc.); be self-contained (props: configuration DTO value + onChange + projectId optional; no shell imports).
- **13 tab names VERBATIM**: "Scope", "Crawl as and capture", "Limits", "Rules", "Directives", "Resources and external scope", "Authentication and cookies", "Click discovery and site types", "Consent states", "Analysis categories", "Thresholds", "Extraction and search", "Advanced".
- Later-phase engine tabs: controls DISABLED WITH NOTE ("engine lands in phase N"), never fake placeholders (CR-018).

## Wire shapes (code against these; do not edit contracts)
- REST configuration DTO: architecture 05/01 §10.4 + existing Api wire contracts (read CrawlForge.Api\Controllers\ConfigurationsController.cs + wire contracts for the exact JSON). PolitenessJson storage shape {"aimd":{...}} (HOTFIX-01; AimdSettingsParser accepts both shapes). Limits: null = unlimited sentinel (03/01 §4.2) — UI shows "Unlimited" default with explicit override input; never clamp.
- Cookie-jar toggle (criterion #56): "Authentication and cookies" tab must expose a cookie-jar enable/disable control bound to the configuration DTO field the engine reads (lead wires engine consumption; discover the field name from CrawlConfigurationDto/ScopeRulesJson — document it in your handoff; if no field exists, define the DTO extension as a handoff item for lead/09-06, do NOT edit backend).
- Hub events: contracts\hub\events.ts is READ-ONLY for you (lead adds CR-027 local-notification).
- apiClient/store: READ-ONLY (lead-owned). Your components take data via props or call the existing apiClient surface functions if they exist; document any new apiClient function you need as a handoff item (lead adds it to store/apiClient).

## Component tests (vitest, tests\frontend\project\)
Render each component with realistic DTO fixtures; assert: 13 tab labels present; default tab >3 inputs; cookie-jar toggle flips DTO value; limits unlimited-by-default + override; disabled-with-note for later-phase tabs; overview sections/markers; configurations CRUD calls (mock fetch/apiClient). Real-producer→real-consumer: feed the ACTUAL REST DTO JSON shape (copy from Api wire contracts / Api.Tests seeds) into your form binding and back.

## Verification (must end green, exact output in report)
1. `cd E:\Crawlforge\app\src\frontend && npm run build`
2. `cd E:\Crawlforge\app && npm run test -- --run tests/frontend/project`

## Final report format
Files · per-deliverable evidence · exact command output · MOUNTING HANDOFF (component export names + props + exact testids + where lead mounts: overview on project-row click, configurations list + hub affordance, editor embedded in create-project-modal; any apiClient/store additions needed; cookie-jar DTO field) · contract-test payloads for lead's ConfigEditorWireConformanceTests/ProjectOverviewWireTests · blockers/ambiguities. No commits.