# Task: Fix the two blockers that stop CrawlForge audits from running (workpiece HOTFIX-01)

CrawlForge is a desktop website crawler and SEO auditor at `E:\Crawlforge\app` (.NET 10 backend in `app\src\backend`, Electron shell in `app\src\electron`, React frontend in `app\src\frontend`). The repository `E:\Crawlforge` is a git repository. The specification is `E:\Crawlforge\architecture\` v2.0. A runtime probe (`E:\Crawlforge\audit-work\runtime-probe\RUNTIME-PROBE.md` and `runtime-probe.json`) measured the current build against a local fixture site and found two blockers. Read the probe report first.

This is a **small, bounded fix**. Fix only what is listed. Do not refactor, do not implement other missing features, do not change the specification.

## Blocker 1: every audit fails at start

Audits started from the UI, REST or MCP fail within about 40 ms with `ArgumentOutOfRangeException ... initial rate must be positive (actual 0)` and 0 URLs. Cause found by the probe: the API stores the AIMD politeness settings of a configuration in a nested JSON shape, while the crawl engine parses a flat shape, so it reads the initial rate as 0.

Fix:

1. Make the stored shape and the parsed shape the same, using the contract defined in `architecture\03-crawl-engine\01-limits-and-aimd-politeness.md` (§3.3 parameters) and the configuration contract in `architecture\05-api-hub-and-mcp\01-rest-api-contract.md`. Choose the side to change so the REST contract stays as specified; if the spec is ambiguous, keep the REST shape and fix the engine's parsing.
2. Existing stored configurations in the user's database (for example the project "Airpower" and the probe project) must start working without manual edits: either read both shapes, or migrate stored rows through the existing migration mechanism.
3. An invalid or missing politeness value must never crash the audit at start: fall back to the documented defaults and record a local diagnostic.
4. When an audit does fail, the failure must be visible: the audit's state becomes failed with its error message, the REST and hub surfaces report it, and the main hub's audit list shows **failed** (not "RUNNING") with the message. Today the UI keeps showing a failed audit as running.

## Blocker 2: the audit run view crashes the whole window

Opening a running audit's run view blanks the entire renderer once the first host-throttle event arrives: uncaught `TypeError: Cannot read properties of undefined (reading 'toFixed')` in the Host throttle table (`ttfbEwmaMs`), caused by a mismatch between the hub event payload and what the frontend expects.

Fix:

1. Align the host-throttle event payload with the hub contract in `architecture\05-api-hub-and-mcp\02-realtime-websocket-hub.md` (and FS-CRAWL-002 §3.7 in `01-limits-and-aimd-politeness.md`), on whichever side is wrong.
2. The frontend must tolerate missing numeric fields in live payloads (render a dash, not crash).
3. Add an error boundary around each report and run-view surface so one failing panel shows an inline error and never blanks the whole window.

## Contract tests (required)

Both blockers are wire-shape mismatches between layers. For each, add a **contract-conformance test** that serialises the producer's real payload and parses it with the real consumer code: configuration politeness JSON (REST/storage to engine) and the host-throttle hub event (backend to frontend). These tests must fail on the old code and pass on the new code.

## Acceptance (must be demonstrated, not assumed)

1. Start the fixture site: `node app\tests\fixture-site\server.mjs` (http://127.0.0.1:47311).
2. Launch the app with `app\launch-crawlforge.ps1`.
3. In the UI: create a project with start URL `http://127.0.0.1:47311/`, press **Start audit** on the main hub, open the run view while it runs. The audit crawls URLs, the run view stays up through host-throttle updates, and the audit reaches a terminal state that the main hub shows correctly.
4. Start one audit through REST and one through MCP against the same project; both crawl URLs.
5. `GET http://127.0.0.1:47311/__fixture/requests` shows `nonGet` empty.
6. The new contract tests and the existing test suites for the touched projects pass.

If the fixture crawl runs long because of the calendar and filter crawler traps (not yet detected; separate later work), cap the run with a limit override; that is expected.

## Rules

- Change only code needed for these two blockers, their contract tests, and the failed-state visibility. Record anything else you notice in the report instead of fixing it.
- Commit once, message starting `HOTFIX-01:`, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Write `E:\Crawlforge\audit-work\hotfix-01-report.md`: root causes, files changed, which side of each contract was changed and why, the contract tests added, acceptance evidence for each of steps 3 to 6 (audit IDs, URL counts, states, screenshots path if taken), and anything noticed but not fixed.

## Final reply

Reply with the report's summary: root causes, the fix for each, acceptance results 3 to 6, and anything left open.
