# Maintainer operations (#234)

The normal `archive-server` binary owns these seams. No migration runs
implicitly. The local image and AppHost gates, and OTLP HTTP/protobuf, belong
to #235. The C# backend and its existing tests remain the reference.

| Rust seam | Frozen owner | Contract |
| --- | --- | --- |
| `--inventur <verzeichnis> [--model <schluessel>] [--out <bericht.json>]` | `InventurTests`, [Quellen-Inventur runbook](../../docs/drive-inventur.md) | Offline, with indented camelCase JSON and a German summary. Root errors exit 2. Absolute `Archive__Pdfium__LibraryPath` is required if a PDF is encountered; loading the native library is never reported as a zero-page scan. The text classification reuses the bounded Rust extractor; the sanitized corpus is a parity gate, not proof of arbitrary PDFium/PdfPig layout equivalence. |
| `--cleanup-uploads` | `UploadSessionResumeTests` cleaner cases | Development. Pending only, strictly before now minus `Archive__Assets__ExpiryGrace` (default one hour). Grace accepts invariant signed TimeSpan values: `0` means no grace, a bare integer counts days, and negative grace moves the cutoff ahead. Best-effort staged-object removal, then persist Abandoned. No dry run. |
| `--cleanup-dropped-blobs [--dry-run]` | `DroppedBlobCleanerTests` | Maintenance does not gate this command. Enumerate `revisions/`, then `pending/`, then `dateien/`. Exact ordinal pending names referenced by **any** session stay. Live Dateien and other prefixes stay. Dry run makes no changes; `Kept` excludes unrelated prefixes. |
| `--cleanup-papierkorb [--dry-run]` | `PapierkorbCleanerTests` and `PapierkorbRecoveryTests.RestoringAfterSevenDaysRefusesAndTheCleanupFinishesTheRow` | Development only, paused during maintenance. Snapshot IDs strictly before the retention cutoff (default seven days); restore keeps its existing at-or-before refusal. Remove Dateien, then Lieder, then Auftritte through shared removal, not by prefix. The counts describe selected rows, not recursively removed children. |
| `--initialize-local-storage`, `--worker-smoke`, `/api/dev/exercise` | `LocalServices`; AppHost clean start | Private `archive-assets`, configured extraction/reading/video queues, initial emulator CORS without replacing existing rules. Smoke uses an owned Blob and unique scratch queue with W3C Producer → Consumer and actual SMTP; it cleans Blob then scratch queue, with a 15s cleanup budget independent of cancellation. HTTP exercise has a 30s work deadline and the host drains owned cleanup after disconnect. |
| `/api/dev/database`, `/api/dev/ai-budget`, `/api/dev/extraction-duplicate` | `DiagnosticEndpoints`; AppHost clean start, mixed batch, extraction roundtrip | Development only. Pending SQLx IDs are computed from compiled migrations and the applied-version catalog comment published by explicit `--migrate`; no history reads or extra grants for `archive_runtime`. AI totals use the existing ledger's month, spent and cap, with no provider invocation. Duplicate sends one extraction envelope; no queue gives 502, configured transport failure 500. |
| `--send-test-mail` | `SendTestMailAsync`, #222 operator/mail gates | Remains Development + explicit Azure provider + valid recipient. Smoke uses local SMTP instead. Do not deliver an external test message as an acceptance run. |

**Retained limits.** Frozen `FiniteJobs.IsDevelopmentRequired` refuses
Papierkorb cleanup outside Development even though the unchanged Bicep
schedules it in production. The video-job Restrict FK still blocks removal;
the command fails and does not delete that Datei's bytes or cascade the job.
An expired owner selected before the sweep removes its children as in the
frozen owner removal. A dropped-blob operator should run in a quiescent
maintenance window: it uses the frozen snapshot of session references, not
a transaction spanning the database and Blob Storage. The operational
diagnostics do not grant runtime access to SQLx bookkeeping tables.

### Review regressions

`ExpiryGrace` uses its own invariant TimeSpan binding rather than the storage
ticket-duration parser. The fixture `tests/fixtures/expiry-grace-bcl.json`
captures signed 100ns ticks or rejection from .NET 10.0.14, under `de-AT`,
using `ConfigurationBuilder.Bind` against the unchanged `AssetStorageOptions`.
Run `cargo test -p archive-server --locked --lib
exact_ticks_and_rejections_match_frozen_expiry_grace_binding`. To regenerate,
compile `tests/oracles/ExpiryGraceOracle.cs` in an external `net10.0` console
project with nullable/implicit usings, a `Microsoft.AspNetCore.App` framework
reference, and a reference to the freshly built frozen `Archive.Backend.dll`.
The oracle prints JSON; retain its input, tick and rejection rows. In addition
to leading-zero precision and midpoint rounding, the fixture includes the
BCL rejection caused by its limited rounding table. TimeSpan extrema bind,
but a cutoff outside the .NET calendar still refuses the cleanup.

