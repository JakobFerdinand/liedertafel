# backend-rs agent guide

This directory is the Rust rewrite of the archive backend (a Cargo workspace,
spec [#218](https://github.com/JakobFerdinand/liedertafel/issues/218), wave 1
[#219](https://github.com/JakobFerdinand/liedertafel/issues/219)). Research
notes behind the agent setup: `../docs/rust-port-agent-setup.md`. The root
AGENTS.md at the repo top applies here too; nested files are additive.

## Boundary

- `src/archive/backend` (the C# backend) is frozen: it is the parity oracle
  for the whole port. Do not modify it or its tests while the port runs; all
  backend work lands here.
- The statically exported Next.js frontend (`src/archive/frontend`) is not
  changed by the port; it stays the only client seam.

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

- Build: `cargo build --workspace --locked`. Tests: `cargo test -p <member>`;
  focused: `cargo test <member> --test <file> <filter-substring>`.
- `Cargo.lock` is committed; every command passes `--locked` so upgrades only
  happen on purpose.
- Frontend deps (`pnpm`) are untouched by this workspace; the frontend `out/`
  export is served by the backend binary.
- Native telemetry detector/lifecycle: `node --test tests/browser/telemetry-*.test.cjs`
  (or add `--test-name-pattern="formatter"` before the file for a focused run).
  Before changing native capture, redaction or schema fixtures, read
  `tests/telemetry/README.md` for external decoder setup and private replay rules.
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

- Auth/passkeys (#221): before changing auth policy, session storage, mail or
  translated auth tests, read `docs/auth-parity.md` for the expert-core boundary,
  legacy synthetic fixtures, oracle/exception mapping and real PG/browser/native
  commands, approved exact-version license exceptions and native OpenSSL/support
  requirements. Later member routes reuse `Auth::authorize`/`RequestAccess::respond`;
  account-changing transactions reuse `Auth::revoke_sessions`.

- Member administration/operator/mail (#222): before editing these boundaries
  or their tests, read `docs/member-parity.md` for transaction lock order,
  repair-vs-email-change credential behavior, native CLI/browser/OTLP commands,
  frozen-suite mapping and the honestly failed frozen browser controls. Focused
  PG example: `cargo test -p archive-server --locked --test member_revocation
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

- Auftritte/Programme/initial Entwurf (#225): before changing partial dates,
  programme IDs/order, Zeitmarke guards, history, event documents or opaque form
  persistence, read `docs/events-parity.md` for binding/order/concurrency and
  per-file oracle ownership. Historical EF cases use the explicit approved
  equivalents in `docs/historical-migration-disposition.md`, not old-schema
  upgrades. Focused PG: `cargo test -p archive-server --locked --test
  auftritt_programm -- --ignored`. Native unchanged UI:
  `node tests/browser/events-native.cjs`; scan/model semantics stay #231.

- Before changing culture ordering or CLR string/path Guid binding, read
  `docs/events-review-remediation.md`: migration 0004 preserves ICU equality
  before secondary keys; `clr_guid` is separate from strict typed JSON Guid.
  The independent fixture generator is `tests/oracles/ReviewOracle.cs`; its
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

- fmt + clippy + tests green, parity cases translated
  where the wave demands them, C# backend untouched, telemetry visible in the
  local Aspire stack, and this file updated in the same PR whenever the
  workspace layout, commands or checks change.
