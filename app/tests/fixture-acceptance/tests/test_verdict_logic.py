"""Verdict-logic self-tests: the evaluator must MEASURE probe-shaped data and
reproduce probe-2's verdict semantics (4 pass / 5 partial / 10 fail /
12 not_testable in http AND js), and must flip verdicts when the measured
evidence changes (proving nothing is hardcoded)."""

import json
import unittest
from pathlib import Path

from harness.verdicts import evaluate_matrix, count_verdicts, merge_audit_data
from tests.synthetic import build_data, build_ctx, BASE, _f

EXPECTED_PATH = Path(__file__).resolve().parents[2] / "fixture-site" / "expected-results.json"
TRAPS = json.loads(EXPECTED_PATH.read_text(encoding="utf-8"))["traps"]

PROBE2_COUNTS = {"pass": 4, "partial": 5, "fail": 10, "not_testable": 12}
PROBE2_RESULTS = {
    "noindex": "pass", "canonical-conflict": "pass", "soft-404-server": "pass", "missing-alt": "pass",
    "redirect-chain": "partial", "redirect-loop": "partial", "robots-blocked": "partial",
    "broken-links": "partial", "duplicate-titles": "partial",
    "orphan": "fail", "postback-links": "fail", "cookieless-session": "fail", "large-viewstate": "fail",
    "crawler-trap-filter": "fail", "crawler-trap-calendar": "fail", "cookie-gate": "fail",
    "structured-data-errors": "fail", "hreflang-no-return": "fail", "thin-page": "fail",
}


class VerdictLogicTests(unittest.TestCase):
    def test_http_matrix_reproduces_probe2(self):
        data = build_data()
        verdicts = evaluate_matrix(TRAPS, "http", build_ctx("http"), data)
        self.assertEqual(count_verdicts(verdicts), PROBE2_COUNTS)
        for tid, expected in PROBE2_RESULTS.items():
            self.assertEqual(verdicts[tid]["result"], expected, f"{tid}: {verdicts[tid]['evidence']}")
        for tid, v in verdicts.items():
            self.assertTrue(v["evidence"].strip(), f"{tid} evidence empty")

    def test_js_matrix_reproduces_probe2_when_rendering_inert(self):
        data = build_data()
        verdicts = evaluate_matrix(TRAPS, "js", build_ctx("js", inert=True), data)
        self.assertEqual(count_verdicts(verdicts), PROBE2_COUNTS)
        for tid, expected in PROBE2_RESULTS.items():
            self.assertEqual(verdicts[tid]["result"], expected, f"{tid}: {verdicts[tid]['evidence']}")
        # js-capture traps are not_testable specifically because of DETECTED inertness
        self.assertIn("inert", verdicts["js-only-link"]["evidence"])
        self.assertIn("vacuous", verdicts["never-place-bet"]["evidence"])

    def test_js_trap_passes_when_rendering_works(self):
        data = build_data(
            extra_urls=[(40, "/pages/js-only-target.html", 200, "JS-only target", None)],
            extra_findings=[_f(200, "render.links-delta", 22, "rendered-only link", "notice", {})],
        )
        verdicts = evaluate_matrix(TRAPS, "js", build_ctx("js", inert=False), data)
        self.assertEqual(verdicts["js-only-link"]["result"], "pass")

    def test_orphan_flips_to_pass_when_discovered_and_reported(self):
        data = build_data(
            extra_urls=[(41, "/pages/orphan.html", 200, "Orphan page", None)],
            extra_findings=[_f(201, "linking.orphan", 41, "orphan page", "warning", {})],
        )
        verdicts = evaluate_matrix(TRAPS, "http", build_ctx("http"), data)
        self.assertEqual(verdicts["orphan"]["result"], "pass")

    def test_duplicate_titles_flips_to_pass_with_duplicate_title_finding(self):
        data = build_data(extra_findings=[_f(202, "content.duplicate-title", 10, "duplicate title", "warning", {})])
        verdicts = evaluate_matrix(TRAPS, "http", build_ctx("http"), data)
        self.assertEqual(verdicts["duplicate-titles"]["result"], "pass")

    def test_thin_page_flips_to_pass_with_thin_text_on_url(self):
        data = build_data(extra_findings=[_f(203, "content.thin-text", 9, "thin text", "notice", {"wordCount": 6})])
        verdicts = evaluate_matrix(TRAPS, "http", build_ctx("http"), data)
        self.assertEqual(verdicts["thin-page"]["result"], "pass")

    def test_robots_blocked_passes_with_block_reason(self):
        data = build_data()
        for u in data["urls"]:
            if u["canonicalUrl"] == BASE + "/private/blocked.html":
                u["blockReason"] = "robots.txt disallow"
        verdicts = evaluate_matrix(TRAPS, "http", build_ctx("http"), data)
        self.assertEqual(verdicts["robots-blocked"]["result"], "pass")

    def test_mixed_content_not_testable_without_tls(self):
        data = build_data()
        v = evaluate_matrix(TRAPS, "http", build_ctx("http", tls=False), data)
        self.assertEqual(v["mixed-content"]["result"], "not_testable")
        self.assertIn("requires_tls", v["mixed-content"]["evidence"])

    def test_dual_mode_all_not_testable(self):
        for mode in ("dual", "adaptive"):
            v = evaluate_matrix(TRAPS, mode, {"wire_probe_evidence": "no wire support"}, {})
            self.assertEqual(count_verdicts(v), {"pass": 0, "partial": 0, "fail": 0, "not_testable": 31})

    def test_late_meta_change_not_testable_in_http_js(self):
        data = build_data()
        for mode in ("http", "js"):
            v = evaluate_matrix(TRAPS, mode, build_ctx(mode, inert=(mode == "js")), data)
            self.assertEqual(v["late-meta-change"]["result"], "not_testable")

    def test_selection_subset(self):
        data = build_data()
        sel = ["noindex", "canonical-conflict", "soft-404-server", "missing-alt"]
        v = evaluate_matrix(TRAPS, "http", build_ctx("http"), data, sel)
        self.assertEqual(set(v.keys()), set(sel))
        self.assertEqual(count_verdicts(v), {"pass": 4, "partial": 0, "fail": 0, "not_testable": 0})

    def test_pooling_recovers_cross_run_evidence(self):
        # probe-2 convention: the js run truncated by the time cap lacks the
        # internal 404 row; pooling with the http run recovers it (partial).
        http_data = build_data()
        js_data = build_data()
        js_data["urls"] = [u for u in js_data["urls"] if u["canonicalUrl"] != BASE + "/pages/does-not-exist.html"]
        js_data["id"] = "901"
        js_only = evaluate_matrix(TRAPS, "js", build_ctx("js", inert=True), js_data)
        self.assertEqual(js_only["broken-links"]["result"], "fail")  # per-run reality stays visible
        pooled = merge_audit_data([http_data, js_data])
        pv = evaluate_matrix(TRAPS, "js", build_ctx("js", inert=True), pooled)
        self.assertEqual(pv["broken-links"]["result"], "partial")
        self.assertEqual(pooled["id"], "900+901")


if __name__ == "__main__":
    unittest.main()