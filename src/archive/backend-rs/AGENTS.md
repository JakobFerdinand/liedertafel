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
  the dev sampler and the `Liedertafel.Archive` schema parity.
- `crates/archive-server`: the archive origin — static Next.js export,
  `/api/build`, `/alive` (+ `/health` in Development) and the single
  implemented-routes list with the stable JSON 404/501 miss for unported
  `/api/*` routes.

Add one line per member crate under the workspace root as they land, and
update this section in the same PR whenever the member list changes.

## Build, check, test

- One cargo job at a time (host budget and parallelism rules live in the
  global instructions, not in this repo): never build while tests run, never
  build two worktrees at once.
- Build: `cargo build --workspace --locked`. Tests: `cargo test -p <member>`;
  focused: `cargo test <member> --test <file> <filter-substring>`.
- `Cargo.lock` is committed; every command passes `--locked` so upgrades only
  happen on purpose.
- Frontend deps (`pnpm`) are untouched by this workspace; the frontend `out/`
  export is served by the backend binary.
- The three live-spend AI evaluations keep their skip-when-unconfigured
  environment gates from the C# suites; without explicit configuration they
  must pass by skipping, never by calling a model.

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

- Any `/api/*` route without a Rust implementation answers the single stable
  JSON 404/501 error shape, driven by one list of implemented routes.
- `/api/build` and the readiness endpoint have the same shapes as today from
  wave 1 onward.
- OTLP traces/metrics/logs keep the dashboard-compatible schema; every host
  registers the service-defaults equivalent (OTLP mandatory, console-only
  logging insufficient) and workers carry W3C trace context.
- Governing docs: [ADR-0001](../../docs/adr/0001-flat-lied-and-upload-first.md)
  wins on conflict, the architecture doc at `../docs/architecture.md`
  (§14 governs AI work), and the startup/runbook contracts in
  `../README.md`.

## Done means

- fmt + clippy + tests green (run one at a time), parity cases translated
  where the wave demands them, C# backend untouched, telemetry visible in the
  local Aspire stack, and this file updated in the same PR whenever the
  workspace layout, commands or checks change.
