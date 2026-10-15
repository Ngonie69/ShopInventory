"""Fail if a SAP client method builds SapRequestRejectedException without first ruling out an outage.

Why: SAP wraps "I could not reach my database" (500, code 7, "Failed to connect to kfldb port 40020
after 0 ms: Couldn't connect to server") in the same error envelope as a real refusal. Read as a
refusal, a write's outage was permanent -- every posting pass spent an attempt -- and "not
committed", so the document was free to be sent again without a lookup. Each site that turns a
response into SapRequestRejectedException must call ThrowIfSapBackendUnreachable before it.

    python scripts/Check-SapRejectionSites.py

Exit 0 when every site is covered, 1 with the uncovered sites listed.
"""

import glob
import re
import sys

FILES = sorted(glob.glob("ShopInventory/Services/SAPServiceLayerClient*.cs"))
METHOD = re.compile(r'^    (public|private|internal|protected)\b[^;=]*\(')
REJECTION = re.compile(r'new SapRequestRejectedException\(')
GUARD = "ThrowIfSapBackendUnreachable("


def main():
    covered, missing = 0, []
    for path in FILES:
        lines = open(path, encoding="utf-8").read().splitlines()
        for index, line in enumerate(lines):
            if not REJECTION.search(line):
                continue
            start = next((i for i in range(index, -1, -1) if METHOD.match(lines[i])), 0)
            name = lines[start].strip()
            if any(GUARD in body for body in lines[start:index]):
                covered += 1
                print(f"ok      {path}:{index + 1}  {name[:90]}")
            else:
                missing.append(f"{path}:{index + 1}  {name[:90]}")

    for site in missing:
        print(f"MISSING {site}")
    print(f"{covered} covered, {len(missing)} missing")
    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main())
