#!/usr/bin/env bash
# The deploy job runs this under the archive-prod lock using its resolved image
# and maintenance setting. Never print the preview, which can include secrets.
# Deploy and Unsupported are inconclusive; only classified changes may apply.
set -euo pipefail
if ! preview=$(az deployment group what-if \
  --resource-group "$RESOURCE_GROUP" \
  --template-file infrastructure/archive/main.bicep \
  --parameters infrastructure/archive/main.bicepparam \
  --parameters maintenanceMode="$MAINTENANCE_MODE" \
  --no-pretty-print --output json 2>/dev/null); then
  echo "::error::Fresh archive what-if failed; refusing to apply." >&2
  exit 1
fi
if ! jq -e -s '
  length == 1 and (.[0] |
    type == "object"
    and ((has("status") | not) or .status == "Succeeded")
    and (.changes | type == "array")
    and all(.changes[]; .changeType |
      . == "Create" or . == "Modify" or . == "NoChange" or . == "Ignore"))
' <<< "$preview" >/dev/null 2>&1; then
  echo "::error::Fresh archive what-if did not provide a valid nondestructive plan; refusing to apply." >&2
  exit 1
fi
