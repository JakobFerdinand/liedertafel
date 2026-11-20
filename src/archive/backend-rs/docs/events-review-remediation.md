# #225 review remediation: R1–R3

Review fixed point: `227dc5f`, integration `4aab55c`. This document supersedes
the initial candidate's collation and string/path Guid claims. Acceptance remains
with the independent rereviewer; wave 5 stays blocked until that decision.

## R1: culture equality before secondary keys

Predefined `und-x-icu` is deterministic: PG adds an original-byte tie-break before
later ORDER BY keys. NFC normalization alone would not cover ICU ignorable
characters. Explicit additive Rust migration **0004_culture_comparison.sql**
creates exactly `public.archive_culture (provider=icu, locale='und',
deterministic=false)`. No runtime DDL, Bicep, baseline rewrite, new table/column,
canonical-grant change or stored-text normalization is involved.

The collation retains tertiary distinctions (case, accents and ligatures are not
folded). Event Lied-Aufnahmen now compare titles with it before CreatedAt/Guid;
history recording links compare labels before passage Guid. Conflict names are
still **ordinal-distinct**, retaining input order with a HashSet+Vec; culture-equal
ties preserve that input order via unnest ordinality, not a byte fallback or
collation-based DISTINCT. This is the frozen Linux/en-US/root ICU contract, not a
promise that arbitrary ICU versions or arbitrary .NET CurrentCulture tailorings
are identical. No claim that NFC alone establishes all ICU equivalences is made.

`event_culture.rs` drives real PG + authenticated HTTP against **8 independent
frozen datasets** (six rows each): NFC/NFD ä, soft hyphen, zero-width joiner,
Angstrom canonical equivalence and combining grapheme joiner, plus non-equal
case/accent/ligature controls. It checks direct comparison equality/sign, full
CreatedAt/Guid and passage-Guid order, ordinal distinct conflict names, and exact
stored title/label bytes. The same test reconstructs its owned prior Rust head
(migrations 1–3) and invokes native `--migrate` twice, comparing all seeded rows
and existing history across the additive upgrade/rerun.

`migrate_process` separately checks exact provider/locale/determinism/encoding,
fresh and adopted schema against independently written expected additive DDL,
migrator ownership, runtime comparison use and denied runtime create/alter/drop.
Only the exact pinned new pg_dump collation block is removed for the *pre/post*
EF-adoption comparison; full schema parity includes the independent expected
collation. The immutable canonical fixture and adoption fingerprint stay intact.
The already-reviewed historical-equivalent map remains authoritative; **no
historical EF transforms are executed by Rust**, and current SQL is not waived.

