# Auth/passkey parity (#221)

The frozen C# backend/tests and the frontend remain unchanged. Authentication is
implemented in `crates/archive-server/src/auth`; migration 2 adds only
`archive_sessions`, `archive_csrf`, and `archive_ceremonies`. Identity users,
roles, passkey COSE JSON, salted email-code hashes, invitations, and audit enum
numbers remain canonical. The canonical Neon grant script is unchanged; the
native migration gate exercises runtime DML on all three additive tables and
still denies both migration histories and runtime DDL.

## Security boundary

`passkey.rs` is the single **expert-core adapter** to the pinned, mature
`webauthn-rs-core 0.5.5` OpenSSL maintenance family (see the source/support
qualifications and audit loop in [`auth-dependency-audit.md`](auth-dependency-audit.md)). The upstream warning on
`new_unsafe_experts_only` matters: review this adapter when changing policy or
upgrading that crate. Its use is deliberate, not a claim of independent audit.
The high-level passkey builder does not request required resident credentials,
and its discoverable UUID helper rejects Identity's existing 36-byte handles.

- Upstream generates challenges and verifies complete ceremonies: client-data
  type/challenge/origin/cross-origin, RP hash, UP/UV, algorithms, signatures,
  counters, and signed backup flags. No server-side ceremony/signature crypto is
  reimplemented, no verifier failure is bypassed, and public options are not
  patched independently of their matching server state.
- Required UV/resident policy is set on the upstream builder; attestation is
  `none` without a CA/device-trust requirement, as in the frozen application.
  Strict counters and unchanged backup eligibility remain enabled. Zero/zero
  counters are allowed; any nonzero non-increase fails.
- Discoverable login resolves the credential ID against Identity, then checks
  the returned handle against **that row's** Guid-text account ID before calling
  the real verifier. It never authorizes by an unverified handle alone.
- Legacy public keys are imported through upstream's COSE parser. Existing JSON
  stays intact except verified counter/backup updates; old names, timestamps,
  attestation bytes and client-data bytes are not rewritten. Newly enrolled keys
  use the same PascalCase JSON and standard-Base64 COSE storage, without inventing
  certificate trust. Management IDs are standard Base64; ceremony IDs are URL-safe.
- The separately authorized aggregate inventory found **one existing ES256 key**,
  no unsupported/unparsed keys. This adapter imports/offers ES256, **not every
  algorithm .NET theoretically offered**. Before cutover, the maintainer must
  separately authorize/recheck aggregate compatibility: the running frozen host
  can still enroll other algorithms. An unsupported key is a stop/escalation,
  never a reason to drop a key, reset a counter or require re-enrollment.

Ceremony state is server-only JSON in PostgreSQL, one row per opaque browser and
operation, five-minute expiry, registration bound to the account **and session**.
An atomic DELETE claims it once before verification, including on failure.
Account locks serialize registration caps, credential counter updates, code
consumption, and session issuance. Enrollment rechecks membership, freshness,
session validity, and the cap inside the account-locked transaction.

Sessions are 256-bit opaque cookies with **only SHA256 digests stored**. The
30-day sliding lifetime matches the actual frozen cookie configuration (not its
unused `SessionLifetime` option). Renewal only UPDATEs existing rows; it cannot
resurrect revoked tokens or reset immutable `verifiedAt`/`authMethod`. Every
request rereads confirmation, current lockout, current roles, security stamp,
and reactivation/email-change/repair audit resets. Logout revokes all account
sessions and bumps the Identity stamp. `Auth::revoke_sessions` is the shared
account-transaction seam for #222. Role changes alone keep the session/freshness.

`Auth::authorize` + `RequestAccess::respond` is the tested member/editor/admin
request seam for later routes: CSRF before authorization/effects, current database
decision, unified German errors, no-store and sliding-cookie renewal. Later
sensitive admin mutations additionally check `current.is_fresh` and take the
same account lock before their writes. Resource visibility is not implemented
early merely to make this auth gate appear broader.

