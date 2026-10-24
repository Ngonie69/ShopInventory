"""Turn one VanSalesReportService read from "null on any failure" into a ReportResult with a reason.

Each report read had the same shape:

    public async Task<T?> GetXAsync(params)
    {
        try
        {
            ...build url...
            return await httpClient.GetFromJsonAsync<T>(url);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "...");
            return null;
        }
    }

and becomes

    public async Task<ReportResult<T>> GetXAsync(params, CancellationToken cancellationToken = default)
    {
        ...build url...
        return await GetReportAsync<T>(url, "<what>", cancellationToken);
    }

with the interface member changed to match. GetReportAsync logs, reads the API's own refusal and
lets a cancellation through, so the try/catch has nothing left to do.

Usage: python -I scripts/convert_van_report_reads.py <repo root> <method name> "<what, e.g. performance report>" [service file]
The service file defaults to VanSalesReportService.cs; VanSalesAttendanceService.cs has the same shape.
The service needs a private GetReportAsync<T>(url, what, cancellationToken) delegating to ReportReader.
One method per run, so each page's commit converts the read it calls and the build stays green.
Idempotent: a converted method is reported and left alone.
"""
import re
import sys
from pathlib import Path

root, method, what = Path(sys.argv[1]), sys.argv[2], sys.argv[3]
service = sys.argv[4] if len(sys.argv) > 4 else "VanSalesReportService.cs"
path = root / "ShopInventory.Web/Services" / service
raw = path.read_bytes()
bom = raw.startswith(b"\xef\xbb\xbf")
text = raw.decode("utf-8-sig")
newline = "\r\n" if "\r\n" in text else "\n"
text = text.replace("\r\n", "\n")

if re.search(rf"Task<ReportResult<\w+>> {method}\(", text):
    print(f"{method}: already converted")
    sys.exit(0)

# The interface member.
interface = re.compile(rf"Task<(\w+)\?> {method}\(([^;]*?)\);")
match = interface.search(text)
if not match:
    sys.exit(f"{method}: interface member not found")
type_name = match.group(1)
params = match.group(2).rstrip()
text = text[:match.start()] + (
    f"Task<ReportResult<{type_name}>> {method}({params},"
    f"{chr(10) if chr(10) in params else ' '}"
    f"{'        ' if chr(10) in params else ''}CancellationToken cancellationToken = default);"
) + text[match.end():]

# The implementation.
impl = re.compile(
    rf"public async Task<{type_name}\?> {method}\((?P<params>[^)]*?)\)\n"
    r"    \{\n"
    r"        try\n"
    r"        \{\n"
    r"(?P<body>.*?)\n"
    r"        \}\n"
    r"        catch \(Exception ex\)\n"
    r"        \{\n"
    r"            logger\.LogError\([^;]*\);\n"
    r"            return null;\n"
    r"        \}\n"
    r"    \}",
    re.S)
match = impl.search(text)
if not match:
    sys.exit(f"{method}: implementation not in the expected shape")

params = match.group("params").rstrip()
multiline = "\n" in params
signature = (
    f"public async Task<ReportResult<{type_name}>> {method}({params},"
    + ("\n        " if multiline else " ")
    + "CancellationToken cancellationToken = default)"
)

body = "\n".join(line[4:] if line.startswith("    ") else line for line in match.group("body").split("\n"))
body, count = re.subn(
    rf"return await httpClient\.GetFromJsonAsync<{type_name}>\(\s*(.*?)\);",
    lambda m: f'return await GetReportAsync<{type_name}>(\n            {m.group(1).strip()},\n            "{what}",\n            cancellationToken);'
    if "\n" in m.group(0) else
    f'return await GetReportAsync<{type_name}>({m.group(1).strip()}, "{what}", cancellationToken);',
    body,
    count=1,
    flags=re.S)
if count != 1:
    sys.exit(f"{method}: no GetFromJsonAsync<{type_name}> call in the body")

text = text[:match.start()] + signature + "\n    {\n" + body + "\n    }" + text[match.end():]

path.write_bytes(("﻿" if bom else "").encode("utf-8") + text.replace("\n", newline).encode("utf-8"))
print(f"{method}: converted to ReportResult<{type_name}>")
