---
id: ARC-015
status: in_progress
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
  ARC-049 configuration point; no storage key ever reaches the frontend.
- Local Azurite CORS bootstrap (permissive emulator rule for direct browser
  PUT/GET, no credentials needed) lives in the dev-only local storage
  initialization; live CORS/SAS behaviour stays with ARC-042/ARC-049.
- Endpoints: `POST /api/musical-versions/{id}/assets` (create logical asset),
  `POST /api/assets/{id}/upload-session` (pending object + bounded upload
  ticket), `POST /api/upload-sessions/{id}/finalize` (validates existence,
  size, `%PDF-` magic bytes, ownership; idempotent retry), `GET
  /api/assets/{id}/access` (member + visibility check → 15-minute scoped
  read/download tickets). Song detail response carries current assets per
  musical version; pending revisions are never exposed.
- Extension points for later slices: asset types (ARC-016 audio/MIDI, ARC-023
  event documents reuse the owner contract), voice labels (ARC-016),
  revision-change (ARC-031 swaps the current pointer and notifies extraction/
  search), retained-reference (ARC-037: revisions stay live until explicitly
  removed; final deletion consults retained references).

