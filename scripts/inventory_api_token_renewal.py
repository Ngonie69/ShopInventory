#!/usr/bin/env python3
"""Inventory which Web code renews the API access token before it calls the API.

ShopInventory.Web reaches the API through one HttpClient per Blazor circuit. The bearer token on it
is whatever CustomAuthStateProvider last attached: navigation re-attaches a valid one, nothing else
does. A class that sends on that client directly therefore works until the 60-minute access token
runs out and then gets a 401 on every call, while the user is still signed in. The pages that show
it are the ones that stay open and keep calling: a timer, a hub push, a long form.

ApiTokenAuthentication is the fix (see its doc comment). This lists, for every class and component
that sends to the API:

  * each send, and what stands in front of it:
      retry   inside ApiTokenAuthentication.SendAsync, or a wrapper built on it - a valid token is
              attached first, and a 401 renews the token and sends once more;
      apply   ApiTokenAuthentication.ApplyAsync / GetAccessTokenAsync runs first - an expired
              token is renewed, a token the API rejects early is not retried;
      none    sent on whatever the client already carries;
  * which client it sends on: the circuit's shared one, a typed client, or one it made from
    IHttpClientFactory (which carries no bearer at all unless the class attaches one);
  * what the API does with that send once the token has expired, read off the controller:
      401        the endpoint authenticates the bearer only, so the call is refused;
      key        the endpoint also accepts the X-API-Key the circuit's client always carries, so
                 the call is answered - as the Web's key, not as the user. Nothing looks wrong:
                 the user's own data comes back empty, role and permission checks are judged for
                 the key, and the audit trail names the key;
      anonymous  the endpoint asks for nobody, so the token does not matter and the send is not
                 counted as exposed;
      ?          the URL could not be matched to one controller action; counted as exposed;
  * which routed pages reach it, and how: from a timer tick, from a hub push, from a layout (so on
    every page), from a page with a form on it.

Reachability comes from the same model find_page_permission_gaps.py builds: page -> components ->
injected services and MediatR requests -> members. Blind spots, most common first:

  * "Left open" cannot be read from source. A timer, a push subscription and a form are the signals
    listed; a dashboard somebody leaves on a wall screen with none of them is not flagged.
  * A tick is followed from the Timer callback, from the member awaiting WaitForNextTickAsync, and
    from a member looping on Task.Delay. A JavaScript interval calling a [JSInvokable] is not seen.
  * A push is an event of an injected service whose type name says Hub or Notification.
  * Calls resolve by member name, so overloads are merged, and a service resolved outside
    constructor, @inject or [Inject] injection is invisible.

Deterministic, read-only, Python 3 stdlib only. Takes about a minute.

Usage:
    python scripts/inventory_api_token_renewal.py [repo_root] [out_dir] [--allow-unrenewed-ticks]
        repo_root  default: the repository this script lives in
        out_dir    default: <repo_root>/artifacts/api-token-renewal (gitignored)

Writes inventory.json and inventory.md and prints the summary. Exits 1 when the built-in controls
fail, or when a send a timer tick or a hub push reaches goes out on the circuit's client, to an
endpoint that asks who is calling, with no renewal in front of it (--allow-unrenewed-ticks reports
those without failing).
"""
import json
import os
import re
import sys
from collections import defaultdict

FLAGS = {a for a in sys.argv[1:] if a.startswith("--")}
sys.argv = [a for a in sys.argv if not a.startswith("--")]
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import find_page_permission_gaps as gaps  # noqa: E402

REPO = gaps.REPO
OUT = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else os.path.join(REPO, "artifacts", "api-token-renewal")

