#!/usr/bin/env bash
# Exercise the actual pre-apply guard using only a stubbed Azure CLI.
set -euo pipefail

guard=$(git rev-parse --show-toplevel)/.github/scripts/check-archive-what-if.sh
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
export PATH="$scratch:$PATH" RESOURCE_GROUP=synthetic MAINTENANCE_MODE=true
cat >"$scratch/az" <<'BASH'
#!/usr/bin/env bash
[[ "$*" == 'deployment group what-if --resource-group synthetic --template-file infrastructure/archive/main.bicep --parameters infrastructure/archive/main.bicepparam --parameters maintenanceMode=true --no-pretty-print --output json' ]] || exit 99
printf '%s' "$PREVIEW_RESULT"
exit "$PREVIEW_STATUS"
BASH
chmod +x "$scratch/az"

check() {
  local label=$1 expected=$2 actual=allowed
  PREVIEW_STATUS=$3 PREVIEW_RESULT=$4 bash "$guard" >"$scratch/guard.log" 2>&1 || actual=refused
  if [[ "$actual" != "$expected" ]]; then
    cat "$scratch/guard.log" >&2
    printf '%s: expected %s, got %s\n' "$label" "$expected" "$actual" >&2
    exit 1
  fi
  printf 'PASS archive what-if: %s (%s)\n' "$label" "$actual"
}

check cli-failure refused 7 '{"changes":[]}'
check malformed-json refused 0 '{"changes":'
check missing-changes refused 0 '{}'
check null-changes refused 0 '{"changes":null}'
check object-changes refused 0 '{"changes":{}}'
check invalid-entry refused 0 '{"changes":[{}]}'
check multiple-documents refused 0 '{"changes":[]} {"changes":[]}'
check failed-status refused 0 '{"status":"Failed","changes":[]}'
check delete refused 0 '{"changes":[{"changeType":"Modify"},{"changeType":"Delete"}]}'
check replace refused 0 '{"changes":[{"changeType":"Replace"}]}'
check inconclusive-deploy refused 0 '{"changes":[{"changeType":"Deploy"}]}'
check inconclusive-unsupported refused 0 '{"changes":[{"changeType":"Unsupported"}]}'
check no-changes allowed 0 '{"status":"Succeeded","changes":[]}'
check nondestructive allowed 0 '{"changes":[{"changeType":"Modify"},{"changeType":"Create"}]}'
