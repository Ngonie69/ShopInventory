#!/usr/bin/env python3
"""Take a role off the role gate of every API write action, leaving its reads alone.

For each action in ShopInventory/Controllers whose attributes carry [HttpPost], [HttpPut],
[HttpPatch] or [HttpDelete]:

  * an action-level [Authorize(... Roles = "...")] naming the role loses it;
  * an action with no Roles gate of its own, on a controller whose class-level Roles gate names
    the role, gains an action-level [Authorize(Roles = "...")] holding the class's roles without it
    (ASP.NET Core requires every Roles attribute to pass, so the action narrows the class).

Roles written either as a literal list ("Admin,Manager") or as interpolated constants
($"{ApplicationRoles.Admin},{ApplicationRoles.Manager}") are handled.

Actions named in KEEP are skipped and reported. The script is idempotent: a second run changes
nothing. Deterministic, Python 3 stdlib only.

Usage:
    python scripts/withhold_role_from_api_writes.py [--role Manager] [--check]
        --check   report what would change and exit 1 if anything would, without writing
"""
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CONTROLLERS = os.path.join(REPO, "ShopInventory", "Controllers")

# Writes the role keeps, by Controller.Action, with the reason.
KEEP = {
    # Emailing a report is reading it somewhere else (decided 2026-10-28).
    "DesktopIntegrationController.EmailEndOfDayReport": "report email",
    "DesktopIntegrationController.SendDesktopSalesReviewEmail": "report email",
    "ReportController.SendSalesPerformanceEmail": "report email",
    # Re-reads a warehouse's stock from SAP into the snapshot the page shows; raises nothing.
    "DesktopIntegrationController.RefreshWarehouseStock": "stock refresh is a read",
}

WRITE_VERBS = re.compile(r"\[\s*Http(Post|Put|Patch|Delete)\b")
ROLES_ATTR = re.compile(r'(\[\s*Authorize\s*\([^\]]*?Roles\s*=\s*)(\$?"[^"]*")')
METHOD = re.compile(r"^\s*public\s+(?:async\s+)?[\w<>\[\],\s?.]+?\s+(\w+)\s*\(")
CLASS = re.compile(r"^\s*public\s+(?:sealed\s+|partial\s+)*class\s+(\w+)")


def split_roles(literal):
    """'"A,B"' or '$"{X.A},{X.B}"' -> (is_interpolated, [items as written])."""
    interpolated = literal.startswith("$")
    body = literal[2:-1] if interpolated else literal[1:-1]
    return interpolated, [item.strip() for item in body.split(",") if item.strip()]


def is_role(item, role):
    return item == role or item == "{ApplicationRoles.%s}" % role


def join_roles(interpolated, items):
    return ('$"' if interpolated else '"') + ",".join(items) + '"'


def process(path, role, changes, kept):
    with open(path, encoding="utf-8-sig", newline="") as f:
        raw = f.read()
    had_bom = raw.startswith("﻿")
    eol = "\r\n" if "\r\n" in raw else "\n"
    lines = raw.split(eol)

    class_name = None
    class_roles = None  # (interpolated, items) of the class-level Roles gate
    attrs_start = None
    out = list(lines)
    inserts = []  # (index, text)

    for i, line in enumerate(lines):
        m = CLASS.match(line)
        if m:
            class_name = m.group(1)
            # Class attributes are the attribute lines directly above.
            j = i - 1
            class_roles = None
            while j >= 0 and (lines[j].strip().startswith("[") or lines[j].strip().startswith("///")):
                rm = ROLES_ATTR.search(lines[j])
                if rm:
                    class_roles = split_roles(rm.group(2))
                j -= 1
            continue

        m = METHOD.match(line)
        if not m or class_name is None:
            continue
        action = m.group(1)

        # The action's attribute block: contiguous attribute/doc lines above the signature.
        j = i - 1
        block = []
        while j >= 0 and (lines[j].strip().startswith("[") or lines[j].strip().startswith("///")):
            block.append(j)
            j -= 1
        if not any(WRITE_VERBS.search(lines[k]) for k in block):
            continue

        key = f"{class_name}.{action}"
        own = [k for k in block if ROLES_ATTR.search(lines[k])]
        names_role = any(any(is_role(x, role) for x in split_roles(ROLES_ATTR.search(lines[k]).group(2))[1]) for k in own)
        class_names_role = class_roles is not None and any(is_role(x, role) for x in class_roles[1])
        if not names_role and not (not own and class_names_role):
            continue
        if key in KEEP:
            kept.append(f"{rel(path)}:{i + 1} {key} ({KEEP[key]})")
            continue

        if own:
            for k in own:
                rm = ROLES_ATTR.search(out[k])
                interpolated, items = split_roles(rm.group(2))
                if not any(is_role(x, role) for x in items):
                    continue
                kept_items = [x for x in items if not is_role(x, role)]
                out[k] = out[k][: rm.start(2)] + join_roles(interpolated, kept_items) + out[k][rm.end(2):]
                changes.append(f"{rel(path)}:{k + 1} {key}: removed {role}")
        else:
            interpolated, items = class_roles
            kept_items = [x for x in items if not is_role(x, role)]
            verb_line = next(k for k in sorted(block) if WRITE_VERBS.search(lines[k]))
            indent = re.match(r"\s*", lines[verb_line]).group(0)
            inserts.append((verb_line + 1, f"{indent}[Authorize(Roles = {join_roles(interpolated, kept_items)})]"))
            changes.append(f"{rel(path)}:{verb_line + 1} {key}: narrowed class gate without {role}")

    for index, text in sorted(inserts, reverse=True):
        out.insert(index, text)

    new = eol.join(out)
    if new != eol.join(lines):
        return ("﻿" if had_bom else "") + new
    return None


def rel(path):
    return os.path.relpath(path, REPO).replace("\\", "/")


def main():
    args = sys.argv[1:]
    role = args[args.index("--role") + 1] if "--role" in args else "Manager"
    check = "--check" in args
    changes, kept, written = [], [], []
    for name in sorted(os.listdir(CONTROLLERS)):
        if not name.endswith(".cs"):
            continue
        path = os.path.join(CONTROLLERS, name)
        new = process(path, role, changes, kept)
        if new is not None:
            written.append(path)
            if not check:
                with open(path, "w", encoding="utf-8", newline="") as f:
                    f.write(new)
    for line in changes:
        print("CHANGE", line)
    for line in kept:
        print("KEEP  ", line)
    print(f"{len(changes)} change(s) in {len(written)} file(s); {len(kept)} kept")
    if check and changes:
        sys.exit(1)


if __name__ == "__main__":
    main()
