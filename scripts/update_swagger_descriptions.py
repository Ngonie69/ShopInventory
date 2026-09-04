#!/usr/bin/env python3
"""Give every API action an XML <summary>, so Swagger UI describes it.

Swagger reads its per-operation description from the compiled XML doc file, which is built from the
`///` comments on the action. An action without one renders in the UI as a bare verb and path, and
the reader has to guess from the URL what it does -- 373 of the 554 actions were in that state.

Writing 373 summaries by hand is both slow and unverifiable. But the repo already keeps two
catalogues that describe every route in prose, and `scripts/Test-RouteCatalogues.ps1` holds both
of them true against the controllers:

    API.md                                                - endpoint tables, the richer wording
    ShopInventory.Web/Components/Pages/ApiExplorer.razor  - _apiCategories, one row per route

So the summaries are derived rather than invented: this parses the routes out of the controllers,
looks each one up in those catalogues, and writes the `///` block above the action.
`swagger_descriptions.json` next to this script wins over both, for the routes whose catalogue
wording is too terse to help a reader (a lone "Decide", "Search", "Add").

An action that already has a `///` comment is never touched, so hand-written wording -- including
anything edited after a run -- survives. Re-running changes nothing, which makes the check for
"did the catalogue drift" just: run it and read the diff.

Usage:
    python scripts/update_swagger_descriptions.py --report    # coverage + sources, writes nothing
    python scripts/update_swagger_descriptions.py --diff      # show the blocks it would insert
    python scripts/update_swagger_descriptions.py             # rewrite the controllers
    python scripts/update_swagger_descriptions.py --check     # exit 1 if any action lacks a summary
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
CONTROLLERS = REPO / "ShopInventory" / "Controllers"
API_MD = REPO / "API.md"
EXPLORER = REPO / "ShopInventory.Web" / "Components" / "Pages" / "ApiExplorer.razor"
OVERRIDES = Path(__file__).resolve().parent / "swagger_descriptions.json"

VERBS = ("Get", "Post", "Put", "Delete", "Patch", "Head", "Options")
HTTP_ATTR = re.compile(r"^\s*\[Http(" + "|".join(VERBS) + r")(?:\(\s*(?:\"([^\"]*)\")?[^)]*\))?\]")
CLASS_ROUTE = re.compile(r'^\s*\[Route\("([^"]+)"\)\]')
CLASS_DECL = re.compile(r"^\s*(?:public|internal)\s+(?:sealed\s+|abstract\s+|partial\s+)*class\s+(\w+)")
EXPLORER_ROW = re.compile(r'new\(\s*"([A-Z]+)"\s*,\s*"([^"]+)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)')
MD_ROW = re.compile(r"^\|\s*([A-Z]+)\s*\|\s*`([^`]+)`\s*\|(?:[^|]*\|)?\s*([^|]*?)\s*\|\s*$")


def norm(path: str) -> str:
    """Key a route so a catalogue row and a controller route compare equal.

    Route constraints and parameter names are not part of the identity: `{id:int}` and `{id}` are
    the same route, and a catalogue that says `{docEntry}` where the controller says `{DocEntry}`
    is not drift. Two routes on one controller differing only in a parameter name would be
    ambiguous to ASP.NET anyway, so collapsing them cannot merge two live endpoints.
    """
    path = path.split("?", 1)[0].strip().strip("/")
    path = re.sub(r"\{[^}]*\}", "{}", path)
    return path.lower()


def action_name(lines: list[str], attr_line: int) -> str:
    """The method name below an [Http*] attribute, for the last-resort generated summary."""
    for line in lines[attr_line : attr_line + 12]:
        if line.strip().startswith("["):
            continue
        m = re.search(r"\b(\w+)\s*\(", line)
        if m:
            return m.group(1)
    return ""


def parse_controllers() -> list[dict]:
    """Every action, with the line its attribute block starts on and whether it is documented."""
    actions = []
    for file in sorted(CONTROLLERS.glob("*.cs")):
        lines = file.read_text(encoding="utf-8-sig").split("\n")
        base, cls = None, None
        for i, line in enumerate(lines):
            m = CLASS_ROUTE.match(line)
            if m and line.startswith("["):
                base = m.group(1)
                continue
            m = CLASS_DECL.match(line)
            if m and cls is None:
                cls = m.group(1)
                if base:
                    base = base.replace("[controller]", cls.removesuffix("Controller"))
                continue
            m = HTTP_ATTR.match(line)
            if not m:
                continue
            verb, tail = m.group(1).upper(), m.group(2)
            path = "/".join(p for p in [(base or "").strip("/"), (tail or "").strip("/")] if p)

            # Walk back over the rest of the attribute block, then over any `///` block above it.
            # Having a `///` comment is not the same as having a <summary>: one action carries only
            # <remarks>, which Swashbuckle renders as the description while the summary line stays
            # empty. That one needs a summary as much as an undocumented action does, so the test
            # is for the tag, not for the comment. A summary written above an existing block keeps
            # the `///` lines contiguous and puts the summary first, which is the conventional order.
            start, j = i, i - 1
            while j >= 0 and (lines[j].strip().startswith("[") or lines[j].strip() == ""):
                if lines[j].strip().startswith("["):
                    start = j
                j -= 1
            doc_block = []
            while j >= 0 and lines[j].strip().startswith("///"):
                doc_block.append(lines[j])
                start = j
                j -= 1
            documented = "<summary>" in " ".join(doc_block)
            actions.append(
                {
                    "file": file,
                    "line": start,
                    "attr_line": i,
                    "verb": verb,
                    "path": "/" + path,
                    "key": verb + " " + norm(path),
                    "name": action_name(lines, i),
                    "indent": lines[i][: len(lines[i]) - len(lines[i].lstrip())],
                    "documented": documented,
                }
            )
    return actions


def parse_api_md() -> dict[str, str]:
    """Endpoint-table rows: | VERB | `path` | permission | description |."""
    found: dict[str, str] = {}
    for line in API_MD.read_text(encoding="utf-8").split("\n"):
        m = MD_ROW.match(line)
        if not m:
            continue
        verb, path, desc = m.group(1), m.group(2), m.group(3)
        if verb not in {v.upper() for v in VERBS} or not path.startswith("/"):
            continue
        desc = re.sub(r"`([^`]*)`", r"\1", desc).replace("**", "").strip()
        if desc:
            found.setdefault(verb + " " + norm(path), desc)
    return found


def parse_explorer() -> dict[str, str]:
    """ApiExplorer.razor's new("VERB", "/api/...", "description") rows."""
    text = EXPLORER.read_text(encoding="utf-8")
    return {
        m.group(1) + " " + norm(m.group(2)): m.group(3).replace('\\"', '"').strip()
        for m in EXPLORER_ROW.finditer(text)
    }


