#!/usr/bin/env bash
# Drops "Mobile" from the maintenance lockout's names, now that it covers the web portal too.
#
# Identifiers only. Three things it deliberately leaves alone, because they are contracts with
# something outside this repository:
#   - the routes /api/maintenance/mobile and /api/maintenance/mobile/status, which the handsets call
#   - the error code "Maintenance.MobileTransactionsSuspended", which the apps read off the refusal
#   - the SystemConfigs keys "Mobile.Maintenance.*", which MaintenanceStore renames with a fallback
# None of the three contain the token "MobileMaintenance", so the substitution cannot reach them.
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

# 1. Contents.
mapfile -t files < <(grep -rl "MobileMaintenance\|mobileMaintenance" --include=*.cs --include=*.razor --include=*.md .)
printf '%s\n' "${files[@]}" | sed 's/^/  content: /'
sed -i 's/MobileMaintenance/Maintenance/g; s/mobileMaintenance/maintenance/g' "${files[@]}"

# 2. Directories, deepest first so a rename never invalidates a path still to come.
find . -depth -type d -name '*MobileMaintenance*' -print0 | while IFS= read -r -d '' dir; do
    new="$(dirname "$dir")/$(basename "$dir" | sed 's/MobileMaintenance/Maintenance/g')"
    echo "  dir:     $dir -> $new"
    git mv "$dir" "$new"
done

# 3. Files.
find . -type f -name '*MobileMaintenance*' -print0 | while IFS= read -r -d '' file; do
    new="$(dirname "$file")/$(basename "$file" | sed 's/MobileMaintenance/Maintenance/g')"
    echo "  file:    $file -> $new"
    git mv "$file" "$new"
done

echo
echo "Remaining 'MobileMaintenance' occurrences (expect none):"
grep -rn "MobileMaintenance" --include=*.cs --include=*.razor --include=*.md . || echo "  none"
