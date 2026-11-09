# Mitglied administration (#222)

Built on accepted #221 (`7f71f8a`), on `ticket/222-rust-members`. The frozen C#
backend/tests and frontend are unchanged. This is wave 2b, not a cutover or an
implementation of any catalogue/storage wave. No schema migration is needed:
Identity, `member_invitations`, `member_email_changes`, and numeric audit actions
remain canonical. Migration checksums, baseline and grant scripts are unchanged.

## Shared boundaries

- `Auth::authorize` / `RequestAccess::respond` retain CSRF-first mutations,
  current membership, no-store, and sliding cookies. Administration adds the
  oracle's Administrator/fresh-verification messages. Reads need no freshness;
  either original email-code or passkey verification satisfies freshness.
- `identity::lock` is a PostgreSQL transaction advisory lock shared by member
  writes, seed and operator commands. It precedes account locks. Under it, the
  actor's membership, roles, existing session and original freshness are checked
  again through #221's `lock_active` / `check_session` facilities. A queued write
  cannot proceed after demotion/revocation or freshness expiry, including expiry
  while waiting for the final target-account lock. Auth ceremonies
  never acquire the member lock, avoiding inverse lock order.
- The last-active-admin count and removal/demotion commit in that transaction.
  Independent replicas cannot both remove their own last administrative access.
  Role changes update roles without revoking sessions or refreshing verification.
  Deactivation/reactivation and email/changed operator repair use the existing
  `Auth::revoke_sessions`; obsolete tickets never revive on reactivation.
- Invitation identity/roles/history and change-code hashes commit **before**
  delivery. Only the mail boundary is faked. Failure leaves retryable state with
  the original 502 copy; code-send rejection retains the oracle's 500 title and
  immediate safe retry. Address confirmation preserves ID, roles, invitation
  acceptance and passkeys, consumes account/address-bound codes once, rechecks
  collision, invalidates old sign-in/change/registration state, and revokes the
  target's sessions (including the acting admin when changing their own address).

## Finite commands

`--bootstrap-admin --email <address> [--name <name>] [--operator <name>]` refuses
when **any account** exists, including a roleless/member-only database. The
confirmed Administrator has no password and no fabricated invitation history.

`--repair-admin --email <address> [--accountId <guid> | --account-id <guid>]
[--name <name>] [--operator <name>]` preserves these distinct oracle paths:

- A missing address creates a confirmed Administrator, an accepted invitation
  without an inviting actor, and `AdministratorRepaired` (4), actor `Guid.Empty`,
  note `Betreiber-Reparatur durch <operator>` (default operator `Betreiber`).
- An existing address confirms/reactivates/promotes the same row, preserving
  other roles. Promotion alone preserves address spelling, failure count and an
  already-expired lockout. A truly active admin is a no-op: no extra audit,
  revocation, passkey deletion, or challenge invalidation.
- Explicit account-ID repair moves the account to a free address and always
  executes compromise recovery, even when the address is unchanged. Changed
  repair revokes **all** passkeys and sessions, invalidates obsolete challenges,
  and follows the invitation's normalized address. Ordinary email changes do
  not revoke passkeys.

Outside Development, bootstrap/repair require `Archive__OperatorToken` and
`--operator-token`, compared in constant time. Connection selection prefers
`ConnectionStrings__archive-migrations`, falling back to `archive-db` only when
absent/blank. Commands neither migrate implicitly nor require HTTP/static/mail
configuration. Success and handled failure both flush/shut down service defaults;
Development still requires OTLP. `ServiceDefaults::finish_finite_job` checks all
three flush/shutdown outcomes; configured collector failure exits nonzero even
when the account transaction already committed. Job spans and completion/failure
events use the frozen command names (`bootstrap-admin`, etc.), and handled domain
failures mark the job span Error. Native tests run a real local gRPC receiver;
they do not silently swallow exporter failures. No login secret or complete
address is printed. Other worker/migration orchestration remains its later owner.