def load_overrides() -> dict[str, str]:
    if not OVERRIDES.exists():
        return {}
    raw = json.loads(OVERRIDES.read_text(encoding="utf-8"))
    out = {}
    for k, v in raw.items():
        if k.startswith("//"):
            continue
        verb, path = k.split(" ", 1)
        out[verb.upper() + " " + norm(path)] = v
    return out


def from_name(name: str) -> str:
    """"GetPagedInvoices" -> "Get paged invoices". The floor, not the goal."""
    words = re.findall(r"[A-Z]+(?![a-z])|[A-Z][a-z]*|\d+|[a-z]+", name or "")
    if not words:
        return ""
    return " ".join([words[0].capitalize()] + [w if w.isupper() else w.lower() for w in words[1:]])


# A trailing parenthetical whose every comma-separated item is a parameter name, optionally with
# its default: "(page 1, pageSize 20, cardCode, fromDate, toDate)". Swagger already lists the
# parameters and their defaults directly under the summary, so repeating them there is noise.
# Anything else stays: "(cached 5 min)", "(1-5)", "(multipart form)", "(holds inventory)" all say
# something the parameter table does not.
PARAM_LIST = re.compile(r"\s*\(([^()]*)\)\s*$")
PARAM_ITEM = re.compile(r"^[a-z][A-Za-z0-9]*(?:\s+(?:\d+|default\s+\w+|true|false))?$")


def strip_param_list(desc: str) -> str:
    m = PARAM_LIST.search(desc)
    if not m:
        return desc
    items = [i.strip() for i in m.group(1).split(",")]
    if items and all(PARAM_ITEM.match(i) for i in items):
        return desc[: m.start()].rstrip()
    return desc


def polish(desc: str) -> str:
    """One line, sentence case, no trailing period -- the style the documented actions already use.

    XML-escapes last, so a description mentioning `<` or `&` cannot break the doc file.
    """
    desc = " ".join(strip_param_list(desc).split()).rstrip(".")
    if desc and desc[0].islower() and not desc.split(" ")[0].isupper():
        desc = desc[0].upper() + desc[1:]
    return desc.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def resolve(actions: list[dict]) -> None:
    overrides, md, explorer = load_overrides(), parse_api_md(), parse_explorer()
    for a in actions:
        for source, table in (("override", overrides), ("API.md", md), ("ApiExplorer", explorer)):
            if table.get(a["key"]):
                a["desc"], a["source"] = polish(table[a["key"]]), source
                break
        else:
            a["desc"], a["source"] = polish(from_name(a["name"])), "action name"


