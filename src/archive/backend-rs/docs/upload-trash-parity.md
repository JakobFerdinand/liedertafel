# Upload-first Lied, immutable Datei and Papierkorb (#224)

Base: accepted #223 integration `8ab47bb`. Frozen C#, tests, frontend, baseline,
infrastructure, workflows and production image are unchanged. This is wave 3b,
not a worker, AI, event/recording CRUD or cutover implementation.

## Boundaries and ordering

- All routes use real current Identity membership/session/CSRF through
  `Auth::authorize` and `RequestAccess::respond`. An editor creates/patches a
  flat Lied; create/patch/draft fields have the frozen case-insensitive wire
  names, UTF-16 bounds, null-leaves/empty-clears and unknown-field refusal.
- Initiation checks the visible owner, kind/media family, replacement ownership,
  dimensions, and stored bytes **including trash** plus pending declarations
  (or their cap when undeclared). The new declaration contributes zero when
  omitted, exactly as the oracle. Finalize checks actual size plus stored bytes,
  not other pending reservations. Checks retain the oracle's per-request budget
  arithmetic; no new cross-session atomic budget reservation guarantee is claimed.
- A session is saved before minting a ticket; a ticket failure leaves that row.
  Renewal extends expiry before minting, works after ticket expiry while Pending,
  and exposes the original `blobName`; initiation returns `blobName:null`.
  Renew/cancel/finalize remain creator-only even for another administrator.
- Browser bytes go straight to the private Blob service. The frozen engine's
  base64 six-digit IDs, contiguous-prefix committed/uncommitted discovery,
  actual-type block PUTs, XML `<Latest>` commit and **absence** of
  `x-ms-blob-content-type` are retained. Only absent/octet-stream stored type
  falls back to the declaration; explicit `application/xml` is not broadened.
- Finalize preserves terminal-state/idempotent replay, expired-state abandonment,
  identity mismatch staying Pending, missing-object retry, magic-byte/type gates,
  and unique `dateien/{dateiId}/{attemptId}` promotion. Actual bytes need not equal
  a declaration: only dimensions supplied on **both** requests are compared.
- Promotion completes before the transaction changes session/file state. SQL
  compare-and-swap arbitrates competing finalize/replacement saves. Losing attempts
  delete only their own uniquely promoted copy; the staged object stays replayable.
  `FinalizedHook::before_save` uses the same SQL connection/transaction for durable
  later-owner outbox rows. After commit: staged deletion, then ordered best-effort
  `after_save`. Idempotent replay does not rerun provider calls or hooks.
- Replacement inherits kind/Partitur/Stimme/recorded-at link; the old Datei and its
  video copy go to trash in that same transaction. Lied/Auftritt deletion stamps
  **only the owner**; canonical visible reads hide its children. Individual trash
  remains individual when the owner returns. Restore permits seven days (or the
  configured retention), refuses at the boundary and requires a live parent first.
  Restoring a replaced Datei keeps its successor alongside it.
- Shared lifecycle reads **seeded** event/programme/recording/passage/video rows.
  When a live playback copy leaves, originals return Cold→Hot provider-first;
  refused delete/replacement/restore moves roll back SQL state. Restoring a Cold
  original without a live copy self-heals Hot; restoring an original does not
  silently restore its trashed video copy. Permanent recording-copy removal clears
  slots/passage references, keeps recording/passage rows, returns originals Hot
  best-effort, commits deletion, then releases bytes best-effort. Owner removal
  explicitly orders programme/passage/recording/Datei references.
- **Frozen limitation, not a successful video-removal claim:** `PapierkorbRemoval`
  does not clear `video_jobs` references. Permanent removal of a video-job-referenced
  Datei fails FK `23503` on real PostgreSQL. An external actual-.NET probe against
  the reviewed baseline reproduces this, and Rust retains a non-successful 500
  without deleting bytes. No silent bug fix or successful cleanup claim is made.
  A behavior change needs separate authorization; #233 must account for this when
  integrating the video lifecycle, not assume cleanup was proved here.
