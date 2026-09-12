#!/usr/bin/env python3
"""Find Web pages that open for a role the API will refuse.

The Web gates pages by role ([Authorize(Roles = ...)] and UserRoles); the API gates endpoints by
permission ([RequirePermission(...)], resolved from Permissions.GetDefaultPermissionsForRole). The
two lists live in different projects and neither can see the other, so a role can open a page whose
calls the API answers with 403. While the Web's API key let every Web request past RequirePermission
none of those mismatches showed; they surface as soon as the API checks the signed-in user.

This joins them statically: page role gate -> components and injected services -> Web service
methods -> API URL strings -> controller routes -> RequirePermission -> role default permissions,
and lists every (role, page, endpoint, missing permission) the API would refuse. Per-user direct
grants are ignored; Admin is skipped because it holds every permission.

Blind spots, most common first:
  * <AuthorizeView Roles="..."> is modelled: a page method referenced only from inside such views is
    reached only by those roles, and so is everything it calls. Other role checks inside a page (an
    @if on a role flag, a redirect, a modal opened from a gated button) are not, so a row can still
    be unreachable from the UI. Verified exceptions go in ROLE_CONDITIONAL_RENDERS (a component
    rendered for some roles), ROLE_REDIRECTS (a role sent elsewhere) or ROLE_HIDDEN_CALLS (one call
    behind a role check). Redirects and hidden calls name source patterns that must all still match
    the page, or the run reports them stale, ignores them and exits 1.
  * Services resolved outside constructor, @inject or [Inject] injection are invisible.
  * Calls resolve by method name, so overloads are merged.

Deterministic, read-only, Python 3 stdlib only. Takes about a minute.

Usage:
    python scripts/find_page_permission_gaps.py [repo_root] [out_dir]
        repo_root  default: the repository this script lives in
        out_dir    default: <repo_root>/artifacts/permission-impact (gitignored)

Writes impact.json and impact.md. Exits 1 when the built-in control fails (SalesRep and Cashier
must not be refused quotations.view/create/edit from the quotation pages) or an exception is stale.
"""
import json
import os
import re
import sys
from collections import defaultdict

REPO = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else os.path.join(REPO, "artifacts", "permission-impact")

VERBS = ("GET", "POST", "PUT", "PATCH", "DELETE")


def rel(p):
    return os.path.relpath(p, REPO).replace("\\", "/")


def read(p):
    with open(p, encoding="utf-8-sig", errors="replace") as f:
        return f.read()


# --------------------------------------------------------------------------------------------
# C# lexer: masks comments and string/char literals (same length, newlines kept) and returns
# the literals with their spans. Interpolated strings keep their holes' expressions.
# --------------------------------------------------------------------------------------------
class Lit:
    __slots__ = ("start", "end", "parts")  # parts: list of ("t", text) | ("h", expr)

    def __init__(self, start, end, parts):
        self.start, self.end, self.parts = start, end, parts


def lex_cs(src):
    n = len(src)
    out = list(src)
    lits = []

    def blank(a, b, fill=" "):
        # literals are filled with \x01 (not whitespace, not a word char) so a regex's \s*
        # cannot run across a masked literal; comments become plain spaces.
        if src[a] != "/":
            fill = "\x01"
        for k in range(a, b):
            if out[k] != "\n":
                out[k] = fill

    i = 0
    while i < n:
        c = src[i]
        if c == "/" and i + 1 < n and src[i + 1] == "/":
            j = src.find("\n", i)
            j = n if j < 0 else j
            blank(i, j)
            i = j
            continue
        if c == "/" and i + 1 < n and src[i + 1] == "*":
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            blank(i, j)
            i = j
            continue
        if c == "'":
            # char literal
            j = i + 1
            if j < n and src[j] == "\\":
                j += 2
                while j < n and src[j] != "'":
                    j += 1
            else:
                j += 1
            if j < n and src[j] == "'":
                blank(i, j + 1)
                i = j + 1
                continue
            i += 1
            continue
        m = re.match(r'(\$+@?|@\$+|@)?("""+|")', src[i:i + 12])
        if m and (m.group(2) or "") and (i == 0 or not (src[i - 1].isalnum() or src[i - 1] == "_") or m.group(1)):
            prefix = m.group(1) or ""
            quotes = m.group(2)
            interp = "$" in prefix
            verbatim = "@" in prefix
            raw = len(quotes) >= 3
            j = i + len(prefix) + len(quotes)
            parts = []
            buf = []
            dollar_count = prefix.count("$")
            while j < n:
                ch = src[j]
                if raw:
                    if src.startswith(quotes, j):
                        j += len(quotes)
                        break
                elif ch == '"':
                    if verbatim and j + 1 < n and src[j + 1] == '"':
                        buf.append('"')
                        j += 2
                        continue
                    j += 1
                    break
                elif ch == "\\" and not verbatim:
                    buf.append(src[j:j + 2])
                    j += 2
                    continue
                elif ch == "\n" and not verbatim:
                    j += 1
                    break
                if interp and ch == "{":
                    need = dollar_count if raw else 1
                    if not raw and j + 1 < n and src[j + 1] == "{":
                        buf.append("{")
                        j += 2
                        continue
                    if raw and not src.startswith("{" * need, j):
                        buf.append(ch)
                        j += 1
                        continue
                    # hole
                    if buf:
                        parts.append(("t", "".join(buf)))
                        buf = []
                    j += need
                    depth = 1
                    hs = j
                    while j < n and depth:
                        cj = src[j]
                        if cj == '"' or (cj in "$@" and j + 1 < n and src[j + 1] == '"'):
                            # nested string inside hole: skip it
                            _, sub_end = _skip_string(src, j)
                            j = sub_end
                            continue
                        if cj == "{":
                            depth += 1
                        elif cj == "}":
                            depth -= 1
                            if depth == 0:
                                break
                        j += 1
                    parts.append(("h", src[hs:j]))
                    j += need
                    continue
                if interp and not raw and ch == "}" and j + 1 < n and src[j + 1] == "}":
                    buf.append("}")
                    j += 2
                    continue
                buf.append(ch)
                j += 1
            if buf:
                parts.append(("t", "".join(buf)))
            j = min(j, n)  # an unterminated literal (or a sliced sub-lex) can overrun the input
            lits.append(Lit(i, j, parts))
            blank(i, j)
            i = j
            continue
        i += 1
    return "".join(out), lits


def _skip_string(src, i):
    masked, lits = lex_cs(src[i:i + 4000])
    if lits and lits[0].start == 0:
        return lits[0], i + lits[0].end
    return None, i + 1


def lit_text(lit):
    return "".join(t if k == "t" else "{" + t + "}" for k, t in lit.parts)


def line_of(src, pos):
    return src.count("\n", 0, pos) + 1


def match_paren(masked, open_pos):
    """Given index of '(' / '{' / '[', return index of its matching closer."""
    pairs = {"(": ")", "{": "}", "[": "]"}
    o = masked[open_pos]
    cl = pairs[o]
    depth = 0
    for k in range(open_pos, len(masked)):
        ch = masked[k]
        if ch == o:
            depth += 1
        elif ch == cl:
            depth -= 1
            if depth == 0:
                return k
    return len(masked) - 1


def split_top_commas(s):
    parts, depth, cur = [], 0, []
    for ch in s:
        if ch in "([{<":
            depth += 1
        elif ch in ")]}>":
            depth -= 1
        if ch == "," and depth == 0:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    parts.append("".join(cur))
    return [p.strip() for p in parts]


# --------------------------------------------------------------------------------------------
# Route matching
# --------------------------------------------------------------------------------------------
CONSTRAINT_RE = {"int": r"-?\d+", "long": r"-?\d+", "guid": r"[0-9a-fA-F]{8}-?([0-9a-fA-F]{4}-?){3}[0-9a-fA-F]{12}",
                 "bool": r"(?i:true|false)", "decimal": r"-?\d+(\.\d+)?", "double": r"-?\d+(\.\d+)?"}


def url_segments(template):
    t = template.strip().split("?")[0].split("#")[0].lstrip("/")
    segs = t.split("/")
    while segs and segs[-1] == "":
        segs.pop()
    return segs


def is_generic_url(segs):
    # "api/{x}" with the controller segment itself a hole would match every controller
    return len(segs) < 2 or segs[1].replace(HOLE, "") == ""


def _seg_score(rseg, useg):
    """Per-segment specificity. A URL segment is literal, a pure hole, or partial
    (literal text plus a hole, e.g. "sales-summary{query}")."""
    pure_hole = useg.replace(HOLE, "") == ""
    partial = HOLE in useg and not pure_hole
    if rseg.startswith("{") and rseg.endswith("}"):
        inner = rseg[1:-1].lstrip("*").rstrip("?")
        cons = inner.split(":")[1:]
        literal_chars = useg.replace(HOLE, "")
        for c in cons:
            base = c.split("(")[0].lower()
            if base not in CONSTRAINT_RE:
                continue
            if not HOLE in useg and not re.fullmatch(CONSTRAINT_RE[base], useg):
                return None
            if partial and base in ("int", "long", "decimal", "double") and not re.fullmatch(r"[\d.-]*", literal_chars):
                return None
        if partial:
            return 1  # a partial literal is more likely the literal route it spells
        return 3 if cons else 2
    if HOLE in useg:
        pat = ".*".join(re.escape(p) for p in useg.lower().split(HOLE))
        if not re.fullmatch(pat, rseg.lower()):
            return None
        return 4 if partial else 1
    return 4 if useg.lower() == rseg.lower() else None


def match_route(rsegs, usegs):
    """Score tuple (higher = more specific) or None."""
    scores = []
    for i, r in enumerate(rsegs):
        if r.startswith("{*") or r.startswith("{**"):
            if len(usegs) < i:
                return None
            scores.append(0)
            return tuple(scores)
        if i >= len(usegs):
            if r.startswith("{") and r.endswith("?}"):
                scores.append(0)
                continue
            return None
        s = _seg_score(r, usegs[i])
        if s is None:
            return None
        scores.append(s)
    if len(usegs) > len(rsegs):
        return None
    return tuple(scores)


def match_url(url, endpoints):
    segs = url_segments(url["template"])
    if is_generic_url(segs):
        return "generic", []
    cands = []
    for e in endpoints:
        if url["verb"] != "ANY" and e["verb"] != url["verb"]:
            continue
        sc = match_route(e["segs"], segs)
        if sc is not None:
            cands.append((sc, e))
    if not cands:
        return "unmatched", []
    best = max(sc for sc, _ in cands)
    return "matched", [e for sc, e in cands if sc == best]


def display_template(t):
    return t.replace(HOLE, "{}")


