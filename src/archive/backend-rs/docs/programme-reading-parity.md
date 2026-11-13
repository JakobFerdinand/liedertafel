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

Programme uses #230's shared request-drop child token and `Origin::reading_tasks`.
A disconnected handler cancels its consumer, while the tracker joins native work
and accounting independent of that handler's join handle. Host cancellation
precedes Axum wait; producer close/wait precedes Gateway drain and checked OTLP
flush. There is no per-request global Gateway drain.

`programm_http_lifetime` exercises actual TCP disconnect and host signal during a
held second call, with a reported100/20 first call and full-reservation settlement
of an unreported field call. It observes settlement **before** global drain and
checks no event/draft writes. The separate `programme-http-native` example is an
offline-only test host (loopback `test231_programm_*` database, no configured Azure
provider); it exposes the real route/auth/Gateway/SQL with held scripted delivery.
It is not a product command, credentials bypass or live evaluation authorization.
`programm-reading-native.cjs` drives it for actual process SIGTERM/failed
settlement and terminal OTLP privacy, alongside the real default product/unchanged
UI draft-resume/editor-save journey. These native gates are pending until an
executed final evidence table is added; the socket gate alone is not a process
or image/AppHost acceptance claim.

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
Current validation remains in progress, not accepted.
