# Stored Noten reading (#229)

Base: accepted `fda8e31` (#228); branch `ticket/229-rust-reading-worker`.
Independent review/acceptance belongs to the parent. Frozen backend/tests,
frontend, infrastructure, workflows and Dockerfile are unchanged. No live AI,
hosted token, Neon, external mail or private document is used.

## Reboot recovery — 2026-11-13

The reboot cleared **all** former `/tmp/opencode` helpers, logs, reports, native
installs and private captures. The Nov10 validation table and red/green filenames
below are historical claims preserved in `c4abb2d`, **not currently available raw
proof**. Lost failed/timeout runs remain historical failures; no replacement log
is fabricated. Independent source review remains parent-owned and pinned to
`c4abb2d`, not inferred from this recovery or repeated by the implementer.

Fresh recovery evidence lives under the ignored durable root
`/home/jakob/private/liedertafel/src/archive/backend-rs/target/agent-218/`:
`evidence/229-report.md`, `evidence/229/` (helpers, private env and stage logs),
`evidence/229-scratch/` (private captures/spools), and `native/229/` (exact native
installs, external test dependencies and an unchanged-source frontend export).
The restored worktree is `worktrees/229`; Cargo still uses `target/ticket-229`.
Only the named stopped `archive-229-postgres`17.6 and `archive-229-azurite`3.35.0
fixtures were restarted after safe image/loopback-port checks; data/targets and
all unrelated containers were retained.

For committed tests with hardcoded scratch paths, the empty post-reboot
`/tmp/opencode` directory is a compatibility **symlink** to durable
`evidence/229-scratch`, not evidence on tmpfs. New capture indexes use resolved
durable paths, with directory0700/replay0600 and checked terminal completions.
The environment and recovered own local fixture password stay private0600.
No source assertions or product configuration are changed for path recovery.

Native downloads retain the exact committed OpenSSL3.5.9/PDFium8138 archive and
library digests from `auth-dependency-audit.md` / `extraction-parity.md`; no host
native library or Cargo dependency/toolchain/policy upgrade is made. Fresh
provenance includes public URLs/digests and unmodified OpenSSL static build
commands (`no-tests`, so no upstream OpenSSL test-suite pass is claimed).
The frontend export is rebuilt from unchanged tracked sources with its frozen
lockfile and package-manager11.3.0, with source/output inventory provenance.
External browser delivery dependencies are separately pinned/locked; the lost
Queue npm install's exact version is unavailable and the newly recorded
test-only version is not misrepresented as historical byte identity.

Final full post-R2a validation completed at implementation SHA
**`49ec786b81cbf47fe5b4dfad9695c0212c4978f4`**. Latest integration `fda8e31`
is retained as an ancestor; the prior requested merge was a no-op. All **35
recorded stages exited0**, sequentially:
fmt → all-target clippy → workspace → examples/default build → active deny →
linkage → ALL combined explicit gates → default product rebuild/linkage → ten
native journeys and individual replays → frozen .NET and actual oracles →
frontend unit/detector/mocked UI. No historical count substitutes for these gates.

| Fresh recovery gate | Executed result |
| --- | --- |
| Ordinary Rust workspace | **113 passed / 0 failed / 226 ignored** |
| ALL combined explicit gates | **226 passed / 0 failed / 0 ignored**:217 SQL (including native PDF/gateway/worker/config children),1 schema/adoption/grants,5 private Blob,1 private Queue,2 standalone native PDF |
| Native journeys and private drained replays | **10/10 PASS +10/10 PASS**, all terminal completion/secrecy checks, empty findings/errors/legacyMatches, capture0700/replay0600 |
| Frozen scoped .NET | **128 passed**, with actual current reading PG park/wake and all three PG ledger contention/replica cases; historical rekey excluded only under its explicit equivalent |
| Frontend unit / mocked desktop-mobile UI / detector | **115 /184 /110 passed**, zero failed; units/detector zero skipped |
| Actual frozen scan agent | **3 observations**; schema and both prompts regenerated exactly |
| Actual frozen quote/configuration oracles | Exact regenerated fixtures: **1,112,064 scalar +18 quote +8 clean cases**, **1703 numeric binder cases/131 spellings/13 settings** |
| Locked fmt/clippy/builds/active cargo-deny0.20.2 | **PASS**; no policy suppression, existing duplicate-version/package/compiler warnings retained |
| Final default product linkage | Static **OpenSSL3.5.9**, no dynamic libssl/libcrypto; SHA256 `4d708cdf58bd71a20a54579c0f6e5415538feeca39ad1dc70ca2573a2bd37b61` |

Counts are test functions, not expanded fixture cases/children. The ordinary
Rust and frozen .NET totals each include an absent-configuration live scan guard
early return, **not** a paid evaluation pass. Scripted evaluation remains3/3
positive documents,1092 synthetic micro-EUR, with wrong/missing/unchecked controls.
The rebuilt static export checks147 tracked source/config/test files against the
unchanged frontend (excluding generated next-env.d.ts);112 output files have
inventory SHA256 `eb91ff006cb4d94544df532c48db0ce67cc13f7fd7462de3bb212dcfa7089df5`.
This is newly generated output, not byte identity with the lost Nov10 export.

Authoritative fresh logs: `evidence/229/r2a-pipeline.log`, individual
`evidence/229/final-r2a/<stage>.log`, `final-r2a/results.json`, `final-r2a/captures.json`,
`final-r2a/evidence.json`, `r2a-evidence-audit.log`; native/tool/frontend and
review-fixture provenance are in `evidence/229/*-provenance.json`. Private failed
captures are indexed separately in `failed-captures.json`, not relabelled clean.
The durable report pins implementation and documentation-only last commit.
Independent final source rereview is **SOURCE ACCEPTABLE at49ec786**, with
R1/R2/R2a closed and no new actionable finding (`evidence/229-review.md`). This
does not claim reviewer SQL/native execution. Independent focused-runtime
clearance and parent integration authorization remain separate required gates.
Hosted/live/private/image/AppHost/future
model-HTTP and cutover limitations below remain unwaived; #230/#231 stay blocked.

### R2a residual converter finding after source rereview

Independent rereview at `9c9ee0f` closed R1's source finding (including independently
regenerated exhaustive BCL/production-Rust pure checks) and the original R2 padded
numeric finding. It found **Low R2a**: the outer BaseNumberConverter prefix is
followed by Convert radix16's own optional sign/prefix step. Valid `#0x1`,
`0x0x1`, `&h0X1` and padded variants were incorrectly defaulted. The reviewer did
not execute their downstream SQL/workload consequence; no cap bypass is claimed.

