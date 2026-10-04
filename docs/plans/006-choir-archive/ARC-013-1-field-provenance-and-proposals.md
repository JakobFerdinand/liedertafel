---
id: ARC-013-1
status: planned
phase: core
kind: slice
depends_on: ["ARC-013", "ARC-014"]
touches: ["catalogue", "events", "membership-admin", "db-migrations"]
external_inputs: []
---

# ARC-013-1 — Mark AI-derived fields and review proposals in one queue

**Depends on:** [ARC-013](ARC-013-first-published-song.md),
[ARC-014](ARC-014-arrangements-and-keys.md).

## Outcome

An editor sees which catalogue fields were written by AI, reverts one with a
click, and works through everything that needs confirmation in a single
"Vorschläge" queue.

## Acceptance criteria

- [ ] Persist per-field provenance for song, arrangement, musical-version,
  asset and event fields: source (human / regex / AI), confidence, model,
  previous value, time. Add it through an explicit EF migration.
- [ ] A human edit sets the field's source to human and locks it: later AI or
  regex runs never overwrite it. Reverting restores the previous value and
  locks the field the same way.
- [ ] Offer one write path for automated writers that applies a value only when
  the field is unlocked and the confidence is above a configured threshold;
  otherwise it creates a proposal.
- [ ] Persist proposals (kind, target record, proposed change, reason,
  confidence, source document) with accept / reject and editor attribution.
  Accepting applies the change through the normal authorized write path.
- [ ] Show a "KI" badge with revert on auto-applied fields in the existing
  forms, and a "Vorschläge" page in `/verwaltung` listing open proposals by
  kind. Members never see the badge or proposals.
- [ ] Changes to identity or visibility (new song, merge, publish, delete,
  member administration) can only ever be proposals, enforced server-side.

## Verification

Write a field through the automated path above and below the threshold, edit
it by hand, rerun the automated write and confirm the human value stays.
Accept and reject a proposal as an editor; confirm a member cannot reach the
queue or the provenance.

## Handoff and parallel work

ARC-034-1, ARC-036, ARC-025-1, ARC-046, ARC-047 and ARC-031 write through this
path. It needs no model access and can be built beside ARC-021-1. Decisions:
[architecture §14](architecture.md#14-ai-assistance).
