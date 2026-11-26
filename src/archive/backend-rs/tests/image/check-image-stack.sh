#!/usr/bin/env bash
# Cross-stack gate for the candidate production image (issue #246). Boots the
# inherited image-test topology itself — PostgreSQL with the canonical role
# bootstrap and synthetic fixture passwords, Azurite with the well-known
# emulator key, Mailpit and a local OTLP collector on a private network — then
# verifies only what is uniquely cross-stack: storage initialization, explicit
# migration, API liveness with the served static export, one smoke worker pass
# and the separate adoption database. Rust, browser and image-shell checks stay
# in their own suites. Run from the repo root after building the candidate
# image. Requires Podman (or Docker) and Python 3. No host bind mounts: SQL
# reaches PostgreSQL through cp and exec, collector configuration through the
# environment, so remote container engines work as well.
set -euo pipefail

runtime=${ARCHIVE_CONTAINER_RUNTIME:-podman}
tag=${Archive__TestImageTag:?Set Archive__TestImageTag to the local candidate tag}
version=${ARCHIVE_EXPECTED_IMAGE_VERSION:?Set ARCHIVE_EXPECTED_IMAGE_VERSION to the build version}
image=localhost/liedertafel-archive:$tag
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../../.." && pwd)
work=$(mktemp -d)
suffix=$$
network=archive-gate-$suffix
pg=archive-gate-pg-$suffix
azurite=archive-gate-azurite-$suffix
mail=archive-gate-mail-$suffix
collector=archive-gate-otelcol-$suffix
api=archive-gate-api-$suffix

cleanup() {
	"$runtime" rm -f "$api" "$collector" "$mail" "$azurite" "$pg" >/dev/null 2>&1 || true
	"$runtime" network rm "$network" >/dev/null 2>&1 || true
	rm -rf -- "$work"
}
trap cleanup EXIT

# Same throwaway dependency topology as the retired image-test AppHost: the
# canonical Neon role bootstrap installs the real permission model and the
# fixture passwords unlock its synthetic logins before first start. The SQL is
# copied into the created container so no worktree bind mount is needed.
"$runtime" network create "$network" >/dev/null
"$runtime" create --name "$pg" --network "$network" --network-alias archive-postgres \
	-e POSTGRES_USER=archive_admin -e POSTGRES_PASSWORD=rust235-admin -e POSTGRES_DB=archive \
	docker.io/library/postgres:17.6 >/dev/null
"$runtime" cp "$root/infrastructure/neon/bootstrap-roles.sql" \
	"$pg":/docker-entrypoint-initdb.d/10-bootstrap.sql >/dev/null
"$runtime" cp "$root/src/archive/backend-rs/tests/image/fixture-role-passwords.sql" \
	"$pg":/docker-entrypoint-initdb.d/20-fixture-passwords.sql >/dev/null
"$runtime" start "$pg" >/dev/null

"$runtime" run -d --name "$azurite" --network "$network" --network-alias archive-blobs-235 \
	mcr.microsoft.com/azure-storage/azurite:3.35.0 \
	azurite --location /data --disableProductStyleUrl --skipApiVersionCheck \
	--blobHost 0.0.0.0 --queueHost 0.0.0.0 >/dev/null
"$runtime" run -d --name "$mail" --network "$network" --network-alias archive-mail \
	docker.io/axllent/mailpit:v1.27.8 >/dev/null

# The collector configuration rides in the environment (like compose.yaml),
# because the scratch-based collector image has no shell for mount tricks.
otel_config=$(cat <<'YAML'
receivers:
  otlp:
    protocols:
      http:
        endpoint: 0.0.0.0:4318
exporters:
  debug:
    verbosity: basic
extensions:
  health_check:
    endpoint: 0.0.0.0:13133
service:
  extensions: [health_check]
  pipelines:
    traces:
      receivers: [otlp]
      exporters: [debug]
    metrics:
      receivers: [otlp]
      exporters: [debug]
    logs:
      receivers: [otlp]
      exporters: [debug]
YAML
)
"$runtime" run -d --name "$collector" --network "$network" --network-alias archive-otelcol \
	-e "OTEL_CONFIG=$otel_config" \
	ghcr.io/open-telemetry/opentelemetry-collector-releases/opentelemetry-collector:0.166.0 \
	--config=env:OTEL_CONFIG >/dev/null

