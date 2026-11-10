# Auftritte, Programme and initial Entwurf persistence (#225)

Base: accepted integration `4aab55c` (#224). Frozen C#, tests, frontend,
infrastructure, workflows, image, migrations, EF baseline and grants stay unchanged.
The existing upload/trash/tier lifecycle is reused. No AI, worker or recording
mutation endpoint is registered here.

## Contracts and sequencing

- Saved Auftritte are immediately visible to active members; editor writes use
  the existing session/CSRF decision. No event publication/revision/evidence
  workflow. Event create starts at token **0**; every successful PATCH bumps it,
  even an empty/null patch. PATCH validation precedes optional
  **expectedRowVersion** comparison; null leaves strings/date, empty clears
  optionals, a date object replaces all four parts. Actors come from the session.
- Dates stay unknown/year/month/day, 1800–2100 with real leap dates and exact
  HH:mm times. Newest order is year descending, day precision before month before
  year (even January day before December month), then month/day descending/Guid.
  Years navigation always summarizes all live events, independently of filters.
  Upcoming uses PostgreSQL's Europe/Vienna conversion of the injected clock,
  precision-aware bounds, coarse-first ascending date order and unknown last.
- One programme per event, enforced by the baseline unique key. First PUT ignores
  the supplied token and creates even an empty programme at version **1**.
  Subsequent PUT requires the current token **before domain item validation**.
  Full-array replacement preserves known IDs; omitted/foreign/unknown IDs receive
  fresh server IDs. Repeated Lieder are valid; duplicate requested IDs/null entries
  are not. Two-phase parking above max(old,new count) and final 1..n positions
  commit with attribution/CAS in one SQL transaction; event token/time stay put.
  First-create uniqueness/FK races retain the oracle's nonfriendly failure paths.
- Any seeded Zeitmarke, including an unpublished recording, blocks removing its
  entry. Restrict FK remains intact; reordering preserves marks. Keeping a known
  entry while changing its Lied is legal. Conflict names are distinct, culture
  ordered, bounded to three plus remainder. ICU root collation (`und-x-icu`) on
  local PG17 supplies the frozen Linux/invariant-culture label order; no general
  portability claim across arbitrary host CurrentCulture settings is made.
- Lied history is derived from programme entries plus live Lied-owned Aufnahmen
  naming a live Auftritt, grouped per event and paged at 20 with clamping and
  full-set aggregates. Seeded recording links honor publication/editor visibility,
  resolved live playable slots and needsReview timestamp withholding. These are
  read/guard controls, not recording endpoint/worker acceptance. Unknown dates
  count upcoming for programmes; the later chat's `IsPlanned` rule requires a
  known year (#232 must retain that distinction).
- Documents are live **actual event-owned sonstiges**, not Lied files merely
  naming the event or event Aufnahme slots. Description PATCH is available to
  every editor, normalizes/validates before optional stale check, and increments
  only on an actual normalized change. Its response is the full Datei summary,
  not the document art projection. Upload/finalize/replacement ownership, magic
  bytes, signed direct Blob transfer and trash/restore remain the accepted #224
  protocol, without a parallel event upload system.
- Initial Auftritt Entwürfe are owned, editor-only, no-store whole-form persistence
  with last-write-wins, 25-draft/50-count/raw-title≤200/raw-form≤100000 UTF-16 limits.
  Empty title is valid; count is not reconciled with opaque browser content.
  Exact lowercase numeric `version:1` is required; 1.0/1e0/string/Version fail.
  Raw JSON preserves lexemes, whitespace and dictionary duplicates in storage and
  responses. DELETE is bodyless and repeatably 204, including foreign/missing IDs.
  A browser form never acquires real Datei ownership. #231 owns actual scan/model
  suggestions and their later persistence-integrated regression.

## Binding evidence

The shared accepted streaming binder is extended route-locally, validating each
recognized typed occurrence before case-insensitive last-arriving replacement.
Unknown event fields are skipped as raw JSON (not a union DTO). Ordinary int32
accepts quoted signs/leading zeroes but not surrounding spaces/floats/exponents;
ordinary uint32 accepts quoted digits/leading zeroes, not '+', negatives or spaces.
The custom Lied rowVersion rule remains unchanged. Nonnullable booleans reject
explicit null; nullable strings and typed D-format Guid retain their distinctions.
`formular` alone is captured with serde_json RawValue (a feature of the existing
dependency), never coerced into DTO fields or a numeric-normalizing Value map.

Actual unchanged frozen assembly probe:
`/tmp/opencode/218-event-binding-probe/Program.cs`, fresh output
`/tmp/opencode/225-frozen-dto-oracle.log`: **7 reflected DTOs, 157 serializer
observations, 9 validator invocations**. MVID
`d2b58c7a-9545-454e-9618-e64deb4d08de`; SHA256
`a31547b75ae0fdd0b93547d52e6817c0d90cb2e056e7a5e005164de517ba2b89`.
Those observations are not independent HTTP-suite passes. Empty bytes from a
serializer alone do not establish optional HTTP-body semantics. Frozen Development
typed-binding failures can return **500**, versus non-Development **400**; Rust's
controlled 400 is not claimed as universal environment-identical error behavior.

Fresh actual HTTP probe `/tmp/opencode/225-http-oracle/{Program.cs,observed.json}`
records **71 observations** against unchanged `LiedTestHost`: event/history
case/empty/repeated queries, optional JSON bodies, typed failures, ignored unknown
huge numeric data, bodyless DELETE and content-type/path constraints. Missing or
non-JSON write media types select the API 404; JSON and vendor `application/*+json`
select the handler (including zero bytes). Draft `:guid` paths accept CLR N/D/B/P/X
and surrounding whitespace, reject URN/invalid paths before auth; this differs
deliberately from strict D-format **JSON** IDs. Frozen Development invalid scalar
queries and typed bodies return 500; semantic validation remains its normal 400.
The freshly built frozen HTTP assembly MVID is
`832e3b4f-1e79-46fd-bb6b-5ae7f06ad5ed`, SHA256
`ed159fb2c988fdba053e68c8747d76ac5d2e6e623a00a081d3276e782c258ee9`;
its source tree is unchanged. These are observations, not additional suite passes.

## Per-file oracle map

Rust test files below are under `crates/archive-server/tests/`. Multiple assertions
inside a test are controls, never multiplied into reported test counts.

| Frozen file / complete scoped cases | Rust sibling / later owner |
| --- | --- |
| `EventsApiTests.cs` all 7 facts, including pure date display cases | `events_api`: auth/immediate visibility/no publish, second-editor attribution, full date replacement/clear/no-op/stale validation order, all list/summary/filter and German validation/display assertions; HTTP UTF-16/leap/time/query/CSRF controls added |
| `AuftrittProgrammTests.cs` all 4 facts | `auftritt_programm`: create/embed/upcoming/resolve/repeats, stable reorder/removal/new IDs and stale no-write, all invalid entries/missing event/editor gates; additional two-origin CAS, unique positions, attribution and Vienna precision controls |
| `AuftrittEntwurfApiTests.cs` all 11 facts + 4 theory examples | `auftritt_entwurf_api`: full form replace/list/newest/privacy/editor/CSRF/repeat discard/not-an-event/mismatched ID/all shape/size/count/25-draft cases; `event_binding` raw opaque/version/length/typed-Guid/bodyless controls; #231 owns scan/model semantics only |
| `LiedHistorieTests.cs` all 5 facts | `lied_historie`: programme order/duplicates/uncertain counts, live reorders/removal, seeded Lied-Aufnahme references on both pages and trash exclusion, missing/anonymous, fixed/clamped deterministic pages |
| `AuftrittDateienTests.cs` facts 1–3 | `auftritt_dateien::editor_creates_auftritt_uploads_document_photo_replaces_and_any_editor_edits_metadata` creates the real event then shared sessions/finalize/private-access contracts; inherited seeded owner control retained. Facts 4–6 (recording attachment/publication/playback/Zeitmarke replacement) are **#227**, not blanket file-level passes |
| `DateiEndpoints.cs` description PATCH + frozen `auftritt-dokumente.spec.ts:506–645` | `auftritt_dateien` metadata null/no-op/clear/stale/validation/any-editor/foreign-owner/trash matrix; `event_binding` typed wire-occurrence controls; `tests/browser/events-native.cjs` unchanged real UI edit/replace/photo/direct member Blob access |
| `RecordingPassageApiTests.cs::ProgrammeEntryWithZeitmarkenCannotBeRemovedBehindTheirBack`; `RecordingPassages.cs` link projection | `auftritt_programm` seeded unpublished guard/reorder/FK and editor/member current/needsReview reads. Full recording-write scenario remains **#227** |
| `AuftrittProgrammMigrationPostgresTests.cs`, `OldModelContractPostgresTests.cs` | **Approved baseline equivalents**, `historical_baseline` native head adoption/full-row preservation/rerun and older-history/obsolete-remnant rejection. Historical transforms **NOT executed**. Detailed per-assertion disposition: [historical-migration-disposition.md](historical-migration-disposition.md) |
| `RuntimeGrantsTests.cs` | Inherited explicit `migrate_process::postgres_migration_contract`: independent schema and actual runtime roles/grants; no waiver |
| `auftritte.spec.ts`, `programm.spec.ts`, `auftritt-dokumente.spec.ts`, `lied-historie.spec.ts` | Unchanged mocked browser regressions plus native real-origin controls. Mock passes do not prove HTTP/SQL/storage. `auftritt-ablegen.spec.ts` manual/persistence controls are #225; mocked scan assertions are UI regression only, actual scans **#231**; recording flows **#227** |
| `lib/auftritt-entwurf/{gespeichert,reducer}.test.ts` | Unchanged pure frontend regressions; native persistence restart/resume pins the byte-free browser/server seam |

## Reproduce / remaining boundaries

Use an isolated persistent CARGO_TARGET_DIR, static OpenSSL 3.5.9, `--locked`,
local throwaway ARCHIVE_TEST_POSTGRES and private ARCHIVE_TEST_AZURITE as in the
accepted upload runbook. Run fmt check → clippy → ordinary workspace sequentially,
then **all explicit** inherited/new PG, schema/grants and Azurite gates. Native
`node tests/browser/events-native.cjs` uses the unchanged export and external
NODE_PATH/ARCHIVE_CHROMIUM like `upload-native.cjs`; it exercises drop/manual save
despite the unported reader, owned restart/resume, programme search/save/reorder,
history, document PATCH/replacement/member Blob read and photo upload. It makes
no scan/model or recording-route success claim. Drain and replay every private
capture per `tests/telemetry/README.md`.

Inherited real frozen browser failures, video-job FK removal refusal and original
uncaptured redaction ambiguity remain documented in predecessor runbooks; a fresh
native pass does not erase them. Hosted storage, actual Rust image/AppHost,
production, live AI/mail and cutover remain later/approval-gated.

## Verification (2026-11-10)

Sequential fmt check → workspace/all-target clippy `-D warnings` → workspace tests
passed on the final source. All Cargo build/test/audit commands used `--locked`,
static OpenSSL 3.5.9, CARGO_INCREMENTAL=0, CARGO_PROFILE_DEV_DEBUG=0 and two jobs.
Isolated target: repository-ignored `target/ticket-225` on the root filesystem,
not tmpfs. Integration `4aab55c` was confirmed ancestor/merged before handoff.

| Gate | Actual result |
| --- | --- |
| fmt; clippy | Pass |
| Ordinary workspace | **57 passed, 0 failed, 143 explicitly ignored** |
| Explicit real-PG suites | **138 passed, 0 ignored** = 119 inherited + 19 wave-4 tests; includes native historical head-equivalent and current constraint/CAS guards |
| Separate native schema/parity/runtime-grant gate | **1 passed**; frozen baseline/grants unchanged |
| Private Azurite 3.35.0 | **4 passed**, signed transfer/range/block/resume/copy/tier/lifecycle controls |
| Default-feature workspace build; active cargo-deny | Pass; advisories/bans/licenses/sources all OK, existing policy unchanged |
| Native linkage | Embedded **OpenSSL 3.5.9**, no dynamic libssl/libcrypto |
| Structured detector/lifecycle | **110 passed**, 0 failed/skipped |
| Native auth/member/catalogue/upload/events/query-binding | All **6 pass**; real auth/SQL/SMTP-boundary/OTLP, private Blob where needed, unchanged UI where applicable; query differential is **36 actual .NET↔Rust cases** |
| Private final replay | All **6 pass**, zero findings/protocol/lifecycle errors |
| Unchanged frozen .NET selected suites | **37 passed, 0 skipped**; includes three recording-owned AuftrittDateien facts, not claimed as Rust #225 translations |
| Actual frozen binding probes | **157 serializer observations + 9 validator calls**, **71 HTTP observations**; observations, not additional independent test passes |
| Unchanged Auftritt frontend reducer/persistence unit tests | **29 passed**, 0 skipped |
| Unchanged frozen Playwright selection | **76 passed**, desktop + mobile, API-mocked static UI including scan-shaped UI regressions; not model/HTTP/SQL/provider acceptance |

All **143/143** ordinary ignored gates were subsequently explicitly executed
(138 + 1 + 4). No unconfigured/ignored test is counted as a pass. Historical
transforms were **NOT executed by Rust**; current feature SQL/constraints and the
independent schema/grant gate were executed, not waived.

Logs: `/tmp/opencode/225-{fmt,clippy,workspace,pg,schema-grants,azurite,build,deny,
linkage,detector}-final.log`, `225-{native-auth,members-native,catalogue-native,
upload-native,events-native,query-binding}-{final,replay-final}.log`,
`225-dotnet-oracles.log`, `225-frozen-dto-oracle.log`, `225-http-oracle.log`,
`225-frontend-unit.log`, `225-frozen-browser-final.log`. Red/green logs retain
event creation, programme, draft, history, documents, raw unknown data,
content-type selection, Guid path and seeded unknown-date ordering loops.
Initial harness setup/selector failures and unavailable cargo-deny PATH were
corrected before these final runs; their earlier logs are not passes.

Final private captures (0700 directories, 0600 replay/reports; never publish values):

- `/tmp/opencode/223-native-auth-telemetry-ru8DI6`
- `/tmp/opencode/223-members-native-telemetry-KUXHk5`
- `/tmp/opencode/223-catalogue-native-telemetry-trJVMD`
- `/tmp/opencode/223-upload-native-telemetry-2MetqE`
- `/tmp/opencode/223-events-native-telemetry-0maxMw`
- `/tmp/opencode/223-query-binding-telemetry-1cqxLG`

Previously documented real frozen passkey/member browser failures were **not
rerun** in the mocked selection and remain failures, not passes. The preserved
video-job FK removal refusal remains a refusal control, not successful cleanup.
The original uncaptured redaction failure remains unclassified; later clean
captures do not establish its cause. No live AI/mail/Neon/production operation,
push, issue/PR update, integration/main/cutover merge, deploy or worktree cleanup
was performed. #227 recording writes, #231 scan/model behavior, #229/#232 their
historical equivalents and image/AppHost/cutover gates remain with their owners.
