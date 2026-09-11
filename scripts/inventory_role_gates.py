#!/usr/bin/env python3
"""Inventory every role gate in the API, and every role check a role-gate handler cannot see.

A role gate is anything that becomes ASP.NET Core's RolesAuthorizationRequirement:
[Authorize(Roles = ...)] on a controller or action, and policy.RequireRole(...) in a named policy.
ActingUserRoleAuthorizationHandler judges that requirement by the signed-in user when a request also
carries an API key, so this lists where it applies and, as importantly, where it does not:

  * every named policy in the order Program.cs registers it, flagging a name registered twice (the
    later registration silently replaces the earlier one);
  * every RequireRole call and every [Authorize(Roles = ...)] attribute, roles resolved;
  * every controller endpoint with a role gate: which roles it admits, which schemes authenticate it
    (an endpoint whose policies name no scheme is authenticated by the default bearer scheme alone,
    so the key never reaches it), and which staff roles it refuses;
  * role checks written in code — IsInRole, reads of ClaimTypes.Role, RequireAssertion — which no
    authorization handler sees and which must be judged one by one;
  * every authorization handler class and whether anything registers it.

Deterministic, read-only, Python 3 stdlib only. Shares the C# lexer and controller walk with
find_page_permission_gaps.py, so comments and string contents are never mistaken for code.

Usage:
    python scripts/inventory_role_gates.py [repo_root] [out_dir]
        repo_root  default: the repository this script lives in
        out_dir    default: <repo_root>/artifacts/role-gates (gitignored)

Writes inventory.json and inventory.md. Exits 1 when a gate names a role ApplicationRoles does not
define, a role or policy expression cannot be resolved, a policy name is registered twice, or an
authorization handler class is registered nowhere.
"""
import json
import os
import re
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import find_page_permission_gaps as gaps  # noqa: E402

REPO = gaps.REPO
OUT = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else os.path.join(REPO, "artifacts", "role-gates")
API = os.path.join(REPO, "ShopInventory")
PROGRAM = os.path.join(API, "Program.cs")
SKIP_DIRS = {"bin", "obj", "Backups", "Migrations"}
SCHEMES = {
    "JwtBearerDefaults.AuthenticationScheme": "Bearer",
    "AuthenticationSchemes.Jwt": "Bearer",
    "AuthenticationSchemes.ApiKey": "ApiKey",
}
NOT_STAFF = {"Admin", "ApiUser"}


def api_files():
    found = []
    for root, dirs, files in os.walk(API):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS)
        found += [os.path.join(root, f) for f in sorted(files) if f.endswith(".cs")]
    return sorted(found, key=gaps.rel)


class Source:
    def __init__(self, path):
        self.path = path
        self.rel = gaps.rel(path)
        self.src = gaps.read(path)
        self.masked, self.lits = gaps.lex_cs(self.src)

    def line(self, pos):
        return gaps.line_of(self.src, pos)

    def at(self, pos):
        return f"{self.rel}:{self.line(pos)}"

    def text_of_line(self, pos):
        start = self.src.rfind("\n", 0, pos) + 1
        end = self.src.find("\n", pos)
        return self.src[start:end if end >= 0 else None].strip()

    def enclosing_class(self, pos):
        names = [m.group(1) for m in re.finditer(r"\b(?:class|record|struct)\s+(\w+)", self.masked[:pos])]
        return names[-1] if names else None


def load_roles():
    path = os.path.join(API, "Models", "ApplicationRoles.cs")
    _, masked, _, consts = gaps.parse_const_strings(path)
    roles = {name: value for name, (kind, value) in consts.items() if kind == "lit"}
    arrays = {}
    for m in re.finditer(r"\bstatic\s+readonly\s+string\[\]\s+(\w+)\s*=\s*\[", masked):
        close = gaps.match_paren(masked, m.end() - 1)
        names = re.findall(r"\b(\w+)\b", masked[m.end():close])
        arrays[m.group(1)] = [roles[n] for n in names if n in roles]
    return roles, arrays


