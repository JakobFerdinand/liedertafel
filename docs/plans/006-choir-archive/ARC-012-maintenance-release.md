---
id: ARC-012
status: planned
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
