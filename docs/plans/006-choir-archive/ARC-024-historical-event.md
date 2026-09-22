---
id: ARC-024
status: planned
phase: core
kind: slice
depends_on: ["ARC-005"]
touches: ["events", "app-shell", "db-migrations"]
external_inputs: []
---

# ARC-024 — Publish an event even when its historical date is uncertain

**Depends on:** [ARC-005](ARC-005-email-code-sign-in.md).

## Outcome

An editor records a concert or other choir appearance, including one known only
as approximately 1950, and a member browses it in the event history.

## Acceptance criteria

- [ ] Create/edit/publish events with kind, title, venue, optional time, member
  notes and source context; support concerts, services and other appearances.
- [ ] Model exact/partial/approximate dates explicitly rather than inventing a
  January 1 date; define sorting/display for partial and unknown information.
- [ ] Offer a browsable historical event list and detail page with year/period
  navigation, clear uncertainty, and useful empty sections.
- [ ] Apply editor-only mutation, shared visibility/deletion contracts and
  contributor attribution. Event publication is distinct from future setlists.

## Verification

Publish exact-date and approximate-year fixtures, browse/sort them, and verify
member API access cannot reveal drafts. Check an event can be useful without
its programme, attachments or recordings entered yet.

## Handoff and parallel work

Publish stable event IDs, date representation, visibility and extension slots.
This slice runs beside catalogue work; it enables documents, historical evidence,
future programmes and recordings without requiring their complete schemas first.