CSRF works before login through a separate HttpOnly/Strict browser cookie and
bounded PostgreSQL context. The reusable request token is bound to the current
session (or explicitly to pre-auth). GET reuses an unexpired token for parallel
requests; identity changes require a fresh token. The exact 400 title is
`Ungültiger Sicherheitstoken.` so unchanged `postAuth` can retry once before any
domain effect. Passkey options do not rotate CSRF; anonymous login options need
no header, login verification does. Full-page navigation after login preserves
the unchanged passkey client's no-retry behavior. Expired state is deleted in
bounded indexed batches. Cookies are always Secure in production and follow the
effective forwarded HTTPS scheme in Development; forwarding trusts one managed
ingress hop, like `Program.cs`, not an arbitrary public deployment topology.

Email-code sign-in remains required: six digits, salted SHA256, ten minutes,
five attempts/code, 60-second resend suppression, 5/email/hour, 30/IP/hour,
10 failures/email/ten minutes and Identity's 20-failure/15-minute lockout.
Abuse decisions serialize across replicas. Mail failure expires the exact
undelivered challenge for immediate safe retry. Only code verification confirms
email/stamps invitation acceptance. SMTP uses lettre only in Development; the
minimum production code-sender uses keyless ACS Email REST 2023-03-31 with the
official Azure identity credential, same-origin HTTPS polling, no redirects or
send retry and a total timeout (45 seconds by default). #222 owns the complete
invitation/change/test-mail templates, operator commands, dev connection-string
support and the remainder of `AzureMailSenderTests`; those are not claimed here.

The dormant `verify_identity_password` compatibility seam has .NET-generated
V2/SHA1 and V3/SHA1/SHA256/SHA512 vectors, constant-time comparison, bounded input,
salt/subkey sizes and iterations (600,000 maximum), and at most two blocking
CPU tasks, including cancellation. There is no password route/creation/rehash.
This pure-logic seam is the explicit #218/#221 compatibility requirement, not a
new sign-in mode. German invariant normalization preserves `ß`, rather than
expanding it to `SS` and losing existing Identity rows.

Auth telemetry retains `archive.auth.*`, auth-result tags and mail spans.
Credential/state-dumping WebAuthn and sender SDK logs are globally suppressed
even under `RUST_LOG=trace`; application events are fixed/redacted and request
spans omit queries/headers. Native OTLP wire tests inspect traces, metrics, logs
and console output for secret markers. No production mail, identity or AI call
is used by these tests.

## Oracle mapping

Rust HTTP suites are under `crates/archive-server/tests/`; only mail delivery and
time are replaced. Database, one-time state and cryptography are real.

