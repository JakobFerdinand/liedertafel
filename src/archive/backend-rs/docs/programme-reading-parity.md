# Programme suggestions (#231)

Base: accepted `ed71063`. Independent acceptance remains parent-owned. Frozen
C#, tests, frontend, schema, infrastructure and image are unchanged.

## Contracts

`POST /api/auftritt-lesungen` is editor/current-membership + CSRF, no-store,
request-bound even if its echoed Entwurf identity names a saved browser form.
The endpoint writes no Auftritt, Programm, Lied or Auftritt-Entwurf. Only the
unchanged browser persists its opaque whole form and later invokes editor-save
routes. There is no programme registry or stored outcome.

The shared #230 form helper owns multipart/URL-encoded parsing, case-insensitive
accumulation, CLR string Guid/Int32, first named Datei and the distinct native
30,000,000-byte whole-body envelope. Identity precedes Enabled; Enabled precedes
file length/signature. Per-file Reading limits remain separate. Unsupported or
unreadable material/input bounds return422, cap429, unavailable409, delivery or
structured-output failures502. Failures log a static type classification only.

PDF text extraction precedes model transcription. A raster PDF uses the first
`min(4,ImagesPerCall,pageCount)` pages; a photo is one bounded JPEG. The whole
consumer batch must fit its actual programme prompt/image estimate before native
rendering: unlike the stored worker it cannot silently shrink and resume later.
JPEG/PNG/WebP use signatures, not filename/content-type. Photos never upscale,
use ties-to-even dimensions, premultiplied-alpha JPEG onto black, and only the
frozen90/180/270 EXIF rotations; mirrored
origins deliberately remain unchanged. Different JPEG/interpolation libraries
are not claimed byte-identical. Native decode expansion/cooperative cancellation
and final image/AppHost containment remain the accepted #235 limitations.

Programme methods extend the existing `ScanReadingModel`/central script below
Gateway + production SQL ledger. Agents/features are
`archive-programm-transcriber`/`programm-transcription` and
`archive-programm-fields`/`programm-fields`; fresh operation/account attribution
never enters provider JSON. Exact captured instructions/schema, shared UTF-16
text/output bounds and Gateway price/input/cap guards remain code-owned. The
script cannot understand images; its evaluation override reports1700/120 tokens
for transcription, while other programme usage is the frozen synthetic UTF-16
text-length usage (including default System.Text.Json BasicLatin escaping).
The actual frozen scripted-cost oracle and real SQL ledger agree on all seven
documents:18,641 micro-EUR,13 calls. These are source-derived fixture allowances,
not a universal price cap.
This is plumbing/cost-accounting evidence, not real OCR quality or measured
provider tokens. Existing score script usage stays100/20.

Header fields consume the first nonblank exact name; title/venue≤200 UTF-16 and
one line, quote verification via programme normalization, year→month→valid day,
and approximate `ja`→`unsicher:true` follow frozen order. Entries retain order,
duplicates and independent transcription uncertainty. UTF-16/BMP decimal
numbering and date-token boundaries retain supplementary-digit distinctions.
The60 limit counts accepted
entries, not raw answers. Model-selected Lied IDs are never accepted.

The matcher deliberately loads **all saved Lieder, including trash**, as the
frozen query does. This is not member-search policy. Its own normalization folds
umlauts/ß, NFD and BMP UTF-16 letter/decimal-digit categories; supplementary
surrogates introduce gaps, including supplementary combining marks. Ordinal
UTF-16 title then CLR Guid ordering is separate from display culture. Unique
exact normalized-or-bare matches are sure without candidates; multiple exacts
require editor choice, maximum5. Otherwise Levenshtein≥0.6 offers candidates;
best≥0.85 with margin≥0.1 is preselected unsure, retaining candidates.

Actual frozen .NET10 normalization rejects U+FFFE but accepts U+FFFF and
supplementary noncharacters. The pure interface returns a typed error, propagated
as a safe502 by the reader; it never silently repairs an exceptional quote/title.

## Lifetimes

Programme uses #230's shared request-drop child token and `Origin::spawn_reading`.
A disconnected handler cancels its consumer, while the tracker joins native work
and accounting independent of that handler's join handle. Host cancellation
precedes Axum wait; producer close/wait precedes Gateway drain and OTLP flush.
Model-capable product main and the offline fixture use `Origin::drain_readings`
before checked terminal telemetry flush (dependency-free skeleton hosts retain
best-effort flush); the parent-authorized shared patch owns these
wrappers and product-host finish. There is no per-request global Gateway drain.

