# Task: Write the CrawlForge code map (`app/CODEMAP.md`)

You are documenting where things live in the CrawlForge codebase and, above all, **where things get registered, wired and mounted**. Read the code; do not change it. The first build's main failure was code that existed but was never registered in DI, never invoked during a real audit, or never mounted in the UI. This map exists so every future builder knows exactly which files must change for a feature to be live.

## Context

- Repository `E:\Crawlforge` (git). Read `E:\Crawlforge\CLAUDE.md` first.
- App: `E:\Crawlforge\app`. .NET 10 backend in `app\src\backend` (projects CrawlForge.Api, Audit, Contracts, Crawler, Data.*, Export, Host, Intelligence, Mcp, Security), Electron shell in `app\src\electron`, React frontend in `app\src\frontend`, MCP stdio adapter in `app\src\mcp-stdio-adapter`, tests in `app\tests`, fixture site in `app\tests\fixture-site`.
- Background on what is known to be unwired: `E:\Crawlforge\audit-work\reports\runtime-probe-2\RUNTIME-PROBE.md` and `E:\Crawlforge\audit-work\reports\UNDERBUILD-AUDIT.md`.
- Exclude `bin`, `obj`, `node_modules`, `dist` from every search.

## Rules

- Do not modify any file except creating `app\CODEMAP.md` and adding one line to `E:\Crawlforge\CLAUDE.md` (see Output).
- Every file path and symbol you cite must exist: verify each by opening or searching it. No guessed names.
- Prefer precision over prose: file path, then class/function, then one line on what it does.

## What the map must contain

1. **Layout:** one table of every project/package with its purpose (one line each).
2. **Startup sequence:** Electron main process → backend spawn and supervision → loopback handshake and port discovery → renderer load → hub connection. Name the file and symbol at each step.
3. **Backend composition root:** the exact file(s) and method(s) where services are registered (DI modules, hosted services, options binding). Show how to register a new service, with one real example from the code. List every composition module and what it registers.
4. **Audit execution path:** from "start audit" (REST and MCP) to completion: `AuditExecutionEngine` entry point, crawl loop, fetch, parse, audit pipeline steps, where analyzers and rules are registered and discovered, where findings and aggregates are written, where hub events are published. Mark explicitly where rendering should plug in and where it currently does not (rendered document hardcoded to null, per the audits).
5. **API surface:** how controllers are discovered and routed; where the REST contract types live; how the frontend route catalogue (`contracts/api/routes.ts`, `contracts-map.ts`) maps to them.
6. **Real-time hub:** where hub events are defined, published (producers) and consumed in the frontend (demux, store). List each event with its producer location, or "no producer" where none exists.
7. **MCP:** where tools, resources and prompts are registered, how they map to REST/services, where the stdio adapter connects, where the action log is written.
8. **Persistence:** providers, migrations, schema catalogue, and how to add a table or column end to end.
9. **Frontend:** entry point, shell, navigation (rail, command palette, routes/window state), **the mounting point for report surfaces and how a new surface becomes reachable**, store, API client, error boundary (added by HOTFIX-01). Map each UI surface UI-01 to UI-24 (`architecture\06-frontend-shell-and-ui\04-ui-surface-map.md`) to the component that implements it, or "not implemented".
10. **Electron:** main, preload bridge, IPC channels, window catalogue.
11. **Tests:** test projects and what each covers; contract-conformance tests (HOTFIX-01: `WireShapeContractTests`, `host-throttle-wire.test.ts`); fixture site; where the fixture acceptance harness will live (`app\tests\fixture-acceptance\`, build plan WP-08-04).
12. **"How to add" checklists**, each listing every file that must change for the feature to be live, in order: a new audit rule; a new configuration field (storage → REST → engine → configuration editor); a new REST endpoint; a new hub event; a new MCP tool; a new UI surface (component → navigation → mounting → store wiring); a new analyzer that must run in full audits. End each checklist with "Live when:" naming the end-to-end check that proves it (fixture harness or UI path).
13. **Known unwired or unmounted code:** a short table of components that exist but are not registered, invoked or mounted, with file and the missing registration point, cross-referenced to UNDERBUILD gap IDs where applicable.

## Output

- `app\CODEMAP.md`. Aim for something a builder can scan in five minutes: tables and short lines, no essays. Length is fine if the content is all lookup material.
- Add one line to `E:\Crawlforge\CLAUDE.md` under "Source of truth" pointing to `app/CODEMAP.md` as the map of where code is registered and mounted.
- Commit once: `Add app/CODEMAP.md: where code is registered, wired and mounted`, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Final reply

Reply with: the composition root location(s), the surface mounting point, counts from section 13, and anything that surprised you.
