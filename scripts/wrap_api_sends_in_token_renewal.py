#!/usr/bin/env python3
"""Put ApiTokenAuthentication in front of every API send one Web service class makes.

The edit scripts/inventory_api_token_renewal.py asks for, applied the same way everywhere. For the
named class it:

  * injects ILocalStorageService and CustomAuthStateProvider (primary constructor, or fields plus a
    classic constructor), and adds `using Blazored.LocalStorage;`;
  * wraps each send that answers an HttpResponseMessage -
        client.PostAsJsonAsync(url, body, ct)
    becomes
        SendAuthenticatedAsync(() => client.PostAsJsonAsync(url, body, ct))
  * turns client.GetFromJsonAsync<T>(url[, ct]) into GetAuthenticatedJsonAsync<T>(url[, ct]), which
    deserializes or throws exactly as before;
  * appends the two private helpers, built on ApiTokenAuthentication.SendAsync.

It does not guess. Anything it cannot rewrite safely is left alone and listed for a person:
client.SendAsync(request), because a request message cannot be sent twice and the retry needs a
fresh one per attempt; GetFromJsonAsync with serializer options; the string, stream and byte reads.
The run exits 2 when it left something, 0 when the class is fully wrapped.

Run it once per class, then build, run that class's token renewal test, and re-run the inventory
before the next. Running it again on a wrapped class changes nothing.

Usage:
    python scripts/wrap_api_sends_in_token_renewal.py <file.cs> <ClassName> [--dry-run]
"""
import io
import os
import re
import sys

FLAGS = {a for a in sys.argv[1:] if a.startswith("--")}
ARGS = [a for a in sys.argv[1:] if not a.startswith("--")]
sys.argv = sys.argv[:1]
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import find_page_permission_gaps as gaps  # noqa: E402

RESPONSE_METHODS = ("GetAsync", "PostAsync", "PostAsJsonAsync", "PutAsync", "PutAsJsonAsync",
                    "PatchAsync", "PatchAsJsonAsync", "DeleteAsync", "PostAsJsonWithClientAuditAsync")
BY_HAND = ("SendAsync", "GetStringAsync", "GetStreamAsync", "GetByteArrayAsync", "DeleteFromJsonAsync")
TOKEN_ARG = re.compile(r"(?i)^(?:\w+\.)*(?:\w*token|ct|default|none)$")
WRAPPER = "SendAuthenticatedAsync"
JSON_READ = "GetAuthenticatedJsonAsync"


def fail(message):
    print("error: " + message)
    sys.exit(1)


class Edits:
    """Replacements against the original text, applied back to front so offsets stay true."""

    def __init__(self):
        self.items = []

    def add(self, start, end, text):
        self.items.append((start, end, text))

    def apply(self, src):
        for start, end, text in sorted(self.items, key=lambda e: (e[0], e[1]), reverse=True):
            src = src[:start] + text + src[end:]
        return src


def params_of(masked, src, open_paren):
    """[(type, name)] of the parameter list opening at open_paren, and its closing index."""
    close = gaps.match_paren(masked, open_paren)
    out = []
    for part in gaps.split_top_commas(src[open_paren + 1:close]):
        m = re.match(r"(?:\[[^\]]*\]\s*)*(.+?)\s+(\w+)\s*(?:=.*)?$", part.strip(), re.S)
        if m:
            out.append((m.group(1).strip(), m.group(2)))
    return out, close


