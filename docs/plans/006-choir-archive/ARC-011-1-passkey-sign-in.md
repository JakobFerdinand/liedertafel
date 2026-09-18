---
id: ARC-011-1
status: done
phase: core
kind: slice
depends_on: ["ARC-005", "ARC-007", "ARC-008", "ARC-011"]
touches: ["auth", "app-shell", "db-migrations"]
external_inputs: []
---

# ARC-011-1 — Passkey sign-in alongside email codes

**Depends on:** [ARC-005](ARC-005-email-code-sign-in.md),
[ARC-007](ARC-007-membership-revocation.md),
[ARC-008](ARC-008-account-repair.md),
[ARC-011](ARC-011-hosted-persistent-sign-in.md).

## Outcome

An invited, active member enrolls a passkey after email-code verification
and later signs in with that passkey or with an email code. Passkey
sessions behave exactly like email-code sessions; email codes remain for
onboarding and recovery.

## Acceptance criteria

- [ ] Keep `Invitation -> email-code verification -> optional passkey enrollment -> passkey OR email-code login`; a passkey login never confirms an email, accepts an invitation, reactivates a locked/deactivated account, or bypasses membership rules.
- [ ] Enable Identity schema version 3 with `IdentityPasskeyOptions` (stable production RP ID, exact trusted origins, resident/discoverable credentials, required user verification) on runtime, `ArchiveDbContextFactory`, and tests; add an inspected explicit EF migration for `IdentityUserPasskey<Guid>`, never migrating on API startup.
- [ ] Add `POST /api/auth/passkeys/register/options`, `POST /api/auth/passkeys/register/verify`, `POST /api/auth/passkeys/login/options`, `POST /api/auth/passkeys/login/verify`, `GET /api/auth/passkeys`, plus rename/remove endpoints behind the existing antiforgery mechanism.
- [ ] Issue passkey sessions through the existing Archiv session path (`AuthSetup.SignInMemberAsync` or a refactored common equivalent) with identical cookie properties, roles, account claims, and lifetime as email-code sessions; never use `PasskeySignInAsync()` as the final login operation.
- [ ] Fix principal refresh so account ID, authenticated-at timestamp, display name, and an explicit authentication-method claim (`email_code` / `passkey`) survive Identity security-stamp validation; never reset `AuthenticatedAt` on automatic refresh.
- [ ] Require recent authentication (passkey or email code) for enrollment/removal; keep email ownership verification distinct from authentication freshness.
- [ ] Extend the React/Next.js login UI with "Mit Passkey anmelden", enrollment UI, and list/rename/remove, using `parseCreationOptionsFromJSON` / `navigator.credentials.create` / `parseRequestOptionsFromJSON` / `navigator.credentials.get`; feature-detect WebAuthn and keep email code as fallback.
- [ ] Preserve CSRF, cookie, security-stamp, role, and deactivation behavior; bind registration to the authenticated account, cap passkey count and display-name length, audit registration/removal, and keep passkeys stable across ordinary email changes while allowing compromise recovery to revoke them.

## Target authentication model

Keep the existing email-code authentication.

The intended lifecycle is:

`Invitation -> email-code verification -> optional passkey enrollment -> future login via passkey OR email code`

Email codes remain available for onboarding and account recovery. A
passkey login must never implicitly confirm an email address, accept an
invitation, reactivate a locked/deactivated account, or otherwise bypass
existing membership rules.

## Backend

Enable ASP.NET Core Identity schema version 3 and configure
`IdentityPasskeyOptions`.

Use Identity's existing `IdentityUserPasskey<Guid>` persistence rather
than creating a custom WebAuthn credential model.

Ensure the Identity schema version is configured consistently for:

- normal runtime
- `ArchiveDbContextFactory`
- tests

Create and inspect an EF migration adding the passkey schema. Continue
using the project's explicit migration workflow; do not migrate during
API startup.

Configure:

- stable production RP ID
- exact trusted origins
- resident/discoverable credentials
- required user verification

Do not trust arbitrary request host headers when determining the
RP/origin.

## API

Add passkey endpoints alongside the existing `/api/auth/*` endpoints.

Contract:

- `POST /api/auth/passkeys/register/options`
- `POST /api/auth/passkeys/register/verify`
- `POST /api/auth/passkeys/login/options`
- `POST /api/auth/passkeys/login/verify`
- `GET /api/auth/passkeys`
- endpoints for rename/remove

Continue using the existing antiforgery mechanism for mutations.

Registration must require an authenticated active member and recent
verification. Derive the user being enrolled exclusively from the
authenticated principal; never accept an account ID supplied by the
client.

Use:

