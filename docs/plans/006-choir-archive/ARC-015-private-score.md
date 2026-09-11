---
id: ARC-015
status: planned
phase: core
kind: slice
depends_on: ["ARC-013"]
touches: ["assets", "catalogue-materials", "storage", "db-migrations"]
external_inputs: []
---

# ARC-015 — Upload and read one private score

**Depends on:** [ARC-013](ARC-013-first-published-song.md).

## Outcome

An editor uploads a PDF to the chosen musical version; a member opens or
downloads that score from its arrangement page through authorized file access.

## Acceptance criteria

- [ ] Introduce a logical asset, immutable file-revision ID and current-revision
  pointer, linked to the specific musical version rather than only the song.
- [ ] Create a pending upload session, transfer directly to private Blob storage,
  and validate object size/type/ownership before idempotent finalization.
- [ ] Implement the provider-backed storage/ticket adapter with Aspire-managed
  Azurite/reference injection and production identity configuration points;
  start with a bounded small PDF and trace safe dependency operations to Aspire.
- [ ] Read/download through scoped 15-minute tickets after visibility/membership
  checks. Pending files, drafts and mismatched target IDs remain inaccessible.
- [ ] Render a usable phone/tablet PDF view and file metadata. Define asset-type,
  voice-label, revision-change and retained-reference contracts for later slices.

## Verification

Upload/view/download through a browser using local storage; retry finalization
and test wrong owner, missing object, invalid PDF, inactive member, expired ticket
and direct anonymous access. Live SAS/CORS behaviour is required in ARC-042.

## Handoff and parallel work

This is the shared upload/access boundary for batches, event documents and import.
Document extension points for new owners and file types. Coordinate changes to
upload state and revision IDs; do not couple its completion to every media player.