def expr_tokens(value_text, piece_lits, roles, arrays):
    """A masked C# string expression -> [("s", text) | ("a", [roles])], or None if unresolvable."""
    lit_iter = iter(sorted(piece_lits, key=lambda lit: lit.start))
    tokens = []
    for m in re.finditer(r"\x01+|[A-Za-z_][\w.]*|\+|\S", value_text):
        token = m.group(0)
        if token.startswith("\x01"):
            lit = next(lit_iter, None)
            if lit is None:
                return None
            tokens.append(("s", gaps.lit_text(lit)))
        elif token == "+":
            continue
        elif re.fullmatch(r"[A-Za-z_][\w.]*", token) and (token.startswith("ApplicationRoles.") or "." not in token):
            short = token.split(".")[-1]
            if short in roles:
                tokens.append(("s", roles[short]))
            elif short in arrays:
                tokens.append(("a", arrays[short]))
            else:
                return None
        else:
            return None
    return tokens


def authorize_attr(source, name, a, b, pos, roles, arrays, problems):
    """One [Authorize(...)] or [AllowAnonymous] attribute -> dict."""
    base = name.split(".")[-1].removesuffix("Attribute")
    at = source.at(pos)
    if base == "AllowAnonymous":
        return {"anonymous": True, "at": at}
    info = {"policy": None, "roles": None, "schemes": [], "at": at}
    for arg in gaps.attr_arg_values(source.masked, source.lits, a, b):
        if arg[0] == "lit":
            info["policy"] = arg[1]
        elif arg[0] == "named" and arg[1] == "Roles":
            tokens = expr_tokens(arg[2], arg[3], roles, arrays)
            if tokens is None or any(kind != "s" for kind, _ in tokens):
                problems.append(f"{at}: Roles = {arg[2]!r} cannot be resolved")
                info["roles"] = []
            else:
                info["roles"] = [r.strip() for r in "".join(text for _, text in tokens).split(",") if r.strip()]
        elif arg[0] == "named" and arg[1] == "Policy":
            tokens = expr_tokens(arg[2], arg[3], {}, {})
            info["policy"] = "".join(text for _, text in tokens) if tokens else arg[2]
        elif arg[0] == "named" and arg[1] == "AuthenticationSchemes":
            tokens = expr_tokens(arg[2], arg[3], {}, {})
            info["schemes"] = [s.strip() for s in "".join(t for _, t in tokens or []).split(",") if s.strip()]
        else:
            problems.append(f"{at}: unrecognised Authorize argument {arg[:3]!r}")
    return info


def attribute_target(source, pos):
    open_bracket = source.masked.rfind("[", 0, pos + 1)
    k = gaps.match_paren(source.masked, open_bracket) + 1
    masked = source.masked
    while True:
        while k < len(masked) and masked[k].isspace():
            k += 1
        if k < len(masked) and masked[k] == "[":
            k = gaps.match_paren(masked, k) + 1
            continue
        break
    window = masked[k:k + 600]
    m = re.match(r"(?:(?:public|private|protected|internal|static|sealed|abstract|partial|file)\s+)*"
                 r"(?:class|record|struct|interface)\s+(\w+)", window)
    if m:
        return "class", m.group(1)
    m = re.search(r"(\w+)\s*(?:<[^()]*>)?\s*\(", window)
    return "member", (m.group(1) if m else "?")


def authorize_sites(sources, roles, arrays, problems):
    sites = []
    for source in sources:
        for name, a, b, pos in gaps.iter_attributes(source.masked, source.lits, 0, len(source.masked)):
            if name.split(".")[-1].removesuffix("Attribute") != "Authorize":
                continue
            info = authorize_attr(source, name, a, b, pos, roles, arrays, problems)
            kind, target = attribute_target(source, pos)
            owner = target if kind == "class" else f"{source.enclosing_class(pos)}.{target}"
            sites.append(dict(info, target=owner, target_kind=kind, file=source.rel))
    return sites


def extension_method_at(source, pos):
    names = [m.group(1) for m in re.finditer(
        r"\bstatic\s+[\w<>.?]+\s+(\w+)\s*\(\s*this\s+IServiceCollection\b", source.masked[:pos])]
    return names[-1] if names else None


