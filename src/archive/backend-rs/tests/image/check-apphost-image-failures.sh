#!/usr/bin/env bash
# Harmless negative controls against the same image and container shell as the smoke.
set -euo pipefail
export REAL_RUNTIME=${ARCHIVE_CONTAINER_RUNTIME:-podman}
checker="$(dirname "$0")/check-apphost-image.sh"
dir=$(mktemp -d)
trap 'rm -rf -- "$dir"' EXIT
export ARCHIVE_CONTAINER_RUNTIME="$dir/runtime"
cat >"$ARCHIVE_CONTAINER_RUNTIME" <<'BASH'
#!/usr/bin/env bash
set -euo pipefail
# Only run the isolated shell check. Reaching the HTTP phase is a test failure.
[[ " $* " == *" --network=none "* ]] || exit 99
args=("$@")
case "$CONTROL" in
  writable)
    stub='test() { if [ "$*" = "-w /app/wwwroot" ]; then return 0; elif [ "$*" = "! -w /app/wwwroot" ]; then return 1; else command test "$@"; fi; }' ;;
  node)
    stub='command() { [ "$*" = "-v node" ]; }' ;;
  openssl)
    stub='ldd() { if [ "$1" = /app/archive-server ]; then echo "libssl.so => fixture"; else /usr/bin/ldd "$@"; fi; }' ;;
  pdf-missing)
    stub='ldd() { if [ "$1" = /app/native/libpdfium.so ]; then echo "libfixture.so => not found"; else /usr/bin/ldd "$@"; fi; }' ;;
  archive-ldd|pdf-ldd)
    target=/app/archive-server
    [[ $CONTROL != pdf-ldd ]] || target=/app/native/libpdfium.so
    stub="ldd() { if [ \"\$1\" = $target ]; then echo 'fixture: native tool failed'; return 42; else /usr/bin/ldd \"\$@\"; fi; }" ;;
  grep-error)
    stub='grep() { case "$*" in *.ldd) return 2 ;; *) /usr/bin/grep "$@" ;; esac; }' ;;
  *) exit 98 ;;
esac
last=$((${#args[@]}-1))
args[last]="$stub
${args[last]}"
exec "$REAL_RUNTIME" "${args[@]}"
BASH
chmod +x "$ARCHIVE_CONTAINER_RUNTIME"

for CONTROL in writable node openssl pdf-missing archive-ldd pdf-ldd grep-error; do
  export CONTROL
  status=0
  bash "$checker" >"$dir/output" 2>&1 || status=$?
  case "$CONTROL" in
    writable|node|openssl|pdf-missing) expected=1 ;;
    archive-ldd|pdf-ldd) expected=42 ;;
    grep-error) expected=2 ;;
  esac
  if [[ $status != "$expected" ]]; then
    cat "$dir/output" >&2
    echo "$CONTROL: expected exit $expected, got $status" >&2
    exit 1
  fi
  echo "PASS fail-closed image control: $CONTROL (exit $status)"
done
