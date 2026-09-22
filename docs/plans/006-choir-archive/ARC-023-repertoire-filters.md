---
id: ARC-023
status: planned
phase: core
kind: slice
depends_on: ["ARC-014", "ARC-015", "ARC-020"]
touches: ["search", "catalogue", "db-migrations"]
external_inputs: []
---

# ARC-023 — Filter to suitable arrangements with usable materials

**Depends on:** [ARC-014](ARC-014-arrangements-and-keys.md),
[ARC-015](ARC-015-private-score.md),
[ARC-020](ARC-020-catalogue-search.md).

## Outcome

A conductor filters songs by voice configuration, key, instrumentation or occasion
and sees which matching arrangement actually has the desired material.

## Acceptance criteria

- [ ] Add editor fields and member filters for voice configuration, accompaniment/
  instrumentation, musical key, language, occasion/tags and available file types.
- [ ] Keep unknown values explicit and optional; tags supplement structured
  relations. Duration/difficulty are not silently added.
- [ ] Apply combined conditions to the matching arrangement/version where
  appropriate, not different siblings that collectively happen to satisfy them.
- [ ] Material filters count authorized published/current score/audio/MIDI assets;
  reserve a recording-availability extension point for ARC-032 and store filter
  state in the URL. ARC-032 owns the actual performance-to-recording integration.
- [ ] Give bounded result counts and clear/reset controls without exposing drafts.

## Verification

Use one song whose two arrangements satisfy different filters and verify it cannot
falsely match their combination. Test unknowns, hidden files, empty results, and
URL restoration on a phone.

## Handoff and parallel work

Recording slices register their material availability through this query contract
when integrated. Coordinate shared search SQL/UI with ARC-035; unrelated event
entry can progress in parallel.
