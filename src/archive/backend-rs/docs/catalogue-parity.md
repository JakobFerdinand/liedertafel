# Lieder, Datei access and storage tiers (#223)

Wave 3a starts at integration `e0f716d` (#220/#221/#222 accepted). C# backend,
C# tests, frontend, baseline, migration checksums, runtime grants, infrastructure,
workflows and production image remain unchanged. This is not #224 upload/trash,
#227 recordings, #233 video writes or a cutover.

**#223 accepted and merged as `8ab47bb`.** Independent review examined
`dc3357e` (production) and `d3d556c` (tooling), finding a terminal lifecycle gate
hole and case-sensitive query keys. Both remediations and fresh heavy-gate
evidence are in [catalogue-review-followup.md](catalogue-review-followup.md).
Historical snapshots below retain their original scopes. #224's added write,
trash, lifecycle and persistence coverage is mapped in
[upload-trash-parity.md](upload-trash-parity.md); the #223-only results below
are not retroactively upload/worker evidence.

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
| `LiedApiTests.EditorCreatesLiedAndMemberSeesItImmediately` | `lied_api::member_reads_full_lied_immediately_and_list_omits_lyrics` ports reads; #224 `lied_writes` now covers real create/member visibility |
| `ListSearchesTitlesAndFiltersByBesetzungAnlassAndDateiTyp`, `ListPagesByTwenty`; anonymous read in `CreateRequiresTitleAndEditorRole` | `lied_api` filter/paging/member gates, including aliases, all-types AND, global options, UTF-16 length and German errors; review follow-up adds a real-PG 36-case frozen-.NET query-binding matrix and live native differential |
| `AFieldTheBrowserSendsButTheCatalogDoesNotKnowIsRefused`, create/title/editor and patch/stale/missing mutations | #224 `lied_writes`; see upload/trash map |
| All eight `LiedSearchModuleTests` | `lied_search_module`, unchanged selection/rank/label/order/page properties, plus literal-space, German spelling and overflow oracle |
| All five `LiedSucheQuellenTests` | `lied_suche_quellen`, including completed scan text, all creators/lyrics/labels, German variants and 499-Lieder cold latency <10s; replacement read state seeded, lifecycle #224 |
| Three `NotenTextSearchTests` | `noten_text_search` combines member/editor matching and excludes trash/other kinds/incomplete jobs/trashed parent; actual replacement finalize #224 |
| Both `SearchAgreementTests` | `search_agreement` compares member HTTP with the public shared selection/page component. Actual `catalogue_search` tool adapter comparison remains **#232**, explicitly not a full chat pass |
| `AssetStorageFailureTests.AdapterWithoutAnyConfigurationFailsWithConfigError`, `ConnectionStringModeSignsSharedKeyTicketWithoutNetwork`, `ReadTicketOverridesServeTypeInlineAndDownloadAttachment` | `asset_storage_failure` plus independent REST/ring vectors, official SDK fake-credential/HTTP contracts and real private Azurite |
| `UploadSessionReportsStorageFailureInsteadOfServerError`, `RenewReportsStorageFailureInsteadOfServerError` | #224 `upload_session_resume`; #223 authorized read failure remains a separate gate |
| Tier original Cold/Hot, separate-copy refusal, Lied video listing/view, unknown tier/missing/trash, failed provider and stale version | `datei_storage_tier`, `datei_access`, real-PG two-Origin CAS race, SQL save-failure/retry, private Azurite and native unchanged-UI harness. Recording `/access` assertion remains #227; fixture video generation remains #233 |
| `ColdTrashedOriginalIsStillRemovedByThePapierkorbRelease` | #224 `datei_lifecycle_tiers` permanent release; #234 still owns finite cleanup CLI |
| `TrashingThePlaybackCopyReturnsTheColdOriginalToHotStorage`, `ReplacingThePlaybackCopyReturnsTheColdOriginalToHotStorage`, `RestoringAColdOriginalWithoutLiveCopyReturnsItToHotStorage` | #224 `datei_lifecycle_tiers` with seeded recording/video rows; future endpoint/worker integration remains #227/#233 |
| `ReattachingThePlaybackSlotReturnsTheColdOriginalToHotStorage` | **#227**, provider-before-DB restoration on slot mutation |
| `RemovingThePlaybackCopyReturnsTheColdOriginalToHotStorage` | #224 `datei_lifecycle_tiers` recording slot/reference coordination; frozen video-job FK limitation is explicit in upload/trash doc; #234 cleaner CLI remains |
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
official `@azure/storage-blob@12.34.0` and test decoder `protobufjs@8.8.1`.
Its official pre-sign pipeline extension
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
the matched marker or retain the failed wire. The available subsequent successful
capture is lossy concatenated UTF-8, not the failed run. Its numeric substrings
demonstrate collision potential only. Ten subsequent passes were **not a
resolution**; the deterministic detector fix and remaining ambiguity are below.

Logs: `/tmp/opencode/223-{fmt,clippy,workspace,pg,migration,build,native-auth,
members-native,catalogue-native,storage,dotnet-oracles,deny}-final.log`,
`223-frozen-browser-final.log`, `223-members-native-marker-failure.log`,
`223-members-native-diagnostic.log` and `223-members-native-probe-{1..8}.log`.
Red evidence includes read/filter/Noten/tier/provider/delegation/timeout, dropped
multipart work, fractional-key-cache refresh, nonempty Shared Key body length,
trailing endpoint slash, and .NET ordering/overflow cases (`223-*-red.log`).
The executed small .NET language oracle is `223-search-language-oracle.log`.

## Native redaction detector follow-up (2026-11-09)

Ranked falsifiable hypotheses, declared before testing: unrelated numeric
metadata collision; genuine payload disclosure; artificial cross-boundary match.
The original failure remains **unclassified**, because its wire/console and
matched marker were not retained. No new native legacy-predicate failure occurred
in this follow-up's captured runs. Do not claim a retrospective product leak,
a proved original timestamp collision, or acceptance from passing reruns.

The tight loop is `node --test tests/browser/telemetry-redaction.test.cjs` with
the pinned external `protobufjs@8.8.1` dependency. It first went red on a
minimal **exact old substring-predicate** collision: a synthetic OTP equalled
unrelated formatter microseconds, while the real console-code leak control was
detected. Subsequent red loops caught accepting malformed protocol captures,
the actual native ANSI formatter's timestamp wrapper (found in the fresh private
capture), a Base64/Base64url credential-string spelling mismatch, and leading-zero
OTPs disclosed as numeric values. Red logs:
`/tmp/opencode/223-redaction-{metadata,protocol,ansi,encoding,numeric}-red.log`
(respectively 1/2, 1/81, 1/82, 1/85 and 1/91 failing tests before each fix).

