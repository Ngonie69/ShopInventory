#!/usr/bin/env bash
# Fails on any doc-comment warning in ShopInventory.Web.
#
# GenerateDocumentationFile is on in the csproj, so `dotnet build` already reports these. This
# exists because the interesting families are not one contiguous range — CS1570-1591 are the XML
# warnings, but a bad cref is CS0419/CS1574 — and a pattern narrow enough to look tidy is how one
# gets missed. --no-incremental because a cached compile reports nothing at all for a file it did
# not rebuild, which reads exactly like a pass.
set -uo pipefail
cd "$(dirname "$0")/.."

out=$(dotnet build ShopInventory.Web/ShopInventory.Web.csproj -v q --nologo --no-incremental 2>&1)

if printf '%s\n' "$out" | grep -qE '^Build FAILED|error [A-Z]+[0-9]+'; then
  printf '%s\n' "$out" | grep -E 'error [A-Z]+[0-9]+' | sort -u
  echo "--- build failed"
  exit 2
fi

hits=$(printf '%s\n' "$out" \
       | grep -oE '[^\/]+\.(cs|razor)\([0-9]+,[0-9]+\): warning (CS0419|CS15[0-9]{2}|CS17[0-9]{2}): [^[]*' \
       | sort -u)

if [ -n "$hits" ]; then
  printf '%s\n' "$hits"
  echo "--- $(printf '%s\n' "$hits" | wc -l) doc-comment warning(s)."
  exit 1
fi

# Anything else that appeared is not a doc warning, but the project builds clean today and a new
# warning of any kind is worth seeing rather than filtering away.
others=$(printf '%s\n' "$out" | grep -E ': warning ' | sort -u)
if [ -n "$others" ]; then
  printf '%s\n' "$others"
  echo "--- no doc-comment warnings, but the build is not clean."
  exit 1
fi

echo "OK: ShopInventory.Web builds clean; no doc-comment warnings."
