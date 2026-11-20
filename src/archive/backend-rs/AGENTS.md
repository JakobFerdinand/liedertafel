# backend-rs agent guide

This Cargo workspace is the archive backend ([#218](https://github.com/JakobFerdinand/liedertafel/issues/218)).
The root AGENTS.md applies too. Follow `../README.md` for startup and
`.github/workflows/archive.yml` at the repo root for release checks.

## Boundary

- The retired C# oracle and tests are available at
  `8c7a077988d3ca6a0a72788fb4cc13da9e3ae4a6` in git. Historical parity guides
  reference paths in that tree; use a separate checkout for oracle regeneration.
  The release check runs the translated Rust suites, not a C# backend build.
- The statically exported Next.js frontend (`src/archive/frontend`) remains
  the client seam.

## Documentation lookup is mandatory

Before writing code against a crate API from memory, look it up with the
find-docs skill (`npx ctx7@latest library <crate> "<specific query>"`, then
`npx ctx7@latest docs <libraryId> "<query>"`):

- tokio, axum, tower-http, sqlx, azure_*, pdfium-render, lettre,
  webauthn-rs, tracing, opentelemetry, reqwest, clap
- docs.rs is the API reference of record (`https://docs.rs/<crate>`); crates.io
  answers current versions. If you change a crate version in `Cargo.toml`,
  read that version's changelog before porting code against it.
- Do not silently fall back to training data: axum 0.8 changed path syntax,
  sqlx renamed APIs across 0.8 → 0.9, and widely copied examples pin old
  versions.

## Workspace layout

Wave 1 (#219) created the workspace.

- `crates/archive-telemetry`: service-defaults equivalent — OTLP traces,
  metrics and logs for every host, the Development-mandatory endpoint check,
  the dev sampler, checked finite-operator flush/shutdown and the
  `Liedertafel.Archive` schema parity.
- `crates/archive-server`: the archive origin — static Next.js export,
  auth/passkeys, shared access/maintenance gates, catalogue/Datei reads and writes,
  uploads, trash/tier lifecycle and owned Angefangen persistence, `/api/build`,
  `/alive` (+ `/health` in Development) and the single
  implemented-routes list with the stable JSON 404/501 miss for unported
  `/api/*` routes.
- `crates/archive-db`: explicit `--migrate`, frozen EF/Neon baseline adoption,
  SQLx history and runtime grant repair; its Cargo build script tracks newly
  added migration files. Before editing migrations or their
  parity checks, read `crates/archive-db/baseline/README.md` for provenance and
  the disposable PostgreSQL/pg_dump gate.

Add one line per member crate under the workspace root as they land, and
update this section in the same PR whenever the member list changes.

## Build, check, test

- Build: `cargo build --workspace --locked`. Tests: `cargo test --locked -p <member>`;
  focused: `cargo test --locked -p <member> --test <file> <filter-substring>`.
- `Cargo.lock` is committed; every command passes `--locked` so upgrades only
  happen on purpose.
- Frontend deps (`pnpm`) are untouched by this workspace; the frontend `out/`
  export is served by the backend binary.
- Native telemetry detector/lifecycle: `node --test tests/browser/telemetry-*.test.cjs`
  (or add `--test-name-pattern="formatter"` before the file for a focused run).
  Before changing native capture, redaction or schema fixtures, read
  `tests/telemetry/README.md` for external decoder setup and private replay rules.
- Production image/AppHost, seven cross-stack methods and
  authenticated HTTP/protobuf gates are in `docs/image-apphost-parity.md`.
  The image check's focused negative controls run with
  `bash tests/image/check-apphost-image-failures.sh` against the same local tag.
  `tests/image/check-translated.sh` is the offline release gate for real
  prerequisites, including ignored tests. Keep captures private.
- Incoming HTTP W3C context: `node tests/browser/http-w3c-native.cjs` checks
  the product router, with the offline-only `http-w3c-native`
  example for non-Development sampling. Read `docs/http-w3c-parity.md` before
  changing request-parent extraction, health skipping or sampler controls.
- Catalogue query binding: before changing scalar/repeated keys, read
  `docs/catalogue-review-followup.md` for the actual frozen .NET differential,
  empty/duplicate binding semantics and the authenticated PG regression command.
- The three live-spend AI evaluations keep their skip-when-unconfigured
  environment gates from the C# suites; without explicit configuration they
  must pass by skipping, never by calling a model.
- Migration process checks: `cargo test -p archive-server --locked --test
  migrate_process`. The real schema/grant gate is explicitly ignored by default;
  with a dedicated throwaway server and pg_dump 17 configured per the baseline
  README, run `cargo test -p archive-server --locked --test migrate_process
  postgres_migration_contract -- --ignored --nocapture`. Report an unrun gate
  as unavailable, not passed.

## Lint and format before you commit

- In order, each on its own: `cargo fmt --all -- --check`, then
  `cargo clippy --workspace --all-targets -- -D warnings`, then tests.
- Project lints live in the workspace `[lints]` table and `clippy.toml`; never
  inline `#[allow]`/`#[expect]` without a comment naming why it is needed.

## Toolchain

- `rust-toolchain.toml` (added with wave 1) pins an exact stable patch
  (`channel = "<x.y.z>"`, not `"stable"`, which floats), with `rustfmt` and
  `clippy` components. Re-check the current patch at
  [channel-rust-stable.toml](https://static.rust-lang.org/dist/channel-rust-stable.toml)
  when adding it. Bump it only in its own PR.

## Known traps

- Official Azure crates are **singular**: `azure_storage_blob`,
  `azure_storage_queue`, `azure_identity`. The plural
  `azure_storage_blobs`/`azure_storage_queues` 0.21.0 crates are dead legacy;
  never depend on them (cargo-deny bans them once `deny.toml` lands in #219).
- `azure_identity` has **no `DefaultAzureCredential`**: use
  `DeveloperToolsCredential` for local development,
  `ManagedIdentityCredential` in production (`az login` dev path preserved).
- axum 0.8 uses `/{id}` and `/{*rest}` captures (`/:id` panics at startup);
  extractors run in argument order and the body-consuming one (`Json`, `Form`,
  `Bytes`, `String`) must be last; of several `.layer()`s the last added runs
  first.
- pdfium-render needs the native PDFium library present in the image
  (`Archive:Reading` environment), like `PDFtoImage` today.
- sqlx compile-time-checked macros need `DATABASE_URL` or the committed
  `.sqlx/` offline data (`cargo sqlx prepare`); offline data keeps CI
  database-free.

## Routes and contracts

- Ops (#234): before changing Inventur, cleanup, finite command gates or
  `/api/dev` diagnostics, read `docs/ops-parity.md` for frozen ownership,
  retained FK/hosting limits and the private native gate. Focused:
  `cargo test -p archive-server --locked --test ops_http --test ops_cli`.

- Video (#233): before changing transcode/dispatch, finalize outbox, video status/
  retry, tool processes or copy publication, read `docs/video-parity.md` for
  lease/CAS/finite bounds, actual square/rotation policy and retained FK/capacity
  limits. Focused: `cargo test -p archive-server --locked --test video_worker
  -- --ignored` (private PG/Queue/Blob + real tools). Native: `node
  tests/browser/video-native.cjs` and `node tests/browser/video-process-native.cjs`.
  The retained video-job permanent-removal refusal is not successful cleanup.

- Member chat (#232): before changing AG-UI, authoritative history, catalogue
  tools/citations, A2UI replay, chat options or SSE shutdown, read
  `docs/chat-parity.md`. Focused PG: `cargo test -p archive-server --locked
  --test chat_http_lifetime -- --ignored`. Default product/locked-client/UI:
  `node tests/browser/chat-native.cjs`; process/SQL failure/privacy controls:
  `node tests/browser/chat-lifetime-native.cjs` with offline-only
  `chat-http-native` example. Pinned raw request grammar and first-SQL ownership:
  `node tests/browser/chat-binding-native.cjs` (`chat_binding` is its focused PG
  sibling). Live evaluation keeps its ordinary absent-env gate;
  `chat_a2ui_api` also invokes `tests/browser/chat-a2ui-parse.cjs` over actual
  HTTP component output (needs `ARCHIVE_STATIC_ROOT`'s unchanged locked client).
  the single foreign-model probe has an offline-only disposition, not spending
  authorization. Current jsonb SQL and historical head equivalent are separate.

- Auth/passkeys (#221): before changing auth policy, session storage, mail or
  translated auth tests, read `docs/auth-parity.md` for the expert-core boundary,
  legacy synthetic fixtures, oracle/exception mapping and real PG/browser/native
  commands, approved exact-version license exceptions and native OpenSSL/support
  requirements. Later member routes reuse `Auth::authorize`/`RequestAccess::respond`;
  account-changing transactions reuse `Auth::revoke_sessions`.

- Member administration/operator/mail (#222): before editing these boundaries
  or their tests, read `docs/member-parity.md` for transaction lock order,
  repair-vs-email-change credentials, loopback SMTP/Mailpit, native
  CLI/browser/OTLP and frozen-suite mapping (including failed browser controls).
  Focused PG: `cargo test -p archive-server --locked --test member_revocation
   last_active_administrator -- --ignored`.

- Catalogue/Blob reads/tiers (#223): before changing visibility, shared search,
  tickets, provider configuration or tier policy, read `docs/catalogue-parity.md`
  for the oracle map, bounded range reader, official SDK/local signing seam,
  explicit PostgreSQL/Azurite/browser gates and later-wave ownership. Focused:
  `cargo test -p archive-server --locked --test lied_api member_reads -- --ignored`.

- Upload/trash/draft persistence (#224): before changing sessions, replacement,
  owner visibility, permanent removal, Cold→Hot or Angefangen, read
  `docs/upload-trash-parity.md` for ordering, oracle ownership and the frozen
  video-job Restrict limitation. Focused PG gate: `cargo test -p archive-server
  --locked --test upload_races -- --ignored`. Native UI gate:
  `node tests/browser/upload-native.cjs` (private Azurite endpoint must be
  reachable by its own server-side copy fetch).

- Any `/api/*` route without a Rust implementation answers the single stable
  JSON 404/501 error shape, driven by one list of implemented routes.

- AI foundation (#228): before using the gateway, modifying ledger SQL/prices,
  provider wire types, cancellation or notices, read `docs/ai-foundation-parity.md`.
  Features take `ai::Gateway`, never a provider. Finite hosts await `drain()`
  before checked telemetry flush. Focused offline gate: `cargo test -p
  archive-server --locked --test ai_guards --test ai_wire`; real PostgreSQL:
  `cargo test -p archive-server --locked --test ai_ledger -- --ignored`.
  `examples/ai-foundation-native.rs` and `node tests/browser/ai-foundation-native.cjs`
  are offline test tools, not production commands or live-evaluation authorization.

- Reading (#229): before changing stored reading state/dispatch, scan prompts,
  rendering, quote verification or automated field writes, read
  `docs/reading-parity.md`. Later draft/programme consumers reuse these seams and
  must prove their own actual HTTP shutdown/disconnect/registry lifetimes.
  Focused real-PG/native-PDF gate: `cargo test -p archive-server --locked --test
  reading_worker --test reading_lifecycle -- --ignored`. Native unchanged UI:
  `node tests/browser/reading-native.cjs` (also needs the offline-only
  `reading-cancel-native` example). Live scan evaluation has a runtime absent-env
  skip gate; configuration is not spending authorization.

- Programme suggestions (#231): read `docs/programme-reading-parity.md` before
  changing matching, photo/scan requests, HTTP lifetime/W3C/form controls or evaluation. Focused PG:
  `cargo test -p archive-server --locked --test auftritt_lesen_api -- --ignored`.
  Native UI/HTTP/OTLP: `node tests/browser/programm-reading-native.cjs` with the
  offline-only `programme-http-native` example; live evaluation stays gated.
  Shared generic controls: `node tests/browser/http-w3c-native.cjs` and
  `node tests/browser/lesung-form-native.cjs` (both Rust routes against the
  retained oracle fixtures).

- Auftritte/Programme/initial Entwurf (#225): before changing partial dates,
  programme IDs/order, Zeitmarke guards, history, event documents or opaque form
  persistence, read `docs/events-parity.md` for binding/order/concurrency and
  per-file oracle ownership. Historical EF cases use the explicit approved
  equivalents in `docs/historical-migration-disposition.md`, not old-schema
  upgrades. Focused PG: `cargo test -p archive-server --locked --test
  auftritt_programm -- --ignored`. Native unchanged UI:
  `node tests/browser/events-native.cjs`; scan/model semantics stay #231.

- Lied draft reading (#230): before changing multipart binding, persisted
  claim/store, registry notifications or HTTP producer shutdown, read
  `docs/lied-reading-parity.md`. Focused PG/native-PDF example:
  `cargo test -p archive-server --locked --test lied_entwurf_reading discarding -- --ignored`.
  `node tests/browser/lied-reading-native.cjs` also needs the offline-only
  `lied-reading-native` example. This is not a product command or live-provider
  authorization.
  `node tests/browser/lesung-form-native.cjs` exercises both routes against
  the recorded frozen form grammar. `lied-navigation-native.cjs` runs the
  Rust-only regression (including the retained failed full-navigation boundary).
  Use the pinned historical tree and parity guide for Kestrel/Rust regeneration.

- Before changing culture ordering or CLR string/path Guid binding, read
  `docs/events-review-remediation.md`: migration 0004 preserves ICU equality
  before secondary keys; `clr_guid` is separate from strict typed JSON Guid.
  The historical fixture generator was `tests/oracles/ReviewOracle.cs`; its
  external-project setup/provenance are in that document. Focused real-PG gate:
  `cargo test -p archive-server --locked --test clr_guid_api --test event_culture
  -- --ignored`. Keep constrained draft API fallback before auth.
- `/api/build` and the readiness endpoint have the same shapes as today from
  wave 1 onward.
- Aufnahmen/Zeitmarken (#227): before changing recording slots/publication/tickets,
  passage CRUD/review or their Programme/history projections, read
  `docs/recordings-parity.md` for per-file ownership, direct-Blob policy and
  independent playback-versus-passage stale gates. Focused PG:
  `cargo test -p archive-server --locked --test recording_passage_api -- --ignored`.
  Native unchanged UI/private Blob/OTLP: `node tests/browser/recordings-native.cjs`.
  Seeded completed-copy integration is not the #233 video worker.
- OTLP traces/metrics/logs keep the dashboard-compatible schema; every host
  registers the service-defaults equivalent (OTLP mandatory, console-only
  logging insufficient) and workers carry W3C trace context.
- Governing docs: [ADR-0001](../../docs/adr/0001-flat-lied-and-upload-first.md)
  wins on conflict, the architecture doc at `../docs/architecture.md`
  (§14 governs AI work), and the startup/runbook contracts in
  `../README.md`.

## Done means

For extraction, Queue, PDF or worker-search changes, read
`docs/extraction-parity.md` for finite drain/lease/hook contracts, pinned native
ABI/spool limits and per-file ownership. Focused real-PG gate: `cargo test -p
archive-server --locked --test extraction_worker -- --ignored`; native unchanged
UI/private Queue gate: `node tests/browser/extraction-native.cjs`.

- fmt + clippy + applicable tests green, telemetry visible in the
  local Aspire stack, and this file updated in the same PR whenever the
  workspace layout, commands or checks change.
