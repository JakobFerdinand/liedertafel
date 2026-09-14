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

## Access and existing-project baseline — 2026-09-14

Authenticated read-only inspection with Neon CLI `4.17.3` succeeded. The
maintainer-access prerequisite is available in the local working environment.

| Input | Verified value |
| --- | --- |
| Organization | `org-round-tree-63490380` |
| Organization plan | Free |
| Project | `liedertafel-archiv` (`bitter-base-66886756`) |
| Region | AWS Frankfurt (`aws-eu-central-1`) |
| PostgreSQL version | 18 |
| Default branch | `production` (`br-frosty-moon-b2n9p446`), ready |
| Effective project permission | `ADMIN` |
| Creation source | Neon console |

Setup guidance: leave Neon Auth disabled. The archive owns invitations, email-code
verification and sessions using ASP.NET authentication; Neon supplies PostgreSQL.
Neon Auth is an optional application-user authentication service, not a requirement
for authenticated database connections. Its actual enabled/disabled state was not
checked during this read-only project inventory.

Reproduce the non-secret inventory with the locally authenticated CLI:

```bash
neon orgs list --output json
neon projects list --org-id org-round-tree-63490380 --output json
neon branches list --project-id bitter-base-66886756 --output json
```

Specify the organization explicitly: project listing otherwise prompts for an
organization, which is unsuitable for non-interactive runs.

The [current plan documentation](https://neon.com/docs/introduction/plans)
confirms Free includes 0.5 GB storage per project, 100 CU-hours and 5 GB public
transfer per project/month, with compute suspension after five idle minutes.
These are limits, not measured archive usage.

This establishes access and a project baseline, not completion of the trial.
Provider evaluation, disposable-resource creation/update/import/deletion evidence,
repeatable setup, an encrypted SQL connection test, and runtime/migration role
separation remain outstanding. Use a separate disposable project for lifecycle
testing; the existing `production` branch is not a trial deletion target. Check
the hosted PostgreSQL version against the local stack during the trial. Retrieve
credentials through local authentication/secret tooling, not committed output.
