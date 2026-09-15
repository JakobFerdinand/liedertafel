---
id: ARC-005
status: done
phase: core
kind: slice
depends_on: ["ARC-001"]
touches: ["auth", "app-shell", "db-migrations"]
external_inputs: []
---

# ARC-005 — An invited member signs in with an email code

**Depends on:** [ARC-001](ARC-001-local-walking-skeleton.md).

## Outcome

A locally invited member requests a German email code, enters it, reaches the
private archive shell, and can sign out. The flow uses real API/database state.

## Acceptance criteria

- [x] Add stable account/membership IDs, Member/Editor/Administrator policies,
  invitation state, and a restricted operator bootstrap for the first account.
- [x] Implement request/verify/logout using supported ASP.NET components and
  Aspire-managed mail capture. Hide membership discovery in unauthenticated responses.
- [x] Enforce expiry, one-time use, bounded attempts/resends and shared abuse
  limits; define concrete settings. Persist relevant state across instances.
- [x] Use protected HTTP-only cookies, CSRF protection, and 30-day personal-device
  sessions with server-side active-membership checks. Persist development keys.
- [x] Provide shared current-user, authorization and fresh-code re-verification
  contracts for later admin actions, with accessible German form/error states.

## Implementation progress (2026-09-14/15)

Implemented in `d0039af` (German sign-in flow + gated member area) and `8d3995a`
(email-code backend). Closed out with `3d754af`/`4a768ac` (36-test harness,
provider-agnostic one-time consume), `cb374fa` (AppHost seed → Mailpit → verify
→ logout round trip), `4bc7eaa`/`0541100` (mocked + real dev-gated browser flow),
`c526222` (automated OTLP redaction test), `7f89216` (frontend check hygiene).

## Architecture decision (2026-09-15): ASP.NET Core Identity (supersedes custom engine)

Per maintainer directive, the hand-rolled account/membership/session engine is
replaced by ASP.NET Core Identity (Core docs, `AddIdentity` +
`ConfigureApplicationCookie` + `UserManager`/`SignInManager`; verified against
the `IUserTwoFactorTokenProvider<TUser>` API reference for .NET 10). The HTTP
contract (routes, German messages, `archive.auth` cookie name, `/api/auth/me`
shape) and the frontend are unchanged.

- `ArchiveUser : IdentityUser<Guid>` (stable v7 IDs, `DisplayName`) and
  `ArchiveRole : IdentityRole<Guid>` (`Member`/`Editor`/`Administrator`) replace
  `Account`/`Membership`. Invitation state uses Identity semantics: invited =
  unconfirmed user row, active = confirmed, revoked = locked out (ARC-007).
- Email codes are a custom `IUserTwoFactorTokenProvider<ArchiveUser>`
  (`EmailCode`, purpose `signin`): salted-hash challenges with expiry,
  attempt caps and optimistic one-time consume stay in `auth_sign_in_codes`
  (now keyed to the Identity user); rate-limit evidence stays in
  `auth_request_log`.
- Sessions use the Identity application cookie (30 days, sliding, HTTP-only,
  SameSite Strict) with `SecurityStampValidator` revalidation; logout also
  bumps the security stamp so tickets die server-side. `freshAge`-style
  re-verification for later admin actions stays a claim-based contract.
- Operator bootstrap (`--bootstrap-admin`) and dev seeds go through
  `UserManager`/`RoleManager`; no password hashes are ever stored
  (passwordless-only).
- Schema: new migration creates the standard `AspNet*` tables and retires
  `auth_accounts`/`auth_memberships` (no production data exists yet).

## Verification

Exercise the full browser flow and API denial for uninvited users. Test expired,
reused and concurrently submitted codes, attempt limits, logout, restart with
retained keys, and state-changing requests without valid CSRF protection.
Run through AppHost and verify auth traces/structured logs in Aspire redact codes,
cookies and sensitive recipient data.

## Verification evidence (2026-09-15, Identity engine)

- `dotnet test tests/archive/backend`: **36/36 green**, including request/
  verify/logout round trip, uninvited-indistinguishability, expired/reused
  codes, attempt/resend/abuse caps, CSRF rejection, plaintext audit,
  key-retained restart, production cookie flags, dev seeds, both bootstrap
  paths, and `AuthTelemetryRedactsCodesEmailsAndCookies` (local OTLP
  collector asserts `archive.auth.*`/`archive.mail.send` spans and metrics
  carry no code, address or session ticket).
- `ASPIRE_CONTAINER_RUNTIME=podman dotnet test tests/archive/apphost`:
  **1/1 green (49 s)**. Fresh managed PostgreSQL shows all four migrations
  pending (`InitialArchive`, `AuthSignIn`, `AuthSignInConcurrency`,
  `IdentityAuth`), explicit migrate clears them, Blob/queue/mail exercise
  passes through the real Next.js proxy, then seed → no-disclosure check →
  Mailpit code → verify → `/api/auth/me` → user-bound-CSRF logout →
  anonymous check, then a **five-way concurrent verify: exactly one 200,
  four uniform German 400s**, and four captured mails.
- Browser suite against the production container (`liedertafel-archive:verify`,
  no Node runtime, UID 1654) with real PostgreSQL/Mailpit/Azurite:
  **14/14 green** (desktop + mobile): deep links, JSON API errors, dev
  diagnostics, outage retry, German validation states, mocked code request,
  unauthenticated gate, and the real UI end-to-end flow
  (seed → code → `/archiv/` → logout) with per-project addresses.
- Manual Npgsql race via curl (5 parallel verifies): 1× `Anmeldung
  erfolgreich.`, 4× uniform invalid-code message, zero server failures, no
  PII in structured logs. `dotnet build liedertafel.slnx`, frontend
  `pnpm run check` and `pnpm run build` green.

Findings recorded for later slices: concurrent `UserManager` writes race on
the Identity concurrency stamp, so bookkeeping calls tolerate conflicts
(`AuthRequestLog` stays the enforcement source) and seeds/bootstrap verify
end state after unique violations. The five-way race lives in the AppHost
suite because InMemory integer identity keys collide across parallel
contexts (Npgsql identity columns do not). Parallel browser projects need
separate addresses (one-time codes by design). Manual Azurite runs need
`--skipApiVersionCheck` with the pinned SDK (AppHost-managed Azurite handles
version compatibility itself). Turbopack-dev hydration stalls in this
sandbox (dead HMR websocket); the static production export hydrates and
interacts correctly, so all browser evidence above uses production builds.
Cloud mail/key configuration stays with ARC-010/011.

## Handoff and parallel work

Publish identity/role and mail-sender contracts. Seed test accounts for all roles
so catalogue/events need not wait for the admin UI. Cloud mail/key configuration
is verified in ARC-010/011; this slice is complete and testable locally.
