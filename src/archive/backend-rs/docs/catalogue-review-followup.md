# #223 independent review remediation

**Pending rereview, not accepted or merge-authorized.** Addresses both findings
from `/tmp/opencode/liedertafel-218-catalogue-review.md`, which reviewed production
`dc3357e` and tooling `d3d556c`. Work remains on `ticket/223-rust-catalogue`, based
on latest local integration `e0f716d`; integrating that ref into the ticket was
already up to date. No ticket-to-integration/main merge or #224 work is implied.

## Medium: terminal telemetry gate and drain boundary

The independent late-event control was reproduced separately from the old
uncaptured member-marker failure. Five actual subprocesses first went **red**:
after a clean assertion, later stdout, stderr, valid OTLP disclosure, malformed
OTLP or a safe-but-unchecked request left the original process at **exit 0**.
This is an independently established tooling hole, **not an explanation of the
original member failure**, which remains **unclassified (no original wire)**.

`telemetry-capture.cjs` now:

- Watches child **close**, including inherited stdout/stderr, with explicit
  expected command exit codes. Unexpected child failures stay failures.
- Tracks HTTP2 servers, requests and sessions. `drain()` stops new admission,
  gracefully closes sessions, then waits for every request/stream/session close
  event. Tests caught that the server close callback can precede those final
  close events; merely awaiting that callback was insufficient.
- Makes all three native auth/member/catalogue harnesses drain before the final
  secrecy assertion, required safe telemetry-presence checks and PASS.
- Synchronously rescans at terminal exit. New unchecked events, final findings,
  protocol/lifecycle errors or an existing nonzero status produce an effective
  failed completion, persisted in **both** the private final report and terminal
  replay event. There is no async work in `exit`. Catching an assertion or calling
  `process.exit(0)` cannot hide an unclean/unchecked capture.
  A further subprocess red test caught synchronous capture data from an exit
  listener registered later than the recorder. Such appends now replace only
  this capture's own prior terminal record and synchronously persist the failed
  verdict, rather than leaving stale successful evidence on disk.

89 field-level checks plus **21 actual subprocess/lifecycle controls** pass. The
two prior in-process dirty-capture tests were moved to subprocess seams, not
deleted in substance: every protocol service still rejects genuine email/code/
cookie/credential payloads, and private permissions/replay/parser-redaction checks
remain. New controls prove late stdout/stderr/OTLP/malformed data, stale checks,
trailing inherited pipes, in-flight HTTP2 shutdown, nonzero/expected command
exits, caught findings, effective failed final reports and legitimate clean
shutdown. Scanner schema, metadata distinctions, forbidden markers and native
presence assertions were not weakened.

Tight loop (external `protobufjs@8.8.1` per
[`tests/telemetry/README.md`](../tests/telemetry/README.md)):

```bash
node --test tests/browser/telemetry-*.test.cjs
# Lifecycle only, no DB/browser/provider:
node --test tests/browser/telemetry-lifecycle.test.cjs
```

Red/green evidence: `/tmp/opencode/223-review-{terminal,pipe,collector}-{red,green}.log`.
The later exit-listener red case is `223-review-exit-callback-red.log` there.
The terminal red run had **5 failures / 2 passes**; the combined final run has
**110 passed, 0 failed/skipped** in under four seconds. All raw control/native
replays are private local files, never committed or published.

## Low: case-insensitive scalar and repeated catalogue query binding

Only production `crates/archive-server/src/catalogue/mod.rs` changes: query key
comparisons are ASCII case-insensitive for Q/BESETZUNG/ANLASS/DATEITYP/PAGE and
voiceConfiguration/occasion aliases. Scalar handling follows **executed frozen
.NET observations**, not a guessed first-value or generic join rule:

- Nonempty scalar duplicates join with commas in arrival order; empty values are
  skipped during that conversion. A present all-empty primary key remains
  present and overrides its alias before trimming/cleaning.
- Repeated `dateiTyp` stays an ordered array, including empty elements; kind
  validation/deduplication and all-kinds AND remain unchanged. A comma within one
  element is not split into two kinds.
- PAGE shares the scalar conversion and .NET integer's surrounding ASCII-space
  acceptance. Invalid/empty/duplicate comma-separated integers still reject with
  the existing Rust JSON 400; page normalization/overflow rules stay unchanged.
- Auth, current membership, filters, maximum search length and no-store behavior
  are not relaxed. The regression explicitly retains anonymous 401.

`query-binding-native.cjs` starts the **actual freshly built frozen .NET 10
executable** and normal Rust binary, each with a separate migrated local PG DB.
It seeds identical catalogue fixtures, signs in via actual SMTP code/CSRF/cookies,
checks authenticated state and anonymous denial, then compares **36** request
spellings, status, returned query/page/filters/total and ordered titles. No model,
hosted provider, frontend mock, test auth handler or frozen-source edit is used.

The committed independent fixture is `tests/fixtures/catalogue-query-binding.json`.
Its .NET source tree is `895c9885aec1db0b8d2bc8150037b1c8d0e37ea9`. The ignored
`lied_api::mixed_case_scalar_duplicates_aliases_and_repeated_kinds_match_frozen_dotnet`
test first went red against that fixture on real authenticated HTTP/PG, then
green after the production parser change. The live differential additionally
re-executes .NET and checks the stored observations before comparing Rust.

### Oracle environment qualification

