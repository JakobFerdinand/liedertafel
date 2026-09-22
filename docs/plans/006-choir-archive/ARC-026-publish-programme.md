---
id: ARC-026
status: planned
phase: core
kind: slice
depends_on: ["ARC-014", "ARC-024"]
touches: ["programmes", "events", "db-migrations"]
external_inputs: []
---

# ARC-026 — Publish an ordered programme for an upcoming appearance

**Depends on:** [ARC-014](ARC-014-arrangements-and-keys.md),
[ARC-024](ARC-024-historical-event.md).

## Outcome

An editor orders songs for an upcoming event, chooses their actual musical
versions, and explicitly publishes the programme members should follow.

## Acceptance criteria

- [ ] Add/remove/reorder entries using stable item IDs and arrangement/version
  selection, preserving repeated songs as distinct programme entries.
- [ ] Introduce separate working/published revision identities from the first
  publication; save a draft without making it member-visible.
- [ ] Publish atomically with a timestamp; members see ordered items, event details
  and notes with links to the selected musical version's current materials.
- [ ] Validate unavailable/private selections and stale edits on publication.
  Merely passing the event date does not mark its songs performed.
- [ ] Make upcoming published programmes accessible from member navigation.

## Verification

Build and publish a three-song programme, including a transposed version and a
repeated song. Verify member views, stable ordering/IDs, rejected stale changes,
and absence of draft exposure before the first publication.

## Handoff and parallel work

Publish revision/item and arrangement-picker contracts. ARC-027 extends revision
editing; ARC-029 later confirms actual performances. Historical entry ARC-028
is independent and must not share planned/confirmed state accidentally.
