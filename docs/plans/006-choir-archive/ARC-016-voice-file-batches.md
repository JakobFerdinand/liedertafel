---
id: ARC-016
status: done
phase: core
kind: slice
depends_on: ["ARC-015"]
touches: ["assets", "catalogue-materials"]
external_inputs: []
---

# ARC-016 — Add a labelled batch of practice files

**Depends on:** [ARC-015](ARC-015-private-score.md).

## Outcome

An editor drops several score/audio/MIDI files onto an arrangement version and
members can identify the full mix or their voice-part file without guessing filenames.

## Acceptance criteria

- [x] Add multi-file selection/drop with per-file status, bounded concurrency,
  retry and partial-success handling through the existing upload protocol.
- [x] Edit type, description and voice/full-mix labels before publication;
  support different choir configurations rather than a fixed SATB-only list.
- [x] Group published materials by type and voice, with their version association
  always visible. Scores, practice audio and MIDI offer authorized downloads.
- [x] One failed file does not duplicate or roll back successful files; members
  cannot access unreviewed pending items.

## Verification

Upload a mixed batch with one intentionally rejected file, retry it, and verify
labels/ownership after reload. Check the member view on a phone and exercise
unauthorized type/owner changes through the API.

## Handoff and parallel work

ARC-018/019 consume the labelled material list and ticket endpoint. ARC-017 may
improve the transfer engine concurrently; agree on stable upload-session/status
contracts rather than sharing ad hoc component state.

## Progress

Branch: `feat/arc-016-voice-file-batches`.

TDD seams agreed with the user:

- Backend HTTP API (`tests/archive/backend/AssetApiTests.cs`, EF InMemory +
  `FakeAssetStorage`): audio/midi asset types with per-type content-type
  whitelists and finalize validation; `Description` field + `PATCH
  /api/assets/{id}` metadata edits with editor/owner checks; per-file
  independent sessions guarantee partial success; song detail embeds labelled
  materials.
- Frontend Playwright (`src/archive/frontend/tests`, route mocks): batch
  upload with per-file status, bounded concurrency, retry of a rejected file,
  grouped material list by type and voice, unauthorized metadata edits.
- AppHost integration test: mixed batch (PDF + audio + MIDI) through real
  Azurite with labels/ownership after reload.

Design decisions:

- Asset types: `score` (existing), `audio`, `midi`. Type-based content-type
  whitelist: audio accepts `audio/mpeg`, `audio/mp4`, `audio/x-m4a`, `audio/wav`,
  `audio/ogg`; midi accepts `audio/midi` and `audio/x-midi`. Size cap reused
  from `Archive:Assets:MaxUploadBytes`. Magic-byte check stays PDF-only.
- Voice labels: backend keeps free text (`VoiceLabel`); the UI offers
  suggestions parsed from the arrangement's `VoiceConfiguration` plus a
  "Vollmix" option — no fixed SATB list.

- [x] Slice 1: backend audio/midi types + per-type validation
- [x] Slice 2: description + metadata edit endpoint + song detail embedding
- [x] Slice 3: partial-success guarantee test
- [x] Slice 4: frontend batch upload + grouped material list
- [x] Slice 5: apphost mixed-batch integration test
- [x] Final verification + documentation

## Implementation and verification

Backend (`backend/Assets/AssetEndpoints.cs`):

- Type whitelist map `score → application/pdf`, `audio → audio/mpeg`,
  `audio/mp4`, `audio/x-m4a`, `audio/wav`, `audio/ogg`, `midi → audio/midi`,
  `audio/x-midi`. Create normalizes types (trim + lowercase); unknown types
  stay 422 "Unbekannter Materialtyp.". Upload sessions derive their
  content type from the asset type; finalize validates type-conditionally
  (score keeps the `%PDF-` magic-byte check, audio/midi check only the
  whitelist), with new messages "Die Datei ist keine gültige Audiodatei." /
  "Die Datei ist keine gültige MIDI-Datei.". `FileRevision.ContentType`
  stores the probed content type.
- New optional `Description` (≤ 500 chars) on `ArchiveAsset` via explicit EF
  migration `ArchiveAssetDescription` (nullable `description` column). The
  migration-role grants from `infrastructure/neon/runtime-grants.sql` must be
  re-run as the migrator after applying it.
- `PATCH /api/assets/{id}` edits type/voiceLabel/description with the
  established mutation pattern (antiforgery, `no-store`, editor role,
  creator-ownership 403 "Nur die anlegendende Person kann das Material
  bearbeiten."). Type changes are allowed only while the asset has no current
  revision (409 "Der Materialtyp kann nach dem ersten Hochladen nicht
  geändert werden." — relabelled files must never mismatch their content).
  Absent fields stay unchanged, empty strings clear. Song detail embeds
  `description` per asset.

Frontend (`components/noten-bereich.tsx`, `lib/assets.ts`):

- Multi-file selection with an editable per-file batch list (type select,
  voice input with suggestions from the arrangement's voice configuration +
  "Vollmix", description), filename-based voice prefill when it matches the
  suggestion vocabulary.
- Bounded-concurrency transfer (2 parallel workers) through the unchanged
  3-step protocol, per-file status (wartet / wird übertragen / wird geprüft /
  gespeichert / gescheitert), retry per failed file that reuses the created
  asset (new upload session only) so successes are never duplicated or rolled
  back, partial success shown as "X von Y Dateien gespeichert." with the
  German problem title per failed row.
- Published materials grouped by type (Noten / Audio / MIDI) and voice
  (Vollmix group for unlabeled), description text and fassung association
  always visible; pending items expose no tickets. Metadata edits use
  `PATCH /api/assets/{id}` and surface ProblemDetails titles on failure.
  Audio/MIDI get download links; players follow in ARC-018/019.

Verification: backend xUnit matrix `tests/archive/backend` grew from 17 to 28
asset tests (176 total, green), including the mixed batch, per-type validation,
partial success (a failed finalize leaves the other asset's revision untouched;
retry via a fresh session succeeds) and the metadata negatives (owner, role,
type-lock, overlong description). Playwright (`tests/materialien.spec.ts`,
desktop + mobile) covers batch upload with request-order and no-duplicate
assertions, label editing before transfer, type/voice grouping and a surfaced
403 on unauthorized metadata change; `tests/noten.spec.ts` was adapted to the
reworked flow. Full browser suite: 70 passed, 6 skipped (pre-existing
mail-gated auth E2Es, each passing with the mail stack). The Aspire
integration test `MixedVoiceBatchRoundtripThroughRealAzuriteStorage`
(`tests/archive/apphost/WalkingSkeletonTests.cs`, 3/3 suite green) runs a
mixed score/audio/MIDI batch through real Azurite with one intentionally
rejected file, its retry, label/description survival after reload, a member
PATCH rejection and byte-exact member downloads. `pnpm run check` and
`pnpm run build` stay clean; static export intact.

Still open for later slices: ARC-017 (resumable large transfers), ARC-018/019
(in-browser audio/MIDI playback), ARC-042/ARC-049 (live CORS/SAS and
managed-identity tickets).
