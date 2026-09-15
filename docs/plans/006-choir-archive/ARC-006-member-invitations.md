---
id: ARC-006
status: in_progress
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

## Implementation progress (2026-09-15)

Design (Identity-native, no new auth engine):

- Invited = unconfirmed `ArchiveUser` row, active = confirmed, revoked =
  locked out (ARC-007). No separate membership table.
- New `member_invitations` table links each invitation to the stable account ID
  (`UserId`), records attribution (`InvitedByAccountId`, `InvitedAt`,
  `AcceptedAt`) and retryable mail state (`LastSentAt`, `MailStatus`,
  `LastError`). Resend never changes the role silently.
- New admin endpoints (Administrator policy + fresh 10-min re-verification +
  CSRF): `GET /api/admin/members`, `POST /api/admin/invitations`,
  `POST /api/admin/invitations/resend`. Members/Editors get 403.
- Invitation mail via extended `IArchiveMailSender.SendInvitationAsync`
  (German template, `/anmelden/` link, no code). Success means "mail accepted
  by sender", never "delivered". SMTP failure keeps the invitation retryable
  and returns 502.
- Acceptance = first email-code verification confirms the address and stamps
  `AcceptedAt` on the invitation; the stable `accountId` never changes.
- Frontend `/verwaltung/` (static export, client-side API): admin-gated German
  member list + invite form + resend buttons with accessible error states.
