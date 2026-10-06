---
id: ARC-032
status: done
phase: core
kind: slice
depends_on: ["ARC-023", "ARC-030", "ARC-031"]
touches: ["recordings", "song-history", "search", "db-migrations"]
external_inputs: []
---

# ARC-032 — Jump from a song to its passage in a concert recording

**Depends on:** [ARC-023](ARC-023-repertoire-filters.md),
[ARC-030](ARC-030-concert-recording.md),
[ARC-031](ARC-031-song-history.md).

## Outcome

An editor captures a song's start/end while watching, and members find and play
that passage from the song's history or recorded-material search results.

## Acceptance criteria

- [x] Select an existing performance occurrence and capture/edit start/end
  timestamps using the player; validate ordering, duration and event ownership.
- [x] Link the segment to recording and performance IDs without copying the
  performance; a second recording can document that same occurrence.
- [x] Add authorized passage links to history, opening the player at the correct
  position with a clear segment end and working renewed file access.
- [x] Complete the recording-availability filter from ARC-023 through these
  explicit song/performance relationships, respecting visibility/deletion.
- [x] Whole-recording playback continues to work when only some songs are marked.

## AI assistance

No model call and no audio analysis.

- [ ] When the event has a confirmed or published programme, prefill the marker
  list with its songs in order, so the editor steps through known titles and
  only sets start and end times. *(Half done, left open: the deterministic
  prefill from a confirmed programme is built — the marker list is the event's
  occurrences in order; a published but unconfirmed programme has no
  occurrences to mark, so the list only points to the confirmation.)*

Decisions: [architecture §14](architecture.md#14-ai-assistance).

## Verification

Index two songs in one video and one of those performances in a second recording.
Verify precise jumps, invalid/cross-event bounds, recorded-material filtering,
partial indexing and unchanged performance totals after adding the second source.

## Handoff and parallel work

Own the integration between player, search and history rather than leaving it
for the launch gate. Coordinate their existing extension slots; import review
and score revision work can proceed separately.

## Progress

Implemented on `main` (2026-10-06). Contract and design decisions:

- A passage (`recording_passages`, `backend/Recordings/RecordingPassage.cs`)
  links one recording to one performance occurrence with `StartSeconds` /
  `EndSeconds` and the playback revision the times were taken against
  (`PlaybackRevisionId`). It never copies the performance: a second recording
  marks the same occurrence, and nothing in the slice creates, changes or
  counts a performance. At most one passage per (recording, performance).
- Integrity in the database: `StartSeconds >= 0`, `EndSeconds > StartSeconds`,
  unique (recording, performance), every foreign key `Restrict`, and event
  ownership as composite keys — the passage carries `EventId` and references
  `recordings (Id, EventId)` and `performances (Id, EventId)` (both tables got
  an alternate key), so a recording and an occurrence of different events can
  never be joined, whatever the API does. Migration
  `20261006010744_RecordingPassages` (additive; two unique constraints on
  existing tables, one new table).
- Endpoints: `GET /api/recordings/{id}/passages` (members and editors),
  `POST /api/recordings/{id}/passages`, `PATCH …/passages/{passageId}`,
  `POST …/passages/{passageId}/delete`, `POST …/passages/review` (editors,
  antiforgery). Ids are addressed through their parent: a passage under the
  wrong recording and an occurrence of another event are 404. Times are
  rounded to milliseconds and validated (negative, order, a measured duration
  with one second of tolerance, two days at most); a recording without a
  playable file takes no passages. Two 409s stay distinct: stale
  ("Die Zeitmarke wurde zwischenzeitlich geändert.") versus not allowed in
  this state (duplicate passage, no playable file); the UI reloads and
  discards the form only for the first. No-op updates do not bump the version.
  An identical lost retry of a create answers the stored passage.
- Visibility: members only get passages of a published recording of a
  published event and only for published songs; anything else is the
  recording's 404 or simply absent (also from the song history, the
  catalogue filter and its counts). Editors see everything, flagged
  (`isPublished`, `timestampState`).
- Changed playback file: `timestampState` is computed on every read
  (`current` iff the passage's revision is what `RecordingFiles.Resolve`
  returns as playable), not stored. No hook in `MakeCurrentAsync` was needed
  and restoring the earlier file makes the marks trustworthy again. Editors
  see values plus a "Zu prüfen" flag and a banner, and re-anchor with an
  edit (saving a pair always takes the current revision) or the bulk
  `…/review`; members see the song in the recording but receive no times, no
  jump, and the catalogue filter does not count the passage for them.
- Song history: each row gains `recordings` (see README). The history UI
  renders links through the ARC-031 `erweiterung` slot; no "keine
  Aufnahmen" claim anywhere. Counts are untouched.
