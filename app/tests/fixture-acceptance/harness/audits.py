"""Audit lifecycle through normal entry points (REST / UI / MCP / schedule).

Every bulk audit carries explicit limit overrides {maxUrls:220, maxDepth:6,
maxDurationSeconds:180} because crawler-trap detection is absent on the current
build: the cap, not the product, bounds the run (recorded in evidence).
MCP has no limit overrides, so MCP audits use a REST-bounded configuration
{maxUrls:40, maxDepth:3, maxDurationSeconds:90} (probe-2 §6 pattern).
"""

from __future__ import annotations

import time

from .rest import RestClient

BULK_LIMITS = {"maxUrls": 220, "maxDepth": 6, "maxDurationSeconds": 180, "maxResponseBytes": None}
MCP_LIMITS = {"maxUrls": 40, "maxDepth": 3, "maxDurationSeconds": 90, "maxResponseBytes": None}
TERMINAL_STATES = {"completed", "failed", "cancelled", "canceled"}


class SnapshotRaceError(RuntimeError):
    """PRODUCT DEFECT (measured 2026-11-18, audit 18): AuditsController.StartAsync
    starts the engine (entity-DTO snapshot, numeric id) and then overwrites
    audits.configuration_snapshot_json with the WIRE snapshot (string id) —
    AuditsController.cs:140-160. RunAuditAsync (AuditExecutionEngine.cs:497)
    deserializes with PayloadJson, which cannot read string ids: whichever
    snapshot the engine reads first decides success/failure (a race). Same root
    cause as probe-2 §6's deterministic re-audit $.id error (engine.cs:436).
    The harness records every failed attempt and retries the SAME entry point;
    it never patches product state."""

    def __init__(self, audit_id, message: str, polled: dict):
        self.audit_id = audit_id
        self.polled = polled
        super().__init__(f"audit {audit_id}: configuration-snapshot race at start — {message}")


def is_snapshot_race(final: dict, elapsed_s: float) -> bool:
    """EXACT documented signature only (lead-approved binding condition 1):
    state failed <10s, 0 URLs fetched, errorMessage containing the
    CrawlConfigurationDto/$.id conversion error. Any other failure stands."""
    err = str(final.get("errorMessage") or "")
    counters = final.get("counters") or {}
    fetched = counters.get("fetched")
    return ("CrawlConfigurationDto" in err and "$.id" in err
            and str(final.get("state", "")).lower() == "failed"
            and elapsed_s < 10.0 and fetched in (0, None))


def create_project(rest: RestClient, name: str, description: str) -> dict:
    return rest.data("POST", "/api/projects", {"name": name, "description": description})


def create_configuration(rest: RestClient, project_id, name: str, start_urls: list[str],
                         rendering: dict | None = None, limits: dict | None = None,
                         description: str | None = None) -> dict:
    body: dict = {"name": name, "startUrls": start_urls, "respectRobotsTxt": True}
    if description is not None:
        body["description"] = description
    if rendering is not None:
        body["rendering"] = rendering
    if limits is not None:
        body["limits"] = limits
    return rest.data("POST", f"/api/projects/{project_id}/configurations", body)


def bound_configuration_limits(rest: RestClient, config_id, limits: dict) -> dict:
    """PUT the limits subset (other fields preserved by the subset mapper),
    then read back to verify. Used for the UI-started audit because the UI has
    NO limit controls (UI-04 absent) — recorded as evidence, not hidden."""
    rest.data("PUT", f"/api/configurations/{config_id}", {"limits": limits})
    return rest.data("GET", f"/api/configurations/{config_id}")


def start_audit_rest(rest: RestClient, config_id, limit_overrides: dict | None = None) -> str:
    body: dict = {"configurationId": str(config_id)}
    if limit_overrides is not None:
        body["limitOverrides"] = limit_overrides
    data = rest.data("POST", "/api/audits", body)
    return str(data["auditId"])


def poll_audit(rest: RestClient, audit_id, cap_s: float = 600.0, interval_s: float = 2.0) -> dict:
    """Poll GET /api/audits/{id} to a terminal state; log transitions."""
    t0 = time.monotonic()
    transitions: list[dict] = []
    last_sig = None
    final = None
    while True:
        audit = rest.data("GET", f"/api/audits/{audit_id}")
        final = audit
        state = str(audit.get("state", "")).lower()
        phase = str(audit.get("phase", "")).lower()
        counters = audit.get("counters") or audit.get("progress") or {}
        sig = (state, phase)
        elapsed = round(time.monotonic() - t0, 1)
        if sig != last_sig:
            transitions.append({"t": elapsed, "state": state, "phase": phase, "counters": counters})
            last_sig = sig
        if state in TERMINAL_STATES:
            polled = {"final": audit, "transitions": transitions, "elapsed_s": elapsed, "timed_out": False}
            if is_snapshot_race(audit, elapsed):
                raise SnapshotRaceError(audit_id, str(audit.get("errorMessage")), polled)
            return polled
        if time.monotonic() - t0 > cap_s:
            return {"final": audit, "transitions": transitions, "elapsed_s": elapsed, "timed_out": True}
        time.sleep(interval_s)


