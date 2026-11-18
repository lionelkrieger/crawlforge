"""FixtureSafetyAssertionTests — the global no-non-get-requests assertion
(criterion #54) must pass on an empty recorder, FAIL on a synthetic non-empty
recorder (a triggered Place bet / Deposit / Logout POST), and the tagHits
inspection must detect pre-consent tag firing for the consent-banner trap."""

import unittest

from harness.fixture import assert_no_non_get, inspect_tag_hits


class FixtureSafetyAssertionTests(unittest.TestCase):
    def test_empty_recorder_passes(self):
        ok, evidence = assert_no_non_get({"nonGet": [], "tagHits": []})
        self.assertTrue(ok)
        self.assertIn("nonGet == []", evidence)

    def test_synthetic_bet_post_fails_assertion(self):
        rec = {"nonGet": [{"at": "2026-11-18T00:00:00Z", "method": "POST", "path": "/api/bet"}], "tagHits": []}
        ok, evidence = assert_no_non_get(rec)
        self.assertFalse(ok, "a POST /api/bet must fail the safety assertion")
        self.assertIn("/api/bet", evidence)

    def test_synthetic_deposit_and_logout_fail(self):
        for path in ("/api/deposit", "/api/logout"):
            ok, _ = assert_no_non_get({"nonGet": [{"method": "POST", "path": path}]})
            self.assertFalse(ok, path)

    def test_missing_or_malformed_nonget_fails(self):
        self.assertFalse(assert_no_non_get({})[0])
        self.assertFalse(assert_no_non_get({"nonGet": "oops"})[0])
        self.assertFalse(assert_no_non_get({"nonGet": None})[0])

    def test_tag_hits_inspection(self):
        rec = {"nonGet": [], "tagHits": [
            {"at": "t1", "query": "?ev=page_view&consent=none"},
            {"at": "t2", "query": "?ev=page_view&consent=granted"},
            {"at": "t3", "query": "?ev=click&consent=none"},
        ]}
        tags = inspect_tag_hits(rec)
        self.assertEqual(tags["total"], 3)
        self.assertEqual(tags["beforeConsent"], 2)
        self.assertEqual(tags["granted"], 1)

    def test_tag_hits_empty(self):
        tags = inspect_tag_hits({"nonGet": [], "tagHits": []})
        self.assertEqual(tags["total"], 0)
        self.assertEqual(tags["beforeConsent"], 0)


if __name__ == "__main__":
    unittest.main()