- Catalogue filter: `material=recording` is now accepted (the ARC-023
  extension point). Alone it is a song-level condition; with other
  arrangement-level conditions the same arrangement (with a key filter the
  same version) must carry the passage, so siblings never combine. An
  occurrence with an unknown arrangement makes the song recorded but cannot
  satisfy an arrangement condition. Editors count every passage, members
  only member-visible, current ones. The frontend maps the URL value
  `material=aufnahme` and shows "Aufnahme mit markierter Stelle"; alone it
  is a song filter (no "Passende Fassung" hint).
- Performance deletion / skipping with passages (ARC-029 handoff): refused,
  not cascaded. `POST /api/performances/{id}/delete` and the programme
  confirmation PUT (skipped entry, removed encore) answer 409
  `Zeitmarken vorhanden: „Lied“ in „Aufnahme“ … Entferne zuerst diese
  Zeitmarken in den Aufnahmen.` (up to three named); a foreign-key violation
  on PostgreSQL after a concurrent marking maps to the same answer. The
  confirmation review carries `passageCount` per occurrence and the UI warns
  before; the UI keeps its input on that 409 (it is not a stale state).
- Player: `MedienSpieler` takes `abschnitt` ({von, bis, titel, marke}); a
  jump shows "Abschnitt: „Lied“ · 0:01 – 0:03", the playback stops at the end
  ("Ende des Abschnitts „Lied“ erreicht.", with repeat and "In der ganzen
  Aufnahme weiterspielen"), and a seek past the end is not held back.
  Tickets renew as before; a jump on a closed player fetches a fresh ticket.
- Deep link: `/auftritt/?id=…&aufnahme=<id>&stelle=<passageId>` (the history
  links; takes precedence over `&t=`). An unknown or in-review passage opens
  the recording at the start and says so.
- Editor UI ("Zeitmarken · <Aufnahme>"): the event's occurrences in order,
  per row Anfang/Ende text fields (m:ss, h:mm:ss, decimals), "Position
  übernehmen" from the open player, save (create or patch with the seen
  version), jump, remove with a second click; client-side validation with a
  persistent `role="alert"` region and focus on its message; after a save the
  focus moves to the next unmarked song. Overlaps are a hint, not an error.

Left open: the AI checkbox is half done — see the criterion above.

## Verification record

Run on 2026-10-06 on the development machine (Linux arm64, Docker); frontend
scripts through `corepack pnpm`.

| Command | Result |
| --- | --- |
| `dotnet build src/archive/Archive.slnx` | succeeded, 0 warnings, 0 errors |
| `dotnet ef migrations has-pending-model-changes --project backend` (placeholder `ConnectionStrings__archive-migrations`) | "No changes have been made to the model since the last migration." |
| `dotnet test tests/archive/backend` | 538 passed, 0 failed (526 before; +13 `RecordingPassageApiTests`, −2 and +1 cases in `FilterApiTests.InvalidMaterialFiltersAreRejected` because `recording` is now accepted) |
| `corepack pnpm run check` (in `src/archive/frontend`) | passed (Biome, route types, `tsc`) |
| `corepack pnpm run build` | passed, static export |
| `ARCHIVE_BASE_URL=http://localhost:3132 corepack pnpm exec playwright test tests/auftritt-passagen.spec.ts --workers=1` (static export via `python3 -m http.server`) | 24 passed (12 scenarios, desktop + mobile) |
| same, `lied-historie`, `filter`, `auftritt-bestaetigung`, `auftritt-belege`, `lieder` | 120 passed |
| same, `auftritt-aufnahmen`, `audio-wiedergabe`, `auftritte`, `auftritt-dokumente`, `programm`, `materialien` | 112 passed |
| `dotnet test tests/archive/apphost --filter "FullyQualifiedName~WalkingSkeleton"` | 5 passed, 0 failed (12 m 8 s; real PostgreSQL + Azurite, the four existing tests, the extended concert-recording test and the new `_RecordingPassages` migration applied by `archive-migrate`; `pnpm` shim on PATH) |

What the checks cover:

- `tests/archive/backend/RecordingPassageApiTests.cs` (+ `.Scenario.cs`, 13
  tests at the HTTP seam, in-memory store, fake storage): two songs in one
  video and one of them in a second recording with complete write responses
  and unchanged performance rows and history counts; whole-recording playback
  with only some songs marked; the editor's marker list (and a published but
  unconfirmed programme); bounds, event ownership, wrong parent ids,
  duplicate and lost-retry, no-file; edit/stale/no-op/delete; authorization
  (anonymous, member 403, missing antiforgery 400, downgraded 403, revoked
  401); unpublished recording/event/song never reaching members (list,
  history, withdrawing and republishing); changed playback file flagging and
  re-anchoring by edit and by bulk review; delete/skip refused with the named
  passages and proceeding after removal, plus the review's `passageCount`;
  the catalogue filter (member vs editor visibility, withdrawal, combination
  with other material and search, same-arrangement semantics, unknown chain).
