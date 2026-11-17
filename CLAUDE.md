# CrawlForge: agent guide

CrawlForge is an offline-first desktop website crawler and SEO auditor: .NET 10 backend, Electron shell, React frontend, local MCP server. Owner: Lionel. Licence: AGPL-3.0 (contributions under a CLA).

## Source of truth (read before doing anything)

| What | Where | Rule |
|---|---|---|
| Definition of done | `architecture/` (v2.0, living master) | The **only** specification. Builders never edit it. Changes go through change records (`architecture/changes/`) approved by the owner. |
| Operating rules | `audit-work/SOURCE-OF-TRUTH-LOOP.md` | Authority flows decisions -> register -> architecture -> build plan -> app. Nothing downstream changes anything upstream. |
| Owner decisions | `audit-work/decisions-log.md` | D-001 onward. |
| Capabilities | `audit-work/register/capabilities.yaml`, `architecture/TRACEABILITY.md` | Stable IDs `CAP-<section>-<row>` / `CAP-<section>-Xnn`. |
| UI surfaces and bindings | `architecture/06-frontend-shell-and-ui/04-ui-surface-map.md` | Every in-scope capability is bound to a reachable screen (UI-01 to UI-24). |
| Build plan | `buildplan/` (v2.0; v1.1 is history in `buildplan/v1.1/`) | Orchestrator writes status fields; builders do not. |
| Acceptance | `architecture/07-quality-and-verification/` and `app/tests/fixture-site/` | Done = a full audit through the UI or MCP against the fixture site produces the expected results. Unit tests alone never complete a workpiece. |
| Measured state | `audit-work/reports/runtime-probe-2/` | Strongest evidence of what the app actually does. Audit documents are leads, not proof. |

If the spec is wrong, ambiguous or contradictory: **stop that item and report it** (loop section 8). Never change the spec or the plan to match what you built.

## Build and run

- SDK is pinned by `app/global.json` (.NET 10.0.404, rollForward latestFeature). Check with `dotnet --version` inside `app`; it must print 10.0.x. A .NET 11 preview is also installed on this machine and must not be used.
- Launch: `app\launch-crawlforge.ps1` (rebuilds stale backend, frontend and Electron shell, then launches). Launch logs: `%LOCALAPPDATA%\CrawlForge\logs`.
- Frontend loads over `file://`, so Vite `base` must stay `'./'`.
- Fixture site: `node app\tests\fixture-site\server.mjs` -> `http://127.0.0.1:47311`. `GET /__fixture/reset` before each run, `GET /__fixture/requests` after (the `nonGet` list must be empty).
- UI automation: run a **copy** of the launch script with `--remote-debugging-port=9222` (or 9223 if taken) and connect over CDP. Never edit the original script for probing.

## Ports and processes

- 47311 fixture site, 9222/9223 Electron remote debugging, backend port is discovered from the loopback handshake (`app/src/electron/src/handshake/port-discovery.ts`).
- Stale CrawlForge or fixture processes from earlier sessions often hold these ports. Check and stop them before starting.

## Data

- App data lives in `%APPDATA%\CrawlForge`. The owner's real projects (for example "Airpower") must never be modified or deleted. Create clearly named test projects ("Fixture ...", "HOTFIX ...") for any testing.

## Known gotchas

- **Git Bash path mangling:** arguments like `/api/...` or `/C` get rewritten to Windows paths. Prefix commands with `MSYS_NO_PATHCONV=1` or use PowerShell.
- **SDK resolution:** if `dotnet` reports "A compatible .NET SDK was not found", the .NET 10 SDK is not installed where the `dotnet` on PATH can see it; do not change `global.json` to the preview.
- **Static web assets:** builds under the .NET 11 preview needed `-p:StaticWebAssetsEnabled=false`; with the pinned .NET 10 SDK this should not be needed. If it is, report it.
- **Wire-shape mismatches** between layers caused the worst first-build defects (AIMD settings, host-throttle events, REST counter keys, `re-audit` id). Every cross-layer payload you add or change needs a contract-conformance test that feeds the real producer output into the real consumer.
- **Registration and mounting:** much first-build code exists but was never registered in DI, invoked in a real audit, or mounted in the UI. A feature is not done until it is wired and reachable through its bound UI path.
- `.workbuddy-ai/` belongs to a separate helper agent (WorkBuddy). Leave it alone; it is ignored by git.
- Large folders to exclude from searches: `bin`, `obj`, `node_modules`, `dist`, `tools-env`, `app/packaging/vendor`.

## Git

- Repository root `E:\Crawlforge`. Tags: `baseline-v1` (first build), `architecture-v2.0`.
- Commit only your own work, with a clear message; end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push, never force, never rewrite history.
- One change record per commit for architecture changes; workpiece IDs at the start of build commit messages.

## Session profiles

- Default sessions use `.claude/settings.json` (routine commands pre-approved).
- **Builder sessions** start with `claude --settings .claude/settings.builder.json`, which blocks edits to the specification, the build plan and the register.