# --------------------------------------------------------------------------------------------
# Permissions and roles (API models)
# --------------------------------------------------------------------------------------------
def parse_const_strings(path):
    """const string X = "..." / = Other.Y  -> dict (raw, unresolved refs kept as ('ref', name))."""
    src = read(path)
    masked, lits = lex_cs(src)
    by_start = {l.start: l for l in lits}
    consts = {}
    for m in re.finditer(r"\bconst\s+string\s+(\w+)\s*=\s*", masked):
        k = m.end()
        if k in by_start:
            consts[m.group(1)] = ("lit", lit_text(by_start[k]))
        else:
            rm = re.match(r"([\w.]+)\s*;", masked[k:])
            if rm:
                consts[m.group(1)] = ("ref", rm.group(1))
    return src, masked, lits, consts


def load_permissions():
    path = os.path.join(REPO, "ShopInventory", "Models", "Permission.cs")
    src, masked, lits, _ = parse_const_strings(path)
    # split into the two classes
    classes = {}
    for m in re.finditer(r"\bclass\s+(\w+)", masked):
        ob = masked.find("{", m.end())
        cb = match_paren(masked, ob)
        classes[m.group(1)] = (ob, cb)
    by_start = {l.start: l for l in lits}

    def consts_in(name):
        ob, cb = classes[name]
        d = {}
        seg = masked[ob:cb]
        for m in re.finditer(r"\bconst\s+string\s+(\w+)\s*=\s*", seg):
            k = ob + m.end()
            if k in by_start:
                d[m.group(1)] = ("lit", lit_text(by_start[k]))
            else:
                rm = re.match(r"([\w.]+)\s*;", masked[k:])
                d[m.group(1)] = ("ref", rm.group(1))
        return d

    plural = consts_in("Permissions")
    singular = consts_in("Permission")
    resolved = {}

    def res(cls, name, seen=()):
        table = plural if cls == "Permissions" else singular
        if name not in table:
            return None
        kind, v = table[name]
        if kind == "lit":
            return v
        if (cls, name) in seen:
            return None
        if "." in v:
            c2, n2 = v.split(".", 1)
            return res(c2, n2, seen + ((cls, name),))
        return res(cls, v, seen + ((cls, name),))

    for n in plural:
        resolved["Permissions." + n] = res("Permissions", n)
    for n in singular:
        resolved["Permission." + n] = res("Permission", n)

    # all permissions = codes listed in GetAllPermissionsGrouped (new(Code, ...))
    ob, cb = classes["Permissions"]
    gm = re.search(r"GetAllPermissionsGrouped\s*\(\s*\)", masked[ob:cb])
    g_open = masked.find("{", ob + gm.end())
    g_close = match_paren(masked, g_open)
    all_perms = []
    for m in re.finditer(r"\bnew\s*\(\s*(\w+)\s*,", masked[g_open:g_close]):
        v = res("Permissions", m.group(1))
        if v and v not in all_perms:
            all_perms.append(v)

    # role -> default permissions switch
    sm = re.search(r"GetDefaultPermissionsForRole\s*\(\s*string\s+role\s*\)", masked[ob:cb])
    body_open = masked.find("{", ob + sm.end())
    sw = masked.find("switch", body_open)
    sw_open = masked.find("{", sw)
    sw_close = match_paren(masked, sw_open)
    roles_default = {}
    k = sw_open + 1
    arm_re = re.compile(r"\s*(ApplicationRoles\.\w+|_)\s*=>\s*")
    while k < sw_close:
        am = arm_re.match(masked, k)
        if not am:
            k += 1
            continue
        key = am.group(1)
        k = am.end()
        if masked.startswith("GetAllPermissions()", k):
            roles_default[key] = list(all_perms)
            k += len("GetAllPermissions()")
        else:
            lm = re.match(r"new\s+List<string>\s*", masked[k:])
            lo = k + lm.end()
            lc = match_paren(masked, lo)
            names = re.findall(r"\b(\w+)\b", masked[lo + 1:lc])
            roles_default[key] = [res("Permissions", nm) for nm in names if res("Permissions", nm)]
            k = lc + 1
        cm = re.match(r"\s*,", masked[k:])
        if cm:
            k += cm.end()
    return resolved, all_perms, roles_default


def load_app_roles():
    path = os.path.join(REPO, "ShopInventory", "Models", "ApplicationRoles.cs")
    src, masked, lits, consts = parse_const_strings(path)
    roles = {k: v for k, (kind, v) in consts.items() if kind == "lit"}
    am = re.search(r"AssignableRoles\s*=\s*\[", masked)
    close = masked.find("]", am.end())
    assignable = [roles[n] for n in re.findall(r"\b(\w+)\b", masked[am.end():close]) if n in roles]
    return roles, assignable


def load_web_roles():
    path = os.path.join(REPO, "ShopInventory.Web", "Data", "UserRoles.cs")
    _, _, _, consts = parse_const_strings(path)
    return {k: v for k, (kind, v) in consts.items() if kind == "lit"}


# --------------------------------------------------------------------------------------------
# API controllers
# --------------------------------------------------------------------------------------------
HTTP_ATTR = re.compile(r"\bHttp(Get|Post|Put|Patch|Delete)\b")


def iter_attributes(masked, lits, start, end):
    """Yield (name, args_masked_start, args_end, pos) for [Attr(...)] groups between start and end."""
    k = start
    while k < end:
        ob = masked.find("[", k, end)
        if ob < 0:
            return
        cb = match_paren(masked, ob)
        inner_start = ob + 1
        # an attribute list can hold several attributes separated by commas
        seg = masked[inner_start:cb]
        pos = 0
        for piece in split_top_commas(seg):
            idx = seg.find(piece, pos)
            pos = idx + len(piece)
            am = re.match(r"\s*([\w.]+)\s*(\()?", piece)
            if not am:
                continue
            abs_piece = inner_start + idx
            if am.group(2):
                po = abs_piece + am.start(2)
                pc = match_paren(masked, po)
                yield am.group(1), po + 1, pc, abs_piece
            else:
                yield am.group(1), None, None, abs_piece
        k = cb + 1


def attr_arg_values(masked, lits, a, b):
    """Return list of args: ('lit', text) or ('expr', text) or ('named', name, value)."""
    if a is None:
        return []
    inlits = [l for l in lits if l.start >= a and l.end <= b]
    raw = masked[a:b]
    args = []
    pos = 0
    for piece in split_top_commas(raw):
        idx = raw.find(piece, pos) if piece else pos
        pos = idx + len(piece)
        s = a + idx
        e = s + len(piece)
        ls = [l for l in inlits if l.start >= s and l.end <= e]
        nm = re.match(r"\s*(\w+)\s*=\s*", piece)
        if nm and not piece.strip().startswith("=="):
            args.append(("named", nm.group(1), piece[nm.end():].strip(), ls))
        elif ls and piece.strip().replace("\x01", "") == "":
            args.append(("lit", lit_text(ls[0])))
        elif ls:
            args.append(("lit", "".join(lit_text(l) for l in ls)))
        else:
            args.append(("expr", piece.strip()))
    return args


def parse_controllers(perm_consts):
    ctrl_dir = os.path.join(REPO, "ShopInventory", "Controllers")
    endpoints = []
    for fn in sorted(os.listdir(ctrl_dir)):
        if not fn.endswith(".cs"):
            continue
        path = os.path.join(ctrl_dir, fn)
        src = read(path)
        masked, lits = lex_cs(src)
        for cm in re.finditer(r"\bclass\s+(\w+)", masked):
            cname = cm.group(1)
            if not cname.endswith("Controller"):
                continue
            # class attributes: from previous '}' / ';' / namespace line up to class keyword
            prev = max(masked.rfind(";", 0, cm.start()), masked.rfind("}", 0, cm.start()))
            prev_ob = masked.rfind("{", 0, cm.start())
            head_start = max(prev, prev_ob) + 1
            cls_route = []
            cls_perms = []
            for name, a, b, p in iter_attributes(masked, lits, head_start, cm.start()):
                base = name.split(".")[-1]
                args = attr_arg_values(masked, lits, a, b)
                if base in ("Route", "RouteAttribute"):
                    cls_route += [x[1] for x in args if x[0] == "lit"]
                elif base in ("RequirePermission", "RequirePermissionAttribute"):
                    cls_perms.append(_perm_attr(args, perm_consts, rel(path), line_of(src, p)))
            body_open = masked.find("{", cm.end())
            body_close = match_paren(masked, body_open)
            if not cls_route:
                cls_route = [""]
            ctrl_token = cname[: -len("Controller")]
            # walk members at depth 1 of the class body
            depth = 0
            seg_start = body_open + 1
            k = body_open + 1
            while k < body_close:
                ch = masked[k]
                if ch == "{" or ch == "(" or ch == "[":
                    if ch == "{" and depth == 0:
                        header = masked[seg_start:k]
                        close = match_paren(masked, k)
                        _handle_member(src, masked, lits, seg_start, k, path, cname, ctrl_token,
                                       cls_route, cls_perms, perm_consts, endpoints)
                        k = close + 1
                        seg_start = k
                        continue
                    depth += 1
                elif ch in ")]":
                    depth -= 1
                elif ch == "}":
                    depth -= 1
                elif ch == ";" and depth == 0:
                    # expression-bodied member ends here
                    if "=>" in masked[seg_start:k]:
                        _handle_member(src, masked, lits, seg_start, k, path, cname, ctrl_token,
                                       cls_route, cls_perms, perm_consts, endpoints)
                    seg_start = k + 1
                k += 1
    return endpoints


def _perm_attr(args, perm_consts, file, line):
    require_all = False
    perms = []
    unresolved = []
    for i, a in enumerate(args):
        if a[0] == "expr" and a[1] in ("true", "false") and i == 0:
            require_all = a[1] == "true"
            continue
        if a[0] == "lit":
            perms.append(a[1])
        elif a[0] == "expr":
            v = perm_consts.get(a[1])
            if v:
                perms.append(v)
            else:
                unresolved.append(a[1])
        elif a[0] == "named":
            unresolved.append(a[1] + "=" + a[2])
    return {"all": require_all, "perms": perms, "unresolved": unresolved, "at": f"{file}:{line}"}


