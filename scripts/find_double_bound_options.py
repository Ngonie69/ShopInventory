"""Find options properties that bind to their default *plus* their configured value.

The ASP.NET Core configuration binder appends to a collection that already holds
items rather than replacing it — true for both `List<T>` and `T[]`, which bind
through different code paths. So an options property declared with a non-empty
collection initializer that is ALSO supplied in appsettings.json ends up holding
both, silently.

`DailyStockSettings.MonitoredWarehouses` was one: 21 in the initializer, the same
21 in appsettings.json, 42 at runtime — so the 07:00 snapshot job walked every
warehouse twice.

Usage, from a repo root:

    python scripts/find_double_bound_options.py [root ...]

Reports one line per affected property. Exit code 1 if any were found, so it can
gate a check. The match is deliberately loose — a collection initializer and a
JSON array of the same property name anywhere in the file — because a false
positive costs one glance and a miss costs a silent doubling. Verify each hit.
"""
from __future__ import annotations

import json
import pathlib
import re
import sys

# public List<string> Name { get; set; } = new() { ... }   /  = [ ... ]  /  = new[] { ... }
DECLARATION = re.compile(
    r'public\s+(?P<type>(?:List|IList|ICollection|IEnumerable|HashSet)\s*<[^>]+>|[\w.<>]+\[\])\s+'
    r'(?P<name>\w+)\s*\{\s*get;\s*set;\s*\}\s*=\s*(?P<init>new[^;]*?\{(?P<body>[^}]*)\}|\[(?P<body2>[^\]]*)\])\s*;',
    re.MULTILINE,
)

SKIP_DIRS = {'bin', 'obj', '.git', 'node_modules', 'artifacts', '.claude'}


def skipped(path: pathlib.Path, root: pathlib.Path) -> bool:
    """Whether a file sits under a directory we ignore.

    Relative to the root, never absolute: a git worktree lives under `.claude/worktrees/`,
    so testing the absolute path's parts against SKIP_DIRS silently skips the entire
    repository and reports it clean. That happened.
    """
    try:
        relative = path.relative_to(root)
    except ValueError:
        return False

    return any(part in SKIP_DIRS for part in relative.parts)


def json_arrays(path: pathlib.Path) -> dict[str, int]:
    """Every array-valued key in a JSON file, by key name, with its length."""
    try:
        data = json.loads(path.read_text(encoding='utf-8-sig'))
    except Exception:
        return {}

    found: dict[str, int] = {}

    def walk(node) -> None:
        if isinstance(node, dict):
            for key, value in node.items():
                if isinstance(value, list):
                    found[key] = len(value)
                walk(value)
        elif isinstance(node, list):
            for item in node:
                walk(item)

    walk(data)
    return found


def scan(root: pathlib.Path) -> list[tuple[str, str, int, int, str]]:
    configured: dict[str, tuple[int, str]] = {}
    for settings_file in root.rglob('appsettings*.json'):
        if skipped(settings_file, root):
            continue
        for key, length in json_arrays(settings_file).items():
            # Keep the longest; a Development override is usually the shorter one.
            if key not in configured or length > configured[key][0]:
                configured[key] = (length, str(settings_file.relative_to(root)))

    hits = []
    for source in root.rglob('*.cs'):
        if skipped(source, root):
            continue
        text = source.read_text(encoding='utf-8-sig', errors='replace')
        for match in DECLARATION.finditer(text):
            body = match.group('body') or match.group('body2') or ''
            if not body.strip():
                continue  # `= []` is the fixed shape; nothing to append to.

            name = match.group('name')
            if name not in configured:
                continue

            default_count = len([p for p in body.split(',') if p.strip()])
            config_count, where = configured[name]
            hits.append((
                str(source.relative_to(root)),
                name,
                default_count,
                config_count,
                where,
            ))

    return hits


def main() -> int:
    roots = [pathlib.Path(a).resolve() for a in sys.argv[1:]] or [pathlib.Path.cwd()]
    total = 0

    for root in roots:
        hits = scan(root)
        print(f'== {root}')
        if not hits:
            print('   no double-bound options properties')
            continue
        for source, name, default_count, config_count, where in sorted(hits):
            total += 1
            print(
                f'   {source}: {name} '
                f'-> initializer {default_count} + {where} {config_count} '
                f'= {default_count + config_count} at runtime'
            )

    return 1 if total else 0


if __name__ == '__main__':
    raise SystemExit(main())