def registration_order(source, pos, program):
    """(Program.cs line the registration runs from, line within the defining file)."""
    if source.path == program.path:
        return (source.line(pos), source.line(pos)), "Program.cs"
    method = extension_method_at(source, pos)
    if method:
        call = re.search(r"\.\s*" + re.escape(method) + r"\s*\(", program.masked)
        if call:
            return (program.line(call.start()), source.line(pos)), f"Program.cs:{program.line(call.start())} {method}()"
    return (10 ** 9, source.line(pos)), "not registered from Program.cs"


def call_args(source, name_regex, start, end):
    for m in re.finditer(name_regex, source.masked[start:end]):
        open_paren = start + m.end() - 1
        close = gaps.match_paren(source.masked, open_paren)
        yield start + m.start(), gaps.attr_arg_values(source.masked, source.lits, open_paren + 1, close)


def resolve_role_args(source, args, pos, roles, arrays, problems):
    resolved = []
    for arg in args:
        if arg[0] == "lit":
            resolved.append(arg[1])
        elif arg[0] == "expr":
            tokens = expr_tokens(arg[1], [], roles, arrays)
            if not tokens:
                problems.append(f"{source.at(pos)}: RequireRole argument {arg[1]!r} cannot be resolved")
                continue
            for kind, value in tokens:
                resolved += value if kind == "a" else [value]
        else:
            problems.append(f"{source.at(pos)}: RequireRole argument {arg[:3]!r} cannot be resolved")
    return resolved


def policy_definitions(sources, program, roles, arrays, perm_consts, problems):
    definitions = []
    for source in sources:
        for m in re.finditer(r"\bAddPolicy\s*\(", source.masked):
            open_paren = m.end() - 1
            close = gaps.match_paren(source.masked, open_paren)
            args = gaps.attr_arg_values(source.masked, source.lits, open_paren + 1, close)
            name = args[0][1] if args and args[0][0] == "lit" else (args[0][1] if args else "?")
            role_sets = [resolve_role_args(source, a, p, roles, arrays, problems)
                         for p, a in call_args(source, r"\bRequireRole\s*\(", open_paren, close)]
            schemes = []
            for p, a in call_args(source, r"\bAddAuthenticationSchemes\s*\(", open_paren, close):
                schemes += [SCHEMES.get(x[1], x[1]) for x in a]
            permissions = []
            for p, a in call_args(source, r"\bnew\s+PermissionRequirement\s*\(", open_paren, close):
                body = source.masked[p:gaps.match_paren(source.masked, source.masked.find("(", p))]
                permissions += [perm_consts.get(ref, ref) for ref in re.findall(r"\bPermissions?\.\w+", body)] \
                    or ["{" + re.sub(r"\s+", " ", body.split("{", 1)[-1].split("}", 1)[0]).strip() + "}"]
            claims = [x[1] for p, a in call_args(source, r"\bRequireClaim\s*\(", open_paren, close) for x in a]
            order, registered_from = registration_order(source, m.start(), program)
            definitions.append({
                "name": name, "at": source.at(m.start()), "order": order, "registered_from": registered_from,
                "role_sets": role_sets, "schemes": schemes, "permissions": permissions, "claims": claims,
                "require_role_at": [source.at(p) for p, _ in call_args(source, r"\bRequireRole\s*\(", open_paren, close)],
            })
    definitions.sort(key=lambda d: (d["order"], d["at"]))
    final = {}
    replaced = []
    for d in definitions:
        if d["name"] in final:
            replaced.append({"name": d["name"], "replaced": final[d["name"]]["at"], "by": d["at"]})
            final[d["name"]]["replaced_by"] = d["at"]
        final[d["name"]] = d
    for r in replaced:
        problems.append(f"policy {r['name']!r} defined at {r['replaced']} is replaced by {r['by']}")
    return definitions, final


def require_role_sites(sources, definitions, roles, arrays, problems):
    in_policy = {at for d in definitions for at in d["require_role_at"]}
    sites = []
    for source in sources:
        for pos, args in call_args(source, r"\bRequireRole\s*\(", 0, len(source.masked)):
            at = source.at(pos)
            owner = next((d for d in definitions if at in d["require_role_at"]), None)
            sites.append({
                "at": at,
                "roles": resolve_role_args(source, args, pos, roles, arrays, problems) if at not in in_policy
                else owner["role_sets"][owner["require_role_at"].index(at)],
                "policy": owner["name"] if owner else None,
                "live": bool(owner) and "replaced_by" not in owner,
                "source": source.text_of_line(pos),
            })
    return sites