def _handle_member(src, masked, lits, hs, he, path, cname, ctrl_token, cls_route, cls_perms, perm_consts, endpoints):
    header = masked[hs:he]
    verbs = []
    templates = []
    method_routes = []
    perms = []
    authorize = []
    name_m = None
    last_attr_end = hs
    for name, a, b, p in iter_attributes(masked, lits, hs, he):
        # only attributes before the signature
        sig_m = re.search(r"\b(public|private|protected|internal)\b", masked[hs:he])
        if sig_m and p > hs + sig_m.start():
            break
        base = name.split(".")[-1]
        args = attr_arg_values(masked, lits, a, b)
        hm = re.fullmatch(r"Http(Get|Post|Put|Patch|Delete)(Attribute)?", base)
        if hm:
            t = next((x[1] for x in args if x[0] == "lit"), None)
            verbs.append((hm.group(1).upper(), t))
        elif base in ("Route", "RouteAttribute"):
            method_routes += [x[1] for x in args if x[0] == "lit"]
        elif base in ("AcceptVerbs",):
            for x in args:
                if x[0] == "lit":
                    verbs.append((x[1].upper(), None))
        elif base in ("Authorize", "AuthorizeAttribute", "AllowAnonymous", "AllowAnonymousAttribute"):
            # kept raw: scripts/inventory_role_gates.py resolves roles and policies from these spans
            authorize.append({"name": name, "a": a, "b": b, "pos": p, "path": path})
        elif base in ("RequirePermission", "RequirePermissionAttribute"):
            perms.append(_perm_attr(args, perm_consts, rel(path), line_of(src, p)))
    if not verbs:
        return
    sig = re.search(r"\b(\w+)\s*(<[^()]*>)?\s*\(", re.sub(r"\[[^\]]*\]", lambda m: " " * len(m.group(0)), header))
    action = sig.group(1) if sig else "?"
    line = line_of(src, hs + len(header) - len(header.lstrip()))
    for verb, tmpl in verbs:
        tmpls = [tmpl] if tmpl is not None else (method_routes or [None])
        for t in tmpls:
            for cr in cls_route:
                full = join_route(cr, t, ctrl_token, action)
                endpoints.append({
                    "verb": verb,
                    "route": full,
                    "norm": normalise_route(full),
                    "controller": cname,
                    "action": action,
                    "file": rel(path),
                    "line": line_of(src, he),
                    "attrs": cls_perms + perms,
                    "authorize": authorize,
                })


def join_route(cls_route, tmpl, ctrl_token, action):
    def sub(s):
        return s.replace("[controller]", ctrl_token).replace("[action]", action)
    if tmpl is not None and (tmpl.startswith("~/") or tmpl.startswith("/")):
        return sub(tmpl.lstrip("~").strip("/"))
    parts = [sub(cls_route).strip("/")]
    if tmpl:
        parts.append(sub(tmpl).strip("/"))
    return "/".join(p for p in parts if p)


def normalise_route(route):
    return "/".join(re.sub(r"^\{.*\}$", "{}", s) if s.startswith("{") else s
                    for s in route.lower().strip("/").split("/"))


# --------------------------------------------------------------------------------------------
# Web code model
# --------------------------------------------------------------------------------------------
VERB_METHODS = {
    "GetAsync": "GET", "GetFromJsonAsync": "GET", "GetStringAsync": "GET", "GetStreamAsync": "GET",
    "GetByteArrayAsync": "GET", "PostAsJsonAsync": "POST", "PostAsync": "POST",
    "PutAsJsonAsync": "PUT", "PutAsync": "PUT", "PatchAsync": "PATCH", "PatchAsJsonAsync": "PATCH",
    "DeleteAsync": "DELETE", "DeleteFromJsonAsync": "DELETE",
}
VERB_CALL_RE = re.compile(r"\.(" + "|".join(VERB_METHODS) + r")\s*(<[^()]*?>)?\s*\(")
HTTPMETHOD_RE = re.compile(r"HttpMethod\.(Get|Post|Put|Patch|Delete)\b")
SKIP_DIRS = {"bin", "obj", "Migrations", "wwwroot", "node_modules", "logs"}


class Member:
    def __init__(self, cls, name, start, end):
        self.cls, self.name, self.start, self.end = cls, name, start, end
        self.urls = []      # dicts: template, verb, how, line
        self.calls = set()  # (kind, a, b)

    @property
    def key(self):
        return f"{self.cls.name}.{self.name}"


class CodeUnit:
    """A C# class (from .cs) or a razor component (razor + partial .razor.cs + base class)."""

    def __init__(self, name, kind):
        self.name, self.kind = name, kind
        self.files = []          # (path, src, masked, lits, region_start, region_end)
        self.bases = []          # base types / interfaces
        self.members = defaultdict(list)
        self.injected = {}       # var name -> type name
        self.consts = {}         # const/static string name -> literal text
        self.page_routes = []
        self.authorize = None    # list of attribute raw strings
        self.markup = ""         # razor markup (masked-ish) for component tag detection
        self.whole = None        # Member covering the whole unit (razor components)
        self.wholes = []         # [(entry pseudo-member, roles or None)]: ungated, then one per AuthorizeView role set


def walk_web_files():
    root = os.path.join(REPO, "ShopInventory.Web")
    for dp, dns, fns in os.walk(root):
        dns[:] = [d for d in dns if d not in SKIP_DIRS]
        for fn in fns:
            yield os.path.join(dp, fn)


TYPE_DECL = re.compile(r"\b(class|record|struct|interface)\s+(\w+)(\s*<[^>{]*>)?")


def parse_cs_classes(path, src, masked, lits, region=(0, None), into=None):
    """Find type declarations; for each, members at the class-body level."""
    units = []
    end_all = region[1] if region[1] is not None else len(masked)
    pos = region[0]
    for tm in TYPE_DECL.finditer(masked, region[0], end_all):
        if tm.start() < pos:
            continue
        name = tm.group(2)
        # header until '{' or ';'
        k = tm.end()
        depth = 0
        while k < end_all:
            ch = masked[k]
            if ch in "([":
                depth += 1
            elif ch in ")]":
                depth -= 1
            elif ch == "{" and depth == 0:
                break
            elif ch == ";" and depth == 0:
                break
            k += 1
        if k >= end_all or masked[k] == ";":
            continue
        header = masked[tm.end():k]
        body_open = k
        body_close = match_paren(masked, body_open)
        unit = into if into is not None else CodeUnit(name, tm.group(1))
        if into is None:
            units.append(unit)
        unit.files.append((path, src, masked, lits, body_open, body_close))
        # bases: after ':' in header, also primary constructor params
        hm = re.search(r"\)\s*:\s*(.*)$|^\s*:\s*(.*)$", header, re.S)
        pc = re.match(r"\s*\(", header)
        if pc:
            pco = tm.end() + pc.end() - 1
            pcc = match_paren(masked, pco)
            _collect_injections(unit, masked[pco + 1:pcc])
            after = masked[pcc + 1:k]
            bm = re.match(r"\s*:\s*(.*)$", after, re.S)
            base_txt = bm.group(1) if bm else ""
        else:
            bm = re.match(r"\s*(<[^>]*>)?\s*:\s*(.*)$", header, re.S)
            base_txt = bm.group(2) if bm else ""
        base_txt = re.sub(r"\bwhere\b.*$", "", base_txt, flags=re.S)
        for b in split_top_commas(base_txt):
            bn = re.match(r"\s*([\w.]+)", b)
            if bn:
                unit.bases.append((bn.group(1).split(".")[-1], b.strip()))
        _collect_members(unit, path, src, masked, lits, body_open, body_close)
        pos = body_close  # nested types are left to their parent (members cover them)
    return units


FIELD_DECL = re.compile(r"(?:\[Inject\][\s\x01]*)?(?:(?:private|protected|public|internal|readonly|static|required)\s+)*"
                        r"([A-Z]\w*(?:<[^<>;=()]*>)?)\??\s+(_?\w+)\s*(?=[;,)=]|\{\s*get|\s*$)")
NESTED_SINK = []


def _collect_injections(unit, text):
    for m in FIELD_DECL.finditer(text):
        t = re.sub(r"<.*", "", m.group(1))
        unit.injected.setdefault(m.group(2), t)


def _collect_members(unit, path, src, masked, lits, body_open, body_close):
    by_start = {l.start: l for l in lits}
    depth = 0
    seg = body_open + 1
    k = body_open + 1
    while k < body_close:
        ch = masked[k]
        if ch == "{" and depth == 0:
            header = masked[seg:k]
            close = match_paren(masked, k)
            if TYPE_DECL.search(header):
                # nested type: parse it separately but fold its members into this unit
                NESTED_SINK.extend(parse_cs_classes(path, src, masked, lits, (seg, close + 1), into=None))
                k = close + 1
                seg = k
                continue
            # a field/property initialiser with braces (= new() { ... }) continues to ';'
            if re.search(r"(?<![=!<>])=(?![=>])\s*[^;]*$", header) and "(" not in header.split("=")[0]:
                k = close + 1
                continue
            _add_member(unit, path, src, masked, lits, by_start, seg, k, close + 1)
            k = close + 1
            seg = k
            continue
        if ch in "([":
            depth += 1
        elif ch in ")]":
            depth -= 1
        elif ch == ";" and depth == 0:
            _add_member(unit, path, src, masked, lits, by_start, seg, None, k + 1)
            seg = k + 1
        k += 1


def _add_member(unit, path, src, masked, lits, by_start, seg, brace, end):
    text = masked[seg:end]
    header = masked[seg:brace] if brace is not None else text
    # field / property declarations (injection + const strings)
    _collect_injections(unit, re.sub(r"\[(?!Inject\])[^\]]*\]", " ", header))
    cm = re.search(r"\b(?:const|static\s+readonly|readonly\s+static)\s+string\s+(\w+)\s*=\s*", header)
    if cm:
        ls = seg + cm.end()
        if ls in by_start:
            unit.consts[cm.group(1)] = lit_text(by_start[ls])
    hdr_clean = re.sub(r"\[[^\]]*\]", lambda m: " " * len(m.group(0)), header)
    name = None
    # the member name is the identifier just before the first '(' (after dropping trailing
    # generic arguments), not the first identifier followed by '(': "Task<T?> Get<T>(" is Get.
    arrow = hdr_clean.find("=>")
    limit = arrow if arrow >= 0 else len(hdr_clean)
    modifiers = {"public", "private", "protected", "internal", "static", "async", "override", "virtual",
                 "sealed", "readonly", "unsafe", "extern", "new", "partial", "abstract"}
    not_names = {"if", "while", "for", "foreach", "switch", "using", "lock", "return", "nameof",
                 "typeof", "base", "this"}
    angle = 0
    k = 0
    while k < limit:
        ch = hdr_clean[k]
        if ch == "<":
            angle += 1
        elif ch == ">":
            angle -= 1
        elif ch == "(":
            if angle > 0:
                # a tuple inside a generic return type: Task<(bool Ok, string Msg)>
                k = match_paren(hdr_clean, k) + 1
                continue
            prefix = hdr_clean[:k].rstrip()
            while prefix.endswith(">"):
                depth = 0
                for j in range(len(prefix) - 1, -1, -1):
                    if prefix[j] == ">":
                        depth += 1
                    elif prefix[j] == "<":
                        depth -= 1
                        if depth == 0:
                            prefix = prefix[:j].rstrip()
                            break
                else:
                    break
            nm = re.search(r"(\w+)$", prefix)
            if not nm or nm.group(1) in modifiers:
                # a bare tuple return type: private (bool Ok, string Msg) Foo(...)
                k = match_paren(hdr_clean, k) + 1
                continue
            if nm.group(1) not in not_names:
                name = nm.group(1)
            break
        k += 1
    if name is None:
        pm = re.search(r"\b(\w+)\s*(=>|$)", hdr_clean.strip())
        if pm:
            name = pm.group(1)
    if not name:
        return
    mem = Member(unit, name, seg, end)
    mem.path, mem.src, mem.masked, mem.lits = path, src, masked, lits
    unit.members[name].append(mem)


