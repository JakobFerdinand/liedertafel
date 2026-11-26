#!/usr/bin/env bash
# Offline integration gate. Run at the repo root after building the production
# image and exporting the unchanged frontend. Only disposable loopback fixtures.
set -euo pipefail

root=$(pwd)
workspace="$root/src/archive/backend-rs"
runtime=${ARCHIVE_CONTAINER_RUNTIME:-docker}
tag=${Archive__TestImageTag:?Set Archive__TestImageTag to the local candidate tag}
image="localhost/liedertafel-archive:$tag"
export Archive__Video__FfmpegPath
export Archive__Video__FfprobePath
Archive__Video__FfmpegPath=$(realpath "$(command -v ffmpeg)")
Archive__Video__FfprobePath=$(realpath "$(command -v ffprobe)")
test -x "$Archive__Video__FfmpegPath" && test -x "$Archive__Video__FfprobePath"
scratch=$(mktemp -d)
export TMPDIR="$scratch"
pg="archive-test-pg-$$"
azurite="archive-test-azurite-$$"
mail="archive-test-mail-$$"
cleanup() {
  "$runtime" rm -f -v "$pg" "$azurite" "$mail" >/dev/null 2>&1 || true
  rm -rf -- "$scratch"
}
trap cleanup EXIT

# No externally configured model, test database, or credentials enter this gate.
unset ARCHIVE_CHAT_ENDPOINT ARCHIVE_CHAT_DEPLOYMENT_NAME ARCHIVE_CHAT_MODEL_VERSION
unset ARCHIVE_SCAN_EVAL_DIR ARCHIVE_SCAN_EVAL_REPORT ARCHIVE_PROGRAMM_EVAL_DIR ARCHIVE_PROGRAMM_EVAL_REPORT
unset ARCHIVE_234_FROZEN_DLL ARCHIVE_QUERY_ORACLE ARCHIVE_FORM_ROUTES ARCHIVE_VIDEO_PAUSE_ONLY
unset Archive__Chat__Endpoint Archive__Chat__DeploymentName
for variable in ConnectionStrings__archive-db ConnectionStrings__archive-migrations \
  ConnectionStrings__archive-blobs ConnectionStrings__archive-queues; do
  if printenv "$variable" >/dev/null; then
    echo "Run the gate without inherited $variable." >&2
    exit 1
  fi
done
unset Archive__Assets__ServiceUri Archive__Extraction__QueueServiceUri \
  Archive__Reading__QueueServiceUri Archive__Video__QueueServiceUri
unset Mail__AzureEndpoint Mail__AzureConnectionString AZURE_CLIENT_ID
export Archive__Chat__Provider=Scripted
export PGPASSWORD=archive-local-ci

"$runtime" pull docker.io/library/postgres:17.6
"$runtime" pull docker.io/library/node:24-bookworm-slim
"$runtime" run -d --name "$pg" -p 127.0.0.1::5432 \
  -e POSTGRES_PASSWORD="$PGPASSWORD" docker.io/library/postgres:17.6 -c max_connections=300
# Signed ticket destinations must resolve from inside Azurite for real server-side
# copy, and outside for the host test. Match internal and published Blob ports.
export ARCHIVE_NATIVE_BLOB_PORT=51026 ARCHIVE_NATIVE_QUEUE_PORT=52026
start_azurite() {
  "$runtime" run -d --name "$azurite" \
    -p "127.0.0.1:$ARCHIVE_NATIVE_BLOB_PORT:$ARCHIVE_NATIVE_BLOB_PORT" \
    -p "127.0.0.1:$ARCHIVE_NATIVE_QUEUE_PORT:$ARCHIVE_NATIVE_QUEUE_PORT" \
    mcr.microsoft.com/azure-storage/azurite:3.35.0 azurite \
    --disableProductStyleUrl --skipApiVersionCheck \
    --blobHost 0.0.0.0 --blobPort "$ARCHIVE_NATIVE_BLOB_PORT" \
    --queueHost 0.0.0.0 --queuePort "$ARCHIVE_NATIVE_QUEUE_PORT"
  for port in "$ARCHIVE_NATIVE_BLOB_PORT" "$ARCHIVE_NATIVE_QUEUE_PORT"; do
    # Port forwarding can accept a connection before Azurite is listening;
    # an empty reply or reset is also transient during this readiness probe.
    curl --silent --show-error --retry 20 --retry-all-errors --retry-delay 1 --max-time 5 \
      -o /dev/null "http://127.0.0.1:$port/devstoreaccount1?comp=list"
  done
}
start_azurite
"$runtime" run -d --name "$mail" -p 127.0.0.1::8025 -p 127.0.0.1::1025 docker.io/axllent/mailpit:v1.27.8

listen_port() { "$runtime" port "$1" "$2/tcp" | awk -F: '$1=="127.0.0.1" {print $2}'; }
pgport=$(listen_port "$pg" 5432)
mailport=$(listen_port "$mail" 8025)
smtpport=$(listen_port "$mail" 1025)
test -n "$pgport" && test -n "$mailport" && test -n "$smtpport"
export ARCHIVE_TEST_MAILPIT_HTTP_PORT="$mailport" ARCHIVE_TEST_MAILPIT_SMTP_PORT="$smtpport"
timeout 45 bash -c 'until "$0" exec "$1" pg_isready -h 127.0.0.1 -U postgres >/dev/null; do sleep 1; done' "$runtime" "$pg"
curl --fail --silent --show-error --retry 20 --retry-connrefused --retry-delay 1 --max-time 5 \
  -o /dev/null "http://127.0.0.1:$mailport/readyz"

