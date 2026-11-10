# AI foundation (#228)

## Current review status — narrow R4 fix awaiting ledger clearance

The independent transport rereview is **ACCEPTABLE at `3c7db5d`**, with T1–T4
closed. The independent ledger rereview resolved R1–R3 and found one new **Low
R4** in configuration decimal precision, also present in the original candidate.
The earlier green tests remain valid for their covered inputs; this requires an
unusual greater-than28-scale override, not a shipped-price/cap bypass.

`6932b33` fixes only the options converter and adds actual configuration
oracles/regressions. It implements the reached invariant DecimalConverter
`NumberStyles.Float` grammar after Unicode Trim, including exponent/sign rules
and direct ties-to-even precision reduction at the96-bit/28-scale boundary.
It retains scale/signed zero, handles huge exponents without exponent-sized
allocation, and rejects underscores/grouping/trailing signs. It does **not**
reuse the notice's `NumberStyles.Number` grammar or Rust Decimal FromStr.

The two original R4 repros are now committed actual ConfigurationBuilder
observations: input`0.50000000000000000000000000005` + output`0.5` costs **1**, not
2 micro-EUR for1/1 tokens; input`0.00000000000000000000000000005` rounds to zero
and the model is unpriced. The real Options/gateway/PostgreSQL test asserts
that underflow returns Unavailable with **zero provider calls, zero usage rows
and zero month rows**. The ordinary actual-binder matrix has **288 observations
(96 spellings × cap/input/output)**, comparing configured values, scale, sign
(including signed zero), usable price and bounded cap conversion. It covers
positive/negative exact/sticky ties, exponents/underflow/overflow, max96-bit,
leading/trailing whitespace, plus/minus and invalid grammar. Maximum parsed
cap values still fail the pre-existing checked i64 micro-EUR bound; conversion
success is not a waiver of that separate configuration guard.

Executed behavioral red→green logs: `/tmp/opencode/228-r4-{red,green}.log` and
`228-r4-underflow-red.log`; focused fmt/clippy and35 ledger/configuration/notice
tests passed in `228-r4-focused-{fmt,clippy,green}.log`. The original reviewer
demonstrations remain independently owned at
`228-ledger-rereview-decimal-probe.rs` and
`228-ledger-rereview-decimal-{tie,zero}-{rust,dotnet}.log`, as documented in
`/tmp/opencode/liedertafel-218-ai-ledger-review.md`. They are not claimed as new
independent implementation evidence.

The full fresh final pipeline completed after the last code fix, against
runtime/test source `6932b33f119b39699cc928aee1a980e02c924650`. The last handoff
commit records documentation only. The parent must obtain independent ledger R4
clearance and verify the narrow diff
from transport-reviewed `3c7db5d` to the final candidate; no duplicate independent
transport review is claimed or required by this handoff. No merge authority is
granted. #229/#230/#231 remain blocked until parent-serialized acceptance.

### Final R4 validation (2026-11-10)

Fresh sequential order: fmt → all-target clippy → workspace tests → workspace
build and examples build → active pinned cargo-deny → static linkage → ALL
explicit gates → nine native journeys with nine drained private replays.
Dependencies remained locked; dedicated target/ticket-228, local PostgreSQL55428
and private Azurite51029/52029/53029 were retained.

| Gate | Final R4 result |
| --- | --- |
| fmt, all-target clippy `-D warnings`, workspace and examples builds | **PASS** |
| Ordinary Rust workspace | **103 passed / 0 failed / 204 ignored**; the new matrix is one test with288 isolated cases |
| ALL combined explicit gates | **204 passed / 0 failed / 0 ignored**: 196 SQL, 1 schema/adoption/grants, 5 private Blob, 1 private Queue, 1 native PDF; configuration suite now exercises23 real gateway/PG children |
| Pinned cargo-deny0.20.2 | **PASS**: advisories/bans/licenses/sources; existing duplicate-version warnings retained |
| Native auth/member/catalogue/upload/events/query/extraction/recordings/AI | **9/9 PASS**; unchanged strengthened AI prefix-error/cancel/cap/failed-settlement/checked-flush controls |
| Drained private telemetry replays | **9/9 PASS**; completion/secrecy checked, findings/errors/legacyMatches empty, captures0700/replays0600 |
| Detector/lifecycle | **110 passed / 0 failed / 0 skipped** |
| Unchanged frozen .NET AI/ledger/provider/notices/infra | **42 passed**, including all three actually executed PostgreSQL concurrency cases |
| Unchanged frontend unit tests | **115 passed / 0 failed / 0 skipped** |
| Unchanged mocked desktop/mobile UI | **184 passed**; static/mock regression tests, not native chat/hosted acceptance |
| Native linkage | Embedded **OpenSSL3.5.9**, no dynamic libssl/libcrypto |

