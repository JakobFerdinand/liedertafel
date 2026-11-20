#!/usr/bin/env bash
# Exercise the actual gate with deterministic cargo-deny results (no network).
set -euo pipefail
gate=$(dirname "$0")/check-dependencies.sh
dir=$(mktemp -d)
trap 'rm -rf "$dir"' EXIT
export PATH="$dir:$PATH" AUDIT_FIXTURE="$dir/findings"
cat >"$dir/cargo-deny" <<'BASH'
#!/usr/bin/env bash
if [[ $* == *'check advisories' ]]; then
  cat "$AUDIT_FIXTURE"
  exit "$AUDIT_STATUS"
fi
exit 0
BASH
chmod +x "$dir/cargo-deny"

chrono='{"type":"diagnostic","fields":{"severity":"error","code":"unmaintained","advisory":{"id":"RUSTSEC-2026-0543","package":"chrono","informational":"unmaintained"}}}'
tz='{"type":"diagnostic","fields":{"severity":"error","code":"unmaintained","advisory":{"id":"RUSTSEC-2026-0555","package":"chrono-tz","informational":"unmaintained"}}}'
summary='{"type":"summary","fields":{"advisories":{"errors":2}}}'

check() {
  local expected=$1 tool_status=$2 actual=green
  shift 2
  printf '%s\n' "$@" >"$AUDIT_FIXTURE"
  AUDIT_STATUS=$tool_status bash "$gate" >"$dir/output" 2>&1 || actual=red
  [[ "$actual" == "$expected" ]]
  # This comparison also catches silently hiding the raw accepted advisories.
  while IFS= read -r line; do grep -Fxq "$line" "$dir/output"; done <"$AUDIT_FIXTURE"
}

check green 0 '{"type":"summary","fields":{"advisories":{"errors":0}}}'
check red 0 'not an audit report'
# status 1 can also indicate a tool failure, not a completed audit.
check red 1 'advisory database could not be read'
check red 1 "$chrono"
check red 1 "$chrono" "${tz/RUSTSEC-2026-0555/RUSTSEC-2026-9999}" "$summary"
check red 1 "$chrono" "${tz/chrono-tz/other-package}" "$summary"
check red 1 "$chrono" "${tz/\"code\":\"unmaintained\"/\"code\":\"vulnerability\"}" "$summary"
check red 1 "$chrono" "$tz" "${tz/\"severity\":\"error\"/\"severity\":\"warning\"}" "$summary"
check red 1 "$chrono" "$tz" '{"type":"summary","fields":{"advisories":{"errors":3}}}'
check red 2 "$chrono" "$tz" "$summary"
check green 1 "$chrono" "$tz" "$summary"
echo 'PASS dependency decision: only the two named unmaintained findings are nonblocking.'
