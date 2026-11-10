# Stored Noten reading (#229)

Base: accepted `fda8e31` (#228); branch `ticket/229-rust-reading-worker`.
Independent review/acceptance belongs to the parent. Frozen backend/tests,
frontend, infrastructure, workflows and Dockerfile are unchanged. No live AI,
hosted token, Neon, external mail or private document is used.

## Shared boundary and later-consumer handoff

`reading::ScanReadingModel` takes **only Gateway**. Each invocation starts a
fresh operation: `scan-transcription` / `archive-scan-transcriber`, or
`scan-fields` / `archive-score-fields`, with the persisted triggering account.
The account/operation/agent never enters provider JSON. The actual gateway owns
pricing, conditional reservation, current-price settlement, ACKs and supervised
drain; all qualifications in `ai-foundation-parity.md` remain applicable.

Reuse these seams in #230/#231:

- `PdfPageRenderer` and `extraction::pdf::{PdfEngine,Spool}`: the accepted explicit
  PDFium8125 wrapper/pinned8138 native ABI, bounded ETag-consistent Blob spool,
  Arc ownership through cancellation, native page count and bounded JPEG batches.
  Images are one-based; one native bitmap at a time, up to10 encoded pages,
  2048px hard maximum. Existing64MiB extraction and32MiB reading limits remain
  distinct. Native C calls are cooperative, **not preemptible**; decoded expansion
  and a stuck C call are not hard-contained. Image/AppHost packaging remains #235.
  Shared spool handoff now awaits Tokio `File::flush()` before returning a path:
  write_all alone can return with its actual file write still queued. The
  deterministic `pdf_spool` one-blocking-thread gate reproduced a returned path
  with zero visible bytes before the fix. This inherited extraction-spool race
  surfaced as an unreadable PDF in the reading lifecycle gate; it is not hidden
  by increasing a timer. Both extraction and reading use the corrected loader.
- `ReadField`, `clean_value`, `quote_is_verified`, UTF-16 `bound_text` and
  `strip_page_markers`. Certain values still need real quotes; every lyric line
  must occur in the text. The whole requested field list is code-owned.
- `ScanReadingModel::batch_size`: includes actual instruction/prompt UTF-16 cost
  and explicit image estimates before rendering. Gateway independently rechecks
  its current hard ceiling before reservation. A single image that cannot fit
  fails without a provider call; field text/output remain separately bounded.
- `scripted_scan_reader`: delivery-only, installed **below Gateway + real SQL
  ledger** by `Services::from_env_with_script`. It cannot reserve, write fields,
  mutate reading progress or call HTTP. Its synthetic usage is100 input/20 output
  per successful response, deliberately reported fixture usage, not a claim of
  measured model tokens. The images cannot actually be understood by the script.
  Programme-specific recognition/answers remain #231; extend this central script
  rather than copy a second scan provider. The default web foundation script stays
  fail-closed until an owning HTTP consumer installs its domain delivery.

No draft Lesung, Programm Lesung or chat endpoint is implemented here. Draft
suggestions must never call the stored catalogue writer. #230 owns persisted
registry cancellation on discard/remove/new request, not tab close. #231 owns
programme prompts/schema/matching and its own live evaluation. #232 owns chat.

`Origin::shutdown` is an optional host-signal seam, cancelled **before** Axum's
graceful wait. Main still awaits Axum requests **before** Gateway drain. There is
no current model-consuming HTTP endpoint: native finite-worker cancellation is
**not proof** of future HTTP shutdown. Each later consumer must link its owned
request/registry work to this signal, then prove actual disconnect versus
persisted survival, host shutdown, producer join, settlement and finite OTLP flush
at the HTTP/process boundary. No early HTTP lifetime framework was added.

## State, spending and field writes

Canonical `reading_jobs` and `extraction_jobs` retain the frozen schema. Missing
reading work deletes a stray message; terminal/budget-parked duplicates do no
work. Visible Lied-Noten only; trash beneath a trashed owner is excluded. A fresh
claim increments attempts and stamps **LastAttemptAt once**; completion/progress
change UpdatedAt only. Nonstale Running waits; strictly stale Running can be
retaken within the count. Extraction pending/missing retries; failed extraction
persists its German deterministic reason. Row or dequeue exhaustion fails.

Successful transcription appends bounded text and commits extraction source/text
and page progress in one short SQL transaction, guarded by both row versions.
Subsequent failure/cap uses that exact saved progress. There is still a crash
window between a paid response and its progress commit; this is **not** universal
exactly-once provider charging. No paid progress is recomputed merely because a
prompt version changed. Continued partial reads start after PagesRead.

Cap refusal parks WaitingForBudget, refunds this row attempt and ACKs; stray
messages are deleted. The dispatcher wakes bounded oldest parked rows only when
the real ledger's worst-case hint admits, then sweeps strictly stale/unstamped
Queued rows, oldest first. The hint is not an atomic reservation. Accepted sends
are never immediately retried because a stamp failed. All enqueue/dispatch stamps
CAS the caller/selected version, preserving the accepted #226 outbox fix.

Deadline recovery intentionally strengthens the frozen ambiguous `version >=
original lease` check: own progress advances a tracked lease, and recovery
requires **exactly that latest own version**. A newer foreign Running lease or a
terminal winner is not modified. Real-PG controls cover all three cases. This is
an explicit safety qualification, not a claim that C# already proved foreign-
lease safety.

`reading/fields.rs::Prefill` runs the accepted deterministic analyzer first, asks
only for still-empty automated fields in FieldCatalog order, ignores unknown or
unrequested fields and consumes each returned field only once. Certain+verified
values pass the sole automated writer. That writer reuses the catalogue's fixed
column allowlist/length validation and atomically tests current emptiness plus
visible source/owner in its SQL UPDATE. Unicode whitespace follows the .NET
whitespace set, not the database locale. Editor writes made while the provider
runs are never overwritten. Attribution is triggering account or creator fallback;
no provenance, proposals, review queue or new catalogue entity is stored.

## Literal finite commands and Enabled semantics

`--read-queue` ensures its Queue, sweeps, then uses the **existing shared Queue
drain**. `--dispatch-reading` ensures/sweeps and invokes deterministic cost notices
without a model. Maintenance returns before dependency work/ensure/notices.
Missing consumer configuration fails; an unconfigured dispatcher sender leaves
work unstamped. Provider failures are not queue-empty success. PDFium startup
configuration is checked before durable worker attempts. Gateway drain is outside
the command cancellation future, before pool close and checked finite OTLP flush.

Enabled=false refuses **new handovers/actions**, hides state, and extraction
creates no new reading row. As in the frozen worker/Program, it is **not an
existing-work spending kill switch**: already-queued reading can still run.
Maintenance is the spending stop. HTTP retry/continue reuse current membership,
CSRF, CLR path parsing, no-store and bodyless binding. They retain missing-file,
non-score, extraction-pending, live-Running, partial/full-text and terminal state
semantics. Only an actual PK_reading_jobs collision gets the bounded insert retry;
other SQL errors surface.

## Oracle and per-file ownership

Rust tests below are under `crates/archive-server/tests`.

| Frozen file | Rust owner and evidence |
| --- | --- |
| `ReadingWorkerTests.cs` | `reading_worker`: actual SQL/gateway/ledger, generated raster PDFs, deterministic/no-call path, only-empty/late-editor writes, malicious/unrequested/duplicate/uncertain/invalid quote/value controls, partial/resume, real cap/no churn/next-month wake and exact old saved usage, transient paid-page reuse, attempts/material/extraction/visibility matrix. Existing extraction handoff/Enabled and Queue drain gates are inherited, not duplicated. |
| `ReadingApiTests.cs` | `reading_api`: authenticated raw HTTP, state/source/REST nulls/no-store, roles/CSRF/disabled, retry/continue/material/full text, missing/pre-existing/insert-race rows, malformed body ignored, removed proposal/provenance routes. `reading_dispatch` exercises real selected/caller CAS send barriers and stamp failure. |
| `ReadingPostgresLiveTests.cs` CURRENT cases | `reading_postgres`, `reading_worker`, `reading_dispatch`, `reading_lifecycle`: real canonical constraints/FKs and production gateway/ledger SQL, budget sweep/progress. No current SQL waiver. |
| Same file, historical rekey/drop case | Explicit approved frozen-head adoption/preservation + older-history rejection in `historical_baseline`; obsolete transformation **NOT executed by Rust**. See `historical-migration-disposition.md` for the exact case/frontier and separately executed constraints. |
| `PdfPageRendererTests.cs` | `pdf_page_renderer`: actual native count, requested/clamped pages, JPEG1000px, dark drawn raster content, corrupt input/cancellation/spool cleanup. `pdf_spool` adds a deterministic real-filesystem queued-write handoff regression. Accepted `pdf_text_extractor`/extraction spool tests still run after sharing the loader. |
| `ScanPromptTests.cs` | `scan_prompt`: exact actual frozen agent schema/prompt/output-bound capture, catalog bullets/version, UTF-16/value/quote rules. `tests/oracles/ScanReadingOracle.cs` executes the unchanged frozen agent/Gateway with fake delivery/budget ONLY for capturing requests, not as reading-domain or SQL acceptance. External project `/tmp/opencode/229-oracle/Oracle.csproj`; log `229-scan-oracle.log`. Committed schema/field-prompt fixtures byte/JSON-match that runtime output. No credential/socket exists in this oracle. Frozen NuGet/compiler warnings remain warnings, not an audit pass. |
| `ScanReadingEvaluationTests.cs` scripted | `scan_reading_evaluation`: generated1/3/5-page corpus through full worker/native renderer/Gateway/real ledger/field writer; per-document correctness and cost, explicit applied-wrong/missing/unchecked negative controls. These are reported outcomes, not extra test-function counts. |
| Same file, `ScanReadingEvaluationLive` | Runtime exact three-value absent/whitespace gate BEFORE provider/fixture access. Configured unpriced/unusable deployment or bad/missing/empty corpus fails, not skips. Optional report/price names preserved; override strings reuse the accepted invariant AI configuration Decimal converter via `Price::from_configuration`, not Rust Decimal FromStr or the frozen evaluation's ambient-culture Double parser. Pure no-I/O controls reject underscores/grouping/nonfinite/nonpositive/underflow and pin exponent/Unicode-trim/ties-to-even behavior. This is an explicit test-configuration parity qualification, not full frozen Double grammar equivalence. No live path executed or private corpus read here. |
| Worker cancellation/finite telemetry | `reading_lifecycle`, `reading_process`, `reading-native.cjs`, offline-only `reading-cancel-native` example: exact latest lease, claim timestamp, parent cancellation/no late writes/full reservation settlement; literal commands/native process signals, unchanged UI/search, W3C and all three actual OTLP signals, terminal private capture/replay. This is executable acceptance, not image/AppHost #235 or future model-HTTP shutdown evidence. |

## Reproduce and evidence

Use dedicated disposable PostgreSQL17, private Azurite3.35 with matching
inside/outside Blob port, static OpenSSL3.5.9, pinned PDFium8138 from the accepted
extraction guide, `--locked` and ticket-isolated target. #229 uses PG55429,
Blob51030/Queue52030/Table53030 (avoiding the retained #228 native ports).

```sh
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
cargo build --workspace --examples --locked
cargo test -p archive-server --locked --test reading_worker --test reading_api --test reading_dispatch --test reading_lifecycle --test reading_postgres --test pdf_page_renderer -- --ignored
cargo test -p archive-server --locked --test scan_reading_evaluation -- --ignored --nocapture
cargo test -p archive-server --locked --test historical_baseline -- --ignored
# ALL inherited explicit SQL/schema/private Blob/Queue/PDF gates still required.
node tests/browser/reading-native.cjs
node --test tests/browser/telemetry-*.test.cjs
```

The native journey also needs `ARCHIVE_READING_CANCEL_PROBE` pointing to the
built `reading-cancel-native` example; its guard accepts only its local
`test229_reading_*` fixture database and loopback fake credential/provider.
It is not a product command or a route to a hosted model.

Final sequential validation counts and exact source pin will be recorded here
and in `/tmp/opencode/liedertafel-218-229-report.md` before handoff. Inherited known
frozen passkey/member browser failures, original uncaptured UNCLASSIFIED redaction
event, video FK refusal and conditional budget/current-price/unknown-ACK/notice
qualifications are retained; no later clean run reclassifies them. Hosted identity,
live model/API acceptance, actual image/AppHost, deployment/cutover remain unrun.
