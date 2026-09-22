---
id: ARC-017
status: done
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

- [x] Extend the upload session to bounded block/chunk transfers, suitable ticket
  lifetimes/renewal, and a documented retry/resume experience for approximately 10 GB.
- [x] Retain committed progress where the chosen browser/storage mechanism allows;
  define file re-selection/identity checks after page reload rather than promising
  cross-browser resume that has not been implemented.
- [x] Enforce actual per-file and configured collection limits at initiation and
  finalization; stale sessions cannot overwrite a different completed object.
- [x] Bound browser/server memory, preserve cancellation state, and supply an
  abandoned-session cleanup contract. File bytes do not traverse ASP.NET.

## Verification

Interrupt a generated/authorized representative large upload, renew its ticket,
resume, and compare size/checksum. Test cancellation, file mismatch, replayed
finalization and out-of-limit uploads. Repeat real Azure transfer in ARC-044.

## Handoff and parallel work

Publish the block/session and streaming-finalization contract to ARC-037. Batch
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
- [x] Integration: apphost interrupt → renew → resume → finalize roundtrip
  with size/checksum comparison plus cancellation/mismatch/replay/limit
  negatives through real Azurite.
- [x] Docs: README ARC-017 section, handoff contract to ARC-037/ARC-016.

## Implementation and verification

Backend (`backend/Assets/`): `PendingUpload` carries the declared file
identity (`DeclaredFileName`, `DeclaredSizeBytes`) and a terminal `Cancelled`
state (explicit migration `UploadSessionResume`). `AssetStorageOptions` gains
`UploadBlockBytes` (8 MiB), `MaxCollectionBytes` (40 GiB per musical version)
and `ExpiryGrace` (1 h); `MaxUploadBytes` defaults to 10 GiB. Upload SAS
tickets grant `Write|Create|Read`. Endpoints extend the ARC-015 protocol:
initiation validates the declared per-file/collection limits, renewal issues
a fresh ticket even after expiry, `DELETE /api/upload-sessions/{id}` cancels,
and finalize checks declared identity (409 mismatch before any storage
access, session stays retryable) plus the actual collection limit. Block-list
commits carry no content type; finalize falls back to the session's declared
whitelisted type. Abandoned-session cleanup runs through the bounded
`--cleanup-uploads` job (`UploadSessionCleaner`), same finite-job pattern as
`--worker-smoke`.

Frontend (`lib/assets.ts`, `components/noten-bereich.tsx`): the transfer
engine performs ordered block PUTs with per-block retry, block-list commit,
progress callbacks (per-row percentage in the batch UI), AbortController
cancellation wired to the cancel endpoint, and resume after reload via
`localStorage arc-upload-<assetId>`: identity re-check (name/size/
lastModified), renew, committed-block discovery through
`comp=blocklist&blocklisttype=all`, and only the missing blocks re-transfer.

Verification: 193 backend xUnit tests green (16 new
`UploadSessionResumeTests` covering limits at initiation/finalization,
renewal, cancellation, mismatch, replay and the cleanup contract). 72
Playwright browser tests pass (6 pre-existing skips), including block-order
and progress assertions, resume after reload, identity-mismatch discard and
cancel cleanup. The Aspire integration test
`LargeUploadInterruptResumeRoundtripThroughRealAzuriteStorage`
(`tests/archive/apphost/WalkingSkeletonTests.cs`) runs the full stack against
real Azurite: interrupt after the first block, renew, committed-block
discovery, resume, ordered commit, finalize with declared identity, size and
SHA-256 comparison of the downloaded revision, idempotent finalize replay,
file-mismatch 409 then correct finalize 200, cancellation 409, and
out-of-limit initiation 413. It surfaced a real product bug (block commits
arrive without a stored content type and were rejected by finalize) — fixed
by the octet-stream fallback above. Live Azure transfer stays with ARC-044;
keyless user-delegation SAS stays with ARC-051.

Handoff: the block/session contract above is published for ARC-037
(streaming finalization consumes the same session and blocks); ARC-016's
batch UI rides this engine unchanged, so transfer-state ownership is settled.