- `src/archive/frontend/tests/auftritt-passagen.spec.ts`: jump, segment end
  and "play on", in-review marks without a jump, deep link by passage (also
  unknown and in-review), no list when nothing is marked, load failure with
  retry, the editor stepping through songs with player positions, client and
  server validation messages, the two 409 kinds, change/remove with the seen
  version, bulk review, and the unconfirmed-programme hint. Further cases in
  `lied-historie.spec.ts` (links, in-review, editor draft), `filter.spec.ts`
  (mapping, URL restoration, no fassung hint) and `auftritt-bestaetigung.spec.ts`
  (warning and the 409 that keeps input).
- `ConcertRecordingRoundtripThroughRealAzuriteStorage`
  (`tests/archive/apphost/WalkingSkeletonTests.cs`) was extended: on real
  PostgreSQL it marks a passage in two recordings, refuses a cross-event
  occurrence and an end beyond the duration, lists history and catalogue
  filter for a member, is refused deleting the occurrence, and sees the
  passage flagged after a new playback file. The pending-migration assertion
  lists `_RecordingPassages`.

Notes and limits of this verification:

- Test-first: the 13 backend tests were observed failing (404) before any
  endpoint existed and passed on the first run after the implementation; their
  sensitivity was checked by temporarily removing the event-ownership check
  and the member visibility filter, which failed three of them. The
  `auftritt-passagen` spec was run against the previous static export first:
  11 of 12 desktop scenarios failed (the one that passed asserts absence). The
  additions to `lied-historie`, `filter` and `auftritt-bestaetigung` were
  written after the UI was built, without a separate red run.
- The browser tests mock the API; playback uses a real 6-second VP8 WebM with
  range requests answered like the blob service. No manual check against the
  running Aspire stack in a real browser; H.264 playback is unverified.
- Segment end is detected on `timeupdate` (about 250 ms granularity), so
  playback may run a fraction of a second past the end before it is set back
  to the end.
- The new queries (passage projections with navigations, `Contains` on id
  lists, a grouped count) were exercised on real PostgreSQL only through the
  apphost flow above.

## Known weaknesses and handoff

- Which passage is "current" depends on `RecordingFiles.Resolve`; a recording
  with no playable file reads every passage as in need of review, and members
  see no times.
- The history query and the filter load the passages of the visible scope per
  request (plain projections, then the playable revision of each distinct
  recording); fine for hundreds of recordings, not cached.
- Overlaps between passages of one recording are a UI hint only; the API
  accepts them. A performance split over several passages in one recording
  is not possible (unique per recording and performance), as scoped out by
  the PRD.
- Members see no per-passage editor data; attribution is stored
  (`CreatedBy…`/`UpdatedBy…`) but not shown.
- ARC-022-1 (chat history tools): reuse `RecordingPassages.LinksByPerformanceAsync`
  / `stellenPfad`; never state a position for `needsReview`.
- ARC-035 (search): `RecordingPassages.RecordedChainsAsync` is the visible
  recorded song/arrangement/version set.
- ARC-040 (event trash): passages and recordings are `Restrict`; trash must
  delete or keep passages explicitly and extend `RecordingVisibility.OfMemberVisible`
  and `EventVisibility`.
- ARC-046 (merge songs): a passage's song is its performance's song, so
  moving performances moves the passages; the unique key is per
  (recording, performance) and cannot collide when songs merge.

## Review follow-up (2026-10-06)

An independent review of `d88c3d9` found one blocking defect and eight
findings; all are fixed in the follow-up commit. No migration (the pushed
`20261006010744_RecordingPassages` is untouched).

- **B1 (blocking)** Writes carry the file the editor looked at.
  `expectedPlaybackRevisionId` (the `playbackRevisionId` of the editor GET) is
  required on `POST`, `PATCH` and `…/review`; missing is a 400, a different
  resolved playable file is the stale-state 409 "Die Datei der Aufnahme wurde
  zwischenzeitlich ersetzt." and nothing is written. The review now takes
  `passages: [{ id, expectedVersion }]` and acts only on those (empty/absent
  is a 400, a foreign id a 404, a stale row token refuses the whole review).
  A PATCH of a passage in need of review must state both bounds (400
  otherwise), so an unseen bound is never re-anchored silently. Client: the
  revision and the listed ids/tokens are sent; on the file-replaced 409 the
  page reloads recording list, closes the player (ticket, position, segment)
  and reloads the marks, keeps the typed values and explains.