# ---------------------------- URL extraction inside a span ----------------------------------
HOLE = "\x00"


def build_template(lit, unit):
    """Literal -> template with holes; const names in holes are substituted."""
    out = []
    for kind, t in lit.parts:
        if kind == "t":
            out.append(t)
        else:
            expr = t.split(":")[0].split(",")[0].strip()
            key = expr.split(".")[-1]
            if re.fullmatch(r"[\w.]+", expr) and key in unit.consts:
                out.append(unit.consts[key])
            else:
                out.append(HOLE)
    return "".join(out)


def is_api_template(t):
    s = t.lstrip()
    return s.startswith("api/") or s.startswith("/api/")


def extract_urls(unit, path, src, masked, lits, a, b):
    """URL occurrences (template, verb, how, line, pos) in masked[a:b]."""
    found = []
    inlits = [l for l in lits if l.start >= a and l.end <= b]
    occ = []
    for l in inlits:
        t = build_template(l, unit)
        if is_api_template(t):
            # string concatenation "api/x/" + expr  -> trailing hole
            nxt = re.match(r"[\s]*\+\s*([^\x01\s;),])", masked[l.end:l.end + 40])
            if nxt:
                t += HOLE
            occ.append((l.start, l.end, t))
    # bare const identifiers holding API paths (PostAsJsonAsync(BaseUrl, ...), string.Format(LedgerPath, ...))
    for cname, cval in unit.consts.items():
        if not is_api_template(cval):
            continue
        for m in re.finditer(r"(?<![\w.$])" + re.escape(cname) + r"\b", masked[a:b]):
            p = a + m.start()
            if re.match(r"\s*=\s*\x01", masked[p + len(cname):p + len(cname) + 10]):
                continue  # the declaration itself
            before = masked[max(0, p - 3):p]
            if "{" in before and masked[p + len(cname):p + len(cname) + 1] in "}:":
                continue  # already substituted through an interpolation hole
            t = re.sub(r"\{\d+[^}]*\}", HOLE, cval)
            occ.append((p, p + len(cname), t))
    for s, e, t in occ:
        verb, how = infer_verb(unit, masked, a, b, s, e)
        found.append({"template": t, "verb": verb, "how": how, "file": rel(path), "line": line_of(src, s)})
    return found


def _verbs_in(text):
    vs = set(VERB_METHODS[m.group(1)] for m in VERB_CALL_RE.finditer(text))
    vs |= set(m.group(1).upper() for m in HTTPMETHOD_RE.finditer(text))
    return vs


def infer_verb(unit, masked, a, b, s, e):
    before = masked[max(a, s - 160):s]
    m = re.search(r"\.(" + "|".join(VERB_METHODS) + r")\s*(<[^()]*?>)?\s*\(\s*$", before)
    if m:
        return VERB_METHODS[m.group(1)], "direct-arg"
    m = re.search(r"HttpMethod\.(Get|Post|Put|Patch|Delete)\s*,\s*$", before)
    if m:
        return m.group(1).upper(), "httpmethod-arg"
    m = re.search(r"new\s+HttpMethod\(\s*\x01+\s*\)\s*,\s*$", before)
    # variable assignment:  var url = "..."; / url = ...; / string url = ...
    m = re.search(r"(?:\b(?:var|string)\??\s+)?\b(\w+)\s*(\+?=)\s*$", before)
    if m and m.group(1) not in ("return",):
        v = m.group(1)
        span = masked[a:b]
        vs = set()
        for vm in re.finditer(r"\.(" + "|".join(VERB_METHODS) + r")\s*(<[^()]*?>)?\s*\(\s*" + re.escape(v) + r"\b", span):
            vs.add(VERB_METHODS[vm.group(1)])
        for vm in re.finditer(r"HttpMethod\.(Get|Post|Put|Patch|Delete)\s*,\s*" + re.escape(v) + r"\b", span):
            vs.add(vm.group(1).upper())
        # variable passed to an own helper: Helper(v ...) -> helper's single verb
        for hm in re.finditer(r"(?<![.\w])(\w+)\s*(<[^()]*?>)?\s*\(\s*" + re.escape(v) + r"\b", span):
            hv = _helper_verbs(unit, hm.group(1))
            if len(hv) == 1:
                vs |= hv
        if len(vs) == 1:
            return vs.pop(), "via-variable"
        if len(vs) > 1:
            return "ANY", "variable-multi-verb"
    # argument of an own helper
    m = re.search(r"(?<![.\w])(\w+)\s*(<[^()]*?>)?\s*\(\s*$", before)
    if m and m.group(1) not in VERB_METHODS:
        hv = _helper_verbs(unit, m.group(1))
        if len(hv) == 1:
            return next(iter(hv)), "via-helper"
    vs = _verbs_in(masked[a:b])
    if len(vs) == 1:
        return next(iter(vs)), "only-verb-in-member"
    return "ANY", "unknown"


def _call_hint(text, start):
    """Verb implied by the call site: .GetFromJsonAsync<T>(Builder(...)) / HttpMethod.Post, Builder(...)."""
    before = text[max(0, start - 160):start]
    m = re.search(r"\.(" + "|".join(VERB_METHODS) + r")\s*(<[^()]*?>)?\s*\(\s*(?:await\s+)?$", before)
    if m:
        return VERB_METHODS[m.group(1)]
    m = re.search(r"HttpMethod\.(Get|Post|Put|Patch|Delete)\s*,\s*$", before)
    if m:
        return m.group(1).upper()
    return ""


def _member_calls(unit, text, known_units, request_types):
    """Call edges: (kind, target type/unit, member, verb hint from the call site or '')."""
    calls = set()
    for m in re.finditer(r"(?<![\w.])(?:this\.)?(_?\w+)\s*\??\.\s*(\w+)\s*(<[^()]*?>)?\s*\(", text):
        recv, meth = m.group(1), m.group(2)
        if recv in unit.injected:
            calls.add(("inj", unit.injected[recv], meth, _call_hint(text, m.start())))
        elif recv in known_units and recv != unit.name:
            calls.add(("static", recv, meth, _call_hint(text, m.start())))
    for m in re.finditer(r"(?<![\w.])(\w+)\s*(<[^()]*?>)?\s*\(", text):
        if m.group(1) in unit.members:
            calls.add(("self", unit.name, m.group(1), _call_hint(text, m.start())))
    for m in re.finditer(r"\bnew\s+(\w+)\s*\(", text):
        if m.group(1) in request_types:
            calls.add(("mediator", m.group(1), "Handle", ""))
    return calls


def _helper_verbs(unit, name, seen=None):
    seen = seen or set()
    if name in seen:
        return set()
    seen.add(name)
    vs = set()
    for mem in unit.members.get(name, []):
        vs |= _verbs_in(mem.masked[mem.start:mem.end])
    return vs


# ---------------------------- Razor ---------------------------------------------------------
class RazorInfo:
    def __init__(self, name, path):
        self.name, self.path = name, path
        self.routes = []
        self.auth = []          # list of ("roles", [..]) | ("any",) | ("policy", p) | ("anon",)
        self.inherits = None
        self.layout = None
        self.tags = []          # (component name, line, restrict_roles or None)
        self.src = ""


def resolve_roles_expr(expr, web_roles):
    """Roles = "A,B" | UserRoles.X | UserRoles.A + "," + UserRoles.B | $"{UserRoles.A},{...}"."""
    pieces = []
    for m in re.finditer(r'UserRoles\.(\w+)|"([^"]*)"', expr):
        if m.group(1):
            pieces.append(web_roles.get(m.group(1), "?" + m.group(1)))
        else:
            txt = re.sub(r"\{UserRoles\.(\w+)\}", lambda mm: web_roles.get(mm.group(1), "?" + mm.group(1)), m.group(2))
            pieces.append(txt)
    joined = ",".join(pieces)
    return [r.strip() for r in joined.split(",") if r.strip()]


def parse_authorize_attrs(text, web_roles):
    out = []
    for m in re.finditer(r"\[\s*(Authorize|AllowAnonymous)\b(\s*\((.*?)\))?\s*\]", text):
        if m.group(1) == "AllowAnonymous":
            out.append(("anon",))
            continue
        args = m.group(3) or ""
        rm = re.search(r"Roles\s*=\s*(.+?)(?:,\s*Policy\s*=|$)", args)
        pm = re.search(r'Policy\s*=\s*("[^"]*"|[\w.]+)', args)
        if rm:
            out.append(("roles", resolve_roles_expr(rm.group(1), web_roles)))
        elif pm:
            out.append(("policy", pm.group(1)))
        else:
            out.append(("any",))
    return out


def parse_razor(path, web_roles):
    src = read(path)
    name = os.path.splitext(os.path.basename(path))[0]
    info = RazorInfo(name, path)
    info.src = src
    for m in re.finditer(r'^\s*@page\s+"([^"]*)"', src, re.M):
        info.routes.append(m.group(1))
    attr_lines = "\n".join(m.group(1) for m in re.finditer(r"^\s*@attribute\s+(\[.*\])\s*$", src, re.M))
    info.auth = parse_authorize_attrs(attr_lines, web_roles)
    im = re.search(r"^\s*@inherits\s+([\w.]+)", src, re.M)
    info.inherits = im.group(1).split(".")[-1] if im else None
    lm = re.search(r"^\s*@layout\s+([\w.]+)", src, re.M)
    info.layout = lm.group(1).split(".")[-1] if lm else None
    return info


def razor_code_regions(src, masked):
    regions = []
    for m in re.finditer(r"@(code|functions)\s*\{", masked):
        ob = m.end() - 1
        regions.append((ob, match_paren(masked, ob)))
    return regions


