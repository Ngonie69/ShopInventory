#!/usr/bin/env python3
"""Decides whether a "Deploy to production" run deploys, and which commit.

A production cutover drops every request in flight. Update-Production.ps1 moves the public port
binding from the old IIS slot to the new one, and between the removal and the addition nothing listens
on the port: new requests get a 404 from HTTP.sys, and requests already running are cut off, so nginx
answers them 502. On 14 September 2026 three merges deployed between 16:00 and 16:40 CAT. KEFSHOP's
till lost its notification connection at the end of each, and a sale posted at 16:41 got a 502 while
the API went on writing it. The customer left with no receipt.

So a merge no longer deploys during trading hours:

    workflow_run (Tests passed on a push to main)
        outside 07:00-19:00 CAT   deploy the commit that passed
        inside it                 do not; the evening run deploys it
    schedule (19:30 CAT)          deploy main's head, if Tests passed for it
                                  (the deploy job skips a commit that is already live)
    workflow_dispatch             deploy now, whatever the time: the way to ship an urgent fix

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

# Zimbabwe keeps UTC+2 all year, with no daylight saving to move the window.
CAT = timezone(timedelta(hours=2), "CAT")

# Every shop's hours fall inside this, as Kefalos gave them on 14 September 2026: the earliest opens at
# 08:00 and the latest closes at 17:00, and CORMACH2 trades on Sundays. The hour before opening is for
# vans getting ready. The two hours after closing cover the 17:00 incoming-payment run and the 18:00
# consolidation, both of which a restart would cut through. Every day, because CORMACH2 has no day off.
TRADING_STARTS = time(7, 0)
TRADING_ENDS = time(19, 0)

# The scheduled run, in CAT. Kept here beside the window it has to fall outside; the workflow's cron
# says 17:30 UTC, and test_deploy_window.py fails if the two disagree.
EVENING_DEPLOY = time(19, 30)


def trading(now_utc: datetime) -> bool:
    """Whether a deploy starting at now_utc would start during trading hours."""
    local = now_utc.astimezone(CAT).time()
    return TRADING_STARTS <= local < TRADING_ENDS


def decide(event: str, now_utc: datetime, tested_sha: str, head_sha: str, tests_passed: bool):
    """(deploy, sha, reason) for one run."""
    local = now_utc.astimezone(CAT).strftime("%H:%M")
    window = f"{TRADING_STARTS:%H:%M}-{TRADING_ENDS:%H:%M} CAT"

    if event == "workflow_dispatch":
        return True, head_sha, f"Run by hand at {local} CAT: deploys whatever the time."

    if event == "workflow_run":
        if not tested_sha:
            return False, "", "No tested commit on the triggering run, so there is nothing known good to deploy."

        if trading(now_utc):
            return False, tested_sha, (
                f"Tests passed at {local} CAT, inside trading hours ({window}). A cutover drops the requests in "
                f"flight, so this waits for the {EVENING_DEPLOY:%H:%M} CAT deploy. Run the workflow by hand to "
                "deploy now.")

        return True, tested_sha, f"Tests passed at {local} CAT, outside trading hours ({window})."

    if event == "schedule":
        if not head_sha:
            return False, "", "No head commit for main."

        if not tests_passed:
            return False, head_sha, (
                f"Tests have not passed for main's head {head_sha[:7]}, so the evening deploy does not ship it. "
                "It deploys the next evening once they do, or run the workflow by hand.")

        return True, head_sha, f"Evening deploy at {local} CAT of main's head {head_sha[:7]}."

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
