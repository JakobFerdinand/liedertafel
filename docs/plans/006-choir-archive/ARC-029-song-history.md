---
id: ARC-029
status: planned
phase: core
kind: slice
depends_on: ["ARC-026"]
touches: ["song-history", "performances", "catalogue"]
external_inputs: []
---

# ARC-029 — See a song's history with honest performance totals

**Depends on:** [ARC-026](ARC-026-performance-evidence.md).

## Outcome

A member opens a song and sees where it is documented, with confirmed
performances separated from uncertain programme evidence.

## Acceptance criteria

- [ ] Add song/arrangement history views with links to events, evidence/source
  context, known/unknown arrangements and exact/approximate dates.
- [ ] Show separate confirmed and unconfirmed counts qualified as based on
  recorded history; do not imply 120 years of complete data.
- [ ] Count stable performance occurrences rather than evidence documents or
  recordings. Preserve genuine repeated performances within one event.
- [ ] Apply publication/deletion checks to rows and aggregate counts, with
  deterministic ordering and bounded pagination.
- [ ] Provide an extension slot keyed by performance ID for upcoming recording
  links; this slice is useful without recording indexing.

## Verification

Use mixed evidence, uncertain dates, duplicate supporting sources, repeats and
hidden events. Verify totals, filters and links across song/arrangement views.
Changing one occurrence's evidence status changes the right total once.

## Handoff and parallel work

ARC-030 supplies passage links through the performance slot. This history view
can proceed while concert playback is built; coordinate the shared song page
with catalogue/search contributors.