Only the three cleanup commands accept trailing configuration overrides for
their existing settings (maintenance, grace/retention, database and Blob
configuration). The frozen argument forms `--Key=value`, `Key=value`,
`--Key value`, and `/Key value` are case-insensitive, last wins, and override
environment values; unset configuration keeps the existing Rust defaults.
Blank/malformed maintenance refuses work when Papierkorb reads it; the other
cleanups still don't read the flag. Put `--dry-run` **last**: in the frozen
configuration provider a bare long switch consumes the next token if present.
Retention cutoff must stay within the .NET calendar, just like the grace
cutoff. The native gate runs the actual frozen and Rust cleanup commands
against disposable rows and objects to check these boundaries. Frozen
`--cleanup-papierkorb` can prove the pause but cannot run the cleaner after
the flag binds false: `Program.cs` registers `PapierkorbCleaner` on the web
host only. The native Rust cases exercise the adopted retention behavior
from the frozen cleaner class; the oracle's unpaused process failure is
explicitly retained, not counted as a frozen cleanup pass.

The frozen `MaintenanceTests` HTTP payload/probe/auth/fallback matrix is in
`catalogue_maintenance.rs`; the normal Production forwarded-HTTPS
antiforgery control is in `auth_api.rs`.

The runtime-role diagnostic is exercised through the normal binary in
`cargo test -p archive-server --locked --test migrate_process
postgres_migration_contract -- --ignored`; use disposable PostgreSQL and
pg_dump 17 as in [the baseline runbook](../crates/archive-db/baseline/README.md).
No history on a fresh database reports every compiled ID pending. Existing
history without valid status metadata fails closed until a successful explicit
`--migrate`, which also publishes status on a migration no-op. The comment
records only successful SQLx version integers; it is not an authorization
signal, and an interrupted run may leave the last published status until
the next successful migration. Direct reads and writes (including comment
changes) remain denied to `archive_runtime`.

Inventur reads the shipped defaults plus optional `appsettings.json`, then
`appsettings.Development.json` beside the executable and finally environment
overrides, irrespective of the host environment. `--model` overrides the
deployment-name price key; otherwise the last fallback is `scripted`. Unknown
or unpriced models produce null EUR and a note. `.url` targets use UTF-8 by
default, honoring UTF-8, UTF-16 and UTF-32 BOMs as the frozen reader does; the
targets are never fetched. Inventory follows directory links under their
enumerated paths, even if the target lies outside the input root. A link to a
directory on the current ancestry is counted but not descended into; this is
an intentional finite boundary the frozen walk lacks. A dangling link fails the
walk (Rust exits 1 instead of an unhandled .NET exception). Review linked paths
before running on any non-fixture inventory. The frozen repeated-suffix failure on a
stem still ending in `)` after stripping a numeric suffix is not reinterpreted
as an additional duplicate candidate; the Rust command exits nonzero with a
bounded error.

Inventur retains decimal prices from JSON/environment, emits exact JSON
numbers, and formats its six-place summary directly from the decimal estimate.
The high-precision and midpoint controls can compare actual reports and the
summary to the frozen executable.

## Gates

From `src/archive/backend-rs`, use an isolated `CARGO_TARGET_DIR`:

```sh
cargo test -p archive-server --locked --test ops_http --test ops_cli
cargo test -p archive-server --locked --test ops_cli -- --ignored
```

For the ignored Inventur cases, set `Archive__Pdfium__LibraryPath` to the pinned
PDFium from [extraction parity](extraction-parity.md). Set
`ARCHIVE_234_FROZEN_DLL` and `ARCHIVE_234_DOTNET` to absolute paths to compare
the sanitized corpus, BOMs and acyclic link reports to a fresh frozen
executable. The native gate also uses those variables to compare cleanup grace
exit codes, rows and objects against the frozen command.
For that optional frozen comparison only, start the disposable Azurite with
`--skipApiVersionCheck`: the frozen Azure Blob SDK requests a version newer
than Azurite 3.35.0 accepts. Also run the Rust-only gate on strict Azurite.
Only generated and committed sanitized inputs belong in these gates.

`node tests/browser/ops-native.cjs` takes the built `ARCHIVE_BINARY` and explicit
`ARCHIVE_234_{PG,BLOB,QUEUE,SMTP,MAIL}_PORT`, `ARCHIVE_234_PG_PASSWORD` for
**dedicated fresh loopback fixtures**. Use PostgreSQL 17.6, Azurite 3.35.0 and
Mailpit 1.27.8; no local production settings are inherited by subprocesses.
Provide Node fixture libraries `pg`, `@azure/storage-blob`, `@azure/storage-queue`
and `protobufjs@8.8.1` via `NODE_PATH`. The capture helper needs a writable
`/tmp/opencode`; if necessary bind a private scratch directory there in a
private mount namespace. Reset only the dedicated fixture containers between runs. The harness uses the
product processes, a schema-aware local gRPC collector, and real SQL, Blob,
Queue and SMTP. The captured report must be redaction-clean after child-pipe
and collector drain.

The harness checks the exact seven pause messages and their log categories,
malformed pause configuration, Development refusals, successful ungated work,
three-signal success/failure exit codes, and cancellation with scratch resources
already created. HTTP probes cover status/cache/CSRF (including the frozen form
fallback), database pending before/after explicit migration, ledger aggregates,
and extraction duplicate delivery. The maintenance/control runs have no
configured hosted provider credentials. Existing member operator and worker
tests still own their feature-specific positive work and identity contracts.
