"""Regression tests for the UI-interaction determinism fix (lead send-back
2026-11-18): click/fill must ASSERT success, retry with backoff, and raise
CdpError on persistent failure — a missed click is never silently swallowed.
The CDP child is mocked; no live app is needed."""

import unittest
from unittest.mock import patch

from harness.cdp import CdpError, CdpSession


def bare_session() -> CdpSession:
    return CdpSession.__new__(CdpSession)  # skip __init__ (no child process)


class ClickFillPropagationTests(unittest.TestCase):
    def test_click_retries_then_succeeds(self):
        s = bare_session()
        with patch.object(CdpSession, "call", side_effect=[
            {"clicked": False, "error": "element not found: testid=create-project-submit"},
            {"clicked": True, "rect": {"x": 1, "y": 2}, "method": "input"},
        ]) as m:
            r = s.click(testid="create-project-submit", timeout=5.0)
        self.assertTrue(r["clicked"])
        self.assertEqual(r["attempts"], 2)
        self.assertEqual(m.call_count, 2)

    def test_click_persistent_failure_raises(self):
        s = bare_session()
        with patch.object(CdpSession, "call", return_value={"clicked": False, "error": "element not found"}):
            with self.assertRaises(CdpError) as ctx:
                s.click(testid="create-project-submit", timeout=0.6)
        self.assertIn("did not succeed", str(ctx.exception))

    def test_fill_retries_then_succeeds(self):
        s = bare_session()
        with patch.object(CdpSession, "call", side_effect=[
            {"ok": False, "error": "input not found"},
            {"ok": True, "value": "x", "expected": "x"},
        ]):
            r = s.fill("x", testid="project-name-input", timeout=5.0)
        self.assertTrue(r["ok"])
        self.assertEqual(r["attempts"], 2)

    def test_fill_value_mismatch_raises(self):
        s = bare_session()
        with patch.object(CdpSession, "call", return_value={"ok": False, "value": "", "expected": "x"}):
            with self.assertRaises(CdpError):
                s.fill("x", testid="project-name-input", timeout=0.6)

    def test_transport_error_propagates_immediately(self):
        s = bare_session()
        with patch.object(CdpSession, "call", side_effect=CdpError("CDP child exited (code 1)")) as m:
            with self.assertRaises(CdpError):
                s.click(testid="nav-hub", timeout=5.0)
        self.assertEqual(m.call_count, 1)  # no retry loop on transport death

    def test_must_wait_for_raises_on_timeout(self):
        s = bare_session()
        with patch.object(CdpSession, "call", return_value={"found": False, "timeout": True}):
            with self.assertRaises(CdpError):
                s.must_wait_for(testid="create-project-modal", timeout_ms=100)

    def test_must_wait_for_passes_on_found(self):
        s = bare_session()
        with patch.object(CdpSession, "call", return_value={"found": True}):
            self.assertTrue(s.must_wait_for(testid="create-project-modal")["found"])


if __name__ == "__main__":
    unittest.main()