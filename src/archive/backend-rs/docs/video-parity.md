# Deterministic video copies (#233)

Base: accepted integration `f3614c1161ecabecae5c320ac4959382edee1738`.
This is an implementation checkpoint, **not independent acceptance**. Final
fresh combined gate results are recorded in the ticket's external evidence.
Frozen backend/tests/frontend/infra/workflows/product Dockerfile stay unchanged.

## Product boundaries

- `--transcode-queue` ensures its queue, sweeps durable Queued work and drains
  finitely. `--dispatch-videos` ensures and sweeps only. Both pause before any
  provider/DB work in maintenance, complete through checked finite telemetry,
  and use the accepted shared Queue drain (serial batches, visibility starts
  before receive, deadline=min(message,batch), release headroom, no heartbeat).
  Video commands validate one typed maintenance snapshot for entry, drain and
  per-message gates. Absent means false; present blank/malformed fails before
  providers (frozen .NET10 `GetValue<bool>`, not the origin's blank-as-absent
  convenience). Padded/case-insensitive true pauses; false permits real work.
  Actual CLI/PG/Queue controls first reproduced invalid dispatch/transcode and
  padded-true dependency access. Paused healthy fixtures retain the full job,
  original bytes and unread receipt; no private worker-only seam is introduced.
  Exact exported pause messages remain `Video worker paused: maintenance mode is
  enabled` / `Video dispatch paused: maintenance mode is enabled`, category
  `Archive.Videos`. `ARCHIVE_VIDEO_PAUSE_ONLY=1 node tests/browser/video-process-native.cjs`
  checks both actual CLI exits, decoded OTLP scope/message, command spans and
  correlated normal completion after child closure and collector drain, without
  opening providers. The same controls run in the ordinary process journey.
- `Archive:Video` retains frozen defaults: 10GiB input, three attempts, stale
  lease1h, message/visibility8h, redisplay1h, dispatch100, backoff1m, batch1/cap32,
  messages10, drain10h. Positive configured values retain their source widths;
  invalid/nonpositive setters fall back. Integer binding reuses the already
  verified BCL configuration grammar; executable paths are trimmed. Queue
  connection string wins; Video URI falls back to Extraction URI.
