---
id: ARC-048
status: planned
phase: follow-up
kind: slice
depends_on: ["ARC-044"]
touches: ["catalogue-merge", "assets", "programmes", "performances", "db-migrations"]
external_inputs: []
---

# ARC-048 — Merge duplicate arrangements with explicit version mapping

**Depends on:** [ARC-044](ARC-044-merge-songs.md).

## Outcome

An editor merges duplicate arrangements while explicitly mapping musical
versions, so scores, practice files and performance references remain correct.

## Acceptance criteria

- [ ] Extend the reviewed merge flow to arrangements under the surviving song;
  require explicit version/key mappings or preservation as distinct versions.
- [ ] Preview conflicts in current files and revision histories; never silently
  overwrite different scores or choose a practice file from another key.
- [ ] Preserve planned/actual programme pointers, evidence, recording links,
  source provenance and replaced-identity lookups with atomic stale-safe changes.
- [ ] Keep genuine repeated performance occurrences distinct; identify ambiguous
  duplicate evidence for review rather than guessing during the merge.

## Verification

Merge arrangements with overlapping keys, conflicting scores, retained revisions
and both planned and confirmed appearances. Verify current-material selection,
all links, counts, recorded-material search and rejection of a stale preview.

## Handoff and parallel work

This follow-up intentionally follows the smaller song-merge slice. Its identifier
does not imply it follows the chatbot: phase and explicit dependencies determine
scheduling. Coordinate asset/revision references with correction attachments.
