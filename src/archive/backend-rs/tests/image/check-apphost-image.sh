#!/usr/bin/env bash
set -euo pipefail

runtime=${ASPIRE_CONTAINER_RUNTIME:-podman}
tag=${Archive__TestImageTag:?Set Archive__TestImageTag to the local candidate tag}
version=${ARCHIVE_EXPECTED_IMAGE_VERSION:?Set ARCHIVE_EXPECTED_IMAGE_VERSION to the build version}
image=localhost/liedertafel-archive:$tag

# Loader, linkage, distro codecs, notices and nonroot scratch. No host mounts
# or network access. The cross-stack tests separately use PDFium on real PDFs.
"$runtime" run --rm --pull=never --network=none --cpus=2 --memory=512m --read-only \
  --tmpfs /scratch-cap:rw,noexec,nosuid,size=128m,mode=1777 \
  --entrypoint sh "$image" -ec '
  test "$(id -u)" = 10001
  test "$(stat -c %u /app/scratch)" = 10001
  test "$(stat -c %a /app/scratch)" = 700
  fail() { echo "$*" >&2; exit 1; }
  if test -w /app/wwwroot; then fail "/app/wwwroot must not be writable"; fi
  umask 077
  export TMPDIR=/scratch-cap/private
  mkdir "$TMPDIR"
  test "$(stat -c %a "$TMPDIR")" = 700
  file=$(mktemp)
  test "$(stat -c %a "$file")" = 600
  rm "$file"
  if command -v node; then fail "Node must not be present in the runtime image"; fi
  if command -v dotnet; then fail ".NET must not be present in the runtime image"; fi
  reject_linkage() {
    if grep -E "$1" "$2"; then
      fail "Unexpected native linkage in $2"
    else
      status=$?
      if test "$status" -ne 1; then exit "$status"; fi
    fi
  }
  ldd /app/archive-server >"$TMPDIR/archive.ldd"
  cat "$TMPDIR/archive.ldd"
  reject_linkage "lib(ssl|crypto)\.so|not found" "$TMPDIR/archive.ldd"
  ldd /app/native/libpdfium.so >"$TMPDIR/pdfium.ldd"
  cat "$TMPDIR/pdfium.ldd"
  reject_linkage "not found" "$TMPDIR/pdfium.ldd"
  grep -aq "OpenSSL 3.5.9" /app/archive-server
  echo "655e831208783f38dc187ad424bdfdae39858cfbfcf3a9a2cb9dd0694b5abcad  /app/native/libpdfium.so" | sha256sum -c -
  test -s /usr/share/doc/archive-native/pdfium/LICENSE
  test -s /usr/share/doc/archive-native/pdfium/licenses/harfbuzz.txt
  test -s /usr/share/doc/archive-native/OpenSSL-LICENSE.txt
  test -s /usr/share/doc/archive-native/dotnet-runtime-LICENSE.txt
  find /usr/share/doc/archive-native/mpl-source -name "*-0.5.5.crate" >"$TMPDIR/mpl-source"
  test "$(wc -l <"$TMPDIR/mpl-source")" = 4
  test -s /usr/share/doc/ffmpeg/copyright
  test -s /usr/share/doc/libx264-164/copyright
  dpkg-query -W -f="\${Package} \${Version} \${source:Package} \${source:Version}\n" ffmpeg libx264-164
  ffmpeg -v error -f lavfi -i color=s=64x36:d=1 -f lavfi -i sine=d=1 \
    -c:v libx264 -c:a aac -shortest "$TMPDIR/codec.mp4"
  ffprobe -v error -show_entries stream=codec_name -of csv=p=0 "$TMPDIR/codec.mp4" >"$TMPDIR/codecs"
  grep -qx h264 "$TMPDIR/codecs"
  grep -qx aac "$TMPDIR/codecs"
  ffmpeg -v error -xerror -i "$TMPDIR/codec.mp4" -f null -
  du -sh "$TMPDIR"
  df -h "$TMPDIR"
'

