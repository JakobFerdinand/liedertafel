---
id: ARC-007
status: in_progress
phase: core
kind: slice
depends_on: ["ARC-005"]
touches: ["membership-admin", "auth", "db-migrations"]
external_inputs: []
---

# ARC-007 — Role changes and deactivation affect existing sessions

**Depends on:** [ARC-005](ARC-005-email-code-sign-in.md).

## Outcome

An administrator deactivates a member or changes a role, and already-open browser
sessions obey the new permissions on their next authorized request.

## Acceptance criteria

- [ ] Add administrator UI/API actions for deactivation, reactivation, and role
  changes using seeded members; require fresh verification for sensitive changes.
- [ ] Preserve stable identity and contribution history through deactivation.
- [ ] Enforce current membership/roles server-side across replicas rather than
  trusting a stale cookie role for its entire 30-day lifetime.
- [ ] Revoke access without deleting archive content; show useful signed-out or
  forbidden states to affected users. Record who performed the change.
- [ ] Define and test last-administrator handling alongside the maintainer repair
  path; avoid an accidental privilege-escalation or session-revival shortcut.

## Verification

Use two logged-in browser sessions, change membership/role in one, and exercise
protected reads and writes in the other. Verify reactivation's documented session
behaviour and denied re-verification by an inactive account.

## Handoff and parallel work

Expose a shared authorization decision for subsequent file-ticket endpoints.
Already issued tickets expire naturally; ARC-018 tests renewal denial. Coordinate
auth/admin edits with ARC-006/008, not with independent song/event UI work.