LIFECYCLE_MEMBERS = {
    "OnInitialized", "OnInitializedAsync", "OnParametersSet", "OnParametersSetAsync",
    "OnAfterRender", "OnAfterRenderAsync", "SetParametersAsync", "ShouldRender",
    "BuildRenderTree", "Dispose", "DisposeAsync",
}


def authorize_view_spans(src, web_roles):
    """(start, end, roles) for each <AuthorizeView Roles=...>. A span stops at the first closing tag
    and at any <NotAuthorized>, whose content renders for everyone else; both errors are on the
    side of treating markup as ungated."""
    spans = []
    for m in re.finditer(r'<AuthorizeView\b[^>]*\bRoles\s*=\s*"([^"]*)"[^>]*>', src):
        end = src.find("</AuthorizeView>", m.end())
        end = end if end > 0 else len(src)
        not_authorized = src.find("<NotAuthorized", m.end(), end)
        if not_authorized > 0:
            end = not_authorized
        roles_txt = m.group(1)
        if roles_txt.startswith("@"):
            roles = resolve_roles_expr(roles_txt, web_roles)
        else:
            roles = [r.strip() for r in roles_txt.split(",") if r.strip()]
        spans.append((m.end(), end, roles))
    return spans


def find_component_tags(info, component_names, web_roles):
    src = info.src
    # AuthorizeView Roles spans (restrict whatever renders inside them)
    spans = authorize_view_spans(src, web_roles)
    for m in re.finditer(r"(?<![\w.<>])<([A-Z]\w*)(?=[\s/>])", src):
        cname = m.group(1)
        if cname not in component_names or cname == info.name:
            continue
        restrict = None
        for a, b, roles in spans:
            if a <= m.start() < b:
                restrict = roles if restrict is None else [r for r in restrict if r in roles]
        info.tags.append((cname, line_of(src, m.start()), restrict))


# --------------------------------------------------------------------------------------------
# Model build
# --------------------------------------------------------------------------------------------
EXCLUDED_URL_FILES = {"ShopInventory.Web/Components/Pages/ApiExplorer.razor"}  # an endpoint catalogue, not calls


def merge_unit(units, u):
    if u.name not in units:
        units[u.name] = u
        return
    t = units[u.name]
    t.files += u.files
    t.bases += u.bases
    for k, v in u.members.items():
        t.members[k] += v
    for k, v in u.injected.items():
        t.injected.setdefault(k, v)
    t.consts.update(u.consts)
    for mems in t.members.values():
        for m in mems:
            m.cls = t


def build_model(web_roles):
    units = {}
    reg = defaultdict(set)
    razors = {}
    razor_cs_auth = defaultdict(list)
    for path in sorted(walk_web_files()):
        if path.endswith(".cs"):
            src = read(path)
            masked, lits = lex_cs(src)
            NESTED_SINK.clear()
            found = parse_cs_classes(path, src, masked, lits) + list(NESTED_SINK)
            for u in found:
                merge_unit(units, u)
            for m in re.finditer(r"\bAdd(?:Scoped|Transient|Singleton|HttpClient)\s*<\s*(\w+)\s*,\s*(\w+)\s*>", masked):
                reg[m.group(1)].add(m.group(2))
            if path.endswith(".razor.cs"):
                cm = re.search(r"\bpartial\s+class\s+(\w+)", masked)
                if cm:
                    head = masked[:cm.start()]
                    razor_cs_auth[cm.group(1)] += parse_authorize_attrs(head, web_roles)
        elif path.endswith(".razor"):
            info = parse_razor(path, web_roles)
            razors[info.name] = info
            src = info.src
            masked, lits = lex_cs(src)
            u = CodeUnit(info.name, "razor")
            u.files.append((path, src, masked, lits, 0, len(src)))
            for m in re.finditer(r"^\s*@inject\s+([\w.<>?]+)\s+(\w+)\s*$", src, re.M):
                u.injected[m.group(2)] = re.sub(r"<.*", "", m.group(1)).split(".")[-1]
            for ob, cb in razor_code_regions(src, masked):
                _collect_members(u, path, src, masked, lits, ob, cb)
            info.code_regions = razor_code_regions(src, masked)
            info.masked, info.lits = masked, lits
            merge_unit(units, u)
    for name, extra in razor_cs_auth.items():
        if name in razors:
            razors[name].auth += extra
    component_names = set(razors)
    for info in razors.values():
        find_component_tags(info, component_names, web_roles)

    request_types = defaultdict(list)
    for u in units.values():
        for bname, braw in u.bases:
            if bname == "IRequestHandler":
                rm = re.match(r"[\w.]*IRequestHandler\s*<\s*(\w+)", braw)
                if rm:
                    request_types[rm.group(1)].append(u.name)
    impls = defaultdict(set)
    for u in units.values():
        for bname, _ in u.bases:
            impls[bname].add(u.name)
    for k, vs in reg.items():
        impls[k] |= vs

    known = set(units)
    all_urls = []
    for u in units.values():
        for mems in u.members.values():
            for mem in mems:
                span = mem.masked[mem.start:mem.end]
                if rel(mem.path) in EXCLUDED_URL_FILES:
                    mem.urls = []
                else:
                    mem.urls = extract_urls(u, mem.path, mem.src, mem.masked, mem.lits, mem.start, mem.end)
                mem.calls = _member_calls(u, span, known, request_types)
                # A method group (OnClick = Save, EventCallback.Factory.Create(this, Save)) is a call too.
                # Not out of an enum, which the type scan collects as a member: its value names can
                # match a method's (OrderActionType.ConvertToInvoice) and are never calls.
                if not re.search(r"\benum\b", span.split("{", 1)[0]):
                    for ref in re.finditer(r"(?<![\w.])(\w+)\b(?!\s*(?:<[^()]*?>)?\s*\()", span):
                        if ref.group(1) in u.members and ref.group(1) != mem.name:
                            mem.calls.add(("self", u.name, ref.group(1), ""))
                all_urls += mem.urls
    # Razor entry points. A member is an entry restricted to an AuthorizeView's roles when every
    # markup reference to it sits inside such a view. Lifecycle methods, members referenced from
    # ungated markup or from code outside any member, and members never referenced at all stay
    # ungated entries, as before. A member referenced only from other members' bodies is reached
    # through those members' call edges, and so inherits whatever gates them.
    for name, info in razors.items():
        u = units[name]
        spans = authorize_view_spans(info.src, web_roles)
        code = [(a, b + 1) for a, b in razor_code_regions(info.src, info.masked)]
        member_spans = defaultdict(list)
        for mems in u.members.values():
            for mm in mems:
                member_spans[mm.path].append((mm.start, mm.end))
        # An enum value can share a page method's name (OrderActionType.ConvertToInvoice); a mention
        # inside an enum body is never a call, so it must not ungate the method.
        enum_spans = defaultdict(list)
        for (fpath, _fsrc, fmasked, _flits, _fa, _fb) in u.files:
            for em in re.finditer(r"\benum\s+\w+[^{;]*\{", fmasked):
                ob = em.end() - 1
                enum_spans[fpath].append((ob, match_paren(fmasked, ob) + 1))

        def restriction_at(pos, spans=spans):
            roles = None
            for a, b, rs in spans:
                if a <= pos < b:
                    roles = list(rs) if roles is None else [r for r in roles if r in rs]
            return roles

        def in_code(pos, code=code):
            return any(a <= pos < b for a, b in code)

        entry_roles = {}
        for mname, mems in u.members.items():
            if mname in LIFECYCLE_MEMBERS:
                entry_roles[mname] = None
                continue
            pattern = re.compile(r"(?<![\w.])" + re.escape(mname) + r"\b")
            own = [(mm.path, mm.start, mm.end) for mm in mems]
            markup_refs, body_ref, loose_ref = [], False, False
            for (fpath, fsrc, fmasked, _flits, _fa, _fb) in u.files:
                is_razor = fpath.endswith(".razor")
                for ref in pattern.finditer(fsrc if is_razor else fmasked):
                    p = ref.start()
                    if any(a <= p < b for a, b in enum_spans[fpath]):
                        continue
                    if is_razor and not in_code(p):
                        markup_refs.append(p)
                    elif any(op == fpath and os_ <= p < oe for op, os_, oe in own):
                        continue
                    elif any(a <= p < b for a, b in member_spans[fpath]):
                        body_ref = True
                    else:
                        loose_ref = True
            if loose_ref or (not markup_refs and not body_ref):
                entry_roles[mname] = None
            elif markup_refs:
                gates = [restriction_at(p) for p in markup_refs]
                entry_roles[mname] = None if any(g is None for g in gates) else set().union(*map(set, gates))

        def blank(text, ranges):
            # \x01, not a space: _member_calls' patterns put \s* on both sides of an optional
            # generic, and across a run of thousands of spaces they backtrack quadratically.
            chars = list(text)
            for a, b in ranges:
                for k in range(max(a, 0), min(b, len(chars))):
                    if chars[k] != "\n":
                        chars[k] = "\x01"
            return "".join(chars)

        ungated_markup = blank(info.src, code + [(a, b) for a, b, _ in spans])
        whole = Member(u, "<component>", 0, 0)
        whole.path, whole.src, whole.urls = info.path, info.src, []
        whole.calls = {c for c in _member_calls(u, ungated_markup, known, request_types) if c[0] != "self"}
        whole.calls |= {("self", u.name, mname, "") for mname, r in entry_roles.items() if r is None}
        if info.inherits:
            whole.calls.add(("allof", info.inherits, "", ""))
        u.whole = whole
        u.wholes = [(whole, None)]
        gated = defaultdict(set)
        for a, b, _ in spans:
            inside = blank(info.src, [(0, a), (b, len(info.src))] + code)
            key = tuple(sorted(restriction_at(a) or []))
            gated[key] |= {c for c in _member_calls(u, inside, known, request_types) if c[0] != "self"}
        for mname, r in entry_roles.items():
            if r is not None:
                gated[tuple(sorted(r))].add(("self", u.name, mname, ""))
        for key, calls in sorted(gated.items()):
            entry = Member(u, "<component>", 0, 0)
            entry.path, entry.src, entry.urls, entry.calls = info.path, info.src, [], calls
            u.wholes.append((entry, list(key)))

    # Self-check: every api/ literal the lexer sees in a C# region must belong to a parsed member,
    # or a parser gap is silently dropping calls. Razor markup literals are listed but flagged.
    claimed = {(u_["file"], u_["line"]) for u_ in all_urls}
    ORPHAN_URLS.clear()
    for u in units.values():
        for (path, src, masked, lits, a, b) in u.files:
            if rel(path) in EXCLUDED_URL_FILES:
                continue
            regions = [(a, b)]
            in_markup = None
            if path.endswith(".razor"):
                regions = razor_code_regions(src, masked)
                in_markup = regions
            for l in lits:
                t = lit_text(l)
                if not is_api_template(t):
                    continue
                site = (rel(path), line_of(src, l.start))
                if site in claimed:
                    continue
                inside_code = any(ra <= l.start <= rb for ra, rb in (in_markup or [(0, len(src))]))
                ORPHAN_URLS.append({"file": site[0], "line": site[1], "url": t,
                                    "where": "razor markup" if in_markup is not None and not inside_code else "code"})
    # a file can be merged into several units; keep one row per site
    seen_sites = set()
    ORPHAN_URLS[:] = [o for o in ORPHAN_URLS if not ((o["file"], o["line"]) in seen_sites or seen_sites.add((o["file"], o["line"])))]
    return units, razors, impls, request_types, all_urls


