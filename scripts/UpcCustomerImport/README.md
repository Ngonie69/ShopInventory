# Loading the UPC route workbooks

Puts the office's four UPC workbooks (`UPC 1.xlsx`, `UPC 2 Customers.xlsx`, `UPC 3 Database.xlsx`,
`UPC 4 Customers.xlsx`) onto the UPC vans (VAN014 to VAN017) as route customers with calling days.
The production database can't be reached from a laptop, so the work is split between the laptop and
the API server.

| Step | Where | Command | What it does |
| --- | --- | --- | --- |
| 1 | laptop | `python -I scripts/UpcCustomerImport/read_upc_workbooks.py "<folder with the xlsx>"` | Reads the 8 sheets into `out/outlets.csv` and `out/headings.csv` |
| 2 | server | `.\Export-UpcRouteCustomers.ps1` | Read-only. Writes `upc-reps.csv`, `upc-customers.csv` and `upc-summary.txt` |
| 3 | laptop | `python -I scripts/UpcCustomerImport/build_apply.py "<folder with the export>"` | Matches the outlets to the shops already on each van. Writes `out/plan.csv`, `out/review.csv` and `out/upc-apply.sql` |
| 4 | server | `.\Apply-UpcRouteCustomers.ps1`, then `.\Apply-UpcRouteCustomers.ps1 -Apply` | A dry run, then the real load in one transaction |

Rows in `review.csv` are left out until someone decides them. Put a `decisions.csv` (`key,decision`,
where the decision is `new`, `skip` or an existing customer's Id) next to the export and run step 3
again.

Everything in `out/` is customer data and is git-ignored. This repository is public.

The rules for merging, matching and what the load changes are in the docstring of `build_apply.py`.