- `FinalizedHook` adds video_jobs in the same transaction as a Lied or Auftritt
  Aufnahme Datei. Only kind=aufnahme and ordinal-ignore-case video/* qualify.
  Quiet notification follows commit, tries twice on send failure, never resends
  immediately after an accepted send/stamp failure. Dispatcher/retry/finalize
  stamp only their selected/caller RowVersion; stamp-only saves do not bump it.
- Poison/foreign/empty-Guid notifications delete; parent-aware invisible/missing
  files and nonvideo material do no byte work. Missing jobs self-heal. Terminal
  rows do no rework; fresh Running abandons, strictly stale Running may retake.
  Row/dequeue exhaustion, exact-lease completion/recovery and selected-version
  CAS prevent an older attempt from overwriting a newer winner. Transients
  return the row to Queued before receipt backoff. Parent cancellation leaves
  its counted Running lease recoverable; deadline recovery uses a surviving
  parent, not the expired processing token.
- An expired message that never took a counted lease abandons immediately,
  without reloading a job or acknowledging a concurrent terminal row.
  `video_lifetime::preclaim_deadline_abandons_even_when_a_concurrent_writer_finishes_the_row`
  first reproduced actual receipt deletion through a blocked pre-claim SQL read;
  queued/missing-row and ordinary-terminal-delete controls distinguish it from
  row mutation or a generic timeout. A second real SQL lock gate proves receipt
  release does not depend on job-recovery SQL becoming available. Claimed native
  deadline recovery retains its separate existing tests.
- Editor `GET /api/dateien/videos?ids=…` retains CLR Guid parsing, dedup/request
  order, max100, skipped unknowns, explicit REST nulls and own-Datei-deletion
  filtering (a trashed parent is intentionally not filtered here). Retry uses
  canonical parent-aware visibility/current membership/CSRF/no-store; bounded
  insert-race recovery, freshRunning409, terminal idempotence and failed/stale
  reset preserve the frozen state matrix.
- Unknown stored integer statuses retain the frozen numeric-string payload
  (`Enum.ToString` fallback), rather than being mislabeled `failed`. The current
  schema has no status CHECK. `video_api::unknown_stored_video_status_retains_the_frozen_numeric_payload`
  exercises actual authenticated HTTP/current SQL for0–4 and four unknown signed
  values; all four unknowns failed first, while the normal five labels matched.
  Unknown signed retry replies now also compare the entire payload and persisted
  row before/after, with a positive real-Queue send control followed by no enqueue.

## Native bytes and publication

`ffprobe`/`ffmpeg` use native argv, never a shell. Both output pipes drain
concurrently; tool output/paths/provider payloads are not telemetry. An owned
supervisor survives a dropped handler future, signals currently discoverable
descendants and the dedicated process group, waits for its immediate child and
closes its owned pipes before command completion. Linux uses `/proc` and fixed
`/bin/kill` argv without adding a Cargo dependency/unsafe code. This is not an
unconditional descendant-containment or descendant-reaping guarantee; see R1
below. Ordinary waiting/same-group deadline and SIGTERM controls remain required.

ENOENT for either tool produces the exact frozen terminal German reason:

> Die Konvertierung benötigt ffmpeg, das auf dem Server nicht verfügbar ist.
> Nach dem Beheben der Einrichtung erneut versuchen.

The tools are not required at origin startup. Missing/corrupt/no-video/oversize/
exhausted material has distinct deterministic reasons; tool infrastructure errors
use bounded retry. Input streams through the existing ETag-pinned bounded reader
into unique create-new0600, flushed seekable files, and must exactly match stored
length. Output is uploaded in bounded8MiB SDK blocks, not collected in memory.

Actual policy is a **1920×1920 bounding square**, not a literal1920×1080 box.
Displayed dimensions include rotation. MP4/WebM + h264/vp8/vp9/av1 + absent or
aac/opus/vorbis audio within the bound remains one object (`notNeeded`). Copies
scale down only/floor-to-even, first video/optional first audio, libx264 CRF20,
medium/yuv420p, AAC192k, faststart MP4, no subtitles/source metadata. Unique
`dateien/<copy-id>/<attempt>.mp4` bytes leave the preservation original intact.

Copy Datei/job completion/empty recording playback slots commit in one SQL
transaction. The copy keeps owner/kind/creator and ` - Abspielkopie.mp4` name;
filled slots clear measured duration and bump recording attribution/version.
An editor's playback choice is never overwritten. Existing generic Datei view
selects the live copy while download preserves the original; recording access
retains its selected-playable-file policy. Copies stay out of ordinary lists.

## Frozen case ownership

Rust paths are `crates/archive-server/tests/` unless stated otherwise.

| Frozen method(s) | Rust owner |
| --- | --- |
| VideoProbeTests all9methods, including3theories/all26rows | `video_probe`: rotated iPhone, audio absence/no-video, all16 plan rows, landscape/portrait/odd/no-upscale/keep-zero, all5material +5container rows |
| VideoWorkerTests.AnIphoneOriginalBecomesAPlaybackCopyAndFillsTheSlot | `video_worker::original_becomes_a_real_h264_copy_and_fills_only_empty_slot`; actual native tools/private providers, H264/AAC inspection; `video_lifetime` late-editor slot race |
| AnAlreadyPlayableOriginalStaysOneObject | `video_worker::an_already_playable_original_stays_one_object`; actual generated playable bytes, no extra Datei/slot/duration write |
| CorruptMaterialFailsDeterministicallyWithGermanReason | `video_worker::corrupt_material_fails_with_the_exact_german_reason` |
| AMissingFfmpegFailsTheEnvironmentReason | `video_worker::missing_ffmpeg_or_ffprobe_is_terminal_finite_and_retryable_after_repair`; both actualENOENT paths, real HTTP retry/CLI repair |
| DuplicateMessagesNeverReworkAFinishedRow | `video_worker::duplicate_messages_never_rework_a_finished_row` |
| TransientStorageFailureRequeuesAndAbandonsWithBackoff | `video_worker::transient_storage_failure_requeues_with_backoff_then_exhausts`; genuine refused private Blob authentication |
| MaintenancePausesWithoutTouchingRowOrBlob | `video_worker::maintenance_pauses_both_video_commands_before_dependencies`; invalid providers/DB remain unopened |
| NonVideoMaterialJustDeletesItsMessages | `video_worker::nonvideo_material_only_deletes_its_messages`, both frozen rows |
| DateiInTheTrashIsNotTranscoded | `video_worker::file_and_parent_trash_are_not_transcoded` |
| OversizeInputsFailWithoutOpeningTheTools | `video_worker::oversize_inputs_fail_without_opening_tools_or_counting_attempts` |
| MissingJobRowIsSelfHealedAndProcessedToCompletion | `video_worker::missing_job_row_self_heals_with_the_file_creator` |
| MissingDateiIsDeletedWithoutAnyRowWork | `video_worker::missing_file_is_deleted_without_row_work` |
| MalformedAndForeignEnvelopesAreDeletedAsPoison | `video_worker::malformed_and_foreign_envelopes_are_deleted_as_poison` |
| LeaseAndBatchOptionsStayDefensivelyBounded | `video_worker::video_option_defaults_preserve_the_frozen_getter_matrix` (ordinary public pure defaults), `lease_and_batch_options_stay_defensively_bounded_through_cli` and real Queue observer/setter/path controls; inherited Queue drain regression controls remain |
| PumpDrainsUntilTheQueueIsEmptyAndAppliesDispositions | `video_worker::pump_drains_until_empty_and_applies_bounded_dispositions` |
| DispatcherSweepsExactlyTheUnstampedAndStaleQueuedRows | `video_worker::dispatcher_sweeps_only_unstamped_and_strictly_stale_queued_rows_in_order` |
| Additional public HTTP/current SQL boundaries | `video_api`: owner finalize/outbox/status/retry, selected-version quiet stamps/send count, rollback and concurrent finalize; `video_lifetime`: actual process tree/deadline/SIGTERM/lease/late winner/editor slot; `video_media`: actual square/rotation/decode/length/missing Blob |
| FiniteJobsTests/TelemetryTests video members | `video_worker` + `tests/browser/{video,video-process}-native.cjs`; complete remaining command matrix234 and authenticated HTTP/protobuf export235 remain distinct |
| AppHost playable video fact | Native unchangedUI/normalCLI/private QueueBlob here; actual image remains235 |
| R1 exited-leader/escaped-helper | User-approved **shared retained failure**; independent unchanged frozen-class versus unchanged Rust-module differential below, not a passing product containment gate |

Assertion follow-up (C1): the ordinary pure
`video_worker::video_option_defaults_preserve_the_frozen_getter_matrix` pins the
public `videos::Options::default()` getter matrix, including literal message
budget8h. Receive visibility/fit observations alone do not distinguish4h/8h/24h
budgets; they are not proof of that exact default. This is additive coverage of
already-correct code, not a production bug/fix or an eight-hour execution test.
The existing CLI/default/fallback controls remain. `video_worker` independently
discriminates message
dequeue4 from durable attempt2 with MaxAttempts4 (removing only the dequeue arm
actually fails); positive receipt backoff reappears within redisplay1s rather than
the configured5s×dequeue. A forwarding-only local HTTP observer records the real
private Queue requests, not substitute responses: default/invalid queue name,
batch1/message cap10,8h visibility and8h visibility-fit1 with requested batch2.
Current SQL59/61-minute leases discriminate the1h stale fallback; blank ffprobe
and ffmpeg both reach actual tools beyond all prechecks. Invalid attempt/input
setters retain default3/10GiB prechecks. Copy checks include exact name, owner,
kind, content type, creator, unique Blob path, persisted bytes/size and completed
timestamp/failure/attempt; keep checks retain their completed timestamp too.
`video_api` adds anonymous/member, missing/forged CSRF, live role downgrade and
revocation, with no-store on success and rejection. Revocation with stale
identity-bound CSRF is400 before authorization; refreshed anonymous CSRF remains
unauthorized401. No auth behavior was changed to make those tests pass.

The frozen keep test puts the same Datei in both recording slots using EF
InMemory. **Current** PostgreSQL `CK_recordings_dateien` rejects that fixture.
The Rust test keeps the playable original in its valid original-only slot and
retains one-object/no-slot/no-duration-write assertions; no constraint is waived.
At CLI worker-only cases, initial fresh enqueue stamps prevent the legitimate
pre-drain dispatcher from adding unrelated work. These are fixture adaptations,
not claims that the invalid frozen SQL arrangement succeeds.

## Reproduce

Use ticket-isolated target, supported static OpenSSL3.5.9, disposable localPG,
private Azurite3.35.0 and real executable ffmpeg/ffprobe with x264/AAC capability.
No paid model/private corpus/hosted token/external mail is involved.

```sh
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
cargo test -p archive-server --locked --test video_probe --test video_worker
cargo test -p archive-server --locked --test video_api --test video_worker --test video_lifetime --test video_media -- --ignored --test-threads=1
node tests/browser/video-native.cjs
node tests/browser/video-process-native.cjs
```

Native env uses the existing `ARCHIVE_BINARY`, `ARCHIVE_STATIC_ROOT`,
`ARCHIVE_TEST_POSTGRES`, `ARCHIVE_TEST_AZURITE`, `ARCHIVE_TEST_QUEUE`, external
`NODE_PATH` and optional `ARCHIVE_CHROMIUM`. Both real-tool and actual process-tree
gates use checked child-closed/collector-drained three-signal captures and fresh
terminal private replays per `tests/telemetry/README.md`. All inherited310 explicit
PG/schema/grants/Blob/Queue/PDF gates and18native gates/replays remain required.

## Validated implementation checkpoint (2026-11-16)

Source/test pin `66048df24ae8ae1ef5747b6aae386817ee0611d8`. Fresh combined run
`target/agent-218/evidence/233/full-20261116T153207Z` (workspace-external private
evidence) completed all54 stages with no carried stages. This is implementation
evidence, **not independent runtime acceptance, merge or cutover approval**.

| Gate | Actual result |
| --- | --- |
| fmt / workspace all-targets clippy / locked build+examples / cargo-deny | exit0 |
| Ordinary Rust | 135 harness passes, including **three absent-live runtime returns**;348 explicitly ignored gates are separate |
| Explicit current PG/schema/adoption/grants/Blob/Queue/PDF/native-tool union | 348 passed (inherited310 + video38),0 failed/ignored |
| Video ordinary / explicit | 9 probe +2 worker/defaults ordinary;6 API +9 lifetime +3 media +20 worker explicit |
| Native journeys / fresh terminal private replays | 20 /20 exit0; all child-closed/collector-drained captures checked |
| Telemetry detector / selected frozen .NET | 110 /192 passed |
| Unchanged frontend unit / check / build / selected desktop+mobile browser | 115 /exit0 /exit0 /302 passed |

`artifact-audit.json` and `terminal-review-audit.json` retain original run hashes,
exact counts,20 final capture/replay hashes and147 byte-verified frontend source
files. All43 interruption-corrupted ELF artifacts still match their preserved
hashes. Default product SHA256 is
`ee339c4a7a44b81d52632faa8dcaec4b15920b52257a432afc90179c5a03ed43`, with static
OpenSSL3.5.9 and no dynamic libssl/libcrypto. Extra absent-env checks explicitly
confirmed all three live evaluations skipped before provider construction; they
are **not live evaluation passes**. The narrower guard-test Cargo invocation
relinked a different target binary; a normal locked workspace build restored the
exact actually-tested default-product hash, recorded separately in the audit.

Prior interrupted/setup-failed runs remain incomplete/failures. The corrected
external pg_dump wrapper forwards the test-selected user/database; isolated
frontend frozen-lock modules leave shared229 modules untouched; the mock-browser
Production host supplies only synthetic configuration and uses one locked
Playwright installation. No product behavior, dependency version, frozen source
or test was changed to repair those runner setups. Lost pre-interruption `/tmp`
captures are not recovered or promoted by these fresh captures. R1's explicitly
retained **failed** cleanup boundary and all limits below remain in force.

## Retained limits

- **R1, explicit user decision 2026-11-16: retain parity.** An exited tool leader
  can leave a live helper which escaped into another session/process group.
  Later `/proc` ancestry snapshots and the old leader's process group cannot find
  that helper. Both the entire unchanged frozen `FfmpegVideoTranscoder` public
  `InspectAsync` on .NET10.0.12 and the unchanged Rust process module with exact
  locked dependencies actually returned while this helper remained live, including
  after host exit (three repetitions each, token cancellation and Rust dropped
  caller/tracker drain). This is a **failed cleanup boundary**, not containment
  success or a new guarantee. No process hardening/dependency/unsafe/infra change
  is authorized by this disposition.
  Independent provenance, exact observations and reproduction commands are in
  `/home/jakob/private/liedertafel-218-agent-notes/evidence/R1-differential.md`, with
  external hosts/driver under its adjacent `R1-differential-scratch/`.
  `python3 run.py --repeats 1 --require-contained` actually exited1; final red
  `runs/run-5b390824c3dc4f05b4bcb1dd6ebd59b9/results.json` is retained.
  `python3 run.py --repeats 3` exited0 only for **shared-failure classification**
  (`runs/run-21eaba0dab6749819af457a2e8b07cb1/results.json`), never a containment
  pass. Original/preliminary failed observations remain, including a frozen
  waiting-control zombie/open readers and an earlier Rust waiting-control zombie.
  In the final waiting controls Rust helpers were gone and its pipe readers
  closed; frozen helpers were nonlive zombies with readers still open at return.
  The driver's subreaper was only for its own post-observation cleanup: every
  known synthetic PID was killed/reaped afterward. These are Linux synthetic
  class/module observations, **not full product, real ffmpeg, OTLP or image**
  results. Actual product ordinary tree/pipe/deadline/SIGTERM/drop-tracker native
  tests and checked private replays are still required; no old failure is erased
  and no full cleanup claim is inferred from their narrower passing controls.
- video_jobs original/playback Restrict FKs remain unchanged. Permanent removal
  keeps the known23503/non-success500/no-byte-deletion refusal, **not successful
  video cleanup**. No cascade/job deletion/schema change is introduced.
- Upload-before-save can leave an unreferenced copy after a losing/failed save.
  Temp cleanup is best effort. There is no blanket orphan collector.
- Input bounds do not hard-contain ffmpeg output/temp sum/decoder CPU/memory or
  collected probe stdout. Stale1h versus default8h processing retains overlap
  risk; no heartbeat/new capacity guarantee is invented.
- Local tests use digest-verified FFmpeg-Builds n9.0 GPL binaries solely as
  isolated test tools. This does not change Cargo license policy or settle
  product-image native notices/source/update obligations for FFmpeg/libx264.
- Tiny public/generated media proves neither10GiB/two-hour concert capacity nor
  hosted2CPU/4Gi image limits, ARM/musl support, MI/RBAC or deployment readiness.
  Packaging/capacity remains235; cutover236/main/deploy need separate approval.
- Inherited shared failed full-navigation/URL-layout qualification, browser
  failures, original failed duration run/unclassified redaction, historical
  baseline equivalents and native/budget limitations remain as documented in
  predecessor guides. Later passing captures do not reclassify old failures.
