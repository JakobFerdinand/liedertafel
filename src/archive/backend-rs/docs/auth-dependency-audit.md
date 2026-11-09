# #221 dependency audit follow-up (2026-11-09)

The maintainer explicitly approved the narrow license policy on **2026-11-09**;
it is now applied to the **active `deny.toml`**, retaining upstream notices and
license/source-availability obligations. General MPL/copyleft remains unallowed.
This license approval is **not a security waiver, merge or cutover approval**.
No advisory ignores, fork/patch, custom ceremony
or signature verifier, credential deletion, counter reset or re-enrollment was
introduced. Compatibility is for the actually inventoried **ES256** key, not all
algorithms .NET could theoretically enroll. The separately authorized
pre-cutover aggregate recheck in `auth-parity.md` remains mandatory.

## Source, maintenance and policy check before changing versions

Context7 (`find-docs`) was queried first; its latest-only snippets were not used
to guess 0.5 APIs. Published source archives were verified against crates.io's
SHA256 and inspected directly. All four 0.5.5 WebAuthn-family archives have VCS
commit `d2c10d53ca5ef033d37ee6462e936e9eb72ad98c`, published 2026-04-30,
MSRV 1.88, not yanked. Core archive SHA256:
`296d2d501feb715d80b8e186fb88bab1073bca17f460303a1013d17b673bea6a`.

