---
id: ARC-016
status: in-progress
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

- [ ] Add multi-file selection/drop with per-file status, bounded concurrency,
  retry and partial-success handling through the existing upload protocol.
- [ ] Edit type, description and voice/full-mix labels before publication;
  support different choir configurations rather than a fixed SATB-only list.
- [ ] Group published materials by type and voice, with their version association
  always visible. Scores, practice audio and MIDI offer authorized downloads.
- [ ] One failed file does not duplicate or roll back successful files; members
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
- [ ] Slice 3: partial-success guarantee test
- [ ] Slice 4: frontend batch upload + grouped material list
- [ ] Slice 5: apphost mixed-batch integration test
- [ ] Final verification + documentation