The narrow fix consumes **one positive sign then one optional0x/0X** after the
outer prefix, requires actual hex digits and retains the checked source widths.
It does not repeatedly strip prefixes, accept a sign after the inner prefix or
guess signed hex semantics. Versioned .NET10 `ParseNumbers.StringToInt/Long`
confirms this ordering. R1 quote/casing, worker, native bounds tests and frozen
sources are unchanged. The implementation diff from `9c9ee0f` is confined to
options.rs, the existing ENV test, configuration oracle/fixture and this guide.

The actual frozen binder matrix now has **1703 rows /131 spellings over13
settings** (707 valid,996 exceptions), covering both source widths, outer/inner
positive-sign ordering, empty/suffix/extra-prefix/sign/whitespace failures,
maxima/two's-complement/adjacent overflow. Every row runs through real isolated
Options ENV. `r2a-red.log` failed on actual `#0x1`:33554432 instead of1;
`r2a-final-focused.log` passes the full matrix and unchanged real config worker.
The initial expanded generator also included NUL, which cannot be passed in an
OS ENV value; that harness failure is retained separately, not a product red or
pass. The final ENV-domain matrix retains invalid suffix/ordering controls.
Malformed/overflow Rust-default versus BCL-exception qualification remains.

The9c9ee0f full35-stage green run is retained **pre-R2a evidence only**, under
`evidence/229/final/` and `postfix-pipeline.log`. The final49ec786 run above freshly
repeats all35 stages after the lexical fix. The source rereview independently
closed R2a at that exact pin. Its scope is source/pure loader observations, not
SQL/native acceptance; independent focused-runtime clearance remains pending.
#230/#231 remain blocked.

