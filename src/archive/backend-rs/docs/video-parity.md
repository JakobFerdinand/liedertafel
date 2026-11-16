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
- Editor `GET /api/dateien/videos?ids=…` retains CLR Guid parsing, dedup/request
  order, max100, skipped unknowns, explicit REST nulls and own-Datei-deletion
  filtering (a trashed parent is intentionally not filtered here). Retry uses
  canonical parent-aware visibility/current membership/CSRF/no-store; bounded
  insert-race recovery, freshRunning409, terminal idempotence and failed/stale
  reset preserve the frozen state matrix.

## Native bytes and publication

`ffprobe`/`ffmpeg` use native argv, never a shell. Both output pipes drain
concurrently; tool output/paths/provider payloads are not telemetry. An owned
supervisor survives a dropped handler future, kills actual descendants and the
dedicated process group, then waits/reaps before command completion. Linux uses
`/proc` and fixed `/bin/kill` argv for safe tree signalling without adding a
Cargo dependency/unsafe code. This is Linux-native evidence, not Windows parity.

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
| LeaseAndBatchOptionsStayDefensivelyBounded | `video_worker::lease_and_batch_options_stay_defensively_bounded_through_cli`, actual configured child processes; inherited Queue drain regression controls remain |
| PumpDrainsUntilTheQueueIsEmptyAndAppliesDispositions | `video_worker::pump_drains_until_empty_and_applies_bounded_dispositions` |
| DispatcherSweepsExactlyTheUnstampedAndStaleQueuedRows | `video_worker::dispatcher_sweeps_only_unstamped_and_strictly_stale_queued_rows_in_order` |
| Additional public HTTP/current SQL boundaries | `video_api`: owner finalize/outbox/status/retry, selected-version quiet stamps/send count, rollback and concurrent finalize; `video_lifetime`: actual process tree/deadline/SIGTERM/lease/late winner/editor slot; `video_media`: actual square/rotation/decode/length/missing Blob |
| FiniteJobsTests/TelemetryTests video members | `video_worker` + `tests/browser/{video,video-process}-native.cjs`; complete remaining command matrix234 and authenticated HTTP/protobuf export235 remain distinct |
| AppHost playable video fact | Native unchangedUI/normalCLI/private QueueBlob here; actual image/AppHost remains235 |

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

## Retained limits

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
