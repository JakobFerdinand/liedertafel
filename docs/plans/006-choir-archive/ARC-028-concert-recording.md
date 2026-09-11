---
id: ARC-028
status: planned
phase: core
kind: slice
depends_on: ["ARC-018", "ARC-023"]
touches: ["recordings", "event-materials", "media-player", "db-migrations"]
external_inputs: []
---

# ARC-028 — Publish and play a whole concert recording

**Depends on:** [ARC-018](ARC-018-practice-audio.md),
[ARC-023](ARC-023-event-documents.md).

## Outcome

An editor uploads an event recording and a member can watch/listen to it before
any song timestamps have been entered.

## Acceptance criteria

- [ ] Introduce recording identity, event ownership and original/playback asset
  references; one verified playable original may serve both roles.
- [ ] Add audio/video viewing with seeking and the existing renewable ticket
  flow, labels, publication control and missing/unsupported playback states.
- [ ] Allow multiple independently labelled recordings of the same event; merely
  uploading another recording never creates a performance occurrence.
- [ ] Preserve an incompatible original and support attaching an externally
  converted playback file; validate the candidate before marking it usable.
- [ ] Default concert downloads to disabled and enforce editor-controlled
  enablement in API/access responses as well as the UI.

## Verification

Publish a short video and a separate audio recording, play/seek as a member,
replace an unusable playback candidate and exercise explicit download permission.
Confirm the event remains useful without timestamps and no history count changes.

## Handoff and parallel work

Expose stable recording IDs, duration/playback metadata and player positioning to
ARC-030. ARC-029 can build performance history concurrently. Large-transfer and
Cold-storage validation are separate, explicit consumers of this playable flow.
