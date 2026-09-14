# Archive PostgreSQL on Neon

**ARC-003 selects documented Neon CLI/API provisioning for this single-database
scope and rejects OpenTofu adoption.** Completed lifecycle evidence and SQL
acceptance results are in
[ARC-003](../../docs/plans/006-choir-archive/ARC-003-neon-provisioning-trial.md).
This runbook is the ARC-011 provisioning and database-role handoff.

## Decision and fixed inputs

Use an authenticated maintainer with **Neon CLI 4.17.3**, Bash and `jq`. Commands
below run from the repository root. Verify `neon --version`; obtain this version
using the [CLI installation guide](https://neon.com/docs/reference/cli-install).
Use the maintainer's existing login (`neon auth` if needed), never an application
identity. Do not put Neon API credentials in the archive deployment.

| Setting | Selected value |
| --- | --- |
| Organization / plan | `org-round-tree-63490380` / **Free** |
| Region | AWS Frankfurt, `aws-eu-central-1` |
| PostgreSQL | **17**, matching local **17.6** at major-version level; Neon manages minor updates |
| Branch / database / bootstrap owner | `production` / `archive` / `archive_admin` |
| Compute | 0.25–0.5 CU, direct read-write endpoint |
| History retention | 21600 seconds (six hours) |
| Suspend setting | `0`: use the Free plan's five-minute idle suspension |
| Neon Auth | Disabled; ASP.NET owns application authentication |

The existing `liedertafel-archiv` project **`bitter-base-66886756` runs PG18**.
Leave it untouched. Do not automatically reuse it, its branch or its endpoint;
any reuse/version reconciliation requires an explicit ARC-011 decision.

Free limits rechecked for ARC-003 on 2026-09-14: 100 projects, 10 branches/project,
0.5 GB storage/project, 100 CU-hours/project/month, 5 GB public transfer/project/month,
and six-hour history capped at 1 GB of changes. Recheck the
[current limits](https://neon.com/docs/introduction/plans) before provisioning.
History retention is not a separately provisioned backup/recovery branch.

### Why not OpenTofu?

The trial used **OpenTofu 1.12.6** and community provider **`kislerdm/neon` 0.18.0**:

- A 300-second suspend timeout failed with **HTTP 412 after creating the project**,
  leaving a tainted resource; `0` succeeded on Free.
- Rename and minimum-CU updates passed; import followed by a no-op plan passed.
- Organization, region and PostgreSQL-version changes planned **delete/create**.

For one database, credential-bearing state, state-bootstrap/maintenance ownership,
and the transitioning upstream provider outweigh the benefit. SQL role and grant
management is still required. Provider reproduction and lifecycle evidence are
linked from ARC-003; this directory does not introduce HCL or a provider lock file.
No remote state is needed for the rejected path. Future adoption requires a new
decision covering a protected Azure Blob backend, locking and a named bootstrap
owner before any shared state is created.

## 1. Reconcile inventory, then create once

Choose a recognizable, non-sensitive name, for example
`liedertafel-archive-arc003-<date>-<operator>-<suffix>` for a disposable trial.
Use lowercase letters, digits and hyphens. Record the chosen name and resulting
ID in the operator's inventory; a name is **not** an idempotency key.

```bash
ORG_ID=org-round-tree-63490380
PROJECT_NAME='liedertafel-archive-arc003-REPLACE-WITH-UNIQUE-SUFFIX'
neon projects list --org-id "$ORG_ID" --output json
neon orgs list --output json | jq '.[] | {id, plan}'
```

Replace the placeholder and inspect the list before proceeding. If a prior
attempt may already have created this project, reconcile its ID/settings first.
Never automatically create another project on a timeout, HTTP error or malformed
response. In particular, a failed create can still leave a real project behind.

Run the remaining commands in a dedicated Bash session with tracing disabled.
The create response contains passwords and connection URIs: capture **both output
streams privately**, outside the checkout; never print, `tee`, commit or upload
the response. The successful create block emits only the project ID.

```bash
set +x
set -o pipefail
umask 077
PRIVATE_DIR="$(mktemp -d /tmp/archive-neon.XXXXXXXX)" || exit 1
trap 'unset PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD PGSSLMODE PGSSLROOTCERT PGCONNECT_TIMEOUT PSQL_HISTORY; rm -rf -- "$PRIVATE_DIR"' EXIT

if neon api /projects -X POST \
  -F project.name="$PROJECT_NAME" \
  -F project.org_id="$ORG_ID" \
  -F project.region_id=aws-eu-central-1 \
  -F project.pg_version=17 \
  -F project.branch.name=production \
  -F project.branch.database_name=archive \
  -F project.branch.role_name=archive_admin \
  -F project.history_retention_seconds=21600 \
  -F project.default_endpoint_settings.autoscaling_limit_min_cu=0.25 \
  -F project.default_endpoint_settings.autoscaling_limit_max_cu=0.5 \
  -F project.default_endpoint_settings.suspend_timeout_seconds=0 \
  --output json >"$PRIVATE_DIR/create.json" 2>"$PRIVATE_DIR/create.stderr"; then
  PROJECT_ID="$(jq -er '.project.id | strings | select(length > 0)' \
    "$PRIVATE_DIR/create.json")" || exit 1
  jq -r '.project.id' "$PRIVATE_DIR/create.json"
else
  printf '%s\n' 'Creation failed or is ambiguous. List and reconcile before retrying.' >&2
  exit 1
fi
```

On any ambiguous outcome, run `neon projects list --org-id "$ORG_ID" --output json`
again in a fresh session and inspect candidates. Do not paste private diagnostics
into tickets. Continue only after selecting the intended project ID explicitly.

## 2. Inspect the exact project, branch and endpoint

These allowlisted views avoid publishing a complete API response:

```bash
: "${PROJECT_ID:?Set the explicitly reconciled project ID}"
neon api "/projects/$PROJECT_ID" --output json |
  jq '.project | {id, name, org_id, region_id, pg_version,
    history_retention_seconds, default_endpoint_settings}'
neon api "/projects/$PROJECT_ID/branches" --output json |
  jq '.branches[] | {id, name, default, current_state}'
neon api "/projects/$PROJECT_ID/endpoints" --output json |
  jq '.endpoints[] | {id, branch_id, type, host, current_state,
    autoscaling_limit_min_cu, autoscaling_limit_max_cu, suspend_timeout_seconds}'

BRANCH_ID='br-REPLACE-FROM-INSPECTION'
ENDPOINT_ID='ep-REPLACE-FROM-INSPECTION'
DIRECT_HOST='REPLACE-WITH-THAT-ENDPOINT-HOST'
```

Verify the organization, PG17, Frankfurt, six-hour retention, `production` branch,
and that the selected **read-write** endpoint belongs to that branch and has the
actual 0.25–0.5 CU / `0` settings. Project defaults alone are insufficient evidence.
An idle endpoint may be suspended. Use the direct hostname, without `-pooler`.
In the Neon console, select this same project and branch and confirm **Neon Auth
is not enabled**, or run `neon neon-auth status --project-id "$PROJECT_ID" --branch "$BRANCH_ID"`
(expected: `Neon Auth is not configured for this branch`). Record only non-secret settings
and IDs. CLI authentication and SQL password authentication remain required.

## 3. Bootstrap SQL roles and set passwords privately

This step additionally needs a standard installed `psql` client (17 recommended)
and system CA certificates. The trial used the `postgres:17.6` client container
with the host CA bundle mounted read-only; the commands below use installed psql.

Required contract for [`bootstrap-roles.sql`](bootstrap-roles.sql), run as
`archive_admin` against `archive`:

- Keep `archive_admin` as database owner. Create `archive_migrator` and
  `archive_runtime` as SQL `LOGIN` roles **without passwords** initially, without
  `neon_superuser` membership or elevated role/database creation privileges.
  Use SQL rather than console/API role creation, which grants Neon-specific
  elevated membership to created roles.
- Revoke database and `public` schema privileges from `PUBLIC`, including public
  database `CREATE`/`TEMP`; grant the two application roles `CONNECT` and schema
  `USAGE`. Only the migrator receives schema `CREATE`.
- The migrator owns the EF-created objects. Runtime receives table
  `SELECT, INSERT, UPDATE, DELETE` and sequence `USAGE, SELECT` in step 4, including
  default grants **for objects created by `archive_migrator`**. Runtime has no DDL,
  ownership, migrator membership or grant options.

Keep shell tracing off. Retrieve the admin password into a private response file
and then the libpq environment, never a command-line argument. Do not print it or
run an environment dump. These environment variables belong only to this short-lived
maintainer session; use a trusted single-user machine.

```bash
unset PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD PGSSLMODE PGSSLROOTCERT PGCONNECT_TIMEOUT PSQL_HISTORY PGHOSTADDR PGSERVICE PGSERVICEFILE PGOPTIONS
export PGHOST="$DIRECT_HOST" PGPORT=5432 PGDATABASE=archive PGUSER=archive_admin
export PSQL_HISTORY=/dev/null PGSSLROOTCERT=system PGSSLMODE=verify-full
export PGCONNECT_TIMEOUT=15
neon api "/projects/$PROJECT_ID/branches/$BRANCH_ID/roles/archive_admin/reveal_password" \
  --output json >"$PRIVATE_DIR/admin.json" 2>"$PRIVATE_DIR/admin.stderr" || exit 1
PGPASSWORD="$(jq -er '.password | strings | select(length > 0)' \
  "$PRIVATE_DIR/admin.json")" || exit 1
export PGPASSWORD
psql -X --set=ON_ERROR_STOP=1 --file=infrastructure/neon/bootstrap-roles.sql
```

Stop on bootstrap failure; inspect/reconcile existing roles rather than resetting
them blindly. Open an interactive session only after successful bootstrap:

```bash
psql -X --set=ON_ERROR_STOP=1
```

At the `psql` prompt, verify the encrypted connection and use hidden password
prompts (generate separate strong passwords in the password manager first):

```text
\conninfo
\password archive_migrator
\password archive_runtime
\q
```

`PSQL_HISTORY=/dev/null` suppresses `.psql_history`; `-X` skips local startup files.
Do not use literal `ALTER ROLE ... PASSWORD '...'`, URI arguments, shell literals
or pasted SQL logs for passwords. Hand secrets directly into the password manager;
ARC-011 will install the appropriate secrets into Key Vault. Preserve the bootstrap
admin credential there too if retained; it is not an application credential.

```bash
unset PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD PGSSLMODE PGSSLROOTCERT PGCONNECT_TIMEOUT PSQL_HISTORY
```

## 4. Explicit migrations, then runtime grants

Use these **Npgsql** templates, with correctly escaped secret values, stored via
secret tooling rather than pasted into shell history. Both use the direct endpoint:

```text
# Separate migration process: ConnectionStrings__archive-migrations
Host=<direct-host>;Port=5432;Database=archive;Username=archive_migrator;Password=<secret>;SSL Mode=VerifyFull;Maximum Pool Size=5;Minimum Pool Size=0;Timeout=5;Command Timeout=10;Keepalive=0

# API/runtime process: ConnectionStrings__archive-db
Host=<direct-host>;Port=5432;Database=archive;Username=archive_runtime;Password=<different-secret>;SSL Mode=VerifyFull;Maximum Pool Size=5;Minimum Pool Size=0;Timeout=5;Command Timeout=10;Keepalive=0
```

These preserve the [archive connection contract](../../src/archive/README.md#resources-and-configuration-contract).
Install trusted CA roots in the client environment; never bypass certificate or
hostname validation. The API receives **only** `ConnectionStrings__archive-db` for
PostgreSQL: no admin/migration password, `ConnectionStrings__archive-migrations`,
Neon API key or maintainer login. It never migrates at startup.

Supply `ConnectionStrings__archive-migrations` through the separate migration
process's environment, then run the existing explicit command:

```bash
dotnet run --project src/archive/backend --no-launch-profile -- --migrate
```

After **every successful explicit migration**, before starting/resuming runtime,
run [`runtime-grants.sql`](runtime-grants.sql) **as `archive_migrator`**, not admin
or runtime. This helper grants DML on existing application tables and sequence
access, installs default privileges as the migrator, revokes public function
execution, and **revokes ALL runtime privileges
on `public."__EFMigrationsHistory"`** after the broad table grants. Default grants
alone would expose that table. Do not start runtime if migration or grant repair
fails. Coordinate this step with runtime downtime so intermediate grants are not
exposed to an active application.

```bash
unset PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD PGSSLMODE PGSSLROOTCERT PGCONNECT_TIMEOUT PSQL_HISTORY PGHOSTADDR PGSERVICE PGSERVICEFILE PGOPTIONS
export PGHOST="$DIRECT_HOST" PGPORT=5432 PGDATABASE=archive
export PGUSER=archive_migrator PGSSLMODE=verify-full
export PGSSLROOTCERT=system PSQL_HISTORY=/dev/null
psql -X -W --set=ON_ERROR_STOP=1 --file=infrastructure/neon/runtime-grants.sql
unset PGHOST PGPORT PGDATABASE PGUSER PGSSLMODE PGSSLROOTCERT PSQL_HISTORY PGPASSWORD
```

`-W` prompts for the migrator password; no URI/password is passed in argv. Neon
admin cannot alter default privileges for these SQL-created roles without extra
membership. Keep this step under the migrator login rather than adding membership.

For a disposable verification, use the same connection settings with each login
(`PGUSER=archive_runtime` or `archive_migrator`) and `psql -X -W`. As migrator,
create a scratch table **after** running the grants script to test future grants:

```sql
CREATE TABLE public.arc003_probe (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, value text NOT NULL);
```

As runtime, `\conninfo` must report TLS, and these operations must succeed:

```sql
INSERT INTO public.arc003_probe(value) VALUES ('trial') RETURNING id;
UPDATE public.arc003_probe SET value = 'updated';
SELECT * FROM public.arc003_probe;
DELETE FROM public.arc003_probe;
```

Run each negative check separately; each must fail with insufficient privilege:

```sql
CREATE TABLE public.forbidden(id int);
ALTER TABLE public.arc003_probe ADD COLUMN forbidden int;
DROP TABLE public.arc003_probe;
CREATE ROLE forbidden;
SET ROLE archive_migrator;
SELECT * FROM public."__EFMigrationsHistory";
DELETE FROM public."__EFMigrationsHistory";
CREATE TEMP TABLE forbidden(id int);
```

As migrator, drop `public.arc003_probe` and repeat the explicit migration command;
it must exit successfully without reapplying the migration. Do not run scratch
checks against live application data. Neither role should have superuser,
createdb, createrole, replication, bypass-RLS or `neon_superuser` membership.

## 5. Disposable trial cleanup

Delete only an ID explicitly recorded as **disposable** in the trial inventory.
Never select deletion targets by name prefix, delete a whole list, or target
`bitter-base-66886756`. Reconcile any project left by a failed create first.

```bash
neon projects list --org-id "$ORG_ID" --output json
DISPOSABLE_PROJECT_ID='REPLACE-WITH-VERIFIED-DISPOSABLE-ID'
: "${DISPOSABLE_PROJECT_ID:?}"
test "$DISPOSABLE_PROJECT_ID" != bitter-base-66886756 || exit 1
# Run only after matching this ID to the disposable trial inventory and list:
neon api "/projects/$DISPOSABLE_PROJECT_ID" -X DELETE --output json
neon projects list --org-id "$ORG_ID" --output json
```

Confirm the disposable ID is absent and the existing PG18 project remains.
Re-list after an ambiguous delete response; do not infer success from a timeout.
Exit the dedicated shell to remove its private temporary response directory via
the trap. Remove trial-only secrets from the password manager after verified
deletion; retained database secrets follow the ARC-011 handoff.

## References

- [Neon CLI](https://neon.com/docs/reference/neon-cli) and
  [connection strings](https://neon.com/docs/reference/cli-connection-string)
- [Create-project API](https://api-docs.neon.tech/reference/createproject),
  [regions](https://neon.com/docs/introduction/regions), and
  [Free plan limits](https://neon.com/docs/introduction/plans)
- [Manage roles / SQL-created role privileges](https://neon.com/docs/manage/roles)
  and [secure connections](https://neon.com/docs/connect/connect-securely)
- [Trial provider's tagged project contract](https://github.com/kislerdm/terraform-provider-neon/blob/v0.18.0/docs/resources/project.md)
- [ARC-003 decision and trial evidence](../../docs/plans/006-choir-archive/ARC-003-neon-provisioning-trial.md)