`programm_http_lifetime` exercises actual TCP disconnect and host signal during a
held second call, with a reported100/20 first call and full-reservation settlement
of an unreported field call. It observes settlement **before** global drain and
checks no event/draft writes. The separate `programme-http-native` example is an
offline-only test host (loopback `test231_programm_*` database, no configured Azure
provider); it exposes the real route/auth/Gateway/SQL with held scripted delivery.
It is not a product command, credentials bypass or live evaluation authorization.
`programm-reading-native.cjs` drives it for actual process SIGTERM/failed
settlement and terminal OTLP privacy, alongside the real default product/unchanged
UI draft-resume/editor-save journey. The final table records these executed
gates; neither a socket gate nor the offline process fixture is image/AppHost
acceptance or a paid-provider proof.

## Per-file ownership and controls

| Frozen seam/file | Rust owner |
| --- | --- |
| `AuftrittLesenApiTests.cs` | `auftritt_lesen_api`: photo/embedded PDF/raster first pages, identity/header/order, nullable unmatched, uncertain independent bits, invented quote/value drop, first duplicate field, calendar dependencies,60 accepted entries, source photo formats/EXIF, role/CSRF/Enabled/binding/first-file, cap/input and safe normalization errors. Matcher/evaluation cases below cover near/duplicate/umlaut picks. |
| `ProgrammMatcher.cs` (no standalone frozen test file) | `programm_matcher`: actual frozen22 match/30 string observations plus exhaustive1,112,064 scalar contexts in both modes.27,899 sparse changed/exception rows encode the actual oracle; omitted observations retain the documented identity/BMP or surrogate-gap baseline. Supplementary-mark red→green and actual U+FFFE HTTP red→green are retained. |
| Programme methods in `ScanReadingModel.cs` | Actual unchanged frozen agent/Gateway capture via `tests/oracles/ProgrammReadingOracle.cs`: exact two requests/instructions/schema and output ceilings; raw HTTP test checks requests and real SQL feature/account attribution.12 Skia-generated photo observations independently pin formats/EXIF/ties-to-even dimensions, alpha and corner orientation, allowing JPEG quantization tolerance. Four actual clean-title/two header observations pin UTF-16 regex numeric boundaries; seven scripted-answer/cost observations pin synthetic token cost. |
| `ProgrammLesenEvaluationTests.cs`, scripted | `programm_reading_evaluation`: same seven generated-photo documents and catalogue/extra duplicate, real endpoint/reader/matcher/Gateway/ledger; same eight outcome taxonomy and header errors. Wrong preselection/extra negatives fail. `unmatched-missed` is reported but does not fail a document, exactly as frozen. Nonempty positives require positive cost and≥2 calls; no arbitrary universal cost cap is invented. |
| Same file, live | Runtime exact three-variable absent/whitespace gate BEFORE provider/private corpus; configured missing/malformed/empty corpus or unusable prices fail, not skip. Uses the accepted invariant `Price::from_configuration` rather than ambient-culture Double parsing, the same explicit test-configuration qualification as #229. No live path/private corpus executed. |
| `AuftrittEntwurfApiTests.cs`, opaque form cases | Already #225 (`auftritt_entwurf_api`, `event_binding`); inherited, not newly translated. New native journey proves real read→browser whole-form save→restart/resume→editor save, no automatic server outcome. |
| `auftritt-ablegen.spec.ts` and frontend reducer/storage suites | Unchanged scoped UI/unit regressions; mocked tests are not native HTTP/accounting proof. |

## Reproduce

Use isolated PostgreSQL17/private Azurite, static OpenSSL3.5.9/pinned PDFium8138,
unchanged verified static export, dedicated build target and private capture root.

```sh
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --locked
cargo test -p archive-server --locked --test auftritt_lesen_api --test programm_http_lifetime --test programm_reading_evaluation -- --ignored --nocapture
cargo build --workspace --examples --locked
node tests/browser/programm-reading-native.cjs
```

The native journey additionally needs `ARCHIVE_PROGRAMME_HTTP_PROBE` pointing to
the built `programme-http-native` example. Run ALL inherited explicit gates and
native journeys/replays as well. Exact tested source/ancestry, terminal artifacts,
counts and failed/unrun distinctions belong in durable `evidence/231-report.md`.
Independent acceptance remains parent-owned.

## Shared review remediation (parent-authorized; pre-edge validation incomplete)

