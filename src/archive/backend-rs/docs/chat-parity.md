# Member chat (#232)

## P2 media-data grammar remediation (2026-11-16)

Tested product/test pin `6524fb8f799557625fb10ad1f8a776f0cd59454d`: fresh fmt,
locked all-target clippy,124 ordinary/310 explicit tests,95 frozen cases,
124 unchanged browser cases,110 detector controls and18 native suites with18
new private terminal replays passed after the final product change. All4 actual
strict components and both form consumers ran. Native default-product binding:
985 bodies,294 accepted/691 zero-call/row/thread rejections. Final guide changes
are documentation-only; exact final pin, preservation/idle/source-linkage audit
and limitations are in `evidence/232-report.md`. Independent protocol P2 review
and catalogue narrow-union verification remain required; #233 stays blocked.

The protocol rereview closed P1; the catalogue rereview closed S1–S6. Its catalogue,
evaluation, history and ledger source/test files remain byte-identical to005e46b.
P2 changes only `chat/input.rs`'s AG-UI data-source validation, not any global,
auth or provider Base64 policy. It ignores only SP/TAB/CR/LF, accepts nonzero
unused pad bits, and explicitly retains required canonical padding length and
placement. VT/FF/NBSP, malformed padding and invalid alphabet remain safe400
before provider/ledger/thread work, never copied frozen500s.

`232-chat-media-base64.json` records234 actual frozen HTTP + BCL
`Convert.FromBase64String` observations: the independent12 unchanged raw bodies
first, all ASCII control/space/DEL positions, additional Unicode whitespace,
padding/alphabet boundaries, unused pad bits and audio/video/document consumers.
All58 BCL-valid rows finish in frozen HTTP;176 BCL-invalid rows produce frozen
FormatException/500 and safely reject400 in Rust. The original751 fixture is
unchanged. The shared actual product-router/native binder gates now run985
bodies; accepted browser media remains untrusted and only the last user text
reaches the model. Exact red/green/native/full results and final pins belong in
the durable report; the table below is explicitly pre-P2, not new acceptance.

API lookup: Context7 quota exhausted; pinned base640.22.1 docs.rs and local
package source confirm `with_decode_allow_trailing_bits` and
`DecodePaddingMode::RequireCanonical`. No dependency/lockfile change.

## PRE-P2 review-remediated candidate (2026-11-16)

P1 and S1–S6 are implemented in separate commits through product-source pin
`6f3f5ffad04f3f8b925a212498eb307f7f5f7f67`; original `ad8de88` evidence remains
pre-remediation. Fresh post-code gates passed: fmt, locked all-target clippy,
124 ordinary/310 explicit Rust tests,95 unchanged frozen cases,124 unchanged
frontend browser cases,110 detector/lifecycle controls and18 native suites with
18 fresh private three-signal terminal replays. Both form consumers, default
product/locked HttpAgent,751 raw binder bodies and first-SQL ownership control
are included. The native auth runner's wrong filename attempt is retained; the
correct harness passed. Exact pins/logs and final clean/idle audit live in the
durable `evidence/232-report.md`; counts are not independent acceptance.

The guide's final documentation-only commit does not change the tested product
or test tree. Both independent rereviews and parent-owned serialized acceptance
remain required; #233 is blocked. No paid/live/hosted provider, deployment,
outbound merge/push, fixture cleanup or frozen-source change was authorized.

This is a local implementation candidate, not an accepted port wave.
The frozen C# backend/tests and static frontend remain unchanged. This guide owns
the chat seams; foundation accounting limitations remain in
`ai-foundation-parity.md`. No hosted, paid-model or image acceptance is
implied by offline tests.

## Ownership and wire

### P1 request grammar remediation

`chat/input.rs` replaces permissive Value lookups. Its recorded public HTTP
corpus (`tests/fixtures/232-chat-binding.json`) includes the independent reviewer's
original30 bodies plus721 typed/polymorphic/case/duplicate/media-source controls.
Expected status/question values come from an actual unchanged frozen factory
using AGUI.Abstractions/Server1.0.0, corroborated against pinned upstream source
f08ccf853497914ef70a5f0197af45bb74915bc2 and reflection of the loaded assemblies.
Generated CLR properties bind case-insensitively and validate every occurrence;
the user/custom discriminator readers use last exact-case JsonElement lookups.
Text parts concatenate in order. All supplied history/tool/context/resume shapes
are validated but remain untrusted and are not model grounding or executable
tools. Only the last user question survives into the owned producer.

