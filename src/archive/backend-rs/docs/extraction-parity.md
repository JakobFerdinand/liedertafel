# Stored-Datei extraction (#226)

## Tested recording union — ready for independent rereview, not accepted

Integration `e851f42` (#227 tree `bd28dd9`) is merged into this branch as
`e4ca191`. Additive conflicts preserve both route registries and both sets of
raw binding shapes/rules. Recording visibility/lifecycle/lib/guides and extraction
main/hooks/dependencies are retained. Final implementation pin is **`13de7b4`** after
R1–R4 and the overlapping-credit follow-up. Fresh combined checks use226-owned providers.
The historical table at the bottom remains pre-union evidence, not union totals.
Independent review of original `99d92b8` is **COMPLETE/HOLD** with four
findings. This union is not accepted; fresh full gates and independent rereview
were required. Full gates below passed; independent rereview remains outstanding
before any acceptance or wave6 advance.

## Review remediation checkpoint

- R1: `935e660` ensures both finite command queues after maintenance and through
  cancellation. Actual absent private-Azurite dispatch test failed first
  (`dispatch must provision its initially absent queue`); full extraction native
  harness then passed, including absent/existing/paused/unconfigured/provider
  failure and SIGTERM controls. Logs: `/tmp/opencode/226-r1-{native-red,native-green}.log`.
- R2: `score_text_boundaries` first failed on 21 of 54 frozen observations,
  including all six reviewer cases. Explicit UTF-16 Letter/Nd lookaround predicates,
  non-consuming voice boundaries/optional-suffix fallback, bounded signature pair
  and 600-UTF-16-unit metronome prefix now agree on all 54. Empty-noise input retains
  the frozen empty-facts early return. Canonical clean-text SHA256 is checked too.
  `tests/oracles/ExtractionScoreOracle.cs` is run in external
  `/tmp/opencode/226-score-oracle/Probe.csproj` against the unchanged frozen DLL;
  compact output exactly matches committed `fixtures/226-score-bcl.jsonl`.
  `/tmp/opencode/226-r2-{red,green,fmt,clippy}.log`: red then four analyzer tests
  green; fmt and workspace/all-target clippy green. No new dependency/write path.
  Final boundary audit added two frozen overlapping-credit controls: rejecting
  `aText und Musik:` must still find the inner `Musik:` (and the inverse role).
  Both failed before the follow-up scanning fix, then all **56** observations and
  three original analyzer tests passed. `/tmp/opencode/226-r2-overlap-{red,green,
  fmt,clippy,fixture-comparison}.log`; generated56-row fixture byte-matches.
- R3: explicit pre-attempt versus counted-attempt save kind retains claim-time
  `LastAttemptAt` while `UpdatedAt` records completion. Real-PG/authenticated HTTP
  advancing-clock test first reproduced four seven-second timestamp shifts
  (row/dequeue exhaustion, each transient/deadline), then passed all six counted
  cases and the already-spent precheck control. All six worker tests pass;
  `/tmp/opencode/226-r3-{red,green,fmt,clippy}.log` retains exact evidence.
- R4: `extraction_outbox` executed four new failing real-SQL/provider-send-barrier
  tests (dispatcher, authenticated finalize/retry, generated native-PDF reading
  handoff). Each late stamp overwrote the newer row's reset enqueue state. The
  reviewer's original finding remains source-derived; these are NEW reproductions.
  Selected dispatcher versions, finalize's inserted version zero, retry's exact
  selected/reset/inserted version and reading's inserted version zero now guard
  stamps. Zero affected rows warn and do not resend; dispatch counts successful
  stamps only. All 15 extraction API/worker/outbox tests pass, including normal
  stamps, queued/reset/insert retry callers, terminal duplicates, finalize reentry
  and later dispatcher recovery. `/tmp/opencode/226-r4-{red,green,fmt,clippy}.log`.
  Source audit: frozen `ExtractionService.cs:57–74` uses EF identity resolution of
  the caller-tracked row; dispatcher `:161–197` retains its selected tracked row;
  extraction/reading model configurations mark RowVersion as concurrency token.
  Worker/retry explicitly bump it; stamp-only saves do NOT bump RowVersion or
  UpdatedAt. Rust preserves that rule; normal controls compare every row field.
  No transaction crosses external I/O and no atomic outbox guarantee is invented.
- Final combined validation below ran after all fixes; independent rereview has
  **not** run. Earlier pre-union/initial-union passes are not final acceptance.

Base: accepted integration `5c5611a`. Frozen C#, xUnit, frontend, infrastructure,
workflow and Dockerfile remain unchanged. No model is called. Image/AppHost
retargeting is #235; hosted identity/storage and cutover are separate gates.

## Final combined evidence (2026-11-10)

Executed on implementation **`13de7b47a970f1a1f2de4c04bdcdcb81d8656cf8`**, then
documentation-only changes. Latest integration `e851f42` re-merge check returned
`Already up to date.` Dedicated226 PG55426/Blob51026/Queue52026, isolated target,
static OpenSSL3.5.9, `--locked`, two Cargo jobs, no peer227 resources.

| Gate | Actual final result |
| --- | --- |
| Sequential fmt → all-target workspace clippy `-D warnings` → workspace tests → build → active cargo-deny → linkage | Pass; narrow dependency exceptions unchanged; embedded OpenSSL3.5.9, no dynamic libssl/libcrypto |
| Ordinary workspace | **65 passed /0 failed /192 ignored** |
| Every explicit combined ignored gate | **192 passed /0 failed /0 ignored**:184 SQL (including native-PDF spool/handoff),1 exact schema/adoption/grants,5 private Blob Azurite,1 private Queue Azurite,1 native PDF |
| #227 progressing-clock attribution | Both real-SQL/raw-HTTP tests pass within192; accepted production/tests/harness unchanged |
| Native auth/member/catalogue/upload/events/query/extraction/recordings | **8/8 pass**, production executable/unchanged UI/private226 providers; query differential36 observations |
| Private drained final capture replays | **8/8 pass**, findings/errors/legacyMatches empty;0700 directories/0600 artifacts |
| Structured telemetry detector/lifecycle | **110 passed /0 failed /0 skipped** |
| Scoped unchanged frozen .NET | **168 passed /0 failed /0 skipped**; existing compiler/NuGet warnings retained, not an advisory pass |
| Seven-spec unchanged mocked UI, desktop+mobile | **138 passed**; `extraktion`, `noten`, `lieder`, `auftritt-aufnahmen`, `auftritt-passagen`, `lied-historie`, `programm` |
| All unchanged frontend unit tests | **115 passed /0 failed /0 skipped** |
| External frozen analyzer oracle | **56 observations** and clean-text hashes byte-match committed fixture (one Rust boundary test, not56 tests) |
| External frozen recording oracle | **8 DTOs /106 serializer /6 rounding observations** equal accepted output excluding separate-worktree build MVID |

Final logs are `/tmp/opencode/226-union-final-{fmt,clippy,workspace,build,deny,
linkage,explicit,explicit-counts,detector,dotnet,frozen-browser,frontend-unit,
oracles,recording-oracle,integration-check}.log`, eight
`226-union-final-<native-name>.log` / `-replay.log`, and
`226-union-final-native-{results.json,evidence.log,summary.log}`. These are actual
combined counts, not sums of inherited duplicate gates. Native extraction freshly
re-executed absent/existing queue provision, maintenance non-provision,
unconfigured sender/consumer, bad-key failure, SIGTERM ensure cancellation,
native ABI/spool/attempt bounds, real reading notification and PDF-only UI search.

Preserved setup qualifications: first intermediate explicit run hit the harness
120s timeout and is incomplete; its unbounded-timeout repeat passed192, and the
final13de7b4 full repeat also passed192. Initial mocked-UI invocation had a wrong
external CLI path and ran no tests; corrected invocation and final repeat passed138.
These setup failures remain recorded; they are not product failures or passes.

### Retained qualifications

The accepted #227 timestamp/slot/CAS controls and native harness wait for
`Original für „Native Tonaufnahme“ gespeichert.` are unchanged from `e851f42`.
Its original `/tmp/opencode/223-recordings-native-telemetry-NQIgxq` capture remains
failed (`incomplete-or-failed-native-run`, zero secrecy findings), not a pass
retroactively manufactured by later clean runs. Known frozen real-member/passkey
browser failures remain failures and were not rerun by the scoped mocked selection;
the original uncaptured member-redaction ambiguity stays **UNCLASSIFIED**. Video-FK
refusal and historical migration-equivalent qualifications are unchanged.

Unrun here: actual image/AppHost packaging/roundtrip (#235), hosted Entra/RBAC,
production/Neon/deployment/cutover, real external mail, live AI and full reading
gateway/writer/progress suites (#229), video workers (#233). Cooperative C-call
cancellation and decoded-expansion limits below remain explicit limitations.

## Boundaries

- `--dispatch-extraction` sweeps Queued only, oldest first, with a missing or
  **strictly** stale enqueue stamp and bounded take. Send precedes stamp; accepted
  send/stamp failure never immediately resends. Unconfigured sending leaves rows
  unstamped. `--extract-queue` sweeps then drains; an unconfigured consumer fails.
  Maintenance stops before dependency work.
- Shared Queue drain snapshots settings, stops at the first empty receive,
  message/drain budget or maintenance, and handles serially with fresh SQL reads.
  Batch time starts before receive. Active deadline is min(message budget,
  receive visibility), **not** clamped to overall drain deadline. Unprocessed
  receipts get zero visibility; disposition failure does not suppress later
  releases. Parent cancellation propagates. No renewal heartbeat/poison queue.
- Official singular Queue1.1.0 shares Blob credentials, user-assigned identity,
  Shared Key policy, bounded retry/transport and emulator version. Plain UTF-8
  JSON is SDK XML-serialized, not Base64. Abandon sends **no body**, preserving
  the envelope. Missing provider receipt/id/count is not queue-empty success.
  Raw binding preserves typed duplicates/last values, exact camelCase envelope
  names and typed-JSON D Guid; string/path IDs use the shared CLR parser. W3C
  context is a remote Consumer parent, never baggage. Invalid trace context does
  not poison valid material. SDK/provider bodies, receipts and paths are filtered.
- Existing finalize hooks write canonical `extraction_jobs` in the Datei's
  transaction; bounded quiet enqueue follows commit. No parallel lifecycle,
  authentication, leases or search index. Canonical parent-aware visibility is
  used by workers. Terminal duplicates do not open bytes. Running work waits
  unless strictly stale. Exhausted stale rows fail without another attempt;
  queued oversize fails before Blob open, stale oversize counts its retake first.
  Transients persist Queued before abandon. Row **or** message exhaustion fails.
  Deadline recovery/completion require exact lease RowVersion; terminal winners
  acknowledge, newer/released leases are not overwritten. No transaction spans
  external PDF/Blob processing.
- Completed/NoText Lied-Noten may insert a queued reading row in the same
  completion transaction when enabled, then send after commit. Sonstiges never
  hands off. This is notification plumbing, not #229's gateway/spending/scan/
  automated field writing. Reading defaults mirror the frozen configuration:
  enabled in Development, disabled in Production, explicit environment override
  wins. Its configured initial page limit is retained. Both handoffs share the
  two-attempt quiet sender; dispatch deliberately does not retry a row immediately.
  Editor status/retry reuse membership/CSRF/no-store,
  bounded request-order IDs, explicit REST nulls, bounded insert recovery,
  live-running409 and terminal idempotence. Facts are read-time deterministic
  analysis, not catalogue writes. Completed text feeds existing shared search.

## Native PDFium, bounds and honest limitations

#229 shared-spool follow-up: the loader now lives in `extraction/pdf.rs` and is
shared with `reading::PdfPageRenderer`. A new actual filesystem scheduling gate
(`pdf_spool`) proved that Tokio write_all could return a path before its queued
write executed (zero visible bytes). `File::flush().await` now precedes native
handoff; it guarantees completed kernel-visible writes, not fsync durability.
Earlier accepted passes cover their observed inputs, not this newly exposed
scheduling race. See `reading-parity.md` for red/green and combined final gates.

Wrapper exactly `pdfium-render0.9.5`, defaults off, explicit
`pdfium_8125/thread_safe/image_025`. Current wrapper/API was rechecked at
crates.io. Native publisher's latest non-prerelease was rechecked2026-11-10:
`chromium/8138` (published2026-11-04). Local pinned bblanchon8138
Linux x64/glibc archive SHA256:
`d6b1e28950a0392590d79bbdbb7eb19686872b688f8a36f170acb19e41473113`.
Extracted `libpdfium.so` SHA256:
`655e831208783f38dc187ad424bdfdae39858cfbfcf3a9a2cb9dd0694b5abcad`.
The explicit8125 API binding is runtime-tested with this8138 library, not
inferred from symbol inspection. The initially tested8125 artifact is historical
evidence, not the final native selection. PDFium has no claimed LTS support promise.
Publisher is not Google/Foxit. #235 must carry native/third-party notices and
security-update ownership; Cargo advisories do not audit native C++. Cached7961
with8125 bindings is not the selected pairing;7881 symbol inspection was never
runtime proof.

`Archive__Pdfium__LibraryPath` is an explicit verified absolute library path;
binding is fallible/process-wide, no random discovery fallback. Worker startup
refuses a missing library or incompatible7961/8125 pairing before spending row
attempts; dispatch remains independent of PDFium.
`Archive__Pdfium__SpoolDirectory` selects existing writable scratch. ETag-pinned
Blob ranges request at most max+one bytes. Unique create-new0600 spools write at
most the byte cap and clean up after normal outcomes/cancellation. PDFium reads
a genuinely seekable local file; the remote forward stream is not seekable parity.

Defaults64MiB/500pages/100000UTF-16 units. Excluded pages are not opened; accepted
text is partial. Count is checked **before** `chars()` allocates its index list,
then bounded UTF-16 units are decoded (including actual native surrogate-index
pairs). Runtime fixtures preserve a supplementary character and check both sides
of its two-unit boundary. No page `.all()` followed
by truncation. Rendering uses an explicit2048px bounding square, aspect ratio
and per-page lifetime, only a narrow future reading seam.

Checks run before open, during spool, per page/character and after native calls/
disposal. Started native parsing is **not preemptible**. A blocking task keeps
async deadlines responsive; expired work cannot commit, and its spool remains
owned until native work ends. Decoded expansion and a stuck C call are not hard
memory/wall-clock-contained. Image capacity remains #235. PDFium is not PdfPig's
parser/layout; arbitrary malformed/layout byte equivalence is not established.
Only generated public synthetic PDFs are used, including the pypdf6.12.0 encrypted
blank page in `226-encrypted-pdf.base64` (public password/generation in its test).
No private PDF was processed.

## Per-file ownership

Rust paths below are `crates/archive-server/tests/` except the native harness.
Grouped assertions do not multiply reported test passes.

| Frozen file | Rust owner/evidence |
| --- | --- |
| `ExtractionApiTests.cs` | `extraction_api`, `extraction_outbox`: authenticated status/retry, bounds/order/nulls/facts, stale/running/terminal matrix, finalize outbox, quiet send/stamp failure/reentry, selected/caller CAS barriers and SQL dispatcher |
| `ExtractionWorkerTests.cs` | `extraction_worker`, `pdf_text_extractor`, `queue_drain`, `queue_azurite`: SQL attempts/exact-lease deadlines, generated native PDF and actual receipts. `tests/browser/extraction-native.cjs`: real finite processes/upload/search/dispatch/noText/remote-context/OTLP |
| `ScoreTextAnalyzerTests.cs` | `score_text_analyzer`, `score_text_boundaries`: glyph/syllable/heading/staff voice/key/signature/tempo/credit/copyright/noise/header bounds plus56 externally frozen boundary/UTF16 observations and clean-text hashes |
| `NotenTextSearchTests.cs` | inherited `noten_text_search`; native unchanged-UI upload→search finds PDF-only words |
| `LiedSucheQuellenTests.cs` | inherited `lied_suche_quellen`; new completion enters shared canonical visible SQL/search |
| `SearchAgreementTests.cs` | inherited `search_agreement`; actual chat adapter remains #232 |
| `FiniteJobsTests.cs`, `TelemetryTests.cs` | extraction native maintenance/finite completion/three-signal redaction here; full operator allowlist/cleanup #234, image/AppHost #235 |
| AppHost `ExtractionRoundtripTests.cs` | native executable/unchanged UI/private providers here; actual image/AppHost plus gateway/field prefill #235 after #228/#229 |
| `ReadingWorkerTests.cs`, `ReadingApiTests.cs`, `PdfPageRendererTests.cs` | #229 owns full scan/progress/budget/writer/render suites; queued handoff and bounded native render capability only here |

This is an ownership map, not a claim each frozen method or image/hosted gate
has passed. Final counts and explicit coverage gaps live in the #226 handoff.
Known passkey/member frozen-browser failures, video-FK refusal and original
uncaptured redaction ambiguity remain in the inherited parity documents.

## Reproduce

Use supported static OpenSSL3.5.9 (`auth-dependency-audit.md`), `--locked`, ticket-
isolated target and dedicated local PostgreSQL17/Azurite3.35. #226 uses PG55426,
Blob51026→51026, Queue52026→52026, unique databases/queues/containers. Matching Blob
inside/outside ports allow Azurite's server-side copy to reach itself.

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
cargo test -p archive-server --locked --test extraction_api --test extraction_worker --test extraction_outbox -- --ignored
cargo test -p archive-server --locked --test score_text_analyzer --test score_text_boundaries
cargo test -p archive-server --locked --test pdf_text_extractor --test queue_azurite -- --ignored
# Inherited explicit PG/schema/Azurite gates remain required.
node --test tests/browser/telemetry-*.test.cjs
node tests/browser/extraction-native.cjs
cargo deny --locked check
```

Native harness requires `ARCHIVE_BINARY`, current unchanged static export in
`ARCHIVE_STATIC_ROOT`, `ARCHIVE_TEST_POSTGRES`, `ARCHIVE_TEST_AZURITE`,
`ARCHIVE_TEST_QUEUE`, explicit PDFium path, external `NODE_PATH` and optionally
`ARCHIVE_CHROMIUM`. Node Queue SDK12.32.0 uses a version-pinning test pipeline;
an ignored `serviceVersion` option does not pin its generated client's version.
Existing terminal collector-drain/child-closed/fail-closed capture is reused.
A stale export calling `/api/songs` is not the current frontend contract.

## Historical original-candidate verification (99d92b8)

Latest integration was re-merged/check-confirmed at `5c5611a` before the final
sequential format → clippy → workspace → active-audit run. Every Cargo command
used the scoped static OpenSSL3.5.9 and ticket226 target, `--locked`, no incremental
build and two build jobs.

| Gate | Actual result |
| --- | --- |
| Format; workspace/all-target clippy `-D warnings`; workspace build | Pass |
| Ordinary workspace | **64 passed / 0 failed / 161 ignored** |
| Every explicit ignored gate, real local providers | **161 passed / 0 failed / 0 ignored**:154 SQL (including combined native-spool SQL),1 schema/grants,4 Blob Azurite,1 Queue Azurite,1 native PDF |
| Active cargo-deny advisory/license/ban/source policy | Pass; existing narrow exceptions unchanged |
| Native executable linkage | Embedded **OpenSSL3.5.9**, no dynamic libssl/libcrypto |
| Structured telemetry detector/lifecycle | **110 passed / 0 failed / 0 skipped** |
| Native auth/member/catalogue/upload/event/extraction plus .NET query differential | All **7 pass**, each with a passing final private replay; query differential36 cases |
| Frozen xUnit extraction/API/analyzer/Noten/search suites | **113 passed / 0 skipped**; frozen NuGet/compiler warnings retained, not suppressed |
| Frozen extraction/Noten/catalogue Playwright, existing wrapper | **19 passed**, unchanged mocked static UI regression; native real backend proof is separate above |

The native extraction gate additionally refuses absent/incompatible libraries,
checks no durable attempt was spent, cancels stalled Queue transport through an
actual SIGTERM/finite telemetry finish, verifies a final allowed stale attempt
and spent-lease failure, and sends the queued reading notification through real
Azurite without implementing or calling AI. Not image/AppHost, hosted MI/RBAC,
live-model or universal PDF-layout equivalence evidence. Previously failed frozen
real-member/passkey assertions were not rerun in the mocked selection and remain
failures; the original uncaptured redaction event remains unclassified.