### Independent review HOLD and local remediation

The independent source review at `c4abb2d` found **R1 Low** quote-casing and
**R2 Medium** numeric ENV parity gaps (`evidence/229-review.md`). Its small
offline probes did not execute SQL writes. The pre-fix recovery run completed
fmt/clippy/109 ordinary/224 explicit/build/audit/static linkage and native auth
plus replay, then **failed before member browser assertions** because the long
absolute TMPDIR exceeded Chromium's Unix-socket path limit. Its logs/capture are
retained under `evidence/229/pre-fix` and durable scratch; later stages were unrun.
The short compatibility TMPDIR alias addresses only that setup failure. No code
was edited until that job's completion notification.

- **R1**, `359d5e6`: shared reading normalization now uses deliberately simple
  invariant scalar lowercase, explicitly preserving U+0130, not Rust full/context
  casing. Search/auth folding is unchanged. Versioned .NET10 `pal_casing.c`
  documents the simple ICU mapping and invariant dotted-I special case. The new
  `ReadingQuoteOracle.cs` invokes the **actual unchanged** frozen Normalize,
  CleanValue and QuoteIsVerified helpers:1,112,064 scalar observations (1512
  nonidentity entries),18 multi-character quote and8 clean/UTF-16 cases. This
  checks rather than assumes per-scalar Rust/BCL equivalence. It covers dotted-I
  rejection, sigma acceptance, German accents, combining/non-NFC, supplementary
  letters/emoji, ß versus SS, soft hyphen, .NET whitespace and UTF-16 limits.
  `reading_quotes` and real-PG `reading_quote_worker` showed behavioral red→green:
  the original worker actually wrote the forbidden dotted-I expansion; the fixed
  worker drops it, accepts the frozen Greek case, and preserves a late editor's
  field using a row-lock-controlled race. Real gateway/SQL accounting remains in
  the path; no timing sleep or replacement writer proves that result.
- **R2**: numeric ENV parsing trims before conversion, including JPEG, and retains
  the frozen signed source widths (Int64 PDF bytes; Int32 other settings), positive
  defaults and image/pixel/batch/JPEG clamps. Actual BaseNumberConverter #/0x/&h
  signed-width forms are also checked rather than guessed. The actual unchanged
  ReadingOptions + ConfigurationBuilder oracle captures **533 cases over all13
  numeric settings**. `reading_configuration` executes each through real ENV in
  a separate child. Malformed/overflow values still retain the pre-existing Rust
  defaults whereas the frozen binder throws: **not identical exception behavior**.
  This is not a demonstrated monthly-cap bypass. `reading_configuration_worker`
  checks actual padded page/image/JPEG/text/output/nontext reservation bounds;
  a no-trim mutation fails the actual page-progress assertion. The native reading
  journey now uses padded3-page/2-image values, asserts the actual extraction
  handoff and finite command costs/progress, and verifies a padded10-byte refusal
  without another model call. Original native red was PageLimit20 versus3.

