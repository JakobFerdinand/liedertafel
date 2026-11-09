# Lieder, Datei access and storage tiers (#223)

Wave 3a starts at integration `e0f716d` (#220/#221/#222 accepted). C# backend,
C# tests, frontend, baseline, migration checksums, runtime grants, infrastructure,
workflows and production image remain unchanged. This is not #224 upload/trash,
#227 recordings, #233 video writes or a cutover.

## Shared contracts

- Four catalogue routes join the existing single registry: GET `/api/lieder`,
  GET `/api/lieder/{id}`, GET `/api/dateien/{id}/access`, POST
  `/api/dateien/{id}/storage-tier`. Ordinary membership and editor decisions,
  CSRF-first mutations, current database membership, sliding cookies and no-store
  reuse `Auth::authorize` / `RequestAccess::respond`. No catalogue test fabricates
  an authentication principal. `CurrentSession::can_read_auftritt_datei` retains
  the direct-Auftritt-Aufnahme restriction: members need the later recording's
  publication/download-aware ticket, even for a published recording.
- `catalogue::queries` owns live Lied/Datei reads. Dateien require both their own
  live state and their parent's live state; Noten extraction joins this predicate
  **in the SQL statement**, not through a previously materialized list of ids.
  Only completed Noten text contributes, from embedded PDFs or scan reading.
  Pending drafts, other kinds and trashed/replaced Dateien contribute nothing.
- `catalogue::search` owns German folding, literal-space tokenization, all-token
  matching across fields, labels, ranks, folded UTF-16 ordinal title/id order and
  paging. Creators and Liedtext are searchable; Besetzung/Tonart/Sprache/Anlass
  are not accidental search fields. The two plain equality filters are folded;
  requested Datei types are normalized, deduplicated and combined with AND.
  Options use all live Lieder, not just the filtered page. List rows omit lyrics;
  detail and nullable bookkeeping/Datei fields retain the frozen shapes.
- A local executed .NET language/BCL oracle pins two easily missed details:
  `OrdinalIgnoreCase` does not expand `ß` to `SS` and orders null before empty;
  the frozen unchecked Int32 page-offset multiplication wraps before LINQ Skip.
  An extreme page of `2147483647` therefore starts at zero, not at a new
  saturating offset. This oddity is preserved, not silently redesigned.
- Video jobs/recording slots are **read from existing baseline rows**. Completed
  live video copies are hidden as separate detail entries. View tickets use the
  copy, download tickets always use the original. An unavailable copy falls back
  to the original. A visible recorded-at event contributes its title and honest
  partial-date display; remaining event semantics belong to #225.
- Tier requests accept only trimmed/lowercased Hot/Cold, check the optional
  version before idempotence, and do nothing for the same tier. Cold requires a
  separate completed live video copy or validated recording playback slot.
  Provider first, then a real SQL optimistic-concurrency update: a provider
  failure is the exact German 502, a lost version race is 409. Failed persistence
  does not claim the transition; repeating the provider's idempotent move heals
  the row. No recording/video/upload/trash write route is introduced.
- The shared maintenance prerequisite mirrors the frozen API pause (503,
  Retry-After 600, before dependencies/CSRF), leaving `/api/build`,
  `/api/maintenance` and process health available. #234 still owns finite-worker
  maintenance and the complete ops/maintenance suite, not claimed here.

## Provider seam and configuration

The injected `Storage` boundary contains only read-ticket and tier operations.
`BlobStorage` also exposes local/provider upload-ticket signing and the bounded
probe/header/forward-reader needed by the next waves. Official SDK access is
used by private fixture bootstrap; no application upload lifecycle, promotion,
deletion, cleanup or worker writer is implemented early.

`ConnectionStrings__archive-blobs` takes precedence over
`Archive__Assets__ServiceUri`; container defaults to `archive-assets`, read
lifetime to 15 minutes (`Archive__Assets__ContainerName` / `ReadTicketLifetime`
override them). Missing configuration fails only when storage is requested,
with `Kein Blob-Verbindungsstring konfiguriert.`; endpoints translate it to the
shared 502. The actual AppHost supplies a full emulator connection string,
not a `UseDevelopmentStorage=true` shortcut.

Official Microsoft dependencies are **azure_storage_blob 1.1.0**, **azure_identity
1.0.0**, **azure_storage_sas 1.0.0**, **azure_storage_common 1.0.0** and **azure_core
1.1.0**, with their released source/changelogs inspected. Existing packages were
not upgraded. Six new locked packages are MIT; the active cargo-deny policy
and the four approved exact-version WebAuthn MPL exceptions are unchanged.

- Keyless mode uses official managed identity with explicit
  `ManagedIdentityCredentialOptions.user_assigned_id = ClientId(AZURE_CLIENT_ID)`.
  Development keyless mode retains `DeveloperToolsCredential`; no credential is
  acquired during construction. Production delegation uses the **official**
  `SasBuilder`, never an account key disguised as a delegation key. Its signed
  `sv=2026-04-06` is not edited after signing.
- A Tokio mutex serializes delegation refresh. Cache reuse requires coverage of
  now and the requested signed expiry. Both key requests and SAS serialize whole
  seconds; comparisons use that same precision, including subsecond clocks.
  Failed/cancelled refresh does not poison the cache or strand the lock.
  Requests start at now minus five minutes and expire with the requested ticket.
- Local Shared Key uses the official per-try Policy/HMAC extension and Microsoft
  REST canonicalization, with a redacted policy Debug implementation. The encoded
  request path includes the doubled account for IP-style Azurite URLs; the service
  SAS instead signs the unencoded `/blob/account/container/blob` resource. SDK
  body length is installed **before signing**, without collecting a stream.
  This matters for nonempty SDK XML requests. Per-retry date/signature are fresh.
- Private Azurite **3.35.0** uses public client `version=2025-11-05`, and local
  service SAS signs that same compatible version. No OAuth/basic bypass, public
  container, loose mode or version-check skip is used.
- Upload SAS is blob-scoped `rcw`, read SAS `r`, no start time or broader grants.
  Content type and inline/attachment overrides are signed only when a nonempty
  type is supplied. The exact unchanged block PUT/discovery/XML-commit sequence
  is tested **at the provider seam**, not claimed as an implemented upload API.
- SDK retry maximum is **2**, not its default 8. Per-try header/network deadline,
  connection timeout and transport read-idle timeout are **5 seconds**. Redirects
  and decompression are disabled. SDK/HTTP diagnostics remain globally suppressed
  by existing service defaults even under trace logging; application spans use
  operation-only `archive.assets.*`, with generic errors, never URLs or keys.
- The reader is **forward-only**, not full .NET seekable-OpenRead parity. It lazily
  downloads sequential, HEAD-ETag-pinned ranges of at most 8 MiB through the official
  SDK and never collects a whole file. A red test caught the SDK's whole-object
  multipart download leaving a detached initial task after drop; one bounded
  range at a time avoids that path. A synthetic 1 GiB object proves bounded reads,
  ETag pinning and cancellation without allocating that object. #226/#233 must
  adapt actual extraction/video consumers and prove their own bounded reading.

Issued SAS cannot be retracted by application revocation/trash before expiry;
those changes stop **new** tickets. Browser GET/HEAD/ranges go to Blob directly,
never through a new archive proxy API.

## Frozen-suite ownership map

Rust tests are `crates/archive-server/tests/*.rs`. Seeded SQL is fixture setup,
not an assertion that the write path producing it already exists.

| Frozen oracle | #223 evidence / remaining owner |
| --- | --- |
| `LiedApiTests.EditorCreatesLiedAndMemberSeesItImmediately` | `lied_api::member_reads_full_lied_immediately_and_list_omits_lyrics` ports its read/list/shape assertions from seeded rows; create is #224 |
| `ListSearchesTitlesAndFiltersByBesetzungAnlassAndDateiTyp`, `ListPagesByTwenty`; anonymous read in `CreateRequiresTitleAndEditorRole` | `lied_api` filter/paging/member gates, including aliases, all-types AND, global options, UTF-16 length and German errors |
| `AFieldTheBrowserSendsButTheCatalogDoesNotKnowIsRefused`, create/title/editor and patch/stale/missing mutations | **#224**, not claimed |
| All eight `LiedSearchModuleTests` | `lied_search_module`, unchanged selection/rank/label/order/page properties, plus literal-space, German spelling and overflow oracle |
| All five `LiedSucheQuellenTests` | `lied_suche_quellen`, including completed scan text, all creators/lyrics/labels, German variants and 499-Lieder cold latency <10s; replacement read state seeded, lifecycle #224 |
| Three `NotenTextSearchTests` | `noten_text_search` combines member/editor matching and excludes trash/other kinds/incomplete jobs/trashed parent; actual replacement finalize #224 |
| Both `SearchAgreementTests` | `search_agreement` compares member HTTP with the public shared selection/page component. Actual `catalogue_search` tool adapter comparison remains **#232**, explicitly not a full chat pass |
| `AssetStorageFailureTests.AdapterWithoutAnyConfigurationFailsWithConfigError`, `ConnectionStringModeSignsSharedKeyTicketWithoutNetwork`, `ReadTicketOverridesServeTypeInlineAndDownloadAttachment` | `asset_storage_failure` plus independent REST/ring vectors, official SDK fake-credential/HTTP contracts and real private Azurite |
| `UploadSessionReportsStorageFailureInsteadOfServerError`, `RenewReportsStorageFailureInsteadOfServerError` | **#224**; #223 separately verifies authorized read failures return exact 502 |
| Tier original Cold/Hot, separate-copy refusal, Lied video listing/view, unknown tier/missing/trash, failed provider and stale version | `datei_storage_tier`, `datei_access`, real-PG two-Origin CAS race, SQL save-failure/retry, private Azurite and native unchanged-UI harness. Recording `/access` assertion remains #227; fixture video generation remains #233 |
| `ColdTrashedOriginalIsStillRemovedByThePapierkorbRelease` | **#224/#234** permanent removal/cleanup, not a read-tier pass |
| `TrashingThePlaybackCopyReturnsTheColdOriginalToHotStorage`, `ReplacingThePlaybackCopyReturnsTheColdOriginalToHotStorage`, `RestoringAColdOriginalWithoutLiveCopyReturnsItToHotStorage` | **#224**, coordinated with #227 recording slots and #233 video trash/hooks |
| `ReattachingThePlaybackSlotReturnsTheColdOriginalToHotStorage` | **#227**, provider-before-DB restoration on slot mutation |
| `RemovingThePlaybackCopyReturnsTheColdOriginalToHotStorage` | **#224/#227/#234**, permanent removal/reference coordination |
| `ArchiveAccessTests.IsAuftrittsAufnahmeMemberVisible…`, live Lied reads | Real authenticated `datei_access` member/editor/published-recording/document/trashed-parent gates; other event/recording/passage access assertions stay #225/#227 |
| AppHost direct-download/range assertions | Provider/native components exercised here; full retargeted Rust image, all workers and unchanged AppHost assertions remain **#235** |

The old Rust unported-route fixture now names the still-unimplemented upload
route instead of the implemented Lied detail; the stable miss assertions remain
whole. No frozen assertion or frontend selector is weakened.

## Reproducing gates

Every Cargo build/test uses supported **static OpenSSL 3.5.9**, including ordinary
default-feature native builds; see [auth-dependency-audit.md](auth-dependency-audit.md).
Heavy gates run sequentially. From `backend-rs`:

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
# Explicit disposable local ARCHIVE_TEST_POSTGRES:
cargo test -p archive-server --locked --test lied_api --test lied_suche_quellen \
  --test noten_text_search --test search_agreement --test datei_access \
  --test datei_storage_tier -- --ignored
# All inherited auth/member/process gates per auth-parity.md/member-parity.md.
# Native schema/grants: baseline README's separate disposable server/pg_dump.
cargo test -p archive-server --locked --test migrate_process \
  postgres_migration_contract -- --ignored
# ARCHIVE_TEST_AZURITE = full private loopback Azurite 3.35.0 connection string:
cargo test -p archive-server --locked --test storage_azurite -- --ignored
cargo build --workspace --locked
cargo deny --locked check
```

`tests/browser/catalogue-native.cjs` requires `ARCHIVE_TEST_POSTGRES`,
`ARCHIVE_TEST_AZURITE`, `ARCHIVE_STATIC_ROOT` (unchanged exported frontend), and
optionally `ARCHIVE_BINARY` / `ARCHIVE_CHROMIUM`. Use external test-only Node
dependencies (`NODE_PATH`): the existing Playwright/pg harness dependencies plus
official `@azure/storage-blob@12.34.0`. Its official pre-sign pipeline extension
pins Azurite's API version; it does not skip version checking. The SDK only creates
private fixtures/CORS. The harness starts native migration/seed/origin/restart,
synthetic SMTP and a real local gRPC OTLP receiver; UI and authenticated API
requests use real sessions/CSRF, no frontend routes are mocked. It checks Noten
view/direct Blob HEAD/ranges/overrides, seeded video view-vs-original download,
Cold policy, same-tier no-provider behavior, exact provider-first 502, and OTLP
trace/log redaction for keys, signatures, cookies, code, object and search markers.

Hosted managed-identity acquisition, real Entra/RBAC/delegation acceptance, Azure
tier billing/latency and production access are **unauthorized/unrun**. Fake
credential/HTTP contracts and Azurite are not represented as that hosted gate.
No AI, real mail, production data, infrastructure, image, workflow, issue, PR,
push or integration/main merge is part of this ticket.

## Verification results

Final local run, 2026-11-09: merged the current integration tip first (already
up to date at `e0f716d`), then format → clippy → workspace → explicit SQL/native/
browser/storage/OTLP/regressions, all Cargo builds/tests `--locked` and isolated
static OpenSSL 3.5.9. Resource-only build settings were `CARGO_INCREMENTAL=0`,
`CARGO_PROFILE_DEV_DEBUG=0`, `-j 2`.

| Gate | Actual result |
| --- | --- |
| fmt check; workspace/all-target clippy `-D warnings` | Pass |
| Ordinary workspace | **54 passed, 0 failed, 76 explicitly ignored** (17 new ordinary checks, 37 inherited) |
| Explicit real-PG auth/access/passkey/allocation/member/operator/catalogue/tier suites | **72 passed, 0 ignored** (25 #223 + 47 inherited) |
| Explicit native migration/schema/canonical runtime-role grant gate | **1 passed**, histories/checksums/grants unchanged |
| Explicit private Azurite 3.35.0 provider contracts | **3 passed, 0 ignored**; signatures, block SAS, full 20 MiB direct download and multi-range forward read, GET/HEAD/ranges/overrides, wrong key/tamper/expiry/read-only rejection, Hot/Cold/error metadata, missing/empty reads, SDK nonempty XML signing |
| Default-feature `cargo build --workspace --locked` | Pass; this normal binary ran all native/browser/OTLP gates |
| Native inherited auth and member UI/CLI/restart/passkey/SMTP/OTLP | Final runs pass; earlier intermittent member marker assertion retained below |
| Native catalogue/Noten UI/private Blob/tier/restart/storage OTLP/redaction | Pass, no frontend mocks |
| Selected frozen C# catalogue/search/storage/access/maintenance suites | **62 passed, 0 skipped**; existing compiler/NuGet warnings unchanged |
| Active default `cargo deny --locked check` | Exit **0**: advisories/bans/licenses/sources pass; policy unchanged |
| Native linkage | Embedded **OpenSSL 3.5.9**, no dynamic libssl/libcrypto |
| Unmodified frozen Playwright: lieder, noten, uebe-dateien, mitglieder, passkey | **28 executed: 26 mocked-API UI cases passed, 2 real-backend cases failed, 0 skipped**; not counted as a full browser pass |

All **76** ordinary ignored checks were subsequently executed: 72 SQL/native
tests + 1 migration gate + 3 private storage gates. No ignored/hosted check is
included in the ordinary 54 passes.

The frozen browser failures remain the original member code-request assertion
at line **582** (202 expected, 400 actual) and passkey stale text locator at line
**102**. Actual frozen **.NET** baseline evidence is retained in
[auth-browser-baseline.md](auth-browser-baseline.md) and
[member-parity.md](member-parity.md): .NET reproduces the passkey locator at the
same line; its real member flow fails earlier at line **513**. This is **not** an
identical-member-assertion claim. Neither is a pass; no selector/auth/CSRF assertion
was edited. The independent unmocked native controls above passed. The 17 new
catalogue-related frozen mocked cases are UI regression coverage, not #224 write
endpoint evidence.

One earlier inherited `members-native.cjs` run failed its generic
`telemetry leaked member marker` assertion. Its original harness did not report
the matched needle, so the failed run cannot retrospectively be classified.
The assertion was **not weakened**: ten subsequent runs passed (nine diagnostic
runs changing only failure diagnostics/capture, then the final unmodified run).
A successful diagnostic wire capture contained 1,965 unrelated six-digit
timestamp/metadata substrings, demonstrating the predicate's possible numeric
collisions, **not proving the initial failure's cause**. Keep this intermittent
inherited harness warning visible for review rather than relabeling the failure
as a pass. No production credential/data appeared in these synthetic local runs.

Logs: `/tmp/opencode/223-{fmt,clippy,workspace,pg,migration,build,native-auth,
members-native,catalogue-native,storage,dotnet-oracles,deny}-final.log`,
`223-frozen-browser-final.log`, `223-members-native-marker-failure.log`,
`223-members-native-diagnostic.log` and `223-members-native-probe-{1..8}.log`.
Red evidence includes read/filter/Noten/tier/provider/delegation/timeout, dropped
multipart work, fractional-key-cache refresh, nonempty Shared Key body length,
trailing endpoint slash, and .NET ordering/overflow cases (`223-*-red.log`).
The executed small .NET language oracle is `223-search-language-oracle.log`.
