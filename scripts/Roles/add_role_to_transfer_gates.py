"""Adds a role to every inventory-transfer role gate that admits the depot controller.

The transfer gates are role strings spread over three API controllers and the Web's page lists,
none of which can see the others. This rewrites all of them in one pass, so a role added to the
transfer workflow is added everywhere the depot controller already is, and re-running it is a no-op.

    python -I scripts/Roles/add_role_to_transfer_gates.py ShopStock

Only the transfer gates are touched. The daily stock fetch and the stock refresh endpoints spell
their lists differently ("Admin,ApiUser,..." and "Admin,Manager,...") and stay as they are.
"""

import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]

# The role strings exactly as the transfer gates spell them, and the files that carry them.
STRING_GATE = "Admin,StockController,WashBay,DepotController"
STRING_FILES = [
    "ShopInventory/Controllers/InventoryTransferController.cs",
    "ShopInventory/Controllers/ApprovalProcessController.cs",
    "ShopInventory/Controllers/DesktopIntegrationController.cs",
    "ShopInventory.Web/Data/UserRoles.cs",
]

# The same list as prose in doc comments, and as an array of constants on the transfers page.
PROSE_GATE = "Admin, StockController, WashBay, DepotController"
ARRAY_GATE = "[UserRoles.Admin, UserRoles.StockController, UserRoles.WashBay, UserRoles.DepotController"
ARRAY_FILES = ["ShopInventory.Web/Components/Pages/InventoryTransfers.razor"]

# The endpoint tables document the same gates as prose.
DOC_FILES = ["API.md"]

# Who may open /inventory-transfers: the transfer roles plus the manager, read only.
VIEW_GATE = "Admin,Manager,StockController,WashBay,DepotController"


def rewrite(path: pathlib.Path, role: str) -> int:
    raw = path.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    changed = 0
    for gate, joiner in (
        (STRING_GATE, ","),
        (VIEW_GATE, ","),
        (PROSE_GATE, ", "),
        (ARRAY_GATE, ", UserRoles."),
    ):
        updated = gate + joiner + role
        # Skip sites already carrying the role so the script can run twice.
        count = text.count(gate) - text.count(updated)
        if count:
            text = text.replace(updated, "\0").replace(gate, updated).replace("\0", updated)
            changed += count
    if changed:
        path.write_bytes(("﻿" if bom else "").encode("utf-8") + text.encode("utf-8"))
    return changed


def main() -> None:
    if len(sys.argv) != 2:
        sys.exit("usage: add_role_to_transfer_gates.py <Role>")
    role = sys.argv[1]
    total = 0
    for relative in STRING_FILES + ARRAY_FILES + DOC_FILES:
        changed = rewrite(ROOT / relative, role)
        total += changed
        print(f"{changed:3}  {relative}")
    print(f"{total:3}  gates now admit {role}")


if __name__ == "__main__":
    main()
