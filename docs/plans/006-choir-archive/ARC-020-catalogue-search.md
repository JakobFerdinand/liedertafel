---
id: ARC-020
status: planned
phase: core
kind: slice
depends_on: ["ARC-013"]
touches: ["search", "catalogue", "app-shell", "db-migrations"]
external_inputs: []
---

# ARC-020 — Find a song by title, creator or remembered words

**Depends on:** [ARC-013](ARC-013-first-published-song.md).

## Outcome

An editor enters alternate titles/creators/lyrics, and a member finds the correct
catalogue entry from the search-first home using those words.

## Acceptance criteria

- [ ] Extend catalogue editing for alternate titles, composer/arranger/lyricist
  information and entered lyrics/opening words where not already present.
- [ ] Implement bounded, database-backed search with deterministic pagination,
  useful German text behaviour, and matching arrangement context in results.
- [ ] Preserve query state in navigation and provide usable loading/empty/error
  states on phone and desktop.
- [ ] Share publication/deletion authorization with catalogue reads; draft text
  cannot leak through results, result counts or snippets.
- [ ] Establish an authorized search-result contract that can later include PDF
  matches without replacing this initial searchable journey.

## Verification

Search title variants, creator names, German umlauts and entered opening words
against public/draft fixtures. Verify deterministic results, limits and query
restoration; no dedicated search service is introduced.

## Handoff and parallel work

ARC-021 adds filters and ARC-033 adds PDF text. Coordinate catalogue metadata and
home-screen edits with ARC-014/029; file-transfer implementation can proceed separately.
