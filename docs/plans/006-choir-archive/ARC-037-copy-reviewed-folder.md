---
id: ARC-037
status: planned
phase: core
kind: slice
depends_on: ["ARC-012", "ARC-017", "ARC-036", "ARC-051"]
touches: ["import", "assets", "azure-jobs", "apphost", "service-defaults", "db-migrations"]
external_inputs: ["azure-maintainer-access", "drive-sample-access"]
---

# ARC-037 — Copy one reviewed Drive folder into the independent archive

**Depends on:** [ARC-012](ARC-012-maintenance-release.md),
[ARC-017](ARC-017-large-upload-resume.md),
[ARC-036](ARC-036-import-preview.md),
[ARC-051](ARC-051-hosted-private-file.md).

## Outcome

An editor starts copying one reviewed folder and sees verified archive-owned
files ready for publication, even after the import job is interrupted and restarted.

## Acceptance criteria

- [ ] Implement a finite, manually started C# import job with scoped source
  credentials, maintenance checks, persistent progress and bounded retries.
- [ ] Register a local import execution resource in AppHost with fixture/default
  source configuration and explicit triggering. Export OTel logs/traces/metrics
  to Aspire with shared defaults, including telemetry flush when the run exits.
- [ ] Stream/chunk Drive data directly to pending Blob objects; do not stage a
  10 GB original on the container's temporary local disk.
- [ ] Verify copied identity/size/checksum as supported, finalize through shared
  asset rules, and link results to source/candidate/musical-version identities.
- [ ] Retries skip verified completed work and reconcile incomplete objects;
  source changes during transfer become reviewable conflicts.
- [ ] Keep imported content editor-only until publication and show useful
  per-file success/failure state; a failed file does not duplicate siblings.

## Verification

Copy an authorized representative folder through the real Azure job, interrupt
mid-transfer, rerun, and verify bytes/associations and absence of duplicate assets.
Exercise source-version change, revoked source access and job timeout locally.
Verify the local run and its safe diagnostic context are visible in Aspire without
manually starting database, storage or telemetry services.

## Handoff and parallel work

ARC-038 adds batch publication, not another copy mechanism. Coordinate asset
finalization with ARC-017/031 and job configuration with ARC-034; the import worker
does not technically depend on the PDF worker being implemented.