| Frozen case(s) | Rust evidence |
| --- | --- |
| `AuthApiTests.RequestVerifyMeLogoutRoundTrip` | `auth_api::request_verify_me_logout_round_trip` |
| `UninvitedEmailGetsIndistinguishableResponse`, `ExpiredCodeIsRejected`, `VerifyAttemptLimitLocksChallenge`, `ResendCooldownSuppressesMailButKeepsSameResponse`, `RequestAbuseLimitReturns429WithoutDisclosure`, `CodesAreNeverStoredPlaintext` | `unknown_email_uniformity_expiry_reuse_attempts_and_resend_limits` |
| `ReusedCodeIsRejected` + real PG race from the AppHost oracle | `concurrent_code_verification_issues_one_session_and_replay_fails` |
| `AuthMutationsRejectMissingOrForgedCsrf`, `MeIsAnonymousEnvelopeWithoutCookie` | same-named `auth_api` translations, plus scope/parallel-reuse/logout test |
| `AuthTelemetryRedactsCodesEmailsAndCookies` | `tests/browser/native-auth.cjs`: real native processes + SMTP boundary + gRPC OTLP collector, including passkey verification under trace logging |
| `SessionSurvivesRestartWhenKeysPathRetained` | session/shared-DB HTTP test + native process restart/replica test; keyring-specific assertions are inapplicable below |
| `SessionCookieIsHttpOnlySameSiteAndSecureInProduction`, `HostedForwardedHttpsServesAntiforgeryWithoutServerFailure` | `production_and_forwarded_https_keep_secure_strict_httponly_cookies_and_dev_seed_absent` |
| `DevSeedCreatesAllRolesAndStaysAbsentInProduction` | HTTP seed is used by the real browser/native suites; production absence is asserted; complete seed/operator coverage belongs to #222 |
| `BootstrapCreatesFirstAdminAndRefusesSecond`, `BootstrapOutsideDevelopmentRequiresOperatorToken` | **#222**, not claimed as translated in #221 |
| All eight `PasskeyApiTests` cases | `auth_api` options/freshness/claims/missing-CSRF tests + `passkey_api` management/invalid-credential tests; real browser ceremonies below supplement the policy-only oracle |
| `ArchiveAccessTests.DecideMember…`, `DecideEditor…`, `ValidateAntiforgery…`, `RequireMemberAndEditor…`, `DecideAdministrator…`, `RequireAdministrator…` | `archive_access` HTTP gate: anonymous/inactive 401, member/editor/admin 403/allow, no-store, CSRF-first and next-request database decisions |
| `IsAuftrittsAufnahmeMemberVisible…`, `IsAufnahmeMemberVisible…`, `CanChangeRecordingFiles…`, `MemberLiederAndMemberAuftritte…` | Resource visibility: **#223/#225/#227**, not falsely claimed as auth coverage |
| `ApiTests` cookie/CSRF/JSON miss cases | `auth_api`, `http_contract`, `process_startup` and native harness; implemented routes are removed from the unported-route fixture only |

Additional real-signature cases cover legacy Guid-text handles, wrong RP/origin/
port/subdomain/UP/UV/handle, bad signature, challenge expiry/replay/browser/operation/
session binding, cap races, counter races/rollback/zero, BE changes in both
directions, backup-state persistence, unconfirmed/locked/roleless accounts,
ownership, auditing, membership changes, expiry, and original freshness.

### Explicit Data Protection exception

#218 drops Data Protection fidelity and old-session interoperability. Each
`DataProtectionConfigurationTests` case is **inapplicable**, not a Rust pass:
`ProductionWithoutKeyUrisThrows`, `ProductionWithPartialOrNonHttpsUrisThrows`,
`ProductionForbidsLegacyFilesystemKeysPath`,
`ProductionEscapeKeepsEphemeralKeysWithoutUris`,
`ProductionWithBothHttpsUrisConfiguresWithoutNetworkIo`, and
`DevelopmentKeepsFilesystemKeys`. XML files, Blob/Key Vault URIs and ephemeral
key escape switches have no Rust equivalent. Native shared-PG restart/replica,
expiry, stamp/audit revocation and digest-at-rest evidence replace the session
properties formerly inferred from a retained ring. Passkeys are preserved;
existing Data Protection sessions reset at cutover as explicitly authorized.

## Reproducing local gates

Use a dedicated **local disposable PostgreSQL 17** server, never a live database.
The HTTP harness creates unique databases and runs the actual `--migrate` binary;
the migration gate additionally needs pg_dump 17 and its own empty fixed names.
For remote Podman use the stdin/exec wrapper from the baseline README.

From `backend-rs`, with command-scoped Cargo PATH where necessary:

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
# Explicit SQL gates, otherwise reported as ignored:
cargo test -p archive-server --locked --test auth_api --test passkey_api \
  --test archive_access -- --ignored
cargo test -p archive-server --locked --test migrate_process \
  postgres_migration_contract -- --ignored --nocapture
