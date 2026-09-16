#!/usr/bin/env python3
"""Decides whether a "Deploy to production" run deploys, and which commit.

A merge used to be held out of trading hours, because a cutover dropped every request in flight.
Update-Production.ps1 moved the public port binding from the old IIS slot to the new one in a
separate write to applicationHost.config for each site it touched, and between the first removal and
the addition nothing listened on the port: new requests got a 404 from HTTP.sys, and requests already
running were cut off, so nginx answered them 502. On 14 September 2026 three merges deployed between
16:00 and 16:40 CAT. KEFSHOP's till lost its notification connection at the end of each, and a sale
posted at 16:41 got a 502 while the API went on writing it. The customer left with no receipt.

The handover is now one commit - see Switch-PublicTrafficToSite and its tests in
scripts/Test-DeployPublicBindingSwap.ps1 - so no committed configuration has the port belonging to
nobody, and every cutover measures the public port across the switch and says in the run summary how
long, if at all, it answered nothing. That number is the standing evidence for this file being what
it is. If it stops reading zero, put the window back.

    workflow_run (Tests passed on a push to main)   deploy the commit that passed, whatever the time
    schedule (19:30 CAT)                            deploy main's head if Tests passed for it, as a
                                                    backstop for a merge whose deploy never ran; the
                                                    deploy job skips a commit already live, so on an
                                                    ordinary night this does nothing
    workflow_dispatch                               deploy the chosen ref now

Reads its inputs from the environment and writes deploy, sha and reason to $GITHUB_OUTPUT.

    DEPLOY_EVENT    github.event_name
    TESTED_SHA      github.event.workflow_run.head_sha (workflow_run only)
    HEAD_SHA        github.sha
    TESTS_PASSED    "true" when Tests passed for HEAD_SHA (schedule only)
    DEPLOY_NOW      an ISO time in UTC, for tests; the real clock otherwise

    python scripts/DeployWindow/test_deploy_window.py   # the decision table
"""

import os
import sys
from datetime import datetime, time, timedelta, timezone

# Zimbabwe keeps UTC+2 all year. Only used to say the time in the reason a human reads.
CAT = timezone(timedelta(hours=2), "CAT")

# The backstop run, in CAT. The workflow's cron says 17:30 UTC, and test_deploy_window.py fails if
# the two disagree.
EVENING_DEPLOY = time(19, 30)


def decide(event: str, now_utc: datetime, tested_sha: str, head_sha: str, tests_passed: bool):
    """(deploy, sha, reason) for one run."""
    local = now_utc.astimezone(CAT).strftime("%H:%M")

    if event == "workflow_dispatch":
        return True, head_sha, f"Run by hand at {local} CAT."

    if event == "workflow_run":
        if not tested_sha:
            return False, "", "No tested commit on the triggering run, so there is nothing known good to deploy."

        return True, tested_sha, f"Tests passed at {local} CAT. The cutover is a handover, so it ships now."

    if event == "schedule":
        if not head_sha:
            return False, "", "No head commit for main."

        if not tests_passed:
            return False, head_sha, (
                f"Tests have not passed for main's head {head_sha[:7]}, so the backstop run does not ship it. "
                "It ships as soon as they do, or run the workflow by hand.")

        return True, head_sha, (
            f"Backstop run at {local} CAT of main's head {head_sha[:7]}, in case its merge did not deploy. "
            "Skipped without a cutover if it is already live.")

    return False, "", f"Not a deploy trigger: {event!r}."


def main() -> int:
    now = os.environ.get("DEPLOY_NOW")
    now_utc = datetime.fromisoformat(now).astimezone(timezone.utc) if now else datetime.now(timezone.utc)

    deploy, sha, reason = decide(
        os.environ.get("DEPLOY_EVENT", ""),
        now_utc,
        os.environ.get("TESTED_SHA", ""),
        os.environ.get("HEAD_SHA", ""),
        os.environ.get("TESTS_PASSED", "").lower() == "true",
    )

    print(f"deploy={str(deploy).lower()} sha={sha}")
    print(reason)

    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as handle:
            handle.write(f"deploy={str(deploy).lower()}\n")
            handle.write(f"sha={sha}\n")
            handle.write(f"reason={reason}\n")

    return 0


if __name__ == "__main__":
    sys.exit(main())