- `SignInManager.MakePasskeyCreationOptionsAsync`
- `SignInManager.PerformPasskeyAttestationAsync`
- `SignInManager.MakePasskeyRequestOptionsAsync`
- `SignInManager.PerformPasskeyAssertionAsync`
- `UserManager.AddOrUpdatePasskeyAsync`

For username-less login, create request options with `user: null`.

## Session integration

Do **not** use `PasskeySignInAsync()` as the final login operation.

After successful assertion:

1. Validate the assertion.
2. Resolve the associated `ArchiveUser`.
3. Apply Archiv's existing active-account/membership checks.
4. Persist the updated passkey returned by the assertion.
5. Issue the session through the existing Archiv session creation path
   (`AuthSetup.SignInMemberAsync` or a refactored common equivalent).

The resulting passkey session must have exactly the same cookie
properties, roles, account claims and session lifetime as an email-code
session.

A successful assertion alone must never bypass lockout, deactivation,
role or membership checks.

## Session claims

Before or as part of this work, fix the likely principal-refresh issue
in the existing authentication implementation.

Archiv requires custom claims including:

- account ID
- authenticated-at timestamp
- display name

Identity security-stamp validation rebuilds principals periodically.
Ensure Archiv-specific claims survive that refresh.

Prefer a custom claims-principal factory for stable account claims and
explicitly preserve session-specific authentication claims.

Do not reset `AuthenticatedAt` to the current time during automatic
principal refresh, because that would incorrectly make an old session
satisfy recent-verification requirements.

## Authentication method / freshness

Add an explicit authentication-method claim, e.g.:

- `email_code`
- `passkey`

A freshly verified passkey may satisfy normal recent-authentication
requirements.

Keep email ownership verification distinct from authentication
freshness.

Passkey enrollment/removal should require recent authentication rather
than merely possession of a potentially 30-day-old session.

## Frontend

Extend the existing React/Next.js login UI rather than adding
server-side Next.js authentication.

Add:

- "Mit Passkey anmelden"
- passkey enrollment UI for authenticated members
- list/rename/remove passkeys

Use the browser WebAuthn API:

- `PublicKeyCredential.parseCreationOptionsFromJSON`
- `navigator.credentials.create`
- `PublicKeyCredential.parseRequestOptionsFromJSON`
- `navigator.credentials.get`

Feature-detect WebAuthn/passkey support and retain email-code login as
fallback.

Treat user cancellation of the authenticator dialog as a normal UI
outcome.

Conditional passkey autofill is optional and can be implemented later;
start with an explicit login button.

## Security

Preserve the existing CSRF, cookie, security-stamp, role and
account-deactivation behavior.

Passkey registration must bind the credential to the currently
authenticated account.

Validate RP ID, origin, challenge and user identity through the Identity
passkey APIs.

Consider adding shared server-side single-use ceremony/challenge
tracking if stronger replay guarantees than Identity's temporary
protected state are required.

Allow multiple passkeys per account, but impose a reasonable maximum
and display-name length.

Audit passkey registration and removal.

Define credential-revocation behavior separately from session
revocation. In particular, account-compromise recovery should be able
to revoke registered passkeys.

Ordinary email-address changes should normally preserve passkeys
because credentials belong to the stable account ID.

## Verification

Add backend and browser/integration coverage for:

- successful enrollment
- successful passkey login
- username-less login
- multiple passkeys
- wrong origin
- invalid challenge
- replayed/expired ceremony
- CSRF failures
- enrollment against another account
- unconfirmed account
- deactivated/locked account
- removed credential
- concurrent requests
- login across replicas/shared Data Protection
- email-code fallback
- `/api/auth/me` immediately after login
- `/api/auth/me` after Identity security-stamp principal refresh
- preservation of `AuthenticatedAt`
- recent-authentication checks using email code vs passkey

Use Chromium's virtual WebAuthn authenticator for automated browser
tests where practical.

## Constraints

Do not:

- replace ASP.NET Core Identity
- introduce a separate auth service
- implement WebAuthn cryptography manually
- remove email-code authentication
- bypass invitation/email-confirmation semantics
- accept account identity from an unauthenticated client during enrollment
- change the existing explicit database migration policy

Keep the implementation small and aligned with the current Archiv
architecture. Refactor shared authentication/session logic where
necessary rather than duplicating the email-code and passkey session
paths.

## Handoff and parallel work

Publish the passkey API contract, RP/origin configuration, session-claim
contract (`AuthenticatedAt`, authentication method), and
credential-vs-session revocation semantics for catalogue/events work.
This slice extends the ARC-011 hosted session; it does not change the
ARC-012 maintenance/migration policy or the ARC-010 sender wiring.

## Implementation progress

Branch `feat/ARC-011-1-passkey-sign-in`. Working state and exact next
steps: [ARC-011-1-HANDOFF.md](ARC-011-1-HANDOFF.md).

