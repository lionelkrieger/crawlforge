"""Evidence retention: results/<timestamp>/ with verdicts, surfaces, summary,
audit ids, fixture request log, screenshots and raw data pulls."""

from __future__ import annotations

import json
from datetime import datetime, timezone
from pathlib import Path

from . import HARNESS_VERSION, WORKPIECE

GITIGNORE = """# WP-08-04 harness evidence: bulky binaries and raw pulls stay on disk;
# machine-readable verdicts and small logs are committed.
screenshots/
*-data.json
*-trap-details.json
*.log
electron-stdout.log
electron-stderr.log
launch-console.log
fixture-server.log
"""


class ResultsDir:
    def __init__(self, root: Path, timestamp: str | None = None):
        self.timestamp = timestamp or datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        self.root = root / self.timestamp
        self.root.mkdir(parents=True, exist_ok=True)
        (self.root / "screenshots").mkdir(exist_ok=True)
        gi = root / ".gitignore"
        if not gi.exists():
            gi.write_text(GITIGNORE, encoding="utf-8")

    def path(self, name: str) -> Path:
        return self.root / name

    def write_json(self, name: str, obj) -> Path:
        p = self.path(name)
        p.write_text(json.dumps(obj, indent=2, ensure_ascii=False, default=str), encoding="utf-8")
        return p

    def write_text(self, name: str, text: str) -> Path:
        p = self.path(name)
        p.write_text(text, encoding="utf-8")
        return p


def build_summary(verdicts_doc: dict, surfaces: dict | None, runs: list[dict]) -> str:
    lines: list[str] = []
    lines.append(f"# Fixture acceptance run — {verdicts_doc.get('timestamp')} ({WORKPIECE} harness v{HARNESS_VERSION})")
    lines.append("")
    lines.append(f"- Fixture: {verdicts_doc.get('fixture_base_url')}")
    lines.append(f"- Product git HEAD: {verdicts_doc.get('git_head')}")
    lines.append(f"- Expected-results SHA-256: {verdicts_doc.get('expected_results_sha256')}")
    lines.append(f"- Exit code: {verdicts_doc.get('exit_code')} ({verdicts_doc.get('exit_reason')})")
    env = verdicts_doc.get("environment") or {}
    if env:
        lines.append(f"- Environment: CDP port notes={env.get('cdp_port_notes')}; "
                     f"fixture started_by_harness={env.get('fixture_started_by_harness')}, reused={env.get('fixture_reused_existing')}. "
                     "Pre-run session events: results/ENVIRONMENT-NOTES.md.")
    lines.append("")
    lines.append("## Acceptance trap matrix (probe-2 convention: bulk runs pooled per mode)")
    lines.append("")
    modes = verdicts_doc.get("modes") or {}
    if modes:
        lines.append("| Mode | pass | partial | fail | not_testable |")
        lines.append("|---|---:|---:|---:|---:|")
        for mode, m in modes.items():
            c = m.get("counts") or {}
            lines.append(f"| {mode} | {c.get('pass', 0)} | {c.get('partial', 0)} | {c.get('fail', 0)} | {c.get('not_testable', 0)} |")
        lines.append("")
        for mode, m in modes.items():
            lines.append(f"### Mode {mode}")
            lines.append("")
            lines.append("| Trap | Result | Evidence |")
            lines.append("|---|---|---|")
            for tid, v in (m.get("traps") or {}).items():
                ev = (v.get("evidence") or "").replace("|", "\\|").replace("\n", " ")
                lines.append(f"| {tid} | {v.get('result')} | {ev[:400]} |")
            lines.append("")
    lines.append("## Runs (normal entry points)")
    lines.append("")
    for r in runs:
        lines.append(f"- **{r.get('run_id')}** mode={r.get('mode')} entry={r.get('entry_point')} "
                     f"project={r.get('project_id')} config={r.get('configuration_id')} audit={r.get('audit_id')} "
                     f"state={r.get('state')} urls={r.get('url_count')} findings={r.get('finding_count')} "
                     f"duration={r.get('duration_s')}s limits={json.dumps(r.get('limit_overrides'))}")
        if r.get("rendering_inert"):
            lines.append(f"  - rendering inertness: {r['rendering_inert'].get('evidence')}")
        if r.get("safety"):
            lines.append(f"  - safety: {r['safety'].get('evidence')}")
    lines.append("")
    lines.append("## Global assertions")
    lines.append("")
    for ga in verdicts_doc.get("global_assertions") or []:
        lines.append(f"- {ga.get('id')}: ok={ga.get('ok')}")
        for a in ga.get("per_audit") or []:
            lines.append(f"  - audit {a.get('audit_id')} ({a.get('entry')}): nonGet={a.get('nonGet')} ok={a.get('ok')}")
    lines.append("")
    defects = verdicts_doc.get("product_defects") or {}
    if defects:
        lines.append("## Product defects observed (record-only; no product changes)")
        lines.append("")
        for key, d in defects.items():
            lines.append(f"### {key}")
            lines.append("")
            lines.append(d.get("description", ""))
            lines.append("")
            lines.append(f"- Citations: {', '.join(d.get('citations') or [])}")
            lines.append(f"- probe-2 note: {d.get('probe2_note')}")
            lines.append(f"- Retry policy: {d.get('retry_policy')}")
            lines.append(f"- Suggested routing: {d.get('suggested_routing')}")
            for a in d.get("failed_attempts_this_run") or []:
                lines.append(f"- Failed attempt: run={a.get('run')} audit={a.get('audit_id')} "
                             f"elapsed={a.get('elapsed_s')}s error={a.get('error')}")
            lines.append("")
    if surfaces:
        lines.append("## UI surfaces (CR-018 walker)")
        lines.append("")
        counts: dict = {}
        for s in surfaces.values():
            counts[s["status"]] = counts.get(s["status"], 0) + 1
        lines.append(f"Status counts: {json.dumps(counts)}")
        lines.append("")
        lines.append("| Surface | Status | Evidence |")
        lines.append("|---|---|---|")
        for sid, s in surfaces.items():
            ev = (s.get("evidence") or "").replace("|", "\\|").replace("\n", " ")
            lines.append(f"| {sid} {s.get('surface')} | {s.get('status')} | {ev[:300]} |")
        lines.append("")
    return "\n".join(lines)