ORPHAN_URLS = []


def base_chain(units, name, seen=None):
    seen = seen or set()
    if name in seen or name not in units:
        return []
    seen.add(name)
    out = [units[name]]
    for bname, _ in units[name].bases:
        out += base_chain(units, bname, seen)
    return out


def resolve_targets(call, units, impls, request_types):
    kind, a, b = call[0], call[1], call[2]
    targets = []
    if kind == "inj" or kind == "static":
        names = set()
        if a in units and units[a].kind not in ("interface",):
            names.add(a)
        names |= impls.get(a, set())
        for n in sorted(names):
            for uu in base_chain(units, n):
                targets += uu.members.get(b, [])
    elif kind == "self":
        for uu in base_chain(units, a):
            targets += uu.members.get(b, [])
    elif kind == "mediator":
        for h in request_types.get(a, []):
            targets += units[h].members.get("Handle", [])
    elif kind == "allof":
        for uu in base_chain(units, a):
            for mems in uu.members.values():
                targets += mems
    return targets


def reachable_urls(start_members, units, impls, request_types):
    """BFS; returns list of (url, chain) with the shortest chain per member."""
    parent = {}
    hints = defaultdict(set)  # member id -> verb hints from the call sites that reach it
    queue = []
    for m in start_members:
        if id(m) not in parent:
            parent[id(m)] = (None, m)
            queue.append(m)
    i = 0
    while i < len(queue):
        m = queue[i]
        i += 1
        for call in sorted(m.calls):
            for t in resolve_targets(call, units, impls, request_types):
                if call[3]:
                    hints[id(t)].add(call[3])
                if id(t) not in parent:
                    parent[id(t)] = (m, t)
                    queue.append(t)
    out = []
    for m in queue:
        if not m.urls:
            continue
        chain = []
        cur = m
        while cur is not None:
            chain.append(cur.key if cur.name != "<component>" else cur.cls.name)
            cur = parent[id(cur)][0]
        chain.reverse()
        for url in m.urls:
            # a URL builder (string-returning member) takes its verb from the call site
            if url["verb"] == "ANY" and len(hints[id(m)]) == 1:
                url = dict(url, verb=next(iter(hints[id(m)])), how="via-caller-hint")
            out.append((url, chain))
    return out


def component_closure(page, razors, units):
    """[(component name, restrict roles or None, via chain)] including the page itself."""
    out = [(page, None, [page])]
    seen = {page: None}
    stack = [(page, None, [page])]
    while stack:
        name, restrict, via = stack.pop()
        info = razors.get(name)
        if not info:
            continue
        for cname, line, r in info.tags:
            nr = restrict
            if r is not None:
                nr = r if nr is None else [x for x in nr if x in r]
            key = (cname, tuple(nr) if nr is not None else None)
            if key in seen:
                continue
            seen[key] = True
            item = (cname, nr, via + [f"<{cname}> ({rel(info.path)}:{line})"])
            out.append(item)
            stack.append(item)
    return out


# Role-conditional renders the tag scan cannot see. Each entry is read from the page source and
# restricts which of the page's roles actually render the component.
ROLE_CONDITIONAL_RENDERS = {
    # Home.razor: @if (isSalesRep) <SalesRepDashboard/> else if (isDepotController) <DepotDashboard/>
    # else <AdminDashboard/>; any other role is redirected by RoleLandingRoutes before rendering.
    ("Home", "SalesRepDashboard"): ["SalesRep"],
    ("Home", "DepotDashboard"): ["DepotController"],
    ("Home", "AdminDashboard"): ["Admin"],
}


# Roles a page sends elsewhere before it calls the API. Keyed (page component, role); the value is
# the source patterns that make it true, every one of which must still match the page, and why.
ROLE_REDIRECTS = {
    ("UserManagement", "SalesRep"): (
        [r"isSalesRepView\s*=>\s*string\.Equals\(\s*currentUserRole\s*,\s*UserRoles\.SalesRep",
         r'if\s*\(\s*isSalesRepView\s*\)\s*\{\s*NavigationManager\.NavigateTo\(\s*"/merchandiser-account"'],
        "UserManagement.razor sends SalesRep to /merchandiser-account in OnAfterRenderAsync"),
}

# Calls a page makes only behind a role check the scan cannot read: an @if on a role flag, or a modal
# opened from a gated button. Keyed (page component, role, "VERB route" as the controller declares
# it); the value is the source patterns that must all still match the page (its .razor and its
# code-behind together), and why.
ROLE_HIDDEN_CALLS = {
    ("UserManagement", "PodOperator", "PUT api/UserManagement/{id:guid}/permissions"): (
        [r"isPodOperatorView\s*=>\s*string\.Equals\(\s*currentUserRole\s*,\s*UserRoles\.PodOperator",
         r"@if\s*\(\s*!isPodOperatorView\s*\)\s*\{\s*<button[^\n]*ShowPermissionsModal"],
        "UserManagement.razor shows Manage permissions only when !isPodOperatorView"),
    ("Invoices", "Cashier", "POST api/Invoice/{docEntry:int}/cancel"): (
        [r'<AuthorizeView Roles="Admin"[^>]*>\s*<button[^\n]*@onclick="OpenCancelInvoiceModal"',
         r"(?s)Task OpenCancelInvoiceModal\(\).{0,1200}?showCancelInvoiceModal\s*=\s*true"],
        "Invoices.razor opens the cancel modal only from an Admin-gated button"),
    ("SalesOrders", "SalesRep", "POST api/SalesOrder/{id}/convert-to-invoice"): (
        [r'(?s)<AuthorizeView Roles="Admin,Cashier"[^>]*>.{0,800}?OpenConvertDialog\(order\)',
         r"(?s)void OpenConvertDialog\(SalesOrderDto order\).{0,300}?convertOrder\s*=\s*order;",
         r"(?s)\A(?!(?:.*?\bOpenConvertDialog\b){3})"],
        "SalesOrders.razor opens the convert dialog only from an Admin,Cashier-gated button"),
    ("RouteCustomers", "Cashier", "DELETE api/route-customers/{id:int}"): (
        [r'(?s)<AuthorizeView Roles="Admin"[^>]*>.{0,800}?PromptDelete\(customer\)',
         r"(?s)void PromptDelete\(RouteCustomerModel customer\).{0,300}?customerPendingDelete\s*=\s*customer;",
         r"(?s)\A(?!(?:.*?\bPromptDelete\b){3})"],
        "RouteCustomers.razor opens the removal confirmation only from an Admin-gated button"),
    ("RouteCustomers", "Manager", "DELETE api/route-customers/{id:int}"): (
        [r'(?s)<AuthorizeView Roles="Admin"[^>]*>.{0,800}?PromptDelete\(customer\)',
         r"(?s)void PromptDelete\(RouteCustomerModel customer\).{0,300}?customerPendingDelete\s*=\s*customer;",
         r"(?s)\A(?!(?:.*?\bPromptDelete\b){3})"],
        "RouteCustomers.razor opens the removal confirmation only from an Admin-gated button"),
    ("CreditNoteApprovals", "WashBay", "POST api/credit-note-approvals/{code:int}/add"): (
        [r'(?s)<AuthorizeView Roles="@UserRoles\.CreditNoteAddRoles"[^>]*>.{0,600}?@onclick="OpenAddConfirm"',
         r"private void OpenAddConfirm\(\)\s*=>\s*showAddConfirm\s*=\s*true;",
         r"(?s)\A(?!(?:.*?\bOpenAddConfirm\b){3})",
         r"(?s)\A(?!(?:.*?\bshowAddConfirm\s*=\s*true){2})"],
        "CreditNoteApprovals opens the add confirmation only from the CreditNoteAddRoles-gated button"),
    ("MobileDrafts", "Merchandiser", "PUT api/SalesOrder/{id}"): (
        [r"canSaveOrderPrices\s*=\s*user\.IsInRole\(UserRoles\.Admin\)\s*\|\|\s*user\.IsInRole\(UserRoles\.Cashier\)\s*\|\|\s*user\.IsInRole\(UserRoles\.SalesRep\);",
         r"(?s)if\s*\(\s*!canSaveOrderPrices\s*\)\s*\{\s*ApplyLocalOrderPricing\(order,\s*updatedLines\);\s*return true;\s*\}.{0,3000}?SalesOrderService\.UpdateSalesOrderAsync\(order\.Id",
         r"(?s)\A(?!(?:.*?\bUpdateSalesOrderAsync\(){2})"],
        "MobileDrafts saves hydrated prices only for Admin, Cashier and SalesRep; a merchandiser's stay in memory"),
}
USED_HIDDEN_CALLS = set()
STALE_EXCEPTIONS = []

# Role gates — [Authorize(Roles = ...)] and the RequireRole policies — judged for the signed-in user, as
# ActingUserRoleAuthorizationHandler judges them once a user token rides with the Web's key. Filled by
# main(), written by write_role_outputs().
ROLE_FINDINGS = []
_GATE_MODEL = None


def endpoint_role_gates(e, perm_consts):
    """The endpoint's role gates from scripts/inventory_role_gates.py, computed once per endpoint."""
    global _GATE_MODEL
    if _GATE_MODEL is None:
        import inventory_role_gates as role_gates  # imported late: it imports this module
        _GATE_MODEL = role_gates.GateModel(perm_consts, role_gates.UniqueList())
    if "gates" not in e:
        e["gates"] = _GATE_MODEL.gates(e)
    return e["gates"]


