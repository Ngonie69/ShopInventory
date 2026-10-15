#!/usr/bin/env python3
"""List every API action with its effective authorization, and fail on a write any staff role can make.

"ApiAccess" is RequireRole(ApplicationRoles.ApiAccessRoles): every staff role, from Admin to Lab,
Driver and TillOperator. An action whose only gate is that policy (or ApiAccessWithOperator, which is
wider still) is open to anyone with a staff login. For a read that is sometimes fine; for a write it
almost never is. On 2026-10-15, 82 writes relied on it alone: 28 are on the allowlist below, and
the other 54 included posting SAP payments and invoices.

For each controller action this resolves, from the source as text:

  * verb and route ([Route] on the class joined to each [Http*] on the action);
  * class and action [Authorize] / [Authorize(Roles = ...)] / [Authorize(Policy = ...)] /
    [AllowAnonymous], with policies resolved to the roles, schemes and permission requirements
    their AddPolicy registration names;
  * class and action [RequirePermission(...)].

and classifies it:

  anonymous        [AllowAnonymous] somewhere on it
  permission       a RequirePermission attribute or a policy carrying a PermissionRequirement
  role             a Roles gate that refuses at least one staff role
  service-key      a policy in SERVICE_KEY_POLICIES: an integration's key, never a person
  apiaccess-only   nothing narrower than "any staff role"
  authenticated    no gate at all beyond a valid token (no [Authorize] arguments anywhere)
  handler-checked  apiaccess-only or authenticated, but on HANDLER_CHECKED: the action sends a command
                   whose handler asks DocumentAttachmentAccessService about the entity in the request

A write (POST, PUT, PATCH, DELETE) classed apiaccess-only or authenticated fails the run unless it is
on ALLOWLIST below with the reason it is safe, or on HANDLER_CHECKED and its handler is seen to ask. An allowlist entry no endpoint matches, or one whose
endpoint has since been gated, also fails: a list that has stopped describing the code is not a
review of it.

Shares the C# lexer and controller walk with find_page_permission_gaps.py and the policy resolution
with inventory_role_gates.py, so comments and string contents are never mistaken for code.

Deterministic, read-only, Python 3 stdlib only.

Usage:
    python scripts/check_api_write_gates.py [repo_root] [out_dir] [--compare OTHER/inventory.json]
        repo_root  default: the repository this script lives in
        out_dir    default: <repo_root>/artifacts/api-write-gates (gitignored)
        --compare  an inventory.json from another tree (say, main): prints, route by route, which
                   staff roles each side admits that the other does not, and writes role-changes.md

The role matrix (inventory.json "admits") is each role's default permissions run through every
gate on the endpoint. Direct per-user grants are not modelled, and Admin and ApiUser are left out
because they hold every permission.

Writes inventory.json and inventory.md (every action, one row per verb and route). Exits 1 on an
unlisted ungated write, a stale allowlist entry, or a gate the parser cannot resolve.

Run it once against a tree you know to be dirty before trusting a green run (git stash a gate, or
check out the commit before one): a parser that silently matches nothing looks clean too. The
built-in control below fails the run if the walk finds suspiciously few actions.
"""
import json
import os
import re
import sys
from collections import Counter

# --compare is taken off argv before the shared modules read their positional arguments from it.
COMPARE = None
if "--compare" in sys.argv:
    _i = sys.argv.index("--compare")
    COMPARE = sys.argv[_i + 1]
    del sys.argv[_i:_i + 2]

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import find_page_permission_gaps as gaps  # noqa: E402
import inventory_role_gates as role_gates  # noqa: E402

REPO = gaps.REPO
OUT = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else os.path.join(REPO, "artifacts", "api-write-gates")
WRITE_VERBS = {"POST", "PUT", "PATCH", "DELETE"}
# Policies that admit an integration's key and refuse every person (ServiceKeyAuthorizationPolicy.cs).
SERVICE_KEY_POLICIES = {"ServiceKeyOnly"}

# The walk found 678 endpoints on 2026-10-15. Far fewer means the parser stopped seeing controllers.
MIN_ENDPOINTS = 400

