"""Insert a placeholder <param> tag for every CS1573 warning in a build log.

CS1573 means a declaration documents some of its parameters but not all. The fix is a real
description for each missing one, which no script can write, so this does the mechanical half:
it finds each declaration's doc block, works out where the missing tag belongs in signature
order, and inserts

    /// <param name="X">TODO-DOC</param>

with the block's own indentation. Fill every TODO-DOC by hand, then prove none is left:

    python scripts/add_missing_param_tags.py build.log           # insert placeholders
    python scripts/add_missing_param_tags.py --check              # exit 1 while any TODO-DOC remains

The log must come from `dotnet build --no-incremental`; an incremental build omits warnings for
files it did not recompile. Running it twice over the same log is safe: a tag that is already
there is skipped.
"""
from __future__ import annotations

import argparse
import collections
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
PLACEHOLDER = "TODO-DOC"
WARNING = re.compile(
    r"(?P<path>[A-Za-z]:[\\/][^(]+?|[^\s(]+?)\((?P<line>\d+),(?P<col>\d+)\): warning CS1573: "
    r"Parameter '(?P<name>\w+)' has no matching param tag"
)
PARAM_OPEN = re.compile(r'<param\s+name="(?P<name>\w+)"')


def parse_log(log: pathlib.Path) -> dict[pathlib.Path, set[tuple[int, int, str]]]:
    sites: dict[pathlib.Path, set[tuple[int, int, str]]] = collections.defaultdict(set)
    for raw in log.read_text(encoding="utf-8", errors="replace").splitlines():
        match = WARNING.search(raw)
        if not match:
            continue
        path = pathlib.Path(match["path"].strip())
        if not path.is_absolute():
            path = ROOT / path
        sites[path.resolve()].add((int(match["line"]), int(match["col"]), match["name"]))
    return sites


def doc_block_end(lines: list[str], param_line: int) -> int:
    """Index of the last /// line above the declaration that holds the parameter at param_line."""
    index = param_line - 1
    while index >= 0:
        stripped = lines[index].lstrip()
        if stripped.startswith("///"):
            return index
        index -= 1
    raise ValueError(f"no doc comment above line {param_line + 1}")


def doc_block_start(lines: list[str], end: int) -> int:
    index = end
    while index - 1 >= 0 and lines[index - 1].lstrip().startswith("///"):
        index -= 1
    return index


def signature_position(lines: list[str], decl_start: int, name: str) -> tuple[int, int] | None:
    """Where a parameter named `name` is declared, searching from the declaration onward."""
    token = re.compile(rf"\b{re.escape(name)}\s*(?:[,)=]|$)")
    for index in range(decl_start, min(len(lines), decl_start + 200)):
        match = token.search(lines[index])
        if match:
            return index, match.start()
    return None


def fix_file(path: pathlib.Path, sites: set[tuple[int, int, str]]) -> int:
    text = path.read_text(encoding="utf-8-sig")
    had_bom = path.read_bytes().startswith(b"\xef\xbb\xbf")
    newline = "\r\n" if "\r\n" in text else "\n"
    lines = text.split(newline)

    # Group the missing parameters by the doc block they belong to.
    by_block: dict[int, list[tuple[int, int, str]]] = collections.defaultdict(list)
    for line, col, name in sites:
        end = doc_block_end(lines, line - 1)
        by_block[end].append((line - 1, col - 1, name))

    inserted = 0
    # Work bottom-up so earlier insertions do not shift later block indexes.
    for end in sorted(by_block, reverse=True):
        start = doc_block_start(lines, end)
        decl_start = end + 1

        # Existing param tags: (signature position, first line, last line).
        existing: list[tuple[tuple[int, int], int, int]] = []
        present: set[str] = set()
        indent = None
        index = start
        while index <= end:
            match = PARAM_OPEN.search(lines[index])
            if match:
                first = index
                while "</param>" not in lines[index] and index < end:
                    index += 1
                name = match["name"]
                present.add(name)
                if indent is None:
                    indent = lines[first][: lines[first].index("<param")]
                position = signature_position(lines, decl_start, name) or (10**9, 0)
                existing.append((position, first, index))
            index += 1
        if indent is None:
            raise ValueError(f"{path}:{end + 1}: CS1573 reported but the block has no <param> tag")

        missing = sorted({(line, col, name) for line, col, name in by_block[end] if name not in present})
        # Insert each missing tag before the first existing tag declared after it, else after the last.
        insert_at: dict[int, list[str]] = collections.defaultdict(list)
        last_param_line = max(last for _, _, last in existing)
        for line, col, name in missing:
            later = [first for position, first, _ in existing if position > (line, col)]
            target = min(later) if later else last_param_line + 1
            insert_at[target].append(f'{indent}<param name="{name}">{PLACEHOLDER}</param>')
            inserted += 1
        for target in sorted(insert_at, reverse=True):
            lines[target:target] = insert_at[target]

    output = newline.join(lines)
    path.write_bytes(("﻿" if had_bom else "").encode("utf-8") + output.encode("utf-8"))
    return inserted


def check() -> int:
    result = subprocess.run(
        ["git", "grep", "-n", f">{PLACEHOLDER}<", "--", "*.cs", "*.razor"],
        cwd=ROOT, capture_output=True, text=True,
    )
    if result.stdout.strip():
        print(result.stdout.rstrip())
        print(f"--- {len(result.stdout.strip().splitlines())} placeholder(s) still to describe.")
        return 1
    print(f"OK: no {PLACEHOLDER} placeholders left.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("log", nargs="?", type=pathlib.Path, help="output of dotnet build --no-incremental")
    parser.add_argument("--check", action="store_true", help="fail while any placeholder remains")
    args = parser.parse_args()
    if args.check:
        return check()
    if not args.log:
        parser.error("a build log is required unless --check is given")

    total = 0
    for path, sites in sorted(parse_log(args.log).items()):
        count = fix_file(path, sites)
        total += count
        print(f"{count:3} {path.relative_to(ROOT).as_posix()}")
    print(f"--- {total} placeholder tag(s) inserted.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
