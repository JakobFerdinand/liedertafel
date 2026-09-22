---
id: ARC-038
status: planned
phase: core
kind: slice
depends_on: ["ARC-037"]
touches: ["import", "catalogue", "assets", "db-migrations"]
external_inputs: []
---

# ARC-038 — Bulk-publish clear imports and resolve ambiguous ones

**Depends on:** [ARC-037](ARC-037-copy-reviewed-folder.md).

## Outcome

An editor bulk-accepts correctly mapped imported songs/files while uncertain or
duplicate candidates stay in a manageable private review queue.

## Acceptance criteria

- [ ] Add bounded review lists, clear/uncertain/failed filters and bulk acceptance
  with per-item validation/results, reusing verified copies from ARC-037.
- [ ] Resolve import duplicates by explicitly selecting existing catalogue
  identities, remapping or rejecting candidates before publication.
- [ ] Publish useful partial records with visible optional-metadata gaps; never
  invent missing arrangement/date information or overwrite ambiguous revisions.
- [ ] Resume review/publication and repeat scans without duplicating public
  songs, file revisions or source associations.
- [ ] Produce an import summary for operator/editor review. General-purpose
  merging of already-published duplicates remains ARC-046.

## Verification

Use a multi-folder fixture batch containing duplicate titles, keys, revisions and
one failed transfer. Bulk-publish clear items, resolve one ambiguity, rerun, and
verify exactly the intended member-visible collection with retained provenance.

## Handoff and parallel work

The capability is tested on a bounded sample here. ARC-045 performs the actual
catalogue rollout after the technical pilot; that rollout is not required to
finish this implementation slice.
