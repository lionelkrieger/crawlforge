# Environment events — WP-08-04 acceptance session (2026-11-18, Africa/Johannesburg)

Measured BEFORE the first harness run; recorded per lead instruction. These are
session facts, not per-run measurements (per-run environment is in each
`verdicts.json` → `environment`).

1. **Orphaned fixture on 47311 (killed, identity-verified).** PID 7568,
   `node app/tests/fixture-site/server.mjs`, started 2026-11-17 19:02 — an
   orphan from a pre-probe-2 session, holding the fixture port. Identity was
   verified via CIM `Win32_Process.CommandLine` before `Stop-Process -Id 7568
   -Force`. The port was free afterwards; every harness run since starts its
   own fixture and stops it in `finally`.
2. **Ghost CDP listener on 9222 (cannot be killed; intermittent).** PID 17600
   shows LISTENING (+CLOSE_WAIT) on 127.0.0.1:9222, but no such process exists
   (`tasklist` / CIM empty) and `/json/version` times out — a dead process's
   socket still held by the kernel. There is no process to stop. The ghost is
   INTERMITTENT: at 01:41 a connect() probe reported the port free, the harness
   launched Electron on 9222, and Electron's devtools bind then failed
   (`tcp_socket_win.cc:590 bind() 0x2740`, `Cannot start http server for
   devtools` — results/20261117T234120Z/electron-stderr.log); the harness
   shut that instance down cleanly (taskkill fallback on its own identified
   tree, ports freed). Fix measured into the harness: `pick_cdp_port()` now
   consults `netstat -ano` LISTENING entries (kernel truth), not just a connect
   probe, and `launch_and_wait()` retries the other port after shutting down
   only its own failed instance. The ghost is never killed (nothing to kill).
3. **Port 47312 is reserved for cf-wp-08-03** (headless backend + isolated data
   dir). This workpiece never touches it; all harness work uses 47311 + the
   real `%APPDATA%\CrawlForge` data directory through normal entry points.
4. **Development artifacts in the real data directory (clearly named, kept as
   evidence):** project 5 "Fixture harness http 20261117T232842Z" (config 10;
   audit 14 started by the first UI-entry smoke run, then **cancelled via REST**
   after a harness bug stopped the fixture mid-run) and project 6 "Fixture
   harness http 20261117T233534Z" (config 11; audit 15 completed, 186 URLs /
   1522 findings — the successful UI-entry smoke run). The debug Electron
   instance was closed gracefully via CDP `Browser.close` before the full proof
   run (all electron.exe + CrawlForge.Host.dll processes drained; 9223 freed).
   No existing project/audit/configuration (incl. "Airpower", projects 1–4)
   was modified or deleted at any point.
5. **Toolchain measured:** dotnet 10.0.404 inside `app` (pinned SDK honoured),
   node v26.5.1, python 3.14.6, `@modelcontextprotocol/sdk` 1.32.2 hoisted at
   `app/node_modules` (no installs performed), MCP stdio adapter dist present.
6. **MCP schema observation (full-run evidence `mcp-list.json`):** the
   `start-audit` tool schema advertises optional `limitOverrides`, while
   probe-2 §6 recorded MCP as having no usable limit override. The harness
   does not rely on either behaviour: MCP audits run on REST-bounded
   configurations (40/3/90), exactly the probe-2 pattern.
7. **Evidence retention:** superseded harness-development run directories
   (20261117T232842Z, 233327Z, 233534Z, 233851Z, 234925Z — fixed harness bugs
   and debug iterations) were removed after the final proof run succeeded;
   their DB artifacts (projects 5–12 etc.) remain clearly named in the data
   directory. Kept: 20261117T234120Z (ghost-bind failure evidence),
   20261117T235434Z (audit-18 snapshot-race defect evidence + http/MCP/
   scheduled/walker measurements), 20261118T000559Z (exact verification
   command, exit 0), 20261118T000959Z (FINAL full proof run). Supplementary
   re-audit defect probes: `SUPPLEMENTARY-PROBES.md`.