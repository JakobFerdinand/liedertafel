---
id: ARC-005
status: planned
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

- [ ] Add stable account/membership IDs, Member/Editor/Administrator policies,
  invitation state, and a restricted operator bootstrap for the first account.
- [ ] Implement request/verify/logout using supported ASP.NET components and
  Aspire-managed mail capture. Hide membership discovery in unauthenticated responses.
- [ ] Enforce expiry, one-time use, bounded attempts/resends and shared abuse
  limits; define concrete settings. Persist relevant state across instances.
- [ ] Use protected HTTP-only cookies, CSRF protection, and 30-day personal-device
  sessions with server-side active-membership checks. Persist development keys.
- [ ] Provide shared current-user, authorization and fresh-code re-verification
  contracts for later admin actions, with accessible German form/error states.

## Verification

Exercise the full browser flow and API denial for uninvited users. Test expired,
reused and concurrently submitted codes, attempt limits, logout, restart with
retained keys, and state-changing requests without valid CSRF protection.
Run through AppHost and verify auth traces/structured logs in Aspire redact codes,
cookies and sensitive recipient data.

## Handoff and parallel work

Publish identity/role and mail-sender contracts. Seed test accounts for all roles
so catalogue/events need not wait for the admin UI. Cloud mail/key configuration
is verified in ARC-010/011; this slice is complete and testable locally.