SEND_METHODS = sorted(set(gaps.VERB_METHODS) | {"SendAsync", "PostAsJsonWithClientAuditAsync"})
SEND_RE = re.compile(r"(?<![\w.])(?:this\.)?(_?\w+)\s*\??\.\s*(" + "|".join(SEND_METHODS) + r")\s*(<[^()]*?>)?\s*\(")
RETRY_MARK = re.compile(r"\bApiTokenAuthentication\s*\.\s*SendAsync\s*\(|\bRenewAfterUnauthorizedAsync\s*\(|\bRefreshAccessTokenAsync\s*\(")
APPLY_MARK = re.compile(r"\bApiTokenAuthentication\s*\.\s*ApplyAsync\s*\(|\bGetAccessTokenAsync\s*\(")
DIRECT_RETRY_CALL = re.compile(r"\bApiTokenAuthentication\s*\.\s*SendAsync\s*\(")
TIMER_NEW = re.compile(r"\bnew\s+(?:System\.Threading\.|System\.Timers\.)?Timer\s*\(")
PUSH_TYPE = re.compile(r"Hub|Notification")
# The provider itself, and the helper the fix is built from: their sends are the renewal.
NOT_CALLERS = {"CustomAuthStateProvider", "ApiTokenAuthentication"}
CIRCUIT = "circuit"


def body(mem):
    return mem.masked[mem.start:mem.end]


def exposed(site):
    """Sent on the circuit's client with nothing in front of it, to an endpoint that asks who is calling."""
    return site["renewal"] == "none" and site["client"] == CIRCUIT and site["expired"] != "anonymous"


def members_of(unit):
    return [m for mems in unit.members.values() for m in mems]


# --------------------------------------------------------------------------------------------
# Which client a send goes out on
# --------------------------------------------------------------------------------------------
def factory_name(masked, lits, paren):
    """The literal passed to CreateClient( at masked[paren], or '?' when it is not a literal."""
    k = paren + 1
    while k < len(masked) and masked[k] in " \t\r\n":
        k += 1
    for lit in lits:
        if lit.start == k:
            return gaps.lit_text(lit)
    return "?"


def created_clients(unit):
    """Members that hand back a client they made from the factory: member name -> client name."""
    made = {}
    for mem in members_of(unit):
        m = re.search(r"\bCreateClient\s*\(", body(mem))
        header = mem.masked[mem.start:mem.masked.find("{", mem.start, mem.end) if "{" in body(mem) else mem.end]
        if m and "HttpClient" in header:
            made[mem.name] = factory_name(mem.masked, mem.lits, mem.start + m.end() - 1)
    return made


def client_of(unit, mem, recv, typed_units):
    """What recv is in this member: 'circuit', 'typed', 'factory:<name>', 'parameter', or None."""
    text = body(mem)
    assigned = re.search(r"(?<![\w.])" + re.escape(recv) + r"\s*=\s*(?:await\s+)?(?:[\w.]+\.)?(\w+)\s*\(", text)
    if assigned:
        callee = assigned.group(1)
        if callee == "CreateClient":
            return "factory:" + factory_name(mem.masked, mem.lits, mem.start + assigned.end() - 1)
        made = created_clients(unit)
        if callee in made:
            return "factory:" + made[callee]
    for other in members_of(unit):
        m = re.search(r"(?<![\w.])" + re.escape(recv) + r"\s*=\s*(?:[\w.]+\.)?CreateClient\s*\(", body(other))
        if m:
            return "factory:" + factory_name(other.masked, other.lits, other.start + m.end() - 1)
    if re.search(r"\bHttpClient\??\s+" + re.escape(recv) + r"\b", text) and unit.injected.get(recv) != "HttpClient":
        return "parameter"
    if unit.injected.get(recv) == "HttpClient":
        return "typed" if unit.name in typed_units else CIRCUIT
    if re.search(r"\bHttpClient\??\s+" + re.escape(recv) + r"\b", text):
        return "parameter"
    return None


# --------------------------------------------------------------------------------------------
# What stands in front of a send
# --------------------------------------------------------------------------------------------
def renewal_members(unit):
    """(members whose own sends are retried, members whose arguments are, members that apply)."""
    retry = {m.name for m in members_of(unit) if RETRY_MARK.search(body(m))}
    apply_ = {m.name for m in members_of(unit) if APPLY_MARK.search(body(m))}
    # A helper that takes the send as a Func and hands it on to a wrapper is a wrapper to its own
    # callers: WriteAsync<T>(send, ...) => SendAuthenticatedAsync(send).
    callable_ = set(retry)
    grew = True
    while grew:
        grew = False
        for mem in members_of(unit):
            if mem.name in callable_:
                continue
            text = body(mem)
            brace = text.find("{")
            arrow = text.find("=>")
            cut = min(x for x in (brace, arrow, len(text)) if x >= 0)
            params = re.findall(r"\bFunc\s*<[^()]*HttpResponseMessage[^()]*>\s+(\w+)", text[:cut])
            spans = call_arg_spans(text, callable_)
            if any(re.search(r"\b" + re.escape(p) + r"\b", text[a:b]) for p in params for a, b in spans):
                callable_.add(mem.name)
                grew = True
    return retry, callable_, apply_ - retry


