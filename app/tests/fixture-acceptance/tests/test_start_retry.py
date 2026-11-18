"""Snapshot-race retry self-tests (lead-approved binding conditions):
retry ONLY on the exact documented signature, max 3 attempts, same entry
point, every attempt recorded; any other failure stands as-is."""

import unittest
from unittest.mock import patch

import harness.audits as audits
from harness.audits import SnapshotRaceError, is_snapshot_race, start_with_retry

RACE_ERROR = ("The JSON value could not be converted to "
              "CrawlForge.Contracts.Entities.CrawlConfigurationDto. Path: $.id | LineNumber: 0 | BytePositionInLine: 10.")


def failed_audit(error=RACE_ERROR, fetched=0, state="failed"):
    return {"id": "99", "state": state, "errorMessage": error,
            "counters": {"queued": 0, "fetched": fetched, "error": 0, "blocked": 0}}


class SnapshotRaceSignatureTests(unittest.TestCase):
    def test_exact_signature_matches(self):
        self.assertTrue(is_snapshot_race(failed_audit(), 0.036))

    def test_other_error_does_not_match(self):
        self.assertFalse(is_snapshot_race(failed_audit(error="renderer crashed"), 0.5))

    def test_slow_failure_does_not_match(self):
        self.assertFalse(is_snapshot_race(failed_audit(), 12.0))

    def test_failure_after_fetching_does_not_match(self):
        self.assertFalse(is_snapshot_race(failed_audit(fetched=5), 1.0))

    def test_completed_does_not_match(self):
        self.assertFalse(is_snapshot_race(failed_audit(state="completed", error=None), 0.1))

    def test_missing_error_does_not_match(self):
        self.assertFalse(is_snapshot_race(failed_audit(error=None), 0.1))


class FakeFixture:
    def __init__(self):
        self.resets = 0

    def reset(self):
        self.resets += 1
        return {"ok": True}

    def requests(self):
        return {"nonGet": [], "tagHits": []}


class StartWithRetryTests(unittest.TestCase):
    def _run(self, poll_side_effect, start_ids):
        fixture = FakeFixture()
        log: list = []
        ids = iter(start_ids)
        with patch.object(audits, "poll_audit", side_effect=poll_side_effect):
            aid, polled, attempts = start_with_retry(lambda: next(ids), object(), fixture, log,
                                                     "test entry", cap_s=60, max_attempts=3)
        return aid, polled, attempts, fixture, log

    def test_retry_then_success_records_failed_attempt(self):
        race = SnapshotRaceError("90", RACE_ERROR, {"final": failed_audit(), "transitions": [], "elapsed_s": 0.04, "timed_out": False})
        ok = {"final": {"id": "91", "state": "completed", "counters": {"fetched": 10}}, "transitions": [], "elapsed_s": 5.0, "timed_out": False}
        aid, polled, attempts, fixture, log = self._run([race, ok], ["90", "91"])
        self.assertEqual(aid, "91")
        self.assertEqual(polled["final"]["state"], "completed")
        self.assertEqual(len(attempts), 2)  # failed attempt + success marker
        self.assertEqual(attempts[0]["audit_id"], "90")
        self.assertIn("$.id", attempts[0]["error"])
        self.assertEqual(len(log), 1)  # safety checked after the failed attempt too
        self.assertTrue(log[0]["ok"])
        self.assertEqual(fixture.resets, 2)  # reset before EACH attempt

    def test_all_attempts_fail_returns_failed_result(self):
        def race(aid):
            return SnapshotRaceError(aid, RACE_ERROR, {"final": failed_audit(), "transitions": [], "elapsed_s": 0.04, "timed_out": False})
        aid, polled, attempts, fixture, log = self._run([race("90"), race("91"), race("92")], ["90", "91", "92"])
        self.assertEqual(aid, "92")
        self.assertEqual(polled["final"]["state"], "failed")  # verdicts will measure fail
        self.assertEqual(len(attempts), 3)
        self.assertEqual(len(log), 3)
        self.assertEqual(fixture.resets, 3)

    def test_non_race_failure_is_not_retried(self):
        other = {"final": failed_audit(error="renderer crashed"), "transitions": [], "elapsed_s": 1.0, "timed_out": False}
        aid, polled, attempts, fixture, log = self._run([other], ["90"])
        self.assertEqual(aid, "90")
        self.assertEqual(polled["final"]["errorMessage"], "renderer crashed")
        self.assertEqual(attempts, [])  # stands as-is, no retry
        self.assertEqual(fixture.resets, 1)


if __name__ == "__main__":
    unittest.main()