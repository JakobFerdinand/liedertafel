---
id: ARC-006
status: planned
phase: core
kind: slice
depends_on: ["ARC-005"]
touches: ["membership-admin", "auth", "db-migrations"]
external_inputs: []
---

# ARC-006 — An administrator invites another choir member

**Depends on:** [ARC-005](ARC-005-email-code-sign-in.md).

## Outcome

An administrator invites a singer from the German member-management screen;
the recipient can accept and use the existing email-code sign-in flow.

## Acceptance criteria

- [ ] List membership/invitation states and create or resend an invitation with
  the intended role; guard sensitive actions with fresh verification.
- [ ] Handle existing accounts and repeated submissions without duplicate members
  or unintended role changes; distinguish mail acceptance from delivery success.
- [ ] Invitation acceptance links to a stable account ID and records attribution.
- [ ] Members/editors cannot invoke invitation administration through the API.
- [ ] Errors leave a retryable, understandable invitation state rather than
  claiming success when the sender rejected the email.

## Verification

Invite using the local mail sink, accept in another browser, and sign in. Exercise
duplicate email/invitation submissions, failed sending, resend, and direct API
attempts by a Member and Editor.

## Handoff and parallel work

Reuse ARC-005's identity and mail contracts. Coordinate membership-admin routes
with ARC-007/008; unrelated catalogue and event slices can proceed concurrently.