- **N1** The confirmation PUT asks for passages only after the token and
  `knownOccurrences` stale checks, so an outdated form is told to reload.
- **N2** A jump (and a deep link, and a ticket renewal) compares the access
  response's `revisionId` with the list's `playback.revisionId`; on a
  difference the cached segment/position is dropped, list and marks reload,
  and the page says so instead of positioning.
- **N3** "hängt 1 Zeitmarke … die Zeitmarke … ist" / "hängen n …" and the same
  warning on an encore row and for an encore removed in the form.
- **N4** An unknown `stelle` now opens the player at the start like its
  sibling branches (the omission was real).
- **N5** Time fields: visible hint ("m:ss oder h:mm:ss") wired with
  `aria-describedby`, `aria-invalid` and the message id on the offending field,
  focus moves to that field (server errors still focus the alert).
- **N6** After the segment end the main play button clears the "Ende des
  Abschnitts …" state and its buttons; seeking outside the segment releases
  the stop.
- **N7** The FK/unique mappings check `PostgresException.ConstraintName`
  (`RecordingPassages.IsPerformanceForeignKeyViolation`,
  `IsDuplicateViolation`, `IsParentGoneViolation`): the passages message only
  for the passage→performance key, the duplicate message for the unique
  (recording, performance) index; anything else follows the previous generic
  handling. `IsConflict` is gone.
- **N8** A mark whose start lies beyond the real file duration (unknown
  duration at marking time, shorter file) is not shown as a segment: the player
  says "Die Zeitmarke … liegt außerhalb der Datei (Dauer …). Die Aufnahme
  beginnt am Anfang." The server still cannot check a null duration.
- **N9** Tests for anonymous and antiforgery on PATCH/delete/review, restoring
  an earlier file, losing the playable file, and ticket renewal during a
  segment.

### Follow-up verification

| Command | Result |
| --- | --- |
| `dotnet build src/archive/Archive.slnx` | succeeded, 0 errors |
| `dotnet ef migrations has-pending-model-changes --project backend` | "No changes have been made to the model since the last migration." |
| `dotnet test tests/archive/backend` | 545 passed, 0 failed (538 + 7 new) |
| `corepack pnpm run check`, `corepack pnpm run build` | passed |
| `corepack pnpm exec playwright test tests/auftritt-passagen.spec.ts --workers=1` (static export) | 36 passed (18 scenarios, desktop + mobile; 6 new) |
| same, `auftritt-aufnahmen`, `audio-wiedergabe`, `auftritt-bestaetigung`, `auftritt-belege`, `lied-historie`, `filter` | 134 passed, then `auftritt-aufnahmen` 36 passed again after a mock fix |
| `dotnet test tests/archive/apphost --filter "FullyQualifiedName~ConcertRecording"` | 1 passed (2 m 57 s), flow updated for the required revision, a stale-file refusal and a bulk review |

Seen failing first: backend — `OutdatedPlaybackRevisionIsRefused…`,
`EditorWithTheCurrentRevision…`, `StaleConfirmationForm…`,
`ChangedPlaybackFile…` (both-bounds rule) and the constraint-mapping test
(against a stub that throws) failed; the antiforgery, restore and
lost-playable tests passed on first run because they pin existing behaviour.
Browser — the updated `auftritt-passagen.spec.ts` was run against the
previous static export first: 10 of 18 desktop scenarios failed (the eight
that passed are unchanged behaviour). `auftritt-bestaetigung.spec.ts` additions
were written after the UI change without a separate red run.

One existing browser mock was wrong and is fixed: `auftritt-aufnahmen.spec.ts`
issued tickets whose `revisionId` differed from the list's for the audio
recording; the page now compares them, so the helper pairs them as the real API
does.

## Further known weaknesses (recorded, deliberately left)

- Members can see that a passage is under review (the song is listed, "Zeitmarke
  wird überprüft") but never its times — a decision of this ticket, not a leak.
- `IX_recording_passages_PerformanceId` and `IX_recording_passages_RecordingId_EventId`
  are redundant next to other indexes; removing them needs a migration, not
  worth it now.
- The alternate keys make `Recording.EventId` and `Performance.EventId`
  immutable in EF; a future "move to another event" needs a migration of the
  keys.
- The CHECK constraints accept NaN/Infinity at database level; the API rejects
  them.
- Unverified until pilot devices: precise jumps (the segment end rides on
  `timeupdate`), fullscreen video with native controls, background-tab
  throttling and H.264.
