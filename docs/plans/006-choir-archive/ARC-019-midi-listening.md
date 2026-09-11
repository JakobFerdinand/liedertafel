---
id: ARC-019
status: planned
phase: core
kind: slice
depends_on: ["ARC-016"]
touches: ["midi-player", "catalogue-materials"]
external_inputs: []
---

# ARC-019 — Listen to MIDI and change practice tempo

**Depends on:** [ARC-016](ARC-016-voice-file-batches.md).

## Outcome

A member plays an authorized MIDI file inside the app and changes tempo without
installing another application.

## Acceptance criteria

- [ ] Select and justify a bounded browser synthesis/parsing approach with
  compatible licensing and modest downloadable resources.
- [ ] Provide play/pause, seeking, volume and tempo adjustment with basic sound;
  initialize audio through a user gesture on supported mobile browsers.
- [ ] Fetch bytes only through existing authorized file access, retain MIDI
  download, and label the musical version/voice clearly.
- [ ] Stop/release audio resources on navigation and report unsupported/corrupt
  files without breaking other catalogue material. No track mixer or audio export.

## Verification

Use a representative multi-track MIDI fixture to verify tempo changes, seek and
pause/resume on desktop and phone browsers; test malformed input and permission
denial. Confirm the app does not silently depend on a remote conversion service.

## Handoff and parallel work

This player can run alongside audio work ARC-018 using the shared material/ticket
contract. Coordinate the catalogue launch button slot, not their playback engines.