The65 explicitly marked unsafe frozen observations are safely rejected400 before
provider/ledger/thread writes instead of copying500 or invalid null-runId SSE.
These include null entries and downstream construction errors; the pinned MEAI
adapter also throws `Unknown chat role` for activity/reasoning messages, which
this member-chat adapter rejects safely rather than forwarding unsupported roles.
No strict-client schema was relaxed. The native binder gate uses the default
binary, identical raw bodies, real auth/SQL, the locked client with text parts,
and a separate before-first-ownership-SQL lock/socket-abort/SIGTERM control.

`chat/mod.rs` authorizes Member + CSRF before the independent Enabled/Disabled
gates, then transfers the entire producer into the existing Origin tracker
before thread/history SQL. Its cancellation guard belongs to the SSE body:
returning a handler is not a disconnect. Body drop cancels delivery. History
save/trim and per-call accounting remain joined after disconnect or early
RUN_ERROR. Host shutdown signals before waiting for Axum; Origin joins producers
before Gateway drain and checked terminal telemetry. There is no per-chat global
Gateway drain, alternate shutdown framework or resumable-reading registry.

Every model iteration and the one first-step/pre-update feature retry use the
same operation and the real Gateway. All three tools remain offered, output is
2000 clamped by the foundation ceiling, and the configured logical step bound
includes the first call. The first-update deadline disarms on any update;
overall cancellation remains active through tools. Gateway accounting and
history are separate transactions; no exactly-once paid/history claim is made.

Only the latest browser user question is used. Saved recent20 messages supply
text-only grounding; supplied history/tools/state are not authority. Successful
filtered text/cards are saved; failure keeps the question only. The answer
limit truncates persistence, not live text. Timestamps retain the frozen100ns
offset; PostgreSQL microsecond precision means this is **not** a proven strict
question-before-answer ordering under equal stored timestamps. Concurrent turns
are not serialized or claimed exactly once.

The emitter omits absent event-owned properties (including nested usage fields),
does not send tool results or surface-tool invocations, and derives citation
chips solely from this run's actual tool results and filtered markers. Ordinary
text is not a factual-claim sanitizer. Tool-result JSON keeps meaningful nulls
and the frozen default encoder; REST `a2ui:null` is allowed. `WireUsage` carries
grouped reported metadata separately from ACK/max-based billing. Invalid
negative/unsafe protocol counts fail closed rather than sending a value the
locked client refuses; frozen adapter negative-count permissiveness is not a
safe-client guarantee.

## Catalogue and A2UI

S4/S5 boundary expectations in `232-chat-clipping.json` come from actual frozen
factory HTTP A2UI plus provider-captured tool strings, not source-extracted
expressions. They cover the space at index159, split title/composer/teaser,
300-unit lyrics and2000-unit analyzed score text; a200-unit split query with a
scalar-prefix decoy separately confirms internal UTF16 matching. JSON clipping
replaces the retained unpaired surrogate with U+FFFD. Internal query matching
must not use that JSON replacement or silently broaden to the scalar prefix;
only the split final token needs a UTF16 constraint over shared search candidates.

Persistence was observed **separately** by invoking the unchanged frozen
`ChatThreadHistoryProvider.StoreTurnAsync` with its actual EF10.0.12/Npgsql10.0.3
against dedicated PostgreSQL/current-model tables: bound2 over `a😀x` throws
`EncoderFallbackException` inside the turn batch and leaves no messages; bound3
saves `a😀`. This is real provider/database evidence, not a frozen full HTTP/PG
journey or historical-migration claim. Rust now rejects that invalid persistence
slice before committing a turn, emits the existing safe RUN_ERROR, and keeps
already settled paid usage without replay. Complete pairs still persist. No
wire-replacement policy is applied to SQL and no schema/ledger policy changes.

`tools.rs` uses the shared member search/ranking/visible completed Noten paths,
page size10, query200 UTF16, pages1..5, bounded deduplicated score text, and
`events/history.rs`'s current history/recording visibility. `search_agreement`
now compares the **actual tool adapter** against `/api/lieder`, not a surrogate.

