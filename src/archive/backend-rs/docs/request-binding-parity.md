# Raw request-binding remediation (#218 predecessors / #224)

Status: implemented on `ticket/224-rust-upload-trash`, **pending independent
rereview, not accepted/merged**. #225 remains blocked. The catalogue fix is
`683b7fa`; the auth/member follow-up is a separate `fix(auth)` commit. Both build
on integration `8ab47bb`. Frozen C#, tests, frontend, schema and dependency policy
are unchanged.

## Scope and oracle

Source audits: `/tmp/opencode/liedertafel-218-upload-review.md` (two Low findings)
and `/tmp/opencode/liedertafel-218-request-binding-audit.md` (four Low findings,
including its non-findings). Frozen contracts are in `Auth/{AuthEndpoints,
PasskeyEndpoints,MemberAdminEndpoints}.cs`, `Development/DiagnosticEndpoints.cs`,
the Lied field converters, upload endpoints and Lied draft request records.

`crates/archive-server/src/binding.rs` is the one scoped streaming record binder.
It validates each recognized occurrence before insertion, matches record names
case-insensitively, retains the last wire value, and preserves null. An earlier
wrong JSON type cannot be erased by a later valid exact/mixed-case duplicate.
Domain-invalid **strings** can still be overwritten by later strings; this is
typed binding, not early domain validation. Catalogue unknown-field/dictionary
semantics are retained. Auth unknown values are skipped only for that route.

Auth/member have exactly these 13 nullable-string DTOs:

| Operation | Canonical string fields |
| --- | --- |
| code request | email |
| code verify | email, code |
| invite | email, displayName, role |
| resend | email |
| deactivate | accountId |
| reactivate | accountId |
| role | accountId, role |
| email request | accountId, newEmail |
| email confirm | accountId, newEmail, code |
| passkey login verify | credential |
| passkey register verify | credential, name |
| passkey rename | credentialId, name |
| passkey remove | credentialId |

CamelCase canonical keys feed the unchanged domain handlers. Logout, login/
register options and Development seed read **no JSON body**. Existing CSRF,
membership, freshness, session, crypto, role and transaction policy stays intact.
`AccountId` remains a string DTO with subsequent domain parsing; no typed-Guid
constraint was added to JSON, CLI or paths. No default-role bug is alleged/fixed.

Actual frozen `Archive.Backend.dll` SHA256:
`a31547b75ae0fdd0b93547d52e6817c0d90cb2e056e7a5e005164de517ba2b89`,
MVID `d2b58c7a-9545-454e-9618-e64deb4d08de` (same assembly as the source audit).
External probe sources/logs are retained locally:

- `/tmp/opencode/224-remediation-binding/`: **111** actual catalogue serializer
  observations; committed `tests/fixtures/upload-binding-oracle.json`.
- `/tmp/opencode/224-auth-binding-oracle/`: **830** actual observations, reflected
  across all 13 DTOs / 22 properties: **593 rejected, 237 accepted**; committed
  `tests/fixtures/auth-binding-oracle.json` (one observation per line).
- `/tmp/opencode/224-http-binding/`: **93 actual HTTP observations**, using
  reflection to invoke the unchanged frozen `LiedTestHost`, real endpoint binding
  and CSRF/sign-in helpers. Slim route/raw/status/title observations are committed
  in `tests/fixtures/http-binding-oracle.json`; dynamic options/cookies are omitted.
  The host uses its existing InMemory/fake-storage fixture, **not real PG**.

HTTP proves empty/null DTO bodies reach domain missing-field validation and that
all four bodyless routes accept malformed bytes. Finalize empty/null/object reach
the missing-blob 409, while nonobject/malformed bodies fail before that path.
**Development's exception handler returns 500 for typed binding failures**.
Rust's controlled 400 is not a claim that frozen HTTP universally returns 400.
Ordinary catalogue integer fields follow HTTP quoted-integer handling; custom
Lied rowVersion still refuses strings. Those numeric rules do not apply to the
13 auth/member string DTOs.

## HTTP/PG evidence and red loops

Tests use the explicitly requested raw HTTP/router + real migrated PostgreSQL
seams; only time, delivery and provider boundaries already present are replaced.

- `upload_binding`: **2 tests**; 84 non-finalize fixture bodies, 27 finalize
  bodies × pending/finalized (**54**) and three optional-body controls. Invalid
  finalize binding preserves stored session/provider calls before terminal replay.
