---
id: ARC-008
status: in_progress
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

## Implementation progress (2026-09-15)

Design (Identity-native, no new auth engine):

- Verified admin email change keeps the stable v7 `accountId`: `POST
  /api/admin/members/email/request { accountId, newEmail }` creates a
  salted-hash challenge in the new `member_email_changes` table and mails a
  German confirmation code to the **new** address via
  `IArchiveMailSender.SendEmailChangeCodeAsync`; `POST
  /api/admin/members/email/confirm { accountId, newEmail, code }` proves
  ownership and then rewrites `Email`/`UserName` on the same user row,
  follows `member_invitations.NormalizedEmail`, deletes obsolete
  `auth_sign_in_codes` rows and consumes pending change rows.
- Collision: a new address belonging to another account returns 409 with no
  change and no mail. Same-address requests return 400. Change codes live in
  their own table, so receiving the change mail alone never creates a
  session: a sign-in request for the not-yet-assigned new address gets the
  uniform no-disclosure 202 without mail, and the change code is rejected by
  `/api/auth/code/verify`.
- Session handling: confirm bumps the target security stamp and appends an
  `EmailChanged` audit row. `AuthSetup.OnValidatePrincipal` rejects tickets
  with `AuthenticatedAt` older than the latest `Reactivated`/`EmailChanged`/
  `AdministratorRepaired` audit, so the target's open sessions die on the
  next request (same contract as ARC-007 reactivation) and a fresh code at
  the new address is required. The acting admin session is untouched unless
  the admin changed their own address.
- Repair command: new finite `--repair-admin` operator command (same
  `Archive:OperatorToken` permission boundary as `--bootstrap-admin`;
  Development needs no token). Modes: `--email <addr>` ensures a confirmed
  active Administrator for that address (reactivates/clears lockout,
  confirms, grants the role; creates the account + invitation row when
  absent), and `--accountId <id> --email <new>` moves an existing account to
  a free address when the old inbox is unavailable (collision-checked,
  challenges invalidated). Actor is `Guid.Empty` (maintainer, not a member);
  the operator name is carried in the audit `Note` and in structured logs by
  domain only. No passwords are stored and no login secrets are printed.
- Frontend `/verwaltung/`: per-row new-address input plus code request and
  confirmation with German accessible states, fresh-verification re-login
  prompt and 409 collision display.
- Second-maintainer access (handoff to ARC-042 for the Azure permission
  boundary): run the published container/CLI with the migration-role
  connection plus `Archive:OperatorToken` from Key Vault, e.g.
  `dotnet run --project src/archive/backend --no-launch-profile --
  --repair-admin --email <addr> --operator <name> --operator-token <token>`
  (add `--accountId <id>` for an address move). The token is never baked
  into the image and no universal admin password exists.
