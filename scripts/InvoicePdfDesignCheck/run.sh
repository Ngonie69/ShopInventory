#!/usr/bin/env bash
# Checks the invoice PDF against the Fiscal Tax Invoice design.
#
#   bash scripts/InvoicePdfDesignCheck/run.sh [output-dir]
#
# 1. Prints design.html, the design as a static page, to PDF with headless Chrome.
# 2. Renders every InvoicePdfServiceTests fixture to PDF (INVOICE_PDF_OUT).
# 3. compare.py: where each text run lands on the design fixture, design against service.
# 4. layout_check.py: overlaps, insets, title band, table head and footer across every page.
#
# Needs Chrome and pymupdf (python -m pip install pymupdf). Run from the repo root.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
out="${1:-$here/out}"
chrome="${CHROME:-/c/Program Files/Google/Chrome/Application/chrome.exe}"
rm -rf "$out"
mkdir -p "$out/service"

to_windows() { cygpath -w "$1" 2>/dev/null || echo "$1"; }
to_url() { local path; path="$(cygpath -m "$1" 2>/dev/null || echo "$1")"; echo "file:///${path#/}"; }

"$chrome" --headless=new --disable-gpu --no-pdf-header-footer \
  --user-data-dir="$(to_windows "$out/chrome-profile")" \
  --print-to-pdf="$(to_windows "$out/design.pdf")" \
  "$(to_url "$here/design.html")" 2>/dev/null

status=0
# A failing test still writes its PDF, and the comparison is what shows where it went wrong.
INVOICE_PDF_OUT="$(to_windows "$out/service")" dotnet test ShopInventory.Tests \
  --filter "FullyQualifiedName~InvoicePdfServiceTests" > "$out/test.log" 2>&1 || status=1
grep -E "Passed!|Failed!" "$out/test.log"

python "$here/compare.py" "$out/design.pdf" "$out/service/design.pdf" || status=1
python "$here/layout_check.py" "$out/service" || status=1
exit $status