The first actual frozen **Development** run established the scalar/array rules,
but also returned **500** for invalid integer bindings: Development makes ASP.NET
throw, and the frozen generic exception handler maps that to 500. That observation
is retained at `/tmp/opencode/223-review-query-oracle.json`; it is not hidden or
claimed to equal Rust's existing 400 contract.

The final full differential uses a local **Testing** .NET host: the unchanged
non-Development binder returns 400 instead of throwing. It uses the frozen
ephemeral-key test setting, explicit localhost RP/origin, SMTP (never Production
mail) and forwarded HTTPS for normal secure cookie/antiforgery policy. Seed and
migration commands remain explicitly Development. This neither changes frozen
code nor relaxes code verification/CSRF. The same 36 observations were captured
at `223-review-query-oracle-testing.json`, generated into the committed fixture,
and freshly reproduced in the final live comparison. No claim of identical
invalid-query status across .NET environments is made.

From `backend-rs`, with local `ARCHIVE_TEST_POSTGRES`, external Node dependencies,
normal `ARCHIVE_BINARY`, unchanged `ARCHIVE_STATIC_ROOT`, and a built frozen
`../backend/bin/Debug/net10.0/Archive.Backend.dll`:

```bash
dotnet build ../backend/Archive.Backend.csproj --no-restore
node tests/browser/query-binding-native.cjs
cargo test -p archive-server --locked --test lied_api \
  mixed_case_scalar_duplicates_aliases_and_repeated_kinds_match_frozen_dotnet -- --ignored
# Explicit oracle maintenance only; never generated from Rust:
ARCHIVE_QUERY_ORACLE=/tmp/opencode/query-oracle.json \
  node tests/browser/query-binding-native.cjs --oracle-only
```

## Fresh final verification (2026-11-09)

All Rust Cargo builds/tests used static **OpenSSL 3.5.9**, `--locked`, resource-only
`CARGO_INCREMENTAL=0 CARGO_PROFILE_DEV_DEBUG=0` and `-j 2`. fmt, clippy, ordinary
workspace and active audit ran sequentially, followed by detector and explicit
heavy gates; no earlier heavy-gate result is substituted for this round.

| Fresh gate | Result |
| --- | --- |
| fmt; all-target/workspace clippy `-D warnings` | Pass |
| Ordinary workspace | **54 passed, 0 failed, 77 explicitly ignored** |
| Active cargo-deny 0.20.2, unchanged policy | Exit **0**, advisories/bans/licenses/sources pass |
| Detector + actual lifecycle subprocesses | **110 passed**, no failures/skips |
| Explicit inherited auth/member + catalogue/search/tier real-PG gates | **73 passed, 0 ignored** (previous 72 plus the new binding matrix) |
| Explicit migration/schema/canonical runtime grants | **1 passed** |
| Private Azurite 3.35.0 provider contracts | **3 passed, 0 ignored** |
| Default-feature workspace build and native linkage | Pass; embedded 3.5.9, no host 3.6.5 or dynamic libssl/libcrypto |
| Live frozen .NET vs Rust query differential | **36 cases pass**, real login/CSRF/PG; fresh .NET reproduces the independent fixture |
| Native auth/replica/legacy signature/restart/SMTP/OTLP | Pass; **50 requests / 74 markers**, checked clean final report and exit 0 |
| Native member UI/CLI/passkey/restart/SMTP/OTLP | Pass; **81 requests / 29 markers**, checked clean final report and exit 0 |
| Native catalogue/Noten/private Blob/tier/restart/OTLP | Pass; **43 requests / 19 markers**, checked clean final report and exit 0 |
| Private replay of all four final native captures | Pass; final findings/errors reproduce, effective terminal code 0 |
| Frozen selected C# catalogue/search/storage/access/maintenance suites | **62 passed, 0 skipped**; existing NuGet/compiler warnings unchanged |
| Unmodified frozen Playwright catalogue/Noten/Übe/member/passkey suites | **28 executed: 26 mocked UI passes, 2 known real-backend failures, 0 skipped**, exit 1, not a passing browser gate |

The frozen browser failures are still member line **582** (202 expected, 400
actual) and passkey line **102** (stale text locator). Earlier actual frozen
.NET baseline evidence remains separate: passkey also failed at line 102,
member earlier at line **513**, not an identical-assertion claim. Those .NET
browser-baseline runs were **not repeated** here; the 62 C# tests and new native
.NET query oracle were. The unchanged static frontend export was reused.

Fresh logs: `/tmp/opencode/223-review-{fmt,clippy,workspace,deny,detector,pg,
migration,storage,build,query-differential,native-auth,members-native,
catalogue-native,dotnet-build,dotnet-oracles,frozen-browser,linkage}.log`.
Binding red/green: `223-review-query-binding-{red,green}.log` there. Final private
captures (0700 dirs, 0600 replay/reports; contain synthetic credentials):

- `/tmp/opencode/223-native-auth-telemetry-Umn7sR/`
- `/tmp/opencode/223-members-native-telemetry-yBmmRL/`
- `/tmp/opencode/223-catalogue-native-telemetry-9y4VGz/`
- `/tmp/opencode/223-query-binding-telemetry-EBsPjS/`

Frozen C#/tests/frontend, infrastructure, workflow, Dockerfile, migrations,
baseline, grants and dependency policy remain untouched. No hosted identity or
storage, production data, AI, real mail, push, issue/PR edit, cutover, deployment,
or integration/main acceptance merge occurred. **Rereview is still required.**
