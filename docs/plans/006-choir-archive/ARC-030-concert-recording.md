---
id: ARC-030
status: done
phase: core
kind: slice
depends_on: ["ARC-018", "ARC-025"]
touches: ["recordings", "event-materials", "media-player", "db-migrations"]
external_inputs: []
---

# ARC-030 — Publish and play a whole concert recording

**Depends on:** [ARC-018](ARC-018-practice-audio.md),
[ARC-025](ARC-025-event-documents.md).

## Outcome

An editor uploads an event recording and a member can watch/listen to it before
any song timestamps have been entered.

## Acceptance criteria

- [x] Introduce recording identity, event ownership and original/playback asset
  references; one verified playable original may serve both roles.
- [x] Add audio/video viewing with seeking and the existing renewable ticket
  flow, labels, publication control and missing/unsupported playback states.
- [x] Allow multiple independently labelled recordings of the same event; merely
  uploading another recording never creates a performance occurrence.
- [x] Preserve an incompatible original and support attaching an externally
  converted playback file; validate the candidate before marking it usable.
- [x] Default concert downloads to disabled and enforce editor-controlled
  enablement in API/access responses as well as the UI.

## AI assistance

None. Audio analysis, including automatic boundary detection, stays out of scope. Decisions: [architecture §14](architecture.md#14-ai-assistance).

## Verification

Publish a short video and a separate audio recording, play/seek as a member,
replace an unusable playback candidate and exercise explicit download permission.
Confirm the event remains useful without timestamps and no history count changes.

## Handoff and parallel work

Expose stable recording IDs, duration/playback metadata and player positioning to
ARC-032. ARC-031 can build performance history concurrently. Large-transfer and
Cold-storage validation are separate, explicit consumers of this playable flow.

## Progress

Implemented on `main` (2026-10-05). The ticket has no AI part.

Design decisions:

- A recording is its own row (`recordings`, `backend/Recordings/`) with a
  stable id, owned by one event, with a label, a kind (`audio`/`video`), a
  publication stamp, a download switch and an optional measured duration.
- Its files are two event-owned assets that reuse the ARC-015 revision and
  ticket contracts: `recording-original` (any format, only an empty file is
  refused) and the optional `recording-playback`. No object is duplicated: a
  playable original is what members stream until a playback copy has a file.
- "Validated" means a server-side container check at finalize
  (`RecordingFormats`: MP4/M4A, WebM, MP3, WAV for audio; MP4, WebM for
  video), judged from the leading bytes for the recording's kind. It is not a
  decode. A candidate that fails is refused with 422 and never becomes
  current; the previous copy stays. A file that passes but does not play in a
  member's browser is reported by the player, and the editor replaces the
  copy (a further revision of the same slot).
- Who may do what: every editor may relabel, publish and switch downloads;
  only the editor who created the recording may change its files (the
  existing `MayChangeCurrentFile` rule for event-owned assets, plus the same
  check on opening the playback slot).
- Members reach recording files only through
  `GET /api/recordings/{id}/access`. The generic asset ticket answers 404 for
  members on recording assets; editors keep it (original download for
  conversion, ARC-033 history endpoints).
- Downloads are off by default. With the switch off no download ticket is
  issued at all. With it on, the download is the file members play, or the
  preserved original when nothing is playable.
- Publishing needs at least one file (409, "not allowed in this state");
  a stale `expectedVersion` is a different 409 ("reload"). The UI reloads and
  discards the form only for the stale one.
- `durationSeconds` is measured by the uploading browser from the local file
  and cleared server-side whenever the file members play changes.
- Migration `20261005104333_ConcertRecordings`: new table only, nothing to
  backfill.

Endpoints: `GET`/`POST /api/events/{id}/recordings`,
`PATCH /api/recordings/{id}`, `POST /api/recordings/{id}/playback`,
`GET /api/recordings/{id}/access`. No new page route; the section lives on
`/auftritt/?id=…` and accepts `&aufnahme=<id>&t=<seconds>`.

Frontend: `components/auftritt-aufnahmen.tsx`, `lib/aufnahmen.ts`; the
ARC-018 player became `components/medien-spieler.tsx` (audio and video,
`sprung` prop for positioning) with `audio-spieler.tsx` as a thin wrapper.

## Verification record

Run on 2026-10-05 on the development machine (Linux arm64, Docker); frontend
scripts through `corepack pnpm`, the Aspire tests with
`~/.local/share/pnpm/bin` prepended to `PATH`.

| Command | Result |
| --- | --- |
| `dotnet build src/archive/Archive.slnx` | succeeded, 0 warnings, 0 errors |
| `dotnet test tests/archive/backend` | 520 passed, 0 failed, 0 skipped (506 before, 14 new) |
| `dotnet ef migrations has-pending-model-changes --project backend` | "No changes have been made to the model since the last migration." |
| `corepack pnpm run check` (in `src/archive/frontend`) | passed (Biome, route types, `tsc`) |
| `corepack pnpm run build` | passed, static export of all routes |
| `ARCHIVE_BASE_URL=http://localhost:3130 corepack pnpm exec playwright test tests/auftritt-aufnahmen.spec.ts tests/audio-wiedergabe.spec.ts tests/auftritte.spec.ts tests/auftritt-dokumente.spec.ts --workers=1` (static export served by `python3 -m http.server`) | 64 passed |
| same, `audio-wiedergabe`, `auftritte`, `auftritt-belege`, `auftritt-bestaetigung`, `auftritt-dokumente`, `extraktion`, `lied-historie`, `programm`, `noten`, `materialien`, `midi-wiedergabe`, `noten-verlauf` (one build earlier) | 220 run, 218 passed, 2 failed: `auftritte.spec.ts` "Mitglied öffnet einen Auftritt per Direktlink" on both projects expected the old placeholder text and had no mock for the new recordings list; mock added, the spec then passed 20/20 |
| `dotnet test tests/archive/apphost --filter "FullyQualifiedName~ConcertRecording"` | 1 passed (2 m 38 s) |
| `dotnet test tests/archive/apphost --filter "FullyQualifiedName~WalkingSkeleton"` | 5 passed, 0 failed (11 m 21 s; the four existing tests and the new one) |

What the checks cover:

- `tests/archive/backend/RecordingApiTests.cs` and `.Rules.cs` (14 tests,
  HTTP seam, in-memory store, `FakeAssetStorage`): create/upload/publish/play
  with complete write responses and exactly one view ticket; a QuickTime
  original preserved, a refused and then an accepted playback copy, a
  replaced copy; container judgement per kind and the empty file; downloads
  off by default, enabled, disabled again, and the generic asset ticket
  closed to members; download of an unplayable original; several recordings
  with unchanged song history, event row, documents and performance count;
  draft event and unpublished recording indistinguishable from unknown ids;
  renewal refused after membership ends; member 403, missing antiforgery 400,
  anonymous 401, downgraded editor 403, revoked editor 401; a second editor
  may publish but not touch files; recording assets cannot be created or
  retyped through the generic asset endpoints; validation, the two distinct
  409s; duration set and cleared.
- `src/archive/frontend/tests/auftritt-aufnahmen.spec.ts` (12 scenarios on
  desktop and mobile, API mocked in the browser, storage mock answering range
  requests): a member plays and seeks a real VP8 WebM in the video player and
  a WAV in the audio player, with downloads only where enabled; playback
  continues at the same position across a ticket renewal; a ticket that
  cannot be renewed is dropped before it expires; missing playback copy,
  undecodable file, empty list, load failure with retry, withdrawn
  recording; a link with a time opens at that position; an editor creates a
  recording, transfers the original in blocks and reports the duration;
  publishes and enables downloads; stale conflict versus not-allowed state;
  refused then accepted playback copy; another editor's recording.
- `ConcertRecordingRoundtripThroughRealAzuriteStorage`
  (`tests/archive/apphost/WalkingSkeletonTests.cs`): the migration applied to
  real PostgreSQL; QuickTime original stored and downloaded byte-exact with
  an attachment disposition; refused and accepted playback copy; a real
  `Range` request against the view ticket answered 206 with `video/webm`;
  renewal; download switch; generic ticket 404 for the member; stale version
  409; a second audio recording; event without documents or performances.

Notes and limits of this verification:

- Test-first: the first backend test was observed failing (404) before any
  endpoint existed. Four of the further thirteen failed on first run and
  exposed a real bug (the new playback asset was tracked as an existing row,
  so opening the slot answered 409). The other nine passed on first run
  because the behaviour was already implemented with the first slice; their
  sensitivity was checked afterwards by temporarily removing six guards
  (generic-ticket gate, patch guard, download switch, creator check, document
  filter, member publication filter), which made seven tests fail. The
  browser spec was written after the component.
- Playback in the browser tests uses mocked storage. The video is a real,
  6-second VP8 WebM; no MP4/H.264 was played (the test browser has no H.264
  decoder), and no real long recording was played. Hosted long playback and
  live SAS/CORS stay with ARC-044/ARC-051.
- No manual check in a real browser against the running Aspire stack.
- The full browser suite was not rerun; `shell.spec.ts` needs a backend.
- The migration ran against the empty database of the Aspire test only.

## Left out and known weaknesses

- No delete for a recording, also not for an empty draft; deletion and trash
  are ARC-039/ARC-040. The kind cannot be changed after creation.
- No history panel for recording files in the UI; the ARC-033 editor
  endpoints work on both slots.
- The container check cannot see codecs. An MP4 with, say, HEVC counts as
  playable until an editor adds a playback copy.
- The download switch withholds the download link; the streaming ticket is
  still a 15-minute read URL for the same object.
- Recording files count against the event's collection budget
  (`Archive:Assets:MaxCollectionBytes`, 40 GiB by default) together with its
  documents and all retained revisions; several multi-gigabyte originals of
  one event reach it and are refused with 413. The value is an operator
  decision for large-transfer validation (stated in the README).
- The upload ticket is not renewed during a transfer by the shared engine.
  The recordings section resumes automatically with a renewed ticket when a
  transfer breaks after progress; this path is only reasoned, not tested
  with a large file (large-transfer validation is a separate consumer).
- A stale-state 409 discards the small recording form (label and two
  switches) and reloads, by the existing convention; only "not allowed in
  this state" keeps the input.
- Races are reasoned, not provoked on PostgreSQL: finalize against a
  concurrent PATCH (both bump the recording's version; the loser answers
  409 and is retryable) and two simultaneous `/playback` calls (unique index
  on `PlaybackAssetId`, loser 409).
- H.264/MP4 playback, real long files and behaviour on phones are unverified
  until pilot devices are available.
- The 30-second promote deadline for objects near 10 GB is inherited from
  ARC-017 unchanged.

## Review follow-up (2026-10-05)

An independent review of `83e1436` found no data exposure and no regression
in the shared asset code, one blocking item and ten findings. Changes:

- **B1** With downloads off, the player sets `controlsList="nodownload"` on
  the media element and suppresses its context menu
  (`MedienSpieler` prop `herunterladenErlaubt`); picture-in-picture is left
  alone. Still not copy protection.
- **N1** A terminally refused transfer (422, 413, terminal 409) forgets its
  remembered upload session; a remembered session whose renewal answers
  409/404 gives way to one fresh session. A lost concurrency finalize keeps
  its session, and the engine's retry-once in `lib/assets.ts` is untouched
  (the only change there: a failed renewal now carries its HTTP status).
- **N2** Twenty seconds before expiry the list fetches a fresh ticket itself
  and retries every five seconds while the old one is valid. The player
  continues at the same position. Only when that fails (or the recording is
  gone) is the ticket dropped, five seconds before expiry, with the position
  remembered; reopening continues there. The timer now always reschedules.
- **N3** "außerhalb" in the copy; comments in the touched files checked.
- **N4** Position and play state are read when the address is swapped, not
  when the ticket is requested; a seek during the swap applies to the new
  address.
- **N5** A pending jump survives a failed first load. An unknown `aufnahme`
  id is ignored; an empty, unreadable or negative `t` opens at the start; a
  `t` beyond the end starts at the beginning.
- **N6** MP4 counts only with a known audio/video brand (HEIC/AVIF and
  unknown brands do not; `M4A `/`M4B ` are audio only); MP3 needs an ID3 tag
  or a valid Layer III frame header (a UTF-16 byte order mark does not
  pass); an empty recording file is refused from its size, without a read.
- **N7** Check constraint `CK_recordings_slots` (playback slot is never the
  original asset), added by amending the unapplied migration
  `20261005104333_ConcertRecordings` and the snapshot. The API ignores a
  playback link that is not a `recording-playback` asset of the recording's
  event, and finalize accepts a recording file only in the slot its type
  names.
- **N8** A PATCH without an effective change answers 200 without moving the
  version or `UpdatedAt`.
- **N9** `tests/aufnahmen-mock.ts` gives the other event specs an empty
  recordings list; the real load-failure state keeps its own test. With the
  list now loading there, the "Aufnahme hinzufügen" disclosure collided with
  a selector of the documents spec, so it got its own class
  (`aufnahme-anlegen`) in the shared disclosure styles.
- **N10** Backend test for restoring an earlier playback copy.

| Command | Result |
| --- | --- |
| `dotnet build src/archive/Archive.slnx` | succeeded, 0 errors |
| `dotnet test tests/archive/backend` | 526 passed, 0 failed, 0 skipped (520 + 6 new) |
| `dotnet ef migrations has-pending-model-changes --project backend` | "No changes have been made to the model since the last migration." |
| `corepack pnpm run check` | passed |
| `corepack pnpm run build` | passed |
| `ARCHIVE_BASE_URL=http://localhost:3130 corepack pnpm exec playwright test tests/auftritt-aufnahmen.spec.ts tests/audio-wiedergabe.spec.ts tests/auftritte.spec.ts tests/auftritt-dokumente.spec.ts tests/auftritt-belege.spec.ts tests/auftritt-bestaetigung.spec.ts tests/programm.spec.ts tests/noten-verlauf.spec.ts tests/extraktion.spec.ts tests/lied-historie.spec.ts --workers=1` | 214 passed |
| same without `extraktion` and `lied-historie`, one build earlier | 6 failed: three `auftritt-dokumente.spec.ts` tests on both projects, because `details.material-verwaltung` matched two elements once the recordings list loaded there; fixed by the class above |
| `dotnet test tests/archive/apphost --filter "FullyQualifiedName~ConcertRecording"` | 1 passed (2 m 44 s), amended migration applied to real PostgreSQL |

Seen failing before the fix:

- Backend (`RecordingApiTests.Review.cs`, 6 tests): five failed first —
  brand/byte-order-mark recognition, the HEIC/AVIF playback copy, the empty
  file without a read, the unchanged PATCH, the foreign playback link. The
  restore test (N10) passed on first run; it pins existing behaviour.
- Browser (`auftritt-aufnahmen.spec.ts`, now 18 scenarios): run against the
  previous build, seven failed — the `controlsList` assertion, the renewal
  blip, drop-and-reopen at the remembered position, both deep-link cases,
  and both upload-retry cases. "Paused video keeps its position" already
  passed on the previous build (the player's own renewal covers it); it
  stays as a regression test.

Limits of the follow-up verification:

- Ticket lifetimes in the browser tests are short real lifetimes (12–66
  seconds), not a mocked clock; the 15-minute case is the same code path
  with other numbers.
- The seek-during-renewal fix (N4) is covered by the position checks around
  a swap, not by a test that seeks at the exact moment of the swap.
- The new check constraint was applied to PostgreSQL by the Aspire test;
  no test tries to violate it there.
- The full WalkingSkeleton class and the full browser suite were not rerun.

## Handoff

- ARC-032: key passages on `recordings.Id`. `playback.revisionId` (list and
  access response) names the file a timestamp was taken against;
  `durationSeconds` may be null. `MedienSpieler` positions through
  `sprung={{ sekunden, marke }}`; the page already understands
  `?aufnahme=<id>&t=<seconds>`. A file change runs through
  `RecordingFiles.OnCurrentFileChangedAsync` — the place to mark passages
  for review. Recordings create no performances; linking passages to
  performances is ARC-032's own data.
- ARC-040: `recordings.EventId`, `OriginalAssetId` and `PlaybackAssetId` are
  `Restrict`. Member visibility is `RecordingVisibility.IsMemberVisible`
  (list filter, access endpoint), built on `EventVisibility`; the list
  endpoint answers the event's 404 first.
- ARC-041: `RecordingFiles.Resolve` says which revision members stream
  (`Playable`); an original that is not that revision is the Cold candidate.
  With downloads enabled and nothing playable, members download the
  original. Asset types `recording-original` / `recording-playback`
  distinguish the slots in `assets`.