def call_arg_spans(text, names):
    """Spans of the argument lists of calls to any of names, and of ApiTokenAuthentication.SendAsync."""
    spans = []
    for m in DIRECT_RETRY_CALL.finditer(text):
        spans.append((m.end(), gaps.match_paren(text, m.end() - 1)))
    if names:
        for m in re.finditer(r"(?<![\w.])(?:this\.)?(" + "|".join(map(re.escape, sorted(names))) + r")\s*(<[^()]*?>)?\s*\(", text):
            spans.append((m.end(), gaps.match_paren(text, m.end() - 1)))
    return spans


def classify(unit, mem, pos, retry_names, wrapper_names, apply_names):
    """retry / apply / none for the send at offset pos of the member's body."""
    text = body(mem)
    if mem.name in retry_names:
        return "retry"
    if any(a <= pos < b for a, b in call_arg_spans(text, wrapper_names)):
        return "retry"
    before = text[:pos]
    if APPLY_MARK.search(before):
        return "apply"
    if apply_names and re.search(r"(?<![\w.])(?:this\.)?(" + "|".join(map(re.escape, sorted(apply_names))) + r")\s*(<[^()]*?>)?\s*\(", before):
        return "apply"
    return "none"


def sends_of(unit, typed_units, endpoints=None):
    """Every send in the unit: member, line, method, client, renewal, expired-token effect."""
    retry_names, wrapper_names, apply_names = renewal_members(unit)
    out, skipped = [], []
    for mem in members_of(unit):
        text = body(mem)
        for m in SEND_RE.finditer(text):
            recv, method = m.group(1), m.group(2)
            client = client_of(unit, mem, recv, typed_units)
            site = {"member": mem.name, "file": gaps.rel(mem.path), "line": gaps.line_of(mem.src, mem.start + m.start()),
                    "method": method, "receiver": recv}
            if client is None:
                # hubConnection.SendAsync and friends. Kept visible when the name looks like a client,
                # so a parser gap cannot quietly drop a real send.
                other_type = re.search(r"\b" + re.escape(recv) + r"\s*=\s*new\s+(\w+)", text)
                if re.search(r"http|client", recv, re.I) and not (other_type and other_type.group(1) != "HttpClient"):
                    skipped.append(site)
                continue
            site["client"] = client
            site["renewal"] = classify(unit, mem, m.start(), retry_names, wrapper_names, apply_names)
            site["expired"] = site_effect(site, mem, unit, endpoints) if endpoints else "?"
            site["_mem"] = mem
            out.append(site)
    return out, skipped


# --------------------------------------------------------------------------------------------
# What the API does with a send whose token has expired
# --------------------------------------------------------------------------------------------
class Endpoints:
    def __init__(self):
        self.perm_consts, _, _ = gaps.load_permissions()
        self.all = gaps.parse_controllers(self.perm_consts)
        for e in self.all:
            e["segs"] = e["route"].strip("/").split("/")

    def effect_of(self, e):
        gates = gaps.endpoint_role_gates(e, self.perm_consts)
        declared = gaps._GATE_MODEL.class_authorize.get((e["file"], e["controller"]), []) + e["authorize"]
        if gates["anonymous"] or (not declared and not e["attrs"]):
            return "anonymous"
        return "key" if gates["key_reachable"] else "401"

    def effects(self, url):
        status, matches = gaps.match_url(url, self.all)
        if status != "matched":
            return {"?"}
        return {self.effect_of(e) for e in matches}


def site_effect(site, mem, unit, endpoints):
    """The expired-token effect of the URL nearest the send; the class's URLs when the member has none."""
    urls = mem.urls
    if urls:
        nearest = min(abs(u["line"] - site["line"]) for u in urls)
        urls = [u for u in urls if abs(u["line"] - site["line"]) == nearest]
    else:
        urls = [u for m in members_of(unit) for u in m.urls]
    found = set()
    for u in urls:
        found |= endpoints.effects(u)
    if not found:
        return "?"
    return "/".join(sorted(found))