Final logs: `/tmp/opencode/228-r4-final-{fmt,clippy,workspace,build,deny,linkage,
explicit,native-summary,detector,frozen-dotnet-sql,frontend-unit,frozen-browser}.log`
and per-native `228-r4-final-<name>.log`/`-replay.log`.
`228-r4-final-native-results.json` pins the nine private captures;
`228-r4-final-evidence.json` records verified counts, source pin, binary hash and
clean privacy completions. Private replays remain local. The final handoff report
`/tmp/opencode/liedertafel-218-228-report.md` pins the documentation commit and
exact `3c7db5d → final SHA` six-file diff: options converter, one configuration
test, its oracle, two configuration fixtures and this parity guide only.
No gateway/ledger SQL/ACK, transport/encoder, identity, notice/threshold, native
helper, frozen source/test/frontend, infrastructure, workflow or Dockerfile change
is included. Original candidate/accepted base/transport-reviewed ancestry and
`git diff --check` passed. Existing source limitations/failures and pending hosted/
image/AppHost/cutover gates remain unwaived; no cleanup or merge was performed.

### Original seven findings — independently closed at `3c7db5d`

All seven had behavioral red→green controls, actual frozen oracles and subsequent
independent closures. Their mappings and source qualifications remain unchanged:

| Finding | Fix / direct evidence |
| --- | --- |
| Ledger R1 Medium | `7d37e66` + `5699e59`: AI-only case-insensitive section/setting/price-field ENV binding, including standard `__`→`:` normalization.21 actual .NET ConfigurationBuilder process observations and21 fresh real-gateway/SQL child cases cover zero/lower cap, token limits, prices, mixed deployment identity, unknown/unusable models. Actual invalid underscore cap/price lexemes are also rejected rather than accepted by Rust Decimal's extended syntax. Rust retains its existing stricter invalid-price configuration error; frozen dictionary binding instead drops the unusable model. Both refuse before provider/rows; neither restores a shipped price. |
| Ledger R2 Low | `1c141f1`: one invariant notice formatter explicitly rounds midpoint away from zero; warnings and breakdown both use it.13 actual BCL positive/negative/tie/zero observations plus captured warning checks. HTTP four-place/micro-EUR totals remain separately asserted and unchanged. |
| Ledger R3 Low | `6a0d868`: actual invariant NumberStyles.Number threshold-entry grammar after comma split/Unicode trim, including trailing signs, numeric whitespace/NUL handling and ties-to-even decimal precision reduction.208 actual BCL spelling/tokenization/precision observations check levels and invalid-entry logging. Cap inclusion/positive filtering stay unchanged. |
| Transport T1 Medium | `22cf552`: queued complete SSE events drain before a stored terminal error, independent of coalesced/split/partial-UTF8 delivery. Actual frozen valid-prefix→JsonReaderException fixture; tools, no-report full cost and consumer-break-before-later-usage controls. Gateway consumer acknowledgements are unchanged. |
| Transport T2 Low | `a6e468c`: supported `Value` helper reproduces frozen BMP/non-BMP escaping, German letters still literal; raw `Payload::json` unchanged. Actual serializer/argument/result/schema+UTF16 fixtures and bound23 refusal of the frozen27-token value. Encoder BMP range facts are derived from an exhaustive frozen BCL scalar probe, not an invented escape-all-nonASCII rule. |
| Transport T3 Low | `444bd8d`: present total_tokens must also be Int32, even though pricing uses only input/output. Actual SDK nonstream/SSE valid/missing/empty/null/malformed/overflow/prefix fixtures; top-level usage:null stays unreported. |
| Transport T4 Low | `dd46b16`: one AI ENV snapshot is trimmed and passed into the approved credential factory. Local refusing App Service query tests cover whitespace/canonical/unset/blank/lazy construction; PATH contains only refusing fake az/azd, so no real credential/token can be obtained. Shared Blob/Queue selection and normalization policy is unchanged. |