The **91 deterministic controls** now cover genuine email/code/cookie/credential
disclosures across trace/log/metric resource, scope and record attributes; trace
events/links/status/name; log bodies; nested AnyValue arrays/key-values/bytes;
integer/double measurements and packed numeric buckets; raw public keys and
canonical credential encodings; ordinary clock/ID collisions; actual fixed64
wire bytes containing a six-digit substring; plain/ANSI console clocks versus
message context; process/channel/chunk/frame boundaries; invalid UTF-8;
unknown/malformed/truncated/compressed captures; private-file permissions,
value-free reporting and replay. The actual native no-secret assertion is
exercised with injected forbidden protocol payloads on **all three services**
and console output and is required to fail. No fixed product login code or
production codegen/logging/auth bypass was introduced.

All three native harnesses now retain separate raw gRPC requests/frames and
process stdout/stderr. The shared scanner validates against the pinned official
OpenTelemetry v1.9.0 schema, inspects every payload field, and classifies only
individually named official fixed64 clocks and the native formatter's leading
clock as metadata. Numeric attributes/measurements and bytes/IDs are not blanket
whitelisted. Required safe span/metric/log presence assertions remain. Member
coverage additionally asserts actual session/CSRF cookies/request tokens and the
actually registered credential/public key before repair revokes them. Auth also
tracks cookies/tokens from the anonymous-allocation loop. See
[`tests/telemetry/README.md`](../tests/telemetry/README.md) for the exact detector,
source/license provenance and private replay contract.

