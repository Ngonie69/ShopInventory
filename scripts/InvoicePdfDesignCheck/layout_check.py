"""Geometry checks over every rendered invoice PDF in a directory.

Usage: python layout_check.py <dir>. Exits 1 on any problem.

- no two text lines overlap (a leading or alignment change that collides rows shows here)
- all text inside the page's 44px/40px/36px insets
- every page carries the title and a PAGE n OF m on the title's baseline
- a page with item rows carries the table head above them
- the footer's hairline (#c9c9d2) is on the last page only, below every item row
"""
import os, sys, re, itertools
import fitz

PX = 0.75
folder = sys.argv[1]
problems = []
pages_seen = 0


def lines_of(page):
    out = []
    for block in page.get_text("dict")["blocks"]:
        for line in block.get("lines", []):
            text = "".join(span["text"] for span in line["spans"]).strip()
            if text:
                out.append((text, fitz.Rect(line["bbox"]), line["spans"][0]["origin"][1]))
    return out


for name in sorted(os.listdir(folder)):
    if not name.endswith(".pdf"):
        continue
    doc = fitz.open(os.path.join(folder, name))
    for number, page in enumerate(doc, start=1):
        pages_seen += 1
        where = f"{name} p{number}"
        lines = lines_of(page)
        W, H = page.rect.width, page.rect.height

        for (ta, ra, _), (tb, rb, _) in itertools.combinations(lines, 2):
            overlap = ra & rb
            # Glyph boxes of adjacent lines touch by a hair; a real collision is more than 1pt deep.
            if not overlap.is_empty and overlap.height > 1.0 and overlap.width > 1.0:
                problems.append(f"{where}: overlap {ta[:25]!r} / {tb[:25]!r} ({overlap.height:.1f}pt)")

        for text, rect, _ in lines:
            if rect.x0 < 44 * PX - 1 or rect.x1 > W - 44 * PX + 1 or rect.y0 < 40 * PX - 1 or rect.y1 > H - 36 * PX + 1:
                problems.append(f"{where}: outside insets {text[:30]!r} {rect}")

        title = [b for t, _, b in lines if t == "Fiscal Tax Invoice"]
        count = [b for t, _, b in lines if re.match(r"PAGE\s+\d+", t)]
        if len(title) != 1 or not count:
            problems.append(f"{where}: title band missing")
        elif abs(title[0] - count[0]) > 0.5:
            problems.append(f"{where}: page count baseline {count[0] - title[0]:+.2f}pt off the title's")

        items = [r for t, r, _ in lines if re.match(r"(ITM|SUP)\d{3}", t)]
        head = [r for t, r, _ in lines if t == "Item Code"]
        if items and (not head or head[0].y1 > min(r.y0 for r in items)):
            problems.append(f"{where}: item rows without a table head above them")

        hairlines = [d for d in page.get_drawings()
                     if (d.get("color") and all(abs(c - v) < 0.01 for c, v in zip(d["color"], (0xC9 / 255, 0xC9 / 255, 0xD2 / 255))))
                     or (d.get("fill") and all(abs(c - v) < 0.01 for c, v in zip(d["fill"], (0xC9 / 255, 0xC9 / 255, 0xD2 / 255))))]
        last = number == len(doc)
        if last and not hairlines:
            problems.append(f"{where}: last page has no footer hairline")
        if not last and hairlines:
            problems.append(f"{where}: footer hairline on a page before the last")
        if hairlines and items:
            rule_y = min(d["rect"].y0 for d in hairlines)
            lowest = max(r.y1 for r in items)
            if lowest + 24 * PX - 1 > rule_y:
                problems.append(f"{where}: last item row {rule_y - lowest:.1f}pt above the hairline (needs 18pt)")

print(f"{pages_seen} pages checked, {len(problems)} problems")
for p in problems[:60]:
    print(" ", p)
sys.exit(1 if problems else 0)
