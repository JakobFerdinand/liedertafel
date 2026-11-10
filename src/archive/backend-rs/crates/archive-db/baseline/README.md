# Frozen EF / Neon baseline (#220)

## Provenance and public-fixture review

The maintainer authorized a read-only Neon acquisition on 2026-11-09. This is
the actual `public` schema, **not** a locally invented EF baseline. Acquisition
used transaction read-only mode and verified TLS, with:

```text
pg_dump --schema-only --schema=public --no-owner --no-privileges
```

- Source PostgreSQL: **17.11 (9045acd)**; pg_dump: **17.6**.
- Frozen EF oracle at repository revision `fff0d59`: **44** migrations, ending
  at `20261028084325_ColdStorage`, EF product version **10.0.12**.
- Raw authorized dump SHA256:
  `b2b1896f76e270cebba7683e41bbd2f663f737f5df930314f6f90a09a358019c`.
- Reviewed `neon-schema.sql` SHA256:
  `8df20577001a390bb9a42fd237cf3a3e2d094c9beb52a967b1ba1f4f948ee246`.

Review found only DDL, structural comments and pg_dump session settings: no
member/application rows, hostnames, credentials, ACLs or owners. The fixture
removes **only** the two transient `\restrict` / `\unrestrict` lines. All schema
definitions, comments, column order, defaults, identity sequences, constraints,
indexes and foreign-key actions are preserved. `provenance.json` is the supplied
read-only EF history metadata, reformatted as JSON; `ef-history.sql` contains
only those 44 migration identifiers/product versions, never application data.

Keep this oracle frozen independently of Rust migrations. A failing comparison
is not permission to regenerate the oracle from the implementation. Replacing
the baseline requires a separately sanctioned acquisition and review.

## Migration semantics

`0001_frozen_ef_baseline.sql` was derived from that reviewed dump. It omits dump
session settings and the `CREATE SCHEMA public`/standard-schema comment wrapper
(a fresh PostgreSQL database already has `public`). It retains all application
DDL and adds the proven EF history metadata for a fresh database. No old
data-reset EF migrations run against an existing estate.

For an existing EF database, migration 1 validates **all 44 history rows** and
a PG17 semantic catalog fingerprint, then adopts the schema without modifying
its data or EF history. Missing/extra history, schema drift and unrelated
nonempty schemas fail closed. `catalog-fingerprint.sql` documents the query
embedded in migration 1; its expected result in `catalog-fingerprint.txt` was
computed on a **local database restored from the Neon fixture**. This check
ignores ownership/ACLs, OIDs, physical dropped-column slots, table data and
sequence current values. It includes tables/columns/types/defaults, constraints,
indexes, identity sequence definitions/dependencies, comments, functions,
triggers, policies, rules and public-schema extensions. It is an adoption guard,
**not** the independent schema-parity acceptance check below.

There is one explicitly verified PG17 representation difference: the original
EF `IN ('literal', ...)` constraints on `dateien.Kind`, `dateien.StorageTier`
and `recordings.Kind` deparse with `ANY ((ARRAY[...::varchar])::text[])`;
restoring that pg_dump text yields `ANY (ARRAY[(...::varchar)::text, ...])`.
Both have identical comparison semantics. A second exact fingerprint in
`catalog-fingerprint-frozen-ef.txt` was obtained by executing the **actual frozen
44-migration EF script locally**, not by guessing a Neon baseline. Full pg_dump
diffing found only those three equivalent cast expressions. Adoption accepts
these two exact fingerprints; the native tests retain each shape. No general
cast normalization or schema-parity exclusion was added.

Rust bookkeeping is `public._sqlx_migrations`; frozen EF bookkeeping stays
`public."__EFMigrationsHistory"`. SQLx checks applied migration checksums;
reruns preserve history rows. Future changes add new Rust migrations rather
than modifying migration 1 or the EF oracle.

## Native operator command

Build from `src/archive/backend-rs` with `cargo build --workspace --locked`, then
run `target/debug/archive-server --migrate`. Supply connections through the
environment, **not argv**:

- `ConnectionStrings__archive-migrations` takes precedence.
- `ConnectionStrings__archive-db` is the unchanged local-admin fallback.
- Both accept PostgreSQL URLs or the documented Npgsql connection string
  (`Host`, `Port`, `Database`, `Username`, `Password`, `SSL Mode`, optional
  `Root Certificate`, and existing pool/timeout/keepalive settings).
- Quoted Npgsql passwords support semicolons and doubled single/double quotes.
  URL values require normal percent encoding. Unsupported settings fail rather
  than silently weakening TLS. `VerifyFull` remains hostname-and-CA verification;
  system CA roots are used unless a root certificate is supplied.
