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


class AMergeShipsWhenItLands(unittest.TestCase):
    def test_a_merge_deploys_whatever_the_hour(self):
        # Including the four times on 14 September 2026 whose cutovers cost a till its receipt.
        # They deploy now because the cutover no longer drops the requests in flight, which is
        # proved in scripts/Test-DeployPublicBindingSwap.ps1 and measured on every run.
        for moment in ["00:15", "06:59", "07:00", "11:00", "16:05", "16:26", "18:59", "19:00", "23:30"]:
            deploy, sha, _ = dw.decide("workflow_run", cat(moment), TESTED, HEAD, tests_passed=True)
            self.assertTrue(deploy, moment)
            self.assertEqual(sha, TESTED, moment)

    def test_every_day_of_the_week_deploys(self):
        # 2026-09-13 is a Sunday, 2026-09-19 a Saturday.
        for day in ["2026-09-13", "2026-09-14", "2026-09-19"]:
            deploy, _, _ = dw.decide("workflow_run", cat("11:00", day), TESTED, HEAD, tests_passed=True)
            self.assertTrue(deploy, day)

    def test_what_deploys_is_the_commit_that_passed_not_mains_head(self):
        # A second merge landing while this one queues must not change what ships.
        deploy, sha, _ = dw.decide("workflow_run", cat("20:10"), TESTED, HEAD, tests_passed=False)
        self.assertTrue(deploy)
        self.assertEqual(sha, TESTED)

    def test_a_run_with_no_tested_commit_deploys_nothing(self):
        deploy, _, _ = dw.decide("workflow_run", cat("21:00"), "", HEAD, tests_passed=True)
        self.assertFalse(deploy)

    def test_nothing_is_held_for_later(self):
        # The old rule answered a merge during trading hours by naming the evening run. Nothing is
        # held now, so no reason may tell someone their merge is waiting for one.
        for moment in ["07:00", "12:00", "16:41", "18:59"]:
            _, _, reason = dw.decide("workflow_run", cat(moment), TESTED, HEAD, tests_passed=True)
            self.assertNotIn("19:30", reason, moment)
            self.assertNotIn("wait", reason.lower(), moment)


class TheBackstopRun(unittest.TestCase):
    def test_mains_head_deploys_when_its_tests_passed(self):
        deploy, sha, _ = dw.decide("schedule", cat("19:30"), "", HEAD, tests_passed=True)
        self.assertTrue(deploy)
        self.assertEqual(sha, HEAD)

    def test_mains_head_does_not_deploy_when_its_tests_have_not_passed(self):
        deploy, sha, reason = dw.decide("schedule", cat("19:30"), "", HEAD, tests_passed=False)
        self.assertFalse(deploy)
        self.assertEqual(sha, HEAD)
        self.assertIn("bbbbbbb", reason)

    def test_the_workflow_cron_is_the_backstop_run(self):
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


class TheCutoverIsStillMeasured(unittest.TestCase):
    """Merges ship during trading hours only because every cutover reports what it cost.

    Lifting the hold and losing the measurement in the same repository would leave nobody able to
    tell whether it had been a mistake, which is how the 14 September incident went unnoticed for a
    day. These read Update-Production.ps1 as text rather than run it: PowerShell is not on every
    machine this suite runs on, and scripts/Test-DeployPublicBindingSwap.ps1 covers the behaviour.
    """

    DEPLOY_SCRIPT = Path(__file__).resolve().parents[2] / "Update-Production.ps1"

    def setUp(self):
        self.text = self.DEPLOY_SCRIPT.read_text(encoding="utf-8")

    def test_the_cutover_is_watched_by_a_probe(self):
        self.assertIn("Start-PublicPortProbe -Url $Plan.PublicLiveUrl", self.text)
        self.assertIn("Stop-PublicPortProbe -Probe $probe", self.text)

    def test_the_binding_swap_is_one_commit(self):
        self.assertEqual(self.text.count("$Manager.CommitChanges()"), 1)

    def test_what_it_measured_reaches_the_run_summary(self):
        self.assertIn("GITHUB_STEP_SUMMARY", self.text)
        self.assertIn("CutoverOutageMs", self.text)


if __name__ == "__main__":
    unittest.main(verbosity=1)