def block(a: dict) -> list[str]:
    i = a["indent"]
    return [f"{i}/// <summary>", f"{i}/// {a['desc']}", f"{i}/// </summary>"]


def insertions(actions: list[dict]) -> list[dict]:
    """One block per action, not per route.

    Three actions carry two [Http*] attributes -- `/api/vansales/order` and `/order/with-batches`
    are one method on two routes, not two endpoints. They share an insertion point, and writing a
    block for each would stack two `///` comments on one method. The first-declared route wins,
    because that is the one the catalogues describe in full; the extra route's row tends to say
    only that it is the same action ("The same action as /order").
    """
    best: dict[tuple[Path, int], dict] = {}
    for a in actions:
        if a["documented"] or not a["desc"]:
            continue
        at = (a["file"], a["line"])
        if at not in best or a["attr_line"] < best[at]["attr_line"]:
            best[at] = a
    return list(best.values())


def apply(actions: list[dict]) -> tuple[int, int]:
    """Insert the blocks bottom-up, so an earlier insertion never shifts a later line number.

    The controllers are CRLF without a BOM. Both are preserved from what the file already is
    rather than assumed: writing LF into a CRLF file leaves it mixed, and adding a BOM shows up as
    a whole-file diff that buries the change this script actually made.
    """
    by_file: dict[Path, list[dict]] = {}
    for a in insertions(actions):
        by_file.setdefault(a["file"], []).append(a)
    written = 0
    for file, items in by_file.items():
        raw = file.read_bytes()
        bom = raw.startswith(b"\xef\xbb\xbf")
        text = raw.decode("utf-8-sig")
        newline = "\r\n" if "\r\n" in text else "\n"
        lines = text.split(newline)
        for a in sorted(items, key=lambda x: -x["line"]):
            lines[a["line"] : a["line"]] = block(a)
            written += 1
        out = newline.join(lines)
        file.write_bytes((b"\xef\xbb\xbf" if bom else b"") + out.encode("utf-8"))
    return written, len(by_file)


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("--report", action="store_true", help="coverage and description sources; writes nothing")
    ap.add_argument("--diff", action="store_true", help="print every block that would be inserted")
    ap.add_argument("--check", action="store_true", help="exit 1 if any action would still lack a summary")
    args = ap.parse_args()

    actions = parse_controllers()
    resolve(actions)
    missing = [a for a in actions if not a["documented"]]

    # An override keyed at a route that does not exist is invisible otherwise: it simply never
    # applies, and the endpoint keeps the table wording the override was written to replace.
    orphans = sorted(set(load_overrides()) - {a["key"] for a in actions})
    if orphans:
        print(f"WARNING: {len(orphans)} override(s) match no route -- check the verb and path:")
        for key in orphans:
            print(f"  {key}")

    if args.report or args.diff:
        print(f"{len(actions)} actions | {len(actions) - len(missing)} documented | {len(missing)} to write")
        for source, n in Counter(a["source"] for a in missing).most_common():
            print(f"  {n:4d} from {source}")
        weak = [a for a in missing if a["source"] == "action name"]
        if weak:
            print(f"\nNo catalogue entry -- summary generated from the method name ({len(weak)}):")
            for a in sorted(weak, key=lambda x: x["key"]):
                print(f"  {a['verb']:6s} {a['path']:<62s} {a['desc']}")
        if args.diff:
            print("\n--- blocks to insert ---")
            for a in sorted(missing, key=lambda x: (x["file"].name, x["line"])):
                print(f"{a['file'].name}:{a['line'] + 1}  [{a['source']}]  {a['verb']} {a['path']}")
                print(f"    /// {a['desc']}")
        return 0

    if args.check:
        if missing:
            print(f"{len(missing)} actions have no XML summary; Swagger will show them undescribed:")
            for a in sorted(missing, key=lambda x: x["key"])[:40]:
                print(f"  {a['verb']:6s} {a['path']}")
            return 1
        print(f"All {len(actions)} actions have an XML summary")
        return 0

    written, files = apply(actions)
    print(f"Wrote {written} summaries across {files} controllers")
    return 0


if __name__ == "__main__":
    sys.exit(main())