# Writes that rely on ApiAccess alone on purpose. Key: "VERB route" exactly as the inventory prints it.
# Every entry says why any staff role may make the call. Keep it short and keep it true: an entry
# whose endpoint gains a gate, or disappears, fails the run until it is removed.
SELF = "acts only on the caller's own account, read from the token"
READ = "a POST that only reads: the body carries the query, nothing is written"
ALLOWLIST = {
    # --- the caller's own account ------------------------------------------------------------------
    # Every staff role signs in, changes its password and manages its own second factor and devices.
    "POST api/Auth/passkeys/register/options": SELF,
    "POST api/Auth/passkeys/register/complete": SELF,
    "POST api/Auth/change-password": SELF,
    "POST api/Auth/logout": SELF,
    "POST api/Auth/mobile/biometric-preference": SELF,
    # PasswordController is being reworked separately.
    "POST api/Password/change": SELF,
    "PUT api/Password/credentials": SELF,
    "POST api/vansales/auth/password": SELF,
    "POST api/TwoFactor/setup": SELF,
    "POST api/TwoFactor/enable": SELF,
    "POST api/TwoFactor/disable": SELF,
    "POST api/TwoFactor/verify": SELF,
    "POST api/TwoFactor/backup-codes/regenerate": SELF,
    "POST api/PushNotification/register": SELF + " (registers this device to the caller)",
    "POST api/PushNotification/unregister": SELF + " (removes the caller's own device token)",
    "POST api/PushNotification/test": SELF + " (pushes to the caller's own devices)",
    "POST api/Notification/mark-read": "scoped to the caller's own audience (user and role) by ICallerAccountReader",
    "DELETE api/Notification/{id}": "scoped to the caller's own audience (user and role) by ICallerAccountReader",

    # --- a POST that only reads --------------------------------------------------------------------
    "POST api/DesktopIntegration/stock/validate": READ,
    "POST api/DesktopIntegration/invoices/validate": READ,
    "POST api/DesktopIntegration/transfers/validate": READ,
    "POST api/Stock/warehouse/{warehouseCode}/sales": READ,

}

# Writes any staff role may call because the handler decides, entity by entity, whether this caller may
# read or act on the entity named in the request. /documents/{type}/{id} admits every signed-in role, so
# a role gate on these would break the page for most of them. Key: "VERB route"; value: the handler.
# An entry is checked, not trusted: the action must send that handler's command, and the handler must
# call the access service. A missing call, a missing handler, or an entry whose endpoint has since
# gained an attribute gate fails the run.
DOCUMENT_COMMANDS = "ShopInventory/Features/Documents/Commands"
HANDLER_CHECKED = {
    "POST api/Document/attachments": f"{DOCUMENT_COMMANDS}/UploadAttachment/UploadAttachmentHandler.cs",
    "DELETE api/Document/attachments/{id}": f"{DOCUMENT_COMMANDS}/DeleteAttachment/DeleteAttachmentHandler.cs",
    "POST api/Document/generate": f"{DOCUMENT_COMMANDS}/GenerateDocument/GenerateDocumentHandler.cs",
    "POST api/Document/generate/download":
        f"{DOCUMENT_COMMANDS}/GenerateAndDownloadDocument/GenerateAndDownloadDocumentHandler.cs",
    "POST api/Document/email": f"{DOCUMENT_COMMANDS}/EmailDocument/EmailDocumentHandler.cs",
    "POST api/Document/signatures": f"{DOCUMENT_COMMANDS}/CreateSignature/CreateSignatureHandler.cs",
}
ACCESS_CALL = re.compile(r"\.Authorize(?:Entity|Attachment)AccessAsync\s*\(")


def handler_check_problem(row):
    """None when the action sends the handler's command and the handler asks the access service."""
    handler = HANDLER_CHECKED[row["key"]]
    path = os.path.join(REPO, handler)
    if not os.path.exists(path):
        return f"{row['key']}: {handler} does not exist"
    if not ACCESS_CALL.search(gaps.lex_cs(gaps.read(path))[0]):
        return f"{row['key']}: {handler} never calls DocumentAttachmentAccessService"
    command = os.path.basename(handler)[: -len("Handler.cs")] + "Command"
    file, line = row["at"].rsplit(":", 1)
    src = gaps.read(os.path.join(REPO, file))
    masked = gaps.lex_cs(src)[0]
    # "at" is the line of the action's opening brace
    start = sum(len(x) for x in src.splitlines(keepends=True)[: int(line) - 1])
    body_open = masked.find("{", start)
    body = masked[body_open: gaps.match_paren(masked, body_open) + 1] if body_open >= 0 else ""
    if not re.search(rf"\bnew\s+{command}\s*\(", body):
        return f"{row['key']}: {row['action']} does not send {command}, so {handler} is not its handler"
    return None


