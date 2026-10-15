"""Turn the SAP client's bare `throw new Exception(...)` on a failed READ into SapReadFailedException.

Why: SapFailureClassifier could only judge a bare Exception by its wording, and SAP's "database down"
answer (500, "Couldn't connect to server") matched none of its words, so an outage spent every
posting attempt (till sale KEF-FAC-20260914-8DFB97306920, 14 Sep 2026). The typed exception carries
the HTTP status, which the classifier decides on instead.

What counts as a read: the nearest HTTP verb before the throw, inside the same method, is GET -- or
POST to a Service Layer `/List` endpoint (stored SQLQueries run as POST but write nothing). Writes are
left alone: a write's 5xx may have created the document, which is a different question.

Only a throw whose message interpolates `{response.StatusCode}` is touched, and the message is kept
character for character -- the exception centre and stored posting errors match on it.

    python scripts/Convert-SapReadFailures.py           # report only
    python scripts/Convert-SapReadFailures.py --apply   # rewrite the files

Idempotent: a converted site no longer matches.
"""

import glob
import re
import sys

FILES = sorted(glob.glob("ShopInventory/Services/SAPServiceLayerClient*.cs"))

THROW = re.compile(r'^(?P<indent>\s*)throw new Exception\((?P<msg>\$"[^"\n]*\{response\.StatusCode\}[^"\n]*")\);\s*$')
METHOD = re.compile(r'^    (public|private|internal|protected)\b[^;=]*\(')
VERB = re.compile(
    r'HttpMethod\.(?P<m1>Get|Post|Patch|Put|Delete)\b'
    r'|\.(?P<m2>Get|Post|Patch|Put|Delete)(AsJson)?Async\('
    r'|new HttpMethod\("(?P<m3>[A-Z]+)"\)')


def classify(lines, index):
    """The verb the throw reports on, and whether the request is to a /List endpoint."""
    for back in range(index - 1, -1, -1):
        line = lines[back]
        if METHOD.match(line):
            break
        verbs = list(VERB.finditer(line))
        if verbs:
            hit = verbs[-1]
            verb = (hit.group("m1") or hit.group("m2") or hit.group("m3")).upper()
            window = "".join(lines[max(0, back - 15):index])
            return verb, "/List" in window

    # Nothing before the throw: the request may be built by a local function declared after it
    # (SendDocumentReadAsync's BuildRequest). Accept only if every verb up to the next method agrees.
    found = set()
    for line in lines[index + 1:]:
        if METHOD.match(line):
            break
        for hit in VERB.finditer(line):
            found.add((hit.group("m1") or hit.group("m2") or hit.group("m3")).upper())
    return (found.pop(), False) if len(found) == 1 else (None, False)


def main(apply):
    converted = skipped = 0
    for path in FILES:
        with open(path, "rb") as handle:
            bom = handle.read(3) == b"\xef\xbb\xbf"
        with open(path, encoding="utf-8-sig", newline="") as handle:
            text = handle.read()
        lines = text.splitlines(keepends=True)
        changed = False

        for i, line in enumerate(lines):
            match = THROW.match(line.rstrip("\r\n"))
            if not match:
                continue
            verb, is_list = classify(lines, i)
            read = verb == "GET" or (verb == "POST" and is_list)
            label = f"{path}:{i + 1} {verb or '?'}{' /List' if is_list else ''}"
            if not read:
                print(f"skip    {label}  {match.group('msg')[:90]}")
                skipped += 1
                continue
            print(f"convert {label}  {match.group('msg')[:90]}")
            converted += 1
            ending = line[len(line.rstrip("\r\n")):]
            lines[i] = (f"{match.group('indent')}throw new SapReadFailedException("
                        f"response.StatusCode, {match.group('msg')});{ending}")
            changed = True

        if apply and changed:
            with open(path, "w", encoding="utf-8-sig" if bom else "utf-8", newline="") as handle:
                handle.write("".join(lines))

    print(f"\n{converted} read(s) {'converted' if apply else 'to convert'}, {skipped} left alone")


if __name__ == "__main__":
    main("--apply" in sys.argv)
