# Native telemetry detector fixtures

`official-schema.json` is a **test-only derived reflection descriptor**, not
production codegen. Source: OpenTelemetry proto v1.9.0, commit
`a8951735f7801e8adfaec5c0ace9262771cfec6e`, Copyright OpenTelemetry Authors,
Apache-2.0 (`LICENSE`). `provenance.json` records every input SHA256 and the
generator dependency version. The derivative represents proto definitions as
JSON reflection data without source comments; no application build invokes the
generator or bypasses codegen.

Install the pinned decoder outside the checkout (alongside existing native
Playwright/pg/Azure fixture dependencies), then set `NODE_PATH`:

```bash
npm install --prefix /tmp/opencode/liedertafel-221-browser protobufjs@8.8.1
export NODE_PATH=/tmp/opencode/liedertafel-221-browser/node_modules
node --test tests/browser/telemetry-redaction.test.cjs
# Maintenance only: fetch official sources and regenerate committed fixtures.
node tests/telemetry/generate-schema.cjs --refresh
```

The scanner walks the official field schema and validates wire types rather than
using a decoder that silently drops unknown fields or overwrites duplicate
singular fields. Every string/bytes field, nested AnyValue, numeric attribute and
numeric measurement is inspected. Only individually named official **fixed64
clock fields**, and the anchored native console formatter clock (plain or its
observed ANSI format), are classified as metadata. A timestamp-looking attribute
or message remains payload. Trace/span IDs stay bytes, not decimal/hex strings
that could manufacture a numeric match; their actual bytes are still inspected.
Canonical Base64/Base64url/hex credential spellings and raw credential bytes are
checked. Numeric OTPs are not interpreted as encoded credentials; numeric payload
fields also detect an OTP whose leading zeroes were lost by numeric conversion.
Measurements/counts equal to an actual code remain conservatively suspicious,
not automatically safe metadata.

Each gRPC request/frame and each process/stdout/stderr stream keeps its boundary.
Chunks within one console stream are reassembled. Unknown fields/routes,
compression, bad wire types, invalid UTF-8 and truncated/empty captures fail
closed. This is deliberately stricter than a tolerant production collector.
Presence checks for required safe spans/metrics/logs remain in each native gate.

## Private replay and public-safe diagnostics

The native gates always create `/tmp/opencode/223-<gate>-telemetry-<random>/`
(0700). `replay.jsonl` (0600) synchronously appends exact request/console bytes and
the **actual synthetic marker values** as they are observed. It contains
credentials: keep it local, never commit, paste or attach it. `report.json`
(0600) is refreshed at the assertion and normal process exit, including failed
behavior assertions. Abrupt process termination may leave a partial capture;
replay requires a successful terminal completion record with the secrecy check
covering the last captured event. Partial or behavior-failed runs remain failures,
even if the available payload contains no matching secret.

Reports/assertion failures carry only marker type/static label/ordinal, channel,
request/frame or console stream/line, and indexed schema field path. They contain
no values, snippets, credential hashes, dynamic attribute keys or provider URLs.
The marker ordinal indexes the private replay's marker events. `metadata` records
classified clock matches. `legacyMatches`/`legacyLocations` apply the previous
raw-string substring approach in **capture arrival order** as a diagnostic
comparator, not an acceptance condition or reconstruction of lost original
route/command concatenation order. A raw-only match is not automatically proof
of a safe metadata collision; inspect its private field/frame evidence.

Replay locally, emitting only the value-free report (exit 1 on findings, protocol
errors or invalid replay):

```bash
node tests/browser/telemetry-capture.cjs /tmp/opencode/223-<gate>-telemetry-<random>/replay.jsonl
```

Browser gates observe real Set-Cookie and antiforgery responses, assert session
cookie/CSRF cookie/request-token coverage for each browser, and refuse partial
marker capture. The member gate additionally reads the actually registered
credential/public key before operator repair deletes it. No fixed login code,
auth bypass, frontend mock, product logging exception or frozen source edit is
needed for these detector controls.
