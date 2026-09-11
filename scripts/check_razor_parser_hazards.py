"""Find Razor that the production runner's SDK cannot compile.

The deploy runner (KFL-DNS2) publishes with a 10.0.1xx .NET 10 SDK (10.0.103 on 2026-09-11) whose
Razor parser is older than the 10.0.3xx+ one on developer machines and in CI. Two constructs that the
newer SDKs accept failed the production publish there while building cleanly everywhere else. Both
reproduce locally under 10.0.100-rc.1, which fails with the runner's exact errors:

  A. A C# variable named `section` rendered as `@section.Something`. The old parser reads `@section`
     as the `@section` directive and fails with RZ2005/RZ1011.
  B. A switch expression inside `@code`/`@functions` whose first arm, on the line straight after the
     opening `{`, starts with `<` and is not `<=` - e.g. `{` then `< 1024 => ...`. The old parser opens
     a markup tag there, takes every later generic (`List<string>`) for a tag as well, and the page's
     generated code fails with CS0246 on `__builder` (RZ1006 "code block is missing a closing }").

Rule B is drawn from evidence, not from the parser's source. In this repository on 2026-09-11 one
switch failed under that SDK (`{` then `< 1024 =>`), and eleven compiled: five whose first arm is
`<= 0 =>`, and six whose `< n =>` arm follows an earlier arm (`> 0 =>`, `null =>`). If a construct
this does not describe breaks the runner again, extend the rule from that failure rather than widening
it by guesswork - a check that flags code which compiles gets ignored.

    python scripts/check_razor_parser_hazards.py                 report hazards, exit 1 if any
    python scripts/check_razor_parser_hazards.py --fix           rename hazard-A loop variables, then report
    python scripts/check_razor_parser_hazards.py --root <dir>    scan another checkout's ShopInventory.Web

--fix renames a `@foreach (var section in ...)` variable to `option` within the file, touching only C#
identifier uses (`section.X`, `var section`), never markup such as `<section>` or CSS class names.
Hazard B is reported, not rewritten: the right restructuring depends on the expression.

Checked against the commit that failed to deploy (8198062): rule A flags exactly the seven lines the
runner reported, and rule B flags the one switch that broke its page, and nothing else. Idempotent.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

NEW_NAME = "option"

# `@section.` or `@section(` in markup: the directive parse.
DIRECTIVE_USE = re.compile(r"@section[.(]")
# The loop declaration and plain C# uses of the identifier. The lookbehind excludes `<section`,
# `</section`, `vsg-section-count` and member access such as `x.section`.
LOOP_DECL = re.compile(r"\bvar\s+section\s+in\b")
IDENTIFIER_USE = re.compile(r"(?<![\w\-<\/.])section(?=\s*[.(])")

CODE_BLOCK_START = re.compile(r"^\s*@(code|functions)\s*\{?\s*$")
BLOCK_OPEN = re.compile(r"^\s*\{\s*$")
# `<` then neither `=` nor anything a tag name, closing tag, comment or text tag could start with.
LEADING_LT_ARM = re.compile(r"^\s*<(?![=A-Za-z/!?])")


def first_arm_leading_lt(text: str) -> list[int]:
    hits: list[int] = []
    in_code = False
    previous = ""
    for number, line in enumerate(text.splitlines(), start=1):
        if CODE_BLOCK_START.match(line):
            in_code = True
        elif in_code and LEADING_LT_ARM.match(line) and BLOCK_OPEN.match(previous):
            hits.append(number)
        if line.strip():
            previous = line
    return hits


def fix_section_variable(text: str) -> str:
    if not LOOP_DECL.search(text):
        return text
    return IDENTIFIER_USE.sub(NEW_NAME, LOOP_DECL.sub(f"var {NEW_NAME} in", text))


def write_preserving_format(path: Path, text: str) -> None:
    raw = path.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    newline = "\r\n" if b"\r\n" in raw else "\n"
    body = text.replace("\r\n", "\n").replace("\n", newline)
    path.write_bytes((b"\xef\xbb\xbf" if bom else b"") + body.encode("utf-8"))


def main(argv: list[str]) -> int:
    fix = "--fix" in argv
    root = Path(argv[argv.index("--root") + 1]) if "--root" in argv else Path(".")
    web = root / "ShopInventory.Web"
    if not web.is_dir():
        print(f"No ShopInventory.Web under {root.resolve()}")
        return 2

    problems = 0
    for path in sorted(web.rglob("*.razor")):
        if "obj" in path.parts or "bin" in path.parts:
            continue
        text = path.read_text(encoding="utf-8-sig")
        shown = path.relative_to(root)

        if fix and DIRECTIVE_USE.search(text):
            fixed = fix_section_variable(text)
            if fixed != text:
                write_preserving_format(path, fixed)
                text = fixed
                print(f"  fixed   A  {shown}  (loop variable 'section' -> '{NEW_NAME}')")

        for number, line in enumerate(text.splitlines(), start=1):
            if DIRECTIVE_USE.search(line):
                print(f"  hazard  A  {shown}:{number}  @section read as a directive: {line.strip()}")
                problems += 1

        lines = text.splitlines()
        for number in first_arm_leading_lt(text):
            print(f"  hazard  B  {shown}:{number}  switch's first arm starts with '<', read as a tag: {lines[number - 1].strip()}")
            problems += 1

    print("OK - no Razor parser hazards" if problems == 0 else f"FAILED - {problems} hazard(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
