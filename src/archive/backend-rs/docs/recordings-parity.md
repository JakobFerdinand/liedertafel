# Aufnahmen and Zeitmarken (#227)

Base: accepted integration **5c5611a** (#225). No frozen C#/test/frontend,
infrastructure/workflow/Dockerfile, schema/baseline/grant, dependency or lockfile
changes. No video worker, AI or future endpoint is registered.

## R1 review remediation — ready for independent rereview

Independent review of `b466aa4` found one Low attribution issue: separate clock
reads made publication's `PublishedAt` differ from `UpdatedAt`, and changed
Zeitmarken in one bulk confirmation receive different `editor.updatedAt` values.
Both frozen handlers capture once. The existing fixed-clock fixtures masked this.

The scoped fix captures one instant after recording validation/no-op detection
and before the save await, reusing it for publication/update attribution. Bulk
review captures once after all selected ids/tokens validate, before the first
row-update await, reusing it for every changed row. No transaction, selected-list,
CAS, binding, timestamp bounds, membership, ticket or lifecycle behavior changes.

`recording_attribution.rs` uses raw HTTP, real PostgreSQL and a clock advancing
one millisecond on **every** read (including normal auth). Publication and
multiple-changed-row review each independently failed by one millisecond before
their respective fix, then passed. Controls preserve already-published metadata,
withdrawal/republish, normalized/current no-ops, stale publication/no-op, exact
selected review, unchanged excluded rows and atomic stale-selection refusal.
Assertions concern public payload/persisted attribution, not internal clock-call
counts. Red/green logs: `/tmp/opencode/227-r1-{publication,review}-red.log` and
`227-r1-{publication,attribution}-green.log`. The original candidate's verification
below is historical; the following are **fresh R1 executions** against the fix.

Latest integration remains `5c5611a`; merging it into this ticket was reconfirmed
already up to date before/after verification. #226 had not merged, so no extraction
union or extraction gates are claimed. Cargo uses the dedicated persistent227
target, static OpenSSL3.5.9 and `--locked`; no dependency or lockfile change.

| Fresh R1 gate | Actual result |
| --- | --- |
| Sequential fmt → workspace/all-target clippy → ordinary workspace | Pass; **57 passed /0 failed /175 explicitly ignored** |
| All explicit real-PG suites | **169 passed /0 ignored** =167 prior +2 progressing-clock regressions |
| Native exact schema/adoption/runtime grants | **1 passed** |
| Private Azurite3.35.0 | **5 passed** |
| Ordinary ignored gates explicitly exercised | **175/175 =169+1+5** |
| Default-feature workspace build; active cargo-deny | Pass; advisories/bans/licenses/sources all OK |
| Native linkage | Embedded **OpenSSL3.5.9**, no dynamic libssl/libcrypto |
| Native auth/member/catalogue/upload/events/query-binding/recordings | Final **7 passed**, real crypto/auth/SQL/private Blob/SMTP-boundary/OTLP; unchanged UI where applicable |
| Fully drained final private replays | **7 passed**, zero findings/protocol/lifecycle errors |
| Structured detector/lifecycle | **110 passed /0 skipped** |
| Unchanged scoped frozen .NET | **47 passed /0 skipped** |
| Unchanged scoped frozen mocked Playwright | **100 passed /0 skipped**, desktop+mobile, client regression only |
| Unchanged frontend persistence/reducer/storage units | **34 passed /0 skipped** |
| Committed frozen recording oracle regeneration | **8 DTOs /106 serializer +6 rounding observations**, byte-identical; not independent tests |

The first fresh recording-native run **failed** its immediate duration assertion;
its sampled value was not retained. The frozen UI exposes ready playback before
awaiting local measurement and its PATCH (`auftritt-aufnahmen.tsx:745–753`). The
owned harness now awaits the existing “Original … gespeichert.” completed-operation
message before the unchanged four-second duration assertion. No product/frontend
behavior was changed. Its complete retry and replay passed; the initial failure
remains `/tmp/opencode/227-r1-recordings-native.log`, private capture
`223-recordings-native-telemetry-NQIgxq`. Its replay still exits1 with
`incomplete-or-failed-native-run` (zero secrecy findings), **not a passing gate**.

Fresh logs: `/tmp/opencode/227-r1-{fmt,clippy,workspace,pg,schema,azurite,build,deny,
linkage,detector,dotnet,frozen-browser,frontend-unit,recording-oracle}.log`;
six inherited `227-r1-<native-name>.log`, successful recording retry
`227-r1-recordings-native-final.log`, seven `227-r1-<native-name>-replay.log`.
Final private captures (0700 directories /0600 replay and reports):

- `/tmp/opencode/223-native-auth-telemetry-ixIXgb`
- `/tmp/opencode/223-members-native-telemetry-fAfmKD`
- `/tmp/opencode/223-catalogue-native-telemetry-HbGCde`
- `/tmp/opencode/223-upload-native-telemetry-MPBq5c`
- `/tmp/opencode/223-events-native-telemetry-48irpQ`
- `/tmp/opencode/223-query-binding-telemetry-Y1UgKG`
- `/tmp/opencode/223-recordings-native-telemetry-51qRyB`

Known frozen real-browser failures, original uncaptured member-redaction ambiguity,
video-FK refusal, historical equivalents and hosted/image/live/cutover limits below
remain unchanged. The reviewer's original SystemClock attribution probe is untouched;
its independent rerun/acceptance is pending. No push, issue/PR write, integration/main
merge, deployment, production/live access or worktree/container cleanup occurred.

## Contract

Eight route patterns are added to the single API registry through `recordings::routes`:

- `GET|POST /api/events/{id}/recordings`
- `PATCH /api/recordings/{id}`
- `PUT /api/recordings/{id}/dateien/{original|playback}`
- `GET /api/recordings/{id}/access`
- `GET|POST /api/recordings/{id}/passages`
- `PATCH /api/recordings/{id}/passages/{passageId}`
- `POST /api/recordings/{id}/passages/{passageId}/delete`
- `POST /api/recordings/{id}/passages/review`

There is no recording detail GET/delete, event publication, detached passage
resource, byte proxy or new upload protocol. All requests use accepted current
membership/session/CSRF/no-store. JSON/media-type selection and raw optional
bodies reuse the accepted event boundary; JSON typed Guid remains strict D,
string paths use the accepted shared CLR parser (N/D/B/P/X, not URN).

### Publication, files and tickets

- Recording create validates trimmed label (required, 200 UTF-16 units), then
  exact audio/video kind, then live event. Initial recording token is **0**.
  Create never changes the event or Programme. Any editor may change metadata;
  only its creating account may attach files, including when the caller is admin.
- Metadata null/missing leaves fields unchanged; actual no-ops leave token and
  attribution unchanged. Optional `expectedVersion` is checked even on no-op.
  Duration must be finite, positive, at most 172800 seconds. Publishing needs
  some retained file, **not necessarily playable media**. Existing publication
  can remain when its file leaves; the resulting missing/readiness state is honest.
- Slots accept only live actual-event-owned Aufnahme Dateien. Foreign, Lied,
  document, deleted or auto-video-copy Dateien are refused. The frozen original
  exclusion means reattaching the current original returns 404; do not silently
  change it into the playback slot's idempotent response. In-use checks and the
  current schema's per-slot unique indexes are retained; no new cross-slot index.
  Attachment's missing/foreign-file title is specifically `Datei nicht gefunden.`
  (`DateiUploads`), distinct from the generic Datei-read route's title.
- Refused empty/unplayable playback Datei goes to trash; existing copy stays.
  A successful replacement trashes its earlier Datei/automatic copy, preserves
  replacement link, restores served Cold originals Hot provider-first, then CAS
  commits recording state. Duration clears only when actual played identity changes.
  Transaction-visible reads account for auto-copy trash. Seeded completed video
  jobs can fill **only an empty playback slot**; there is no transcode implementation.
- Member list requires live event and published recording. Access/passages require
  recording publication + live event for members; editors may also inspect a
  trashed event's recording via its known id, as the oracle permits. Generic
  `/api/dateien/{id}/access` remains unavailable to members for **every** actual
  Auftritt Aufnahme, whether attached/published/download-enabled or not.
- Playback prefers live playable copy, then playable original, otherwise retained
  original/copy yields `needsPlaybackCopy`, no file yields `missing`. Exact audio
  types: MP4/WebM/MPEG/WAV; video: MP4/WebM; ordinal-ignore-case. The same accepted
  catalogue predicate is reused by tier checks.
- `/access` issues signed direct private Blob inline GET/HEAD/range URLs only
  for playable files; attachment only if downloads enabled, **also for editors**.
  Both URLs target the selected playable file (or preserved file if none plays),
  not always the preservation original. Editors fetch the original separately
  through generic Datei access. No view/download ticket is minted for a withheld
  URL; all new tickets recheck membership/publication/policy. Provider errors are
  safe German 502. Existing SAS remain valid until bounded expiry after withdrawal,
  trash or revocation; no cryptographic revocation claim.

### Zeitmarken, stable entries and races

- One mark per recording/Programme entry; same Lied may have repeated entries or
  marks in multiple recordings. Entry identity comes from that recording's own
  event Programme. Marking changes neither Programme nor history counts.
- Bounds round to even milliseconds **before** validation: finite start >=0 and
  <=172800, end > start, end <=172800 and <= measured duration +1 second. Create
  requires both; current PATCH leaves omitted bounds; a mark anchored to another
  Datei requires both bounds explicitly. Identical create retry is 200 with the
  stored full display-chain shape; a different pair is a distinct-state 409.
- Writes anchoring times require `expectedPlaybackDateiId`. Missing revision is
  400, no playable file is its own 409, replaced playback is a different stale
  409. Passage version is independent of recording version; stale optional
  `expectedVersion` is checked before no-op. Delete does not need a playable file.
- Bulk review requires explicit nonempty ids/tokens, deduplicates effects but
  checks **every** supplied token. Foreign/missing/stale member refuses the whole
  review before effects. Only named outdated marks reanchor; already-current marks
  remain unchanged, and response includes the complete list. SQL transaction/CAS
  rolls back all updates on a race. Named unique and parent FK violations map to
  specific conflicts; unrelated constraints stay unexpected failures.
- Replacing/trashing/removing playback does not rewrite marks. `needsReview` is
  computed from the current played identity. Editors retain old times/anchor;
  members/history get null times until edited/confirmed. Live Lied titles filter
  passage display; member payload omits editor candidates, revision and duration.
  History uses inherited ICU culture equality before passage-id ties.
- Real two-origin CAS and unique constraints arbitrate concurrent creation/edit/
  slot changes. A forced native SQL/advisory-lock barrier exercises both original
  replacements after their read snapshots: only one survives; even a **Datei/tier**
  CAS loser gets the recording's stale-state title, like the frozen save's catch.
  As in the oracle, playback is checked when read, not locked across
  every later statement; no invented atomic cross-resource snapshot guarantee.
  Any mark, including unpublished, blocks Programme entry removal via the accepted
  guard/Restrict FK. Reorder retains mark ids and updates their computed position.

## Per-file oracle ownership

Rust siblings are under `crates/archive-server/tests/`. Grouped assertions count
as one test, not as multiple frozen test passes.

| Frozen file / precise cases | Rust evidence / later ownership |
| --- | --- |
| `AuftrittAufnahmeRegelnTests.cs` all 4 facts | `auftritt_aufnahme_regeln`: publish/file/download, unpublished/member/editor/CSRF, empty/still/foreign refusal, duration/playback/trash; additional retained-unplayable policy and full metadata shape/no-op/token/UTF-16 controls |
| `AuftrittDateienTests.cs` recording facts 4–6 | `auftritt_aufnahme_regeln`, `recording_passage_api`, `recording_passage_followup`, `recording_lifecycle`: real event create→shared upload→attach→publish→ticket; validated copy/creator replacement/trash; actual Zeitmarke replacement→member withholding. Document/photo facts 1–3 remain accepted #225 regression, not claimed newly translated |
| `RecordingPassageApiTests.cs` all 10 facts | `recording_passage_api`: two Lieder/two recordings/full display chain/history counts; whole playback/unmarked Lied; entry candidates/order; all bounds/event/duplicate/retry/missing checks; PATCH/no-op/token/retarget-ignore/delete; all auth paths/live membership; publication/withdrawal; changed-playback withholding/reanchor; review/no-op; real stable Programme removal guard/reorder/removal |
| `RecordingPassageApiTests.Followup.cs` all 5 facts | `recording_passage_followup`: stale playback on create/PATCH/review + no effects, missing revision/list/foreign tokens, exact selected atomic review, stale own token even when current, lost playable file; `recording_passage_api` covers all anonymous/CSRF mutations; `recording_races` independently checks real current constraints and exact named-error mapping through HTTP, including wrong-code/name negative controls |
| `RecordingPassageApiTests.Scenario.cs` helper | `support/recordings.rs`: real login/current membership/SQL/event/Lied/Programme/upload/finalize/attach/publication. Only storage delivery is fake in ordinary API fixtures; private Azurite/native UI use actual signed provider operations |
| `DateiStorageTierTests.cs::ReattachingThePlaybackSlotReturnsTheColdOriginalToHotStorage` | `recording_lifecycle` real attach→Cold→reattach with provider-failure rollback and actual replacement link. Inherited tier/trash/restore/removal suites remain mandatory; video job creation/retry/transcode remains **#233** |
| `LiedHistorieTests.cs` all five accepted cases + live recording links | Inherited `lied_historie`, `event_culture`, `auftritt_programm` remain regression gates; `recording_passage_api/followup` add actual recording/passage writes and computed member/editor projections, not seeded-only claims |
| `WalkingSkeletonTests.cs` concert recording segment | `recording_storage` signed upload→finalize→attach→publication→GET/HEAD/206→policy/tamper; `tests/browser/recordings-native.cjs` actual unchanged UI WAV upload/measurement/marks/Programme reorder/history/playback/download/publication/replacement/review/restart/OTLP. Full actual Rust **image/AppHost retarget is #235**, not a native-binary substitute claim |
| Frozen frontend recording/zeitmarken suites | Unchanged mocked Playwright regression plus native actual UI gate. Mocks prove client rendering only, not SQL/signatures. Synthetic WAV generated by the harness; no real media/private member fixtures |
| Current schema/constraints/grants | `recording_races`, inherited explicit `migrate_process` schema/grants and `historical_baseline`. No new schema needed; no current SQL case waived |
| Recording publication and selected-review attribution (`RecordingEndpoints.cs:176–194`, `RecordingPassageEndpoints.cs:307–313`), independent review R1 | `recording_attribution`: two raw HTTP/real-PG tests with a deterministically progressing clock; equal per-operation stamps, no-op/already-published/withdrawal/republish/stale and exact-selected atomic review controls. Original fixed-clock suites masked the finding; this adds boundary evidence, not a new clock-call-count contract |
| Historical EF migrations | #227 has **no new historical transformation waiver**. Only individually mapped approved programme/old-model equivalents in `historical-migration-disposition.md` remain applicable. Historical transforms are **NOT executed by Rust**; reading/A2UI historical equivalents stay #229/#232 |

## Independent binding evidence

`tests/oracles/RecordingOracle.cs` is an external net10.0 console probe referencing
the unchanged frozen assembly plus `Microsoft.AspNetCore.App`, never a backend
source edit. Executed external project: `/tmp/opencode/227-recording-oracle/`.
Frozen assembly MVID `d2b58c7a-9545-454e-9618-e64deb4d08de`, SHA256
`a31547b75ae0fdd0b93547d52e6817c0d90cb2e056e7a5e005164de517ba2b89`.
**8 DTOs /106 serializer observations /6 rounding observations**, not 120 tests.
Reproduce `dotnet run --project /tmp/opencode/227-recording-oracle/Committed.csproj`.
This compiles the committed generator directly; its output was byte-compared
against the original external probe's106 observations and matched exactly.

The accepted occurrence-streaming binder is extended route-locally, not replaced.
Every typed duplicate is validated before last-arrival case-insensitive replacement;
unknown members are skipped. Nullable bool/string/typed Guid/ordinary uint rules
remain distinct. Double accepts numeric decimal/exponent tokens, quoted signs,
decimal/exponents, `.5`, `1.`, exact named `NaN|Infinity|-Infinity`; quoted whitespace,
lowercase names, `+Infinity` and **quoted** numeric overflow fail binding. Unquoted
numeric overflow binds nonfinite and receives the domain-specific duration/start/end
error. Nonfinite representation is confined to request binding, never persisted or
returned as invalid JSON. Rust controlled400 versus frozen Development binding500
qualification remains explicit (non-Development frozen400); no universal status claim.

## Reproduce / final verification

Dedicated disposable PG17.6 `archive-227-pg`/55427, private Azurite3.35.0
`archive-227-azurite`/51027→51027 (self-reachable copy topology), ephemeral native
HTTP/SMTP/collector ports and random private DB/container fixtures. No #226 service
or target is reused. Scoped target: ignored root `target/ticket-227`; embedded
static OpenSSL **3.5.9** at `/tmp/opencode/221-openssl-359/install`; `--locked` always.

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
cargo test -p archive-server --locked --test auftritt_aufnahme_regeln \
  --test recording_passage_api --test recording_passage_followup \
  --test recording_binding --test recording_lifecycle --test recording_races -- --ignored
cargo test -p archive-server --locked --test recording_storage --test storage_azurite -- --ignored
node tests/browser/recordings-native.cjs
node --test tests/browser/telemetry-*.test.cjs
cargo deny --locked check
```

Inherited full explicit PostgreSQL/schema/native crypto/auth/member/catalogue/upload/
event/query-binding gates also remain required. Native env uses `ARCHIVE_BINARY`,
`ARCHIVE_STATIC_ROOT`, local `ARCHIVE_TEST_POSTGRES`, private `ARCHIVE_TEST_AZURITE`,
external NODE_PATH/Chromium as in predecessor runbooks. Drain/replay captures under
0700/0600 per `tests/telemetry/README.md`; never commit actual marker/replay values.

Known frozen passkey/member real-browser failures remain **failures**, not erased
by mocked/native passes. Video-job FK23503 permanent-removal refusal remains a
refusal, not successful cleanup; no frozen FK behavior was silently fixed. The
original uncaptured member-redaction ambiguity stays **unclassified**. Hosted
Entra/RBAC/storage, actual Rust image/AppHost, live AI/mail/Neon/production/cutover
remain unrun and outside this ticket's authorization.

## Original candidate verification (b466aa4, before R1)

Latest integration `5c5611a` merged/reconfirmed **already up to date** before the
final sequential fmt→clippy→workspace gates. All Cargo commands used `--locked`,
static OpenSSL3.5.9 and the dedicated ignored `target/ticket-227`. No dependency,
lockfile, toolchain or cargo-deny policy change.

| Gate | Actual result |
| --- | --- |
| fmt; workspace/all-target clippy `-D warnings` | Pass |
| Ordinary workspace | **57 passed, 0 failed, 173 explicitly ignored** |
| All explicit PostgreSQL suites | **167 passed, 0 ignored** (144 inherited +23 recording HTTP/SQL tests) |
| Separate native exact schema/adoption/grants | **1 passed**; baseline/schema/grants unchanged |
| Private Azurite3.35.0 | **5 passed** (4 inherited +1 recording audio/video signed upload/access/policy/range/tamper roundtrip) |
| Ordinary ignored gates explicitly executed | **173/173 =167+1+5**; none counted as ordinary passes |
| Default-feature workspace build; active cargo-deny | Pass; advisories/bans/licenses/sources all OK |
| Native linkage | Embedded **OpenSSL3.5.9**, no dynamic libssl/libcrypto |
| Structured telemetry detector/lifecycle | **110 passed**, 0 skipped |
| Native auth/member/catalogue/upload/events/query-binding/recordings | All **7 passed**, real auth/SQL/private storage/local delivery/OTLP; actual unchanged UI where applicable; query binding is36 actual .NET↔Rust cases |
| Private fully drained capture replays | All **7 passed**, zero findings/protocol/lifecycle errors |
| Unchanged frozen .NET selected recording/Programme/history/tier suites | **47 passed**, 0 skipped |
| Unchanged frozen Playwright selection | **100 passed**, desktop+mobile; API-mocked client regression, not backend/provider proof |
| Unchanged Auftritt persistence/reducer + storage frontend units | **34 passed**, 0 skipped |
| Actual frozen recording binder | 8 reflected DTOs,106 serializer +6 rounding observations; not independent tests |

The final forced file-CAS race first failed with the generic Datei stale title,
then passed after scoped recording-save mapping. Earlier unported-route reds,
typed/bounds/policy matrices and current lifecycle/race greens are retained outside
the checkout. Initial native selector/setup failures are not included as passes.
The final per-assertion audit also pinned current-token PATCH bound failures,
mixed published/unpublished recordings, and attachment's upload-specific 404
title (focused red→green) rather than relying on generic file-read wording.

Logs: `/tmp/opencode/227-{fmt,clippy,workspace,pg,schema,azurite,build,deny,linkage,
detector,dotnet,frozen-browser,frontend-unit}-final.log`; seven
`227-{native-auth,members-native,catalogue-native,upload-native,events-native,
query-binding-native,recordings-native}{,-replay}-final.log`; actual frozen probe
`227-recording-oracle/observed.log`; forced race `227-file-cas-{red,green}.log`.
Attachment title: `227-attachment-title-{red,green}.log`.

Final private capture directories (0700, replay/report0600; paths only, never
publish actual marker values):

- `/tmp/opencode/223-native-auth-telemetry-GLgXwv`
- `/tmp/opencode/223-members-native-telemetry-RUn8O5`
- `/tmp/opencode/223-catalogue-native-telemetry-KFR2rU`
- `/tmp/opencode/223-upload-native-telemetry-v5e5yp`
- `/tmp/opencode/223-events-native-telemetry-E099MN`
- `/tmp/opencode/223-query-binding-telemetry-KcA4WI`
- `/tmp/opencode/223-recordings-native-telemetry-8xZe7L`

Known frozen real passkey/member failures were **not rerun** in the mocked browser
selection and remain failures. Current Rust SQL video-FK refusal controls were
rerun among the167 PG tests, not recast as successful cleanup. Historical EF
transforms were **NOT executed by Rust**; current SQL/schema/constraints/grants
were executed, not waived. No push, issue/PR write, merge into integration/main,
deployment, production/live access or worktree/container cleanup was performed.
This candidate is ready for **independent review**, not an accepted wave-6 gate.
