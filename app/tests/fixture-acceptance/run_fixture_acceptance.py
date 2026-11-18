#!/usr/bin/env python3
"""WP-08-04 Fixture Acceptance Harness.

Runs the REAL CrawlForge product against app/tests/fixture-site through normal
entry points only (UI over CDP, REST, MCP stdio adapter, schedule run-now),
compares every trap against expected-results.json, walks every UI surface path
(CR-018), and writes machine-readable verdicts + evidence under results/<ts>/.

Usage (Python 3.14 stdlib only; Node >= 22 for the CDP/MCP helpers):
  python run_fixture_acceptance.py --mode http --traps noindex,canonical-conflict,soft-404-server,missing-alt
  python run_fixture_acceptance.py --mode http,js --traps all --surfaces all --mcp --scheduled
  python run_fixture_acceptance.py --mode dual --traps all          # all not_testable (no wire support)
  python run_fixture_acceptance.py --tls --traps mixed-content      # no cert material -> not_testable
  python run_fixture_acceptance.py --surfaces all                   # UI walker only

Modes: http | js | dual | adaptive (comma lists allowed).
Selectors: --traps / --surfaces accept comma-separated ids or 'all';
           --traps none skips audits (walker-only runs).
--tls is a convenience switch: with --tls-cert/--tls-key it serves the fixture
over HTTPS; without cert material it runs over HTTP and mixed-content stays
not_testable (a self-signed pair would not be trusted by the product fetcher —
there is no normal-entry-point control to bypass certificate validation).

Exit codes: 0 = no fail among selected traps and all global safety assertions
hold; 1 = at least one selected trap FAIL or a safety assertion failure (a full
matrix run exits 1 today — that is the measured reality, not a harness error);
2 = harness/infrastructure error. Surface statuses are recorded but do not
drive the exit code (the brief ties exit to selected traps + safety).

The harness never modifies product code, the fixture, or expectations; it
creates only clearly named "Fixture harness ..." projects in the real data
directory and never touches existing projects/audits/configs. It shuts down
what it started (Electron via CDP Browser.close, fixture process).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import time
import traceback
from pathlib import Path

HARNESS_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(HARNESS_DIR))

from harness import HARNESS_VERSION, WORKPIECE  # noqa: E402
from harness.audits import (BULK_LIMITS, MCP_LIMITS, SnapshotRaceError,  # noqa: E402
                            bound_configuration_limits, click_discovery_activity,
                            create_configuration, create_project, is_snapshot_race,
                            poll_audit, pull_audit_data, rendering_inertness,
                            start_audit_rest, start_with_retry)
from harness.cdp import CdpSession, CdpError  # noqa: E402
from harness.evidence import ResultsDir, build_summary  # noqa: E402
from harness.fixture import FixtureServer, assert_no_non_get, inspect_tag_hits  # noqa: E402
from harness.launch import (check_sdk, launch_and_wait, pick_cdp_port, shutdown_app,  # noqa: E402
                            cdp_targets, _cdp_responds, CDP_PORTS)
from harness.mcp import mcp_call, McpError  # noqa: E402
from harness.rest import RestClient, read_handshake, wait_backend  # noqa: E402
from harness.ui_walker import UiWalker, SURFACE_CATALOGUE  # noqa: E402
from harness.verdicts import (VERDICT_ENUM, count_verdicts, evaluate_matrix,  # noqa: E402
                              merge_audit_data)

FIXTURE_DIR = HARNESS_DIR.parent / "fixture-site"
EXPECTED_PATH = FIXTURE_DIR / "expected-results.json"
VALID_MODES = ("http", "js", "dual", "adaptive")


def log(msg: str) -> None:
    print(f"[fixture-acceptance] {msg}", flush=True)


def git_head() -> str:
    try:
        r = subprocess.run(["git", "-C", str(HARNESS_DIR), "rev-parse", "HEAD"],
                           capture_output=True, text=True, timeout=30)
        return (r.stdout or "").strip() or "unknown"
    except (OSError, subprocess.TimeoutExpired):
        return "unknown"


def parse_ids(value: str, valid: list[str], kind: str) -> list[str] | None:
    """'all' -> None (meaning every id); 'none' -> []; else validated list."""
    v = (value or "").strip().lower()
    if v in ("all", ""):
        return None
    if v == "none":
        return []
    ids = [x.strip() for x in value.split(",") if x.strip()]
    bad = [x for x in ids if x not in valid]
    if bad:
        raise SystemExit(f"unknown {kind} ids: {bad}; valid: {valid}")
    return ids


# --- entry-point flows -------------------------------------------------------

def safety_check(fixture: FixtureServer, run: dict, fixture_log: list) -> dict:
    req = fixture.requests()
    ok, evidence = assert_no_non_get(req)
    tags = inspect_tag_hits(req)
    entry = {
        "audit_id": run.get("audit_id"), "entry": run.get("entry_point"),
        "nonGet": req.get("nonGet") or [], "tagHits": req.get("tagHits") or [],
        "ok": ok, "evidence": evidence,
    }
    fixture_log.append(entry)
    run["safety"] = {"ok": ok, "evidence": evidence, "tag_hits": tags}
    run["fixture_requests"] = req
    log(f"audit {run.get('audit_id')}: safety nonGet==[] -> {ok} ({evidence}); tagHits={tags['total']}")
    return entry


def make_ctx(mode: str, run: dict, data: dict, fixture: FixtureServer, tls_active: bool) -> dict:
    rendering_enabled = mode == "js"
    inert = rendering_inertness(data, rendering_enabled)
    click = click_discovery_activity(data)
    run["rendering_inert"] = inert
    run["click_activity"] = click
    return {
        "base_url": fixture.base_url,
        "tls_active": tls_active,
        "rendering_enabled": rendering_enabled,
        "rendering_inert": inert,
        "click_activity": click,
        "fixture_requests": run.get("fixture_requests") or {},
        "tag_hits": (run.get("safety") or {}).get("tag_hits") or {},
        "audit_state": run.get("state"),
        "limit_overrides": run.get("limit_overrides"),
    }


def rest_bulk_audit(mode: str, rest: RestClient, fixture: FixtureServer, results: ResultsDir,
                    ts: str, traps: list[dict], selected: list[str] | None) -> tuple[dict, dict]:
    """REST-started bulk audit (probe-2 style) with explicit limit overrides."""
    name = f"Fixture harness {mode} {ts}"
    project = create_project(rest, name, f"{WORKPIECE} fixture acceptance — {mode} bulk matrix (REST entry)")
    pid = project["id"]
    rendering = ({"enabled": True, "deviceProfile": "desktop", "infiniteScroll": False, "captureConsole": True}
                 if mode == "js" else
                 {"enabled": False, "deviceProfile": None, "infiniteScroll": False, "captureConsole": False})
    config = create_configuration(
        rest, pid, f"Fixture harness {mode}", [fixture.base_url + "/"], rendering=rendering,
        description=f"{mode} capture; rendering.enabled={rendering['enabled']}; no UI capture-mode control exists (UI-04 absent)")
    cid = config["id"]
    audit_id, polled, attempts = start_with_retry(
        lambda: start_audit_rest(rest, cid, BULK_LIMITS),
        rest, fixture, results.fixture_log, f"REST {mode}", cap_s=600)
    log(f"mode {mode}: REST audit {audit_id} (project {pid}, config {cid}, limits {BULK_LIMITS}); start attempts={len(attempts) + 1}")
    final = polled["final"]
    if str(final.get("state", "")).lower() == "failed" and attempts:
        log(f"mode {mode}: ALL START ATTEMPTS FAILED (product snapshot race) — verdicts stay fail; MESSAGE LEAD")
    run = {
        "run_id": f"{mode}-rest", "mode": mode, "entry_point": "REST POST /api/audits",
        "project_id": pid, "project_name": name, "configuration_id": cid, "audit_id": audit_id,
        "limit_overrides": BULK_LIMITS, "state": str(final.get("state", "")).lower(),
        "transitions": polled["transitions"], "duration_s": polled["elapsed_s"],
        "timed_out": polled["timed_out"],
        "configuration_setup": f"REST-only configuration; rendering={json.dumps(rendering)}",
        "start_attempts": attempts,
    }
    safety_check(fixture, run, results.fixture_log)
    data = pull_audit_data(rest, audit_id, trap_paths(traps), fixture.base_url)
    run["url_count"] = len(data["urls"])
    run["finding_count"] = len(data["findings"])
    run["link_count"] = len(data["links"])
    results.write_json(f"audit-{audit_id}-data.json", data)
    ctx = make_ctx(mode, run, data, fixture, fixture.scheme == "https")
    run["traps"] = evaluate_matrix(traps, mode, ctx, data, selected)
    run["counts"] = count_verdicts(run["traps"])
    log(f"mode {mode}: audit {audit_id} {run['state']} — urls={run['url_count']} findings={run['finding_count']} "
        f"counts={run['counts']}")
    return run, data


def ui_entry_audit(rest: RestClient, cdp: CdpSession, fixture: FixtureServer, results: ResultsDir,
                   ts: str, traps: list[dict], selected: list[str] | None) -> tuple[dict, dict, dict]:
    """CR-023 UI entry: create the project through the UI New project dialog,
    bound the configuration limits via REST PUT (the UI has NO limit controls —
    UI-04 absent; recorded), press Start audit IN THE UI, inspect UI-10 over CDP.
    """
    name = f"Fixture harness http {ts}"
    evidence: dict = {"flow": "UI New project dialog -> REST PUT limits -> UI Start audit -> CDP UI-10 inspection",
                      "note_no_ui_limit_controls": "UI-04 configuration editor absent; limits bounded via REST PUT /api/configurations/{id} (subset update), read back to verify."}
    cdp.events(clear=True)
    cdp.click(testid="nav-hub")
    cdp.wait(900)
    cdp.click(testid="new-project-button")
    cdp.wait_for(testid="create-project-modal", timeout_ms=10000)
    cdp.wait(400)
    cdp.fill(name, testid="project-name-input")
    cdp.fill(fixture.base_url + "/", testid="project-url-input")
    cdp.fill(f"{WORKPIECE} fixture acceptance — UI entry (CR-023)", testid="project-description-input")
    cdp.shot(results.path("screenshots") / "UI-01-new-project-filled.png")
    cdp.click(testid="create-project-submit")
    cdp.wait(3000)
    create_events = cdp.events(clear=True)
    evidence["create_api_calls"] = [
        f"{r.get('method')} {r.get('url')} {(r.get('postData') or '')[:300]}"
        for r in create_events.get("requests") or [] if "/api/" in (r.get("url") or "")
    ]
    cdp.shot(results.path("screenshots") / "UI-01-after-create.png")
    # Resolve the created project + its default configuration through REST read.
    projects = rest.paged("/api/projects")
    project = next((p for p in projects if p.get("name") == name), None)
    if project is None:
        raise RuntimeError("UI project creation did not produce a project row (POST /api/projects failed?)")
    pid = project["id"]
    configs = rest.paged(f"/api/projects/{pid}/configurations")
    if not configs:
        raise RuntimeError("UI New project dialog did not create a default configuration")
    cid = configs[0]["id"]
    evidence["project_id"] = pid
    evidence["configuration_id"] = cid
    bounded = bound_configuration_limits(rest, cid, BULK_LIMITS)
    evidence["rest_limit_bounding"] = {"put_payload": {"limits": BULK_LIMITS}, "read_back_limits": bounded.get("limits")}
    log(f"UI entry: project {pid} created via dialog; config {cid} bounded via REST PUT {BULK_LIMITS}")
    # Press Start audit in the UI.
    row_present = cdp.count(f"[data-testid='project-row-{pid}']") > 0
    evidence["project_row_visible_in_hub"] = row_present

    def latest_audit_for_config() -> str:
        audits = rest.paged("/api/audits", params={"projectId": str(pid)})
        mine = [a for a in audits if str(a.get("configurationId")) == str(cid)]
        if not mine:
            raise RuntimeError("Start audit did not create an audit (POST /api/audits missing?)")
        return str(max(mine, key=lambda a: int(a["id"]))["id"])

    def ui_start_fn() -> str:
        cdp.events(clear=True)
        cdp.click(testid=f"start-audit-{pid}")
        cdp.wait(3500)
        start_events = cdp.events(clear=True)
        evidence.setdefault("start_api_calls", [
            f"{r.get('method')} {r.get('url')} {(r.get('postData') or '')[:300]}"
            for r in start_events.get("requests") or [] if "/api/" in (r.get("url") or "")
        ])
        return latest_audit_for_config()

    if not row_present:
        evidence["fallback"] = ("project row outside the hub's bounded project window; Start audit button not "
                                "reachable — started via REST for this run (ui_entry.proven=false)")
        log("UI entry: WARNING — project row not visible in hub window; REST fallback")
        start_fn = lambda: start_audit_rest(rest, cid, BULK_LIMITS)  # noqa: E731
        entry_point = "REST fallback (UI row outside bounded window)"
        proven = False
    else:
        start_fn = ui_start_fn
        entry_point = "UI Start audit button (CDP) + REST limit-bounding + REST poll"
        proven = True
    audit_id, polled, attempts = start_with_retry(start_fn, rest, fixture, results.fixture_log,
                                                  "UI entry" if proven else "REST fallback", cap_s=600)
    evidence["start_attempts"] = attempts
    if proven:
        cdp.shot(results.path("screenshots") / "UI-10-running.png")
        evidence["ui10_running_dump"] = cdp.dump()
    log(f"UI entry: audit {audit_id} ({entry_point}); start attempts={len(attempts) + 1}")
    final = polled["final"]
    if str(final.get("state", "")).lower() == "failed" and attempts:
        log("UI entry: ALL START ATTEMPTS FAILED (product snapshot race) — verdicts stay fail; MESSAGE LEAD")
    run = {
        "run_id": "http-ui", "mode": "http", "entry_point": entry_point,
        "project_id": pid, "project_name": name, "configuration_id": cid, "audit_id": audit_id,
        "limit_overrides": BULK_LIMITS, "state": str(final.get("state", "")).lower(),
        "transitions": polled["transitions"], "duration_s": polled["elapsed_s"],
        "timed_out": polled["timed_out"],
        "configuration_setup": "Project + default config created through UI dialog; rendering.enabled=false (default); limits bounded via REST PUT.",
        "start_attempts": attempts,
    }
    safety_check(fixture, run, results.fixture_log)
    data = pull_audit_data(rest, audit_id, trap_paths(traps), fixture.base_url)
    run["url_count"] = len(data["urls"])
    run["finding_count"] = len(data["findings"])
    run["link_count"] = len(data["links"])
    results.write_json(f"audit-{audit_id}-data.json", data)
    # Inspect the run view (UI-10) over CDP after completion.
    try:
        cdp.click(testid="nav-audits")
        cdp.wait(1200)
        if cdp.count(f"[data-testid='open-audit-{audit_id}']") > 0:
            cdp.click(testid=f"open-audit-{audit_id}")
        else:
            cdp.click(text="Open report")
        cdp.wait(2000)
        evidence["ui10_completed_dump"] = cdp.dump()
        cdp.shot(results.path("screenshots") / "UI-10-completed.png")
    except CdpError as e:
        evidence["ui10_inspection_error"] = str(e)
    evidence["proven"] = proven
    ctx = make_ctx("http", run, data, fixture, fixture.scheme == "https")
    run["traps"] = evaluate_matrix(traps, "http", ctx, data, selected)
    run["counts"] = count_verdicts(run["traps"])
    log(f"UI entry: audit {audit_id} {run['state']} — urls={run['url_count']} findings={run['finding_count']} counts={run['counts']}")
    return run, data, evidence


def wire_probe_run(mode: str, rest: RestClient, fixture: FixtureServer, results: ResultsDir,
                   ts: str, traps: list[dict], selected: list[str] | None) -> dict:
    """dual/adaptive: MEASURE the wire-contract absence (no audit is run — the
    mode cannot be exercised through any normal entry point)."""
    name = f"Fixture harness wire-probe {mode} {ts}"
    project = create_project(rest, name, f"{WORKPIECE} — probe for {mode} capture wire support")
    pid = project["id"]
    body = {
        "name": f"Fixture harness {mode} probe",
        "startUrls": [fixture.base_url + "/"],
        "respectRobotsTxt": True,
        "rendering": {"enabled": True, "deviceProfile": "desktop", "infiniteScroll": False, "captureConsole": True},
        "captureMode": mode,
        "renderingMode": mode,
    }
    status, created = rest.request("POST", f"/api/projects/{pid}/configurations", body)
    readback = None
    cid = None
    if isinstance(created, dict) and created.get("data"):
        cid = created["data"].get("id")
        readback = rest.data("GET", f"/api/configurations/{cid}")
    wire_evidence = (
        f"POST configuration with captureMode={mode!r}/renderingMode={mode!r} -> HTTP {status}; "
        f"read-back rendering={json.dumps((readback or {}).get('rendering'))}; "
        f"captureMode persisted={(readback or {}).get('captureMode') is not None}; "
        "RenderingSettingsWire members are only enabled/deviceProfile/infiniteScroll/captureConsole "
        "(app/src/backend/CrawlForge.Api/Controllers/ProjectWireContracts.cs:23) — no dual/adaptive capture contract exists."
    )
    results.write_json(f"wire-probe-{mode}.json", {"request": body, "status": status, "created": created, "readback": readback})
    run = {
        "run_id": f"{mode}-wire-probe", "mode": mode, "entry_point": "REST configuration wire probe (no audit — mode unsupported)",
        "project_id": pid, "project_name": name, "configuration_id": cid, "audit_id": None,
        "limit_overrides": None, "state": "not_run", "wire_probe_evidence": wire_evidence,
    }
    ctx = {"base_url": fixture.base_url, "tls_active": fixture.scheme == "https", "wire_probe_evidence": wire_evidence}
    run["traps"] = evaluate_matrix(traps, mode, ctx, {}, selected)
    run["counts"] = count_verdicts(run["traps"])
    log(f"mode {mode}: wire probe recorded; all selected traps not_testable ({run['counts']})")
    return run


def mcp_flow_run(rest: RestClient, fixture: FixtureServer, results: ResultsDir, ts: str,
                 traps: list[dict], selected: list[str] | None) -> dict:
    """--mcp: start/poll/read through the stdio adapter on a REST-bounded config
    (MCP has no limit overrides). MCP gaps are recorded as evidence."""
    name = f"Fixture harness MCP {ts}"
    project = create_project(rest, name, f"{WORKPIECE} — MCP entry point flow")
    pid = project["id"]
    config = create_configuration(
        rest, pid, "Fixture harness MCP bounded", [fixture.base_url + "/"], limits=MCP_LIMITS,
        description="MCP audit config bounded via REST limits (probe-2 §6 pattern; harness does not rely on MCP limitOverrides)")
    cid = config["id"]
    listing = mcp_call(["list"], timeout_s=120)
    results.write_json("mcp-list.json", listing)
    # MCP-native flow (start-audit -> get-audit-progress polling -> resources ->
    # prompt). Retry ONLY on the exact documented snapshot-race signature,
    # through the SAME MCP entry point, max 3 attempts; every attempt recorded.
    attempts: list = []
    flow = None
    audit_id = None
    final = None
    for i in range(3):
        fixture.reset()
        flow = mcp_call(["flow", str(pid), str(cid), "400"], timeout_s=500)
        audit_id = flow.get("auditId")
        if not audit_id:
            break
        final = rest.data("GET", f"/api/audits/{audit_id}")
        elapsed = float(final.get("elapsedSeconds") or 0.0)
        if is_snapshot_race(final, elapsed):
            req = fixture.requests()
            ok, ev = assert_no_non_get(req)
            attempts.append({"attempt": i + 1, "audit_id": audit_id,
                             "outcome": "failed (product snapshot race)",
                             "error": str(final.get("errorMessage")), "elapsed_s": elapsed,
                             "safety_ok": ok, "nonGet": req.get("nonGet") or []})
            results.fixture_log.append({"audit_id": audit_id, "entry": f"MCP start-audit (failed start attempt {i + 1})",
                                        "nonGet": req.get("nonGet") or [], "tagHits": req.get("tagHits") or [],
                                        "ok": ok, "evidence": ev})
            log(f"MCP flow: attempt {i + 1} audit {audit_id} hit the product snapshot race — recorded, retrying")
            continue
        break
    results.write_json("mcp-flow.json", flow)
    run = {
        "run_id": "mcp", "mode": "http", "entry_point": "MCP stdio adapter start-audit (REST-bounded config)",
        "project_id": pid, "project_name": name, "configuration_id": cid, "audit_id": audit_id,
        "limit_overrides": MCP_LIMITS, "state": "unknown",
        "mcp_bounded_note": ("Configuration bounded via REST POST (maxUrls=40, maxDepth=3, maxDurationSeconds=90) per probe-2 §6. "
                             "Note: the start-audit tool schema advertises optional limitOverrides (see mcp-list.json); "
                             "the harness does not rely on it — REST-bounded configuration is safe under either behaviour."),
        "mcp_gaps": {
            "management_tools": "none — no project or configuration management tools (see mcp-list.json)",
            "resource_templates": (listing.get("resourceTemplates") or {}).get("error", "listed"),
        },
    }
    run["start_attempts"] = attempts
    if audit_id and final is not None:
        run["state"] = str(final.get("state", "")).lower()
        run["transitions"] = [{"t": float(final.get("elapsedSeconds") or 0.0), "state": run["state"],
                               "phase": str(final.get("phase", "")).lower(), "counters": final.get("counters") or {}}]
        run["duration_s"] = float(final.get("elapsedSeconds") or 0.0)
        if run["state"] == "failed" and attempts:
            log("MCP flow: ALL START ATTEMPTS FAILED (product snapshot race) — verdicts stay fail; MESSAGE LEAD")
        safety_check(fixture, run, results.fixture_log)
        data = pull_audit_data(rest, audit_id, trap_paths(traps), fixture.base_url)
        run["url_count"] = len(data["urls"])
        run["finding_count"] = len(data["findings"])
        results.write_json(f"audit-{audit_id}-data.json", data)
        ctx = make_ctx("http", run, data, fixture, fixture.scheme == "https")
        run["traps"] = evaluate_matrix(traps, "http", ctx, data, selected)
        run["counts"] = count_verdicts(run["traps"])
        # re-audit defect probe (recorded as evidence, not a harness failure)
        try:
            reaudit = mcp_call(["reaudit", str(audit_id)], timeout_s=120)
            results.write_json("mcp-reaudit.json", reaudit)
            run["mcp_gaps"]["reaudit"] = reaudit.get("steps")
        except McpError as e:
            run["mcp_gaps"]["reaudit"] = str(e)
    else:
        run["traps"] = {t["id"]: {"result": "fail", "evidence": "MCP start-audit produced no auditId"} for t in traps
                        if selected is None or t["id"] in selected}
        run["counts"] = count_verdicts(run["traps"])
    log(f"MCP flow: audit {audit_id} state={run['state']} counts={run.get('counts')}")
    return run


def scheduled_flow_run(rest: RestClient, fixture: FixtureServer, results: ResultsDir, ts: str,
                       traps: list[dict], selected: list[str] | None) -> dict:
    """--scheduled: schedule entry point via REST POST schedules + run-now
    (UI-09 absent — recorded). The schedule is deleted afterwards (harness-owned
    artifact only); existing data is never touched."""
    name = f"Fixture harness scheduled {ts}"
    project = create_project(rest, name, f"{WORKPIECE} — schedule entry point flow")
    pid = project["id"]
    config = create_configuration(
        rest, pid, "Fixture harness scheduled bounded", [fixture.base_url + "/"], limits=MCP_LIMITS,
        description="Schedule run-now config bounded via REST (same cap rationale as MCP)")
    cid = config["id"]
    schedule = rest.data("POST", f"/api/projects/{pid}/schedules", {
        "name": f"Fixture harness schedule {ts}", "configurationId": str(cid),
        "cron": "interval:365.00:00:00", "enabled": True,
    })
    sid = schedule["id"]
    audit_id, polled, attempts = start_with_retry(
        lambda: str(rest.data("POST", f"/api/schedules/{sid}/run-now")["auditId"]),
        rest, fixture, results.fixture_log, "schedule run-now", cap_s=300)
    log(f"scheduled flow: schedule {sid} run-now -> audit {audit_id}; start attempts={len(attempts) + 1}")
    if str(polled["final"].get("state", "")).lower() == "failed" and attempts:
        log("scheduled flow: ALL START ATTEMPTS FAILED (product snapshot race) — verdicts stay fail; MESSAGE LEAD")
    run = {
        "run_id": "scheduled", "mode": "http", "entry_point": "REST POST /api/projects/{id}/schedules + /api/schedules/{id}/run-now",
        "project_id": pid, "project_name": name, "configuration_id": cid, "audit_id": audit_id,
        "schedule_id": sid, "limit_overrides": MCP_LIMITS,
        "start_attempts": attempts,
        "state": str(polled["final"].get("state", "")).lower(),
        "transitions": polled["transitions"], "duration_s": polled["elapsed_s"],
        "ui09_note": "UI-09 Schedules surface absent; schedule created and triggered through REST (recorded by walker).",
    }
    safety_check(fixture, run, results.fixture_log)
    data = pull_audit_data(rest, audit_id, trap_paths(traps), fixture.base_url)
    run["url_count"] = len(data["urls"])
    run["finding_count"] = len(data["findings"])
    results.write_json(f"audit-{audit_id}-data.json", data)
    runs_history = rest.data("GET", f"/api/schedules/{sid}/runs")
    run["schedule_run_history"] = runs_history
    ctx = make_ctx("http", run, data, fixture, fixture.scheme == "https")
    run["traps"] = evaluate_matrix(traps, "http", ctx, data, selected)
    run["counts"] = count_verdicts(run["traps"])
    results.write_json("scheduled-flow.json", {k: v for k, v in run.items() if k != "traps"})
    # Cleanup our own schedule (never existing data).
    try:
        rest.ok("DELETE", f"/api/schedules/{sid}")
        run["schedule_cleanup"] = "deleted (harness-owned artifact)"
    except Exception as e:  # noqa: BLE001
        run["schedule_cleanup"] = f"delete failed: {e}"
    log(f"scheduled flow: audit {audit_id} {run['state']} counts={run['counts']}")
    return run


def trap_paths(traps: list[dict]) -> list[str]:
    seen: list[str] = []
    for t in traps:
        for u in t.get("urls") or []:
            if u not in seen:
                seen.append(u)
    return seen


# --- main --------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description="WP-08-04 CrawlForge fixture acceptance harness")
    ap.add_argument("--mode", default="http", help="http|js|dual|adaptive (comma list allowed)")
    ap.add_argument("--traps", default="all", help="comma-separated trap ids, 'all', or 'none'")
    ap.add_argument("--surfaces", default="none", help="comma-separated UI ids, 'all', or 'none'")
    ap.add_argument("--mcp", action="store_true", help="also run the MCP entry-point flow")
    ap.add_argument("--scheduled", action="store_true", help="also run the schedule entry-point flow")
    ap.add_argument("--tls", action="store_true", help="TLS variant convenience switch (see --tls-cert/--tls-key)")
    ap.add_argument("--tls-cert", default=None, help="PEM certificate for the fixture HTTPS variant")
    ap.add_argument("--tls-key", default=None, help="PEM private key for the fixture HTTPS variant")
    ap.add_argument("--rest-start", action="store_true", help="start the http bulk audit via REST instead of the UI (debug)")
    ap.add_argument("--reuse-app", action="store_true", help="reuse an already-running harness app on 9222/9223 (debug; not shut down)")
    ap.add_argument("--keep-app", action="store_true", help="leave the app running after the run (debug)")
    ap.add_argument("--results-root", default=str(HARNESS_DIR / "results"), help="evidence root directory")
    args = ap.parse_args()

    modes = [m.strip().lower() for m in args.mode.split(",") if m.strip()]
    bad = [m for m in modes if m not in VALID_MODES]
    if bad:
        raise SystemExit(f"invalid --mode values: {bad}; valid: {VALID_MODES}")
    expected = json.loads(EXPECTED_PATH.read_text(encoding="utf-8"))
    all_traps = expected["traps"]
    trap_ids = [t["id"] for t in all_traps]
    surface_ids = [s[0] for s in SURFACE_CATALOGUE]
    selected_traps = parse_ids(args.traps, trap_ids, "trap")
    selected_surfaces = parse_ids(args.surfaces, surface_ids, "surface")
    if selected_traps is not None and len(selected_traps) == 0 and (selected_surfaces is not None and len(selected_surfaces) == 0):
        raise SystemExit("--traps none requires --surfaces (walker-only run)")
    traps = [t for t in all_traps if selected_traps is None or t["id"] in selected_traps]

    ts = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime())
    results = ResultsDir(Path(args.results_root), ts)
    results.fixture_log: list = []  # type: ignore[attr-defined]
    expected_sha = hashlib.sha256(EXPECTED_PATH.read_bytes()).hexdigest()

    tls_active = bool(args.tls_cert and args.tls_key)
    tls_note = None
    if args.tls and not tls_active:
        tls_note = ("--tls requested without --tls-cert/--tls-key: running over plain HTTP; mixed-content stays "
                    "not_testable. A self-signed pair is not generated because the product fetcher has no "
                    "normal-entry-point control to trust an untrusted certificate (it would fail for the wrong reason).")
        log(tls_note)
    if bool(args.tls_cert) != bool(args.tls_key):
        raise SystemExit("--tls-cert and --tls-key must be supplied together")

    exit_code = 0
    exit_reason = "no fail among selected traps; global safety assertions hold"
    runs: list[dict] = []
    surfaces: dict | None = None
    ui_entry_evidence: dict | None = None
    bulk_datas: dict[str, dict] = {}
    fixture = FixtureServer(tls_cert=args.tls_cert, tls_key=args.tls_key)
    cdp: CdpSession | None = None
    launched = False
    cdp_port = 0
    rest: RestClient | None = None

    try:
        sdk = check_sdk()
        log(f"pinned SDK: dotnet {sdk}")
        fixture.ensure_running(results.path("fixture-server.log"))
        log(f"fixture ready at {fixture.base_url} (started_by_us={fixture.started_by_us}, reused={fixture.reused})")

        # App launch (normal entry point) or reuse.
        port_notes: list[str] = []
        reuse = False
        if args.reuse_app:
            for p in (9222, 9223):
                if _cdp_responds(p) and any("index.html" in (t.get("url") or "") for t in cdp_targets(p)):
                    try:
                        rest = wait_backend(timeout_s=15)
                        cdp_port = p
                        reuse = True
                        break
                    except TimeoutError:
                        continue
        if not reuse:
            cdp_port, port_notes = pick_cdp_port()
            for n in port_notes:
                log("CDP port note: " + n)
            candidates = [cdp_port] + [p for p in CDP_PORTS if p != cdp_port]
            launched = True
            log(f"launcher finished; waiting for app readiness (candidates {candidates})")
            rest, page, cdp_port, retry_notes = launch_and_wait(results.root, candidates)
            port_notes.extend(retry_notes)
            for n in retry_notes:
                log("launch retry note: " + n)
            log(f"app ready: renderer {page.get('url')} backend {rest.base} CDP {cdp_port}")
        else:
            log(f"reusing running app on CDP {cdp_port}")
        cdp = CdpSession(cdp_port)
        log(f"CDP session attached to {cdp.page_url}")

        run_audit_flows = selected_traps is None or len(selected_traps) > 0
        if run_audit_flows:
            for mode in modes:
                if mode in ("dual", "adaptive"):
                    runs.append(wire_probe_run(mode, rest, fixture, results, ts, traps, selected_traps))
                    continue
                if mode == "http" and not args.rest_start:
                    run, data, ui_entry_evidence = ui_entry_audit(rest, cdp, fixture, results, ts, traps, selected_traps)
                    results.write_json("ui-entry.json", ui_entry_evidence)
                else:
                    run, data = rest_bulk_audit(mode, rest, fixture, results, ts, traps, selected_traps)
                runs.append(run)
                bulk_datas[mode] = data
            if args.mcp:
                runs.append(mcp_flow_run(rest, fixture, results, ts, traps, selected_traps))
            if args.scheduled:
                runs.append(scheduled_flow_run(rest, fixture, results, ts, traps, selected_traps))

        # Acceptance matrix per bulk mode — probe-2 convention: the session's
        # bulk runs are pooled (its §5 table cites 'Audit 11/12' jointly for
        # both mode columns). Per-run measurements remain in each run record.
        modes_doc: dict = {}
        bulk_modes = [m for m in modes if m in bulk_datas]
        if bulk_modes:
            pooled = merge_audit_data([bulk_datas[m] for m in bulk_modes])
            js_data = bulk_datas.get("js")
            inert_src = js_data if js_data is not None else bulk_datas[bulk_modes[0]]
            inert = rendering_inertness(inert_src, js_data is not None)
            merged_req: dict = {"nonGet": [], "tagHits": []}
            for m in bulk_modes:
                r = next(x for x in runs if x.get("run_id") in (f"{m}-rest", "http-ui") and x.get("mode") == m)
                fr = r.get("fixture_requests") or {}
                merged_req["nonGet"].extend(fr.get("nonGet") or [])
                merged_req["tagHits"].extend(fr.get("tagHits") or [])
            pool_ctx = {
                "base_url": fixture.base_url, "tls_active": tls_active,
                "rendering_inert": inert, "click_activity": click_discovery_activity(pooled),
                "fixture_requests": merged_req, "tag_hits": inspect_tag_hits(merged_req),
                "audit_state": "completed" if all(
                    next(x for x in runs if x.get("mode") == m and x.get("run_id") in (f"{m}-rest", "http-ui")).get("state") == "completed"
                    for m in bulk_modes) else "incomplete",
                "limit_overrides": BULK_LIMITS,
            }
            for mode in bulk_modes:
                tv = evaluate_matrix(traps, mode, pool_ctx, pooled, selected_traps)
                modes_doc[mode] = {"traps": tv, "counts": count_verdicts(tv),
                                   "evidence_pool": [bulk_datas[m].get("id") for m in bulk_modes],
                                   "pooling_note": "probe-2 convention: bulk runs pooled for the acceptance matrix; per-run verdicts in runs[]"}
            results.write_json("pooled-audit-data.json", pooled)

        # UI walker (None = 'all', [] = 'none').
        if selected_surfaces is None or len(selected_surfaces) > 0:
            walker_ctx = {
                "fixture_url": fixture.base_url,
                "project_id": next((r.get("project_id") for r in runs if r.get("project_id")), None),
                "project_name": next((r.get("project_name") for r in runs if r.get("project_name")), None),
                "audit_id": next((r.get("audit_id") for r in runs if r.get("audit_id")), None),
            }
            if walker_ctx["audit_id"] is None and rest is not None:
                audits = rest.paged("/api/audits")
                if audits:
                    latest = max(audits, key=lambda a: int(a["id"]))
                    walker_ctx["audit_id"] = str(latest["id"])
                    walker_ctx["audit_source"] = "latest existing audit (read-only inspection)"
            walker = UiWalker(cdp, results.root, walker_ctx)
            surfaces = walker.walk(selected_surfaces)
            results.write_json("surfaces.json", surfaces)
            sc: dict = {}
            for s in surfaces.values():
                sc[s["status"]] = sc.get(s["status"], 0) + 1
            log(f"UI walker statuses: {sc}")

        # Global assertions + exit code.
        global_ok = all(e["ok"] for e in results.fixture_log) if results.fixture_log else True
        global_assertions = [{
            "id": "no-non-get-requests",
            "check": "GET /__fixture/requests -> nonGet is empty after any audit",
            "criteria": [54],
            "ok": global_ok,
            "per_audit": [{k: e[k] for k in ("audit_id", "entry", "nonGet", "ok", "evidence")} for e in results.fixture_log],
        }]
        fails: list[str] = []
        for mode, m in modes_doc.items():
            for tid, v in m["traps"].items():
                if v["result"] == "fail":
                    fails.append(f"{mode}:{tid}")
        for r in runs:
            if r.get("run_id") in ("mcp", "scheduled") or r.get("mode") in ("dual", "adaptive"):
                for tid, v in (r.get("traps") or {}).items():
                    if v["result"] == "fail":
                        fails.append(f"{r['run_id']}:{tid}")
        if not global_ok:
            exit_code = 1
            exit_reason = "global assertion no-non-get-requests FAILED"
        elif fails:
            exit_code = 1
            exit_reason = f"{len(fails)} selected trap verdict(s) FAIL: {', '.join(fails[:12])}{'…' if len(fails) > 12 else ''}"

        # Product defect evidence (lead-approved handling): every failed start
        # attempt on the exact snapshot-race signature stays visible here AND
        # in each run record; retries never substitute another mode's data.
        failed_attempts = [
            {"run": r.get("run_id"), "mode": r.get("mode"), **a}
            for r in runs for a in (r.get("start_attempts") or []) if a.get("error")
        ]
        product_defects = None
        if failed_attempts:
            product_defects = {
                "configuration_snapshot_race": {
                    "description": ("AuditsController.StartAsync starts the engine (entity-DTO snapshot, numeric id) "
                                    "and then overwrites audits.configuration_snapshot_json with the WIRE snapshot "
                                    "(string id). RunAuditAsync deserializes with PayloadJson, which cannot read "
                                    "string ids: whichever snapshot the engine reads first decides success/failure "
                                    "(probabilistic start failure; deterministic for re-audit, which reads the "
                                    "stored WIRE snapshot)."),
                    "citations": [
                        "app/src/backend/CrawlForge.Api/Controllers/AuditsController.cs:140-160",
                        "app/src/backend/CrawlForge.Host/Execution/AuditExecutionEngine.cs:497 (start read path)",
                        "app/src/backend/CrawlForge.Host/Execution/AuditExecutionEngine.cs:436-438 (re-audit read path; probe-2 §6 $.id error)",
                    ],
                    "probe2_note": ("probe-2's audits 11/12/13 all won the race, so this is a newly-exposed "
                                    "probabilistic start defect (deterministic for re-audit), not a harness regression."),
                    "first_observed": {"audit_id": "18", "run": "js-rest",
                                       "evidence": "results/20261117T235434Z/audit-18-data.json (audit.errorMessage)"},
                    "suggested_routing": "WP-09-06 (CrawlForge.Api controllers) + WP-09-11 (Host execution) — per lead",
                    "retry_policy": ("EXACT signature only: state failed <10s, 0 URLs fetched, errorMessage contains "
                                     "CrawlConfigurationDto/$.id conversion error. Max 3 attempts, SAME configuration, "
                                     "SAME normal entry point. Every attempt recorded. All-fail => verdicts stay fail, "
                                     "exit non-zero, lead messaged. No product code changes."),
                    "failed_attempts_this_run": failed_attempts,
                }
            }

        verdicts_doc = {
            "harness": f"{WORKPIECE} fixture acceptance harness", "version": HARNESS_VERSION,
            "timestamp": ts, "git_head": git_head(),
            "fixture_base_url": fixture.base_url, "expected_results_sha256": expected_sha,
            "modes_requested": modes, "selected_traps": selected_traps if selected_traps is not None else "all",
            "selected_surfaces": selected_surfaces if selected_surfaces is not None else ("all" if args.surfaces.strip().lower() == "all" else "none"),
            "tls": {"active": tls_active, "note": tls_note},
            "cdp_port": cdp_port, "app_launched_by_harness": launched,
            "environment": {
                "cdp_port_notes": port_notes,
                "fixture_started_by_harness": fixture.started_by_us,
                "fixture_reused_existing": fixture.reused,
                "session_events_file": "results/ENVIRONMENT-NOTES.md (pre-run session measurements)",
            },
            "global_assertions": global_assertions,
            "product_defects": product_defects,
            "modes": modes_doc,
            "runs": [{k: v for k, v in r.items() if k not in ("fixture_requests",)} for r in runs],
            "ui_entry": ui_entry_evidence,
            "exit_code": exit_code, "exit_reason": exit_reason,
            "verdict_enum": list(VERDICT_ENUM),
        }
        results.write_json("verdicts.json", verdicts_doc)
        results.write_json("audit-ids.json", {
            "projects": [{"id": r.get("project_id"), "name": r.get("project_name"), "run": r.get("run_id")} for r in runs if r.get("project_id")],
            "configurations": [{"id": r.get("configuration_id"), "run": r.get("run_id")} for r in runs if r.get("configuration_id")],
            "audits": [{"id": r.get("audit_id"), "run": r.get("run_id"), "entry": r.get("entry_point"), "state": r.get("state")} for r in runs if r.get("audit_id")],
            "schedules": [{"id": r.get("schedule_id"), "run": r.get("run_id"), "cleanup": r.get("schedule_cleanup")} for r in runs if r.get("schedule_id")],
        })
        results.write_json("fixture-request-log.json", results.fixture_log)
        summary = build_summary(verdicts_doc, surfaces, runs)
        results.write_text("summary.md", summary)
        log(f"evidence written to {results.root}")
        for mode, m in modes_doc.items():
            log(f"acceptance mode {mode}: {m['counts']}")
        log(f"EXIT {exit_code}: {exit_reason}")
        return exit_code

    except Exception as e:  # noqa: BLE001 - infrastructure failure
        traceback.print_exc()
        log(f"HARNESS ERROR: {type(e).__name__}: {e}")
        try:
            results.write_json("harness-error.json", {"error": f"{type(e).__name__}: {e}", "traceback": traceback.format_exc()})
        except OSError:
            pass
        return 2
    finally:
        try:
            if launched and not args.keep_app:
                report = shutdown_app(cdp, cdp_port, launched)
                log(f"app shutdown: {json.dumps(report)}")
                try:
                    results.write_json("app-shutdown.json", report)
                except OSError:
                    pass
            elif launched and args.keep_app:
                log("--keep-app: leaving the app running")
        except Exception as e:  # noqa: BLE001
            log(f"shutdown warning: {e}")
        if cdp is not None:
            cdp.close()
        fixture.stop()
        log("fixture stopped" if fixture.started_by_us else "fixture left running (reused, not started by harness)")


if __name__ == "__main__":
    sys.exit(main())