- Angefangen persists the whole owned form, choices/marks and byte-free Datei
  identities, newest-first summaries, 25-draft/50-file bounds, repeatable discard
  and retained server outcomes. Removed files cascade their outcomes. Committed
  removal/discard notifies `DraftLifecycle`; no reader is registered. Suggestions,
  model calls, cancellation/supersession and first-reading races remain #230.

Issued SAS cannot be cryptographically retracted by cancel/trash/revocation;
application state prevents finalize/new tickets, not existing bounded provider access.

## Provider and private local setup

The existing `Storage` seam gains upload ticket, probe/header, copy and delete.
Official singular Blob 1.1.0 performs asynchronous `start_copy_from_url`, polls
properties (pending/temporary 404→wait; success→done; failed/aborted→failure), with
30-second monotonic bound and 250-ms interval. Source SAS is read-only for 60s;
copy never deletes the source. Delete ignores **only** 404. Existing keyless
delegation, explicit user-assigned identity, Shared Key signing, 2 retries, 5s
network bounds and operation-only/redacted telemetry remain intact.

Private Azurite 3.35.0 requires an endpoint its **own server-side copy fetch** can
reach. A host-mapped `127.0.0.1:51023`→container `10000` works for browser reads but
fails copy with internal `ECONNREFUSED`. The #224 isolated fixture maps
`127.0.0.1:51024:51024` and runs `azurite-blob --blobHost 0.0.0.0 --blobPort 51024`.
Normal signatures, private containers and supported `2025-11-05` are enforced;
no public/OAuth/loose/version-skip workaround. Hosted Entra/RBAC/delegation remains
separately approval-gated and unrun.

## Per-file oracle map

All Rust paths below are under `crates/archive-server/tests`, except the native
browser harness. Grouping several frozen facts in one HTTP control does not
create multiple reported test passes.

