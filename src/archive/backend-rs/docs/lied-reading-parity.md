# Lied draft reading (#230)

## Shutdown checkpoint — paused by user, not accepted

Branch `ticket/230-rust-lied-reading`; implementation/union merge
`99f0c5470feccd5889edea2f48633535412b8c43` incorporates accepted integration
`06946d47cdfb875314636d0d963fe83df466c38c` (#231). The checkpoint commit changes
this guide only. Four merge conflicts were resolved additively (root guidance,
Origin registry field, HTTP-module visibility, both test-support exports); one
automatically duplicated tracker initializer was removed. Shared/Programme code
and Lied/Claim code retain their accepted/pre-union bytes.

User cancelled validation during stage53, `mocked-ui`. Exactly52 stages recorded
exit0; stage53 is interrupted, not passed. No later pipeline stage was planned;
terminal artifact audit and final runtime/source clearance remain unrun/pending.
Job `sh_1d5f67d9c001zETztpEd6xfFN8`, runner PID812170 and its exact owned children
received SIGTERM; no live owned PID remained, and the shell completed exit143.
No unrelated services/containers were stopped or cleaned up. No checks were
started after the pause request. Partial logs/captures remain retained.

Durable checkpoint: `target/agent-218/evidence/230-report.md` TOP, with exact final
documentation HEAD/status, process/termination records and nonsecret resume
instructions. Partial run: `evidence/230/final-union-06946d4/{results,captures}.json`,
per-stage logs, `evidence/230/pipeline-union-06946d4.log`. No full-union pass or
acceptance is claimed. #232 stays blocked. Resume requires fresh authorization,
own230 target/fixtures/short scratch `/tmp/opencode/a230`, preserved immutable
provenance and a NEW output directory; never overwrite this interrupted run.
No live AI, tokens/private corpus, production, push/tracker/outbound merge/deploy
or cleanup is authorized. URL-layout qualification and the shared failed full-
navigation baseline remain retained, not successful-resume claims.

The following historical validation sections predate this paused consumer union.

Accepted base `ed710631694574b32776db7a7f35a295ac179201` (#229). Independent
review/integration acceptance remains parent-owned. Frozen C#/tests/frontend,
infrastructure, workflows and image are unchanged. #231 owns programme additions.

## HTTP and reader contracts

`POST /api/lied-lesungen` uses current membership/editor/CSRF/no-store. Order:
access → form → required string Guid/positive Int32 → Enabled → first named file
length/signature → owned claim → reading. Filename/part type never prove PDF.
Optional invalid/unknown/foreign Datei means **stateless**, not foreign mutation
or registry cancellation. Nil Guid remains parseable. No JSON identity binder.

Shared `reading/http.rs` uses Axum multipart and already-locked MIME parsing.
Names accumulate case-insensitively, nonempty scalar values join with commas,
first named file wins. URL-encoded forms count as form content; a valid identity
without file reaches422. Native **30,000,000-byte whole-body** envelope remains
distinct from Reading32MiB and Extraction64MiB. Boundary128,1024 sections,
headers16/16KiB and URL encoded-byte key2048/value4MiB precede domain validation.
Every multipart section counts before classification (including files/ignored
attachments);1024 passes,1025 refuses. Multipart
text uses actual built-in charset/BOM decoding (unknown charset defaults to UTF8;
disabled UTF7 is refused). Malformed forms return controlled JSON400 rather
than framework exception payloads; native oversized bodies return413. TestServer
does not establish the native Kestrel envelope. Raw bounded section-header
metadata preserves whitespace/duplicates that multer's HeaderMap discards; body
lengths and boundary parsing still come from multer. The whole form is buffered
once under the independent30MB cap before parsing (Bytes clones share storage).
Header limits count distinct case-insensitive names. Frozen ProcessLineChar
recognizes LF after CR before checking that LF against the line budget:99 padding
characters fit the100 limit,100 do not;16,383 aggregate ASCII header characters
leave room for the final empty line's CR,16,384 do not. Caps remain100/16KiB;
no arbitrary allowance or source buffer-layout resource escape is adopted.
Attachment sections are ignored, not accumulated. Case-insensitive
disposition parameters, filename*, empty filename-as-scalar and quoted MIME B
names use the actual framework metadata policy. The quoted MIME decoder alone
allows noncanonical unused base64 bits; provider/auth decoding is unchanged.

`tests/oracles/LesungFormGrammarOracle.cs` and `tests/browser/lesung-form-cases.cjs`
compare **identical public bytes**:137 actual FormFeature observations covering
UTF8/UTF16LE-BE/UTF32/BOM override/unknown or disabled charset, both encoded
identities, attachment before/after valid fields, case/repeated fields, first
file,127–129 boundaries,16–17 distinct or repeated headers, raw whitespace and
limit-adjacent header blocks, valid boundary transport padding and its100-byte
line budget (space/tab98/99/100 and header16,382/16,383/16,384 adjacent controls),
1024/1025 mixed sections, raw URL byte bounds/escaping/multibyte/empty
segments and URL charsets. `lesung-form-native.cjs` tests Content-Length AND
31-byte chunked framing on both actual frozen routes, and the default Rust Lied
route. Set `ARCHIVE_FORM_ROUTES=/api/lied-lesungen,/api/auftritt-lesungen` on the
consumer union to test both Rust routes; #231 must do that on its own branch.
Oracle parser refusal is not a claim of universal native .NET400 versus500
equivalence: frozen unhandled exceptions may be500; Rust returns controlled400
before Enabled. The separate49-observation disposition oracle includes all36
enabled built-in aliases. Axum/multer remain the parser, including their existing
malformed-body acceptance limits; no universal RFC/.NET parser equivalence is
claimed from this finite corpus.

`tests/oracles/LesungFormOracle.cs` executes real ASP.NET ReadFormAsync,
StringValues and BCL Int32 in en-US:150 observations over both form types.
Signs/leading zeroes/ASCII numeric whitespace/source width/NUL ordering remain.
UTF8/ASCII URL percent-decoded NUL fails before Int32; multipart retains its legacy
tail NUL tolerance. Other URL encodings use the actual FormPipeReader slow policy:
encoding-specific RAW byte separators and limits, decode without BOM sniffing,
then UTF8 percent-unescape. A slow-path percent NUL is retained. Unknown encoding
falls back to UTF8; disabled built-in UTF7 refuses before identity. Invalid UTF8
percent sequences remain literal, not lossy decoded replacements. Existing
independent139-case CLR Guid-string parser is reused.

`LesungUrlPipeOracle.cs` executes actual public FormPipeReader on identical fresh
buffers in single/multi-segment layouts:174 observations, including adjacent4MiB
limits. Its three empty-tail cases expose **source layout variance**: the slow
path drops empty pairs that the fast path counts (1024 empty tails can evade the
source slow-path count). Rust enforces the1024 raw-pair bound independent of
transport layout, including empty segments except the nonexistent pair after a
final delimiter. Default frozen native Content-Length/chunked cases agree. This
finite native policy is not universal internal-pipe-layout equivalence or an
independent waiver; the actual variance remains explicit for parent review.

`DraftReader` extracts embedded text first with Extraction bounds. Only NoText
renders first `min(3,ImagesPerCall,nativePages)` pages. The **whole requested
batch** must fit the shared estimator before rendering: worker adaptive batching
must not silently omit draft pages. Shared0600/flushed `Spool::from_bytes` and
PdfEngine/PdfPageRenderer retain native ownership through cancellation.
Deterministic facts win; only remaining code-owned fields are requested. Shared
clean/quote/every-lyric-line checks apply; verified **uncertain** suggestions remain
`sicher:false`. Unknown/unrequested/duplicate answers drop, consuming even an
invalid first occurrence. Fixed form order excludes voice_label/occasion. Empty
transcription yields empty suggestions. No catalogue writer/job/upload or
form/editor-mark mutation is reachable.

## Durable and instance-local arbitration

Claim takes short parent/Datei key-share locks then conditional INSERT/ON CONFLICT.
The COMPLETE Claim→registry→recheck→read→store future is transferred through
`Origin::spawn_reading` **before the first Claim SQL await**, after auth/form/file
validation. The handler retains its drop signal while ownership is unresolved.
A successful owned Claim ignores that signal; eventual missing/foreign fallback
checks it before reading/spending. A blocked Claim is not abandoned on disconnect.
Equal/older numbers answer400; only higher numbers reset `liest`. PostgreSQL
arbitrates first-insert races without broad exception retries or provider-time
locks. Terminal UPDATE compares exact draft/Datei/request/`liest` **in SQL**;
removed/superseded targets answer404 and cannot be resurrected. These CAS guards
strengthen the frozen EF read-then-save race, not distributed immediate stopping
or exactly-once charging. Outcomes alone persist: success fields,429 unavailable,
other material/input/provider/cancellation failures retryable. Existing five-minute
interrupted projection and form-save preservation remain unchanged.

- Stateless work: HTTP handler-drop guard + host signal; no registry dedup solely
  for echoed browser identities.
- Owned persisted work: detached tracked consumer + instance-local registry,
  post-commit discard/removal, higher request, three-minute deadline and host
  signal. **Not** tab disconnect. Cleanup removes only its own handle; delayed old
  registration cannot cancel newer work. Registration precedes durable recheck,
  closing the removal-before-registration gap without spending.
- Other replicas may run to their own limit; conditional SQL refuses stale stores.

`Origin::spawn_reading` transfers the current HTTP tracing span together with
owned work (plain Tokio spawn loses it); native OTLP verifies every model span's
parent ID and trace against the exported HTTP span.
Main cancels `Origin::shutdown` **before Axum wait**. Afterwards `Origin::drain_readings`
joins whole consumer/native/store tasks, THEN Gateway
accounting, THEN terminal telemetry. Model-capable hosts use checked three-signal
finite flush/shutdown and fail nonzero on accounting/flush failure; the watchdog
covers this sequence. Dependency-free skeleton hosts retain their prior
best-effort collector control. Never globally drain Gateway per request.
Unconfigured web delivery now uses the central scripted scan delivery, still
below real Gateway/PostgreSQL. #231 alone extends its programme recognition.

## Frozen suite map

| Frozen source/scope | Rust public seam |
| --- | --- |
| Entire `LiedLesenApiTests.cs` | `lied_lesen_api`: text/scan/identity/wire mapping, analyzer/uncertain/quotes/duplicates/lyrics, cap/input/material/access/current membership/CSRF, order/form/native-envelope controls |
| `LiedEntwurfApiTests.cs` reading methods238–520 | `lied_entwurf_reading`: outcome/resume/no form mutation, late discard/remove, supersession/higher retry, real notifications/equal first claims/budget/stale/foreign/stateless, actual three-minute limit and locked stale terminal UPDATE |
| Same file persistence/validation | Existing #224 `lied_entwurf_api`, inherited regression, not re-claimed translation |
| Actual HTTP disconnect | `lied_reading_lifetime`: real TCP/Router/Gateway/PG/PDF; held-model lifetimes plus real Claim row-lock barrier, actual Hyper-drop witness, owned completion and no-spend missing-file/foreign fallback |
| Product terminal telemetry | Ordinary `lied_reading_process`: actual product SIGTERM fails nonzero on terminal flush refusal; inherited `process_startup` skeleton remains unchanged |
| Native unchanged UI/model HTTP/host | `tests/browser/lied-reading-native.cjs` + offline-only `examples/lied-reading-native.rs`: real sign-in/CSRF, tab close/resume,30MB envelope, both raw-socket lifetimes, early Claim-lock disconnect, discard/both held-model shutdowns, failed settlement/nonzero/conservative reservation, all OTLP signals/private terminal replay |
| Full navigation versus tab close | `lied-navigation-native.cjs` + external build of `tests/oracles/NavigationHost.cs`: actual frozen Kestrel and Rust, same static client/PDF/provider holds; retained shared failed navigation versus two positive boundaries |

The example permits only local `test230_lied_*` DB, loopback HTTP and local SMTP
with a fixed synthetic credential. It serves product router/readers/registry and
shared drain, not a product command or hosted-token path. Generated DBs remain
for audit. Synthetic100/20 usage is fixture delivery, not measured OCR quality.

## Reproduce and qualifications

### Final implementation validation

**Latest LF-budget correction:** source rereview atb242ea0 closed R1–R4 and found
one Low line-budget off-by-one. Three bounded checks now recognize accepted LF
before its limit check. Actual frozen Kestrel BOTH routes versus Rust Lied, both
Content-Length/chunked, reproduced16,383 headers and99 padding as400-versus409
before the correction; all137 cases pass afterwards. Exact logs:
`red-readline-header-native.log`, `red-readline-padding-native.log`,
`green-readline-native.log`. All130 pre-existing actual oracle observations remain
unchanged. All50 stages were freshly repeated at a630580 after this last code
change and the artifact audit passed. The prior b242 evidence remains preserved
as PRE-FIX in its separate evidence directory; it is not LF-follow-up clearance.

Review `230-review.md` independently SOURCE-CLOSED R1–R4 atb242ea0 and retained
only the new LF Low. That Low's final source/runtime clearance remains pending.
Each code fix is a separate logical commit:

| Finding | Fix | Evidence level / retained red→green |
| --- | --- | --- |
| R1 all-section1024 cap |61307d0 | actual frozen/default Rust native1024/1025 files/ignored/scalars; `red-form-r1-native-2.log` → `green-form-r1-native.log` |
| R2 raw URL byte/count bounds |d5604c8 | actual adjacent2048/4194304 raw/escaped/multibyte/empty-tail native controls; `red-form-r2-native.log` → `green-form-r2-native.log`; internal pipe-layout variance remains separately explicit |
| R3 URL charset/percent/BOM policy |78d94d7 | actual130-byte-case native differential +174 public FormPipeReader layout observations; `red-form-r3-native.log` → `green-form-r3-native.log` |
| R4 Claim ownership |cc3546c | initially source-derived, then real TCP/PG lock + actual handler-drop witness reproduced missing owned result; `red-claim-socket.log` → `green-claim-socket.log`; no-spend foreign/missing fallback and native model-socket/W3C follow-up |
| Known boundary transport padding |6e8616e | actual FormFeature/native accepted/rejected padding controls; `red-form-padding-native.log` → `green-form-padding-native.log` |
| LF-before-limit off-by-one |a630580 | exact space/tab98/99/100 and ASCII header16382/16383/16384 actual oracle/native controls; two native reds above →137-case green; unchanged100/16384 caps |

Full-navigation causation/baseline is the measured differential below, not an
assumed consequence of R4. Prior231 disposition/charset/header findings were
covered by029; incoming context uses separate b1fb6ed and owned-parent46cf40d.

Runtime/test source **a6305809f63ce0ca1393981af9caf00419bb93a6**, integration
ancestor **ed710631694574b32776db7a7f35a295ac179201** (latest merge: already up to
date). All50 sequential stages completed after the last implementation change;
artifact audit passed. The final handoff commit changes documentation only.

| Gate | Result |
| --- | --- |
| fmt, all-target clippy `-D warnings`, workspace/examples builds, pinned cargo-deny | PASS |
| Ordinary Rust workspace |114 passed /247 ignored /0 failed |
| ALL combined explicit gates |247 passed /0 ignored /0 failed; real PG/schema/private Blob/Queue/native PDF included |
| Native protocol/lifecycle/differential gates |14/14; includes W3C,137-case forms and the retained shared-navigation-failure classification, NOT14 blanket successful-resume journeys |
| Terminal private OTLP replays |14/14; findings/errors/legacyMatches empty, completion/secrecy checked;0700/0600 |
| Unchanged frozen.NET scoped suites |160 passed |
| Unchanged frontend unit / mocked desktop-mobile UI |115 /202 passed |
| Official-schema detector/lifecycle |110 passed |
| Actual form / disposition / URL pipe-layout observations |137 /49 /174, independently regenerated; observations are not test-function counts |
| Native linkage |supported static OpenSSL3.5.9; no dynamic libssl/libcrypto |

Durable artifacts are under original workspace
`target/agent-218/evidence/230/final-a630580/` (`evidence.json`, `results.json`,
`captures.json`, per-stage logs), with `audit-a630580.log` beside it. Final LF
source rereview/runtime clearance remain parent-owned. The accepted source-layout
qualification and shared failed-navigation baseline stay retained, not new owner
blockers or successful-resume claims. Programme Rust route is absent here:
#231 must retest its route/model chain and the combined consumer union. The green
absent-config live guard is not a live-model evaluation. No acceptance, transfer,
cutover or #232 clearance is implied by these implementer gates.
For231 already consuming the verified b242 chain, only the separate
`230-shared-readline-lf-a630580.patch` is new; source/base/scope/digest are in
`230-final-shared-patches-v2.json`. All old patch bytes/manifests remain unchanged.

```sh
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
cargo test -p archive-server --locked --test lied_lesen_api --test lied_entwurf_reading --test lied_reading_lifetime -- --ignored
cargo build --workspace --examples --locked
# ALL inherited explicit SQL/schema/private Blob/Queue/native PDF gates apply.
node tests/browser/lied-reading-native.cjs
node tests/browser/lesung-form-native.cjs
node tests/browser/lied-navigation-native.cjs
node --test tests/browser/telemetry-*.test.cjs
```

Native env adds `ARCHIVE_LIED_READING_PROBE` for the built offline example.
Navigation also needs `ARCHIVE_NAVIGATION_DOTNET_HOST`: external net10 console
project referencing unchanged backend and exact existing Mvc.Testing10.0.12,
compiling only `NavigationHost.cs`. It uses actual Program/Kestrel in Development,
real PG/Gateway/ledger and static frontend; only keyed provider DELIVERY waits on
loopback HTTP then delegates to the frozen scripted client. Synthetic local DB,
SMTP, endpoint and credential guards remain. No frontend/auth/ledger/native mocks.
Dedicated230 PG55432/Azurite51033/52033/53033 stay separate from231; the initial
custom-key fixture on51032 remains retained but is not the final gate fixture.
Accepted
immutable OpenSSL3.5.9/PDFium8138/browser/static files are provenance checked.
Source-neutral external capture-root relocation moves inherited hardcoded paths
to230's private durable root; scanner/assertions remain unchanged. Existing
versions/checksums remain pinned; only feature-required package/edge additions
for Axum multipart/image PNG-WebP and direct already-locked MIME occur. The
requested #231 union also exposes already-locked unicode-normalization0.1.26;
no matcher/reader/photo/programme source is duplicated here.

Final source pins/counts/logs are recorded only after combined jobs complete in
durable `target/agent-218/evidence/230-report.md`; pending gates are not passes.
Initial native fixture errors (missing SDK choice index; wrong SQL table spelling)
and a real **full-page-navigation** failure remain retained. A controlled actual
frozen.NET-versus-Rust differential now reproduces it identically, including
after the Claim ownership repair:

| Boundary on BOTH actual hosts | Browser/server state | Resumed title |
| --- | --- | --- |
| Full navigation while model held | `fehlgeschlagen` / `gelesen`, valid title suggestion | `Synthetic` — LOST SUGGESTION, retained failure |
| Full navigation after result applied/persisted | `gelesen` / `gelesen` | `Waldfahrt` |
| Actual tab close while model held | `liest` / `gelesen` | `Waldfahrt` |

Instrumented fetch rethrows the SAME original exception, records only type/name,
abort flags/status/timing in sessionStorage; no response/credential alteration.
At held full-navigation both record pagehide then **TypeError**, not AbortError,
with `signal.aborted=false`; no HTTP read response is observed. No browser console
errors were emitted in the measured controls. The unchanged client consequently
persists failed status and suppresses the same-request stored suggestions.
This establishes the common baseline and boundary, not a new fixed UI journey.
The differential asserts this retained failure AND both positive controls; a green
differential is NOT successful full-navigation resume or acceptance/waiver.
Original failed artifacts remain; frontend is frozen, parent owns disposition.

Inherited passkey/member browser failures, UNCLASSIFIED historical redaction,
video FK refusal, historical migration equivalents, conditional budget/current
price/unknown-ACK/not-exactly-once notices, native cooperative cancellation and
decoded-expansion limits remain unwaived. Hosted identity/storage/mail, live AI,
private inputs, image/AppHost/cutover and whole-integration independent review
stay separately gated. No push/tracker/integration-main merge/deployment/cleanup.
#232 waits for independently accepted230 **and**231.