- `auth_binding`: **4 tests**. All **593** rejected fixture bodies run with valid
  CSRF/auth and outstanding code/login/register state; full SQL row snapshots
  (including request logs, account/role/passkey/invitation/email-change/audit,
  session and ceremony state) and mail counts stay unchanged. Four additional
  optional-name type controls use a new invite-eligible email, not an existing
  account conflict. All **237** accepted observations reach the same domain
  behavior as their actual-.NET canonical bound values; **39** empty/null/object
  controls match the frozen HTTP status/title. **20** bodyless controls include
  logout revocation. Successful raw invitation/resend/code-login/role/deactivate/
  reactivate/email-change flows pin wire order and optional null display names.
- Additional `passkey_api` test: three real ES256 signed logins over fresh
  challenges with both mixed-case orders and exact duplicates, actual rename/
  remove and numeric **unknown** name on remove. Numeric known enrollment name
  preserves the ceremony; null name reaches its normal claim and deliberately
  malformed credential rejection. Existing signed-negative/crypto tests stay intact.
  Route-specific numeric unknown role on resend / code on email-request also pass.

Red runs (all explicit, none ignored): `224-binding-red.log` **0 pass/1 fail**;
`224-finalize-binding-red.log` **0/1** (one filtered);
`224-numeric-binding-red.log` **0/2**; `224-auth-binding-red.log` **0/1**;
`224-auth-bodyless-red.log` **0/1** (three filtered). The first auth red matrix
allowed mutations/session changes and thus later cascading CSRF differences;
it is not 593 independent red failures. The bodyless red temporarily reinstated
the old parsing requirement after typed binding was fixed; that guard was removed.
Final green is the complete explicit suite below, not a count of matrix cases as
separate test passes. Focused intermediate test-setup corrections (including a
canonical domain 404 on missing passkey removal) are not production fixes.

## Final verification (2026-11-09)

Sequential fmt check → all-target/workspace clippy → workspace → active deny,
then explicit gates; every Cargo build/test uses `--locked`, static OpenSSL
**3.5.9**, persistent own target, `CARGO_INCREMENTAL=0 CARGO_PROFILE_DEV_DEBUG=0`
and `-j 2`. No manifest/lock/advisory/license change.

| Gate | Actual result |
| --- | --- |
| fmt / clippy `-D warnings` / default workspace build | Pass |
| Ordinary workspace | **57 passed, 0 failed, 122 ignored** |
| Explicit all real-PG auth/member/catalogue/upload/trash/draft/races | **117 passed, 0 ignored** |
| Separate migration/schema/canonical runtime grants | **1 passed** |
| Private Azurite 3.35.0 | **4 passed, 0 ignored** |
| All ordinary ignored subsequently executed | **122/122** (117 + 1 + 4) |
| Active cargo deny / native linkage | Exit **0**; embedded **3.5.9**, no dynamic libssl/libcrypto |
| Detector/lifecycle controls | **110 passed, 0 skipped** |
| Native auth / members / catalogue / upload + private replays | All **4 pass**, all **4 replays clean** |
| Unchanged selected frozen .NET suites | **234 passed, 0 skipped**, 22 named classes (full selection retained in command file) |
| Unchanged frozen static/mock Playwright | **59 scheduled: 58 passed, 1 skipped** |

Logs: `/tmp/opencode/224-followup-{fmt,clippy,workspace,deny,pg,schema-grants,
azurite,build,linkage,detector,native-auth,members-native,catalogue-native,
upload-native,dotnet-oracles,frozen-browser}.log`, plus four `*-replay.log` files.
Exact frozen selection: `224-followup-dotnet-command.txt`. Native captures are
private 0700/0600 (never publish values): auth `223-native-auth-telemetry-f09xJZ`,
members `223-members-native-telemetry-AxtVoJ`, catalogue
`223-catalogue-native-telemetry-WCpCZC`, upload `223-upload-native-telemetry-A3ESRJ`.

The mocked UI result is **not** backend acceptance: its real member flow skips
without a Development backend. Previous frozen real-member/passkey failures remain
failures, not rerun/relabelled here. The original uncaptured redaction failure
remains unclassified. Native real UI/SMTP/crypto/OTLP evidence is separate.
The frozen video-job permanent-removal FK **23503/500** limitation is preserved
and re-exercised in the explicit PG suite, not a successful-removal claim. No live
AI/ACS/Neon/hosted storage, push, issue/PR write, acceptance/integration/main merge,
deploy, cleanup or #225 work occurred; later-wave and hosted/cutover gates remain
unclaimed. Independent review is coordinated by the maintainer.

Focused reproduction (same local-only PG/OpenSSL environment as the parity docs):

```bash
cargo test -p archive-server --locked --test upload_binding --test auth_binding \
  --test passkey_api -- --ignored
```