- Upstream has a [`v5-maint` branch](https://github.com/kanidm/webauthn-rs/tree/v5-maint),
  inspected at `be696b79800bd1953df78e87d0215571733cc26f` (2026-06-02).
  There is no published 0.5.6 or 0.5.5 release-tag/changelog to cite. Release
  source/commit history is the version-change record; the 0.5.5 release commit
  includes the origin dot-boundary fix. Upstream's published
  [GHSA-22w3-693w-x895](https://github.com/kanidm/webauthn-rs/security/advisories/GHSA-22w3-693w-x895)
  explicitly identifies **0.5.5** as patched, not 0.5.4 or earlier.
- [`SECURITY.md`](https://github.com/kanidm/webauthn-rs/blob/be696b79800bd1953df78e87d0215571733cc26f/SECURITY.md)
  says **typically only the most recent release and main HEAD are supported**.
  A maintained branch and its recent security release are evidence, **not an
  explicit 0.5 LTS/security-backport promise**. No maintainer was contacted and
  no guarantee beyond that policy is claimed; retain this qualification in
  dependency review and watch upstream security releases. The project README
  reports a SUSE security audit, not an audit of this archive's expert adapter.
- [0.5.5 `crypto.rs`](https://docs.rs/crate/webauthn-rs-core/0.5.5/source/src/crypto.rs)
  imports the original COSE key with upstream parsing and verifies ES256 through
  OpenSSL SHA256 `Verifier::verify_oneshot`. It does not implement all .NET
  algorithms; the earlier hypothetical-all-nine exclusion was not an ES256
  blocker. No direct OpenSSL verification was added in archive code.
- [0.5.5 `core.rs`](https://docs.rs/crate/webauthn-rs-core/0.5.5/source/src/core.rs)
  supports the existing byte-handle registration builder, required resident key
  and required UV, `none` attestation, exact origin policy, strict counters and
  `allow_backup_eligible_upgrade(false)`. Exact origin/subdomain/port policy
  remains enforced. **CrossOrigin is checked for registration only** in 0.5.5,
  not assertions; the frozen custom C# origin callback also ignores that field
  on assertion. Existing `crossOrigin:false` fixtures do not prove its rejection,
  and no new assertion-side policy was introduced. Counter zero/zero remains valid; nonzero
  non-increase is rejected upstream. Both BE transitions remain rejected.
- The only behavioral API adaptation is the documented resident-login hook
  `AuthenticationState::set_allowed_credentials`: after credential-ID lookup
  and the **same** owner/36-byte Guid-text handle check, insert that one imported
  credential into the **server-only** state and call upstream
  `authenticate_credential`. Challenge, UV and strict-BE state is unchanged.
  Remaining edits convert upstream byte wrappers at SQL/COSE encoding boundaries.
  Identity JSON, session/CSRF storage, transaction ordering and tests are unchanged.

## Native OpenSSL is a separate dependency

Upstream's [OpenSSL policy](https://github.com/kanidm/webauthn-rs/blob/be696b79800bd1953df78e87d0215571733cc26f/OpenSSL.md)
requires supported OpenSSL v3, with more than a year of public bugfix support.
The host initially supplied **3.6.5**. Although the initial functional tests
passed, [OpenSSL's current downloads](https://openssl-library.org/source/) and
[release strategy](https://openssl-library.org/policies/releasestrat/) no longer
list that series as supported; those initial runs are not native-support proof.

Final gates instead use an **unmodified official OpenSSL 3.5.9 LTS**, supported
until 2030-04-08, isolated outside the checkout/system install. Official release:
<https://github.com/openssl/openssl/releases/tag/openssl-3.5.9>. Archive SHA256:
`603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a`.
Native OpenSSL 3.5.9 is Apache-2.0 (already allowed); Cargo resolves `openssl
0.10.82`, `openssl-sys 0.9.118`, `openssl-macros 0.1.1`.

Local build commands, after checking that official digest:

```bash
# From the extracted official openssl-3.5.9 source; no upstream source edits.
perl Configure --prefix=/tmp/opencode/221-openssl-359/install \
  --openssldir=/tmp/opencode/221-openssl-359/install/ssl no-shared no-tests
make -j 4
make install_sw
# Scope these to EVERY subsequent Cargo build/clippy/test command:
OPENSSL_DIR=/tmp/opencode/221-openssl-359/install OPENSSL_STATIC=1 \
  cargo test --workspace --locked
```

The native library's own test suite is not claimed (`no-tests` above); the
archive's full positive/negative ceremony gates exercise the linked verifier.
Future image/build owners must supply an upstream-supported native OpenSSL too;
Cargo's advisory database cannot audit system libcrypto. No Dockerfile,
infrastructure, production package or cutover configuration was changed here.

## Red-capable audit loop

Run with cargo-deny 0.20.2 and the checked-in policy, from `backend-rs`:

```bash
cargo deny --locked check
```

| Configuration | Result |
| --- | --- |
| Candidate `9450e95`, core 0.6.2, real policy | Exit **5**: RUSTSEC-2023-0071 in `rsa 0.10.0-rc.19` through crypto-glue; licenses also fail |
| Core 0.5.5 before license approval (`e603f92`), previous active policy | Exit **4**: **advisories/bans/sources pass**, licenses fail |
| Core 0.5.5, then-review-only proposal | Historical exit **0**; not counted as active-policy approval |
| Core 0.5.5, maintainer-approved **active `deny.toml`** | Exit **0**: **advisories/bans/licenses/sources pass**; no advisory waiver |

`cargo metadata --locked` confirms **rsa and crypto-glue are absent**, not merely
unused or hidden by an ignore. The 0.6.2 → 0.5.5 version-only check first failed
compilation at the changed upstream byte-wrapper/single-credential APIs; those
adapter calls were then changed using the versioned source above. Existing HTTP,
native/replica/OTLP and real-browser assertions are retained, not weakened.

## Exact license inventory and authorized active policy

Before approval, the Linux x86_64/aarch64 audit rejected these **11 package versions**.
Declared SPDX expressions are preserved in
[`auth-dependency-licenses.json`](auth-dependency-licenses.json), derived from
the actual locked Cargo metadata and matched to the cargo-deny failures:

| Package | Required additional license |
| --- | --- |
| `aws-lc-rs 1.19.0`, `aws-lc-sys 0.47.0` | ISC |
| `ring 0.17.14`, `rustls-webpki 0.103.16`, `untrusted 0.9.0` | ISC |
| `foldhash 0.2.0` (existing SQLx graph) | Zlib |
| `quoted_printable 0.5.2` (lettre) | 0BSD |
| `base64urlsafedata 0.5.5` | **MPL-2.0, approved exact-version exception** |
| `webauthn-attestation-ca 0.5.5` | **MPL-2.0, approved exact-version exception** |
| `webauthn-rs-core 0.5.5` | **MPL-2.0, approved exact-version exception** |
| `webauthn-rs-proto 0.5.5` | **MPL-2.0, approved exact-version exception** |

The authorized change adds just the **three permissive entries** `0BSD`, `ISC`,
`Zlib` with named-crate comments and the **four exact name+version MPL exceptions**
above. There is no global MPL/copyleft allow and no exception for another version.
`MIT-0` is not necessary: AWS-LC's alternative clauses can use already-allowed
licenses once ISC is allowed. Platform-only crates outside the configured Linux
graph are not added to the policy. No modified MPL dependency source is vendored;
upstream license, copyright and notice text is not removed or changed. Retain
those notices and the source-availability obligations in any later distribution;
the dependency inventory remains recorded rather than replaced by an agent's
legal assurance.

The agent-authored whole-config proposal was removed once applied: **`deny.toml`
is the only active policy**, avoiding a second drifting copy. Its earlier form
and conditional audit are retained in commit `e603f92` as historical evidence.
The actual default audit command is:

```bash
cargo deny --locked check
```

The current active audit and config/docs-only verification results are recorded
below. #222 has not been started; readiness for the merger does not itself merge
the ticket or approve deployment. Native OpenSSL 3.5.9 and the honest upstream
v5 support qualification above remain requirements, not waived by license approval.

## Active-policy verification after explicit approval (2026-11-09)

Merged the current integration tip first (`0ff95e6`, already up to date). Ran
upstream cargo-deny **0.20.2** from `src/archive/backend-rs` with **no `--config`
override**, using the actual active `deny.toml`:

```text
$ cargo deny --locked check
advisories ok, bans ok, licenses ok, sources ok
exit: 0
```

Seventeen duplicate-version warnings remain under the unchanged
`multiple-versions = "warn"` policy. No errors, advisory ignores or security
waivers were added. A TOML scope check also confirmed exactly the three
permissive additions and four `@0.5.5` MPL exceptions, MPL absent from global
`allow`, and graph/advisory/bans policy unchanged.

For this **config/docs-only** change, sequential format → clippy → workspace
tests were rerun and passed:

```bash
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -j 2 -- -D warnings
cargo test --workspace --locked -j 2
```

Workspace result: **31 passed, 0 failed, 21 explicitly ignored**. Clippy/tests
used the same supported native build with
`OPENSSL_DIR=/tmp/opencode/221-openssl-359/install OPENSSL_STATIC=1`, plus
`CARGO_INCREMENTAL=0 CARGO_PROFILE_DEV_DEBUG=0` for build resources.
The heavy explicit PostgreSQL/browser/native/.NET functional gates were **not
rerun** for this policy-only change; their passing evidence remains the
`e603f92` verifier/backend revalidation in `auth-parity.md`. The frozen Playwright
selector stays a proven failure on both .NET and Rust, not a passing test.

Local full outputs are `/tmp/opencode/221-deny-active-approved.log`,
`/tmp/opencode/221-clippy-license-approved.log`, and
`/tmp/opencode/221-workspace-license-approved.log`. The license/audit blocker is
resolved; at the license-only checkpoint the ticket was ready for the merger,
not merged, deployed or marked accepted. The subsequent Medium allocation-review
fix and fresh functional evidence are in `auth-parity.md`; #221 is unaccepted
pending review of that fix and #222 remains blocked. No later-wave work or
production access was performed.