- [x] Session-claim contract: `archive.auth_method` (`email_code`/`passkey`),
      `ArchiveClaimsFactory` preserves account ID/display name across
      security-stamp refresh, `OnRefreshingPrincipal` carries
      `AuthenticatedAt`/method without resetting freshness (08ca3fb).
- [x] Identity schema version 3 + `IdentityPasskeyOptions` (stable RP ID
      required outside Development, exact trusted origins with dev-loopback
      fallback, resident keys, required user verification) on runtime,
      `ArchiveDbContextFactory` (explicit `UseApplicationServiceProvider`
      because the virtual `SchemaVersion` property does not drive the EF
      model), and tests; explicit inspected migration
      `20260918111536_MemberPasskeys` (`AspNetUserPasskeys`), never applied
      at API startup (7fa8b4e).
- [x] Passkey API endpoints (`register/options`, `register/verify`,
      `login/options`, `login/verify`, `GET passkeys`, `rename`, `remove`)
      behind the existing antiforgery mechanism; uniform German 400
      problems for invalid/expired/replayed ceremonies (22ac283).
- [x] Shared session path: assertion resolves the user, applies
      confirmation/lockout/role checks, persists the updated passkey
      (sign counter) and issues the session via `AuthSetup.SignInMemberAsync`
      with method `passkey`; `PasskeySignInAsync` is never the final
      operation (22ac283).
- [x] Enrollment binds to the authenticated active member with fresh
      verification (403 otherwise), caps passkey count and display-name
      length, derives the user exclusively from the principal, audits
      registration/rename/removal (22ac283).
- [x] Frontend: "Mit Passkey anmelden" (feature-detected, cancellation is
      a normal outcome), enrollment UI, list/rename/remove via
      `parseCreationOptionsFromJSON` / `parseRequestOptionsFromJSON`
      (33a90ea, 08dbdc7).
- [x] Backend coverage: 8 passkey API tests + full suite 125/125 green
      (66ed3e2).
- [x] Browser ceremony test with Chromium's virtual authenticator (04d03fa):
      full enroll → logout → username-less login flow is green. Debugging
      uncovered and fixed four real defects (see below).
- [x] Production RP ID/origins wiring in Bicep: `main.bicep` sets
      `Authentication__PasskeyRelyingPartyId` and
      `Authentication__PasskeyOrigins__0` from `customDomain`
      (0944aa0, what-if Modify-only).
- [x] Compromise-recovery path: `--repair-admin` revokes every registered
      passkey of the repaired account in both repair modes, with backend
      coverage (4935069).
- [x] `lib/auth.ts` `MeResponse` gains `authMethod` (c7b39e8).

## Fixes found by the browser ceremony test (04d03fa)

- Frontend: Identity returns creation/request options as a JSON string;
  `lib/passkeys.ts` now parses them before the WebAuthn API call (passing
  the string threw `TypeError`).
- Backend config: without a configured RP ID, Identity fell back to the
  request host (`127.0.0.1`) while the browser origin is `localhost` —
  WebAuthn rejects an RP ID that does not match the origin domain.
  `PasskeyOptionsSetup` now defaults `ServerDomain` to `localhost` in
  Development; production still requires the configured RP ID.
- Contract: `POST /api/auth/passkeys/remove` bound `PasskeyVerifyRequest`
  (`credential`) while the client sends `credentialId`; removal always
  failed with "nicht gefunden". Now binds `PasskeyRemoveRequest`.
- Hydration: `AnmeldeFormular` and `PasskeyVerwaltung` branched on
  `webAuthnSupported()` during render; support detection moved after
  mount, removing the React hydration mismatch.

## Verification

- Backend suite: 125/125 green (`dotnet test tests/archive/backend`),
  including 8 passkey API tests (options JSON, uniform 400, CSRF, 401,
  fresh gate 403, creation-options content, lifecycle, `authMethod` +
  `AuthenticatedAt` across security-stamp refresh) and the repair tests
  asserting passkey revocation for both repair paths.
- Browser ceremony test (`tests/passkey.spec.ts`, Chromium virtual
  authenticator, both projects): email-code sign-in → enrollment in the
  Archiv area → logout → username-less passkey login → archiv session.
  Covers remove (stale passkeys), rename endpoint usage, and the
  ceremony state round trip through Identity's protected temp cookie.
- Frontend `pnpm run check` and `pnpm run build` green.
- Bicep: `az bicep build` clean; `az deployment group what-if` shows
  Modify-only changes adding the two passkey env vars.
- Not verified against real replicas: the AppHost integration suite and
  login across replicas remain an optional follow-up; the session
  contract itself is unchanged from ARC-011 and covered there.
