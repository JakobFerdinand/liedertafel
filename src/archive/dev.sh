#!/usr/bin/env bash
# Local archive stack without .NET (issue #245). Starts the Compose
# dependencies from compose.yaml, applies the schema and runs finite workers
# against them. Requires Docker or Podman with a Compose provider; data lives
# in named volumes that survive `down`. No secrets: the environment comes from
# the commit-safe dev.env.
set -euo pipefail

archive=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
workspace="$archive/backend-rs"
project=archive-dev
timeout=${ARCHIVE_DEV_START_TIMEOUT:-180}

usage() {
	cat <<'EOF'
Usage: dev.sh <command> [args]

Commands:
  up        Start PostgreSQL, Azurite, Mailpit and the OTLP collector and
            wait until ready, then run the one-time local storage
            initialization.
  migrate   Apply the reviewed SQL migrations to the local database.
  worker <smoke|extract|read|transcode|papierkorb-cleanup>
            Run one finite worker to completion.
  down      Stop the stack; the named volumes keep the data.

Environment: ARCHIVE_CONTAINER_RUNTIME (docker|podman) overrides detection,
ARCHIVE_DEV_START_TIMEOUT (seconds) bounds the health wait.
EOF
}

fail() {
	echo "dev.sh: $*" >&2
	exit 1
}

case ${1:-} in
up | migrate | worker | down) ;;
*) usage >&2
	exit 2 ;;
esac

runtime=${ARCHIVE_CONTAINER_RUNTIME:-}
if [[ -z "$runtime" ]]; then
	if command -v docker >/dev/null 2>&1; then
		runtime=docker
	elif command -v podman >/dev/null 2>&1; then
		runtime=podman
	else
		fail "no container runtime found; install Docker or Podman (with a Compose provider) or set ARCHIVE_CONTAINER_RUNTIME."
	fi
fi
command -v "$runtime" >/dev/null 2>&1 || fail "ARCHIVE_CONTAINER_RUNTIME=$runtime is not on PATH."
"$runtime" compose version >/dev/null 2>&1 || fail "$runtime has no Compose provider; install docker-compose or podman-compose."

compose() { "$runtime" compose --project-name "$project" -f "$archive/compose.yaml" "$@"; }

# All Rust commands run from backend-rs so the relative paths in dev.env
# (for example ARCHIVE_STATIC_ROOT) resolve, mirroring the documented manual
# startup. dev.env holds plain KEY=value lines; hyphenated names such as the
# connection strings cannot be shell assignments, so the lines go to `env`
# (the same mechanism the CI release workflow uses). dev.env values win over
# inherited environment variables.
envargs=()
load_env() {
	local line
	envargs=()
	while IFS= read -r line; do
		line=${line%$'\r'}
		[[ -z "$line" || "$line" == \#* ]] && continue
		envargs+=("$line")
	done < "$archive/dev.env"
}

run_server() {
	load_env
	cd "$workspace"
	env "${envargs[@]}" cargo run --locked -p archive-server -- "$@"
}

# Service names mirror compose.yaml's services; container ids resolve through
# compose (the per-container service label) so a rename there cannot silently
# break the wait. An unknown or absent service yields no id and counts as
# missing, like before.
services=(postgres azurite mailpit otelcol)

# The scratch collector image ships no shell or HTTP client, so no in-container
# probe can call its health_check extension on 13133; compose's healthcheck can
# only re-validate the configuration. The wait therefore accepts plain
# "running" for the collector and "healthy" for the three data services.
required_status() {
	case $1 in
	otelcol) echo running ;;
	*) echo healthy ;;
	esac
}

container_id() {
	local id
	while read -r id; do
		[[ "$("$runtime" inspect -f '{{index .Config.Labels "com.docker.compose.service"}}' "$id" 2>/dev/null)" == "$1" ]] && { echo "$id"; return; }
	done < <(compose ps -q)
}

health() { "$runtime" inspect -f '{{.State.Health.Status}}' "$1" 2>/dev/null || echo missing; }

# Fills the global `pending` list (service=status); an empty list means ready.
collect_unhealthy() {
	local service id status want
	pending=()
	for service in "${services[@]}"; do
		status=missing
		want=$(required_status "$service")
		id=$(container_id "$service")
		if [[ -n "$id" ]]; then
			if [[ "$want" == running ]]; then
				status=$("$runtime" inspect -f '{{.State.Status}}' "$id" 2>/dev/null || echo missing)
			else
				status=$(health "$id")
			fi
		fi
		[[ "$status" == "$want" ]] || pending+=("$service=$status")
	done
}

wait_healthy() {
	local deadline=$((SECONDS + timeout))
	while true; do
		collect_unhealthy
		if ((${#pending[@]} == 0)); then
			echo "PASS dev stack ready: ${services[*]}."
			return 0
		fi
		if ((SECONDS >= deadline)); then
			fail "stack not ready after ${timeout}s (${pending[*]}); see '$runtime compose --project-name $project logs'."
		fi
		echo "Waiting for the stack: ${pending[*]}"
		sleep 3
	done
}

require_stack() {
	collect_unhealthy
	((${#pending[@]} == 0)) || fail "local stack is not ready (${pending[*]}); run dev.sh up first."
}

# The pinned PDFium library path is machine-specific (an absolute native
# library path), so dev.env ships it commented out; extraction and reading
# refuse to configure without it. Say so once instead of a later obscure error.
pdfium_notice() {
	if grep -Eq '^Archive__Pdfium__LibraryPath=' "$archive/dev.env" || [[ -n ${Archive__Pdfium__LibraryPath:-} ]]; then
		return 0
	fi
	echo "dev.sh: Archive__Pdfium__LibraryPath is unset; PDF extraction and Lesungen refuse to run until the pinned library is configured — see dev.env's PDFium recipe." >&2
}

case $1 in
up)
	compose up -d
	wait_healthy
	pdfium_notice
	# Same one-time local setup the AppHost performed at startup: the private
	# archive-assets container, the configured queues and the emulator CORS rule.
	run_server --initialize-local-storage
	;;
migrate)
	require_stack
	run_server --migrate
	;;
worker)
	case ${2:-} in
	smoke) worker_arg=--worker-smoke ;;
	extract) worker_arg=--extract-queue
		needs_pdfium=1 ;;
	read) worker_arg=--read-queue
		needs_pdfium=1 ;;
	transcode) worker_arg=--transcode-queue ;;
	papierkorb-cleanup) worker_arg=--cleanup-papierkorb ;;
	*) usage >&2
		exit 2 ;;
	esac
	require_stack
	if [[ ${needs_pdfium:-} ]]; then
		pdfium_notice
	fi
	run_server "$worker_arg"
	;;
down)
	# Keeps the named volumes on purpose; delete them explicitly for a fresh start.
	compose down
	;;
esac
