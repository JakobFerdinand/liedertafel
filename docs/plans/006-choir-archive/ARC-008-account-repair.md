---
id: ARC-008
status: planned
phase: core
kind: slice
depends_on: ["ARC-005"]
touches: ["membership-admin", "auth", "operator-commands"]
external_inputs: []
---

# ARC-008 — Repair an email address without losing account history

**Depends on:** [ARC-005](ARC-005-email-code-sign-in.md).

## Outcome

An administrator helps a member change email, and a technical maintainer can
repair administrative access when the old inbox is unavailable.

## Acceptance criteria

- [ ] Add a verified administrator email-change workflow that retains the account
  ID and contributions, handles collisions, and invalidates obsolete challenges.
- [ ] Define session handling during the change; receiving mail at the new address
  alone must not silently attach it to somebody else's existing account.
- [ ] Extend the restricted bootstrap command to invite/repair an administrator,
  with explicit actor/target logging and no public recovery bypass.
- [ ] Document how the second maintainer obtains permitted execution access;
  never embed a universal administrator password or output login secrets.

## Verification

Change a seeded member's address and verify history, old/new login behaviour,
collision rejection, and session handling. Exercise the repair command locally;
verify its Azure execution permission boundary in ARC-042.

## Handoff and parallel work

Provide operator-command inputs and required permissions to release tooling.
Coordinate shared identity changes with ARC-006/007; this does not block local
catalogue development using seeded identities.