# --------------------------------------------------------------------------------------------
# Reachability
# --------------------------------------------------------------------------------------------
def reach(starts, units, impls, request_types):
    seen, queue = {}, []
    for m in starts:
        if id(m) not in seen:
            seen[id(m)] = m
            queue.append(m)
    i = 0
    while i < len(queue):
        m = queue[i]
        i += 1
        for call in sorted(m.calls):
            for t in gaps.resolve_targets(call, units, impls, request_types):
                if id(t) not in seen:
                    seen[id(t)] = t
                    queue.append(t)
    return seen


def pseudo(unit, calls, path="", src=""):
    m = gaps.Member(unit, "<entry>", 0, 0)
    m.path, m.src, m.urls, m.calls = path, src, [], set(calls)
    return m


def entry_calls(unit, text, known, request_types):
    calls = set(gaps._member_calls(unit, text, known, request_types))
    for ref in re.finditer(r"(?<![\w.])(\w+)\b", text):
        if ref.group(1) in unit.members:
            calls.add(("self", unit.name, ref.group(1), ""))
    return calls


def tick_entries(unit, known, request_types):
    """[(kind, where, start member)] for everything in the component that fires without a click."""
    found = []
    for (path, src, masked, _lits, _a, _b) in unit.files:
        for m in TIMER_NEW.finditer(masked):
            close = gaps.match_paren(masked, m.end() - 1)
            calls = entry_calls(unit, masked[m.end():close], known, request_types)
            found.append(("timer", f"{gaps.rel(path)}:{gaps.line_of(src, m.start())}", pseudo(unit, calls, path, src)))
        for m in re.finditer(r"(?<![\w.])(_?\w+)\s*\??\.\s*(\w+)\s*\+=\s*", masked):
            recv_type = unit.injected.get(m.group(1), "")
            is_elapsed = m.group(2) == "Elapsed"
            if not is_elapsed and not PUSH_TYPE.search(recv_type):
                continue
            end = masked.find(";", m.end())
            calls = entry_calls(unit, masked[m.end():end if end > 0 else len(masked)], known, request_types)
            kind = "timer" if is_elapsed else "push"
            found.append((kind, f"{gaps.rel(path)}:{gaps.line_of(src, m.start())}", pseudo(unit, calls, path, src)))
    for mem in members_of(unit):
        text = body(mem)
        where = f"{gaps.rel(mem.path)}:{gaps.line_of(mem.src, mem.start)}"
        if "WaitForNextTickAsync" in text:
            found.append(("timer", where, mem))
        elif re.search(r"\bwhile\s*\(", text) and re.search(r"\bTask\s*\.\s*Delay\s*\(", text):
            loop = re.search(r"\bwhile\s*\(", text)
            close = gaps.match_paren(text, text.find("{", loop.end())) if "{" in text[loop.end():] else len(text)
            if re.search(r"\bTask\s*\.\s*Delay\s*\(", text[loop.start():close]):
                found.append(("delay-loop", where, mem))
    return found


def closure(page, razors, units):
    """The page and every component it renders, by tag or by opening it as a dialog."""
    seen, stack = [page], [page]
    while stack:
        name = stack.pop()
        info = razors.get(name)
        if not info:
            continue
        nxt = [t[0] for t in info.tags]
        unit = units.get(name)
        for f in (unit.files if unit else []):
            nxt += [m.group(1) for m in re.finditer(r"\bShow(?:Async)?\s*<\s*(\w+)\s*>", f[2]) if m.group(1) in razors]
        for n in nxt:
            if n not in seen:
                seen.append(n)
                stack.append(n)
    return seen


def has_form(names, units, razors):
    binds = 0
    for n in names:
        info = razors.get(n)
        if not info:
            continue
        if "<EditForm" in info.src:
            return True
        binds += len(re.findall(r"@bind[-:\w]*\s*=", info.src))
    return binds >= 8