wait_exec() {
	local name=$1
	shift
	local attempt
	for attempt in {1..60}; do
		if "$runtime" exec "$name" "$@" >/dev/null 2>&1; then
			return 0
		fi
		sleep 1
	done
	echo "check-image-stack: $name did not become ready" >&2
	"$runtime" logs --tail 20 "$name" >&2 || true
	exit 1
}
wait_exec "$pg" pg_isready -q -U archive_admin -d archive
wait_exec "$azurite" node -e "require('http').get('http://127.0.0.1:10000/devstoreaccount1?comp=list', r => process.exit(r.statusCode < 500 ? 0 : 1)).on('error', () => process.exit(1))"
wait_exec "$mail" wget -q -O /dev/null http://127.0.0.1:8025/readyz
# The scratch collector cannot curl its own health endpoint; validate re-checks
# the exact configuration it runs.
wait_exec "$collector" /otelcol validate --config=env:OTEL_CONFIG

# The connection strings are the topology's documented contract: the runtime
# and migration roles from the bootstrap, Azurite's private network alias
# (usable by the API, the workers and Azurite's own server-side copy) and the
# real SMTP mail host, with the offline scripted model provider.
blobs='DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://archive-blobs-235:10000/devstoreaccount1;'
queues='DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;QueueEndpoint=http://archive-blobs-235:10001/devstoreaccount1;'
image_env=(
	--pull=never --cpus=3 --memory=1g --network "$network"
	-e ARCHIVE_ENVIRONMENT=Development
	-e OTEL_METRIC_EXPORT_INTERVAL=5000
	-e OTEL_EXPORTER_OTLP_ENDPOINT=http://archive-otelcol:4318
	-e OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
)
app_env=(
	"${image_env[@]}"
	-e 'ConnectionStrings__archive-db=Host=archive-postgres;Port=5432;Database=archive;Username=archive_runtime;Password=rust235-runtime'
	-e "ConnectionStrings__archive-blobs=$blobs"
	-e "ConnectionStrings__archive-queues=$queues"
	-e Mail__Provider=Smtp -e Mail__Host=archive-mail -e Mail__Port=1025
	-e Archive__Chat__Provider=Scripted
)

image_run() {
	local label=$1
	shift
	local log="$work/$label.log"
	local status=0
	"$runtime" run --rm --name "archive-gate-$label-$suffix" "$@" >"$log" 2>&1 || status=$?
	if ((status != 0)); then
		cat "$log" >&2
		echo "check-image-stack: $label exited $status" >&2
		exit "$status"
	fi
}

sql_assert() {
	local database=$1
	local sql=$2
	local label=$3
	local out
	out=$("$runtime" exec "$pg" psql -U archive_admin -d "$database" -tA -c "$sql")
	if [[ "$out" != t ]]; then
		echo "check-image-stack: $label failed: got '$out'" >&2
		exit 1
	fi
}

# The storage initializer prepares the private container, queues and CORS rule
# exactly once, before the API starts.
image_run storage-init "${app_env[@]}" "$image" --initialize-local-storage

# The API starts against the still-pending schema; migration stays explicit.
"$runtime" run -d --name "$api" -p 127.0.0.1::8080 "${app_env[@]}" "$image" >/dev/null
address=$("$runtime" port "$api" 8080/tcp)
python3 - "$address" "$version" <<'PY'
import http.client
import json
import re
import sys
import time

