---
id: ARC-016
status: planned
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
