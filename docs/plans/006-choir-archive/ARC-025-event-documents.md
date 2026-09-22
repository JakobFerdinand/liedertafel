---
id: ARC-025
status: planned
phase: core
kind: slice
depends_on: ["ARC-015", "ARC-024"]
touches: ["event-materials", "assets", "db-migrations"]
external_inputs: []
---

# ARC-025 — Read a programme scan or photograph alongside its event

**Depends on:** [ARC-015](ARC-015-private-score.md),
[ARC-024](ARC-024-historical-event.md).

## Outcome

An editor attaches a historical programme, document or photograph to an event,
and members can inspect that source on the event page.

## Acceptance criteria

- [ ] Extend the existing asset-owner contract to events and add labelled upload,
  publication and member viewing for documents/images.
- [ ] Preserve description/source notes and event identity; attaching a scanned
  programme does not automatically create confirmed performance records.
- [ ] Use existing private upload/read tickets and enforce event visibility;
  reject arbitrary reassignment of someone else's pending object.
- [ ] Provide usable document/photo viewing on phones and explicit text labels
  for images, with partial-event states when attachments are missing.

## Verification

Attach a PDF and photograph to an approximate-date event and inspect them as a
member. Test direct hidden-event file access, rejected object reassignment and
idempotent upload finalization.

## Handoff and parallel work

Publish event-asset ownership and view slots for ARC-030. Coordinate the shared
asset owner registry with catalogue/import work; event programme editing does
not need to wait for this attachment UI.
