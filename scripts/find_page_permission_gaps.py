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
  * Role checks inside a page (IsInRole, a role flag around a button, a redirect) are not modelled,
    so a row can be unreachable from the UI. Verified exceptions go in ROLE_CONDITIONAL_RENDERS
    (a component rendered for some roles only) or ROLE_REDIRECTS (a role sent elsewhere). A redirect
    names source patterns that must all still match its page, or the run reports it stale, ignores
    it and exits 1.
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


def find_component_tags(info, component_names, web_roles):
    src = info.src
    # AuthorizeView Roles spans (restrict whatever renders inside them)
    spans = []
    for m in re.finditer(r'<AuthorizeView\b[^>]*\bRoles\s*=\s*"([^"]*)"[^>]*>', src):
        end = src.find("</AuthorizeView>", m.end())
        roles_txt = m.group(1)
        if roles_txt.startswith("@"):
            roles = resolve_roles_expr(roles_txt, web_roles)
        else:
            roles = [r.strip() for r in roles_txt.split(",") if r.strip()]
        spans.append((m.end(), end if end > 0 else len(src), roles))
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
                all_urls += mem.urls
    # razor whole-unit members
    for name, info in razors.items():
        u = units[name]
        whole = Member(u, "<component>", 0, 0)
        whole.path, whole.src = info.path, info.src
        whole.urls = []
        calls = _member_calls(u, info.src, known, request_types)
        for mname in u.members:
            calls.add(("self", u.name, mname, ""))
        if info.inherits:
            calls.add(("allof", info.inherits, "", ""))
        whole.calls = calls
        u.whole = whole

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
STALE_EXCEPTIONS = []


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
            unmatched_patterns = [p for p in patterns if not re.search(p, info.src)]
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
            if not u or not u.whole:
                continue
            top = via[1].split(">")[0].lstrip("<") if len(via) > 1 else None
            override = ROLE_CONDITIONAL_RENDERS.get((name, top)) if top else None
            eff = restrict
            if override is not None:
                eff = override if eff is None else [r for r in eff if r in override]
            for url, chain in reachable_urls([u.whole], units, impls, request_types):
                reached.append((url, via[:-1] + chain if len(via) > 1 else chain, eff))
        page_eps = set()
        for url, chain, eff in reached:
            status, hits = match_url(url, endpoints)
            if status != "matched":
                continue
            for e in hits:
                page_eps.add(e["key"])
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
    sys.exit(main())
