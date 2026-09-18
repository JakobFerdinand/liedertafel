---
id: ARC-012
status: in_progress
phase: core
kind: slice
depends_on: ["ARC-011"]
touches: ["release-workflow", "maintenance", "db-migrations"]
external_inputs: ["azure-maintainer-access", "github-package-access"]
---

# ARC-012 — Release a database change in a controlled maintenance window

**Depends on:** [ARC-011](ARC-011-hosted-persistent-sign-in.md).

## Outcome

A maintainer releases a small database-backed change once, while members see a
German maintenance state and conflicting writes/jobs remain paused.

## Acceptance criteria

- [ ] Serialize releases and enter a shared maintenance state before migrations;
  expose a contract that future jobs must honor before conflicting work.
- [ ] Run the selected image's reviewed migration exactly once through restricted
  tooling, never on ordinary container startup.
- [ ] Keep maintenance active on failure, record actionable diagnostics, and
  require inspected fix-forward handling before retry/reopening.
- [ ] Deploy matching app/job versions and perform targeted smoke checks before
  opening access. Define when a previous image remains schema-compatible.
- [ ] Document the operator sequence, credentials, failure handling, and code-only
  rollback. Add no database restore/export or backup job.

## Verification

Rehearse successful additive migration, competing release attempts, restart, and
failure with disposable local fixtures; verify actual production release wiring
with a safe additive change. A replay must not rerun an applied migration.

## Handoff and parallel work

Publish maintenance checks and release/job inputs to ARC-032/035. Feature code
can develop locally concurrently; serialize edits to release definitions and
shared migration snapshots during integration.

## Implementation progress — 2026-09-18 (branch `feat/arc-012-maintenance-release`)

Backend (`044d910`):

- New `Maintenance/MaintenanceConfiguration.cs`: `Archive:MaintenanceMode`
  flag, `GET /api/maintenance` (`{ maintenance, message }`, `no-store`)
  as the contract future jobs must honor, and middleware pausing every
  other `/api/*` with a German 503 (`Retry-After: 600`,
  `application/problem+json`). Probes, `/api/build`, `/api/maintenance`
  and the frontend stay available. Ordinary startup still never migrates.
- New `MaintenanceTests.cs`: **10/10 green** — contract on/off, probes
  available, conflicting work (incl. unknown API paths) 503 with German
  message, normal serving outside the window.

Frontend (`d775bb7`):

- Site-wide `MaintenanceBanner` (client, `no-store`, hidden unless the
  window is active) plus static `/wartung/` page in German. `pnpm run
  check` green (Biome + route types + `tsc`).

Release and topology (`19f9885`):

- `release-archive.yml` now serializes on `archive-prod`, enters
  maintenance and proves it via `/api/maintenance`, runs the selected
  image's `--migrate` exactly once with the per-window
  `archive-migrations-connection` Key Vault secret (masked, runner-only),
  keeps maintenance active on migration/smoke failure for inspected
  fix-forward handling, proves the window on the new revision, reopens
  access, and extends smoke checks (`/api/maintenance:false`,
  `/wartung/`, `/api/antiforgery:200`). `run_migration=false` covers
  code-only releases; push runs always migrate.
- `main.bicep` declares `maintenanceMode` (`Archive__MaintenanceMode`);
  `infra-deploy-archive.yml` preserves the live value like the image, so
  an infra re-run cannot silently reopen the window. `az bicep build`
  green.
- Compatibility rule documented in the workflow header and the runbook:
  additive-only keeps the previous image rollback-compatible;
  destructive changes require split releases and fix-forward only.
- Operator runbook (`infrastructure/archive/README.md`, ARC-012 section):
  sequence, per-window migrator credential plus the one-off Secrets User
  grant for the release identity, failure handling, code-only rollback,
  no database restore/export/backup job, and the ARC-032/035 job
  contract.

Remaining (live, needs maintainer-visible execution): place the
per-window migrator credential, grant the release identity Secrets User,
approve a release carrying a safe additive migration, and observe the
window (banner/503s, competing-run serialization, restart persistence,
failure drill, replay no-op) on production.

## Local verification — 2026-09-18 (branch `feat/arc-012-maintenance-release`)

- `dotnet test tests/archive/backend` **117/117 green** (107 prior + 10
  new maintenance tests). `dotnet build src/archive/Archive.slnx` green;
  `az bicep build` green for `main.bicep`; frontend `pnpm run check`
  green and `pnpm run build` exports all 9 routes including `/wartung`.
- Maintenance drill against the production-mode image (ephemeral keys,
  Azure mail provider selected but never contacted):
  - Flag on: `/alive` 200, `/api/build` 200 `development=false`,
    `/api/maintenance` → `maintenance:true` with the German message,
    `/api/antiforgery` → 503 German Problem Details with traceId,
    unknown `/api/*` → 503 `application/problem+json`.
  - Restart with the flag off: `/api/maintenance` →
    `maintenance:false`, `/api/antiforgery` → 200 (hosted-style
    `X-Forwarded-Proto`), unknown `/api/*` → back to 404 JSON.
- Migration replay safety rests on EF history idempotency
  (`MigrateAsync` never reruns applied versions; same guarantee the
  queued-run serialization relies on); the production replay check runs
  with the first live database release.
