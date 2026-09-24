---
id: ARC-025
status: done
phase: core
kind: slice
depends_on: ["ARC-015", "ARC-024"]
touches: ["event-materials", "assets", "db-migrations"]
external_inputs: []
---

# ARC-025 — Read a programme scan or photograph alongside its event

**Depends on:** [ARC-015](ARC-015-private-score.md),
[ARC-024](ARC-024-historical-event.md).

## Outcome

An editor attaches a historical programme, document or photograph to an event,
and members can inspect that source on the event page.

## Acceptance criteria

- [x] Extend the existing asset-owner contract to events and add labelled upload,
  publication and member viewing for documents/images.
- [x] Preserve description/source notes and event identity; attaching a scanned
  programme does not automatically create confirmed performance records.
- [x] Use existing private upload/read tickets and enforce event visibility;
  reject arbitrary reassignment of someone else's pending object.
- [x] Provide usable document/photo viewing on phones and explicit text labels
  for images, with partial-event states when attachments are missing.

## Verification

Attach a PDF and photograph to an approximate-date event and inspect them as a
member. Test direct hidden-event file access, rejected object reassignment and
idempotent upload finalization.

## Handoff and parallel work

Publish event-asset ownership and view slots for ARC-030. Coordinate the shared
asset owner registry with catalogue/import work; event programme editing does
not need to wait for this attachment UI.

## Implementation and verification (2026-09-24)

Backend (`3851577`, blocker fix `b0a5e51`):

- Asset owner registry: `ArchiveAsset` keeps `MusicalVersionId` nullable
  beside the new `EventId` (restrict FK to `ChoirEvent`, `EventId` index),
  and the `assets` table gains the check constraint `CK_assets_owner` that
  sets exactly one of both owner columns. Catalogue projections filter on
  version ownership; additive migration `20260924145540_EventAssets`
  (snapshot extended).
- `AssetEndpoints` grows the per-owner type whitelists — version assets
  keep score/audio/MIDI, event assets accept only `document` (PDF) and
  `photo` (JPEG/PNG/WEBP) with the ARC-016-style content-type whitelists —
  and honest magic-byte validation at finalize (`%PDF-` for documents,
  JPEG `FF D8 FF`, PNG signature, WEBP `RIFF…WEBP` for photos). For photos
  the effective content type is derived from those header bytes when the
  ARC-017 block commit carries no blob content type (the declared-type
  fallback would otherwise always claim `image/jpeg`).
- Editor-only `POST /api/events/{id}/assets` creates the logical asset
  with an optional description only (≤ 500 chars, no voice label); event
  detail responses embed the resulting `documents` after `sourceNote`
  (sorted by createdAt then id), members see only finalized material,
  pending items stay editor-only and expose no tickets, and attaching
  leaves the event row unchanged — no programme or performance record
  (ARC-026 does that explicitly).
- Upload sessions are creator-only for every asset (a pending object must
  not be reassigned to another editor, 403); collection budgets scope per
  owner — the musical version's or the event's — enforced at initiation
  (declared) and finalization (actual) with 413; `GET
  /api/assets/{id}/access` keeps the unchanged ticket shape (never logged)
  and gates on the shared event visibility decision: a draft event's
  documents answer the same indistinguishable 404 as the event detail and
  material without a current revision reports the neutral pending
  message.
- `tests/archive/backend/EventAssetApiTests.cs`: 13 tests —
  `EditorAttachesDocumentAndPhotoThroughUploadProtocol`,
  `FinalizeValidatesDocumentAndPhotoContent`,
  `PhotoMagicBytesAcceptJpegPngAndWebp`,
  `PhotoFinalizeDerivesTypeFromMagicBytesWithoutStoredContentType`,
  `EventAssetFinalizeIsIdempotent`,
  `PublishedEventDetailListsFinalizedDocumentsToMembers`,
  `DraftEventDocumentsStayInvisibleToMembers`,
  `MemberAccessReturnsViewAndDownloadTicketsForPublishedEvent`,
  `OtherEditorsCannotTouchAnotherEditorsEventAsset`,
  `AttachingAssetsLeavesTheEventRowUnchanged`,
  `EventAssetBudgetIsScopedPerEvent`, `OwnerWhitelistsRejectForeignTypes`,
  `OverlongEventDescriptionIsRejected`; `UploadSessionResumeTests` seeds
  the foreign pending session directly because a second editor can no
  longer initiate a session on someone else's asset.

Frontend (`23835f3`, review fix `5d3c50f`):

- `components/auftritt-dokumente.tsx` takes over the ARC-024 empty
  section on `/auftritt/?id=` (`components/auftritt-detail.tsx` refetches
  the whole event after material changes): the member Lesesaal shows
  photographs with the description as caption and alt text and documents
  with Öffnen/Herunterladen beside the type/size line — each entry
  fetches its ticket once and renews silently before expiry (audio-player
  recipe), and removed material (404), an expired session (401) and
  transient failures explain themselves per entry with Erneut versuchen.
- The editor workbench follows the noten-bereich recipe on the unchanged
  ARC-017 upload engine (create asset, upload session, block transfer to
  the ticket, finalize, per-row progress, retry over the same asset,
  resume from `arc-upload-<assetId>` after a reload); the file chooser
  pre-selects document vs photo from the MIME type, description/type
  edits ride `PATCH /api/assets/{id}` with the type locked after the
  first upload, and a second editor's foreign material explains the 403
  in its row.
- `lib/events.ts` types the event-asset contract and provides
  `createEventAsset`/`patchEventAsset` plus the document/photo helpers;
  `groesseText` moves to `lib/assets.ts` for shared reuse;
  `app/globals.css` adds the Auftritts-Dokumente block (auto-fill photo
  grid that stacks on phones, large view as a real dialog) with existing
  tokens only.
- `tests/auftritt-dokumente.spec.ts`: 6 route-mocked tests × desktop +
  mobile — member viewing with text labels, per-material ticket errors
  with retry, the honest empty section, the full editor attach/transfer
  round-trip, description/type editing with rejected-change copy, and
  the second-editor 403 in the failed row.

Verification evidence:

- `dotnet build src/archive/Archive.slnx` clean; `dotnet test
  tests/archive/backend` **321 passed** including the 13 new
  `EventAssetApiTests` listed above.
- Frontend `pnpm run check` clean (Biome + route types + tsc) and
  `pnpm run build` green; the route-mocked Playwright spec passed
  **12/12** (6 tests × desktop + mobile), covering the 403 copy for a
  second editor and the pending/hidden material states members must
  never see.

Fresh reviewer subagents after the slice (backend and frontend); findings
fixed in place:

- Blocker (fixed, `b0a5e51`): photo finalize on the ARC-017
  content-type-less block-commit path stored `image/jpeg` for every
  upload, so PNG/WEBP originals were mislabelled and the honest
  magic-byte check compared against the wrong type. The effective photo
  content type is now derived from the header magic bytes with the
  pinned fallback test
  `PhotoFinalizeDerivesTypeFromMagicBytesWithoutStoredContentType`.
- Should-fix (fixed, `5d3c50f`): the second-editor rejection test now
  asserts the exact AssetOwnerMessage copy in the failed row, and a
  still-armed sibling wish reference could bind the chosen file to the
  wrong material (the wish reference is cleared per button).
- Nits (fixed): a mismatched re-selected file also cancels the stale
  remote session so its budget releases immediately, the caption is a
  proper `figcaption` as the last child of its figure (editing tools sit
  beside it), the large view labels itself through its caption
  (`aria-labelledby`), and the rejected-edit mock answers the realistic
  concurrency 409 instead of a type-lock rejection.