# --------------------------------------------------------------------------------------------
# Controls
# --------------------------------------------------------------------------------------------
CONTROL = """
public sealed class ControlService(HttpClient httpClient, IHttpClientFactory factory, CustomAuthStateProvider auth)
{
    public Task<HttpResponseMessage> Bare() => httpClient.GetAsync(ControlUrl);

    public async Task<HttpResponseMessage> Wrapped()
        => await SendAuthenticatedAsync(() => httpClient.PostAsJsonAsync(ControlUrl, new { }));

    public async Task<HttpResponseMessage> Applied()
    {
        await EnsureAuthenticationAsync();
        return await httpClient.DeleteAsync(ControlUrl);
    }

    public async Task<HttpResponseMessage> AppliedTooLate()
    {
        var response = await httpClient.GetAsync(ControlUrl);
        await EnsureAuthenticationAsync();
        return response;
    }

    public async Task<HttpResponseMessage> OwnClient()
    {
        var client = factory.CreateClient("ShopInventoryApiUser");
        return await client.GetAsync(ControlUrl);
    }

    public Task<HttpResponseMessage> Forwarded()
        => WriteAsync(() => httpClient.PutAsJsonAsync(ControlUrl, new { }), "fallback");

    public Task Hub(HubConnection hubConnection) => hubConnection.SendAsync(ControlUrl);

    private async Task<HttpResponseMessage> WriteAsync(Func<Task<HttpResponseMessage>> send, string fallback)
    {
        using var response = await SendAuthenticatedAsync(send);
        return response;
    }

    private Task EnsureAuthenticationAsync() => ApiTokenAuthentication.ApplyAsync(httpClient, auth, null);

    private Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> send)
        => ApiTokenAuthentication.SendAsync(httpClient, auth, null, send);
}
"""
CONTROL_EXPECTED = {
    "Bare": (CIRCUIT, "none"),
    "Wrapped": (CIRCUIT, "retry"),
    "Applied": (CIRCUIT, "apply"),
    "AppliedTooLate": (CIRCUIT, "none"),
    "OwnClient": ("factory:ShopInventoryApiUser", "none"),
    "Forwarded": (CIRCUIT, "retry"),
    "WriteAsync": None,              # hands its Func on, sends nothing itself
    "SendAuthenticatedAsync": None,  # sends nothing itself
    "Hub": None,                     # not an HttpClient
}


def run_controls(rows):
    failures = []
    masked, lits = gaps.lex_cs(CONTROL)
    gaps.NESTED_SINK.clear()
    unit = gaps.parse_cs_classes("<control>", CONTROL, masked, lits)[0]
    for m in members_of(unit):
        m.path = os.path.join(REPO, "control.cs")
    sites, _ = sends_of(unit, set())
    got = {s["member"]: (s["client"], s["renewal"]) for s in sites}
    for member, want in CONTROL_EXPECTED.items():
        if got.get(member) != want:
            failures.append(f"control snippet: {member} classified {got.get(member)}, expected {want}")
    # The service the pattern was first written for must read as fully wrapped in the real tree.
    doc = next((r for r in rows if r["name"] == "CustomerDocumentService"), None)
    if doc is None or doc["counts"]["retry"] == 0 or doc["counts"]["none"] or doc["counts"]["apply"]:
        failures.append(f"CustomerDocumentService should be all retry, got {doc and doc['counts']}")
    return failures