def main():
    if len(ARGS) != 2:
        fail("usage: wrap_api_sends_in_token_renewal.py <file.cs> <ClassName> [--dry-run]")
    path, cls = ARGS
    with io.open(path, encoding="utf-8-sig", newline="") as f:
        src = f.read()
    with io.open(path, "rb") as f:
        bom = f.read(3) == b"\xef\xbb\xbf"
    nl = "\r\n" if "\r\n" in src else "\n"
    masked, _lits = gaps.lex_cs(src)

    decl = re.search(r"\bclass\s+" + re.escape(cls) + r"\b", masked)
    if not decl:
        fail(f"class {cls} not found in {path}")
    k = decl.end()
    primary = None
    while masked[k] != "{":
        if masked[k] == "(" and primary is None:
            primary = k
            k = gaps.match_paren(masked, k)
        k += 1
    body_open, body_close = k, gaps.match_paren(masked, k)
    body = masked[body_open:body_close]

    edits = Edits()
    already = "CustomAuthStateProvider" in masked[decl.start():body_close]

    # -- constructor and names --------------------------------------------------------------
    if primary is not None:
        params, close = params_of(masked, src, primary)
        style = "primary"
    else:
        ctor = re.search(r"\bpublic\s+" + re.escape(cls) + r"\s*\(", body)
        if not ctor:
            fail(f"{cls} has neither a primary constructor nor a public one")
        ctor_open = body_open + ctor.end() - 1
        params, close = params_of(masked, src, ctor_open)
        style = "classic"

    def named(type_pattern, where):
        return next((n for t, n in where if re.fullmatch(type_pattern, t)), None)

    if style == "primary":
        client = named(r"HttpClient", params)
        logger = named(r"ILogger<.*>", params)
        storage = named(r"ILocalStorageService", params) or "localStorage"
        provider = named(r"CustomAuthStateProvider", params) or "authStateProvider"
    else:
        fields = [(m.group(1), m.group(2)) for m in re.finditer(
            r"\bprivate\s+readonly\s+([\w<>.]+)\s+(_\w+)\s*;", src[body_open:body_close])]
        client = named(r"HttpClient", fields)
        logger = named(r"ILogger<.*>", fields)
        storage = named(r"ILocalStorageService", fields) or "_localStorage"
        provider = named(r"CustomAuthStateProvider", fields) or "_authStateProvider"
    if not client:
        fail(f"{cls} does not hold an HttpClient")

    # -- sends ------------------------------------------------------------------------------
    wrapped_spans = []
    for m in re.finditer(r"(?<![\w.])(?:ApiTokenAuthentication\s*\.\s*SendAsync|" + WRAPPER + r")\s*\(", body):
        a = body_open + m.end()
        wrapped_spans.append((a, gaps.match_paren(masked, a - 1)))
    helper_spans = []
    for m in re.finditer(r"\b(?:" + WRAPPER + "|" + JSON_READ + r")\s*(?:<\s*\w+\s*>)?\s*\([^)]*\)\s*(?:=>|\{)", body):
        a = body_open + m.start()
        tail = masked.find("{", a, body_open + m.end() + 1)
        helper_spans.append((a, gaps.match_paren(masked, tail) if tail >= 0 else masked.find(";", a)))

    send = re.compile(r"(?<![\w.])(?:this\.)?" + re.escape(client) + r"\s*\.\s*(\w+)\s*(<[^()]*?>)?\s*\(")
    counts = {"wrapped": 0, "json": 0, "already": 0}
    left = []
    uses_json = JSON_READ in body
    for m in send.finditer(body):
        start = body_open + m.start()
        method = m.group(1)
        open_paren = body_open + m.end() - 1
        close_paren = gaps.match_paren(masked, open_paren)
        line = gaps.line_of(src, start)
        if any(a <= start < b for a, b in wrapped_spans + helper_spans):
            counts["already"] += 1
            continue
        if method == "GetFromJsonAsync":
            args = gaps.split_top_commas(masked[open_paren + 1:close_paren])
            if len(args) > 2 or (len(args) == 2 and not TOKEN_ARG.match(args[1])):
                left.append((line, method, "takes serializer options"))
                continue
            edits.add(start, body_open + m.start(1) + len(method), JSON_READ)
            counts["json"] += 1
            uses_json = True
        elif method in RESPONSE_METHODS:
            edits.add(start, start, WRAPPER + "(() => ")
            edits.add(close_paren + 1, close_paren + 1, ")")
            counts["wrapped"] += 1
        elif method in BY_HAND:
            why = "a request message cannot be sent twice" if method == "SendAsync" else "does not answer a response"
            left.append((line, method, why))

    # -- dependencies -----------------------------------------------------------------------
    if not already:
        last = close
        while src[last - 1] in " \t\r\n":
            last -= 1
        line_start = src.rfind("\n", 0, last) + 1
        indent = re.match(r"[ \t]*", src[line_start:]).group(0)
        if style == "primary":
            edits.add(last, last, f",{nl}{indent}ILocalStorageService localStorage,{nl}{indent}CustomAuthStateProvider authStateProvider")
        else:
            if src[line_start:last].lstrip().startswith("public"):
                edits.add(last, last, ", ILocalStorageService localStorage, CustomAuthStateProvider authStateProvider")
            else:
                edits.add(last, last, f",{nl}{indent}ILocalStorageService localStorage,{nl}{indent}CustomAuthStateProvider authStateProvider")
            field = re.search(r"^([ \t]*)private\s+readonly\s+HttpClient\s+" + re.escape(client) + r"\s*;[^\n]*\n",
                              src[body_open:body_close], re.M)
            at = body_open + field.end()
            pad = field.group(1)
            edits.add(at, at, f"{pad}private readonly ILocalStorageService {storage};{nl}"
                              f"{pad}private readonly CustomAuthStateProvider {provider};{nl}")
            ctor_body = masked.find("{", close)
            assign = re.search(r"^([ \t]*)" + re.escape(client) + r"\s*=\s*\w+\s*;[^\n]*\n",
                               src[ctor_body:gaps.match_paren(masked, ctor_body)], re.M)
            if not assign:
                fail(f"could not find where {cls}'s constructor assigns {client}")
            at = ctor_body + assign.end()
            pad = assign.group(1)
            edits.add(at, at, f"{pad}{storage} = localStorage;{nl}{pad}{provider} = authStateProvider;{nl}")

    # -- helpers ----------------------------------------------------------------------------
    helpers = []
    log_arg = f", {logger}" if logger else ""
    if WRAPPER not in body:
        helpers += [
            "",
            "    /// <summary>",
            "    /// As the signed-in user for as long as the session can be renewed: a valid access token is",
            "    /// attached first, and one the API rejects is renewed and the call sent once more.",
            "    /// </summary>",
            f"    private Task<HttpResponseMessage> {WRAPPER}(Func<Task<HttpResponseMessage>> sendAsync)",
            f"        => ApiTokenAuthentication.SendAsync({client}, {provider}, {storage}, sendAsync{log_arg});",
        ]
    if uses_json and not re.search(r"\b" + JSON_READ + r"\s*<\s*T\s*>\s*\(\s*string\b", body):
        helpers += [
            "",
            "    /// <summary>The body deserialized, or the failure thrown, as <c>GetFromJsonAsync</c> has it.</summary>",
            f"    private async Task<T?> {JSON_READ}<T>(string url, CancellationToken cancellationToken = default)",
            "    {",
            f"        using var response = await {WRAPPER}(",
            f"            () => {client}.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken));",
            "        response.EnsureSuccessStatusCode();",
            "        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);",
            "    }",
        ]
    if helpers:
        end = body_close
        while src[end - 1] in " \t":
            end -= 1
        # at the start of the class's closing brace line: a blank line, then each helper
        edits.add(end, end, nl.join(helpers) + nl)

    # -- using ------------------------------------------------------------------------------
    if not re.search(r"^using\s+Blazored\.LocalStorage\s*;", src, re.M):
        usings = list(re.finditer(r"^using\s+(?!static\b)([\w.]+)\s*;[ \t]*\r?\n", src, re.M))
        if not usings:
            fail("no using directives to add Blazored.LocalStorage beside")
        after = [u for u in usings if u.group(1).startswith("System") or u.group(1) < "Blazored.LocalStorage"]
        at = after[-1].end() if after else usings[0].start()
        edits.add(at, at, f"using Blazored.LocalStorage;{nl}")

    out = edits.apply(src)
    changed = out != src
    print(f"{cls} ({path}): {counts['wrapped']} sends wrapped, {counts['json']} JSON reads rewritten, "
          f"{counts['already']} already wrapped, {'dependencies injected' if not already else 'dependencies already there'}"
          f"{'' if changed else ' - nothing to change'}")
    for line, method, why in left:
        print(f"  LEFT FOR A PERSON  {path}:{line}  {client}.{method}  ({why})")
    if changed and "--dry-run" not in FLAGS:
        with io.open(path, "w", encoding="utf-8-sig" if bom else "utf-8", newline="") as f:
            f.write(out)
    return 2 if left else 0


if __name__ == "__main__":
    sys.exit(main())