- One unpooled connection, five-second connect budget, ten-second statement/lock
  timeout, no connection retry or database keep-alive query. All failures exit
  nonzero without printing connection values or database error details.

`ARCHIVE_ENVIRONMENT=Development` requires OTLP just as the origin does. The
finite process registers service defaults, records a `migrate` span/completion
event and flushes/shuts telemetry down on success and handled failure. HTTP
startup never invokes migrations; `PORT`/static export are irrelevant to this
command. AppHost/production command retargeting is deliberately deferred to the
later orchestration/cutover tickets; the C# release remains unchanged.

Production role bootstrap remains `infrastructure/neon/bootstrap-roles.sql`:
the runtime gets only its own connection, never migration credentials.
`archive_runtime` is explicitly refused by `--migrate`. As in the frozen
`RuntimeGrants`, application grants run only as `archive_migrator`, on database
`archive`; local-admin runs skip them. Migration/grant work holds SQLx's advisory
lock until the single-use connection closes. New Rust history creation/revocation
commits atomically so existing broad default grants cannot expose it even on
failed adoption.

`runtime-grants.sql` is byte-identical to the canonical infrastructure helper
(and the untouched frozen C# embedded copy). The process drops its psql meta
line, then delays its final `COMMIT` until the additional Rust-history revocation
has executed. Thus both history tables remain inaccessible when broad runtime
DML grants commit. Grant repair also runs on migration no-ops. Stop runtime for
the migration maintenance window and resume only after exit code 0. The canonical
manual fallback alone predates Rust history; if used manually for a Rust estate,
also revoke ALL from `archive_runtime` on `public._sqlx_migrations` **before the
same transaction commits**.

## Independent local acceptance gate

Additive #225 migration 0004 creates only the explicit nondeterministic root ICU
collation `public.archive_culture`. The schema test adds independently written
expected collation DDL to its restored oracle database before full comparison;
this fixture and the baseline adoption fingerprint remain unchanged. The exact
collation shape/ownership/runtime use and denied runtime DDL are checked, and
`event_culture` proves native prior-Rust-head upgrade/rerun data preservation.
See `../../../docs/events-review-remediation.md` for equality/secondary-key and
independent frozen culture controls. This is not historical EF upgrade support.

The native process suite translates `tests/archive/backend/RuntimeGrantsTests.cs`
and adds #220's schema/CLI cases. It restores `neon-schema.sql` into a separate
oracle database and compares complete `pg_dump --schema-only` output from both
databases, excluding the new Rust history table and the four additive #221
auth tables (`archive_sessions`, `archive_csrf`, `archive_ceremonies`,
`archive_auth_allocation_budgets`). Their DML,
constraints and behavior are exercised by the auth/real-role suites described in
`../../../docs/auth-parity.md`; the frozen oracle itself is unchanged. Both dumps use the
same pg_dump 17 binary; only transient restrict tokens are normalized. Ownership
and grants are tested separately, not erased from an ACL acceptance check.

Use a **dedicated throwaway PostgreSQL 17 server**, never a live database. The
suite needs a superuser connection URL in `ARCHIVE_TEST_POSTGRES`, `pg_dump` 17
on PATH, and libpq password configuration (`PGPASSWORD`/private pgpass) for the
dump client. It creates databases `archive` and `archive_oracle` plus the three
`archive_*` roles. Existing names cause failure rather than being dropped. It
removes only its own successful fixtures; on failure inspect the disposable
server or replace that container before rerunning.

From the workspace:

```bash
cargo test -p archive-server --locked --test migrate_process
# With the above local-only environment configured:
cargo test -p archive-server --locked --test migrate_process \
  postgres_migration_contract -- --ignored --nocapture
```

The DB gate is explicitly `#[ignore]`, not an early-return test counted as passed.
For remote Podman without host pg_dump, set `ARCHIVE_TEST_PG_DUMP` to a local
executable wrapper forwarding arguments to `podman exec` in your disposable
Postgres container, forwarding `PGDATABASE`, `PGUSER`, `PGPASSWORD`; inside the
container use its local socket instead of the host-mapped port. Files can be
streamed over stdin; host bind mounts are not required. No Neon access is needed.

Cases: fresh schema parity, exact EF metadata, unchanged Rust-history reruns,
concurrent reruns, existing-EF adoption retaining synthetic account/passkey data,
checksum drift, incomplete history, default/check/index/sequence drift, unrelated
schema preservation, real canonical bootstrap roles, runtime DML and sequence
use, future default grants, denied DDL/TEMP/role switching/function execution,
both histories denied, grant repair on rerun and protected history after failed
adoption with pre-existing defaults. Ungated process cases cover finite failure,
credential redaction and the Development OTLP requirement.