def classify(gate_info, endpoint_attrs, staff):
    """-> (class, detail) for one endpoint."""
    if gate_info["anonymous"]:
        return "anonymous", "AllowAnonymous"
    permissions = [p for a in endpoint_attrs for p in a["perms"]] + list(gate_info["permissions"])
    if permissions:
        return "permission", ", ".join(dict.fromkeys(permissions))
    if any(g["from"].split(" (")[0] in {f"Policy {p}" for p in SERVICE_KEY_POLICIES} for g in gate_info["gates"]):
        return "service-key", "Policy " + ", ".join(sorted(SERVICE_KEY_POLICIES))
    narrow = [g for g in gate_info["gates"] if any(r not in g["roles"] for r in staff)]
    if narrow:
        admitted = set(staff)
        for g in narrow:
            admitted &= set(g["roles"])
        return "role", ", ".join(r for r in staff if r in admitted) or "(no staff role)"
    if gate_info["gates"]:
        return "apiaccess-only", "; ".join(g["from"].split(" (")[0] for g in gate_info["gates"])
    return "authenticated", "no role, policy or permission"


def admitted_roles(gate_info, endpoint_attrs, kind, staff, defaults):
    """Staff roles the endpoint admits, from role defaults alone."""
    if kind == "anonymous":
        return list(staff)
    if kind == "service-key":
        return []
    admitted = []
    for role in staff:
        if any(role not in g["roles"] for g in gate_info["gates"]):
            continue
        held = set(defaults.get(role, defaults.get("_", [])))
        if any(not (set(a["perms"]) <= held if a["all"] else set(a["perms"]) & held) for a in endpoint_attrs if a["perms"]):
            continue
        if gate_info["permissions"] and not set(gate_info["permissions"]) & held:
            continue
        admitted.append(role)
    return admitted


