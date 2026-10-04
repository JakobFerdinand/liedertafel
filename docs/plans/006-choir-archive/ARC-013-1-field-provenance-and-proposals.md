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

- [ ] Persist per-field provenance in one generic `FieldProvenance` table for
  song, arrangement, musical-version, asset and event fields: entity type and
  id, field, source (human / regex / AI), confidence (`sicher` / `unsicher`),
  model, prompt version, previous value, time, actor. Add it through an
  explicit EF migration.
- [ ] Move the song, arrangement, version and event mutations that AI features
  touch out of the endpoint lambdas into a shared write service used by
  endpoints, proposal handlers and jobs, so validation and audit fields cannot
  drift.
- [ ] Song, arrangement and version PATCHes carry a row version and return 409
  on a stale edit; arrangement and musical version gain a row version.
- [ ] A human edit sets the field's source to human and locks it: later AI or
  regex runs never overwrite it. Reverting restores the previous value and
  locks the field the same way.
- [ ] Offer one write path for automated writers that applies a value only when
  the field is unlocked and the confidence is above a configured threshold;
  otherwise it creates a proposal.
- [ ] Persist proposals in one `Proposal` table (kind, target record, JSON
  payload, reason, confidence, source document, row version of the target at
  proposal time) with a typed handler per kind, accept / reject and editor
  attribution. Accepting applies the change through the shared write service.
- [ ] If the target changed since the proposal was made, accepting shows the
  current value beside the proposed one for a fresh decision instead of
  applying it.
- [ ] Show a "KI" badge with revert on auto-applied fields in the existing
  forms, and a "Vorschläge" page in `/verwaltung` listing open proposals by
  kind. Both are ordinary React over stored state, not agent-rendered, so they
  work while AI is paused. Members never see the badge or proposals.
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
