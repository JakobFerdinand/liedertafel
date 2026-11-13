# Lied draft reading (#230)

Accepted base `ed710631694574b32776db7a7f35a295ac179201` (#229). Independent
review/integration acceptance remains parent-owned. Frozen C#/tests/frontend,
infrastructure, workflows and image are unchanged. #231 owns programme additions.

## HTTP and reader contracts

`POST /api/lied-lesungen` uses current membership/editor/CSRF/no-store. Order:
access → form → required string Guid/positive Int32 → Enabled → first named file
length/signature → owned claim → reading. Filename/part type never prove PDF.
Optional invalid/unknown/foreign Datei means **stateless**, not foreign mutation
or registry cancellation. Nil Guid remains parseable. No JSON identity binder.

Shared `reading/http.rs` uses Axum multipart and already-locked MIME parsing.
Names accumulate case-insensitively, nonempty scalar values join with commas,
first named file wins. URL-encoded forms count as form content; a valid identity
without file reaches422. Native **30,000,000-byte whole-body** envelope remains
distinct from Reading32MiB and Extraction64MiB. Boundary128,1024 sections,
headers16/16KiB and URL key2048/value4MiB precede domain validation. Multipart
text uses charset decoding. Malformed forms return controlled JSON400 rather
than framework exception payloads; native oversized bodies return413. TestServer
does not establish the native Kestrel envelope.

`tests/oracles/LesungFormOracle.cs` executes real ASP.NET ReadFormAsync,
StringValues and BCL Int32 in en-US:150 observations over both form types.
Signs/leading zeroes/ASCII numeric whitespace/source width/NUL ordering remain.
URL percent-decoded NUL fails before Int32; multipart retains its legacy tail
NUL tolerance. Existing independent139-case CLR Guid-string parser is reused.

`DraftReader` extracts embedded text first with Extraction bounds. Only NoText
renders first `min(3,ImagesPerCall,nativePages)` pages. The **whole requested
batch** must fit the shared estimator before rendering: worker adaptive batching
must not silently omit draft pages. Shared0600/flushed `Spool::from_bytes` and
PdfEngine/PdfPageRenderer retain native ownership through cancellation.
Deterministic facts win; only remaining code-owned fields are requested. Shared
clean/quote/every-lyric-line checks apply; verified **uncertain** suggestions remain
`sicher:false`. Unknown/unrequested/duplicate answers drop, consuming even an
invalid first occurrence. Fixed form order excludes voice_label/occasion. Empty
transcription yields empty suggestions. No catalogue writer/job/upload or
form/editor-mark mutation is reachable.

## Durable and instance-local arbitration

Claim takes short parent/Datei key-share locks then conditional INSERT/ON CONFLICT.
Equal/older numbers answer400; only higher numbers reset `liest`. PostgreSQL
arbitrates first-insert races without broad exception retries or provider-time
locks. Terminal UPDATE compares exact draft/Datei/request/`liest` **in SQL**;
removed/superseded targets answer404 and cannot be resurrected. These CAS guards
strengthen the frozen EF read-then-save race, not distributed immediate stopping
or exactly-once charging. Outcomes alone persist: success fields,429 unavailable,
other material/input/provider/cancellation failures retryable. Existing five-minute
interrupted projection and form-save preservation remain unchanged.

- Stateless work: HTTP handler-drop guard + host signal; no registry dedup solely
  for echoed browser identities.
- Owned persisted work: detached tracked consumer + instance-local registry,
  post-commit discard/removal, higher request, three-minute deadline and host
  signal. **Not** tab disconnect. Cleanup removes only its own handle; delayed old
  registration cannot cancel newer work. Registration precedes durable recheck,
  closing the removal-before-registration gap without spending.
- Other replicas may run to their own limit; conditional SQL refuses stale stores.

Main cancels `Origin::shutdown` **before Axum wait**. Afterwards
`Origin::drain_readings` joins whole consumer/native/store tasks, THEN Gateway
accounting, THEN terminal telemetry. Model-capable hosts use checked three-signal
finite flush/shutdown and fail nonzero on accounting/flush failure; the watchdog
covers this sequence. Dependency-free skeleton hosts retain their prior
best-effort collector control. Never globally drain Gateway per request.
Unconfigured web delivery now uses the central scripted scan delivery, still
below real Gateway/PostgreSQL. #231 alone extends its programme recognition.

## Frozen suite map

| Frozen source/scope | Rust public seam |
| --- | --- |
| Entire `LiedLesenApiTests.cs` | `lied_lesen_api`: text/scan/identity/wire mapping, analyzer/uncertain/quotes/duplicates/lyrics, cap/input/material/access/current membership/CSRF, order/form/native-envelope controls |
| `LiedEntwurfApiTests.cs` reading methods238–520 | `lied_entwurf_reading`: outcome/resume/no form mutation, late discard/remove, supersession/higher retry, real notifications/equal first claims/budget/stale/foreign/stateless, actual three-minute limit and locked stale terminal UPDATE |
| Same file persistence/validation | Existing #224 `lied_entwurf_api`, inherited regression, not re-claimed translation |
| Actual HTTP disconnect | `lied_reading_lifetime`: real native TCP/Router/Gateway/PG/PDF, stateless cancellation/full reservation versus persisted completion/GET resume |
| Product terminal telemetry | Ordinary `lied_reading_process`: actual product SIGTERM fails nonzero on terminal flush refusal; inherited `process_startup` skeleton remains unchanged |
| Native unchanged UI/model HTTP/host | `tests/browser/lied-reading-native.cjs` + offline-only `examples/lied-reading-native.rs`: real sign-in/CSRF, tab close/resume,30MB envelope, both raw-socket lifetimes, discard/both held-model shutdowns, failed settlement/nonzero/conservative reservation, all OTLP signals/private terminal replay |

The example permits only local `test230_lied_*` DB, loopback HTTP and local SMTP
with a fixed synthetic credential. It serves product router/readers/registry and
shared drain, not a product command or hosted-token path. Generated DBs remain
for audit. Synthetic100/20 usage is fixture delivery, not measured OCR quality.

## Reproduce and qualifications

```sh
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
cargo test -p archive-server --locked --test lied_lesen_api --test lied_entwurf_reading --test lied_reading_lifetime -- --ignored
cargo build --workspace --examples --locked
# ALL inherited explicit SQL/schema/private Blob/Queue/native PDF gates apply.
node tests/browser/lied-reading-native.cjs
node --test tests/browser/telemetry-*.test.cjs
```

Native env adds `ARCHIVE_LIED_READING_PROBE` for the built offline example.
Dedicated230 PG55432/Azurite51032/52032/53032 stay separate from231. Accepted
immutable OpenSSL3.5.9/PDFium8138/browser/static files are provenance checked.
Source-neutral external capture-root relocation moves inherited hardcoded paths
to230's private durable root; scanner/assertions remain unchanged. Existing
versions/checksums remain pinned; only feature-required package/edge additions
for Axum multipart/image PNG-WebP and direct already-locked MIME occur.

Final source pins/counts/logs are recorded only after combined jobs complete in
durable `target/agent-218/evidence/230-report.md`; pending gates are not passes.
Initial native fixture errors (missing SDK choice index; wrong SQL table spelling)
and a real **full-page-navigation** failure remain retained. Navigation left the
unchanged browser marker failed while server outcome was read, suppressing its
same-request suggestions. Final **actual tab-close** is a distinct passing
journey, not erasure of that failure or a claim of identical frozen-.NET failure.

Inherited passkey/member browser failures, UNCLASSIFIED historical redaction,
video FK refusal, historical migration equivalents, conditional budget/current
price/unknown-ACK/not-exactly-once notices, native cooperative cancellation and
decoded-expansion limits remain unwaived. Hosted identity/storage/mail, live AI,
private inputs, image/AppHost/cutover and whole-integration independent review
stay separately gated. No push/tracker/integration-main merge/deployment/cleanup.
#232 waits for independently accepted230 **and**231.
