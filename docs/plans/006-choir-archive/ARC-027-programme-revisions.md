---
id: ARC-027
status: in_progress
phase: core
kind: slice
depends_on: ["ARC-026"]
touches: ["programmes", "db-migrations"]
external_inputs: []
---

# ARC-027 — Revise a programme without exposing unfinished edits

**Depends on:** [ARC-026](ARC-026-publish-programme.md).

## Outcome

An editor changes an already-published programme while members keep seeing the
last published version, then explicitly publishes the replacement.

## Acceptance criteria

- [ ] Start a working revision from the current published programme and edit its
  order, entries and member notes independently of the published snapshot.
- [ ] Show clear working/published states, publication timestamp and unsaved/stale
  edit feedback; publishing switches the complete member-visible view atomically.
- [ ] Handle two editors without silently overwriting the other's accepted edit
  or publishing against an outdated base revision.
- [ ] Preserve item identity where appropriate and retain the distinction from
  actual performance records. Linked scores remain current musical-version files.

## Verification

Use editor/member browsers simultaneously: partially edit, reload the member
view, publish, and observe only the complete new programme. Exercise competing
edits/publications and an event that already has historical evidence.

## Handoff and parallel work

Coordinate programme identity/locking with ARC-029; historical entry and media
playback can progress independently. ARC-040 later adds deletion/recovery to both
working and published programme state.
