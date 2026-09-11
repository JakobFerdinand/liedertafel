---
id: ARC-017
status: planned
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