def endpoint_gates(endpoint, final, problems):
    authorize = endpoint["authorize"]
    gates, schemes, permissions, notes = [], set(), [], []
    anonymous = any(a.get("anonymous") for a in authorize)
    for a in authorize:
        if a.get("anonymous"):
            continue
        schemes |= set(a.get("schemes") or [])
        if a.get("policy"):
            definition = final.get(a["policy"])
            if definition is None:
                problems.append(f"{a['at']}: policy {a['policy']!r} is defined nowhere")
                continue
            schemes |= set(definition["schemes"])
            permissions += definition["permissions"]
            gates += [{"roles": rs, "from": f"Policy {a['policy']} ({definition['at']})"} for rs in definition["role_sets"]]
        if a.get("roles") is not None:
            gates.append({"roles": a["roles"], "from": f"Roles ({a['at']})"})
    if not schemes:
        notes.append("no scheme named: default bearer scheme only")
    return {
        "anonymous": anonymous,
        "gates": gates,
        "schemes": sorted(schemes) or ["(default) Bearer"],
        "key_reachable": "ApiKey" in schemes,
        "permissions": permissions,
        "notes": notes,
    }


ROLE_READS = [
    ("IsInRole", re.compile(r"\.\s*IsInRole\s*\(")),
    ("ClaimTypes.Role read", re.compile(r"\bClaimTypes\.Role\b")),
    ("RoleClaimType", re.compile(r"\bRoleClaimType\b")),
    ("RequireAssertion", re.compile(r"\bRequireAssertion\s*\(")),
]
CLAIM_CREATION = re.compile(r"new\s*(?:Claim)?\s*\(\s*ClaimTypes\.Role\b")


def role_reads(sources):
    reads = []
    for source in sources:
        for kind, pattern in ROLE_READS:
            for m in pattern.finditer(source.masked):
                if kind == "ClaimTypes.Role read":
                    head = source.masked[max(0, m.start() - 40):m.end()]
                    if CLAIM_CREATION.search(head):
                        continue
                reads.append({"kind": kind, "at": source.at(m.start()), "file": source.rel,
                              "class": source.enclosing_class(m.start()), "source": source.text_of_line(m.start())})
    reads.sort(key=lambda r: (r["file"], int(r["at"].rsplit(":", 1)[1]), r["kind"]))
    return reads


def handlers(sources):
    found = []
    registrations = "\n".join(s.masked for s in sources)
    for source in sources:
        for m in re.finditer(r"\bclass\s+(\w+)\b[^{;]*?:\s*[^{;]*?\b(AuthorizationHandler\s*<\s*(\w+)\s*>|IAuthorizationHandler\b)",
                             source.masked):
            name = m.group(1)
            registered = re.search(r"IAuthorizationHandler\s*,\s*" + re.escape(name) + r"\s*>", registrations) is not None
            found.append({"handler": name, "requirement": m.group(3) or "(any)", "at": source.at(m.start()),
                          "registered": registered})
    return found


class UniqueList(list):
    """A problem found once per endpoint is still one problem."""

    def append(self, item):
        if item not in self:
            super().append(item)


def controller_class_authorize(sources, roles, arrays, problems):
    """(file, controller) -> its class-level [Authorize]/[AllowAnonymous], located as the controller walk locates class attributes."""
    found = {}
    for source in sources:
        if not source.rel.startswith("ShopInventory/Controllers/"):
            continue
        masked = source.masked
        for cm in re.finditer(r"\bclass\s+(\w+)", masked):
            if not cm.group(1).endswith("Controller"):
                continue
            head_start = max(masked.rfind(";", 0, cm.start()), masked.rfind("}", 0, cm.start()),
                             masked.rfind("{", 0, cm.start())) + 1
            found[(source.rel, cm.group(1))] = [
                authorize_attr(source, name, a, b, pos, roles, arrays, problems)
                for name, a, b, pos in gaps.iter_attributes(masked, source.lits, head_start, cm.start())
                if name.split(".")[-1].removesuffix("Attribute") in ("Authorize", "AllowAnonymous")]
    return found


