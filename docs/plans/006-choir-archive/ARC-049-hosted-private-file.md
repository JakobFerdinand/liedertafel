---
id: ARC-049
status: planned
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

- [ ] Provision the live private media containers and required runtime/job roles
  through Bicep, using the existing hosting/identity outputs.
- [ ] Configure actual Blob CORS and managed-identity signing for the established
  pending-upload/finalization/read contracts; no storage key enters the frontend.
- [ ] Exercise one hosted small-file transfer and member read/download, checking
  the catalogue owner's publication/membership decision before issuing access.
- [ ] Expose explicit storage/role outputs for extraction/import jobs, while
  keeping their access separate from key-ring and provisioning state locations.
- [ ] Retain the Aspire/Azurite local path; this slice owns the live provider
  integration and does not wait for concert-player or Cold-tier functionality.

## Verification

Upload, finalize, open and download through the real domain. Verify the browser
contacts Blob directly; test anonymous access, missing/wrong permissions, expired
tickets, hidden catalogue owners and allowed/rejected origins.

## Handoff and parallel work

ARC-032/035 consume these working live storage outputs. ARC-039 later adds the
Hot/Cold recording policy. The late-assigned identifier does not make this a
post-launch issue: its phase and dependencies place it in the early core path.