# --------------------------------------------------------------------------------------------
def main():
    web_roles = gaps.load_web_roles()
    units, razors, impls, request_types, _urls = gaps.build_model(web_roles)
    known = set(units)

    endpoints = Endpoints()
    typed_units = set()
    for path in gaps.walk_web_files():
        if path.endswith(".cs"):
            masked, _ = gaps.lex_cs(gaps.read(path))
            typed_units |= {m.group(1) for m in re.finditer(r"\bAddHttpClient\s*<\s*\w+\s*,\s*(\w+)\s*>", masked)}

    # 1. every send, by owning class or component
    rows, skipped, member_sites = [], [], defaultdict(list)
    for name in sorted(units):
        unit = units[name]
        if name in NOT_CALLERS or unit.kind == "interface":
            continue
        sites, skip = sends_of(unit, typed_units, endpoints)
        skipped += skip
        if not sites:
            continue
        for s in sites:
            member_sites[id(s.pop("_mem"))].append((name, s))
        counts = {k: sum(1 for s in sites if s["renewal"] == k) for k in ("retry", "apply", "none")}
        files = sorted({s["file"] for s in sites})
        rows.append({
            "name": name,
            "kind": "component" if name in razors else "class",
            "files": files,
            "clients": sorted({s["client"] for s in sites}),
            "counts": counts,
            "sites": sites,
            "pages": [], "ticks": [], "pushes": [], "layouts": [], "form_pages": [],
        })
    by_name = {r["name"]: r for r in rows}

    def owners(seen):
        hit = defaultdict(list)
        for mid in seen:
            for owner, site in member_sites.get(mid, []):
                hit[owner].append(site)
        return hit

    # 2. which pages reach each of them, and how
    layouts = sorted(n for n, info in razors.items() if info.inherits == "LayoutComponentBase")
    layout_parts = {l: closure(l, razors, units) for l in layouts}
    pages = sorted(n for n, info in razors.items() if info.routes)
    tick_rows = []
    for host in pages + layouts:
        info = razors[host]
        parts = closure(host, razors, units)
        starts = [entry for c in parts if c in units for entry, _roles in units[c].wholes]
        reached = owners(reach(starts, units, impls, request_types))
        label = info.routes[0] if info.routes else f"<{host}>"
        form = has_form(parts, units, razors) if info.routes else False
        for owner in reached:
            row = by_name[owner]
            (row["pages"] if info.routes else row["layouts"]).append(label)
            if form:
                row["form_pages"].append(label)
        for c in parts:
            if c not in units:
                continue
            if host in pages and any(c in layout_parts[l] for l in layouts):
                continue  # a layout's own parts are reported under the layout
            for kind, where, start in tick_entries(units[c], known, request_types):
                hit = owners(reach([start], units, impls, request_types))
                tick_rows.append({"host": label, "component": c, "kind": kind, "where": where,
                                  "reaches": {o: sorted({(s["renewal"], s["expired"]) for s in ss}) for o, ss in sorted(hit.items())}})
                for owner, ss in hit.items():
                    entry = {"host": label, "component": c, "kind": kind, "where": where,
                             "unrenewed": sorted({f"{s['file']}:{s['line']} ({s['expired']})" for s in ss if exposed(s)})}
                    by_name[owner]["pushes" if kind == "push" else "ticks"].append(entry)

    for r in rows:
        r["pages"] = sorted(set(r["pages"]))
        r["layouts"] = sorted(set(r["layouts"]))
        r["form_pages"] = sorted(set(r["form_pages"]))
        c = r["counts"]
        total = sum(c.values())
        r["status"] = ("retry" if c["retry"] == total else
                       "none" if c["none"] == total else
                       "apply" if c["apply"] == total else "mixed")
        open_sends = [s for s in r["sites"] if exposed(s)]
        r["exposed_sends"] = len(open_sends)
        r["expired"] = {k: sum(1 for s in open_sends if k in s["expired"].split("/")) for k in ("401", "key", "?")}
        unrenewed_tick = any(t["unrenewed"] for t in r["ticks"] + r["pushes"])
        r["tier"] = (0 if not open_sends else
                     1 if unrenewed_tick else
                     2 if r["layouts"] or r["form_pages"] else 3)

    failures = run_controls(rows)
    tier1 = [r for r in rows if r["tier"] == 1]

    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "inventory.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"services": rows, "ticks": tick_rows, "skipped_receivers": skipped,
                   "control_failures": failures}, f, indent=1, sort_keys=True)
    write_md(rows, tick_rows, skipped, failures)

    callers = len(rows)
    print(f"{callers} classes and components send to the API ({sum(sum(r['counts'].values()) for r in rows)} sends)")
    for status, text in (("retry", "renew and retry on every send"), ("apply", "renew first, no retry"),
                         ("mixed", "mixed"), ("none", "never renew")):
        print(f"  {sum(1 for r in rows if r['status'] == status):3d}  {text}")
    print(f"{sum(1 for r in rows if r['tier'])} have a send on the circuit's client, to an endpoint that asks who is calling, with nothing in front of it:")
    for tier, text in ((1, "reached by a timer tick or a hub push"), (2, "behind a layout or a page with a form"), (3, "other pages only")):
        names = [r["name"] for r in rows if r["tier"] == tier]
        print(f"  tier {tier} ({text}): {len(names)}")
        if tier == 1:
            for r in tier1:
                hosts = sorted({f"{t['host']} [{t['kind']}]" for t in r["ticks"] + r["pushes"] if t["unrenewed"]})
                e = r["expired"]
                print(f"      {r['name']}: {r['exposed_sends']} sends ({e['401']} refused, {e['key']} answered as the key, {e['?']} unmatched); " + ", ".join(hosts))
    print(f"wrote {gaps.rel(os.path.join(OUT, 'inventory.md'))}")
    for failure in failures:
        print("CONTROL FAILED: " + failure)
    if failures:
        return 1
    if tier1 and "--allow-unrenewed-ticks" not in FLAGS:
        print("FAIL: a timer tick or hub push reaches a send with no renewal in front of it")
        return 1
    return 0


