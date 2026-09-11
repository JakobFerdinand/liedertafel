---
id: ARC-038
status: planned
phase: core
kind: slice
depends_on: ["ARC-025", "ARC-027", "ARC-028", "ARC-037"]
touches: ["event-trash", "events", "performances", "recordings", "db-migrations"]
external_inputs: []
---

# ARC-038 — Recover a deleted event with its programme and recordings

**Depends on:** [ARC-025](ARC-025-programme-revisions.md),
[ARC-027](ARC-027-confirm-actual-programme.md),
[ARC-028](ARC-028-concert-recording.md),
[ARC-037](ARC-037-catalogue-trash.md).

## Outcome

An editor recovers a deleted event without losing its programme revisions,
historical evidence, actual performances or linked recording ownership.

## Acceptance criteria

- [ ] Apply the shared seven-day trash and Administrator permanent-deletion
  behaviour to events, occurrences, recordings and event-owned materials.
- [ ] Capture consistent deletion-group state across working/published programmes
  and planned/actual links, preserving pre-deletion publication status.
- [ ] Hide deleted events and their evidence from member lists/totals/file-ticket
  issuance; recovery restores references without duplicating performances.
- [ ] Respect shared assets, catalogue references and independently deleted items
  during recovery and cleanup; preserve useful reference/tombstone information
  where deletion cannot safely remove an identity.

## Verification

Delete/recover an event with a revised programme, skipped song, encore, document
and two recordings. Check visibility/totals and pointer identity before/after,
then verify expired cleanup cannot delete a still-referenced object.

## Handoff and parallel work

This completes the cross-feature trash contract before launch. Coordinate
recording/occurrence reference checks with ARC-030; unrelated import UI and MIDI
work can proceed concurrently.