`a2ui.rs` accepts component IDs only and rebuilds labels/properties from current
visible rows. All4 archive components,12 inspected requests and4 surfaces per
run are bounded and deduplicated. Visible IDs are accepted even without prior
retrieval, as in frozen ShowAsync: this is not a retrieved-ID allowlist. GET
rechecks current visibility and ignores stored display properties/URLs, drops
malformed cards without failing text, and never calls the provider. It does not
recensor saved text or recreate citation chips. No basic/editor catalog exists.
S6 keys Quellen by its resolved canonical Lied-ID sequence, not mutable titles
or composers. The HTTP regression pauses delivery between two surface calls,
edits both fields in real SQL outside the provider, then releases delivery and
requires one surface plus one dropped duplicate. Its delivery-only async barrier
changes no Gateway/ACK/reservation/shutdown policy.

## Frozen-file ownership

### S1–S3 review remediation

The live evaluation hard citation set is freshly queried member-visible Lieder,
with folded labels; irrelevant valid citations remain soft, including page-two
browse, draft-title echo and lyric/composer-token relevance offline controls.
Each of the17 scripted runs snapshots ledger row IDs and requires exactly one
fresh operation with positive input/output/cost and zero reservation. The final
set must contain17 distinct operations; an evidence-deletion negative control
demonstrates that an earlier positive row cannot mask a later missing row.

Adapter assertions now include54 reverse-inserted titles and complete page lists,
whitespace/page0 and Lied00, exact analyzed `Tenor 1 A♭ 44 Hosianna,`/dedup/null
and trashed Noten exclusion;23 filtered titles/20 nonmatches across three turns,
exact/disjoint citations and browse end-of-pages; GET no-store; two visible
Programme entries with order and optional-note omission, missing/empty/trashed
Auftritt guards. Stored replay mixes valid/nonexistent/Button/URL/duplicate cards
and includes `[1,2,3]`/`[null]` garbage. Card-bearing foreign/anonymous/revoked GETs
stay unobservable and do not call a provider. Statement triggers reject every
chat/ledger save in the all-save-failure fixture, leaving usage/messages/threads
empty. The independent locked unchanged frontend parser validates all4 actual
HTTP-built components, with an extra-property negative control per component
(`chat-a2ui-parse.cjs`, invoked by `chat_a2ui_api`); this is not4 new UI journeys.

Paths below are `tests/archive/backend/*.cs`; Rust siblings are under
`crates/archive-server/tests`. Grouped tests/table rows are not extra functions.
An ownership row is not by itself an executed assertion-equivalence claim.

| Frozen file | Rust ownership |
| --- | --- |
| `ChatApiTests.cs` (32 methods, including theories) | `chat_api`: access/CSRF/gates, turn/history20/foreign ownership/browser authority, browse/filtered continuation/citations, exact prompt/tool hashes/output/options, retry/deadlines/caps, summed398microEUR, cap/unpriced/unwritable refusal; `chat_http_lifetime`: actual TCP cancellation, blocked history and one paid-save attempt, real failed settlement; native gates below own agent/tool/budget/process/OTLP checks. Scripted unknown/injection/ordinary browse siblings also run in the17-row evaluation. Private Provider ownership replaces the frozen DI/reflection wiring guard without exposing a raw production client. |
| `ChatApiTests.A2ui.cs` (20 methods + theory rows) | `chat_a2ui_api`: server data/save/reload/delete/card-only, all4 components, invalid/schema5/foreign-fields2/removed-components2, duplicate/empty-unused fields,12-request/4-surface bounds; `chat_api` owns shared foreign/revoked/anonymous gates; current tamper/garbage SQL in `chat_a2ui_postgres`; locked-client parse in native gate. |
| `ChatApiTests.Historie.cs` (4 methods) | `chat_history_api`: previous cited Lied follow-up, approximate date/published mark/Lied-Aufnahme/hidden recording, future Programme versus sung, honest unsung/title-only request. |
| `CatalogueToolsTests.cs` (6 methods) | `catalogue_tools`:54-song page0/1/2/5/intMax/limits/narrow/past-end, empty catalogue/invalid/missing/trash details, live completed cleaned/deduplicated Noten/facts, Noten-only match, sung/planned/Lied-Aufnahme/public marks. |
| `SearchAgreementTests.cs` (2 methods) | `search_agreement`: actual HTTP/provider-captured tool result versus HTTP listing; browse12/page10vs20 and exact/title/lyrics ranking. |
| `ChatA2uiPostgresLiveTests.cs` | `chat_a2ui_postgres` owns CURRENT jsonb type/roundtrip/containment/fresh visibility/ID-array/recheck/garbage SQL. `historical_baseline` separately owns the approved head equivalent described below. |
| `ChatEvaluationTests.cs` | `chat_evaluation`: frozen17 rows/14 categories, independently selected citation candidates and trap assertions; zero-extra-spend crosschecks in `chat_api`; ordinary absent-config live gate and configured same17 live sibling with metadata-only observer, hard/soft separation and price checks. Live execution remains unrun. |
| AI ledger/client/provider/notices/infrastructure tests | Inherited #228 ownership in `ai-foundation-parity.md`; no current SQL case is waived. Chat's actual summed loop, lifetime and grouped terminal metadata are additive obligations. |
| Search/Noten module tests | Inherited #223/#226 plus actual adapter ownership here; not replaced by chat heuristics. |
| Immutable frontend chat/A2UI suites/client | Unchanged mocked frontend regressions are separate from actual default-product/native strict-client/UI controls. |

