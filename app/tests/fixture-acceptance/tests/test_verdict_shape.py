"""HarnessVerdictShapeTests — the verdict report schema is validated against
the expected-results.json structure: every trap id present, valid verdict
enum, non-empty evidence, modes covered, counts consistent. Also validates
the surfaces document against the UI-01..UI-24 catalogue."""

import json
import unittest
from pathlib import Path

from harness.verdicts import VERDICT_ENUM, evaluate_matrix, count_verdicts
from harness.ui_walker import SURFACE_CATALOGUE
from tests.synthetic import build_data, build_ctx

HARNESS_DIR = Path(__file__).resolve().parents[1]
EXPECTED_PATH = HARNESS_DIR.parent / "fixture-site" / "expected-results.json"
SURFACE_IDS = [s[0] for s in SURFACE_CATALOGUE]
SURFACE_STATUSES = {"reachable_and_functional", "reachable_but_placeholder", "not_reachable", "not_applicable_yet"}


def load_traps():
    return json.loads(EXPECTED_PATH.read_text(encoding="utf-8"))["traps"]


def validate_verdict_doc(doc: dict, traps: list, required_modes: list[str]) -> list[str]:
    """Return a list of schema violations (empty = valid). Respects trap
    selection (subset runs) and walker-only runs (no audit => no mode matrix)."""
    problems: list[str] = []
    selected = doc.get("selected_traps")
    trap_ids = {t["id"] for t in traps if not isinstance(selected, list) or t["id"] in selected}
    if not any(r.get("audit_id") for r in doc.get("runs") or []):
        required_modes = []  # walker-only / wire-probe-only runs have no bulk matrix
    modes = doc.get("modes") or {}
    for mode in dict.fromkeys(list(required_modes) + list(modes.keys())):
        if mode not in modes:
            problems.append(f"mode '{mode}' missing from verdict doc")
            continue
        tv = modes[mode].get("traps") or {}
        missing = trap_ids - set(tv.keys())
        if missing:
            problems.append(f"mode {mode}: traps missing: {sorted(missing)}")
        for tid, v in tv.items():
            if tid not in trap_ids:
                problems.append(f"mode {mode}: unknown trap id {tid}")
            if v.get("result") not in VERDICT_ENUM:
                problems.append(f"mode {mode}/{tid}: invalid verdict {v.get('result')!r}")
            if not isinstance(v.get("evidence"), str) or not v["evidence"].strip():
                problems.append(f"mode {mode}/{tid}: evidence empty")
        counts = modes[mode].get("counts") or {}
        recomputed = count_verdicts(tv)
        if counts != recomputed:
            problems.append(f"mode {mode}: counts {counts} != recomputed {recomputed}")
    for ga in doc.get("global_assertions") or []:
        if ga.get("id") == "no-non-get-requests":
            break
    else:
        problems.append("global assertion no-non-get-requests missing")
    return problems


class HarnessVerdictShapeTests(unittest.TestCase):
    def test_synthetic_doc_conforms(self):
        traps = load_traps()
        data = build_data()
        modes = {}
        for mode in ("http", "js"):
            tv = evaluate_matrix(traps, mode, build_ctx(mode, inert=(mode == "js")), data)
            modes[mode] = {"traps": tv, "counts": count_verdicts(tv)}
        doc = {"modes": modes, "global_assertions": [{"id": "no-non-get-requests", "ok": True, "per_audit": []}]}
        self.assertEqual(validate_verdict_doc(doc, traps, ["http", "js"]), [])

    def test_validator_rejects_bad_enum(self):
        traps = load_traps()
        tv = {t["id"]: {"result": "pass", "evidence": "x"} for t in traps}
        tv["noindex"]["result"] = "mostly-pass"
        doc = {"modes": {"http": {"traps": tv, "counts": {"pass": 31, "partial": 0, "fail": 0, "not_testable": 0}}},
               "global_assertions": [{"id": "no-non-get-requests", "ok": True}]}
        problems = validate_verdict_doc(doc, traps, ["http"])
        self.assertTrue(any("invalid verdict" in p for p in problems))

    def test_validator_rejects_missing_trap_and_empty_evidence(self):
        traps = load_traps()
        tv = {t["id"]: {"result": "pass", "evidence": "x"} for t in traps if t["id"] != "orphan"}
        tv["noindex"]["evidence"] = "   "
        doc = {"modes": {"http": {"traps": tv, "counts": {}}},
               "global_assertions": [{"id": "no-non-get-requests", "ok": True}]}
        problems = validate_verdict_doc(doc, traps, ["http"])
        self.assertTrue(any("traps missing" in p and "orphan" in p for p in problems))
        self.assertTrue(any("evidence empty" in p for p in problems))
        self.assertTrue(any("counts" in p for p in problems))

    def test_committed_verdict_docs_conform(self):
        """Every results/*/verdicts.json on disk must conform (skipped when none)."""
        docs = sorted((HARNESS_DIR / "results").glob("*/verdicts.json"))
        if not docs:
            self.skipTest("no verdict docs on disk yet")
        traps = load_traps()
        for p in docs:
            doc = json.loads(p.read_text(encoding="utf-8"))
            requested = doc.get("modes_requested") or []
            bulk = [m for m in requested if m in ("http", "js")]
            problems = validate_verdict_doc(doc, traps, bulk)
            self.assertEqual(problems, [], f"{p}: {problems}")

    def test_surfaces_doc_shape(self):
        docs = sorted((HARNESS_DIR / "results").glob("*/surfaces.json"))
        if not docs:
            self.skipTest("no surfaces docs on disk yet")
        for p in docs:
            surfaces = json.loads(p.read_text(encoding="utf-8"))
            for sid, s in surfaces.items():
                self.assertIn(sid, SURFACE_IDS)
                self.assertIn(s.get("status"), SURFACE_STATUSES)
                self.assertTrue((s.get("evidence") or "").strip(), f"{sid} evidence empty")


if __name__ == "__main__":
    unittest.main()