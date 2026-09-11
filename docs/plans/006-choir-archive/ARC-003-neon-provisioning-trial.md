---
id: ARC-003
status: planned
phase: core
kind: decision
depends_on: []
touches: ["neon-provisioning", "planning"]
external_inputs: ["neon-maintainer-access"]
---

# ARC-003 — Prove and choose the Neon provisioning path

**Depends on:** None.

## Outcome

A maintainer can create a disposable Frankfurt database through the selected
repeatable setup path and explain why OpenTofu is or is not worth adopting.

## Acceptance criteria

- [ ] Recheck Neon Free limits, Frankfurt availability, and the candidate
  OpenTofu-compatible provider before testing; do not assume old defaults.
- [ ] Exercise creation, inspection, update, and import on a disposable project;
  inspect replacement/deletion behaviour and organization/region selection.
- [ ] Record an explicit decision: pin the proven provider/lock file, or provide
  a precise documented Neon setup. Neither path adds backup/recovery branches.
- [ ] Specify runtime versus migration database roles and secure credential
  handoff. Provisioning API credentials never belong to the web runtime.
- [ ] If adopting OpenTofu, specify protected Azure remote state, locking and
  bootstrap ownership; dispose of trial resources without publishing secrets.

## Verification

Repeat the selected setup on disposable resources and demonstrate an encrypted
PostgreSQL connection. Record lifecycle evidence and versions; redact credentials
from plans/logs and keep sensitive state outside committed files.

## Handoff and parallel work

ARC-011 consumes the selected setup and role contract. This trial can run beside
ARC-001/002/004. It settles a bounded provider decision, not all Azure deployment.
