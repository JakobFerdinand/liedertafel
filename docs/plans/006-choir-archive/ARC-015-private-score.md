---
id: ARC-015
status: done
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

- [x] Introduce a logical asset, immutable file-revision ID and current-revision
  pointer, linked to the specific musical version rather than only the song.
- [x] Create a pending upload session, transfer directly to private Blob storage,
  and validate object size/type/ownership before idempotent finalization.
- [x] Implement the provider-backed storage/ticket adapter with Aspire-managed
  Azurite/reference injection and production identity configuration points;
  start with a bounded small PDF and trace safe dependency operations to Aspire.
- [x] Read/download through scoped 15-minute tickets after visibility/membership
  checks. Pending files, drafts and mismatched target IDs remain inaccessible.
- [x] Render a usable phone/tablet PDF view and file metadata. Define asset-type,
  voice-label, revision-change and retained-reference contracts for later slices.

## Verification

Upload/view/download through a browser using local storage; retry finalization
and test wrong owner, missing object, invalid PDF, inactive member, expired ticket
and direct anonymous access. Live SAS/CORS behaviour is required in ARC-044.

## Handoff and parallel work

This is the shared upload/access boundary for batches, event documents and import.
Document extension points for new owners and file types. Coordinate changes to
upload state and revision IDs; do not couple its completion to every media player.

## Progress

Branch: `feat/arc-015-private-score`.

Design decisions (see also `architecture.md` §Uploads and §Media transfer):

- New `Assets` feature folder in `backend/`: `ArchiveAsset` (logical asset,
  owned by a `MusicalVersion`, `AssetType` string starting with `"score"`,
  optional `VoiceLabel` string for ARC-016), `FileRevision` (immutable
  revision with stable Guid v7 id, monotonic `RevisionNumber`, blob name,
  content type, byte size), `PendingUpload` (upload session state machine
  Pending → Finalized/Abandoned). `ArchiveAsset.CurrentRevisionId` is the
  current-revision pointer.
- Storage/ticket adapter behind `IAssetStorageAdapter` interface: local
  implementation over `BlobServiceClient` + `StorageSharedKeyCredential`
  (Azurite), generating blob-scoped SAS tickets (upload: PUT, read: GET,
  15 minutes; download adds `rscd=attachment`). Production identity
  configuration (user-delegation SAS via managed identity) is a documented
  ARC-051 configuration point; no storage key ever reaches the frontend.
- Local Azurite CORS bootstrap (permissive emulator rule for direct browser
  PUT/GET, no credentials needed) lives in the dev-only local storage
  initialization; live CORS/SAS behaviour stays with ARC-044/ARC-051.
- Endpoints: `POST /api/musical-versions/{id}/assets` (create logical asset),
  `POST /api/assets/{id}/upload-session` (pending object + bounded upload
  ticket), `POST /api/upload-sessions/{id}/finalize` (validates existence,
  size, `%PDF-` magic bytes, ownership; idempotent retry), `GET
  /api/assets/{id}/access` (member + visibility check → 15-minute scoped
  read/download tickets). Song detail response carries current assets per
  musical version; pending revisions are never exposed.
- Extension points for later slices: asset types (ARC-016 audio/MIDI, ARC-025
  event documents reuse the owner contract), voice labels (ARC-016),
  revision-change (ARC-033 swaps the current pointer and notifies extraction/
  search), retained-reference (ARC-039: revisions stay live until explicitly
  removed; final deletion consults retained references).

Verification (final integration pass): the backend xUnit matrix in
`tests/archive/backend/AssetApiTests.cs` (17 tests, `FakeAssetStorage`)
covers the editor upload flow with idempotent finalize, song-detail asset
embedding, role/validation negatives, wrong-owner/missing-object/invalid-PDF/
oversized/expired sessions, and member/editor/anonymous/inactive read access.
Browser Playwright specs (`src/archive/frontend/tests/noten.spec.ts`) cover
upload from the editor UI, member view/download with correct transfer order,
the empty state, oversized files and finalize-failure messaging. The Aspire
integration test `PrivateScoreUploadRoundtripThroughRealAzuriteStorage`
(`tests/archive/apphost/WalkingSkeletonTests.cs`) runs the full stack against
real Azurite: catalogue creation, direct-to-blob PUT of a tiny valid PDF to the
issued ticket URL, idempotent finalize, and ticket reads that round-trip the
exact bytes — plus the negatives (finalize without object → 409, invalid PDF →
422, access before finalize → 404, anonymous → 401, member on draft → 404,
wrong-owner finalize → 403). The integration pass surfaced a real emulator
topology bug: the server-side revision copy fetches its signed source URL from
inside the Azurite container, which cannot resolve a randomized host port —
fixed by pinning the blob host port to the container port in the AppHost
(`WithBlobPort(10000)`) and passing `DcpPublisher:RandomizePorts=false` in the
integration test. Still open for ARC-044/ARC-051: live SAS/CORS behaviour in
real Azure Storage and the managed-identity (user-delegation SAS) ticket
switch.

