"""Route every WinRM call site in Update-Production.ps1 through the deployment-session helpers.

Two exact literal forms are replaced, nothing else:

  Invoke-Command -ComputerName $ProductionServer -Credential $Credential -Authentication Negotiate -ScriptBlock {
      -> Invoke-DeploymentCommand -ScriptBlock {

  New-PSSession -ComputerName $ProductionServer -Credential $Credential -Authentication Negotiate -ErrorAction Stop
      -> New-DeploymentSession

Leading indentation is preserved, and the trailing `-ErrorAction Stop` that each Invoke-Command
site already carries binds to the helper's own common parameter, so no site changes shape.

Run with --check to assert the file is fully converted (no remaining raw call sites, and the
expected number of helper calls). That is what makes this re-runnable as a verification.
"""

import argparse
import re
import sys
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "Update-Production.ps1"

INVOKE_OLD = (
    "Invoke-Command -ComputerName $ProductionServer -Credential $Credential "
    "-Authentication Negotiate -ScriptBlock {"
)
INVOKE_NEW = "Invoke-DeploymentCommand -ScriptBlock {"

SESSION_OLD = (
    "New-PSSession -ComputerName $ProductionServer -Credential $Credential "
    "-Authentication Negotiate -ErrorAction Stop"
)
SESSION_NEW = "New-DeploymentSession"

EXPECTED_INVOKE = 8
EXPECTED_SESSION = 2

# Any -ComputerName pointed at the production server that is not one of the two forms above.
# If this ever matches, a call site was added that does not route through the helper.
STRAY = re.compile(r"-ComputerName\s+\$ProductionServer")
# Test-Connection deliberately keeps the address: it is an ICMP reachability check, not a session.
STRAY_ALLOWED = ("Test-Connection -ComputerName $ProductionServer",)


def sites(text, needle):
    return [
        (i + 1, line)
        for i, line in enumerate(text.splitlines())
        if needle in line
    ]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("path", nargs="?", default=str(SCRIPT))
    parser.add_argument("--check", action="store_true", help="verify only, change nothing")
    args = parser.parse_args()

    path = Path(args.path)
    text = path.read_text(encoding="utf-8")

    if not args.check:
        before_invoke = sites(text, INVOKE_OLD)
        before_session = sites(text, SESSION_OLD)

        print(f"{len(before_invoke)} Invoke-Command site(s):")
        for line_no, _ in before_invoke:
            print(f"  line {line_no}")
        print(f"{len(before_session)} New-PSSession site(s):")
        for line_no, _ in before_session:
            print(f"  line {line_no}")

        text = text.replace(INVOKE_OLD, INVOKE_NEW).replace(SESSION_OLD, SESSION_NEW)
        path.write_text(text, encoding="utf-8", newline="")
        print(f"\nRewrote {len(before_invoke) + len(before_session)} site(s).")

    # Verification, run in both modes.
    text = path.read_text(encoding="utf-8")
    problems = []

    remaining_invoke = sites(text, INVOKE_OLD)
    remaining_session = sites(text, SESSION_OLD)
    if remaining_invoke:
        problems.append(f"{len(remaining_invoke)} raw Invoke-Command site(s) left: "
                        f"{[n for n, _ in remaining_invoke]}")
    if remaining_session:
        problems.append(f"{len(remaining_session)} raw New-PSSession site(s) left: "
                        f"{[n for n, _ in remaining_session]}")

    got_invoke = sites(text, INVOKE_NEW)
    got_session = [
        (i + 1, line)
        for i, line in enumerate(text.splitlines())
        if line.rstrip().endswith(SESSION_NEW)
    ]
    if len(got_invoke) != EXPECTED_INVOKE:
        problems.append(f"expected {EXPECTED_INVOKE} Invoke-DeploymentCommand calls, found {len(got_invoke)}")
    if len(got_session) != EXPECTED_SESSION:
        problems.append(f"expected {EXPECTED_SESSION} New-DeploymentSession calls, found {len(got_session)}")

    for i, line in enumerate(text.splitlines(), start=1):
        if STRAY.search(line) and not any(a in line for a in STRAY_ALLOWED):
            problems.append(f"line {i}: -ComputerName $ProductionServer outside the helper: {line.strip()}")

    print("\nVerification:")
    print(f"  Invoke-DeploymentCommand calls : {len(got_invoke)} (expected {EXPECTED_INVOKE})")
    for line_no, _ in got_invoke:
        print(f"      line {line_no}")
    print(f"  New-DeploymentSession calls    : {len(got_session)} (expected {EXPECTED_SESSION})")
    for line_no, _ in got_session:
        print(f"      line {line_no}")

    if problems:
        print("\nFAIL:")
        for p in problems:
            print(f"  - {p}")
        return 1

    print("\nOK: every production WinRM call site routes through the helper.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