def write_role_outputs():
    pairs = defaultdict(set)
    by_role = defaultdict(int)
    for f in ROLE_FINDINGS:
        pairs[(f["role"], f["verb"] + " " + f["api_route"], f["controller_action"], f["admitted"])].add(f["page_routes"][0])
        by_role[f["role"]] += 1
    counts = {
        "role_findings": len(ROLE_FINDINGS),
        "role_findings_low_confidence": sum(1 for f in ROLE_FINDINGS if f["low_confidence"]),
        "distinct_role_endpoint_pairs": len(pairs),
        "role_findings_by_role": dict(sorted(by_role.items())),
    }
    data = {"counts": counts, "findings": ROLE_FINDINGS,
            "pairs": [{"role": r, "endpoint": ep, "controller_action": ca, "admitted": adm, "pages": sorted(p)}
                      for (r, ep, ca, adm), p in sorted(pairs.items())]}
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "role-impact.json"), "w", encoding="utf-8") as fh:
        json.dump(data, fh, indent=2)

    L = ["# Role gate impact: Web pages refused once role gates judge the signed-in user\n",
         "Generated by `scripts/find_page_permission_gaps.py` (deterministic; re-run to reproduce). A row is a role "
         "that opens the page, reaches the endpoint through it, and holds none of the roles one of the endpoint's "
         "gates admits. Only endpoints the API key scheme authenticates are judged, since only those see the Web's "
         "key; Admin is never listed. The same blind spots as the permission report apply.\n",
         "## Counts\n"]
    L += [f"- {k}: {v}" for k, v in counts.items()]
    L.append("\n## Distinct (role, endpoint) pairs\n")
    L.append("| Role | Endpoint | Controller.action | The refusing gate admits | Pages |\n|---|---|---|---|---|")
    for (r, ep, ca, adm), p in sorted(pairs.items()):
        L.append(f"| {r} | `{ep}` | {ca} | {adm} | {', '.join(sorted(p))} |")
    L.append("\n## Findings\n")
    L.append("| Role | Page | Endpoint | Refusing gate | Via | URL site | Conf |\n|---|---|---|---|---|---|---|")
    for f in ROLE_FINDINGS:
        L.append(f"| {f['role']} | {', '.join(f['page_routes'])} (`{f['page_file']}`, {f['gate']}) | `{f['verb']} {f['api_route']}` | "
                 f"{f['refused_by']} | {' → '.join(f['via'])} | `{f['url_at']}` | {'low' if f['low_confidence'] else 'ok'} |")
    with open(os.path.join(OUT, "role-impact.md"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(L) + "\n")
    print("\n== Role gate impact ==")
    print(json.dumps(counts, indent=2))
    print(f"Wrote {os.path.join(OUT, 'role-impact.md')}")


# --------------------------------------------------------------------------------------------
# Join + output
# --------------------------------------------------------------------------------------------
def evaluate_attrs(attrs, perms):
    failures = []
    if "system.admin" in perms:
        return failures
    for a in attrs:
        have = [p for p in a["perms"] if p in perms]
        ok = (len(have) == len(a["perms"]) and not a["unresolved"]) if a["all"] else bool(have)
        if not ok:
            failures.append((a, [p for p in a["perms"] if p not in perms]))
    return failures


def attr_expr(a):
    joiner = " AND " if a["all"] else " OR "
    s = joiner.join(a["perms"]) or "?"
    if a["unresolved"]:
        s += " [unresolved: " + ", ".join(a["unresolved"]) + "]"
    return f"({s})"


def main():
    perm_consts, all_perms, role_defaults = load_permissions()
    app_roles, assignable = load_app_roles()
    web_roles = load_web_roles()
    role_key = {v: "ApplicationRoles." + k for k, v in app_roles.items()}

    def perms_for(role):
        return set(role_defaults.get(role_key.get(role, "_"), role_defaults["_"]))

    endpoints = parse_controllers(perm_consts)
    for e in endpoints:
        e["segs"] = e["route"].strip("/").split("/")
        e["key"] = f"{e['verb']} {e['norm']}"
    perm_endpoints = [e for e in endpoints if e["attrs"]]

    units, razors, impls, request_types, all_urls = build_model(web_roles)

    def component_source(page):
        """A page's .razor and its code-behind, for exception patterns."""
        unit = units.get(page)
        return "\n".join(f[1] for f in unit.files) if unit else razors[page].src

    # URL statistics over every URL occurrence in the Web
    url_stats = {"matched": 0, "unmatched": 0, "generic": 0}
    unmatched = []
    generic = []
    seen_occ = set()
    for url in all_urls:
        k = (url["file"], url["line"], url["template"])
        if k in seen_occ:
            continue
        seen_occ.add(k)
        status, hits = match_url(url, endpoints)
        url_stats[status] += 1
        rec = {"file": url["file"], "line": url["line"], "url": display_template(url["template"]),
               "verb": url["verb"], "how": url["how"]}
        if status == "unmatched":
            # would it match with any verb? (verb-inference problem vs missing route)
            st2, h2 = match_url(dict(url, verb="ANY"), endpoints)
            rec["matches_other_verb"] = sorted({h["key"] for h in h2}) if st2 == "matched" else []
            unmatched.append(rec)
        elif status == "generic":
            generic.append(rec)

    # pages
    pages = []
    for name, info in sorted(razors.items()):
        if info.routes:
            pages.append((name, info.routes, rel(info.path)))
    pages.append(("MainLayout", ["layout"], "ShopInventory.Web/Components/Layout/MainLayout.razor"))

    non_admin_assignable = [r for r in assignable if r != "Admin"]
    findings = []
    page_summaries = []
    endpoint_callers = defaultdict(lambda: {"pages": set(), "refused": set()})
    for name, routes, pfile in pages:
        info = razors[name]
        auth = list(info.auth)
        if info.inherits and info.inherits in units:
            for f in units[info.inherits].files:
                head_src = f[2][:max(0, f[4])]
                auth += parse_authorize_attrs(head_src, web_roles)
        if name in ("MainLayout", "NavMenu"):
            gate, roles = "layout (ANY)", list(non_admin_assignable)
        elif any(a[0] == "anon" for a in auth):
            gate, roles = "AllowAnonymous", []
        elif not auth:
            gate, roles = "no [Authorize] (treated as ANY signed-in role)", list(non_admin_assignable)
        else:
            role_lists = [a[1] for a in auth if a[0] == "roles"]
            if role_lists:
                roles = role_lists[0]
                for rl in role_lists[1:]:
                    roles = [r for r in roles if r in rl]
                gate = "Roles=" + ",".join(roles)
                roles = [r for r in roles if r != "Admin"]
            else:
                pol = [a[1] for a in auth if a[0] == "policy"]
                gate = ("Policy=" + ",".join(pol)) if pol else "[Authorize] (ANY)"
                roles = list(non_admin_assignable)
        redirected = []
        for (page_name, role), (patterns, why) in ROLE_REDIRECTS.items():
            if page_name != name:
                continue
            unmatched_patterns = [p for p in patterns if not re.search(p, component_source(name))]
            if unmatched_patterns:
                STALE_EXCEPTIONS.append({"page": name, "role": role, "why": why, "unmatched": unmatched_patterns})
                continue
            if role in roles:
                roles = [r for r in roles if r != role]
                redirected.append(role)
        if name == "MainLayout":
            comps = component_closure("MainLayout", razors, units)
            if "NavMenu" in razors and not any(c[0] == "NavMenu" for c in comps):
                comps += component_closure("NavMenu", razors, units)
        else:
            comps = component_closure(name, razors, units)
        reached = []
        for cname, restrict, via in comps:
            u = units.get(cname)
            if not u or not u.wholes:
                continue
            top = via[1].split(">")[0].lstrip("<") if len(via) > 1 else None
            override = ROLE_CONDITIONAL_RENDERS.get((name, top)) if top else None
            eff = restrict
            if override is not None:
                eff = override if eff is None else [r for r in eff if r in override]
            for entry, entry_roles in u.wholes:
                eff_entry = eff
                if entry_roles is not None:
                    eff_entry = list(entry_roles) if eff_entry is None else [r for r in eff_entry if r in entry_roles]
                for url, chain in reachable_urls([entry], units, impls, request_types):
                    reached.append((url, via[:-1] + chain if len(via) > 1 else chain, eff_entry))
        page_eps = set()
        for url, chain, eff in reached:
            status, hits = match_url(url, endpoints)
            if status != "matched":
                continue
            for e in hits:
                page_eps.add(e["key"])
                g = endpoint_role_gates(e, perm_consts)
                if g["gates"] and g["key_reachable"] and not g["anonymous"]:
                    for role in roles:
                        if eff is not None and role not in eff:
                            continue
                        refusing = [x for x in g["gates"] if role not in x["roles"]]
                        key = [role, pfile, e["verb"], e["route"], f"{url['file']}:{url['line']}"]
                        if refusing and not any(f["key"] == key for f in ROLE_FINDINGS):
                            ROLE_FINDINGS.append({
                                "key": key,
                                "role": role,
                                "page_routes": routes,
                                "page_file": pfile,
                                "gate": gate,
                                "via": chain,
                                "url_at": f"{url['file']}:{url['line']}",
                                "verb": e["verb"],
                                "api_route": e["route"],
                                "controller_action": f"{e['controller']}.{e['action']}",
                                "endpoint_at": f"{e['file']}:{e['line']}",
                                "admitted": "; ".join(", ".join(x["roles"]) for x in refusing),
                                "refused_by": "; ".join(x["from"] for x in refusing),
                                "low_confidence": url["verb"] == "ANY" or len(hits) > 1,
                            })
                if not e["attrs"]:
                    continue
                cal = endpoint_callers[(e["key"], e["controller"], e["action"])]
                cal["pages"].add(f"{routes[0]} ({pfile})")
                for role in roles:
                    if eff is not None and role not in eff:
                        continue
                    fails = evaluate_attrs(e["attrs"], perms_for(role))
                    if not fails:
                        continue
                    hidden_key = (name, role, f"{e['verb']} {e['route']}")
                    if hidden_key in ROLE_HIDDEN_CALLS:
                        patterns, why = ROLE_HIDDEN_CALLS[hidden_key]
                        unmatched_patterns = [p for p in patterns if not re.search(p, component_source(name))]
                        if not unmatched_patterns:
                            USED_HIDDEN_CALLS.add(hidden_key)
                            continue
                        stale = {"page": name, "role": role, "why": why, "unmatched": unmatched_patterns}
                        if stale not in STALE_EXCEPTIONS:
                            STALE_EXCEPTIONS.append(stale)
                    cal["refused"].add(role)
                    findings.append({
                        "role": role,
                        "page_routes": routes,
                        "page_file": pfile,
                        "gate": gate,
                        "via": chain,
                        "url": display_template(url["template"]),
                        "url_at": f"{url['file']}:{url['line']}",
                        "url_verb": url["verb"],
                        "verb_inference": url["how"],
                        "verb": e["verb"],
                        "api_route": e["route"],
                        "controller_action": f"{e['controller']}.{e['action']}",
                        "endpoint_at": f"{e['file']}:{e['line']}",
                        "required": " AND ".join(attr_expr(a) for a in e["attrs"]),
                        "missing": sorted({p for _, ms in fails for p in ms}),
                        "failed_attrs": [attr_expr(a) for a, _ in fails],
                        "low_confidence": url["verb"] == "ANY" or len(hits) > 1,
                    })
        page_summaries.append({"page": name, "routes": routes, "file": pfile, "gate": gate,
                               "roles_evaluated": roles, "roles_redirected": redirected,
                               "endpoints_matched": len(page_eps)})

    # dedupe findings: one row per (role, page, endpoint, url site)
    uniq = {}
    for f in findings:
        k = (f["role"], f["page_file"], f["verb"], f["api_route"], f["url_at"])
        if k not in uniq or len(f["via"]) < len(uniq[k]["via"]):
            uniq[k] = f
    findings = sorted(uniq.values(), key=lambda f: (f["role"], f["page_routes"][0], f["verb"], f["api_route"]))
    return write_outputs(findings, unmatched, generic, url_stats, perm_endpoints, endpoints, page_summaries,
                  endpoint_callers, razors, units, impls, request_types, perms_for)


def write_outputs(findings, unmatched, generic, url_stats, perm_endpoints, endpoints, page_summaries,
                  endpoint_callers, razors, units, impls, request_types, perms_for):
    by_role = defaultdict(int)
    for f in findings:
        by_role[f["role"]] += 1
    triples = defaultdict(set)
    for f in findings:
        triples[(f["role"], f["verb"] + " " + f["api_route"], ", ".join(f["missing"]), f["controller_action"])].add(f["page_routes"][0])
    counts = {
        "api_endpoints": len(endpoints),
        "require_permission_endpoints": len(perm_endpoints),
        "require_permission_actions": len({(e["file"], e["action"], e["line"]) for e in perm_endpoints}),
        "web_url_occurrences": sum(url_stats.values()),
        "web_urls_matched": url_stats["matched"],
        "web_urls_unmatched": url_stats["unmatched"],
        "web_urls_generic": url_stats["generic"],
        "web_url_literals_not_in_any_member": len(ORPHAN_URLS),
        "web_url_literals_not_in_any_member_code": sum(1 for o in ORPHAN_URLS if o["where"] == "code"),
        "pages_analysed": len(page_summaries),
        "findings": len(findings),
        "findings_low_confidence": sum(1 for f in findings if f["low_confidence"]),
        "distinct_role_endpoint_permission_triples": len(triples),
        "findings_by_role": dict(sorted(by_role.items())),
    }
    callers = []
    for (key, ctrl, action), v in sorted(endpoint_callers.items()):
        callers.append({"endpoint": key, "controller_action": f"{ctrl}.{action}",
                        "pages": sorted(v["pages"]), "refused_roles": sorted(v["refused"])})
    data = {"counts": counts, "findings": findings,
            "triples": [{"role": r, "endpoint": ep, "missing": m, "controller_action": ca, "pages": sorted(p)}
                        for (r, ep, m, ca), p in sorted(triples.items())],
            "endpoint_callers": callers, "unmatched_urls": unmatched, "generic_urls": generic,
            "pages": page_summaries, "stale_exceptions": STALE_EXCEPTIONS}
    os.makedirs(OUT, exist_ok=True)

    L = []
    L.append("# RequirePermission impact: Web pages refused once user permissions are checked\n")
    L.append("Generated by `scripts/find_page_permission_gaps.py` (deterministic; re-run to reproduce).\n")
    L.append("## Counts\n")
    for k, v in counts.items():
        L.append(f"- {k}: {v}")
    L.append("\n## Distinct (role, endpoint, missing permission) triples\n")
    L.append("| Role | Endpoint | Controller.action | Missing | Pages |\n|---|---|---|---|---|")
    for (r, ep, m, ca), p in sorted(triples.items()):
        L.append(f"| {r} | `{ep}` | {ca} | {m} | {', '.join(sorted(p))} |")
    L.append("\n## Findings by role and page\n")
    L.append("`low` = verb unknown at the call site or the URL matched more than one route.\n")
    cur_role = None
    cur_page = None
    for f in findings:
        if f["role"] != cur_role:
            cur_role = f["role"]
            cur_page = None
            L.append(f"\n### {cur_role}\n")
        if f["page_file"] != cur_page:
            cur_page = f["page_file"]
            L.append(f"\n#### {', '.join(f['page_routes'])} — `{f['page_file']}` ({f['gate']})\n")
            L.append("| Endpoint | Controller.action | Required | Missing | Via | URL site | Conf |\n|---|---|---|---|---|---|---|")
        L.append(f"| `{f['verb']} {f['api_route']}` | {f['controller_action']} | {f['required']} | {', '.join(f['missing'])} | "
                 f"{' → '.join(f['via'])} | `{f['url_at']}` ({f['url_verb']}/{f['verb_inference']}) | {'low' if f['low_confidence'] else 'ok'} |")
    L.append("\n## RequirePermission endpoints the Web calls, and from which pages\n")
    L.append("| Endpoint | Controller.action | Required | Refused roles | Pages |\n|---|---|---|---|---|")
    ep_by_key = {(e["key"], e["controller"], e["action"]): e for e in perm_endpoints}
    for c in callers:
        e = next((x for k, x in ep_by_key.items() if k[0] == c["endpoint"] and f"{k[1]}.{k[2]}" == c["controller_action"]), None)
        req = " AND ".join(attr_expr(a) for a in e["attrs"]) if e else "?"
        L.append(f"| `{c['endpoint']}` | {c['controller_action']} | {req} | {', '.join(c['refused_roles']) or '—'} | {'<br>'.join(c['pages'])} |")
    called = {c["endpoint"] + c["controller_action"] for c in callers}
    L.append("\n## RequirePermission endpoints no analysed page reaches\n")
    for e in perm_endpoints:
        if e["key"] + f"{e['controller']}.{e['action']}" not in called:
            L.append(f"- `{e['verb']} {e['route']}` {e['controller']}.{e['action']} {' AND '.join(attr_expr(a) for a in e['attrs'])}")
    L.append("\n## Pages\n")
    L.append("| Page | Routes | Gate | Roles evaluated | Redirected away | Endpoints matched |\n|---|---|---|---|---|---|")
    for p in page_summaries:
        L.append(f"| `{p['file']}` | {', '.join(p['routes'])} | {p['gate']} | {', '.join(p['roles_evaluated'])} | "
                 f"{', '.join(p['roles_redirected']) or '—'} | {p['endpoints_matched']} |")
    L.append("\n## Unmatched Web URLs\n")
    L.append("| URL | Verb (inference) | Site | Matches with another verb |\n|---|---|---|---|")
    for u in unmatched:
        L.append(f"| `{u['url']}` | {u['verb']} ({u['how']}) | `{u['file']}:{u['line']}` | {', '.join(u['matches_other_verb']) or '—'} |")
    L.append("\n## Generic URLs (controller segment is a hole; not matched)\n")
    for u in generic:
        L.append(f"- `{u['url']}` at `{u['file']}:{u['line']}`")
    L.append("\n## Self-check: api/ literals no parsed member claims\n")
    L.append("A `code` row here is a parser gap (a call the analysis cannot see); `razor markup` rows are "
             "links/attributes rendered to the browser, not Web→API HttpClient calls.\n")
    for o in ORPHAN_URLS:
        L.append(f"- [{o['where']}] `{o['url']}` at `{o['file']}:{o['line']}`")
    L.append("\n## Stale exceptions (ignored this run)\n")
    for s in STALE_EXCEPTIONS:
        L.append(f"- {s['page']} / {s['role']}: {s['why']}; no longer matches {s['unmatched']}")
    if not STALE_EXCEPTIONS:
        L.append("None.")
    unused = [k for k in ROLE_HIDDEN_CALLS if k not in USED_HIDDEN_CALLS]
    L.append("\n## Hidden-call exceptions no finding needed\n")
    L.append("Not an error: the call is no longer refused, so the entry can probably be deleted.\n")
    for page, role, call in unused:
        L.append(f"- {page} / {role} / `{call}`")
    if not unused:
        L.append("None.")
    data["orphan_urls"] = ORPHAN_URLS
    with open(os.path.join(OUT, "impact.json"), "w", encoding="utf-8") as fh:
        json.dump(data, fh, indent=2, sort_keys=False)
    with open(os.path.join(OUT, "impact.md"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(L) + "\n")

    print(json.dumps(counts, indent=2))
    control_ok = run_controls(findings, data, units, razors, impls, request_types)
    for s in STALE_EXCEPTIONS:
        print(f"STALE exception ignored: {s['page']} / {s['role']} ({s['why']})")
    print(f"\nWrote {os.path.join(OUT, 'impact.md')}")
    return 0 if control_ok and not STALE_EXCEPTIONS else 1


def run_controls(findings, data, units, razors, impls, request_types):
    print("\n== Control (a): QuotationController from quotation pages, SalesRep and Cashier ==")
    qpages = {"ShopInventory.Web/Components/Pages/Quotations.razor", "ShopInventory.Web/Components/Pages/CreateQuotation.razor"}
    reached = [c for c in data["endpoint_callers"] if c["controller_action"].startswith("QuotationController.")
               and any(any(q in p for q in qpages) for p in c["pages"])]
    print(f"QuotationController endpoints reached from the quotation pages: {len(reached)}")
    for c in reached:
        print(f"  {c['endpoint']:45} {c['controller_action']:45} refused={c['refused_roles']}")
    bad = [f for f in findings if f["page_file"] in qpages and f["role"] in ("SalesRep", "Cashier")
           and f["controller_action"].startswith("QuotationController.")
           and set(f["missing"]) & {"quotations.view", "quotations.create", "quotations.edit"}]
    other = [f for f in findings if f["page_file"] in qpages and f["role"] in ("SalesRep", "Cashier")
             and f["controller_action"].startswith("QuotationController.") and f not in bad]
    print("RESULT:", "PASS" if reached and not bad else "FAIL",
          f"(view/create/edit findings: {len(bad)}; other QuotationController findings: {len(other)})")
    for f in bad + other:
        print(f"  {f['role']} {f['verb']} {f['api_route']} missing={f['missing']} via={' -> '.join(f['via'])}")
    return bool(reached) and not bad


if __name__ == "__main__":
    status = main()
    write_role_outputs()
    sys.exit(status)