The shared #230/#231 trust boundary inherits the corrected quote policy; no
duplicate query/reader framework, unsafe lease comparison, global search policy
or unrelated worker change was added. Focused red/green/oracle logs are under
`evidence/229/r1-*` and `r2-*`; historical failed logs lost at reboot are not
recreated as fresh evidence. These are **local fixes, not independent clearance**.
The subsequent final source rereview at49ec786 is acceptable and fresh local
full gates are green as recorded above. Independent focused runtime checks
remain required; neither the local tests nor source-only clearance substitutes
for them.

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
| `ReadingWorkerTests.cs` | `reading_worker`: actual SQL/gateway/ledger, generated raster PDFs, deterministic/no-call path, only-empty/late-editor writes, malicious/unrequested/duplicate/uncertain/invalid quote/value controls, partial/resume, real cap/no churn/next-month wake and exact old saved usage, transient paid-page reuse, attempts/material/extraction/visibility matrix. `reading_quote_worker` adds actual frozen Unicode drop/write and locked late-editor cases; `reading_configuration_worker` checks padded runtime render/model/reservation bounds. Existing extraction handoff/Enabled and Queue drain gates are inherited, not duplicated. |
| `ReadingOptions.cs` | `reading_configuration`:1703 actual ConfigurationBuilder observations over all13 numeric settings, each exercised through real Options ENV in an isolated child; positive/default/clamp/source-width and two-step hex lexical rules, with explicit invalid-default versus binder-exception qualification. `reading_configuration_worker` and strengthened native reading journey prove actual configured extraction handoff and finite command/worker consumption, not only helper fields. |
| `ReadingApiTests.cs` | `reading_api`: authenticated raw HTTP, state/source/REST nulls/no-store, roles/CSRF/disabled, retry/continue/material/full text, missing/pre-existing/insert-race rows, malformed body ignored, removed proposal/provenance routes. `reading_dispatch` exercises real selected/caller CAS send barriers and stamp failure. |
| `ReadingPostgresLiveTests.cs` CURRENT cases | `reading_postgres`, `reading_worker`, `reading_dispatch`, `reading_lifecycle`: real canonical constraints/FKs and production gateway/ledger SQL, budget sweep/progress. No current SQL waiver. |
| Same file, historical rekey/drop case | Explicit approved frozen-head adoption/preservation + older-history rejection in `historical_baseline`; obsolete transformation **NOT executed by Rust**. See `historical-migration-disposition.md` for the exact case/frontier and separately executed constraints. |
| `PdfPageRendererTests.cs` | `pdf_page_renderer`: actual native count, requested/clamped pages, JPEG1000px, dark drawn raster content, corrupt input/cancellation/spool cleanup. `pdf_spool` adds a deterministic real-filesystem queued-write handoff regression. Accepted `pdf_text_extractor`/extraction spool tests still run after sharing the loader. |
| `ScanPromptTests.cs` | `scan_prompt`: exact actual frozen agent schema/prompt/output-bound capture, catalog bullets/version, UTF-16/value/quote rules. `reading_quotes` adds exhaustive actual frozen scalar normalization and multi-character/clean/UTF-16 controls. `tests/oracles/ScanReadingOracle.cs` executes the unchanged frozen agent/Gateway with fake delivery/budget ONLY for capturing requests, not as reading-domain or SQL acceptance. Nov10 external project/log paths were lost; fresh external projects and scan/quote oracle logs are under durable `evidence/229`. Committed fixtures regenerate exactly. No credential/socket exists in these oracles. Frozen NuGet/compiler warnings remain warnings, not an audit pass. |
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
cargo build --workspace --locked
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

## Historical candidate validation — 2026-11-10 (raw evidence lost at reboot)

Runtime/test source: **`7bf628fe852f89b06b83514d879e5eef3c517fcb`**. Accepted
integration tip **`fda8e319215dabd195d6bfd3da66193615b3553a`** is an ancestor;
the requested merge into this ticket branch returned **Already up to date**.
The handoff's final commit changes documentation only. Independent acceptance
and integration-branch merge remain parent-owned; #230/#231 stay blocked.

