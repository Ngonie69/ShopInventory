"""Pin every `uses: owner/repo@tag` in .github/workflows to the commit the tag names today.

A tag is movable: whoever controls the action's repository can point `v7` at new code, and every
workflow here would run it on the next trigger - including the deploy job on the production runner.
A commit sha cannot be moved. The tag is kept as a trailing comment so Dependabot's github-actions
updates (configured in .github/dependabot.yml) still recognise the version and bump sha and comment
together.

Usage:
    python scripts/pin_github_actions.py          # rewrite the workflows in place
    python scripts/pin_github_actions.py --check  # exit 1 if any action is not pinned

Resolving tags needs an authenticated `gh` CLI. Re-running over already pinned files is a no-op.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

WORKFLOWS = Path(__file__).resolve().parent.parent / ".github" / "workflows"

# `uses: owner/repo[/path]@ref` with an optional trailing comment. Local (`./`) and docker
# (`docker://`) references carry no owner/repo and are left alone.
USES = re.compile(
    r"^(?P<indent>\s*-?\s*uses:\s*)"
    r"(?P<action>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_./-]+)?)"
    r"@(?P<ref>[A-Za-z0-9_.-]+)"
    r"(?P<comment>\s*#.*)?$"
)
SHA = re.compile(r"^[0-9a-f]{40}$")


def resolve(action: str, ref: str, cache: dict[tuple[str, str], str]) -> str:
    repo = "/".join(action.split("/")[:2])
    key = (repo, ref)
    if key not in cache:
        # The commits endpoint peels annotated tags, so this is always the commit, never a tag object.
        sha = subprocess.run(
            ["gh", "api", f"repos/{repo}/commits/{ref}", "--jq", ".sha"],
            check=True, capture_output=True, text=True,
        ).stdout.strip()
        if not SHA.match(sha):
            raise SystemExit(f"{repo}@{ref} resolved to {sha!r}, which is not a commit sha")
        cache[key] = sha
    return cache[key]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="report unpinned actions, change nothing")
    args = parser.parse_args()

    cache: dict[tuple[str, str], str] = {}
    unpinned: list[str] = []

    for path in sorted(WORKFLOWS.glob("*.y*ml")):
        lines = path.read_text(encoding="utf-8").splitlines(keepends=True)
        changed = False
        for number, line in enumerate(lines, start=1):
            body = line.rstrip("\r\n")
            match = USES.match(body)
            if not match or SHA.match(match["ref"]):
                continue
            where = f"{path.relative_to(WORKFLOWS.parent.parent)}:{number}"
            unpinned.append(f"{where}  {match['action']}@{match['ref']}")
            if args.check:
                continue
            sha = resolve(match["action"], match["ref"], cache)
            newline = line[len(body):]
            lines[number - 1] = f"{match['indent']}{match['action']}@{sha} # {match['ref']}{newline}"
            changed = True
        if changed:
            path.write_text("".join(lines), encoding="utf-8", newline="")

    for entry in unpinned:
        print(entry)
    if args.check:
        print(f"{len(unpinned)} unpinned action reference(s)")
        return 1 if unpinned else 0
    print(f"pinned {len(unpinned)} reference(s) across {len(cache)} distinct action version(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
