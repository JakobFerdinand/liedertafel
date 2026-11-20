#!/usr/bin/env bash
# Run in the Cargo workspace with cargo-deny 0.20.2 and jq on PATH.
set -euo pipefail

cargo-deny --locked check bans licenses sources

findings=$(mktemp)
trap 'rm -f "$findings"' EXIT
status=0
cargo-deny --locked --format json check advisories >"$findings" 2>&1 || status=$?
cat "$findings"

# #236 comment 7011201805: keep the unfiltered diagnostics above. Only the two
# named unmaintained findings are nonblocking; a tool/fetch failure, changed
# diagnostic format, or any other error fails closed.
[[ $status == 0 || $status == 1 ]]
jq -e -s --argjson status "$status" '
  [.[] | select(.type == "diagnostic" and .fields.severity == "error") | .fields] as $errors
  | [.[] | select(.type == "summary") | .fields.advisories.errors] == [$errors | length]
    and (($status == 0) == ($errors | length == 0))
    and all($errors[];
      .code == "unmaintained" and .advisory.informational == "unmaintained"
      and ([.advisory.id, .advisory.package] |
         . == ["RUSTSEC-2026-0543", "chrono"] or . == ["RUSTSEC-2026-0555", "chrono-tz"]))
    and all(.[]; .type == "summary" or
      (.type == "diagnostic" and (.fields.advisory == null or .fields.severity == "error")))
' "$findings" >/dev/null
if [[ $status == 1 ]]; then
  echo 'Audit reports only the maintainer-accepted unmaintained dependencies (#236); removal remains a follow-up.'
fi