class GateModel:
    """What each endpoint's role gates admit, built once per repository. find_page_permission_gaps.py uses it too."""

    def __init__(self, perm_consts, problems):
        self.problems = problems
        self.roles, self.arrays = load_roles()
        self.sources = [Source(p) for p in api_files()]
        self.program = next(s for s in self.sources if s.path == PROGRAM)
        self.by_path = {os.path.normcase(os.path.abspath(s.path)): s for s in self.sources}
        self.definitions, self.final = policy_definitions(
            self.sources, self.program, self.roles, self.arrays, perm_consts, problems)
        self.class_authorize = controller_class_authorize(self.sources, self.roles, self.arrays, problems)

    def gates(self, endpoint):
        """The controller walk's raw [Authorize] spans resolved and joined to the class's, then judged."""
        authorize = self.class_authorize.get((endpoint["file"], endpoint["controller"]), []) + [
            authorize_attr(self.by_path[os.path.normcase(os.path.abspath(raw["path"]))],
                           raw["name"], raw["a"], raw["b"], raw["pos"], self.roles, self.arrays, self.problems)
            for raw in endpoint["authorize"]]
        return endpoint_gates(dict(endpoint, authorize=authorize), self.final, self.problems)


def main():
    perm_consts, _, _ = gaps.load_permissions()
    problems = UniqueList()
    model = GateModel(perm_consts, problems)
    roles, arrays, sources = model.roles, model.arrays, model.sources
    definitions, final = model.definitions, model.final
    known_roles = set(roles.values())
    staff = [r for r in arrays["ApiAccessWithOperatorRoles"] if r not in NOT_STAFF]

    rr_sites = require_role_sites(sources, definitions, roles, arrays, problems)
    attr_sites = authorize_sites(sources, roles, arrays, problems)
    role_attr_sites = [s for s in attr_sites if s.get("roles") is not None]

    endpoints = []
    for e in gaps.parse_controllers(perm_consts):
        g = model.gates(e)
        if g["anonymous"] or not g["gates"]:
            continue
        refused = [r for r in staff if not all(r in gate["roles"] for gate in g["gates"])]
        endpoints.append(dict(g, verb=e["verb"], route=e["route"], action=f"{e['controller']}.{e['action']}",
                              at=f"{e['file']}:{e['line']}", staff_refused=refused,
                              admin_admitted=all("Admin" in gate["roles"] for gate in g["gates"]),
                              has_action_role_gate=any(gate["from"].startswith("Roles") for gate in g["gates"])))

    for where, role_list in ([(s["at"], s["roles"]) for s in role_attr_sites] +
                             [(s["at"], s["roles"]) for s in rr_sites]):
        for r in role_list:
            if r not in known_roles:
                problems.append(f"{where}: role {r!r} is not defined in ApplicationRoles")

    reads = role_reads(sources)
    handler_list = handlers(sources)
    for h in handler_list:
        if not h["registered"]:
            problems.append(f"{h['at']}: authorization handler {h['handler']} is registered nowhere")

    action_gates = [e for e in endpoints if e["has_action_role_gate"]]
    counts = {
        "files_scanned": len(sources),
        "policy_definitions": len(definitions),
        "policies_replaced": sum(1 for d in definitions if "replaced_by" in d),
        "require_role_calls": len(rr_sites),
        "require_role_calls_live": sum(1 for s in rr_sites if s["live"]),
        "authorize_roles_attributes": len(role_attr_sites),
        "authorize_roles_attributes_on_classes": sum(1 for s in role_attr_sites if s["target_kind"] == "class"),
        "endpoints_with_a_role_gate": len(endpoints),
        "endpoints_with_an_action_or_controller_roles_attribute": len(action_gates),
        "actions_with_an_action_or_controller_roles_attribute": len({e["action"] for e in action_gates}),
        "actions_with_a_roles_attribute_the_key_reaches": len({e["action"] for e in action_gates if e["key_reachable"]}),
        "actions_with_a_roles_attribute_the_key_cannot_reach": len({e["action"] for e in action_gates if not e["key_reachable"]}),
        "role_reads_in_code": len(reads),
        "role_reads_by_kind": dict(sorted(defaultdict(int, {k: sum(1 for r in reads if r["kind"] == k) for k, _ in ROLE_READS}).items())),
        "authorization_handlers": len(handler_list),
        "problems": len(problems),
    }

    data = {"counts": counts, "policies": definitions, "require_role": rr_sites, "authorize_roles": role_attr_sites,
            "endpoints": endpoints, "role_reads": reads, "handlers": handler_list, "problems": problems,
            "staff_roles": staff}
    for d in data["policies"]:
        d["order"] = list(d["order"])
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "inventory.json"), "w", encoding="utf-8") as fh:
        json.dump(data, fh, indent=2)

    L = ["# API role gates\n",
         "Generated by `scripts/inventory_role_gates.py` (deterministic; re-run to reproduce).\n",
         "## Counts\n"]
    L += [f"- {k}: {v}" for k, v in counts.items()]
    L.append("\n## Named policies, in registration order\n")
    L.append("| Policy | Defined at | Registered from | Roles (RequireRole) | Schemes | Permissions | Claims | State |\n|---|---|---|---|---|---|---|---|")
    for d in definitions:
        state = f"replaced by {d['replaced_by']}" if "replaced_by" in d else "live"
        L.append(f"| {d['name']} | `{d['at']}` | {d['registered_from']} | "
                 f"{'; '.join(', '.join(rs) for rs in d['role_sets']) or '—'} | {', '.join(d['schemes']) or '(default) Bearer'} | "
                 f"{', '.join(d['permissions']) or '—'} | {', '.join(d['claims']) or '—'} | {state} |")
    L.append("\n## RequireRole calls\n")
    L.append("| Site | Policy | Live | Roles |\n|---|---|---|---|")
    for s in rr_sites:
        L.append(f"| `{s['at']}` | {s['policy'] or '—'} | {'yes' if s['live'] else 'NO'} | {', '.join(s['roles'])} |")
    L.append("\n## [Authorize(Roles = ...)] attributes\n")
    L.append("| Site | Target | Roles |\n|---|---|---|")
    for s in role_attr_sites:
        L.append(f"| `{s['at']}` | {s['target_kind']} `{s['target']}` | {', '.join(s['roles'])} |")
    L.append("\n## Endpoints with a role gate\n")
    L.append("Gates AND together; each gate admits any one of its roles. `Key` = the API key scheme authenticates "
             "this endpoint, so a Web request arrives with the merged principal. `Staff refused` = staff roles "
             "(ApiAccessWithOperatorRoles without Admin and ApiUser) at least one gate refuses.\n")
    L.append("| Endpoint | Action | Key | Action/controller Roles | Staff refused | Site |\n|---|---|---|---|---|---|")
    for e in sorted(endpoints, key=lambda e: (e["action"], e["verb"], e["route"])):
        attr = "; ".join(", ".join(g["roles"]) for g in e["gates"] if g["from"].startswith("Roles")) or "— (policy only)"
        L.append(f"| `{e['verb']} {e['route']}` | {e['action']} | {'yes' if e['key_reachable'] else 'no'} | {attr} | "
                 f"{', '.join(e['staff_refused']) or '—'} | `{e['at']}` |")
    L.append("\n## Role checks written in code (no authorization handler sees these)\n")
    L.append("| Site | Kind | Class | Source |\n|---|---|---|---|")
    for r in reads:
        L.append(f"| `{r['at']}` | {r['kind']} | {r['class'] or '—'} | `{r['source'].replace('|', '&#124;')}` |")
    L.append("\n## Authorization handlers\n")
    L.append("| Handler | Requirement | Site | Registered |\n|---|---|---|---|")
    for h in handler_list:
        L.append(f"| {h['handler']} | {h['requirement']} | `{h['at']}` | {'yes' if h['registered'] else 'NO'} |")
    L.append("\n## Problems\n")
    L += [f"- {p}" for p in problems] or ["None."]
    with open(os.path.join(OUT, "inventory.md"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(L) + "\n")

    print(json.dumps(counts, indent=2))
    for p in problems:
        print("PROBLEM:", p)
    print(f"\nWrote {os.path.join(OUT, 'inventory.md')}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