Official references: [PG17 nondeterministic collations](https://www.postgresql.org/docs/17/collation.html#COLLATION-NONDETERMINISTIC),
[CREATE COLLATION](https://www.postgresql.org/docs/17/sql-createcollation.html),
[Neon ICU support](https://neon.com/docs/reference/compatibility#collation-support).
Only public documentation and disposable local PG were accessed; no Neon query.

## R2: both unknown-year upcoming dates

The upcoming comparator returns EventId comparison immediately if both years are
null, ignoring leftover month/day. `auftritt_programm::upcoming_both_unknown_years_compare_event_id_before_seeded_month_day`
seeds the review's lower-ID `(null,5,12)` and higher-ID all-null rows and exercises
real `/api/programmes`. Both display honestly unknown and remain upcoming. This
does not broaden create/patch date validation.

## R3: one CLR string parser, separate strict JSON converter

`clr_guid::try_parse` is narrowly for CLR **STRING** inputs: member AccountId,
repair CLI and every existing catalogue/upload/trash/draft/event/history/metadata
path consumer. `GuidPath` is the body-free binding extractor. Both existing
`:guid` draft routes add a shared constraint guard **before auth**, selecting API
404 for invalid strings; unconstrained Guid bindings retain controlled JSON 400
(the actual frozen Development host throws/answers 500 for these typed failures).
Domain nil rejection remains in member and repair helpers, not the shared parser.

Grammar follows official [.NET 10 Guid.cs](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Guid.cs)
and independent actual .NET 10.0.14 observations, not uuid's broader grammar or
the previous permissive wrapper helper:

- N is exactly 32 hex digits. B/P wrap **D only**, never N or another wrapper.
  Surrounding CLR whitespace is accepted, inner B/P whitespace is not; URN fails.
- D/B/P retain the rare CLR compatibility `+`/`0x` component spellings, with their
  fixed total lengths and the special final eight literal hex digits.
- X removes Char.IsWhiteSpace throughout. Components require an outer 0x prefix,
  then CLR's optional `+`/second 0x rules; arbitrary leading zeroes do not overflow.
  uint32 overflow fails; B/C are uint32 then **truncate to ushort**; bytes must fit
  0..255. Empty-after-compatible-prefix behavior is preserved. Malformed braces,
  byte counts, missing prefixes and trailing junk fail. No guessed fixed-width X.
- Empty/whitespace CLI account option still means omitted, as before; a nonempty
  nil Guid is still a domain error. No auth/role/crypto policy was redesigned.

The accepted typed **JSON Guid** converter in `binding.rs` is untouched and stays
D-only. Existing eight binding fixes, all wire-occurrence/null/type distinctions,
ordinary/custom numeric converters and opaque formular raw semantics remain.
Provider-generated Azure mail operation IDs are not public CLR string/domain/path
inputs and are outside this correction; no future endpoints are implemented.

`clr_guid_api` exercises the independent **139-case BCL matrix** at member and live
catalogue/draft HTTP seams (28 distinct actual parsed IDs), malformed constrained
selection before anonymous auth, all 22 existing unconstrained route patterns,
nil-domain guards, no-effects invalid member calls and positive P/X/whitespace
mutations across all five existing member operations. An empty fixture string has
no path capture; the malformed-path control substitutes encoded NUL, not a claim
that the collection URL is a malformed Guid. `member_operator` invokes the native
repair process for all 139 cases, retains blank-option omission semantics, checks
actual target identities and snapshots invalid-target state. These matrix cases
are controls inside tests, not 139 independently reported test passes.

## Independent fixtures and regeneration

Committed generator: `tests/oracles/ReviewOracle.cs` (actual BCL + unchanged frozen
assembly only, never Rust). Fixtures:

- `tests/fixtures/guid-bcl.json`: `[input, parsed-D-or-null]`, **139** rows.
- `tests/fixtures/culture-frozen.json`: **8** six-row datasets, actual
  `LoadLiedAufnahmenAsync` and authenticated frozen history HTTP ordering.
- `tests/fixtures/review-oracle-provenance.json`: .NET **10.0.14**, BCL MVID
  `a3899d31-fe2e-4ae3-b87c-22172033e1b4`, frozen MVID
  `832e3b4f-1e79-46fd-bb6b-5ae7f06ad5ed`, frozen SHA256
  `ed159fb2c988fdba053e68c8747d76ac5d2e6e623a00a081d3276e782c258ee9`,
  culture en-US / sort version 34969. Runtime source MIT notice is retained under
  `tests/oracles/dotnet-runtime-LICENSE.txt`.

Use an **external** net10.0 console project with a Microsoft.AspNetCore.App
framework reference, Compile-link `ReviewOracle.cs`, and copy the existing frozen
`Archive.Backend.deps.json` to that project's output. Set ARCHIVE_FROZEN_TEST_BIN
to the unchanged built frozen test output and
ASPNETCORE_TEST_CONTENTROOT_ARCHIVE_BACKEND to the frozen backend directory. Pass
an external output directory as argv[0]. The Guid JSON object rows emitted by the
generator are losslessly represented as the committed `[input,parsed]` pairs;
culture/provenance JSON are unchanged semantically. Compare fixtures to actual
output before accepting a regeneration; a Rust failure is never permission to
recapture expected results from Rust.

External source/project/output: `/tmp/opencode/225-remediation-oracles/`.
Red/green logs: `/tmp/opencode/225-r{1,2,3}-*.log`; R1 schema fixture setup failure
(PG17 column is `colllocale`, not old `colliculocale`) was corrected, not counted
as a pass.

## Final remediation verification (2026-11-10)

All commands used the persistent `target/ticket-225` on the root filesystem,
static OpenSSL **3.5.9**, `--locked`, incremental/debug disabled and two jobs.
No dependency/version/lockfile/toolchain/audit-policy change. Sequential fmt →
workspace/all-target clippy → ordinary workspace → cargo-deny completed before
the explicit gates. Integration was re-merged before handoff: already up to date
at `4aab55cead07a85cbb97f83c4cb788bc8947a721`.

| Gate | Actual remediation result |
| --- | --- |
| fmt; workspace/all-target clippy `-D warnings`; cargo-deny | Pass; advisories/bans/licenses/sources OK |
| Ordinary workspace | **57 passed / 0 failed / 149 explicitly ignored** |
| All explicit real PostgreSQL suites | **144 passed / 0 ignored**, including six new remediation tests |
| Separate native exact schema/adoption/grants | **1 passed** |
| Private Azurite | **4 passed** |
| Ordinary ignored gates subsequently executed | **149/149 = 144 + 1 + 4**, not skipped-as-passed |
| Default-feature workspace build; native linkage | Pass; embedded **OpenSSL 3.5.9**, no dynamic libssl/libcrypto |
| Structured detector/lifecycle | **110 passed / 0 skipped** |
| Native auth / members / catalogue / upload / events / query binding | All **6 pass**; real auth/SQL/SMTP-boundary/OTLP, private Blob and unchanged UI where applicable; query differential **36 actual frozen .NET↔Rust cases** |
| Final drained private capture replay | All **6 pass**, zero findings/protocol/lifecycle errors |
| Actual BCL + unchanged frozen culture regeneration | **139 Guid rows + 8 culture datasets + provenance match**; observations, not extra independent tests |
| Unchanged selected frozen .NET suites | **37 passed / 0 skipped**, including three oracle-only recording-owned facts |
| Unchanged frozen serializer/domain and HTTP probes | **157 serializer observations + 9 validator calls; 71 HTTP observations**, not independent test passes |
| Unchanged Auftritt reducer/persistence units | **29 passed / 0 skipped** |
| Unchanged five-file frozen Playwright desktop/mobile selection | **76 passed / 0 skipped**, API-mocked static UI only |

Logs: `/tmp/opencode/225-remediation-{fmt,clippy,workspace,deny,pg,schema,
azurite,build,linkage,detector,dotnet,review-oracle,http-oracle,dto-oracle,
frontend-unit,browser}.log`; native/replay logs:
`225-remediation-{native-auth,members-native,catalogue-native,upload-native,
events-native,query-binding}{,-replay}.log`. The temporary static server was
stopped after its mocked browser gate. Red/green logs remain separate.

Final captures (0700 directories / 0600 replay and reports, **never publish raw
values**) under `/tmp/opencode/`: `223-native-auth-telemetry-73alEU`,
`223-members-native-telemetry-tm48Mw`, `223-catalogue-native-telemetry-fjP7Ks`,
`223-upload-native-telemetry-sjAowQ`, `223-events-native-telemetry-Z37Rw3`,
`223-query-binding-telemetry-GNyrGF`.

### Remaining gates / honest limits

- **Independent rereview is pending; wave 5 stays blocked.** No acceptance or
  integration/main/cutover merge is inferred from passing local gates.
- The approved historical-equivalent disposition remains unchanged; historical
  EF transforms were **not executed by Rust**, and no current SQL/grant/schema
  contract is waived.
- #227 owns recording mutations/full recording integration, #231 actual
  scan/model/suggestions and read→resume/save behavior, #229/#232 their assigned
  historical equivalents. This remediation implements no future endpoint.
- Known real frozen passkey/member browser failures were **not rerun** in the
  mocked selection and remain documented failures. Video-job FK23503 permanent
  removal remains refused, not successful cleanup. The original uncaptured
  redaction ambiguity remains unclassified; clean later captures do not explain it.
- Hosted keyless access, image/AppHost estate, later waves and cutover/production
  remain separate gates. No frozen C#/tests/frontend, infrastructure, workflow or
  Dockerfile edits; no push, issue/PR update, deployment, production/Neon/live
  AI/mail access or worktree cleanup.
