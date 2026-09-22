---
id: ARC-014
status: done
phase: core
kind: slice
depends_on: ["ARC-013"]
touches: ["catalogue", "db-migrations"]
external_inputs: []
---

# ARC-014 — Choose between arrangements and transposed versions

**Depends on:** [ARC-013](ARC-013-first-published-song.md).

## Outcome

A member opens one song and clearly chooses between two arrangements and two
keys of an arrangement instead of encountering duplicate-looking song folders.

## Acceptance criteria

- [x] Editors add/edit arrangements and musical versions under the existing IDs,
  with labels, arranger information, voice configuration and optional key.
- [x] The member page groups arrangements and versions distinctly and supports
  direct links to the intended selection.
- [x] Validate parent relationships and avoid silently moving a referenced
  version or treating a corrected PDF as another musical arrangement.
- [x] Preserve explicit unknown metadata and contributor attribution; distinguish
  identical titles/keys using identity rather than a filename convention.

## Verification

Build the two-arrangement/two-key example, reload and follow direct links. Test
attempts to attach a version to the wrong parent and role/visibility boundaries.
File association is verified by ARC-015 using these same stable IDs.

## Handoff and parallel work

Expose a reusable arrangement/version picker for programmes and import review.
Coordinate catalogue detail/picker changes with ARC-015/020/021, while keeping
file transfer work independent of this UI extension.

## Frozen contract (2026-09-19)

Builds on the ARC-013 frozen contract (Guid v7 IDs, snake_case tables, German
ProblemDetails, `no-store`, CSRF on mutations, `CatalogueVisibility` as the
single visibility decision point, Editor/Administrator writes via
`ArchiveAccessService` + role check).

Schema additions (new explicit migration, ARC-013 migration untouched):

- `Arrangement` gains optional `VoiceConfiguration` (≤ 200, null = unknown);
  null stays null — explicit unknown metadata is preserved, never invented.
- `MusicalVersion` gains optional `MusicalKey` (≤ 200, null = unknown).
- No filename-based identity: identical labels/keys stay distinct entities
  distinguished by their stable IDs.

API additions (same conventions as ARC-013):

- `POST /api/songs/{id}/arrangements` (Editor) `{ label, arranger?,
  voiceConfiguration? }` → 201 `{ song }` detail.
- `PATCH /api/arrangements/{id}` (Editor) `{ label?, arranger?,
  voiceConfiguration? }` → 200 `{ song }` detail.
- `POST /api/arrangements/{id}/versions` (Editor) `{ label, creator?,
  musicalKey? }` → 201 `{ song }` detail.
- `PATCH /api/musical-versions/{id}` (Editor) `{ label?, creator?,
  musicalKey? }` → 200 `{ song }` detail.
- Parent validation: an arrangement/version id is always addressed directly;
  a version can never be moved to another arrangement (no parent field exists
  on write requests — a wrong-parent attach is structurally impossible and
  unknown ids answer 404, not a silent move). Detail responses always include
  the full parent chain so clients cannot mistake a corrected PDF (ARC-015
  file revision) for a new arrangement/version.
- `SongDetail` projections extend with `voiceConfiguration` and
  `musicalKey` (camelCase, null preserved).

Frontend:

- `LiedDetail` groups arrangements distinctly from their musical versions and
  supports direct links to the intended selection:
  `/lied/?id=…&fassung={arrangementId}&version={versionId}`.
- Reusable `FassungsWahl` arrangement/version picker component (exported for
  ARC-026 programmes and ARC-036 import review), driven purely by the song
  detail payload and stable IDs.
- Editor controls to add arrangements and versions and to edit labels,
  arranger, voice configuration and optional key inline, reusing
  `LiedFormular`-style patterns and `postAuth`/`patchAuth`.

Attribution and concurrency follow ARC-013: writes record account id +
`TimeProvider` time; song-level edits bump `RowVersion` (children have no
concurrency token in this slice; unknown-id and German 400 validation mirror
existing behaviour).

## Implementation progress — 2026-09-19 (branch `feat/arc-014-arrangements-and-keys`)

Backend (`0accf05`, `45e3505`, `2ff218d`):

- `Arrangement.VoiceConfiguration` and `MusicalVersion.MusicalKey` optional
  (≤ 200) fields; explicit migration
  `20260919132544_ArrangementVoiceConfigurationAndVersionKeys`; ARC-013's
  migration untouched; `WalkingSkeletonTests` asserts the new migration in
  the pending list.
- `CatalogueEndpoints.cs`: `POST /api/songs/{id}/arrangements`,
  `PATCH /api/arrangements/{id}`, `POST /api/arrangements/{id}/versions`,
  `PATCH /api/musical-versions/{id}` — all Editor-only, antiforgery-first,
  German validation (`Das Label ist erforderlich.` etc.), song-level
  `RowVersion` bump, 404s for unknown ids, `SongDetail` extended with
  `voiceConfiguration`/`musicalKey` (nulls preserved). No write request
  carries a parent field, so moving a version to another arrangement is
  structurally impossible; detail responses always include the parent chain.
  Fix `2ff218d`: children are attached via explicit `DbSet.Add` with the FK
  set — navigation-add of pre-keyed Guid v7 children was misdetected as
  Modified and always failed with 409.
- `tests/archive/backend/CatalogueApiTests.cs` (+11 tests, 148 total):
  two-arrangement/two-key creation with identical labels staying distinct
  stable IDs, child PATCH persistence/attribution and kept parents, parent
  404s, member 403 / anonymous 401, German 400s, draft-song child edits with
  optionals staying null.

Frontend (`27c3108`, `49dce6c`, `a001341`):

- New reusable `FassungsWahl` picker (arrangements as grouped sections,
  versions as `aria-pressed` buttons driven purely by stable IDs, exported
  for ARC-026 programmes and ARC-036 import review); `LiedDetail` groups
  arrangements and versions distinctly, supports direct links
  `/lied/?id=…&fassung=…&version=…` (invalid params fall back to the first
  selection, URL kept in sync via `router.replace`).
- Editor controls to add arrangements/versions and edit arrangement and
  version fields (label/arranger/voiceConfiguration, label/creator/musicalKey)
  via shared `FassungsFormular`; `lib/songs.ts` types extended; `lied-*`/
  `fassungs-*` CSS with existing variables only. Null metadata renders
  nothing — no invented "unbekannt" placeholders.

## Verification — 2026-09-19 (branch `feat/arc-014-arrangements-and-keys`)

- `dotnet build src/archive/Archive.slnx` 0 errors; `dotnet test
  tests/archive/backend` **148/148 green** (137 prior + 11 new).
- `dotnet test tests/archive/apphost` (fresh containers) **green**: walking
  skeleton holds and the new
  `ArrangementVoiceConfigurationAndVersionKeys` migration is asserted in
  the pending list against real PostgreSQL.
- Frontend `pnpm run check` clean; `pnpm run build` statically exports;
  mocked Playwright suite 46 passed / 6 failed / 6 skipped — all lieder
  tests (11 × desktop/mobile = 22) green including the two-arrangement/
  two-key selection, direct links with valid and invalid params, editor
  add/edit controls and version editing; the 6 failures are the
  pre-existing `tests/shell.spec.ts` cases that need the real backend
  (reproduce identically on the clean tree, `/api/*` 404s on a static
  export server). Real-backend browser flows were not run; the apphost
  integration test covers the real API/database path.
