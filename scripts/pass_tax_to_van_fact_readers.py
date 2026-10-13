"""Hand TaxSettings to every caller of the van sales fact readers.

VanSalesFactReader.LoadSalesAsync, LoadSaleLinesAsync and VanTruckDays.LoadAsync take a TaxSettings
straight after the context, because an online van sale is stored before tax and is grossed up as it is
read. This puts that argument into every call, gives each handler that makes one an
IOptions<TaxSettings>, and gives every test that builds one of those handlers a default one.

    python -I scripts/pass_tax_to_van_fact_readers.py           # rewrite
    python -I scripts/pass_tax_to_van_fact_readers.py --check   # change nothing; exit 1 if work remains

Idempotent: a call that already passes it, and a constructor that already takes it, are left alone.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
READERS = r"(?:VanSalesFactReader\.(?:LoadSalesAsync|LoadSaleLinesAsync)|VanTruckDays\.LoadAsync)"
# "Already done" is any tax argument, not only the one this writes: a test may pass a table of its own
# (a field called Tax, an options object called NoVat) and must not be given a second.
HAS_TAX = r"(?:\w*[Tt]ax\w*\b|NoVat\b|new TaxSettings\b|Options\.Create\()"
CALL = re.compile(r"(%s\(\s*)(db|_context|context)(\s*,)(?!\s*%s)" % (READERS, HAS_TAX))
HANDLER = re.compile(
    r"(public sealed class \w+Handler\(\s*ApplicationDbContext db)(?!,\s*IOptions<TaxSettings> tax)(\s*[,)])")
TAKES_TAX = re.compile(r"public sealed class (\w+Handler)\(\s*ApplicationDbContext db,\s*IOptions<TaxSettings> tax")
USINGS = ("using Microsoft.Extensions.Options;", "using ShopInventory.Configuration;")


def add_usings(text, wanted):
    lines = text.split("\n")
    block = [i for i, line in enumerate(lines) if line.startswith("using ") and line.rstrip().endswith(";")]
    if not block or not wanted:
        return text
    first, last = block[0], block[-1]
    if any(not lines[i].startswith("using ") for i in range(first, last + 1)):
        return text  # the usings are not one block; leave the file for a person
    merged = sorted(set(lines[first:last + 1]) | set(wanted),
                    key=lambda u: (not u.startswith("using System"), u.rstrip(";")))
    return "\n".join(lines[:first] + merged + lines[last + 1:])


def rewritten(text, tax_argument, construct):
    """The file as it should read. Equal to its input when there is nothing to do."""
    out = CALL.sub(lambda m: "%s%s, %s%s" % (m.group(1), m.group(2), tax_argument, m.group(3)), text)

    def handler(match):
        # The tail is the comma before the next parameter, or the closing bracket. It is kept whole.
        head, tail = match.group(1), match.group(2)
        own_line = "\n" in head or "\n" in tail
        return head + (",\n    " if own_line else ", ") + "IOptions<TaxSettings> tax" + tail

    if tax_argument == "tax.Value" and "tax.Value" in out:
        out = HANDLER.sub(handler, out)
    if construct:
        out = re.sub(
            r"(new (?:%s)\(\s*(?:_context|context|db))(?!,\s*%s)"
            % ("|".join(sorted(construct)), HAS_TAX),
            r"\1, Options.Create(new TaxSettings())", out)
    if out == text:
        return text
    wanted = [u for u in USINGS if u not in out]
    if "Options.Create(" not in out and "IOptions<TaxSettings>" not in out:
        wanted = [u for u in wanted if "Options" not in u]
    return add_usings(out, wanted)


def process(path, tax_argument, construct, write):
    raw = path.read_bytes()
    crlf = b"\r\n" in raw
    text = raw.decode("utf-8").replace("\r\n", "\n")
    out = rewritten(text, tax_argument, construct)
    if out != text and write:
        path.write_bytes((out.replace("\n", "\r\n") if crlf else out).encode("utf-8"))
    return out != text, out


def main():
    write = "--check" not in sys.argv
    changed, handlers = [], set()

    for path in sorted((ROOT / "ShopInventory" / "Features").rglob("*.cs")):
        if path.name == "VanSalesFactReader.cs" or not re.search(READERS, path.read_text(encoding="utf-8")):
            continue
        did, now = process(path, "tax" if path.name == "VanTruckDays.cs" else "tax.Value", None, write)
        handlers |= set(TAKES_TAX.findall(now))
        if did:
            changed.append(path)

    for project in ("ShopInventory.Tests", "ShopInventory.IntegrationTests"):
        for path in sorted((ROOT / project).rglob("*.cs")):
            if {"obj", "bin"} & set(path.parts):
                continue
            text = path.read_text(encoding="utf-8")
            if not re.search(READERS, text) and not any("new %s(" % name in text for name in handlers):
                continue
            did, _ = process(path, "new TaxSettings()", handlers, write)
            if did:
                changed.append(path)

    for path in changed:
        print(("changed " if write else "would change ") + str(path.relative_to(ROOT)))
    print("%d file(s) %s; handlers taking TaxSettings: %s"
          % (len(changed), "changed" if write else "to change", ", ".join(sorted(handlers)) or "none"))
    return 0 if write or not changed else 1


if __name__ == "__main__":
    sys.exit(main())
