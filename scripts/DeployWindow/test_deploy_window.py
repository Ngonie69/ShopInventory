#!/usr/bin/env python3
"""The deploy window's decision table, and a check that the workflow's cron matches it.

    python scripts/DeployWindow/test_deploy_window.py
"""

import re
import sys
import unittest
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import deploy_window as dw  # noqa: E402

WORKFLOW = Path(__file__).resolve().parents[2] / ".github" / "workflows" / "deploy-production.yml"

TESTED = "a" * 40
HEAD = "b" * 40


def cat(hh_mm: str, day: str = "2026-09-14") -> datetime:
    """A moment given in CAT, as the UTC instant the runner's clock would read."""
    return datetime.fromisoformat(f"{day}T{hh_mm}:00+02:00").astimezone(timezone.utc)


class MergesDuringTrading(unittest.TestCase):
    def test_the_merges_of_14_september_wait_for_the_evening(self):
        # #428, #430, #431 and #429 finished Tests between 16:05 and 16:26 CAT, and each deploy cut the
        # KEFSHOP till off. None of them deploys now.
        for moment in ["16:05", "16:08", "16:15", "16:26"]:
            deploy, sha, reason = dw.decide("workflow_run", cat(moment), TESTED, HEAD, tests_passed=True)
            self.assertFalse(deploy, moment)
            self.assertEqual(sha, TESTED)
            self.assertIn("19:30", reason)
            self.assertIn("by hand", reason)

    def test_the_edges_of_the_window(self):
        cases = {
            "06:59": True,   # before opening
            "07:00": False,  # the window has started
            "12:00": False,
            "18:59": False,  # the 18:00 consolidation is still running
            "19:00": True,   # the window has ended
            "23:30": True,
            "00:15": True,
        }
        for moment, deploys in cases.items():
            deploy, _, _ = dw.decide("workflow_run", cat(moment), TESTED, HEAD, tests_passed=True)
            self.assertEqual(deploy, deploys, moment)

    def test_every_day_of_the_week_is_a_trading_day(self):
        # CORMACH2 trades on Sundays. 2026-09-13 is a Sunday, 2026-09-19 a Saturday.
        for day in ["2026-09-13", "2026-09-14", "2026-09-19"]:
            deploy, _, _ = dw.decide("workflow_run", cat("11:00", day), TESTED, HEAD, tests_passed=True)
            self.assertFalse(deploy, day)

    def test_outside_the_window_the_tested_commit_deploys_not_mains_head(self):
        deploy, sha, _ = dw.decide("workflow_run", cat("20:10"), TESTED, HEAD, tests_passed=False)
        self.assertTrue(deploy)
        self.assertEqual(sha, TESTED)

    def test_a_run_with_no_tested_commit_deploys_nothing(self):
        deploy, _, _ = dw.decide("workflow_run", cat("21:00"), "", HEAD, tests_passed=True)
        self.assertFalse(deploy)


class EveningDeploy(unittest.TestCase):
    def test_mains_head_deploys_when_its_tests_passed(self):
        deploy, sha, _ = dw.decide("schedule", cat("19:30"), "", HEAD, tests_passed=True)
        self.assertTrue(deploy)
        self.assertEqual(sha, HEAD)

    def test_mains_head_does_not_deploy_when_its_tests_have_not_passed(self):
        deploy, sha, reason = dw.decide("schedule", cat("19:30"), "", HEAD, tests_passed=False)
        self.assertFalse(deploy)
        self.assertEqual(sha, HEAD)
        self.assertIn("bbbbbbb", reason)

    def test_the_evening_deploy_starts_outside_the_window(self):
        moment = datetime.combine(datetime(2026, 9, 14).date(), dw.EVENING_DEPLOY, tzinfo=dw.CAT)
        self.assertFalse(dw.trading(moment.astimezone(timezone.utc)))

    def test_the_workflow_cron_is_the_evening_deploy(self):
        text = WORKFLOW.read_text(encoding="utf-8")
        crons = re.findall(r"cron:\s*'([^']+)'", text)
        self.assertEqual(len(crons), 1, "one scheduled trigger")

        minute, hour, *rest = crons[0].split()
        self.assertEqual(rest, ["*", "*", "*"], "every day")
        utc = datetime(2026, 9, 14, int(hour), int(minute), tzinfo=timezone.utc)
        self.assertEqual(utc.astimezone(dw.CAT).time(), dw.EVENING_DEPLOY)


class ByHand(unittest.TestCase):
    def test_a_run_by_hand_deploys_at_any_time(self):
        for moment in ["03:00", "10:30", "16:41", "22:00"]:
            deploy, sha, _ = dw.decide("workflow_dispatch", cat(moment), "", HEAD, tests_passed=False)
            self.assertTrue(deploy, moment)
            self.assertEqual(sha, HEAD)

    def test_anything_else_deploys_nothing(self):
        for event in ["push", "pull_request", "pull_request_target", ""]:
            deploy, _, _ = dw.decide(event, cat("21:00"), TESTED, HEAD, tests_passed=True)
            self.assertFalse(deploy, event)


if __name__ == "__main__":
    unittest.main(verbosity=1)
