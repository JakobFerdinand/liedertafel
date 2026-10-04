---
id: ARC-033
status: done
phase: core
kind: slice
depends_on: ["ARC-015"]
touches: ["assets", "catalogue-materials", "db-migrations"]
external_inputs: []
---

# ARC-033 — Correct a score while retaining its previous revision

**Depends on:** [ARC-015](ARC-015-private-score.md).

## Outcome

An editor replaces a score, members receive the current PDF, and an editor can
inspect or make an earlier retained revision current again.

## Acceptance criteria

- [x] Upload a new immutable file revision under the same logical asset and
  atomically update its current pointer after successful validation.
- [x] Preserve revision metadata/attribution and editor-only history; retries and
  competing replacements cannot overwrite another file or lose a revision.
- [x] Members and historical musical-version links resolve current materials;
  a previous revision is not a new arrangement or transposition.
- [x] Selecting an earlier revision as current preserves history and triggers
  the same revision-change contract used by search/extraction consumers.
- [x] Retain score revisions until explicitly removed, independently of the
  seven-day trash policy; enforce role checks on old-revision file tickets.

## AI assistance

Post-launch increment, not required for the launch gate:

- [ ] When a score is replaced, rerun extraction (ARC-034, ARC-034-1) on the new
  revision and show the editor which derived fields changed (key, voice,
  lyrics). Fields a human has locked are never overwritten.

