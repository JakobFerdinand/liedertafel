---
id: ARC-031
status: planned
phase: core
kind: slice
depends_on: ["ARC-028"]
touches: ["song-history", "performances", "catalogue"]
external_inputs: []
---

# ARC-031 — See a song's history with honest performance totals

**Depends on:** [ARC-028](ARC-028-performance-evidence.md).

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

## AI assistance

Counts and dates stay plain database queries. Post-launch increment, not
required for the launch gate:

- [ ] Generate a short written history per song from its recorded performances
  and evidence, with citations to the events, keeping confirmed and uncertain
  evidence distinct.
- [ ] The text is a draft in "Vorschläge" until an editor approves it; members
  see only the approved text. It is stored, never generated on page view, and
  regenerated as a new draft only when the underlying evidence changes.

Depends on ARC-013-1 and ARC-021-1 for this increment. Decisions: [architecture §14](architecture.md#14-ai-assistance).

## Verification

Use mixed evidence, uncertain dates, duplicate supporting sources, repeats and
hidden events. Verify totals, filters and links across song/arrangement views.
Changing one occurrence's evidence status changes the right total once.

## Handoff and parallel work

ARC-032 supplies passage links through the performance slot. This history view
can proceed while concert playback is built; coordinate the shared song page
with catalogue/search contributors.
