# Task: Runtime probe of the current CrawlForge build against the fixture site

You are measuring what the **current** CrawlForge desktop app actually does, as built, so the project has a reliable as-built snapshot before any new building starts. This is a **measurement task, not a fixing task**. You must not change application behaviour. When something fails, record it with evidence and move on.

## Context

- Repository: `E:\Crawlforge` (git; tags `baseline-v1`, `architecture-v2.0`). The app is `E:\Crawlforge\app`: .NET 10 backend (`app\src\backend`), Electron shell (`app\src\electron`), React frontend (`app\src\frontend`), MCP stdio adapter (`app\src\mcp-stdio-adapter`). Launch script: `app\launch-crawlforge.ps1` (builds stale components, then launches Electron).
- Specification (living master): `E:\Crawlforge\architecture\` v2.0. Read `07-quality-and-verification\03-fixture-site.md`, `07-quality-and-verification\01-master-acceptance-criteria.md` §10 (#49 to #58), and `06-frontend-shell-and-ui\04-ui-surface-map.md` (surfaces UI-01 to UI-24 and their navigation paths).
- Fixture site: `E:\Crawlforge\app\tests\fixture-site\`. Read its `README.md` and `expected-results.json` (31 traps, each with URLs, capabilities, criteria, expected outcome and required capture mode).
- Known from earlier audits (`E:\Crawlforge\audit-work\reports\UNDERBUILD-AUDIT.md`): rendering may be inert in full audits, many report surfaces may be unmounted, and there is no configuration editor UI (only a New project dialog and a Start audit button on the main hub). Do not assume these are true; measure them.

## Hard rules

1. **Do not modify any file under `app\src`, `app\tests` (except outputs listed below), `architecture\`, `buildplan\` or `audit-work\register\`.** No bug fixes, no workarounds in product code. Building via the launch script is allowed.
2. Tools you install for probing (for example `playwright-core`) go into a scratch folder **outside** `app\`, for example `E:\Crawlforge\audit-work\history\runtime-probe-1\tools\`. Do not add dependencies to the app.
3. Never trigger the fixture site's "Place bet", "Deposit" or "Logout" controls yourself. The point is to see whether CrawlForge does.
4. Time-box each step. If a step is blocked (for example the app will not start), record the blocker with logs and continue with what can still be measured.
5. Outputs go only to `E:\Crawlforge\audit-work\history\runtime-probe-1\`. Commit them at the end with one commit; end the message with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Steps

### 1. Start the fixture site
Run `node app\tests\fixture-site\server.mjs` in the background (default `http://127.0.0.1:47311`). Confirm `/`, `/robots.txt`, `/sitemap.xml` and `/__fixture/requests` respond. Call `/__fixture/reset` before every audit run.

### 2. Start CrawlForge
Run `app\launch-crawlforge.ps1`. Record build output and whether the main window loads. To inspect and drive the UI, relaunch Electron with remote debugging enabled (for example add `--remote-debugging-port=9222` to the Electron command in a copy of the launch script placed in the scratch folder, not by editing the original) and connect with `playwright-core` `chromium.connectOverCDP`. Discover the backend's loopback port and session token the way the shell does (see `app\src\electron\src\handshake\port-discovery.ts` and the backend's loopback handshake descriptor) so you can also call the REST API directly.

### 3. UI surface walk (UI-01 to UI-24)
For each surface in `04-ui-surface-map.md` §1, try to reach it through its stated navigation path in the running app. Record one of: **reachable and functional** (renders real data or working controls), **reachable but placeholder** (empty frame, "coming soon", static text), **not reachable** (no navigation path), **not applicable yet** (depends on a feature that does not exist). Take a screenshot of each reachable surface. For the configuration editor (UI-04), record which of its 13 tabs and which section 5.1 inputs exist.

### 4. Audit runs against the fixture site
Create a project with start URL `http://127.0.0.1:47311/` through the UI if possible, otherwise through REST (record which). Run audits in every capture mode the current build supports (at least plain HTTP; JS-rendered if a setting for it exists anywhere in UI or REST; record exactly how you enabled it). For each run, record: audit state transitions, URL count, duration, whether it completed, and pull URLs, links, redirect chains and findings through REST (and the UI where reachable). After each run, read `/__fixture/requests` and record `nonGet` and `tagHits`.

### 5. Compare with expected results
For each of the 31 traps in `expected-results.json`, under each capture mode run: **pass**, **partial** or **fail** against its `expect` text, with the evidence (finding rule keys, URL states, values). Traps that require JS capture are `not testable` if the build cannot render in full audits; say so explicitly. Evaluate the global assertion `no-non-get-requests`.

### 6. MCP probe
Using the MCP stdio adapter the way an external client (Claude, VS Code) would: list tools, resources and prompts; start an audit of the fixture site; poll progress to completion; read `audit-summary` and `high-severity-findings`. Record what works, the exact tool list, and any errors. Check whether project or configuration management tools exist.

### 7. Acceptance criteria snapshot
For criteria #49 to #58, and for #1 to #48 where they can be exercised against the fixture site in this session, record pass / partial / fail / not testable with a one-line reason. Do not spend more than a few minutes per criterion.

## Outputs (in `E:\Crawlforge\audit-work\history\runtime-probe-1\`)

- `RUNTIME-PROBE.md`: summary first (what works end to end today, what does not, top blockers), then sections for steps 2 to 7.
- `runtime-probe.json`: machine-readable results with these top-level keys:
  - `environment` (versions, build result, ports);
  - `surfaces`: `{ "UI-01": { "status": "...", "evidence": "...", "screenshot": "..." } }`;
  - `runs`: per capture mode, with audit id, counts, duration, `nonGet`, `tagHits`;
  - `traps`: `{ "<trap id>": { "<mode>": { "result": "pass|partial|fail|not_testable", "evidence": "..." } } }`;
  - `mcp`: tools, resources, prompts, flow result;
  - `criteria`: `{ "49": { "result": "...", "reason": "..." } }`;
  - `capabilities`: for every capability ID named in the traps or bound to a surface you checked, `{ "evidence_level": "runtime", "result": "works|partial|fails|absent", "evidence": "..." }`.
- `screenshots\` with one PNG per reachable surface.
- `logs\` with launch, build and backend logs you relied on.

## Final reply

Reply with the summary section of `RUNTIME-PROBE.md`: counts of surfaces by status, traps by result per capture mode, MCP flow result, whether `nonGet` stayed empty, and the five most important blockers.