The shared-only cumulative patch is pinned to #230 source
`b242ea0d2dfc70c1f9bd4bacc6cf66c1154c077b`:
`230-shared-final-from-631.patch`, SHA256
`55f0ca16637ca1524ebb530c68a013cad06343f3467d86867025b6424f5645ff`.
All manifest digests were independently verified before consumption. It updates
16 shared files from the already-present631 snapshot; no core631 reapplication,
Lied reader/registry/endpoint, Claim fix, AI/model/programme feature, frozen code
or peer AGENTS import. Fifteen whole files match the shared source exactly; only
the exact two shared wrappers are added to this consumer's existing `Origin`.
Its Lied registry state/configuration stays excluded. Cargo's existing Unicode
edge is preserved; only direct already-locked mime0.3.18 is added. All package
versions/sources/checksums remain unchanged.

The shared parser now preserves source disposition/name/header/charset/BOM and
transport-padding rules, counts every multipart section before classification,
and bounds encoded URL key/value bytes before charset-specific decoding. The
generic same-byte130-case native harness always exercises BOTH frozen routes,
then only this branch's implemented Rust `/api/auftritt-lesungen`, with
Content-Length and chunked framing. Rust controlled malformed-form400 is not a
claim of identical unhandled .NET400/500 exceptions. Own external .NET oracles
regenerate130 form observations and174 URL-pipe observations, plus49 disposition/
charset observations. They use no peer mutable fixture/build state.

**Qualification:** actual .NET FormPipeReader fast/single-sequence and slow/
multi-sequence paths count empty URL tails differently. Rust deterministically
enforces1024 raw pairs, including empty segments but no invented pair after the
final separator. Actual default Kestrel tests cover both
Content-Length/chunked; they do not establish universal internal-layout parity
or a general waiver. The source observations and qualification remain retained.

Shared W3C extraction starts from empty context, carries traceparent/tracestate
only and excludes probes/baggage. The programme native harness additionally
decodes real OTLP for four distinguishable requests: successful product reading,
disconnect, SIGTERM, and failed settlement. Each must have remote parent→HTTP
span→both `programm-*` agent children with exact trace/parent IDs, tracestate and
sampled flag. It first failed against the old binaries; that red and the original
post-hoc failure are retained. The updated producer/fixture use shared wrappers;
no duplicate HTTP framework is introduced.

Full-page lost-suggestion navigation is an independently observed shared frozen/
Rust **failure**, distinct from passed tab-close/completed-navigation controls.
This branch does not import the Lied navigation harness, claim that failed
journey passed, or claim Rust Lied-route coverage. Both Rust routes require a
fresh run on the later accepted consumer union. Independent #231 rereview and
#230 shared-source/runtime clearance remain parent-owned; no acceptance or #232
advancement follows from this patch transfer.

### Executed pre-edge candidate d3c9eab

Runtime source `d3c9eab97961de427eb76d0f5e55b99248310fc7`, after a no-op merge of
latest accepted integration ed71063. Focused fmt/all-target clippy/builds and17
programme functions passed. Programme parent-chain, generic W3C and same-byte
form native gates all passed, each followed by its terminal private replay.
Programme chains cover completed/default-product, disconnect, SIGTERM and failed
settlement. Form coverage is130 cases ×2 framings ×frozen2/Rustprogramme1 routes;
this is not a Rust Lied-route pass. Own external framework oracles regenerated
130 form/174 URL-layout observations exactly, plus49 disposition observations.
All package versions/checksums remain unchanged. The old-binary trace-chain red
and original post-hoc W3C failure remain failed evidence, not reclassified passes.

The fresh43-stage combined attempt **stopped at stage12**, not a full pass:
11 preceding stages passed (fmt, clippy, ordinary118 tests/238 ignored, examples/
product builds, active deny, ALL238 explicit tests/0 ignored, static OpenSSL3.5.9
linkage, own frozen build, native-auth and replay). `members-native` failed before
UI assertions because the external runner's long ticket TMPDIR exceeded
Chromium's Unix-socket path limit. Its failed/unchecked capture is retained and
31 remaining stages were not run. A shorter ticket-owned TMPDIR passed a direct
browser launch/close check; no product/source change was made for that setup
failure. Logs/terminal artifact audit: `231/final-shared-pipeline.log`,
`231/final-shared/{results,partial-audit,captures}.json`. No jobs remain active.

**New shared Low LF-budget finding:** the current raw-header cursor rejects valid
99-space/tab boundary padding and16383-character ASCII header lines. Parent
assigned #230 a separate versioned followup relativeb242, without mutating the
existing patches. This candidate's130-case gates do not cover the new edges.
Await parent authorization; consume only that separately pinned fix, then test
98/99/100 spaces/tabs and16382/16383/16384 ASCII header bounds against actual
programme/default origins and rerun complete fresh-source W3C/form/programme/
inherited native and combined gates with a short own TMPDIR. No independent
parser fix, acceptance, merge or #232 advancement is authorized.

