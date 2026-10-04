---
id: ARC-035
status: planned
phase: core
kind: slice
depends_on: ["ARC-020", "ARC-033", "ARC-034"]
touches: ["search", "document-extraction", "assets", "db-migrations"]
external_inputs: []
---

# ARC-035 — Find a song from words inside its current score

**Depends on:** [ARC-020](ARC-020-catalogue-search.md),
[ARC-033](ARC-033-score-revisions.md),
[ARC-034](ARC-034-pdf-extraction.md).

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

## AI assistance

- [ ] Text that ARC-034-1 reads from scans is searchable the same way as
  embedded PDF text, under the same current-revision and visibility rules.

No model call in the search path; search by meaning is ARC-052 and ARC-020-1.
Decisions: [architecture §14](architecture.md#14-ai-assistance).

## Verification

Search a phrase present only in a PDF, replace it with a different phrase, delay
one extraction job, and make a prior revision current again. Assert the right
results/snippets for Member versus Editor and a useful scanned-PDF fallback.

## Handoff and parallel work

Coordinate query/result changes with ARC-023/030. This completes PDF search as a
member journey; the pilot should verify it, not implement missing indexing joins.

## Groundwork already in place (2026-10-03)

`Archive.Backend.Extraction.ScoreTextAnalyzer` reads the stored extraction text
on demand: it drops music-font remnants, joins sung syllables into words
(`CleanText`) and derives voice, key, time signature, tempo and credits
(`ScoreFacts`). Nothing is persisted, so no migration exists and analyzer
improvements apply to every already extracted revision. Current consumers are
the song detail payload (`assets[].detected`, current revisions only), the
editor status query (`cleanText`, `facts`) and the chat's `song_details` tool
(`scoreFacts`, bounded `scoreText`). Search should index `CleanText`, not the
raw text. Entered catalogue values always win over recognised ones.
