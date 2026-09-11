---
id: ARC-011
status: planned
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

- [ ] Provision Neon Free Frankfurt through the selected path; apply existing
  auth migrations explicitly using separate migration/runtime roles.
- [ ] Persist the production key ring in private Blob Storage, protect it with
  Key Vault, and wire runtime credentials/sender permissions securely.
- [ ] Bootstrap only intended pilot accounts and exercise the hosted private
  shell against Neon; no local identity or development key leakage is allowed.
- [ ] Verify key retention/rotation handling for valid cookies, cross-instance
  challenge/session state, bounded cold-start connection handling, and revocation.
- [ ] Verify shared Service Defaults do not introduce database-dependent liveness,
  production exports to a developer dashboard or mandatory AppHost hosting.
- [ ] Finalize the chosen OpenTofu state bootstrap if that path was selected;
  keep provisioning state/credentials inaccessible to the runtime.

## Verification

Complete sign-in, restart/replace the app, and repeat across two replicas. Test
expired/reused codes, unauthorized API access and a failed database connection.
Record actual cold-start timings and ensure no database-dependent keep-alive probe.

## Handoff and parallel work

This is the first hosted identity/database slice; it does not wait for catalogue
features. It enables live jobs, maintenance releases, and storage integrations.
Coordinate shared configuration changes with ARC-009/010 owners.
