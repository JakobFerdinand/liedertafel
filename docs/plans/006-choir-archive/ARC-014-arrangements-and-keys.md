---
id: ARC-014
status: in_progress
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

- [ ] Editors add/edit arrangements and musical versions under the existing IDs,
  with labels, arranger information, voice configuration and optional key.
- [ ] The member page groups arrangements and versions distinctly and supports
  direct links to the intended selection.
- [ ] Validate parent relationships and avoid silently moving a referenced
  version or treating a corrected PDF as another musical arrangement.
- [ ] Preserve explicit unknown metadata and contributor attribution; distinguish
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
  ARC-024 programmes and ARC-034 import review), driven purely by the song
  detail payload and stable IDs.
- Editor controls to add arrangements and versions and to edit labels,
  arranger, voice configuration and optional key inline, reusing
  `LiedFormular`-style patterns and `postAuth`/`patchAuth`.

Attribution and concurrency follow ARC-013: writes record account id +
`TimeProvider` time; song-level edits bump `RowVersion` (children have no
concurrency token in this slice; unknown-id and German 400 validation mirror
existing behaviour).
