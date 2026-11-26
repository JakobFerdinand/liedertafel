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
            wait until healthy, then run the one-time local storage
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

containers=(archive-postgres archive-azurite archive-mailpit archive-otelcol)

health() { "$runtime" inspect -f '{{.State.Health.Status}}' "$1" 2>/dev/null || echo missing; }

unhealthy() {
	local name status
	pending=()
	for name in "${containers[@]}"; do
		status=$(health "$name")
		[[ "$status" == healthy ]] || pending+=("$name=$status")
	done
}

wait_healthy() {
	local deadline=$((SECONDS + timeout))
	while true; do
		unhealthy
		if ((${#pending[@]} == 0)); then
			echo "PASS dev stack healthy: ${containers[*]}."
			return 0
		fi
		if ((SECONDS >= deadline)); then
			fail "stack not healthy after ${timeout}s (${pending[*]}); see '$runtime compose --project-name $project logs'."
		fi
		echo "Waiting for healthy containers: ${pending[*]}"
		sleep 3
	done
}

require_stack() {
	unhealthy
	((${#pending[@]} == 0)) || fail "local stack is not healthy (${pending[*]}); run dev.sh up first."
}

case $1 in
up)
	compose up -d
	wait_healthy
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
	smoke | extract | read | transcode | papierkorb-cleanup) require_stack ;;
	esac
	case ${2:-} in
	smoke) run_server --worker-smoke ;;
	extract) run_server --extract-queue ;;
	read) run_server --read-queue ;;
	transcode) run_server --transcode-queue ;;
	papierkorb-cleanup) run_server --cleanup-papierkorb ;;
	*) usage >&2
		exit 2 ;;
	esac
	;;
down)
	# Keeps the named volumes on purpose; delete them explicitly for a fresh start.
	compose down
	;;
esac
