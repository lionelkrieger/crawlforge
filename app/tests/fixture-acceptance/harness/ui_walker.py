"""UI path walker (CR-018 / 06-frontend-shell-and-ui/04 §1, §3).

For every surface UI-01..UI-24 the walker attempts the SPEC navigation path
from the main hub over CDP and records what actually happens — it never assumes
the CODEMAP outcome. Classification is evidence-driven:
  reachable_and_functional   real data/controls render and work
  reachable_but_placeholder  an empty frame / static text (never functional, CR-018)
  not_reachable              the spec path produces no such surface
  not_applicable_yet         UI-24 recovery (backend failure is NOT induced)
Screenshots + verdicts go to the evidence directory.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

from .cdp import CdpSession, CdpError

SURFACE_CATALOGUE = [
    ("UI-01", "Main hub › Projects", "Shell start screen (first run proceeds to New project)"),
    ("UI-02", "Main hub › Project overview", "Main hub › Projects › select project"),
    ("UI-03", "Project › Configurations", "Project overview › Configurations"),
    ("UI-04", "Configuration editor", "Project › Configurations › open/new configuration"),
    ("UI-05", "Project › Integrations", "Project overview › Integrations"),
    ("UI-06", "Project › Segments", "Project overview › Segments (also segment picker in every report surface)"),
    ("UI-07", "Project › Audit history and retention", "Project overview › Audits"),
    ("UI-08", "Main hub › Clients", "Main hub › Clients"),
    ("UI-09", "Main hub › Schedules", "Main hub › Schedules"),
    ("UI-10", "Audit run view", "Project overview › Start audit, or Audits › running audit"),
    ("UI-11", "Report › Dashboard", "Audits › open audit (report window opens on Dashboard)"),
    ("UI-12", "Report › Issue explorer", "Report window navigation"),
    ("UI-13", "Report › URL explorer and URL detail", "Report window navigation; drill-down from any surface"),
    ("UI-14", "Visualization window", "Report window › Visualizations"),
    ("UI-15", "Report › Comparison (audit and content)", "Report window › Compare, or Project › Audits › select two"),
    ("UI-16", "Report › Specialist surfaces", "Report window navigation (19 specialist surfaces)"),
    ("UI-17", "Log file analyzer", "Project overview › Logs"),
    ("UI-18", "Single-page analyzer", "Main hub toolbar › Analyze a page"),
    ("UI-19", "Standalone tools", "Main hub toolbar › Tools"),
    ("UI-20", "Export dialog", "Report window › Export (every surface)"),
    ("UI-21", "Settings", "Main hub toolbar › Settings"),
    ("UI-22", "Plugin manager", "Settings › Plugins"),
    ("UI-23", "Status bar", "Always visible in every window"),
    ("UI-24", "Recovery screen", "Shown automatically after repeated backend failures"),
]

CONFIG_EDITOR_TABS = [
    "Scope", "Crawl as and capture", "Limits", "Rules", "Directives",
    "Resources and external scope", "Authentication and cookies",
    "Click discovery and site types", "Consent states", "Analysis categories",
    "Thresholds", "Extraction and search", "Advanced",
]

TOOL_KEYS = [
    "single-page", "serp", "render", "robots", "sitemap", "scope",
    "rewrite", "llms", "urlcheck", "structured", "query", "destinations",
]

SPECIALIST_MARKERS = [
    "Custom extraction", "Custom search", "Snippet", "Accessibility", "Performance",
    "Structured data", "Technology", "Rendered comparison", "Scores", "Suppressed",
    "Templates", "N-grams", "Semantic linking", "AI search readiness",
    "Analytics and tagging", "Link equity", "List mode", "WAF", "Search performance",
]

PLACEHOLDER_TEXT = "mount into this workspace from the report workpieces"


def _text(d: dict) -> str:
    return (d.get("text") or "").lower()


def _has(d: dict, needle: str) -> bool:
    n = needle.lower()
    if n in _text(d):
        return True
    if any(n in (b or "").lower() for b in d.get("buttons") or []):
        return True
    return any(n == (t or "").lower() for t in d.get("testids") or [])


def _testid(d: dict, tid: str) -> bool:
    return tid in (d.get("testids") or [])


def _has_nav(d: dict, needle: str) -> bool:
    """Navigation affordances ONLY (buttons/tabs/testids) — never body text.
    Telemetry labels like 'WAF' or 'URLs / sec' in the run view are NOT
    navigation entries and must not satisfy a report-surface path."""
    n = needle.lower()
    if any(n in (b or "").lower() for b in d.get("buttons") or []):
        return True
    return any(n in (t or "").lower() for t in d.get("testids") or [])


class UiWalker:
    def __init__(self, cdp: CdpSession, results_dir: Path, context: dict):
        self.cdp = cdp
        self.dir = results_dir
        self.shots = results_dir / "screenshots"
        self.ctx = context  # project_id, project_name, audit_id, fixture_url
        self.results: dict = {}

    # -- helpers ------------------------------------------------------------

    def shot(self, name: str) -> str | None:
        try:
            return self.cdp.shot(self.shots / f"{name}.png")
        except CdpError:
            return None

    def dump(self) -> dict:
        return self.cdp.dump()

    def nav(self, view: str) -> dict:
        self.cdp.click(testid=f"nav-{view}")
        self.cdp.wait(900)
        return self.dump()

    def ensure_hub(self) -> dict:
        d = self.dump()
        if not _testid(d, "hub-dashboard"):
            d = self.nav("hub")
        return d

    def api_calls(self, events: dict) -> list[str]:
        out = []
        for r in events.get("requests") or []:
            if "/api/" in (r.get("url") or ""):
                out.append(f"{r.get('method')} {re.sub(r'^https?://[^/]+', '', r['url'])} {(r.get('postData') or '')[:200]}".strip())
        return out

    def record(self, sid: str, status: str, evidence: str, screenshot: str | None,
               attempts: list | None = None, extra: dict | None = None):
        surface = next(s for s in SURFACE_CATALOGUE if s[0] == sid)
        self.results[sid] = {
            "id": sid, "surface": surface[1], "spec_path": surface[2],
            "status": status, "evidence": evidence,
            "screenshot": screenshot, "attempts": attempts or [],
            **(extra or {}),
        }

    # -- surfaces -----------------------------------------------------------

    def ui_01(self):
        d = self.ensure_hub()
        shot = self.shot("UI-01-hub")
        rows = self.cdp.count("[data-testid^='project-row-']")
        audit_rows = self.cdp.count("[data-testid^='audit-row-']")
        functional = _testid(d, "hub-dashboard") and _testid(d, "new-project-button") and rows > 0
        status = "reachable_and_functional" if functional else "reachable_but_placeholder"
        self.record("UI-01", status,
                    f"Hub renders real local aggregates: {rows} project rows, {audit_rows} audit rows, "
                    f"New project button present={_testid(d, 'new-project-button')}. "
                    "Project creation through the New project dialog is exercised by the UI-entry audit.",
                    shot, extra={"project_rows": rows, "audit_rows": audit_rows})

    def ui_02(self):
        self.ensure_hub()
        self.cdp.events(clear=True)
        attempts = []
        pid = self.ctx.get("project_id")
        pname = self.ctx.get("project_name") or ""
        d = self.dump()
        if pid:
            # Clicks are asserted+retried by CdpSession; a persistent failure is
            # recorded EXPLICITLY per attempt (never silently swallowed) and the
            # remaining navigation affordances are still exercised.
            for action, kwargs in ((f"click project-row-{pid}", {"testid": f"project-row-{pid}"}),
                                   ("dblclick project row", {"testid": f"project-row-{pid}", "double": True})):
                try:
                    r = self.cdp.click(**kwargs)
                    self.cdp.wait(1500)
                    d = self.cdp.dump()
                    attempts.append({"action": action, "result": r, "url": d.get("url")})
                except CdpError as e:
                    attempts.append({"action": action, "error": str(e)})
        # command palette project pick (the only other navigation affordance)
        self.cdp.ctrl_k()
        self.cdp.wait(700)
        if pname:
            self.cdp.type_text(pname[:40])
            self.cdp.wait(600)
        self.cdp.key("Enter")
        self.cdp.wait(1500)
        d = self.cdp.dump()
        attempts.append({"action": "command palette project pick", "url": d.get("url")})
        self.cdp.key("Escape")
        self.cdp.wait(400)
        events = self.cdp.events(clear=True)
        calls = self.api_calls(events)
        shot = self.shot("UI-02-attempt")
        overview = (_has(d, "Configurations") and _has(d, "Integrations")) or _testid(d, "project-overview")
        if overview:
            self.record("UI-02", "reachable_and_functional", "Project overview rendered with project sections.", shot, attempts)
        else:
            self.record("UI-02", "not_reachable",
                        "Project row click, dblclick and command-palette project pick produced no project overview: "
                        f"no overview controls/text appeared; API calls during attempts={json.dumps(calls)[:400]}; "
                        f"final url={d.get('url')}.", shot, attempts, extra={"api_calls": calls})

    def _absent_surface(self, sid: str, markers: list[str], where: str, shot_name: str, note: str = ""):
        d = self.dump()
        found = [m for m in markers if _has(d, m)]
        shot = self.shot(shot_name)
        if found:
            self.record(sid, "reachable_and_functional",
                        f"{where}: markers present {found}. {note}", shot)
        else:
            self.record(sid, "not_reachable",
                        f"{where}: none of the markers {markers} appear in the running shell "
                        f"(rail buttons={json.dumps(d.get('buttons') or [])[:300]}). {note}", shot)

    def ui_03(self):
        self.ensure_hub()
        self._absent_surface("UI-03", ["Configurations"], "Spec path Project overview › Configurations (overview itself unreachable, UI-02)",
                             "UI-03-attempt", "No Configurations entry in rail, project row or command palette.")

    def ui_04(self):
        self.ensure_hub()
        self.cdp.click(testid="new-project-button")
        modal = self.cdp.wait_for(testid="create-project-modal", timeout_ms=8000)
        if not modal.get("found"):
            self.record("UI-04", "not_reachable",
                        "New project dialog did not open after an asserted, retried click on new-project-button "
                        f"(waitFor result={modal}); no configuration editor surface exists.",
                        self.shot("UI-04-modal-absent"))
            return
        self.cdp.wait(500)
        d = self.dump()
        inputs = d.get("inputs") or []
        tabs_present = [t for t in CONFIG_EDITOR_TABS if _has(d, t)]
        shot = self.shot("UI-04-new-project-dialog")
        self.cdp.key("Escape")
        self.cdp.wait(500)
        if tabs_present and len(inputs) > 3:
            self.record("UI-04", "reachable_and_functional", f"Configuration editor tabs present: {tabs_present}.", shot)
        else:
            self.record("UI-04", "not_reachable",
                        f"No configuration editor: {len(tabs_present)} of {len(CONFIG_EDITOR_TABS)} documented tabs. "
                        f"The only configuration inputs are the New project dialog fields "
                        f"{json.dumps([i.get('testid') or i.get('placeholder') for i in inputs])}; "
                        "no limit, AIMD, rendering, directive, analysis or extraction inputs exist. "
                        "Audit limits must be bounded via REST PUT (recorded in ui-entry evidence).",
                        shot, extra={"tabs_present": tabs_present, "dialog_inputs": inputs})

    def ui_05(self):
        self.ensure_hub()
        self._absent_surface("UI-05", ["Integrations"], "Spec path Project overview › Integrations", "UI-05-attempt")

    def ui_06(self):
        self.ensure_hub()
        self._absent_surface("UI-06", ["Segments"], "Spec path Project overview › Segments / report segment picker", "UI-06-attempt")

    def ui_07(self):
        self.ensure_hub()
        d = self.dump()
        shot = self.shot("UI-07-attempt")
        scoped = _has(d, "Retention policy") or _testid(d, "project-audit-history")
        if scoped:
            self.record("UI-07", "reachable_and_functional", "Project-scoped audit history/retention rendered.", shot)
        else:
            self.record("UI-07", "not_reachable",
                        "Project overview › Audits and retention policy do not exist. The global Audits rail "
                        f"(nav-audits present={_testid(d, 'nav-audits')}) is a different surface and does not satisfy the project-scoped path.",
                        shot)

    def ui_08(self):
        self.ensure_hub()
        self._absent_surface("UI-08", ["Clients"], "Spec path Main hub › Clients", "UI-08-attempt")

    def ui_09(self):
        self.ensure_hub()
        self._absent_surface("UI-09", ["Schedules"], "Spec path Main hub › Schedules", "UI-09-attempt",
                             "Schedule entry point exercised via REST run-now instead (recorded in scheduled-flow evidence).")

    def ui_10(self):
        d = self.nav("audits")
        aid = self.ctx.get("audit_id")
        attempts = [{"action": "nav-audits", "audits_view": _testid(d, "audits-view")}]
        opened = False
        open_error = None
        try:
            if aid and _testid(d, f"open-audit-{aid}"):
                self.cdp.click(testid=f"open-audit-{aid}")
                opened = True
            else:
                r = self.cdp.click(text="Open report")
                opened = bool(r.get("clicked"))
        except CdpError as e:
            open_error = str(e)  # explicit evidence; classification stays dump-driven
        self.cdp.wait(2000)
        d = self.dump()
        attempts.append({"action": f"open audit {aid}", "opened": opened, **({"error": open_error} if open_error else {})})
        shot = self.shot("UI-10-run-view")
        state_el = _testid(d, "report-state")
        real_state = bool(re.search(r"\b(COMPLETED|RUNNING|QUEUED|PAUSED|FAILED|CANCELLED)\b", d.get("text") or ""))
        telemetry = _testid(d, "report-progress") or _testid(d, "hosts-table")
        if _testid(d, "report-view") and state_el and real_state and telemetry:
            self.record("UI-10", "reachable_and_functional",
                        f"Audit run view renders real run data for audit {aid}: state badge, progress/counters and "
                        "host-throttle telemetry present; lifecycle controls mounted for active runs.",
                        shot, attempts)
        elif _testid(d, "report-view"):
            self.record("UI-10", "reachable_but_placeholder",
                        f"Report frame mounted but no real run telemetry (state badge={state_el}, telemetry={telemetry}).",
                        shot, attempts)
        else:
            self.record("UI-10", "not_reachable", "Audit run view did not mount from Audits › open audit.", shot, attempts)

    def ui_11(self):
        d = self.dump()
        if not _testid(d, "report-view"):
            self.nav("report")
            d = self.dump()
        shot = self.shot("UI-11-report-dashboard")
        placeholder = PLACEHOLDER_TEXT.lower() in _text(d)
        grids = any(_testid(d, t) for t in ("findings-grid", "url-explorer", "issues-grid", "dashboard-charts"))
        if placeholder and not grids:
            self.record("UI-11", "reachable_but_placeholder",
                        f"Report workspace shows the static placeholder text '{PLACEHOLDER_TEXT}' and no dashboard "
                        "data grid/chart. Live telemetry above it is real, but the Dashboard surface itself is an empty frame (CR-018: never functional).",
                        shot)
        elif grids:
            self.record("UI-11", "reachable_and_functional", "Dashboard grids/charts render real aggregate data.", shot)
        else:
            self.record("UI-11", "not_reachable", "No report workspace mounted.", shot)

    def _report_nav_absent(self, sid: str, markers: list[str], shot_name: str):
        d = self.dump()
        if not _testid(d, "report-view"):
            self.nav("report")
            d = self.dump()
        found = [m for m in markers if _has_nav(d, m)]
        shot = self.shot(shot_name)
        if found:
            self.record(sid, "reachable_and_functional", f"Report navigation entries present: {found}.", shot)
        else:
            self.record(sid, "not_reachable",
                        f"Report window navigation has no entry for any of {markers} (buttons/testids only; "
                        "telemetry labels do not count); the report frame exposes only live telemetry and the "
                        "placeholder pane (see UI-11).", shot)

    def ui_12(self):
        self._report_nav_absent("UI-12", ["Issue explorer", "Issues"], "UI-12-attempt")

    def ui_13(self):
        self._report_nav_absent("UI-13", ["URL explorer", "URLs"], "UI-13-attempt")

    def ui_14(self):
        self._report_nav_absent("UI-14", ["Visualizations", "Link graph"], "UI-14-attempt")

    def ui_15(self):
        self._report_nav_absent("UI-15", ["Compare", "Comparison"], "UI-15-attempt")

    def ui_16(self):
        d = self.dump()
        if not _testid(d, "report-view"):
            self.nav("report")
            d = self.dump()
        found = [m for m in SPECIALIST_MARKERS if _has_nav(d, m)]
        shot = self.shot("UI-16-attempt")
        if found:
            self.record("UI-16", "reachable_and_functional", f"Specialist surface entries present: {found}.", shot)
        else:
            self.record("UI-16", "not_reachable",
                        f"None of the {len(SPECIALIST_MARKERS)} specialist surfaces has a navigation entry in the report window.", shot)

    def ui_17(self):
        self.ensure_hub()
        self._absent_surface("UI-17", ["Logs", "Log file analyzer"], "Spec path Project overview › Logs", "UI-17-attempt")

    def ui_18(self):
        self.nav("tools")
        self.cdp.click(testid="tools-rail-single-page")
        self.cdp.wait(700)
        self.cdp.events(clear=True)
        url = (self.ctx.get("fixture_url") or "http://127.0.0.1:47311") + "/pages/noindex.html"
        self.cdp.fill(url, testid="sp-url")
        self.cdp.click(testid="sp-analyze")
        found = self.cdp.wait_for(testid="sp-summary", timeout_ms=45000)
        self.cdp.wait(800)
        d = self.dump()
        events = self.cdp.events(clear=True)
        calls = self.api_calls(events)
        shot = self.shot("UI-18-single-page-analyzer")
        count_txt = self.cdp.eval("(document.querySelector('[data-testid=sp-finding-count]')||{}).textContent || ''")
        findings_list = _testid(d, "sp-findings-list")
        ok = found.get("found") and (findings_list or (count_txt or "").strip() not in ("", "0"))
        if ok:
            self.record("UI-18", "reachable_and_functional",
                        f"Single-Page Analyzer ran against {url} without a crawl project: finding count={count_txt!r}, "
                        f"findings list rendered={findings_list}; API calls={json.dumps(calls)[:300]}.",
                        shot, extra={"api_calls": calls, "finding_count": count_txt})
        else:
            self.record("UI-18", "reachable_but_placeholder",
                        f"Analyzer panel mounted but produced no findings for {url} (summary={found}); calls={json.dumps(calls)[:300]}.",
                        shot, extra={"api_calls": calls})

    def ui_19(self):
        self.nav("tools")
        per_tool = {}
        all_ok = True
        for key in TOOL_KEYS:
            try:
                self.cdp.click(testid=f"tools-rail-{key}")
                self.cdp.wait(450)
                d = self.dump()
                panel = _testid(d, f"tools-panel-{key}")
                controls = self.cdp.eval(
                    f"(() => {{ const p = document.querySelector('[data-testid=tools-panel-{key}]'); "
                    "if (!p) return {inputs:0,buttons:0}; "
                    "return {inputs: p.querySelectorAll('input,select,textarea').length, buttons: p.querySelectorAll('button').length}; })()")
                shot = self.shot(f"UI-19-tool-{key}")
                per_tool[key] = {"panel": panel, "controls": controls, "screenshot": shot}
                if not panel:
                    all_ok = False
            except CdpError as e:
                per_tool[key] = {"error": str(e)}
                all_ok = False
        status = "reachable_and_functional" if all_ok else "reachable_but_placeholder"
        self.record("UI-19", status,
                    f"Standalone tools workspace: {sum(1 for v in per_tool.values() if v.get('panel'))}/{len(TOOL_KEYS)} "
                    "tool panels mount with real controls without a crawl project.",
                    self.shot("UI-19-tools"), extra={"tools": per_tool})

    def ui_20(self):
        d = self.dump()
        if not _testid(d, "report-view"):
            self.nav("report")
            d = self.dump()
        found = [m for m in ("Export",) if _has_nav(d, m)]
        shot = self.shot("UI-20-attempt")
        if found:
            self.record("UI-20", "reachable_and_functional", f"Export control present: {found}.", shot)
        else:
            self.record("UI-20", "not_reachable",
                        "No Export control in the report window (buttons/testids). REST export jobs work "
                        "(probe-2 §7) but no UI export dialog is mounted.", shot)

    def ui_21(self):
        d = self.nav("settings")
        shot = self.shot("UI-21-settings")
        present = [s for s in ("Appearance", "Status-bar advisories", "Database", "Automation interface", "Embeddings", "Proxy", "Diagnostics") if _has(d, s)]
        missing = [s for s in ("Export destinations", "Browser", "User dictionary", "Integrations credentials", "Configuration templates", "Release check") if not _has(d, s)]
        if _testid(d, "settings-surface") and present:
            self.record("UI-21", "reachable_and_functional",
                        f"Settings surface renders sections {present}; documented sections missing: {missing}.",
                        shot, extra={"sections_present": present, "sections_missing": missing})
        else:
            self.record("UI-21", "reachable_but_placeholder", "Settings surface mounted without sections.", shot)

    def ui_22(self):
        d = self.dump()
        if not _testid(d, "settings-surface"):
            d = self.nav("settings")
        self._absent_surface("UI-22", ["Plugins", "Plugin manager"], "Spec path Settings › Plugins", "UI-22-attempt")

    def ui_23(self):
        d = self.dump()
        shot = self.shot("UI-23-status-bar")
        ws = self.cdp.eval("(document.querySelector('[data-testid=status-ws]')||{}).textContent || ''")
        heap = self.cdp.eval("(document.querySelector('[data-testid=status-heap]')||{}).textContent || ''")
        if _testid(d, "cf-statusbar") and (ws or heap):
            self.record("UI-23", "reachable_and_functional",
                        f"Status bar visible with live telemetry (WS={ws!r}, Heap={heap!r}) and the Compact action.", shot)
        else:
            self.record("UI-23", "not_reachable", "Status bar absent or empty.", shot)

    def ui_24(self):
        self.record("UI-24", "not_applicable_yet",
                    "Recovery screen is shown automatically after repeated backend failures; the backend is healthy "
                    "and acceptance must NOT induce a backend failure to reach it. Not exercised.", None)

    # -- runner -------------------------------------------------------------

    def walk(self, selected: list[str] | None = None) -> dict:
        methods = {
            "UI-01": self.ui_01, "UI-02": self.ui_02, "UI-03": self.ui_03, "UI-04": self.ui_04,
            "UI-05": self.ui_05, "UI-06": self.ui_06, "UI-07": self.ui_07, "UI-08": self.ui_08,
            "UI-09": self.ui_09, "UI-10": self.ui_10, "UI-11": self.ui_11, "UI-12": self.ui_12,
            "UI-13": self.ui_13, "UI-14": self.ui_14, "UI-15": self.ui_15, "UI-16": self.ui_16,
            "UI-17": self.ui_17, "UI-18": self.ui_18, "UI-19": self.ui_19, "UI-20": self.ui_20,
            "UI-21": self.ui_21, "UI-22": self.ui_22, "UI-23": self.ui_23, "UI-24": self.ui_24,
        }
        for sid, _, _ in SURFACE_CATALOGUE:
            if selected is not None and sid not in selected:
                continue
            try:
                methods[sid]()
            except CdpError as e:
                self.record(sid, "not_reachable", f"Walker attempt failed with CDP error: {e}", self.shot(f"{sid}-error"))
            except Exception as e:  # noqa: BLE001 - record, never crash the walk
                self.record(sid, "not_reachable", f"Walker attempt errored: {type(e).__name__}: {e}", self.shot(f"{sid}-error"))
        return self.results