def request(path):
    connection = http.client.HTTPConnection(sys.argv[1], timeout=2)
    try:
        connection.request("GET", path, headers={"X-Forwarded-Proto": "https"})
        response = connection.getresponse()
        return response.status, response.getheader("Content-Type"), response.read()
    finally:
        connection.close()

for _ in range(100):
    try:
        assert request("/alive")[0] == 200
        break
    except (OSError, http.client.HTTPException):
        time.sleep(0.3)
else:
    raise SystemExit("The archive image never became live")

status, content_type, body = request("/system/status/")
assert status == 200 and content_type.startswith("text/html") and b"Systemstatus" in body
asset = re.search(rb'src="(/_next/[^"]+\.js)"', body)[1].decode()
status, content_type, body = request(asset)
assert status == 200 and "javascript" in content_type and body
status, content_type, body = request("/api/build")
assert json.loads(body) == {"application": "Liedertafel Archiv", "version": sys.argv[2], "development": True}
status, content_type, body = request("/api/no-such-rust-image-route")
assert status == 404 and content_type.startswith("application/problem+json") and json.loads(body)
PY

image_run migrate "${app_env[@]}" \
	-e 'ConnectionStrings__archive-migrations=Host=archive-postgres;Port=5432;Database=archive;Username=archive_migrator;Password=rust235-migrator' \
	"$image" --migrate
sql_assert archive \
	'SELECT array_agg(version ORDER BY version) = ARRAY[1,2,3,4]::bigint[] AND bool_and(success) FROM public._sqlx_migrations' \
	'the four SQLx migrations applied'

image_run worker-smoke "${app_env[@]}" "$image" --worker-smoke

# Adoption: the independently frozen EF schema loads into a separate database,
# then the image migrates it in place and the synthetic Lied survives verbatim.
# The origin never sees this connection.
"$runtime" exec "$pg" psql -U archive_admin -d postgres -c 'CREATE DATABASE archive_adoption' >/dev/null
{
	printf 'DROP SCHEMA public;\n'
	cat "$root/src/archive/backend-rs/crates/archive-db/baseline/neon-schema.sql"
	cat "$root/src/archive/backend-rs/crates/archive-db/baseline/ef-history.sql"
	cat <<'SQL'
INSERT INTO public.lieder
    ("Id", "Title", "CreatedAt", "CreatedByAccountId", "UpdatedAt", "UpdatedByAccountId", "RowVersion")
VALUES ('23500000-0000-4000-8000-000000000001', 'Vorhandenes synthetisches Lied', now(),
    '23500000-0000-4000-8000-000000000002', now(), '23500000-0000-4000-8000-000000000002', 12);
SQL
} | "$runtime" exec -i "$pg" psql -U archive_admin -d archive_adoption -v ON_ERROR_STOP=1 -q -tA
sql_assert archive_adoption \
	"SELECT to_regclass('public._sqlx_migrations') IS NULL" \
	'the adoption database starts at the old EF head'
image_run adopt-check "${image_env[@]}" \
	-e 'ConnectionStrings__archive-migrations=Host=archive-postgres;Port=5432;Database=archive_adoption;Username=archive_admin;Password=rust235-admin' \
	"$image" --migrate
sql_assert archive_adoption \
	"SELECT (SELECT count(*) = 44 FROM public.\"__EFMigrationsHistory\")
		AND (SELECT array_agg(version ORDER BY version) = ARRAY[1,2,3,4]::bigint[]
			AND bool_and(success) FROM public._sqlx_migrations)
		AND EXISTS (SELECT FROM public.lieder WHERE \"Id\" = '23500000-0000-4000-8000-000000000001'
			AND \"Title\" = 'Vorhandenes synthetisches Lied' AND \"RowVersion\" = 12)" \
	'the adoption preserved the frozen state and the synthetic Lied'

"$runtime" stop "$api" >/dev/null
printf '%s\n' 'PASS cross-stack image gate: stack boot, explicit migration, origin export, smoke worker and adoption'