def compare(rows, other_path, out):
    other = json.load(open(other_path, encoding="utf-8"))
    before = {(r["key"]): set(r["admits"]) for r in other["endpoints"]}
    after = {(r["key"]): set(r["admits"]) for r in rows}
    changes = []
    for key in sorted(set(before) | set(after), key=lambda k: (k.split(" ", 1)[1].lower(), k)):
        lost = sorted(before.get(key, set()) - after.get(key, set()))
        gained = sorted(after.get(key, set()) - before.get(key, set()))
        if key not in before or key not in after:
            note = "added" if key not in before else "removed"
            print(f"ROUTE {note.upper()}: {key}")
            continue
        if lost or gained:
            changes.append((key, lost, gained))
    by_role = Counter(role for _, lost, _ in changes for role in lost)
    L = ["| Endpoint | Roles that lose access | Roles that gain access |", "|---|---|---|"]
    L += [f"| `{key}` | {', '.join(lost) or '—'} | {', '.join(gained) or '—'} |" for key, lost, gained in changes]
    L += ["", "| Role | Routes lost |", "|---|---|"] + [f"| {role} | {n} |" for role, n in sorted(by_role.items())]
    with open(os.path.join(out, "role-changes.md"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(L) + "\n")
    for key, lost, gained in changes:
        print(f"CHANGED: {key:<75} lost={','.join(lost) or '-'}  gained={','.join(gained) or '-'}")
    print(f"{len(changes)} endpoints change; routes lost by role: {dict(sorted(by_role.items()))}")


def main():
    perm_consts, _, roles_default = gaps.load_permissions()
    defaults = {k.split(".")[-1]: v for k, v in roles_default.items()}
    problems = role_gates.UniqueList()
    model = role_gates.GateModel(perm_consts, problems)
    staff = [r for r in model.arrays["ApiAccessRoles"] if r not in role_gates.NOT_STAFF]
    matrix_roles = [r for r in model.arrays["ApiAccessWithOperatorRoles"] if r not in role_gates.NOT_STAFF]
    # role constant names -> role values ("Adr" -> "ADR")
    defaults = {model.roles.get(k, k): v for k, v in defaults.items()}

    rows = []
    for e in gaps.parse_controllers(perm_consts):
        for a in e["attrs"]:
            for u in a["unresolved"]:
                problems.append(f"{a['at']}: RequirePermission argument {u!r} cannot be resolved")
        g = model.gates(e)
        kind, detail = classify(g, e["attrs"], staff)
        rows.append({
            "verb": e["verb"], "route": e["route"], "key": f"{e['verb']} {e['route']}",
            "action": f"{e['controller']}.{e['action']}", "at": f"{e['file']}:{e['line']}",
            "write": e["verb"] in WRITE_VERBS, "class": kind, "detail": detail,
            "schemes": g["schemes"],
            "admits": admitted_roles(g, e["attrs"], kind, matrix_roles, defaults),
        })
    rows.sort(key=lambda r: (r["action"], r["verb"], r["route"]))

    for r in rows:
        if r["key"] in HANDLER_CHECKED and r["class"] in ("apiaccess-only", "authenticated"):
            problem = handler_check_problem(r)
            if problem:
                problems.append(problem)
            else:
                r["class"] = "handler-checked"
                r["detail"] = "DocumentAttachmentAccessService in " + os.path.basename(HANDLER_CHECKED[r["key"]])

    if len(rows) < MIN_ENDPOINTS:
        problems.append(f"control: only {len(rows)} endpoints parsed (expected at least {MIN_ENDPOINTS}); "
                        "the controller walk has stopped seeing controllers")

    ungated = [r for r in rows if r["write"] and r["class"] in ("apiaccess-only", "authenticated")]
    unlisted = [r for r in ungated if r["key"] not in ALLOWLIST]
    by_key = {}
    for r in rows:
        by_key.setdefault(r["key"], []).append(r)
    stale = []
    for key in ALLOWLIST:
        hits = by_key.get(key, [])
        if not hits:
            stale.append(f"{key}: no endpoint has this verb and route")
        elif not any(r in ungated for r in hits):
            stale.append(f"{key}: now gated ({', '.join(r['class'] + ' ' + r['detail'] for r in hits)}); remove it")
    for key in HANDLER_CHECKED:
        hits = by_key.get(key, [])
        if not hits:
            stale.append(f"{key}: no endpoint has this verb and route (HANDLER_CHECKED)")
        elif not any(r["class"] == "handler-checked" for r in hits) and not any(r in ungated for r in hits):
            stale.append(f"{key}: now gated by attribute ({', '.join(r['class'] + ' ' + r['detail'] for r in hits)}); "
                         "remove it from HANDLER_CHECKED")

    actions = {r["action"] for r in rows}
    broad_actions = {r["action"] for r in rows if r["class"] in ("apiaccess-only", "authenticated")}
    counts = {
        "endpoints": len(rows),
        "actions": len(actions),
        "endpoints_by_class": dict(sorted(Counter(r["class"] for r in rows).items())),
        "actions_relying_on_apiaccess_alone": len(broad_actions),
        "writes_relying_on_apiaccess_alone": len(ungated),
        "of_which_allowlisted": len(ungated) - len(unlisted),
        "unlisted_ungated_writes": len(unlisted),
        "stale_allowlist_entries": len(stale),
        "problems": len(problems),
    }

    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "inventory.json"), "w", encoding="utf-8") as fh:
        json.dump({"counts": counts, "staff_roles": staff, "endpoints": rows, "unlisted": unlisted,
                   "stale_allowlist": stale, "problems": list(problems)}, fh, indent=2)

    L = ["# API action authorization\n",
         "Generated by `scripts/check_api_write_gates.py` (deterministic; re-run to reproduce).\n",
         "## Counts\n"]
    L += [f"- {k}: {v}" for k, v in counts.items()]
    L.append("\n## Writes relying on ApiAccess alone\n")
    L.append("| Endpoint | Action | Allowlisted because | Site |\n|---|---|---|---|")
    for r in ungated:
        L.append(f"| `{r['key']}` | {r['action']} | {ALLOWLIST.get(r['key'], '**NOT LISTED**')} | `{r['at']}` |")
    L.append("\n## Every endpoint\n")
    L.append("| Endpoint | Action | Class | Gate | Schemes | Site |\n|---|---|---|---|---|---|")
    for r in rows:
        L.append(f"| `{r['key']}` | {r['action']} | {r['class']} | {r['detail']} | {', '.join(r['schemes'])} | `{r['at']}` |")
    with open(os.path.join(OUT, "inventory.md"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(L) + "\n")

    print(json.dumps(counts, indent=2))
    for r in unlisted:
        print(f"UNGATED WRITE: {r['key']}  ({r['action']}, {r['at']}) relies on {r['detail'] or 'authentication'} alone")
    for s in stale:
        print(f"STALE ALLOWLIST: {s}")
    for p in problems:
        print(f"PROBLEM: {p}")
    print(f"\nWrote {os.path.join(OUT, 'inventory.md')}")
    if COMPARE:
        compare(rows, COMPARE, OUT)
    return 1 if (unlisted or stale or problems) else 0


if __name__ == "__main__":
    sys.exit(main())