| Gate | Executed result |
| --- | --- |
| Sequential fmt, all-target clippy `-D warnings`, ordinary workspace, examples build, active pinned cargo-deny, ALL explicit ignored gates | **PASS** in that order; dependencies locked; existing duplicate-version audit warnings retained |
| Ordinary Rust workspace | **109 passed / 0 failed / 224 ignored**; includes one absent-config live guard early return, **not** a paid/live evaluation pass |
| ALL explicit combined gates | **224 passed / 0 failed / 0 ignored**: 215 SQL (including PG+native PDF/gateway/scripted evaluation), 1 schema/adoption/grants, 5 private Blob, 1 private Queue, 2 standalone native PDF |
| Additional default workspace/product build | **PASS**, separately from `--examples`; all ten native journeys then freshly repeated against that default-feature product binary |
| Native auth/member/catalogue/upload/events/query/extraction/recordings/AI/reading and individual drained replays | Final product-binary run **10/10 PASS + 10/10 replay PASS** |
| Native completion/privacy | All ten final captures have empty findings/errors/legacyMatches, terminal completion + secrecy checks, capture0700/replay0600; checked independently after the replay runner |
| Official-schema detector/lifecycle | **110 passed / 0 failed / 0 skipped** |
| Unchanged frozen scoped .NET | **128 passed**, including current reading PG park/wake and all three real PG ledger contention/replica cases; includes one absent-config live guard early return; historical rekey method excluded only under the explicit equivalent above |
| Unchanged frontend unit tests | **115 passed / 0 failed / 0 skipped** |
| Unchanged mocked desktop/mobile UI | **184 passed**, eight-spec selection; not native model/backend acceptance |
| Actual frozen offline scan agent | **3 observations**, schema + both prompts exactly match committed fixtures; no provider socket/credential |
| Binary linkage | Embedded **OpenSSL3.5.9**, no dynamic libssl/libcrypto; final product SHA256 `7fe5af3d783bea71048643c4ef4710559e10a6d306bb2c022ef2b4f4b98c51ff` |

Totals are test-function counts, not expanded assertions, corpus outcomes or
isolated configuration-child processes. Scripted evaluation's generated1/3/5-page
positive set reports **3/3 correct,1092 micro-EUR synthetic ledger cost**; explicit
wrong/missing/unchecked controls are part of the same test, not extra passes or
real OCR-quality claims. No model provider was paid.

Primary logs under `/tmp/opencode/`: `229-final-{fmt,clippy,workspace,build,deny,
explicit,pipeline}.log`, separate `229-final-workspace-build.log`, native summary,
per-native `<name>.log`/`<name>-replay.log`, `229-final-native-results.json`,
`229-final-{evidence.json,evidence-verification.log,linkage.log}`. Scoped evidence:
`229-final-{frozen-dotnet,frontend-unit,frozen-browser-configured,detector}.log`;
oracle `229-scan-oracle.log` and `229-final-oracle-comparison.log`. Retained static
output provenance is `229-static-provenance.log`:112 tracked source/config files
match,111 output files, inventory hash recorded in the external report.

The final report `/tmp/opencode/liedertafel-218-229-report.md` pins both the
implementation and documentation-only last SHA, final binary hash/capture paths,
failed/red/unrun distinctions and remaining gates. Relevant retained controls:

- `229-spool-red.log` failed with zero visible bytes before the flush fix;
  `229-spool-green.log` and12 combined spool/lifecycle stress repetitions passed.
  Prior lifecycle timeouts are real failed runs, not retroactively relabelled.
- `229-preclaim-{red,green}.log` covers deadline expiry before acquiring a lease.
- `229-foreign-lease-negative-control.log` shows the unsafe `>=` mutation failing
  the real-PG foreign-lease assertion; restored exact equality passed separately.
- `229-live-price-{red,green}.log` proves underscore rejection at the pure
  evaluation override seam after reusing the accepted configuration converter.
- Earlier failed fixture/setup/native runs and incomplete full pipelines remain
  retained separately; neither their totals nor prior-source passes replace the
  final-source combined run. Frozen NuGet warnings are not a Rust audit
  pass; the scoped .NET run retains its existing package/compiler warnings.

Inherited frozen passkey/member browser failures, original uncaptured
**UNCLASSIFIED** redaction event, video FK refusal and conditional
budget/current-price/unknown-ACK/notice qualifications remain unwaived. Hosted
identity/RBAC/storage, external mail, live model/API/private corpus, actual
image/AppHost, production deployment/cutover and future model-consuming HTTP
lifetime gates remain **unrun**, not passed by these local checks. No push,
issue/PR write, integration/main merge, deployment or worktree/resource cleanup
was performed. Local fixture services/artifacts remain for the parent.
