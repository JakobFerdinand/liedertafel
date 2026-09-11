---
id: ARC-033
status: planned
phase: core
kind: slice
depends_on: ["ARC-020", "ARC-031", "ARC-032"]
touches: ["search", "document-extraction", "assets", "db-migrations"]
external_inputs: []
---

# ARC-033 — Find a song from words inside its current score

**Depends on:** [ARC-020](ARC-020-catalogue-search.md),
[ARC-031](ARC-031-score-revisions.md),
[ARC-032](ARC-032-pdf-extraction.md).

## Outcome

A member searches remembered words that appear in a digital score and opens the
matching current score under the correct arrangement/version.

## Acceptance criteria

- [ ] Include completed extracted text in database-backed search with bounded
  snippets and clear score/arrangement attribution.
- [ ] Search only current member-visible revisions; editor-only historical text,
  pending files, hidden catalogue entries and deleted assets cannot leak.
- [ ] Replacing or reverting a score updates result eligibility immediately;
  delayed extraction can show pending status but never expose obsolete text.
- [ ] Combine text results with metadata search without duplicate song hits or
  misleading matching-version context.

## Verification

Search a phrase present only in a PDF, replace it with a different phrase, delay
one extraction job, and make a prior revision current again. Assert the right
results/snippets for Member versus Editor and a useful scanned-PDF fallback.

## Handoff and parallel work

Coordinate query/result changes with ARC-021/030. This completes PDF search as a
member journey; the pilot should verify it, not implement missing indexing joins.
