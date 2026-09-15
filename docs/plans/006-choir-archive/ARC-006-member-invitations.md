---
id: ARC-006
status: done
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

- [x] List membership/invitation states and create or resend an invitation with
  the intended role; guard sensitive actions with fresh verification.
- [x] Handle existing accounts and repeated submissions without duplicate members
  or unintended role changes; distinguish mail acceptance from delivery success.
- [x] Invitation acceptance links to a stable account ID and records attribution.
- [x] Members/editors cannot invoke invitation administration through the API.
- [x] Errors leave a retryable, understandable invitation state rather than
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

## Verification evidence (2026-09-15)

- `dotnet test tests/archive/backend`: **52/52 green** (36 ARC-005 + 16 new
  `MemberInvitationTests`): admin list/invite/resend, fresh-verification
  rejection with a 1 ms window, Member/Editor 403 on all three endpoints,
  unauthenticated 401, same-role resend without duplication, cross-role 409
  without role change, active-address 409 without role change or mail,
  resend-accepted/unknown handling, SMTP failure → 502 with `Failed` mail
  state followed by successful resend, acceptance stamping the stable
  `accountId` with inviter attribution, CSRF rejection, invalid email/role
  400s.
- `ASPIRE_CONTAINER_RUNTIME=podman dotnet test tests/archive/apphost`:
  **1/1 green (41 s)**. Fresh managed PostgreSQL applies all five migrations
  including `MemberInvitations`. Through the real Next.js proxy: admin code
  sign-in → invite `neu@liedertafel.test` (201, "zum Versand angenommen" +
  "Zustellung wird nicht bestätigt") → invitation mail
  ("Einladung zum Liedertafel-Archiv") in Mailpit → member list shows
  `invited` → same-role resend 200, cross-role 409, active-address 409 →
  recipient code request/verify (account ID from invite equals verified
  account ID) → Member direct API attempt 403 → list shows `active` →
  resend-after-accept 409. Final Mailpit total 8 (diagnostic + 4 code mails +
  2 invitation mails + worker mail), as asserted.
- Browser suite against static production export: **14/14 mocked green**
  (desktop + mobile): validation states, non-admin gate, mocked invite/resend
  round trip, stale-verification re-login prompt. The dev-gated end-to-end
  test (`Vollständiger Einladungsfluss mit E-Mail-Code`: UI invite → Mailpit
  invitation → code → verify) runs with AppHost
  (`ARCHIVE_MAIL_URL` + dev backend); the same flow is covered in CI by the
  AppHost suite above.
- `dotnet build liedertafel.slnx`, frontend `pnpm run check` and
  `pnpm run build` (with `/verwaltung/` static route) green.

Findings for later slices: the new `Verwaltung` nav item pushed the mobile
header nav past 412 px (no wrapping), which made Playwright mobile taps miss
covered buttons; fixed with `flex-wrap` in the mobile breakpoint (ARC-007/008
should keep the header within 412 px as links grow). Identity
`AllowedUserNameCharacters` now includes German umlauts so umlaut-address
invites create users instead of failing validation. AppHost tests must use a
`UseCookies = false` client for multi-session flows: both the shared client's
jar and a plain `HttpClient` jar attach competing `archive.auth` cookies and
the server then authenticates the wrong session (observed as an admin list
returning 403).
