---
id: ARC-044
status: planned
phase: follow-up
kind: slice
depends_on: ["ARC-030", "ARC-033", "ARC-036", "ARC-038"]
touches: ["catalogue-merge", "catalogue", "performances", "import", "db-migrations"]
external_inputs: []
---

# ARC-044 — Merge two duplicate songs without losing their history

**Depends on:** [ARC-030](ARC-030-recording-passages.md),
[ARC-033](ARC-033-search-score-text.md),
[ARC-036](ARC-036-import-review-publication.md),
[ARC-038](ARC-038-event-trash.md).

## Outcome

An editor previews and merges two published song identities, preserving their
arrangements, source references, materials and documented appearances.

## Acceptance criteria

- [ ] Preview the selected surviving song and all affected references; allow
  deliberate resolution of conflicting titles/metadata before confirmation.
- [ ] Reparent arrangements rather than silently merging different arrangements
  or keys; preserve file revisions, programme links, evidence and provenance.
- [ ] Apply atomically with stale-preview protection, repeat-safe handling,
  attribution, and a stable lookup/redirect for the replaced identity.
- [ ] Recompute authorized search/history views without duplicate occurrence
  counts. Do not auto-collapse two potentially genuine repeated performances.

## Verification

Merge duplicate-title songs with distinct arrangements, score revisions,
confirmed/unconfirmed history and imported sources. Verify every link, totals,
search and an attempted concurrent edit after preview.

## Handoff and parallel work

Phase is follow-up even if technical dependencies finish before launch. ARC-048
adds arrangement-level merging as a separate operation. Correction inbox work
can proceed beside this slice with coordinated shared reference changes.