def start_with_retry(start_fn, rest: RestClient, fixture, fixture_log: list, label: str,
                     cap_s: float = 600.0, max_attempts: int = 3) -> tuple[str, dict, list]:
    """Start an audit through the given NORMAL entry point and poll it. If the
    product's configuration-snapshot race fails the start (SnapshotRaceError),
    record the failed attempt (id, error, safety) and retry the SAME entry point
    up to max_attempts. Every attempt is kept as evidence; if all attempts fail,
    the last failed result is returned so verdicts measure the failure (exit 1)
    — nothing is hidden or patched."""
    from .fixture import assert_no_non_get
    attempts: list = []
    last_aid = None
    last_polled = None
    for i in range(max_attempts):
        fixture.reset()
        aid = start_fn()
        last_aid = aid
        try:
            polled = poll_audit(rest, aid, cap_s=cap_s)
            if attempts:
                attempts.append({"attempt": i + 1, "audit_id": aid, "outcome": str(polled["final"].get("state"))})
            return aid, polled, attempts
        except SnapshotRaceError as e:
            last_polled = e.polled
            req = fixture.requests()
            ok, evidence = assert_no_non_get(req)
            rec = {"attempt": i + 1, "audit_id": aid, "outcome": "failed (product snapshot race)",
                   "error": str(e), "elapsed_s": e.polled.get("elapsed_s"),
                   "safety_ok": ok, "nonGet": req.get("nonGet") or []}
            attempts.append(rec)
            fixture_log.append({"audit_id": aid, "entry": f"{label} (failed start attempt {i + 1})",
                                "nonGet": req.get("nonGet") or [], "tagHits": req.get("tagHits") or [],
                                "ok": ok, "evidence": evidence})
    return last_aid, last_polled, attempts


def pull_audit_data(rest: RestClient, audit_id, trap_paths: list[str], base_url: str) -> dict:
    """Pull explorer data through REST only (probe-2 pull-audit.mjs pattern)."""
    out: dict = {"id": str(audit_id)}
    out["audit"] = rest.data("GET", f"/api/audits/{audit_id}")
    out["summary"] = rest.ok("GET", f"/api/audits/{audit_id}/summary")
    out["urls"] = rest.paged(f"/api/audits/{audit_id}/urls")
    out["links"] = rest.paged(f"/api/audits/{audit_id}/links")
    out["findings"] = rest.paged(f"/api/audits/{audit_id}/findings")
    out["trapUrlDetails"] = {}
    for path in trap_paths:
        full = base_url + path
        row = next((u for u in out["urls"] if u.get("canonicalUrl") == full), None)
        if row is None:
            row = next((u for u in out["urls"] if str(u.get("canonicalUrl") or "").startswith(full)), None)
        if row is None:
            out["trapUrlDetails"][path] = {"found": False}
            continue
        detail = rest.data("GET", f"/api/audits/{audit_id}/urls/{row['id']}")
        out["trapUrlDetails"][path] = {"found": True, "summary": row, "detail": detail}
    return out


def rendering_inertness(data: dict, rendering_enabled: bool) -> dict:
    """DETECT rendering inertness from evidence (never assume it):
    - rendering-dependent findings say 'no rendered tree is available'
    - /pages/js-only-target.html absent although rendering was enabled
    - /pages/js-content.html word count stays at the raw-markup value (~2)
    """
    findings = data.get("findings") or []
    no_tree = [f for f in findings if "no rendered tree is available" in str(f.get("message") or "").lower()]
    urls = data.get("urls") or []
    base = "http://127.0.0.1:47311"
    js_target_present = any(str(u.get("canonicalUrl") or "").startswith(base + "/pages/js-only-target.html") for u in urls)
    js_content_words = None
    det = (data.get("trapUrlDetails") or {}).get("/pages/js-content.html") or {}
    detail = det.get("detail") or {}
    scope = detail.get("scopeDiagnostics") or {}
    if scope:
        js_content_words = scope.get("totalWordCount")
    inert = bool(rendering_enabled) and (len(no_tree) > 0 or not js_target_present)
    evidence = (
        f"rendering.enabled={rendering_enabled}; 'no rendered tree is available' findings={len(no_tree)}; "
        f"/pages/js-only-target.html present={js_target_present}; "
        f"/pages/js-content.html totalWordCount={js_content_words}"
    )
    return {"inert": inert, "evidence": evidence, "no_tree_findings": len(no_tree),
            "js_target_present": js_target_present, "js_content_words": js_content_words}


def click_discovery_activity(data: dict) -> dict:
    """Detect whether click discovery actually exercised anything: click-zone
    link rows or the click-only SPA views (/spa/promotions, /spa/results)."""
    links = data.get("links") or []
    urls = data.get("urls") or []
    click_links = [l for l in links if "click" in str(l.get("domZone") or "").lower()]
    base = "http://127.0.0.1:47311"
    # Only the click-only SPA views count: /spa/promotions and /spa/results are
    # reachable solely via data-route buttons. /spa/markets does NOT count —
    # /live/odds.html links to it with a plain <a href> (measured in probe-2).
    click_only = [u for u in urls if str(u.get("canonicalUrl") or "") in
                  (base + "/spa/promotions", base + "/spa/results")]
    active = len(click_links) > 0 or len(click_only) > 0
    return {"active": active, "click_link_rows": len(click_links), "spa_view_urls": len(click_only),
            "evidence": f"click-zone link rows={len(click_links)}; click-only /spa/promotions|/spa/results URLs={len(click_only)}"}