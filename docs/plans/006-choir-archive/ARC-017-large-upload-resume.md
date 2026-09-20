---
id: ARC-017
status: in_progress
phase: core
kind: slice
depends_on: ["ARC-002", "ARC-015"]
touches: ["assets", "storage", "upload-client"]
external_inputs: []
---

# ARC-017 — Finish a 10 GB upload after an interruption

**Depends on:** [ARC-002](ARC-002-source-inventory.md),
[ARC-015](ARC-015-private-score.md).

## Outcome

An editor can transfer a representative large original with visible progress,
recover from an interrupted connection, and finalize exactly one archive file.

## Acceptance criteria

- [ ] Extend the upload session to bounded block/chunk transfers, suitable ticket
  lifetimes/renewal, and a documented retry/resume experience for approximately 10 GB.
- [ ] Retain committed progress where the chosen browser/storage mechanism allows;
  define file re-selection/identity checks after page reload rather than promising
  cross-browser resume that has not been implemented.
- [ ] Enforce actual per-file and configured collection limits at initiation and
  finalization; stale sessions cannot overwrite a different completed object.
- [ ] Bound browser/server memory, preserve cancellation state, and supply an
  abandoned-session cleanup contract. File bytes do not traverse ASP.NET.

## Verification

Interrupt a generated/authorized representative large upload, renew its ticket,
resume, and compare size/checksum. Test cancellation, file mismatch, replayed
finalization and out-of-limit uploads. Repeat real Azure transfer in ARC-042.

## Handoff and parallel work

Publish the block/session and streaming-finalization contract to ARC-035. Batch
label UI ARC-016 can proceed concurrently if transfer-state ownership is agreed.

## Progress

Branch: `feat/arc-017-large-upload-resume`.

Seams under test (TDD, red → green per slice):

1. Backend HTTP seam (`tests/archive/backend/AssetApiTests.cs`): session
   initiation with declared file identity, ticket renewal (`POST
   /api/upload-sessions/{id}/renew`), cancellation (`DELETE
   /api/upload-sessions/{id}`), finalize identity/limit checks, cleanup job
   entry point.
2. Storage seam (`IAssetStorageAdapter` + real Azurite in
   `tests/archive/apphost/WalkingSkeletonTests.cs`): upload tickets grant
   block transfer (`Write|Create|Read`), block PUT/PUT-blocklist roundtrip,
   committed-block listing for resume.
3. Frontend transfer engine seam (`lib/assets.ts` +
   `src/archive/frontend/tests/noten.spec.ts`): chunked block PUTs with
   progress, cancellation, resume after reload with file identity check.

Slices:

- [x] Backend A: declared identity + limits at initiation (migration,
  `MaxUploadBytes` raised to ~10 GiB, `UploadBlockBytes`, per-version
  `MaxCollectionBytes`, pending-budget enforcement).
- [x] Backend B: ticket renewal endpoint (extends lifetime, fresh ticket).
- [x] Backend C: cancellation state + cancel endpoint (finalize on cancelled
  → 409).
- [x] Backend D: finalize identity check + collection limit at finalization.
- [x] Backend E: abandoned-session cleanup contract (bounded job).
- [x] Frontend F: chunked block upload engine with progress + blocklist commit.
- [x] Frontend G: cancellation, resume after reload (localStorage identity
  check, renewal, committed-block resume), batch UI integration.
- [ ] Integration: apphost interrupt → renew → resume → finalize roundtrip
  with size/checksum comparison plus cancellation/mismatch/replay/limit
  negatives through real Azurite.
- [ ] Docs: README ARC-017 section, handoff contract to ARC-035/ARC-016.
