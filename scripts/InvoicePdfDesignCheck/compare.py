"""Compare where text lands in the design's PDF and in the invoice service's PDF.

Usage: python compare.py <design.pdf> <service.pdf>

Pairs each text run in the design with the same text in the service's PDF and prints how far its
left edge, right edge and baseline moved, in pt. A run is out of place when its baseline moved, or
when both of its edges did: fonts differ slightly in width, so one edge moving is only a wider word.

The footer departs from the design on purpose and is reported but not failed. The design's 1fr
bank column is too narrow for its own sample and wraps "Account Name:" and "Stanbic Bank Zimbabwe",
so the service narrows the totals block instead; and the design breaks the verification code at a
hyphen, which the service never does. Exits 1 when anything else is out of place.
"""
import re
import sys

import fitz

TOLERANCE_PT = 1.5

# Runs in the footer whose placement follows from the deliberate departures above.
DELIBERATE = re.compile(
    r"^(60A7-|2377$|Please deposit into:|Bank:|Stanbic|Zimbabwe|Account|Name:|Number:|Kefalos Cheese|Products|"
    r"9140005966435|Branch:|Belgravia|Currency:|USD$|Net Total|Discount|Freight|Total EXC VAT|VAT Total|Invoice Total)"
)


def runs(path):
    page = fitz.open(path)[0]
    out = []
    for block in page.get_text("dict")["blocks"]:
        for line in block.get("lines", []):
            for span in line["spans"]:
                text = re.sub(r"\s+", " ", span["text"].replace(" ", " ")).strip()
                if text:
                    x0, _, x1, _ = span["bbox"]
                    out.append(dict(text=text, x0=x0, x1=x1, base=span["origin"][1]))
    return out


def main(design_path, service_path):
    design, service = runs(design_path), runs(service_path)
    used = set()
    failures = 0

    print(f"{'text':40} {'dLeft':>7} {'dRight':>7} {'dBase':>7}")
    for run in design:
        match = next((i for i, other in enumerate(service) if i not in used and other["text"] == run["text"]), None)
        if match is None:
            # The two renderers split and wrap runs differently, so fall back to containment.
            match = next((i for i, other in enumerate(service)
                          if i not in used and (run["text"] in other["text"] or other["text"] in run["text"])), None)

        deliberate = bool(DELIBERATE.match(run["text"]))
        if match is None:
            note = "deliberate" if deliberate else "MISSING"
            failures += not deliberate
            print(f"{run['text'][:40]:40} {'':>23}  {note}")
            continue

        used.add(match)
        other = service[match]
        d_left, d_right, d_base = other["x0"] - run["x0"], other["x1"] - run["x1"], other["base"] - run["base"]
        off = abs(d_base) > TOLERANCE_PT or (abs(d_left) > TOLERANCE_PT and abs(d_right) > TOLERANCE_PT)
        note = ("deliberate" if deliberate else "OUT OF PLACE") if off else ""
        failures += off and not deliberate
        print(f"{run['text'][:40]:40} {d_left:7.2f} {d_right:7.2f} {d_base:7.2f}  {note}")

    print(f"\n{failures} run(s) out of place beyond {TOLERANCE_PT}pt")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))
