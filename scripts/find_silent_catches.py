#!/usr/bin/env python3
"""Find catch blocks that swallow a failure without recording it.

A production request spent exactly 60.000 seconds, returned an error, and left nothing in the log
but its Handling/Handled pair -- the catch that produced the error had no logger call in it. This
finds every catch block shaped the same way: one that ends a request unsuccessfully (returns an
Errors.* / Error.* value, or returns a default while discarding the exception) but writes nothing.

Rethrowing catches are not reported: the caller still gets the exception and can log it. Catches
that log at any level are not reported -- the level is a separate judgement, this only asks whether
the failure was recorded at all.

Usage:
    python scripts/find_silent_catches.py [root ...]     # default: ShopInventory
    python scripts/find_silent_catches.py --json         # machine-readable
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

CATCH = re.compile(r"\bcatch\b\s*(\([^)]*\))?\s*(when\s*\([^)]*\)\s*)?\{")
LOGS = re.compile(r"\b(_?[Ll]ogger|_log|log)\s*\.\s*(Log\w*|BeginScope)\s*\(")
RETURNS_ERROR = re.compile(r"\breturn\s+(Errors?\.|Error\b|new\s+Error\b)")
RETHROWS = re.compile(r"\bthrow\b")
SKIP_DIRS = {"obj", "bin", "Migrations", ".git", ".vs"}


def strip_noise(text: str) -> str:
    """Blank out comments and string/char literals so braces inside them do not shift the depth."""
    out, i, n = [], 0, len(text)
    while i < n:
        two = text[i : i + 2]
        if two == "//":
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
        elif two == "/*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("".join(c if c == "\n" else " " for c in text[i:j]))
            i = j
        elif text[i] == '"' and text[i : i + 3] == '"""':
            j = text.find('"""', i + 3)
            j = n if j < 0 else j + 3
            out.append("".join(c if c == "\n" else " " for c in text[i:j]))
            i = j
        elif text[i] in '"\'':
            quote, j = text[i], i + 1
            while j < n and text[j] != quote:
                j += 2 if text[j] == "\\" else 1
            j = min(j + 1, n)
            out.append("".join(c if c == "\n" else " " for c in text[i:j]))
            i = j
        else:
            out.append(text[i])
            i += 1
    return "".join(out)


def block_end(masked: str, open_brace: int) -> int:
    """Index just past the '}' matching the '{' at open_brace."""
    depth = 0
    for i in range(open_brace, len(masked)):
        if masked[i] == "{":
            depth += 1
        elif masked[i] == "}":
            depth -= 1
            if depth == 0:
                return i + 1
    return len(masked)


def find_in_file(path: Path) -> list[dict]:
    raw = path.read_text(encoding="utf-8", errors="replace")
    masked = strip_noise(raw)
    findings = []

    for match in CATCH.finditer(masked):
        open_brace = masked.index("{", match.start())
        end = block_end(masked, open_brace)
        body_masked = masked[open_brace + 1 : end - 1]
        body_raw = raw[open_brace + 1 : end - 1]

        if RETHROWS.search(body_masked) or LOGS.search(body_masked):
            continue
        if not RETURNS_ERROR.search(body_masked):
            continue

        findings.append(
            {
                "file": str(path).replace("\\", "/"),
                "line": raw.count("\n", 0, match.start()) + 1,
                "catch": " ".join(match.group(0).split()),
                "body": " ".join(body_raw.split())[:160],
            }
        )
    return findings


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("roots", nargs="*", default=["ShopInventory"])
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()

    findings = []
    for root in args.roots or ["ShopInventory"]:
        for path in sorted(Path(root).rglob("*.cs")):
            if SKIP_DIRS & set(path.parts):
                continue
            findings.extend(find_in_file(path))

    if args.json:
        print(json.dumps(findings, indent=2))
    else:
        for f in findings:
            print(f"{f['file']}:{f['line']}  {f['catch']}")
            print(f"    {f['body']}")
        print(f"\n{len(findings)} silent catch block(s).")

    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