### Historical and foreign-model dispositions

The approved historical equivalent independently restores frozen-head DDL and
all44 EF history rows, seeds stable thread/account/text/null-A2ui/text-card/
card-only rows, and compares complete chat snapshots across native adoption and
rerun. Truncating history at
`20261010174016_FieldProvenanceAndProposals` must reject without modifying those
rows. It does **not** execute historical ChatMessageA2ui add/down/up transforms.
See `historical-migration-disposition.md`; current jsonb SQL remains mandatory.

The maintainer approved an **offline equivalent only** for the single raw
foreign-ModelId probe: actual unchanged frozen fake-transport capture, private
Rust module loopback/nonstream encoding counterpart, and actual Gateway foreign
refusal with zero provider calls/ledger rows. The raw Azure type stays private;
normalization clears model options before every product call. This proves wire
encoding/refusal, not hosted deployment behavior, and authorizes no paid probe.

## Local checks and resource safety

Use a dedicated disposable local PostgreSQL server, private external browser
dependencies and the immutable frontend export as documented by the earlier
parity guides. Keep raw evidence0700/files0600 and follow
`tests/telemetry/README.md` for terminal capture/replay rules. Preserve failed
captures as failures. Configuration is never model-spending authorization.

```bash
cargo test -p archive-server --locked --test chat_api --test chat_a2ui_api \
  --test chat_history_api --test catalogue_tools --test chat_evaluation \
  --test chat_a2ui_postgres --test chat_http_lifetime --test search_agreement -- --ignored
cargo test -p archive-server --locked --test chat_evaluation
cargo test -p archive-server --locked --lib approved_foreign_model
cargo test -p archive-server --locked --test historical_baseline -- --ignored
cargo build -p archive-server --locked --example chat-http-native
node tests/browser/chat-native.cjs
node tests/browser/chat-lifetime-native.cjs
```

`chat-native.cjs` uses the default product binary, real auth/mail, real locked
HttpAgent1.0.2, unchanged UI/card/reload/ID link and real settled SQL. Its W3C
control checks HTTP→agent→Gateway/tool parentage and all three private signals.
`chat-lifetime-native.cjs` uses the offline-only `chat-http-native` example:
delivery-only scripts below real Gateway/currentSQL/product routes/Origin.
It checks first/second socket abort and SIGTERM, early RUN_ERROR+blocked save,
one paid-save attempt/no replay, retained reservation/nonzero settlement failure,
and nonzero exporter failure. Both gates drain children/collector before secrecy
assertion and retain terminal replays. Neither is a product command, live-provider
authorization or image pass.

The full inherited gates, both form consumers, native browser regressions,
three-signal replay, terminal audit and independent review remain required after
the final code change. Final candidate pin/results belong in the durable #232
evidence report, not inferred from these reproduction commands.

Pre-remediation evidence includes the unchanged scoped frozen95 cases, immutable
desktop/mobile chat/A2UI124 browser cases,110 detector/lifecycle controls and
17 independently replayed native captures (including both shared form consumers).
The new18-suite post-remediation results are distinguished at the top and in
the durable report; no original failure or capture has been removed.
The native navigation differential retains the shared failed held full-page
navigation boundary separately from completed-navigation/tab-close controls.
Earlier setup/failure logs remain failures. Final full explicit counts/results
are recorded only in the evidence report after the jobs complete.