```

Install test-only Node dependencies outside the checkout (Node 24):
`npm install --prefix /tmp/opencode/221-browser @playwright/test@1.63.0 pg@8.23.1`.
Set `NODE_PATH` to that directory's `node_modules`; `ARCHIVE_CHROMIUM` may point at
an existing Chromium. No frontend package/source/test is edited.

- `node tests/browser/native-auth.cjs` with `ARCHIVE_TEST_POSTGRES` starts the
  actual Rust binary, synthetic SMTP receiver, OTLP collector, two replicas and
  repeated native restarts. It imports the exact synthetic .NET row and signs
  fresh assertions with test-only PKCS#8 material. It cleans only its own DB.
- `node tests/browser/ceremonies.cjs enroll` with `ARCHIVE_BASE_URL`,
  `ARCHIVE_MAIL_URL` (local Mailpit), and `ARCHIVE_AUTH_DATABASE` proves email →
  enrollment → logout → discoverable login through the **unchanged static UI**.
  `legacy` imports the synthetic .NET row into a separate empty test DB, installs
  the same CDP credential, and proves browser login/counter/JSON preservation.
  Start its Rust origin with `ARCHIVE_ENVIRONMENT=Development`, a local OTLP
  endpoint, `ConnectionStrings__archive-db` pointing at that explicitly migrated
  throwaway database, `Mail__Provider=Smtp`, Mailpit's `Mail__Host`/`Mail__Port`,
  `PORT` matching the base URL and `ARCHIVE_STATIC_ROOT` pointing at the export.
- `generate` runs that UI enrollment against the frozen **.NET** origin and
  captures the actual Identity JSON and CDP test credential in
  `tests/fixtures/dotnet-es256.json`. It is explicitly synthetic, including the
  private key, and contains no production rows or identifiers. Generation was
  performed against frozen sources at `0ff95e6`, not by fabricating Identity JSON.
- `dotnet run --project tests/identity-fixtures` regenerates synthetic PBKDF2
  vectors; `-- --normalization` prints the .NET German normalization oracle.

The frozen `frontend/tests/passkey.spec.ts` was also executed unchanged using
`tests/browser/playwright.config.cjs`. It **fails** at its stale “Willkommen im
Archiv.” locator: current `components/archiv-bereich.tsx` renders “Dein Konto”.
This is now **also reproduced against the native frozen .NET host**, with the
same byte-identical static export: one test executed, zero skipped, failure at
the original line 102 after successful email login and `/archiv/` navigation.
See [`auth-browser-baseline.md`](auth-browser-baseline.md) for hashes, command,
actual output and the independent positive .NET browser control.
It also uses a substring success locator matching “Noch kein Passkey registriert.”.
It is not counted as passing or modified to hide that drift. The Rust-owned
browser harness asserts the actual current account UI and the exact enrollment
success text, then performs the real cryptographic ceremonies. A fresh static
build can be made from `git archive 0ff95e6 src/archive/frontend` in a separate
temporary directory; the checkout's frontend stays untouched.

Live ACS/managed identity and production aggregate rechecks are **not run**;
provider protocol tests use the actual transport boundary. Full Aspire/image
retargeting and resource/AI routes remain their later waves, not #221 evidence.

## Initial verification snapshot (9450e95, 2026-11-09)

After merging the current integration tip (`0ff95e6`, already up to date), the
sequential format → clippy → workspace-test gates passed. Workspace tests:
**31 passed, 21 explicitly ignored**. All ignored tests were then run explicitly:
**20 auth/access/passkey PostgreSQL tests** (12 + 1 + 7) and **one native
migration/schema/runtime-grant test**, all passed. The native restart/replica/
SMTP/gRPC-OTLP harness and both Chromium flows passed against the latest binary
and a fresh static export of the frozen frontend. The selected frozen C# auth,
passkey, access and Data Protection oracle run passed **46/46**, no skips;
its existing NuGet vulnerability warnings are unchanged, not fixed by this port.
The frozen Playwright test failure above remains a failure, not a passing gate.

**That initial candidate was not dependency-audit clean / release-ready.** An additional
`cargo deny --locked check` with upstream cargo-deny 0.20.2 found:

- **RUSTSEC-2023-0071** in `rsa 0.10.0-rc.19`, pulled by `crypto-glue 0.2.2`
  through the pinned WebAuthn family. The advisory concerns private-key timing;
  this adapter only imports/verifies ES256 and has no RSA private-key operation.
  That limited application exposure is not an advisory waiver. No ignore was
  added. The advisory's fixed `rsa 0.10.0` was released on 2026-11-08, but
  `cargo update -p rsa --precise 0.10.0` fails resolution: crypto-glue still
  requests the removed `sha2` feature. No newer compatible crypto-glue/WebAuthn
  release was available when checked. Resolving the upstream dependency or
  selecting/revalidating another maintained verifier is a handoff blocker;
  do not patch cryptographic validation or suppress the check to hide it.
- License policy rejects the MPL-2.0 WebAuthn/crypto-glue family and newly used
  0BSD/ISC components; it also reports Zlib through the existing SQLx graph.
  `deny.toml` is unchanged: no blanket copyleft acceptance or license exception
  was silently introduced. Maintainer review of those dependencies' licenses
  and an explicit policy decision remain outstanding.
- Bans and sources checks pass. Duplicate-version warnings remain warnings.

The audit tool was downloaded outside the checkout from the official release;
its archive matched GitHub's SHA256
`9f12ed4c49936e09b48bf862b595cde2fe64fcbd9d74dfacac6131ca824c8d5f`.
No dependency vulnerability, license, production, or cutover approval is implied
by the functional test evidence above.

## OpenSSL maintenance-line follow-up

The pinned verifier is now upstream **0.5.5**, not a patched or forked 0.6.2.
The dependency change removes `rsa` and `crypto-glue` completely and makes the
advisory/bans/sources checks green without suppression. Default `cargo deny`
still fails **licenses**: actual `deny.toml` is unchanged. The exact package/SPDX
list, narrow **unapproved** policy proposal, support caveats and supported native
OpenSSL build instructions are in [`auth-dependency-audit.md`](auth-dependency-audit.md).
No license/cutover acceptance or #222 start is implied.

Full revalidation after the verifier/native-backend change used the isolated,
statically linked **OpenSSL 3.5.9** build described there, not the host's 3.6.5:

| Gate | Result |
| --- | --- |
| Sequential `cargo fmt --all -- --check` → `cargo clippy --workspace --all-targets --locked -- -D warnings` → `cargo test --workspace --locked` | Pass; **31 passed, 21 explicitly ignored** |
| Explicit local PG `auth_api`, `archive_access`, `passkey_api` | **12 + 1 + 7 passed**, no ignored cases in these gated runs |
| Explicit native PG migration/schema/runtime-role gate | **1 passed** |
| `cargo build --workspace --locked` | Pass |
| Native restart/replica/SMTP/gRPC-OTLP/redaction harness | Pass, including repeated old-row/new-challenge signatures after process restart |
| Real Chromium `enroll` and synthetic .NET-row `legacy` | Both pass against the unchanged, freshly rebuilt static frontend |
| Frozen selected .NET auth/passkey/access/Data Protection oracle suites | **46 passed, 0 skipped**; existing NuGet warnings unchanged |
| Default `cargo deny --locked check` | **Exit 4**, licenses only; advisories/bans/sources pass |
| Explicitly selected unapproved license proposal | **Exit 0**, not an active-policy pass or acceptance |

The HTTP/browser/native tests and synthetic .NET fixture were **not modified**
to accommodate the 0.5.5 change. `ldd` on the final native server lists no dynamic
libssl/libcrypto; binary inspection finds `OpenSSL 3.5.9` and not `OpenSSL 3.6.5`.
For resource-bounded local builds the Cargo runs additionally set
`CARGO_INCREMENTAL=0 CARGO_PROFILE_DEV_DEBUG=0` and `-j 2`; these are build-resource
settings, not policy changes. Static frontend `pnpm run build` was rerun outside
the checkout from the same frozen source. Live ACS, real credentials, further
production inventory and later Aspire/image gates remain unrun and unclaimed.