export ARCHIVE_TEST_POSTGRES="postgresql://postgres:$PGPASSWORD@127.0.0.1:$pgport/postgres"
account='DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;'
export ARCHIVE_TEST_AZURITE="${account}BlobEndpoint=http://127.0.0.1:$ARCHIVE_NATIVE_BLOB_PORT/devstoreaccount1;"
export ARCHIVE_TEST_QUEUE="${account}QueueEndpoint=http://127.0.0.1:$ARCHIVE_NATIVE_QUEUE_PORT/devstoreaccount1;"
export ARCHIVE_TEST_PG_DUMP="$scratch/pg_dump"
export LOCAL_TEST_RUNTIME="$runtime" LOCAL_TEST_PG="$pg"
cat >"$ARCHIVE_TEST_PG_DUMP" <<'BASH'
#!/usr/bin/env bash
set -euo pipefail
exec "$LOCAL_TEST_RUNTIME" exec -e PGPASSWORD "$LOCAL_TEST_PG" \
  pg_dump -h 127.0.0.1 -U "$PGUSER" -d "$PGDATABASE" "$@"
BASH
chmod +x "$ARCHIVE_TEST_PG_DUMP"

container=$("$runtime" create --pull=never "$image")
"$runtime" cp "$container:/app/native/libpdfium.so" "$scratch/libpdfium.so"
"$runtime" rm "$container" >/dev/null
export Archive__Pdfium__LibraryPath="$scratch/libpdfium.so"
export ARCHIVE_STATIC_ROOT="$root/src/archive/frontend/out"
test -f "$ARCHIVE_STATIC_ROOT/system/status/index.html"
export ARCHIVE_CHROMIUM
ARCHIVE_CHROMIUM=$(cd "$root/src/archive/frontend" && node -p 'require("@playwright/test").chromium.executablePath()')
test -x "$ARCHIVE_CHROMIUM"

cd "$workspace"
cargo test --workspace --locked -- --skip chat_evaluation_live --skip scan_reading_evaluation_live --skip programm_reading_evaluation_live
cargo test --workspace --locked -- --ignored

# The live functions return normally when unconfigured. Assert the explicit
# skip message and report the three omissions rather than counting live passes.
for evaluation in chat_evaluation scan_reading_evaluation programm_reading_evaluation; do
  cargo test --locked -p archive-server --test "$evaluation" "${evaluation}_live" \
    -- --exact --nocapture >"$scratch/live.txt" 2>&1
  cat "$scratch/live.txt"
  grep -Fq 'live run skipped: set ' "$scratch/live.txt"
  printf -- '- Skipped live evaluation (no provider/corpus): `%s`\n' "$evaluation" >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
done

cargo build --locked -p archive-server --examples
target=$(realpath "${CARGO_TARGET_DIR:-target}")/debug
export ARCHIVE_BINARY="$target/archive-server"
export ARCHIVE_AI_PROBE="$target/examples/ai-foundation-native"
export ARCHIVE_READING_CANCEL_PROBE="$target/examples/reading-cancel-native"
export ARCHIVE_LIED_READING_PROBE="$target/examples/lied-reading-native"
export ARCHIVE_PROGRAMME_HTTP_PROBE="$target/examples/programme-http-native"
export ARCHIVE_234_PG_PORT="$pgport" ARCHIVE_234_PG_PASSWORD="$PGPASSWORD"
export ARCHIVE_234_BLOB_PORT="$ARCHIVE_NATIVE_BLOB_PORT" ARCHIVE_234_QUEUE_PORT="$ARCHIVE_NATIVE_QUEUE_PORT"
export ARCHIVE_234_SMTP_PORT="$smtpport" ARCHIVE_234_MAIL_PORT="$mailport"

# These run only the product or offline provider fixtures against the Rust
# origin. Frozen-Kestrel differentials are historical evidence, not live tests.
for gate in native-auth passkey-browser-native members-native catalogue-native query-binding-native \
  upload-native events-native recordings-native extraction-native ai-foundation-native reading-native \
  http-w3c-native lesung-form-native lied-reading-native lied-navigation-native programm-reading-native chat-binding-native chat-native \
  chat-lifetime-native video-native video-process-native ops-native; do
  if [[ "$gate" == ops-native ]]; then
    # Ops asserts account-wide queue isolation before and after its commands.
    # Earlier fixtures leave queues behind; keep that assertion and give ops a
    # fresh owned Azurite instance on the same test sockets and an empty inbox.
    "$runtime" rm -f -v "$azurite" >/dev/null
    start_azurite
    curl --fail --silent --show-error -X DELETE "http://127.0.0.1:$mailport/api/v1/messages" >/dev/null
  fi
  printf 'Running translated process/browser gate: %s\n' "$gate"
  timeout 300 node "tests/browser/$gate.cjs"
done
