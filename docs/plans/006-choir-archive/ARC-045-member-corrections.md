---
id: ARC-045
status: planned
phase: follow-up
kind: slice
depends_on: ["ARC-030", "ARC-038"]
touches: ["corrections", "assets", "db-migrations"]
external_inputs: []
---

# ARC-045 — Submit a contextual correction and resolve it as an editor

**Depends on:** [ARC-030](ARC-030-recording-passages.md),
[ARC-038](ARC-038-event-trash.md).

## Outcome

A member reports an incorrect song/event/recording identification with optional
material, and an editor reviews and resolves it in an inbox.

## Acceptance criteria

- [ ] Submit text tied to a visible catalogue/event/recording reference with an
  optional bounded attachment and explicit acknowledgement.
- [ ] Grant member upload permission only to the pending correction attachment
  they own; reuse safe transfer checks without granting catalogue write access.
- [ ] Provide an editor list/detail view and open/resolved workflow with actor
  attribution. Applying a catalogue correction remains a separate explicit edit.
- [ ] Keep submissions/attachments private to appropriate participants and editors;
  handle deleted/moved target records without losing context or exposing hidden data.
- [ ] Retry submission/resolution without duplicate records or accidental publication.

## Verification

Submit as Member, inspect/resolve as Editor and verify another Member cannot read
the private attachment or use its ticket to upload into the catalogue. Test a
deleted target and replayed submission.

## Handoff and parallel work

Enable in the follow-up phase. Coordinate shared asset-owner extension points
with merge/trash work; this does not require the duplicate-merge UI to be done.
