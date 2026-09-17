---
id: ARC-011
status: in_progress
phase: core
kind: slice
depends_on: ["ARC-003", "ARC-009", "ARC-010"]
touches: ["auth", "azure-foundation", "neon-provisioning", "release-workflow"]
external_inputs: ["azure-maintainer-access", "neon-maintainer-access", "pilot-mailboxes"]
---

# ARC-011 — Sign in to the hosted archive and survive a restart

**Depends on:** [ARC-003](ARC-003-neon-provisioning-trial.md),
[ARC-009](ARC-009-first-cloud-release.md),
[ARC-010](ARC-010-azure-login-email.md).

## Outcome

A maintainer signs into the live archive using a real email code and remains
signed in when Azure replaces the container or serves a request from another replica.

## Acceptance criteria

- [ ] Provision a fresh Neon Free Frankfurt PG17 project through the selected
  documented path in `infrastructure/neon/README.md`; apply existing
  auth migrations explicitly with the separate `archive_migrator` role, run
  `runtime-grants.sql` as migrator after each migration, and give the API only
  the `archive_runtime` connection. Do not reuse the existing PG18 project
  (`bitter-base-66886756`) without an explicit version-reconciliation decision.
- [ ] Persist the production key ring in private Blob Storage, protect it with
  Key Vault, and wire runtime credentials/sender permissions securely.
- [ ] Bootstrap only intended pilot accounts and exercise the hosted private
  shell against Neon; no local identity or development key leakage is allowed.
- [ ] Verify key retention/rotation handling for valid cookies, cross-instance
  challenge/session state, bounded cold-start connection handling, and revocation.
- [ ] Verify shared Service Defaults do not introduce database-dependent liveness,
  production exports to a developer dashboard or mandatory AppHost hosting.
- [ ] Create no OpenTofu state: ARC-003 rejected OpenTofu for this scope.
  Keep provisioning API credentials and the admin database credential
  inaccessible to the runtime; the API receives only
  `ConnectionStrings__archive-db`.

## Verification

Complete sign-in, restart/replace the app, and repeat across two replicas. Test
expired/reused codes, unauthorized API access and a failed database connection.
Record actual cold-start timings and ensure no database-dependent keep-alive probe.

## Handoff and parallel work

This is the first hosted identity/database slice; it consumes the ARC-003
decision and the `infrastructure/neon/` runbook for provisioning and
database roles. It does not wait for catalogue
features. It enables live jobs, maintenance releases, and storage integrations.
Coordinate shared configuration changes with ARC-009/010 owners.