No page-by-page visual comparison. Decisions: [architecture §14](architecture.md#14-ai-assistance).

## Verification

Replace a PDF, follow an existing member link, inspect history as editor, restore
the prior current pointer and test concurrent replacements/unauthorized revision
reads. Recheck identity and attribution after reload.

## Handoff and parallel work

ARC-035 integrates current-revision text visibility. Coordinate upload/revision
contracts with ARC-017/032; player implementation need not wait for this history UI.

## Progress

Implemented on `main` (2026-10-04). The post-launch AI increment above is
**not** part of this slice and stays open; see "Left out".

Design decisions:

- A correction reuses the ARC-015 upload protocol on the existing asset id:
  session, direct transfer, validated finalize. Finalize adds revision
  `max + 1` with its own blob name and swaps the pointer in the same save.
- **Who may correct or restore (decided, wider than the ticket text):** the
  ticket only says "score", but the rule applies to every material type on a
  musical version. Any editor may correct or restore a version-owned asset
  that already has a file — scores, voice files, audio and MIDI. An asset
  without a file, and every event-owned asset, stay with the creating editor
  (ARC-025 rule unchanged). One helper, `MayChangeCurrentFile` in
  `AssetEndpoints.cs`, decides this for both the upload-session and the
  restore path. Sessions still belong to their creator.
- **Revision-change contract:** `RevisionChanges.MakeCurrentAsync`
  (`src/archive/backend/Assets/RevisionChange.cs`) is the only code that
  moves `ArchiveAsset.CurrentRevisionId`. Upload and restore both call it:
  pointer swap, `RowVersion` bump, an append-only `asset_revision_changes`
  entry and, for a PDF revision without one, the ARC-034 extraction row
  commit together. Extraction stays keyed by revision, so a restored revision
  reuses its stored text and nothing is extracted twice.
- Restore creates no revision. A missing `revisionId` is 400;
  `expectedCurrentRevisionId` rejects a stale history with 409; repeating
  the request for the already-current revision is a 200 without a new
  history entry.
- History attribution shows the account's display name and falls back to its
  email address when none is set. This stays: the history is editor-only.
- A finalize that loses a race (asset token moved, or the revision number is
  already taken) answers 409, drops only its own copied object and leaves its
  session pending, so a retry adds the next revision. Other save failures are
  not treated as a conflict. The browser repeats finalize once for exactly
  this 409 (no second transfer) and reloads the history panel whenever it
  shows a 409.
- Migration `20261004104336_ScoreRevisionHistory`: nullable
  `file_revisions.OriginalFileName` (backfilled from the finalizing upload
  session's declared name), table `asset_revision_changes`, and a backfill
  of one upload entry per existing revision.
- The UI calls a file revision "Dateistand" everywhere (the material line
  used to say "Fassung N", which collided with the musical "Fassung").

Endpoints (editor-only, `no-store`; members 403, anonymous 401):

- `GET /api/assets/{id}/revisions`
- `GET /api/assets/{id}/revisions/{revisionId}/access`
- `POST /api/assets/{id}/current-revision`

No new page route: the history is the "Dateistände" panel on a score entry of
`/lied/` (`src/archive/frontend/components/noten-verlauf.tsx`).

## Verification record

Run on 2026-10-04 on the development machine (Linux arm64, Docker). `pnpm`
is not on `PATH` there; frontend scripts ran through `corepack pnpm`, and the
Aspire test with `~/.local/share/pnpm/bin` prepended to `PATH`.

| Command | Result |
| --- | --- |
| `dotnet build src/archive/Archive.slnx` | succeeded, 0 warnings, 0 errors |
| `dotnet test tests/archive/backend` | 477 passed, 0 failed, 0 skipped |
| `corepack pnpm run check` (in `src/archive/frontend`) | passed (Biome, route types, `tsc`) |
| `corepack pnpm run build` | passed, static export of all routes |
| `ARCHIVE_BASE_URL=http://localhost:3100 corepack pnpm exec playwright test --workers=1` | 320 run, 314 passed, 6 failed — see note |
| same, limited to `noten-verlauf`, `noten`, `materialien`, `audio-wiedergabe`, `midi-wiedergabe`, `extraktion` (after the last frontend edit) | 98 passed |
| `dotnet test tests/archive/apphost --filter "FullyQualifiedName~PrivateScoreUploadRoundtripThroughRealAzuriteStorage"` | 1 passed (2 m 39 s) |
| `--migrate` against a throwaway `postgres:17.6` container | exit 0, `20261004104336_ScoreRevisionHistory` applied |

What the checks cover:

- `tests/archive/backend/AssetApiTests.Revisions.cs` (9 tests, HTTP seam with
  the in-memory store and `FakeAssetStorage`): replacement keeps asset,
  arrangement and version and serves the new file to a member through the
  unchanged link; history with file name and uploader for two editors;
  old-revision tickets for editors only (member 403, anonymous 401, revision
  of another asset 404, no ticket issued on refusal); restore keeps both
  revisions, logs a `restore` entry with attribution, switches member access
  and song detail, is idempotent and reusable in both directions; stale
  `expectedCurrentRevisionId`, foreign revision, member and missing CSRF are
  refused; a revision without an extraction row gains a queued one on
  restore; two editors' competing corrections keep three distinct files and a
  late finalize retry does not take the pointer back; a finalize that loses
  the save race answers 409, keeps the staged upload, removes only its own
  copy and succeeds on retry; upload cleanup never removes revisions.
- `src/archive/frontend/tests/noten-verlauf.spec.ts` (6 scenarios on desktop
  and mobile, API mocked in the browser): correct a score from the history
  panel, view an earlier revision, make it current, stale-history conflict,
  load failure with retry, missing file name/uploader stated explicitly,
  members see no history and trigger no history request.
- The Aspire test runs the same journey against real PostgreSQL and Azurite:
  correction, byte-exact read of the current and the earlier revision,
  member refusal, restore, and two finalizes fired concurrently by two
  accounts ending in four distinct, readable revisions.

Notes and limits of this verification:

- The backend tests were written after the endpoints, not strictly test
  first; none of them was observed failing before the implementation.
- The 6 browser failures are the three `shell.spec.ts` tests on both
  projects. They call the real `/api/build` through the dev proxy, and this
  run used a dev server without a backend (`ARCHIVE_API_URL` pointed at a
  closed port), so the proxy answered 500. They were not rerun against the
  Aspire stack and were not compared with a run on the previous commit.
- In the concurrent Aspire run both finalizes committed without a conflict,
  so the lost-race branch was exercised only by the backend test that
  simulates the failed save, not against PostgreSQL.
- In the first pass the migration ran against empty tables only; the
  backfills were checked against seeded rows in the review follow-up below.
- The other Aspire tests (`tests/archive/apphost`) were not run.
- No manual check in a real browser against a running stack; live SAS/CORS
  behaviour stays with ARC-044/ARC-051.

### Review follow-up (2026-10-04)

An independent review of the first commit led to these changes: restore on
an event-owned asset is creator-only (shared `MayChangeCurrentFile` rule),
a missing `revisionId` is 400, the browser repeats a lost finalize once and
reloads the history on any 409, history-panel tickets are dropped before
they expire, and the migration also backfills `OriginalFileName`.

| Command | Result |
| --- | --- |
| `dotnet build src/archive/Archive.slnx` | succeeded, 0 warnings, 0 errors |
| `dotnet test tests/archive/backend` | 480 passed, 0 failed, 0 skipped |
| `corepack pnpm run check` | passed |
| `corepack pnpm run build` | passed |
| `ARCHIVE_BASE_URL=http://localhost:3100 corepack pnpm exec playwright test tests/noten-verlauf.spec.ts tests/noten.spec.ts tests/materialien.spec.ts tests/auftritt-dokumente.spec.ts tests/audio-wiedergabe.spec.ts tests/midi-wiedergabe.spec.ts tests/extraktion.spec.ts --workers=1` | 116 passed |
| `dotnet ef database update 20261002103803_ExtractionJobs`, seed three revisions and four upload sessions by SQL, then `dotnet ef database update` (throwaway `postgres:17.6`) | both backfills correct: file names copied from the finalizing sessions (null where none was declared, the unfinalized session ignored), one upload entry per revision with the right predecessor |
| `dotnet ef migrations has-pending-model-changes --project backend` | "No changes have been made to the model since the last migration." |

New backend tests (`AssetApiTests.Revisions.cs`, now 12): a second editor is
refused both a correction and a restore on an event document that has a file
(observed failing with the version-owned condition removed from the rule);
a restore that loses the save race answers 409, changes nothing and succeeds
on retry; an editor downgraded to member gets 403 and a revoked one 401 on
all three endpoints; a restore without `revisionId` is 400. New browser
scenarios (`noten-verlauf.spec.ts`, now 9): a lost finalize is repeated
without a second transfer; a twice-lost finalize shows the conflict and
reloads the history; an expiring ticket disappears and "Ansehen" fetches a
fresh one.

Not rerun for the follow-up: the Aspire test and the full browser suite. The
seeded migration check bypassed foreign keys (`session_replication_role =
replica`) instead of building the full song chain.

## Left out and known limits

- **AI assistance (post-launch, open):** a replacement already queues
  extraction for the new revision through the ARC-034 outbox, but the editor
  is not shown which derived fields (key, voice, lyrics) changed, and there is
  no notion of human-locked fields yet. That needs ARC-013-1 provenance and
  ARC-034-1.
- **No removal action.** Revisions are retained and nothing expires them, but
  an editor cannot yet remove one deliberately. ARC-039 owns removal together
  with the retained-reference check. Every retained revision counts towards
  the per-version storage budget (`MaxCollectionBytes`, 40 GB).
- An interrupted correction upload is not offered for resume after a page
  reload (the ARC-017 resume list only covers assets without a file); the
  editor starts the correction again.
- Correction and restore work for every version-owned material type at the
  API (see the decision above); the UI offers the history panel for scores
  only. History and old-revision tickets of event-owned assets are readable
  by every editor; only changing them is creator-only.
- History-panel tickets are dropped one minute before they expire, so the
  editor asks for a fresh one; they are not renewed silently.
- The pointer-change log orders entries of the same instant by id, which is
  not a strict order within one millisecond.

## Handoff

- **ARC-035 (search):** join extraction text through
  `assets.CurrentRevisionId` → `ExtractionJobs.RevisionId`. Replacement and
  restore move the pointer in one save, so eligibility changes immediately;
  a current revision whose row is still `Queued`/`Running` is the "pending"
  case. Text of non-current revisions stays in `ExtractionJobs` and must
  never be searched for members.
- **ARC-039 (trash):** `asset_revision_changes` cascades from both the asset
  and the revision; `PreviousRevisionId` is a plain historic reference
  without a foreign key, so removing a revision never blocks on it (the
  history then reports the previous number as unknown). Removing a revision
  must first clear `upload_sessions.FinalizedRevisionId` (Restrict) and must
  never target the current revision (Restrict). Hide trashed assets from the
  three new endpoints as well.
- **ARC-034-1 (scan reading):** anything that writes revision text should
  keep writing into the row of the revision it read, never by asset;
  `MakeCurrentAsync` only creates a missing row and never resets an existing
  one, so a restored revision keeps whatever text it has.
- **Everyone adding material types or owners:** the correction/restore rule
  is `MayChangeCurrentFile`; any editor may replace the file of any
  version-owned asset that has one, whatever its type. Attribution in the
  history may show an email address where no display name exists.
- **ARC-017/032:** upload-session and finalize wire shapes are unchanged;
  `FileRevision.OriginalFileName` is filled from the declared file name.
