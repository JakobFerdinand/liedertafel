---
id: ARC-049
status: in-progress
phase: core
kind: slice
depends_on: ["ARC-011", "ARC-015"]
touches: ["azure-media", "storage", "assets"]
external_inputs: ["azure-maintainer-access"]
---

# ARC-049 — Upload and read the first private file on real Azure storage

**Depends on:** [ARC-011](ARC-011-hosted-persistent-sign-in.md),
[ARC-015](ARC-015-private-score.md).

## Outcome

A signed-in pilot editor uploads a PDF directly to real Blob Storage and a member
reads it through the hosted app's short-lived access ticket.

## Acceptance criteria

- [x] Provision the live private media containers and required runtime/job roles
  through Bicep, using the existing hosting/identity outputs.
- [x] Configure actual Blob CORS and managed-identity signing for the established
  pending-upload/finalization/read contracts; no storage key enters the frontend.
- [ ] Exercise one hosted small-file transfer and member read/download, checking
  the catalogue owner's publication/membership decision before issuing access.
- [x] Expose explicit storage/role outputs for extraction/import jobs, while
  keeping their access separate from key-ring and provisioning state locations.
- [x] Retain the Aspire/Azurite local path; this slice owns the live provider
  integration and does not wait for concert-player or Cold-tier functionality.

## Implementation notes (2026-09-22)

- `BlobAssetStorageAdapter` gained an Entra mode: with
  `Archive:Assets:ServiceUri` set (and no `archive-blobs` connection string)
  tickets are signed as user-delegation SAS through `DefaultAzureCredential`
  (`AZURE_CLIENT_ID` pins the user-assigned identity); the delegation key is
  cached and reused while it covers the requested window. The
  connection-string path stays untouched for Azurite.
- Hosted upload previously died with an unhandled 500
  („Die Anfrage konnte nicht verarbeitet werden.") because the hosted app has
  neither a storage connection string nor shared-key access. Ticket creation
  and copy now surface as German 502 ProblemDetails, and the promote poll
  tolerates the copy not having landed yet (404) within its deadline.
- Frontend: the CSRF token/cookie pair is fetched once per page and reused;
  parallel uploads no longer race cookie rotation into „Ungültiger
  Sicherheitstoken." — a rejected pair is refetched and the request replayed
  once (`lib/auth.ts`, browser specs in `tests/noten.spec.ts`).
- Local end-to-end (Azurite): seed → code sign-in → song/version/asset →
  upload session (SAS) → block PUT → block list commit all verified against
  the real adapter. Finalize's server-side copy still 502s locally because
  Azurite 3.35 rejects `StartCopyFromUri` with HTTP 500 (emulator gap, not a
  backend defect); hosted verification of the full transfer remains open.

## Verification

Upload, finalize, open and download through the real domain. Verify the browser
contacts Blob directly; test anonymous access, missing/wrong permissions, expired
tickets, hidden catalogue owners and allowed/rejected origins.

## Handoff and parallel work

ARC-032/035 consume these working live storage outputs. ARC-039 later adds the
Hot/Cold recording policy. The late-assigned identifier does not make this a
post-launch issue: its phase and dependencies place it in the early core path.
