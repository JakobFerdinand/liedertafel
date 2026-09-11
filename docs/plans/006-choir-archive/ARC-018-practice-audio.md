---
id: ARC-018
status: planned
phase: core
kind: slice
depends_on: ["ARC-016"]
touches: ["media-player", "catalogue-materials", "assets"]
external_inputs: []
---

# ARC-018 — Listen to a voice track through a full practice session

**Depends on:** [ARC-016](ARC-016-voice-file-batches.md).

## Outcome

A member selects the labelled voice track, listens/seeks in the browser, and
continues beyond the first 15-minute file-ticket lifetime.

## Acceptance criteria

- [ ] Add accessible audio play/pause, seeking and volume with clear active-file
  and voice labels; preserve the authorized download option.
- [ ] Renew file access through the API and preserve position/player state
  across renewed URLs and range requests.
- [ ] Handle unsupported formats and temporary failures with an understandable
  state, without treating an original as a verified playable derivative.
- [ ] Recheck membership/visibility for renewal; stop issuing tickets when access
  is removed, acknowledging that an existing ticket expires naturally.

## Verification

Play and seek past an accelerated ticket expiry, then verify actual long playback
in the hosted pilot. Deactivate a seeded membership in the test fixture and
assert renewal fails while an already-issued URL retains its bounded lifetime.

## Handoff and parallel work

Expose player/ticket-renewal primitives for concert video ARC-028. MIDI ARC-019
can proceed beside this slice; coordinate shared controls and material-list slots.