# A bounded local smoke, not a capacity claim. This is the product executable
# with a clean static export and no runtime credential or dependency injection.
container=$("$runtime" run -d --pull=never --cpus=2 --memory=512m \
  -e Authentication__PasskeyRelyingPartyId=archive.invalid \
  -e Authentication__PasskeyOrigins__0=https://archive.invalid \
  -e Mail__Provider=Azure -e Mail__AzureEndpoint=https://mail.invalid \
  -p 127.0.0.1::8080 "$image")
trap '"$runtime" rm -f "$container" >/dev/null 2>&1 || true' EXIT
address=$("$runtime" port "$container" 8080/tcp)
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

for _ in range(50):
    try:
        assert request("/alive")[0] == 200
        break
    except (OSError, http.client.HTTPException):
        time.sleep(0.1)
else:
    raise SystemExit("The archive image never became live")

status, content_type, body = request("/system/status/")
assert status == 200 and content_type.startswith("text/html") and b"Systemstatus" in body
asset = re.search(rb'src="(/_next/[^"]+\.js)"', body)[1].decode()
status, content_type, body = request(asset)
assert status == 200 and "javascript" in content_type and body
status, content_type, body = request("/api/build")
assert status == 200 and content_type.startswith("application/json")
assert json.loads(body) == {"application": "Liedertafel Archiv", "version": sys.argv[2], "development": False}
status, content_type, body = request("/api/no-such-rust-image-route")
assert status == 404 and content_type.startswith("application/problem+json") and json.loads(body)
assert json.loads(request("/api/maintenance")[2])["maintenance"] is False
assert request("/api/dev/database")[0] == 404
PY

"$runtime" stats --no-stream --format '{{.CPUPerc}} {{.MemUsage}}' "$container"
"$runtime" kill --signal TERM "$container" >/dev/null
exit_code=$(timeout 15 "$runtime" wait "$container")
test "$exit_code" = 0
"$runtime" rm "$container" >/dev/null

# The published release observes the same public routes with the flag set.
container=$("$runtime" run -d --pull=never --cpus=2 --memory=512m \
  -e Authentication__PasskeyRelyingPartyId=archive.invalid \
  -e Authentication__PasskeyOrigins__0=https://archive.invalid \
  -e Mail__Provider=Azure -e Mail__AzureEndpoint=https://mail.invalid \
  -e Archive__MaintenanceMode=true -p 127.0.0.1::8080 "$image")
address=$("$runtime" port "$container" 8080/tcp)
base="http://$address"
curl --silent --show-error --fail --retry 20 --retry-connrefused --retry-delay 1 "$base/alive" >/dev/null
curl --silent --show-error --fail "$base/api/maintenance" | jq -e '.maintenance == true' >/dev/null
curl --silent --show-error --fail "$base/api/build" | jq -e --arg version "$version" '.version == $version and .development == false' >/dev/null
body=$(mktemp)
trap 'rm -f "$body"; "$runtime" rm -f "$container" >/dev/null 2>&1 || true' EXIT
[[ $(curl --silent --show-error -o "$body" -w '%{http_code}' "$base/api/antiforgery") == 503 ]]
grep -Fiq wartung "$body"
"$runtime" stop "$container" >/dev/null

# Pause before opening any dependency: use no network and no configuration.
for command in --extract-queue --read-queue --dispatch-reading --transcode-queue --dispatch-videos; do
  "$runtime" run --rm --pull=never --network=none -e Archive__MaintenanceMode=true "$image" "$command"
done
# The frozen production command also refuses outside Development, despite the
# existing schedule. Pin this as a limitation, not a successful hosted sweep.
status=0
"$runtime" run --rm --pull=never --network=none -e Archive__MaintenanceMode=true \
  "$image" --cleanup-papierkorb >"$body" 2>&1 || status=$?
test "$status" = 1
grep -Fq 'Local service commands require Development.' "$body"
printf '%s\n' 'PASS local amd64 image linkage, native codecs, private scratch, export and SIGTERM'