## Historical pinned-source validation (before shared remediation)

Implementation **`cfde61c3920b7c06efe7a9ac36586283d0f2896b`**, after a no-op merge
of latest integration `ed71063` (already an ancestor). The handoff's subsequent
commit is documentation-only. All35 sequential stages completed with exit0;
the independent artifact audit verified counts, native terminal generations,
permissions, source/oracle equality and linkage. Durable logs/results/captures:
`evidence/231/final-pipeline.log`, `231/final/{results,evidence,captures}.json` and
`231/final-audit-green.log`. No pending/ignored check is called a runtime pass.

| Executed gate | Result |
| --- | --- |
| fmt → all-target clippy → ordinary workspace → builds → active pinned deny | PASS; locked dependencies, existing duplicate-version warnings retained |
| Ordinary Rust workspace |118 passed,0 failed,238 ignored |
| ALL combined explicit gates |238 passed,0 failed,0 ignored:229 SQL (including reader/native-PDF/model children),1 schema/adoption/grants,5 private Blob,1 private Queue,2 standalone native PDF |
| Native auth/member/catalogue/upload/events/query/extraction/recordings/AI/reading/programme + individual replays |11/11 +11/11 PASS; three-signal terminal privacy completion, empty findings/errors/legacyMatches, capture0700/replay0600 |
| Programme native additions | Real default product/unchanged UI photo→opaque form→restart/resume→editor save; actual transport30,000,000-byte extra-section refusal and distinct per-file refusal with no model rows; real HTTP disconnect and offline process SIGTERM/unknown+reported accounting; failed settlement retains reservation and exits1 |
| Frozen scoped .NET, current PostgreSQL cases included |163 passed; existing NuGet/compiler warnings are not an audit pass |
| Unchanged frontend units / mocked desktop-mobile UI / detector |115 /200 /110 passed; zero failed; units/detector zero skipped |
| Actual frozen programme oracle regeneration |Exact80 request/prompt/string/match/photo/header/numbering/cost observations +1,112,064 scalar contexts in both modes (27,899 sparse changed/exception rows) |
| Scripted positive evaluation |7/7 documents,13 physical calls,18,641 micro-EUR; all eight entry outcome labels retained, including the allowed `unmatched-missed` |
| Default product linkage |Static OpenSSL3.5.9, no dynamic libssl/libcrypto; SHA256 `66cf5c63760eeb10816cd37280b3aef8be7e8a04a3684f7c77b2fb00adeef236` |

Counts are test functions, not matrix rows, oracle observations, assertions or
corpus children. Ordinary Rust/.NET totals include absent-config Scan and
Programme live guards, **not paid evaluation passes**. Exact frozen scalar,
photo-alpha, BMP numbering/date, kind-casing and synthetic cost controls had
retained behavioral red→green runs. Initial fixture/compile/CLI mistakes and the
first audit helper's Node spec-versus-TAP parser error remain separate failures;
the corrected audit inspected the same completed stage artifacts, not a rerun.

### Historical shared HTTP correlation failure — retained as red

A post-hoc control against the final actual native capture **failed**: incoming
synthetic HTTP `traceparent` was not adopted. Source inspection confirms the
unchanged `frontend::ProbeSkipper` creates request spans without extracting W3C
context; Queue workers' explicit remote-parent controls are separate and passed.
Agent-span presence and all three signals/privacy passed, but this HTTP parent
adoption did not. Evidence: `231/final/programme-w3c-posthoc-failure.json`.
This is shared generic-HTTP ownership (#230/parent), not independently patched
or waived here; it does not turn the11 passed native tests into a W3C pass.
The parent must resolve/dispose this finding before claiming combined HTTP
correlation parity. Product-main failed-exporter behavior above is source-derived,
not an additional executed failure probe.

Immutable reuse was checked against147 current tracked frontend files and112
output files, exact PDFium8138 library SHA256 and OpenSSL3.5.9. Build target231,
PostgreSQL55431, private Azurite51031/52031/53031, providers and final capture
directories are ticket-owned. Native/decoded expansion, conditional cap/current
price/ACK/notices, historical migration equivalents, frozen browser failures,
original UNCLASSIFIED redaction and video-FK refusal remain unwaived. Hosted
identity/storage/model/private corpus, production image/AppHost/cutover and paid
evaluation remain unrun. No push/tracker/main or integration merge, deployment
or manual cleanup occurred. Shared631bb44 snapshots preserve peer ownership;
the second accepted consumer must merge the first and rerun the combined tree.