| Frozen file / scope | Rust evidence / precise later owner |
| --- | --- |
| `LiedApiTests` create/member-read/unknown/title/editor/patch/stale/missing | `lied_writes`; inherited `lied_api` retains list/search/paging/read shape |
| `FieldCatalogTests` all seven binding/name cases | `lied_writes` field-by-field create/case/patch/unknown/create-rowVersion/UTF-16 checks; `lied_api` pins full/list wire keys; `lied_entwurf_api` pins draft-only names and occasion refusal. Exercised through HTTP rather than exposing private request parsing |
| `DateiUploadTests` 14 facts | `datei_upload` Partitur+two Stimmen/order/access, promotion/prefix/staging/idempotence/resume/cancel; `upload_session_resume` validation/budget/terminal/header/failure controls; `upload_races` transactional before/after hooks, actual SQL unique rollback, two-Origin finalize and replacement races |
| `UploadSessionResumeTests` renewal, declarations, cap/declared reservations, lengths, creator/terminal cancel/finalize, mismatch/present-dimensions, default-type fallback, final budget | `upload_session_resume` plus `datei_upload`; `storage_azurite::authenticated_large_block_interrupt_renew_discover_commit_promote_restore_remove` exercises the actual signed transfer. `CleanerMarksExpiredPendingSessionsAbandonedWithinGrace` and `CancelledAndFinalizedSessionsAreNeverCleaned` specifically belong to **#234 finite cleanup CLI**, not claimed here |
| `DateiArtenTests` ten facts | `datei_arten` positive audio/MIDI/camera/document/photo whitelist, voice trim, member tickets, byte-family rejection, recorded-at link/replacement; `upload_session_resume` missing voice/unsupported kind/type/unknown event/member gates |
| `DateiPapierkorbTests` four facts | `datei_papierkorb` immutable replacement/list attribution, same-Lied/live/role guards, target deleted before finalize; `papierkorb_recovery` deletion/stale/restore; actual replacement also removes old Noten text from shared search |
| `PapierkorbRecoveryTests` ten facts | `papierkorb_recovery` Lied/Datei/event delete+restore, parent-first, replaced restoration, retention boundary/live idempotence, combined trash, ordered permanent removal against seeded programme/recording/passage rows. The cleanup half of `RestoringAfterSevenDaysRefusesAndTheCleanupFinishesTheRow` is **#234**, not a restore-only pass |
| `DateiStorageTierTests` Cold original removal, copy trash/replacement/removal, restored original | `datei_lifecycle_tiers`; inherited `datei_storage_tier` retains editor tier controls/CAS/failures. `ReattachingThePlaybackSlotReturnsTheColdOriginalToHotStorage` stays **#227** (slot write endpoint), using the shared lifecycle seam. Video job generation/retry worker cases stay **#233**, not needed to test seeded trash rows |
| `AssetStorageFailureTests` upload/renew failures | `upload_session_resume::failed_tickets_leave_saved_session_and_extended_expiry_and_finalize_provider_failures_are_retryable`; inherited `asset_storage_failure` retains config/signer/read overrides |
| `LiedEntwurfApiTests` first 11 persistence/validation facts (including both unknown-Art examples) | `lied_entwurf_api` form/choices/marks/empty replacement/owned list/privacy/member+CSRF/discard/shape bounds; extra seeded-outcome preservation/removal control prepares **#230**. All subsequent actual reading/model/cancellation/supersession/budget/stale-completion facts stay **#230**; no model-result coverage is claimed from storing form markers |
| `AuftrittDateienTests` shared owner/upload portions | `auftritt_dateien` real auth/SQL uploads against seeded live Auftritt, document/photo and original media-family behavior, member document ticket/direct-Aufnahme refusal. Event document metadata PATCH and full document/photo/event-page orchestration belong **#225**; recording attachment/publication/access/validated playback and Zeitmarke-replacement integration in facts 4–6 belong **#227**. Their shared session/Datei/trash side is implemented here, not their future endpoints |
| `NotenTextSearchTests`, `LiedSucheQuellenTests`, `SearchAgreementTests` replacement state | `datei_papierkorb` actual lifecycle→shared visible search; inherited suites remain regression gates. Chat tool adapter still **#232** |
| `PapierkorbCleanerTests`, cleanup `FiniteJobsTests`, `UploadSessionCleanup` | **#234** owns finite CLI/retention sweep/grace/dry-run/maintenance/flush; shared removal/restore policies are here. No cleaner command was registered early |

## Reproduce

Use static OpenSSL 3.5.9 and `--locked`, as in `auth-dependency-audit.md`. The local
Cargo target is `/tmp/opencode/224-target` (symlink to ignored local build storage
because tmpfs filled); it is not source or a worktree cleanup.

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
# Explicit disposable local PostgreSQL; all API tests perform real sign-in/CSRF.
cargo test -p archive-server --locked --test lied_writes --test datei_upload \
  --test upload_session_resume --test datei_arten --test datei_papierkorb \
  --test papierkorb_recovery --test datei_lifecycle_tiers --test lied_entwurf_api \
  --test auftritt_dateien --test upload_races -- --ignored