### Prior remediation validation at `3c7db5d` (historical)

That combined validation completed against runtime/test source
`96c09fce29cb3abbc3d76edcfa7bfb3e15b0dbc3`; the final commit records documentation
only. Those seven fixes have since been independently closed as stated above;
the new ledger R4 and narrow parent diff verification remain separate.

The fresh sequential pipeline was fmt → all-target clippy → workspace tests →
workspace/examples build → pinned cargo-deny → ALL explicit gates → nine native
journeys and nine drained replays. Dependencies stayed locked, with dedicated
`target/ticket-228`, local PostgreSQL55428 and private Azurite51029/52029/53029.

| Gate | Final result |
| --- | --- |
| fmt, all-target clippy `-D warnings`, workspace/examples build | **PASS** |
| Ordinary Rust workspace | **102 passed / 0 failed / 204 ignored**; ignored tests are counted only in the next row |
| ALL combined explicit gates | **204 passed / 0 failed / 0 ignored**: 196 SQL, 1 schema/adoption/grants, 5 private Blob, 1 private Queue, 1 native PDF |
| Pinned cargo-deny0.20.2 | **PASS**: advisories, bans, licenses and sources; existing duplicate-version warnings retained |
| Native auth/member/catalogue/upload/events/query/extraction/recordings/AI | **9/9 PASS**, including extended AI prefix-error/SQL settlement control |
| Drained private telemetry replays | **9/9 PASS**; findings/errors/legacyMatches empty; completion and secrecy checked; captures0700/replays0600 |
| Official-schema detector/lifecycle | **110 passed / 0 failed / 0 skipped** |
| Unchanged frozen AI/ledger/provider/notices/infra .NET suites | **42 passed**, including actual execution of all three PostgreSQL concurrency cases |
| Unchanged frontend unit tests | **115 passed / 0 failed / 0 skipped** |
| Unchanged mocked UI, desktop/mobile | **184 passed**; static/mock regressions, not native chat or hosted AI acceptance |
| Native linkage | Embedded **OpenSSL3.5.9**, no dynamic libssl/libcrypto |

Fresh evidence: `/tmp/opencode/228-remediation-{fmt,clippy,workspace,build,deny,
explicit,detector,frozen-dotnet-sql,frontend-unit,frozen-browser,linkage,
native-summary}.log`, per-native `228-remediation-<name>.log`/`-replay.log`, and
`228-remediation-{native-results,evidence}.json`. All seven focused behavioral
red→green logs use `228-{r1,r2,r3,t1,t2,t3,t4}-{red,green}.log`; the additive R1
lexeme control uses `228-r1-invalid-lexeme-{red,green}.log`. The extended native
fixture has its own `228-review-native-{red,green}.log`. Original reviewer probes
are evidence from their original reviews, not claimed as new independent work.

Both `74ef3d0` and accepted `c7d9fd8` are verified ancestors. Frozen C#/tests,
frontend, infrastructure, workflows and Dockerfile have no diff from the accepted
base. Existing browser failures, historical UNCLASSIFIED redaction failure,
video FK refusal, PDF/native limits and hosted/image/AppHost/cutover gates remain
as recorded below and in their owning parity docs; these passes do not reclassify
them. Private captures and owned local services remain intact; no cleanup ran.

### Oracle and encoder maintenance