`--seed-dev-auth` and the existing CSRF-protected `/api/dev/auth/seed` share the
same idempotent, race-safe confirmed/passwordless three-role seed. Existing names,
address spelling, lockout and unrelated credentials are preserved. Neither path
is available outside Development.

`--send-test-mail <address>` is Development + `Mail__Provider=Azure` only. The
successful command seam is tested with a fake **delivery** transport; it is not
run against ACS. Its template is clearly a test message without a sign-in code.

## Mail transport

One template source serves lettre (Development SMTP capture only) and ACS Email
REST **2023-03-31**. Production constructs the official managed-identity credential
(including `AZURE_CLIENT_ID` user-assigned selection); Development keyless uses
`DeveloperToolsCredential`. There is no credential acquisition during startup.
`Auth__MailFrom` defaults to `archiv@liedertafel-mining.at`.

Development alone may select `Mail__AzureConnectionString`. Its shared-key
authorization follows the official [ACS HMAC specification](https://learn.microsoft.com/en-us/azure/communication-services/tutorials/hmac-header-tutorial):
SHA256 of exact body bytes, RFC1123 UTC date, host and path/query, HMAC-SHA256.
Both POST and operation polling are signed. Tests independently reconstruct the
signature using ring rather than the implementation's hmac crate. These two new
direct dependencies (hmac and test-only tracing-subscriber) were already in the
locked graph; test-only Axum HTTP2 enables the local gRPC receiver. No package
version or approved license policy changed. Authorization and content-digest
headers are marked sensitive as defense in depth under #221's mandatory
process-wide SDK/HTTP log suppression.

The signing test also caught an inherited #221 defect: joining relative
`emails:send` made it a URL scheme. The absolute `/emails:send` service path is
now explicitly asserted. The total send/poll/credential deadline defaults to 45s
(`Mail__SendTimeout`, nonpositive values use the default; bounded to 120s).
Dropping a caller future cancels it, rather than wrapping cancellation as a send
timeout. No application/HTTP send retry, redirects, external-origin polling, or
unbounded response bodies are allowed. Success is service handoff, not inbox
delivery. Logs retain only status, allowlisted error codes, parsed GUID operation
IDs or fixed failure text, never provider messages or mail content.

## Frozen-suite mapping

Rust suites live in `crates/archive-server/tests`. Consolidation below preserves
the individual frozen assertions; it does not count a gated test as passed in
ordinary workspace tests.

| Frozen cases | Rust evidence |
| --- | --- |
| `MemberInvitationTests.AdminListsMembershipStates`, `AdminInvitesNewMemberWithRoleAndMail`, `AcceptanceLinksToStableAccountIdWithAttribution` | `member_invitation::admin_invites_new_member_with_role_mail_and_acceptance_attribution` |
| Same/different-role duplicate, existing active, pending/accepted/unknown resend, failed mail/retry, invalid email/role | `duplicates_resend_conflicts_validation_and_retryable_delivery` |
| Freshness, members/editors, anonymous, missing/forged CSRF | `admin_policy_csrf_freshness_and_current_membership_on_all_mutations` covers **every** admin mutation, plus reads |
| `MemberRevocationTests.DeactivateRevokesSessionPreservesHistoryAndAuditsActor`, `ReactivationKeepsOldSessionsDeadAndRequiresFreshSignIn` | `member_revocation::deactivate_reactivate_preserve_history_and_never_revive_old_session` |
| Role next-request/idempotence, demoted admin, invalid/unknown/duplicate states | `role_changes_sync_without_relogin_and_same_role_has_one_audit` |
| Last admin; revocation fresh/member/editor/anonymous guards | `last_active_administrator_decisions_are_atomic_across_replicas` + shared all-mutations guard above |
| `MemberEmailChangeTests.EmailChangeRetainsIdHistoryAndRotatesSessions`, `EmailChangeCodesAreNeverStoredPlaintext` | `member_email_change::email_change_retains_identity_history_passkeys_and_rotates_sessions` |
| Own-address logout and old sign-in codes | `own_address_change_revokes_acting_admin_and_obsolete_signin_codes` |
| Collision/no side effects, invalid/unknown/same/bad/expired/superseded codes, failed mail/retry | `email_change_validation_attempt_expiry_supersession_and_delivery_retry` + `change_codes_bind_account_and_address_recheck_collision_and_are_consumed_once` |
| Email mutation freshness/member/editor guards | Shared all-mutations guard above |
| Three `Repair…` frozen cases | Native `member_operator` create/no-op/move/promotion/collision/token tests, both account-ID aliases, passkey/session invalidation and stable audit/history assertions |
| `AuthApiTests.BootstrapCreatesFirstAdminAndRefusesSecond`, `BootstrapOutsideDevelopmentRequiresOperatorToken`, `DevSeedCreatesAllRolesAndStaysAbsentInProduction` | Native `member_operator` bootstrap/token/any-account refusal/concurrent-bootstrap and CLI+HTTP seed-race tests; #221 retains production HTTP absence |
| `AzureMailSenderTests` German templates (all roles, umlauts, defaults, test marking), from override and transport forwarding | `azure_mail_sender` shared-template/test-command tests |
| Rejection/no retry, delayed/hung/cancelled delivery, safe diagnostics | `azure_mail_sender` delay/cancellation/logging tests + #221 `auth_mail` terminal failures, same-origin polling and timeout tests |
| Sender selection/production guards | Native startup matrix in `azure_mail_sender`, including default SMTP, keyless Azure, Development shared key, and production secret refusal, without any send |
| Safe sign-in resend after rejection | #221 `auth_api::failed_mail_allows_immediate_retry_and_verification_failure_cap_is_shared` (rerun) |

Additional real-PG tests prove concurrent duplicate invitation identity, committed
state visible at the delivery boundary, one-time concurrent email confirmation,
and queued mutations losing authorization/freshness while waiting for the lock.
The native browser gate uses an **actual** enrolled Chromium passkey to prove
ordinary email-change retention across a native restart and repair revocation;
it does not replace signature verification with a fake.

## Reproducing gates

Use the supported isolated OpenSSL **3.5.9** build and narrow active license
exceptions documented in [auth-dependency-audit.md](auth-dependency-audit.md).
Scope `OPENSSL_DIR` and `OPENSSL_STATIC=1` to every Cargo build/test. Heavy gates
run sequentially, not concurrently:

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
# Explicit disposable local ARCHIVE_TEST_POSTGRES, never production:
cargo test -p archive-server --locked --test member_invitation \
  --test member_revocation --test member_email_change --test member_operator \
  --test anonymous_state --test auth_api --test passkey_api --test archive_access \
  -- --ignored
# Focused example:
cargo test -p archive-server --locked --test member_revocation \
  last_active_administrator -- --ignored
# Native migration/schema/grants: auth-parity.md + baseline README environment.
cargo test -p archive-server --locked --test migrate_process \
  postgres_migration_contract -- --ignored
cargo deny --locked check
```

With the external Node dependencies/Chromium from `auth-parity.md`, run
`node tests/browser/members-native.cjs`. It requires local
`ARCHIVE_TEST_POSTGRES` and `ARCHIVE_STATIC_ROOT` pointing at an unchanged static
export. Optional `ARCHIVE_BINARY` supports a separate Cargo target directory.
It creates/drops its own uniquely named DB, SMTP receiver, native processes and
gRPC OTLP receiver; checks actual UI invite/resend/role/deactivate/reactivate/email
repair, native bootstrap/seed/repair, success **and failure** finite flush,
member/mail metrics/spans, real passkey behavior and trace-level PII redaction.
`native-auth.cjs` accepts the same optional binary override.

## Frozen Playwright results are separate

The existing passkey stale-selector failure remains as documented in
[auth-browser-baseline.md](auth-browser-baseline.md), not a pass.

This wave also ran **unmodified `frontend/tests/mitglieder.spec.ts`** against both
native Rust and native frozen .NET (each with its own disposable PG database,
local Mailpit and the same configured static export). Both runs executed ten
tests, zero skipped: **nine mocked-API UI cases passed; the real-backend case
failed**. Rust failed at original line **582** (code request expected 202, got
400); .NET failed earlier at original line **513** (admin code request expected
202, got 400). Thus this is **not a claim of identical failing assertions**, and
neither real-backend frozen run is counted as passing. The frozen helper derives
an explicit Cookie header solely from each GET's Set-Cookie; reusable tokens
need not emit another cookie. That source-level helper concern does not justify
weakening CSRF or modifying frozen tests. The independent Rust-owned real-UI
control above retains the browser's actual cookie context and passed all flows.

Logs: `/tmp/opencode/222-{frozen-members,frozen-members-dotnet,members-native,
native-auth,dotnet-oracles}.log`. The selected frozen C# suites passed **80/80**,
zero skipped; existing NuGet vulnerability/compiler warnings remain unchanged.
Initial red evidence is in `222-{invite,revocation,email,operator,repair,mail}-red.log`
and `222-repair-state-red.log` (same temporary directory).
Additional red regressions: `222-finite-flush-red.log` and
`222-mail-header-red.log`, plus `222-target-freshness-red.log` (the old check
before the target wait incorrectly allowed the now-stale write).

## Final local verification (2026-11-09)

Merged the current integration tip (`7f71f8a`, already up to date), then ran
format → clippy → workspace tests sequentially. Every Cargo build/test used
`--locked`, isolated static OpenSSL 3.5.9, and resource-only settings
`CARGO_INCREMENTAL=0 CARGO_PROFILE_DEV_DEBUG=0 -j 2`.

| Gate | Actual result |
| --- | --- |
| fmt check, workspace/all-target clippy `-D warnings` | Pass |
| Ordinary workspace tests | **37 passed, 0 failed, 48 explicitly ignored** |
| Explicit real-PG auth/access/passkey/allocation + member/CLI suites | **47 passed, 0 ignored** (21 new member/operator tests + 26 inherited auth tests) |
| Explicit native migration/schema/canonical runtime-role grant gate | **1 passed**, unchanged migration history/checksums |
| Default `cargo build --workspace --locked` | Pass; native browser/OTLP harnesses used this normal build, not the test-only HTTP2 feature build |
| Native member UI/CLI/passkey/restart/SMTP/OTLP/redaction gate | Pass, including success and handled-failure finite telemetry flush |
| Native #221 restart/replica/legacy ES256/SMTP/OTLP/redaction gate | Pass |
| Selected frozen C# oracle suites | **80 passed, 0 skipped**; existing package/compiler warnings unchanged |
| Active `cargo deny --locked check` | **Exit 0**, advisories/bans/licenses/sources pass; policy unchanged |
| Final native linkage inspection | Embedded **OpenSSL 3.5.9**, no host 3.6.5, no dynamic libssl/libcrypto |
| Frozen Playwright, final Rust run | **11 executed, 9 mocked cases passed, 2 failed, 0 skipped**: member line 582 and known passkey line 102; neither is a pass |
| Native frozen .NET member browser baseline | **10 executed, 9 mocked cases passed, 1 failed, 0 skipped**, earlier member line 513; not an identical-assertion claim |

Final logs are `/tmp/opencode/222-{fmt,clippy,workspace,pg,migration,build,
members-native,native-auth,dotnet-oracles,deny}-final.log`,
`222-frozen-browser-final.log` and `222-evidence-summary.log`. The .NET member
browser baseline is `222-frozen-members-dotnet.log`; the prior #221 .NET passkey
baseline was not rerun. The frozen frontend export was reused, not rebuilt in
this wave; the checkout's frontend stayed untouched.

Live ACS/identity acquisition, production/Neon/AI access, further aggregate
credential inventory, full Aspire/Rust image retargeting and deployment are
**unrun/unclaimed**. The latter broad-stack and separately authorized pre-cutover
gates remain with #235/#236. No frozen source, infrastructure, runtime grants,
production image/workflow, issue, PR or deployment is changed by this ticket.
