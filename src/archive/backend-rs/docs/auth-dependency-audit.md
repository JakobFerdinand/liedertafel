# #221 dependency audit follow-up (2026-11-09)

This is evidence and a **policy proposal, not acceptance or license approval**.
`deny.toml` remains unchanged. No advisory ignores, fork/patch, custom ceremony
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
  `allow_backup_eligible_upgrade(false)`. Defaults keep cross-origin/subdomain/
  port relaxation disabled. Counter zero/zero remains valid; nonzero
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
| Core 0.5.5, real **unchanged** policy | Exit **4**: **advisories/bans/sources pass**, licenses fail |
| Core 0.5.5, explicitly selected **review-only proposal** below | Exit **0**, all four checks pass; duplicate-version warnings remain |

`cargo metadata --locked` confirms **rsa and crypto-glue are absent**, not merely
unused or hidden by an ignore. The 0.6.2 → 0.5.5 version-only check first failed
compilation at the changed upstream byte-wrapper/single-credential APIs; those
adapter calls were then changed using the versioned source above. Existing HTTP,
native/replica/OTLP and real-browser assertions are retained, not weakened.

## Exact license failures and smallest proposed policy change

The active Linux x86_64/aarch64 audit rejects these **11 package versions**.
Declared SPDX expressions are preserved in
[`auth-dependency-licenses.json`](auth-dependency-licenses.json), derived from
the actual locked Cargo metadata and matched to the cargo-deny failures:

| Package | Required additional license |
| --- | --- |
| `aws-lc-rs 1.19.0`, `aws-lc-sys 0.47.0` | ISC |
| `ring 0.17.14`, `rustls-webpki 0.103.16`, `untrusted 0.9.0` | ISC |
| `foldhash 0.2.0` (existing SQLx graph) | Zlib |
| `quoted_printable 0.5.2` (lettre) | 0BSD |
| `base64urlsafedata 0.5.5` | **MPL-2.0, approval required** |
| `webauthn-attestation-ca 0.5.5` | **MPL-2.0, approval required** |
| `webauthn-rs-core 0.5.5` | **MPL-2.0, approval required** |
| `webauthn-rs-proto 0.5.5` | **MPL-2.0, approval required** |

Recommendation: add just the **three permissive entries** `0BSD`, `ISC`, `Zlib`
with named-crate comments, then (only after explicit maintainer approval) add
**four exact name+version MPL exceptions**, never a global MPL/copyleft allow.
`MIT-0` is not necessary: AWS-LC's alternative clauses can use already-allowed
licenses once ISC is allowed. Platform-only crates outside the configured Linux
graph are not silently added to the proposal. No modified MPL dependency source
is vendored; upstream license/notice/source-availability obligations still need
the maintainer's review, not an agent's legal assurance.

[`auth-license-policy.proposed.toml`](auth-license-policy.proposed.toml) is a full,
valid **non-active review artifact** so the orchestrator can evaluate that exact
small change without accidentally changing the real policy:

```bash
cargo deny --locked --config docs/auth-license-policy.proposed.toml check
```

That conditional green run is **not** an approved-policy pass. The default audit
and #221 acceptance remain blocked on license review; #222 has not been started.