# Inherited auth/member/catalogue and separate schema/grant gates remain required.
cargo test -p archive-server --locked --test storage_azurite -- --ignored
node --test tests/browser/telemetry-*.test.cjs
node tests/browser/upload-native.cjs
cargo deny --locked check
```

Native harness requires `ARCHIVE_TEST_POSTGRES`, `ARCHIVE_TEST_AZURITE`, unchanged
`ARCHIVE_STATIC_ROOT`, `ARCHIVE_BINARY`, external `NODE_PATH` dependencies and
optionally `ARCHIVE_CHROMIUM`. It runs normal SMTP-code login, migration/seed/origin/
restart, CORS/private storage, unchanged UI drop/save/existing-Lied match/replacement/
trash/restore/Angefangen resume and interrupted/resumed 20MiB transfer. Fault injection
**aborts** later Blob block network requests (no response fulfillment/API mock), then
asserts contiguous-prefix resume and exact transfer headers/IDs against real Azurite.

The current structured detector watches native child streams and raw OTLP frames,
drains origins/collectors before asserting, and retains private 0700/0600 replay.
Inherited original uncaptured member-redaction failure remains **unclassified**;
neither this new passing capture nor subsequent passes identify its cause.
Frozen passkey/member browser failures remain failures as documented in
`auth-browser-baseline.md` / `member-parity.md`; native controls are not replacements
for claiming those frozen assertions passed.

## Verification (2026-11-09)

Integration was merged/check-confirmed at `8ab47bb` before the final sequential
format → clippy → workspace run, and reconfirmed before handoff. Every Cargo
build/test used `--locked`, static OpenSSL 3.5.9, `CARGO_INCREMENTAL=0`,
`CARGO_PROFILE_DEV_DEBUG=0`, `-j 2`. No manifest, lockfile or audit policy changed.

| Gate | Actual result |
| --- | --- |
| fmt check; workspace/all-target clippy `-D warnings` | Pass |
| Ordinary workspace | **57 passed, 0 failed, 115 explicitly ignored** |
| Explicit real-PG auth/member/catalogue/upload/trash/draft/race suites | **110 passed, 0 ignored** (73 inherited + 37 new); the explicitly named frozen-video-FK refusal control is a limitation check, not a successful-removal claim |
| Separate native migration/schema/canonical runtime grants | **1 passed**, schema/history/grant contracts unchanged |
| Private Azurite 3.35.0 | **4 passed, 0 ignored**, including real signed 20MiB blocks/discover/commit/copy/probe/read/trash/restore/delete plus inherited signing/range/tier/reader controls |
| Default-feature workspace build | Pass |
| Active `cargo deny --locked check` | Exit **0**, existing narrow license/advisory policy retained |
| Native linkage | Embedded **OpenSSL 3.5.9**, no dynamic libssl/libcrypto |
| Structured detector/lifecycle controls | **110 passed, 0 failed/skipped**, including actual forbidden payload/console disclosures and late-event fail-closed controls |
| Native inherited auth / members / catalogue + new upload | All **4 pass**, normal native binaries, real SQL/auth/SMTP/OTLP and private storage where required |
| Final private replay | All **4 pass**, zero findings/protocol/lifecycle errors |
| Unchanged frozen .NET selected suites | **117 passed, 0 skipped**; existing compiler/NuGet warnings retained. Initial tmpfs-aborted run is not a pass |
| Additional actual frozen .NET/PG video-removal differential | Confirms Restrict FK `23503` refusal; no success claim or migration-warning suppression. Uses reviewed baseline fixture |
| Unchanged frozen Playwright UI regressions | **59 scheduled: 58 passed, 1 skipped**, desktop, frozen API mocks against unchanged static export. The substring selection also included member tests; its real invitation flow skipped without a Development backend. These are **mocked UI regression results**, not backend/AI/Blob proof |

All **115** ordinary ignored tests were subsequently explicitly executed:
110 real-PG + 1 schema/grant + 4 storage. Neither hosted gates nor skipped frozen
browser assertions are counted as passes. Previously known frozen real-member /
passkey assertion failures were **not rerun** in the static/mock browser selection;
their previous failures remain honest and unchanged. Fresh native real-auth/UI
controls above pass, which does not erase them.

Final native captures (all private 0700/0600; values must not be published):

- Auth `/tmp/opencode/223-native-auth-telemetry-EBBSwL`: 49 OTLP requests / 74 markers.
- Members `/tmp/opencode/223-members-native-telemetry-rBL1Ks`: 79 / 29.
- Catalogue `/tmp/opencode/223-catalogue-native-telemetry-j38t4p`: 42 / 19.
- Upload `/tmp/opencode/223-upload-native-telemetry-7Xrqgm`: 96 / 33, including
  all actual upload SAS signatures, stored pending/final object names, filenames,
  uploaded-content marker and actual session/CSRF/code values. The existing capture
  helper retains its historical `223-` directory prefix; this is fresh #224 evidence.

Logs: `/tmp/opencode/224-{fmt,clippy,workspace,pg,schema-grants,azurite,build,deny,
linkage,detector,native-auth,members-native,catalogue-native,upload-native}-final.log`,
`224-*-replay-final.log`, `224-dotnet-oracles-final.log`, `224-frozen-browser.log`.
Actual .NET/PG probe source/log: `/tmp/opencode/224-video-removal-oracle/`,
`224-video-removal-dotnet.log`. Red loops are retained for Lied writes, session,
finalize, kinds, immutable replacement, trash, draft, hooks, owner upload, private
copy topology, inherited recorded-at-link replacement and typed request-shape
rejection even for fields ignored later by a replacement or Partitur decision.

No live AI/mail/Neon/hosted storage, push, issue/PR write, integration/main/cutover
merge, deploy or worktree cleanup occurred. #225/#226/#227/#230/#233/#234/#235
integration ownership above, hosted keyless access and approval-gated cutover
remain distinct gates; no later feature is claimed from an injected hook or seeded row.

## Request-binding review remediation (2026-11-09)

The two Low findings in `/tmp/opencode/liedertafel-218-upload-review.md` are
addressed at a scoped streaming request-record boundary. Recognized properties
bind case-insensitively in **wire arrival order**, checking each typed occurrence
before retaining the last value. Exact duplicate invalid→valid occurrences cannot
disappear into a `Value` map. Nested draft records use the same boundary;
dictionary keys/unknown field names retain their data semantics. Domain validation
still follows binding (an earlier semantically invalid string can be replaced).
Finalize binds before session lookup/terminal replay/provider/state work, while
empty bytes, null and objects retain their optional-body behavior. No state machine,
provider, migration, frozen source/test/frontend or video-FK policy changed.

Actual frozen assembly observations are committed in
`tests/fixtures/upload-binding-oracle.json`: **111 controls** (84 create/patch/upload/
draft and 27 finalize). `upload_binding` runs 84 raw HTTP/real-PG controls plus
27 finalize bodies in both pending/finalized states (**54**), and three optional
body controls. Rejections assert unchanged provider calls/session state. Ordinary
integer fields accept quoted integers as the actual HTTP JSON defaults do;
the custom Lied rowVersion converter still refuses strings. The final fixture
includes sign/leading-zero/whitespace/fraction/exponent/overflow controls.

The external reflection program `/tmp/opencode/224-remediation-binding/` invokes
the actual frozen backend DLL, not a re-created DTO. The HTTP reflection probe
`/tmp/opencode/224-http-binding/` invokes unchanged `LiedTestHost` and its actual
routes: **93 HTTP observations** across all 13 auth/member DTOs, all four bodyless
routes and finalize. Slim results are in `tests/fixtures/http-binding-oracle.json`.
For finalize, empty/null/object reach the missing-blob 409; malformed/nonobject
bodies fail before that path. **Development returns 500** for typed binding errors
through its exception handler; Rust's controlled 400 is not claimed as a universal
frozen HTTP 400. Optional auth bodies also reach their ordinary domain 400.

TDD logs under `/tmp/opencode/`: `224-binding-red.log` (wire-order/discarded-invalid
failures), `224-finalize-binding-red.log` (terminal bypass), and
`224-numeric-binding-red.log` (quoted integers); corresponding green logs retain
the passing runs. New HTTP seams were explicitly requested by the maintainer.
Before this catalogue-only commit, sequential fmt → clippy → workspace passed:
**57 passed, 117 ignored**; explicit real-PG suites **112 passed, 0 ignored**;
active deny exited **0**. Logs: `224-binding-{fmt,clippy,workspace,pg,deny}.log`.
Schema/Azurite/native/browser/crypto/OTLP final revalidation follows the separately
authorized auth-binding commit and must not be inferred from these focused gates.
Acceptance remains **HOLD pending independent rereview**; #225 stays blocked.