`AiConfigurationOracle.cs` links unchanged `AiOptions.cs` and invokes real
ConfigurationBuilder/AddEnvironmentVariables in isolated processes. It produces
`228-ai-config.jsonl` (now23 cases, including both R4 repros). Its
`decimal-matrix <unchanged-appsettings.json>` mode executes actual configuration
binding with in-memory string overrides over the unchanged JSON, producing
`228-ai-config-decimals.jsonl` (288 observations); it is not a mathematical
substitute for DecimalConverter. Rust runs every matrix case through actual
Options in an isolated ENV child. The source uses
`DecimalConverter.FromString` → `decimal.Parse(..., NumberStyles.Float, ...)`,
with `BaseNumberConverter.ConvertFrom` trimming first (runtime v10.0.0 sources,
executed BCL10.0.14):
[DecimalConverter](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.ComponentModel.TypeConverter/src/System/ComponentModel/DecimalConverter.cs),
[BaseNumberConverter](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.ComponentModel.TypeConverter/src/System/ComponentModel/BaseNumberConverter.cs).
Invalid dictionary-model binding still differs in exception
shape: frozen binding drops that unusable model; Rust fails configuration. Both
refuse before provider/ledger work, without reverting to shipped prices.
`AiNumberOracle.cs` executes the actual BCL format and
unchanged ParseThresholds into `228-ai-numbers.jsonl` (13 formats+208 thresholds).
`AiTransportReviewOracle.cs` links the unchanged provider/frozen backend and uses
fake credentials/HttpMessageHandler only, producing `228-ai-transport-review.jsonl`.
Its25 observations include10 Value payload matrices, one actual function schema,
13 wire responses and one exhaustive BMP encoder-blocklist observation. These
are additive to the original23 captured SDK observations, not live acceptance.

The encoder-blocklist observation was produced on .NET10.0.14. Its sorted346
BMP ranges (surrogate code units excluded) regenerate
`crates/archive-server/src/ai/encoder-bmp-ranges.json` exactly. Preserve that provenance on a deliberate
update; do not substitute the Rust Unicode version/categories or infer generic
JsonSerializer support. The helper's tested domain is serde_json Value formatting
with the recorded encoder; pre-serialized JSON/schema retains its raw spelling.
The native test extension in `96c09fc` adds coalesced valid usage followed by
malformed SSE over actual reqwest/real PostgreSQL and asserts5/2 tokens,15micro-EUR
at fixture prices and zero reservation after the safely exposed terminal error.
Existing consumer-cancel/cap/failing-settlement/nonzero/checked-flush controls stay.

### Mandatory later HTTP integration gate (#229–#232)

The current host awaits Axum in-flight handlers **before** gateway drain. There
is no current model-consuming HTTP endpoint and neither reviewer alleges a
demonstrated current-route shutdown failure. Before adding those handlers, their
owners MUST connect shutdown cancellation and prove that an active model request
cannot wait circularly for the post-Axum drain or merely hit the30s watchdog.
Request/host cancellation, producer join, durable accounting and finite OTLP
flush must be tested at their actual HTTP/process seam. The finite foundation
example is not proof of this future endpoint integration gate.