### Fresh follow-up evidence (not the earlier #223 heavy-gate results)

All Cargo commands used `--locked` where applicable, `-j 2` for compilation,
isolated `OPENSSL_DIR=/tmp/opencode/221-openssl-359/install OPENSSL_STATIC=1`,
`CARGO_INCREMENTAL=0 CARGO_PROFILE_DEV_DEBUG=0`. Format, clippy and workspace
tests ran sequentially; the normal build supplied the native controls.

| Fresh gate | Result |
| --- | --- |
| Deterministic detector tests | **91 passed, 0 failed/skipped** |
| fmt; workspace/all-target clippy `-D warnings` | Pass |
| Ordinary workspace | **54 passed, 0 failed, 76 explicitly ignored** |
| Default-feature workspace build | Pass |
| Native auth/replica/restart/legacy-signature/SMTP/OTLP | Pass; **62 requests, 74 markers**, zero findings/protocol errors |
| Native member UI/CLI/passkey/restart/SMTP/OTLP | Pass; **97 requests, 29 markers**, zero findings/protocol errors |
| Native catalogue/Noten/private Azurite/tier/restart/OTLP | Pass; **53 requests, 19 markers**, zero findings/protocol errors |
| Private replay of all three final captures | Pass; findings/metadata/errors reproduce the saved reports |
| Active `cargo-deny --locked check` (0.20.2) | Exit **0**, advisories/bans/licenses/sources pass; policy unchanged |
| External Node fixture dependency `npm audit` | Exit **0**, no vulnerabilities |
| Official schema/license/provenance regeneration | Byte-identical to committed fixtures |
| Native linkage | Embedded **OpenSSL 3.5.9**, no host 3.6.5 or dynamic libssl/libcrypto |

Fresh logs: `/tmp/opencode/223-followup-{detector,fmt,clippy,workspace,build,
native-auth-final,members-native-final,catalogue-native-final,deny,node-audit,
linkage,schema}.log`. Final private replay/report directories:

- Auth: `/tmp/opencode/223-native-auth-telemetry-rFEjp5/`
- Members: `/tmp/opencode/223-members-native-telemetry-OFCVDT/`
- Catalogue: `/tmp/opencode/223-catalogue-native-telemetry-Z84j24/`

Those final runs also have zero metadata or raw-comparator matches. This is
**not evidence of the original cause**. The deterministic fixtures, not that
absence or the number of passing native runs, establish the detector correction.
The raw replays contain actual local synthetic secrets: 0700 directories/0600
files, never commit or publish them. Reports identify marker type/static label/
ordinal, service/process channel, frame/line and indexed schema field without
values, snippets or credential hashes. They persist on failures too.

**Not rerun in this test/tooling/docs-only follow-up:** the standalone 72 explicit
real-PG tests, separate native migration/schema/grants gate, three standalone
storage-provider tests, frozen .NET suites and frozen Playwright suites. Their
earlier `dc3357e` evidence and known browser failures above are unchanged, not
fresh passes. The three freshly rerun native controls do use their own real local
PG databases and the catalogue control uses private Azurite; that does not count
as rerunning those standalone suites. No production Rust/Cargo dependency/policy,
frozen source/test, infrastructure, workflow or image file changed. No hosted
identity/storage, production data, AI, real mail, push, merge, issue/PR write,
deployment or #224 work occurred.