def write_md(rows, tick_rows, skipped, failures):
    L = []
    w = L.append
    w("# API token renewal inventory")
    w("")
    w("Generated by `scripts/inventory_api_token_renewal.py`. Do not edit; re-run it.")
    w("")
    w("`retry` renews an expired token and retries a 401 once. `apply` renews an expired token but hands a")
    w("401 back. `none` sends on whatever the client already carries. An unrenewed send is exposed when it")
    w("goes out on the circuit's client to an endpoint that asks who is calling: once the token has expired")
    w("the API either refuses it (`401`) or answers it as the Web's API key instead of the user (`key`).")
    w("Tier 1: a timer tick or hub push reaches an exposed send. Tier 2: a layout or a page with a form")
    w("does. Tier 3: other pages only. Tier 0: nothing exposed.")
    w("")
    if failures:
        w("## CONTROL FAILURES")
        w("")
        L.extend(f"- {f}" for f in failures)
        w("")
    w("## Timers, loops and pushes")
    w("")
    w("| Host | Component | Kind | Declared at | Reaches (renewal / when expired) |")
    w("|---|---|---|---|---|")
    for t in sorted(tick_rows, key=lambda t: (t["host"], t["component"], t["where"])):
        reaches = "; ".join(f"{o}: " + ", ".join(f"{r}/{c}" for r, c in v) for o, v in t["reaches"].items()) or "no API call"
        w(f"| {t['host']} | {t['component']} | {t['kind']} | {t['where']} | {reaches} |")
    w("")
    w("## Every class and component that sends to the API")
    w("")
    w("| Tier | Name | Client | retry | apply | none | Exposed: 401 | Exposed: key | Exposed: ? | Pages | Tick / push hosts | Layouts | Form pages | File |")
    w("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
    for r in sorted(rows, key=lambda r: (r["tier"] or 9, r["name"])):
        c = r["counts"]
        hosts = sorted({f"{t['host']} ({t['kind']})" for t in r["ticks"] + r["pushes"]})
        e = r["expired"]
        w(f"| {r['tier']} | {r['name']} | {', '.join(r['clients'])} | {c['retry']} | {c['apply']} | {c['none']} | "
          f"{e['401']} | {e['key']} | {e['?']} | {len(r['pages'])} | {'; '.join(hosts) or '-'} | {', '.join(r['layouts']) or '-'} | {len(r['form_pages'])} | {r['files'][0]} |")
    w("")
    w("## Exposed sends a tick or push reaches")
    w("")
    for r in sorted(rows, key=lambda r: r["name"]):
        for t in r["ticks"] + r["pushes"]:
            if t["unrenewed"]:
                w(f"- **{r['name']}** from {t['host']} ({t['component']}, {t['kind']} at {t['where']}): " + ", ".join(t["unrenewed"]))
    w("")
    if skipped:
        w("## Sends on a receiver that could not be resolved to an HttpClient")
        w("")
        L.extend(f"- {s['file']}:{s['line']} `{s['receiver']}.{s['method']}`" for s in skipped)
        w("")
    with open(os.path.join(OUT, "inventory.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(L) + "\n")


if __name__ == "__main__":
    sys.exit(main())
