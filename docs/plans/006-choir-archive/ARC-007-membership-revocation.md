---
id: ARC-007
status: done
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

- [x] Add administrator UI/API actions for deactivation, reactivation, and role
  changes using seeded members; require fresh verification for sensitive changes.
- [x] Preserve stable identity and contribution history through deactivation.
- [x] Enforce current membership/roles server-side across replicas rather than
  trusting a stale cookie role for its entire 30-day lifetime.
- [x] Revoke access without deleting archive content; show useful signed-out or
  forbidden states to affected users. Record who performed the change.
- [x] Define and test last-administrator handling alongside the maintainer repair
  path; avoid an accidental privilege-escalation or session-revival shortcut.

## Implementation progress (2026-09-15)

Design (Identity-native, no new auth engine):

- Deactivation = Identity lockout (`LockoutEnabled` + 100-year `LockoutEnd`);
  reactivation clears it. User row, stable v7 `accountId` and
  `member_invitations` acceptance/attribution are never deleted.
- New `member_admin_actions` table (`TargetUserId`, `ActorAccountId`,
  `Deactivated`/`Reactivated`/`RoleChanged`, `OldRoles`/`NewRoles`,
  `OccurredAt`) records who performed each change; structured logs carry
  roles/counts only, never addresses or codes. Migration `MemberRevocation`.
- New admin endpoints (Administrator DB decision + fresh 10-min
  re-verification + CSRF): `POST /api/admin/members/deactivate`,
  `POST /api/admin/members/reactivate`, `POST /api/admin/members/role`
  (all `{ accountId }`, role change adds `{ role }`). 401 signed-out for
  revoked/anonymous, 403 for Member/Editor, 404 unknown ID, 409 for
  already-deactivated/active and last-administrator protection.
- Shared `ArchiveAccessService.GetDecisionAsync` loads lockout/confirmation/
  roles from the database for every decision (handoff for file-ticket
  endpoints). All admin reads/mutations use it instead of trusting cookie
  role claims, so replicas agree on the next request.
- Session enforcement in `AuthSetup.OnValidatePrincipal` (every request):
  lockout/unconfirmed/role-less tickets are rejected + signed out; differing
  DB roles replace the principal with `ShouldRenew` so the next request
  already shows the new permission; tickets with `AuthenticatedAt` older
  than the latest reactivation stay dead (no 5-minute stamp revival).
  Deactivation/reactivation bump the security stamp; role changes do not
  (session stays alive with synced roles).
- Last administrator: deactivation or demotion that would leave zero active
  administrators returns 409
  ("Die letzte Administratorin oder der letzte Administrator kann nicht
  entfernt werden. Reparatur gehört zum Wartungsweg, nicht zur
  Selbstentsperrung."). `--bootstrap-admin` still refuses once any user
  exists, so repair stays on the ARC-008 maintainer path; no
  privilege-escalation or session-revival shortcut exists (locked-out
  accounts get no code mail and verify 400s; old tickets never revive).
- Reactivation session behaviour (documented in API + UI): earlier sessions
  stay invalid; a fresh email-code sign-in is required and returns the same
  `accountId` with preserved roles/history.
- Frontend `/verwaltung/` (static export): per-row role dropdown +
  `Rolle speichern`, `Deaktivieren`/`Reaktivieren` alongside invite/resend,
  with German success/error states, fresh-verification re-login prompt and
  last-admin 409 display. `/archiv/` keeps its signed-out gate;
  `/verwaltung/` keeps its forbidden gate.

## Verification evidence (2026-09-15)

- `dotnet test tests/archive/backend`: **62/62 green** (52 ARC-005/006 +
  10 new `MemberRevocationTests`): deactivate revokes old session (me
  `authenticated:false`, admin 401), preserves ID/roles/`AcceptedAt` and
  audits actor; code request 202 without mail + verify 400 for inactive;
  reactivation keeps old ticket dead and fresh code returns same ID;
  role change visible on next `/api/auth/me` without re-login + idempotent
  same-role; demoted admin gets 403 on next admin read; last-admin
  deactivate/demote 409 with no audit then success with a second admin;
  fresh-verification/CSRF/role isolation, invalid/unknown/duplicate states.
- `ASPIRE_CONTAINER_RUNTIME=podman dotnet test tests/archive/apphost`:
  **1/1 green (40 s)**. Through the real Next.js proxy with two sessions:
  admin deactivates `neu@` → list `deactivated` (same ID/roles) → singer
  session me false + admin 401 → code request 202 without mail + verify
  400 → reactivate `active` → old ticket still false → fresh code returns
  same `accountId` → role `Member`→`Editor` visible on next me →
  last-admin deactivate/demote 409. Final Mailpit total 9 (8 ARC-006 + 1
  reactivated code mail).
- Frontend `pnpm run check` + `pnpm run build` (with `/verwaltung/`
  revocation UI) green; `dotnet build liedertafel.slnx` green. Browser
  mocks added for role save/deactivate/reactivate, stale re-login and
  last-admin 409 (same mocked-API pattern as ARC-006; full container
  browser run stays with `check-archive.yml`).
- Manual risk noted: `SecurityStampValidator` stays at 5 min on purpose —
  per-request stamp `Zero` rejected even fresh tickets in tests, so
  reactivation revival is closed by the `AuthenticatedAt` vs audit check
  instead; role `Editor` is auto-created on change like invites.

## Verification

Use two logged-in browser sessions, change membership/role in one, and exercise
protected reads and writes in the other. Verify reactivation's documented session
behaviour and denied re-verification by an inactive account.

## Handoff and parallel work

Expose a shared authorization decision for subsequent file-ticket endpoints.
Already issued tickets expire naturally; ARC-018 tests renewal denial. Coordinate
auth/admin edits with ARC-006/008, not with independent song/event UI work.

- `ArchiveAccessService` is the shared DB decision for file-ticket
  endpoints. `member_admin_actions` is the audit source. Role sync via
  principal replacement + reactivation guard are the session contract.