Base: accepted `c7d9fd8` (both #226/#227). This is foundation only: no chat
endpoint, tool executor/loop, catalogue agent, scan reader or draft reader.
Frozen C#, xUnit, frontend, infrastructure, workflows and Dockerfile are unchanged.
No hosted identity, model, Neon, private document, mail or deployment is used.
Independent review/acceptance is still required; #229/#230/#231 remain blocked.

## Boundary and ownership

Application consumers receive `ai::Gateway`. Raw `Provider` is module-private;
only the gateway owns it. `Script` supplies delivery below the real gateway,
bounds, operation attribution and ledger; it cannot perform accounting or make
HTTP requests. Static sequences and normalized-request handlers are reusable by
the later reader/chat suites. `Script::requests` records provider-visible
requests only: operation/account/agent markers are structurally absent.

`Services::from_env` is the web/future finite-host registration. Provider choice
uses the exact trimmed `Archive__Chat__Provider=AzureOpenAI`, nonblank Endpoint
and DeploymentName; otherwise the model key is `scripted`. The foundation's
unconfigured script is deliberately empty/fail-closed: domain-specific frozen
`ScriptedChatClient`/`ScriptedScanReader` answers belong to #229–#232, not an early
domain implementation here. Tests install scripts explicitly below the gateway.
Base prices contain Luna only; Development also prices scripted calls. Unknown
deployment names never borrow scripted prices.

Every call requires `Operation`: feature, UUIDv7 operation, nullable triggering
account, optional output bound and explicit non-text estimate. Account attribution
is stored in PostgreSQL, never put in provider JSON. Model options are cloned,
trimmed/case-insensitively deployment-checked, then cleared. Output precedence is
operation override → requested override → configured ceiling, clamped to 1..ceiling.
The UTF-16 estimator includes instructions, text, function names/serialized
arguments/results and raw tool schemas plus eight tokens per message. Non-text
without an estimate and oversized input refuse before reservation.

`Payload::Json` preserves raw JsonElement-like spelling/whitespace. `from_value`
provides the captured indented AIJsonUtilities dictionary/result spelling,
including escaped non-BMP surrogate pairs. Features should retain raw schemas
when already serialized instead of round-tripping them through a map.

## Accounting contract and limits

`Ledger` owns decimal prices and Vienna calendar months; `PostgresStore` owns
atomicity. Money is checked `rust_decimal` arithmetic followed by integral i64
micro-EUR conversion, never binary floating-point. EUR-per-million × token count
already is micro-EUR. Each call rounds cost upward; the monthly cap rounds down.
Floating-point conversions are confined to OTLP numeric instruments, not money.

Reservation SQL is the frozen algorithm: independent transaction, literal
`SET LOCAL lock_timeout = '5s'`, insert month key on conflict, lock month `FOR
UPDATE`, sum ledger cost+reservations, admit equality with the cap, then upsert
the `(OperationId, Model, YearMonth)` usage key. Calls/reservations increase;
existing feature/account attribution stays unchanged. There is no cached balance.

Settlement moves a reservation exactly once in normal Rust code and updates its
original row ID relatively, without the month lock. Repeated cumulative reports
inside one call take component-wise maxima; separate calls add tokens and
individually rounded costs. A non-null empty usage report is reported zero;
missing usage, a failed/abandoned/cancelled call without a report, or vanished
prices settle at full reservation. Reported usage uses current prices, not a
snapshot, and is not clipped to the reservation.

**Admission is conditional on a conservative reservation, not a universal
overshoot proof.** Higher actual input, prices raised during a call or unexpected
billed output may exceed the reserve; the explicit price-change control retains
that source behavior. No TTL refund, new balance, price snapshot or cost clipping
is introduced. Process death/unknown admission commit can leave durable open
reservations. Relative settlement retries are deliberately not an idempotency
mechanism: committed-but-unacknowledged writes could apply twice. That fault has
not been established safe; success/contention tests are not such a proof.

## Cancellation and finite hosts

`Gateway::start` synchronously gives work to an owned `TaskTracker`. A bounded
one-slot delivery channel with consumption acknowledgements means unread queued
usage is not accidentally treated as observed. Dropping/cancelling a `Call`
stops provider delivery; the tracked producer still awaits settlement. Admission
while waiting on a month lock is cancellable; dropping the SQLx transaction
rolls it back, subject to the same unknown-commit qualification above.

`Call::response` and `finish` await terminal accounting. On consumer break/drop,
the host owns completion through `Gateway::drain`, which cancels remaining
producers and waits for their destructors/accounting. Settlement ignores request
cancellation, retries at 100/400/1600ms (four attempts), stops immediately on a
missing row, logs safely, and retains failed reservations. A drain reports failed
accounting to finite hosts; the web host includes drain in its existing shutdown
watchdog before telemetry shutdown. Future finite hosts must call the same drain
before `finish_finite_job`; they must not drop a gateway then claim a clean flush.
Each store settlement attempt has a five-second timeout so four failed attempts
and delays finish within the accepted 30s host budget (22.1s worst case). This is
an explicit Rust finite-lifecycle bound, not a claim that the .NET command timeout
was five seconds. A timed-out/unknown write receives the same conservative failure
handling and the same non-idempotent retry qualification. A paused-clock stalled-
store control checks all four attempts and the retained reservation.

## Hand-written wire contract

Locked reqwest 0.13.5, azure_identity 1.0.0/azure_core 1.1.0. API docs were checked
with find-docs and versioned docs.rs, not a newer SDK API. No OpenAI or Agent SDK
is added. Credential construction reuses the accepted storage identity helper:
configured `AZURE_CLIENT_ID` selects the scoped managed identity, otherwise the
existing developer-tools credential. Both construction and token acquisition are
lazy; simply configuring a client does not request a secret/token.

The real frozen Azure.AI.OpenAI 2.1.0 path was run **offline**, with a fake
TokenCredential and HttpMessageHandler, including its production retry policy.
`tests/oracles/AiWireOracle.cs` is linked with the unchanged frozen provider file
in external `/tmp/opencode/228-oracle/Oracle.csproj`, referencing the frozen
backend project. Its sanitized observations are `tests/fixtures/228-ai-wire.jsonl`.
This project never opens a provider socket. NuGet vulnerability/compiler warnings
from the frozen oracle remain warnings, not a clean dependency audit claim.

Captured request facts:

- `/openai/deployments/<configured>/chat/completions?api-version=2024-10-21`;
  bearer scope `https://cognitiveservices.azure.com/.default`.
- `model` is the configured deployment; `max_completion_tokens` is the normalized
  bound. Default `reasoning_effort` is `none`, explicit efforts are preserved.
- Instructions become a system message. Tools use function/name/description/
  parameters and `tool_choice:auto`; assistant tool calls and matching tool
  results retain their captured serialized argument/result strings. JSON-schema
  output uses `response_format.json_schema`, without an invented `strict:true`.
- Streaming requests include `stream_options.include_usage:true`. Final
  usage-only chunks with empty choices precede `[DONE]`; a finish reason alone
  never terminates reading. Fragmented UTF-8 and CR/LF SSE are decoded only at
  complete event boundaries. Wire/tool assembly has bounded buffers; indexed
  argument fragments stay data, not an executor or schema-validation bypass.
- A `content_filter` finish still returns a response/usage in the actual frozen
  SDK. Rust preserves that finish reason rather than turning it into a full-
  reservation transport failure. Actual HTTP/provider errors remain redacted.
- Missing usage fields in a non-null object are zero, but present null/string/
  overflowing Int32 counts are malformed (as captured from the frozen SDK), not
  free reported-zero calls. Those failures retain the full-reservation charge.
- Frozen production status retries are initial+3 for 408/429/500/502/503/504;
  409/400 and malformed successful JSON are not retried. These physical attempts
  share one reservation, like the SDK, NOT an added unbudgeted feature retry.
  Reqwest automatic protocol retries and redirects are disabled. There is no
  retry after body/stream consumption. Retry-count observations do not establish
  provider acceptance or safe billing after an ambiguous network failure.

**Live acceptance is UNPROVEN.** The frozen dated API may reject
`reasoning_effort` (documented as added in 2024-12-01-preview). No automatic
Responses/v1/API/model upgrade or price-tier change is authorized here.

## Inspection, notices and dormant embedding guard

Editor-only `GET /api/maintenance/ai-budget` uses the shared real auth boundary,
stays available during maintenance, and reads state without a reservation,
provider call or notice watermark write. Decimal JSON numbers preserve the
four-place top-level spent rounding and six-place feature totals.

`Notices` reads current-month grouped ledger rows and sorted explicit positive
thresholds plus the cap (even with an empty list). Invalid entries log an error;
the highest reached unnotified level logs its invariant breakdown, then advances
the monotonic SQL watermark. This is sequential-notice parity, **not** cross-
replica exactly-once logging. #229 owns invoking it from dispatch-reading outside
maintenance. There is no new mail/alert destination or paid notice generation.

`dormant_embedding_call` retains the inert UTF-16/input-only guard and its offline
missing-operation/cap/refusal/7-token-accounting sibling. No embedding provider,
vector registration, endpoint or semantic search exists. This is preservation of
the dormant test contract, not an embedding-feature waiver.

## Frozen-suite ownership map

Frozen production files map as follows: `AiGateway.cs` → `ai/mod.rs` and
`gateway.rs`; `BudgetedChatClient.cs` → gateway normalization/owned delivery plus
`model.rs` estimates/inert guard; `AiOperation.cs` → `model.rs`; `AiBudgetLedger.cs`
and `IAiBudget.cs` → `ledger.rs`; `AiLedgerStore.cs` → its `PostgresStore`;
`AiOptions.cs` → `options.rs`; `AiUsageEntities.cs`/`AiModelConfiguration.cs` → the
existing canonical baseline tables/keys, unchanged, plus the typed ledger rows;
`AiBudgetEndpoints.cs`/`AiCostNotice.cs` → `notice.rs`; `AzureOpenAIChatClient.cs` →
`azure.rs`; provider-script delivery → `script.rs` with domain ownership below.
No migration or canonical-table/grant change was necessary.

| Frozen file | Rust sibling / disposition |
| --- | --- |
| `AiBudgetLedgerTests.cs` | `ai_budget_math`, `ai_guards`, `ai_ledger`: exact money/month/defaults, missing/zero prices, unavailable store, in-flight/cap/equality/hints, missing reports, original-month/current-price behavior; explicit PG 24 callers, 32×10 contention, replica first-month race, row-key attribution, 5s lock and cancellation. No current SQL case waived. |
| `BudgetedChatClientTests.cs` | `ai_guards`, `ai_gateway`, `ai_wire`: required operation/deployment/input/output/options, non-text estimates, repeated-report max versus additive calls, nonstream and streaming, provider refusal, fail/drop/cancel/drain, retry/failure durability, inert embedding guard. Private provider type plus actual below-gateway captures retain the wiring guard without reflection. |
| `AzureOpenAIChatClientTests.cs` | `ai_wire`, `ai_budget_math`, offline `AiWireOracle.cs`/fixture: request/API/scope/options/default effort/schema/tools/images, usage, fragmented SSE, errors, content-filter finish and physical retry counts. |
| `AiCostNoticeTests.cs` | `ai_notices`, model-free `ai_guards`: grouped real SQL, levels/invalid entries, sequential highest/cap/raised-cap watermark, invariant breakdown; actual HTTP editor/member/anonymous/maintenance/read-only control. |
| `InfrastructureDeploymentTests.cs` | `ai_budget_math` reads unchanged Bicep literal deployment parameter/resource association and requires its shipped price. No infra deployment. |
| `InMemoryAiLedgerStore.cs` | `tests/support/ai.rs` is a pure-sibling stand-in only; explicit PG suites always use production raw SQL. |
| `Chat/ScriptedChatClient.cs`, `ReadingTestHost.cs` provider scripts | Generic reusable delivery-only `Script` is here. Domain prompt recognition/answers and reader/tool behavior stay explicitly with #229/#230/#231/#232. |
| Three live evaluations | Shared `LiveEvaluation` runtime guards retain exact required env names; missing/whitespace values are tested before provider construction. Actual document/chat evaluations remain their owning later tickets, not claimed executed here. |

The three gate vocabularies are `ARCHIVE_CHAT_ENDPOINT` +
`ARCHIVE_CHAT_DEPLOYMENT_NAME`, plus `ARCHIVE_SCAN_EVAL_DIR` or
`ARCHIVE_PROGRAMM_EVAL_DIR` respectively. Existing optional model-version,
price-override and report names remain reserved for the later suites. Configuration
is never authorization to spend. No live gate is crossed by these tests.

The frozen live-chat evaluation's raw foreign-model probe outside the gateway
is **NOT implemented or executed in foundation**. #232 must retain an explicit
test-only separately authorized disposition before release; it cannot create a
production raw-provider seam or claim every paid evaluation call is budgeted.

## Reproduction and evidence

Use a dedicated disposable PostgreSQL and scoped static OpenSSL 3.5.9; no
production credentials are needed. Ordinary offline tests:

```sh
cargo test -p archive-server --locked --test ai_guards --test ai_wire --test ai_budget_math
cargo test -p archive-server --locked --test ai_ledger --test ai_gateway --test ai_notices -- --ignored
cargo build -p archive-server --locked --example ai-foundation-native
# Set ARCHIVE_AI_PROBE, ARCHIVE_BINARY and local ARCHIVE_TEST_POSTGRES.
node tests/browser/ai-foundation-native.cjs
```

The Cargo example is an offline-only test process: it constructs its own loopback
HTTP provider/fake credential and refuses a nonlocal or non-`ai228_native_*`
database. It is not a product CLI switch. The Node harness uses the accepted
official-schema telemetry scanner, real finite process drain, private capture and
terminal fail-closed replay. Synthetic prompts/answers/tool payload/email/token/
signature markers must not appear in actual OTLP or console output.
The AI trace and meter scope is `Liedertafel.Archive.Ai`; static error statuses
cannot retain provider exception messages/stacks, and the existing global raw
SDK tracing suppression still applies even under `RUST_LOG=trace`. GenAI token
usage/duration metrics have bounded dimensions, not per-operation UUID dimensions.
The native fixture also forces actual PostgreSQL settlement writes to fail and
checks conservative reservation retention, nonzero finite exit and OTLP flush.

Final executed counts and retained failures are recorded in the handoff report;
pending gates are not passes. Existing frozen browser failures, unclassified
historical redaction failure, video-job FK refusal, cooperative PDF/native limits,
hosted acceptance and image/AppHost/cutover gates remain unchanged.

## Historical original-candidate validation (74ef3d0, 2026-11-10)

Dedicated target `target/ticket-228`, PostgreSQL55428, private Azurite Blob51029 /
Queue52029 (matching internal/external copy ports), static OpenSSL3.5.9, two Cargo
jobs and `--locked`. Latest accepted integration re-merge returned `Already up
to date` at `c7d9fd8`. Final source had no frozen-file diff.

| Gate | Actual result |
| --- | --- |
| Sequential fmt → all-target workspace clippy `-D warnings` → workspace tests → build → active cargo-deny | Pass; existing duplicate-version warnings retained; license/advisory policy unchanged |
| Ordinary workspace | **90 passed /0 failed /203 ignored** |
| ALL explicit combined ignored gates | **203 passed /0 failed /0 ignored**:195 SQL,1 schema/adoption/grants,5 private Blob,1 private Queue,1 native PDF |
| Native auth/member/catalogue/upload/events/query/extraction/recordings + AI foundation | **9/9 pass**; actual unchanged UI and finite processes; AI process uses loopback HTTP/fake credential + real SQL, consumer break/cap and deliberate settlement-failure/nonzero/flush control |
| Drained private final telemetry replays | **9/9 pass**; findings/errors/legacyMatches empty,0700 directories/0600 replays |
| Official-schema detector/lifecycle | **110 passed /0 failed /0 skipped** |
| Unchanged frozen AI/ledger/provider/notices/infra suites | **42 passed**, including three actual frozen PG concurrency cases with local configuration (not absent-gate early returns) |
| Unchanged mocked UI: prior seven specs + chat, desktop/mobile | **184 passed**; mock/static tests, not hosted AI or native chat proof |
| Unchanged frontend unit tests | **115 passed /0 failed /0 skipped** |
| Frozen SDK oracle | **23 offline observations**; requests, usage/malformed cases, metadata/content-filter SSE, physical retries |
| Native linkage | Embedded **OpenSSL3.5.9**, no dynamic libssl/libcrypto |

Logs: `/tmp/opencode/228-final-{fmt,clippy,workspace,build,deny,explicit,linkage,
detector,frontend-unit,frozen-browser-configured,native-summary}.log`, per-native
`228-final-<name>.log`/`-replay.log`, `228-final-{native-results,evidence}.json`,
`228-frozen-dotnet-sql.log`, `228-wire-oracle-final.log`. The external handoff is
`/tmp/opencode/liedertafel-218-228-report.md`; private replays must not be published.

Retained setup failures: the first browser command discovered184 cases but
could not launch missing Chromium1243, so **no assertion pass** came from that
run (`228-final-frozen-browser.log`). The explicit external config selected the
existing approved Chromium1248 without changing frozen tests/frontend; its
separate configured run passed184. The first combined explicit attempt stopped
on a test JSON-number-kind assertion (`0.0` versus `0`), not an accounting error;
the corrected numeric wire assertion and full203 rerun passed. Initial
`cargo deny` was unavailable on PATH; the final gate used the existing pinned
cargo-deny0.20.2 executable and passed. Executed red→green regressions for
content-filter billing, malformed usage, admission cancellation and stalled
settlement remain in separate `228-*-{red,green}.log` files.
