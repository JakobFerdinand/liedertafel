---
id: ARC-003
status: done
phase: core
kind: decision
depends_on: []
touches: ["neon-provisioning", "planning"]
external_inputs: ["neon-maintainer-access"]
---

# ARC-003 — Prove and choose the Neon provisioning path

**Depends on:** None.

## Outcome

A maintainer can create a disposable Frankfurt database through the selected
repeatable setup path and explain why OpenTofu is or is not worth adopting.

## Acceptance criteria

- [x] Recheck Neon Free limits, Frankfurt availability, and the candidate
  OpenTofu-compatible provider before testing; do not assume old defaults.
- [x] Exercise creation, inspection, update, and import on a disposable project;
  inspect replacement/deletion behaviour and organization/region selection.
- [x] Record an explicit decision: pin the proven provider/lock file, or provide
  a precise documented Neon setup. Neither path adds backup/recovery branches.
- [x] Specify runtime versus migration database roles and secure credential
  handoff. Provisioning API credentials never belong to the web runtime.
- [x] If adopting OpenTofu, specify protected Azure remote state, locking and
  bootstrap ownership; dispose of trial resources without publishing secrets.

## Verification

Repeat the selected setup on disposable resources and demonstrate an encrypted
PostgreSQL connection. Record lifecycle evidence and versions; redact credentials
from plans/logs and keep sensitive state outside committed files.

## Handoff and parallel work

ARC-011 consumes the selected setup and role contract. This trial can run beside
ARC-001/002/004. It settles a bounded provider decision, not all Azure deployment.

## Access and existing-project baseline — 2026-09-14

Authenticated read-only inspection with Neon CLI `4.17.3` succeeded. The
maintainer-access prerequisite is available in the local working environment.

| Input | Verified value |
| --- | --- |
| Organization | `org-round-tree-63490380` |
| Organization plan | Free |
| Project | `liedertafel-archiv` (`bitter-base-66886756`) |
| Region | AWS Frankfurt (`aws-eu-central-1`) |
| PostgreSQL version | 18 |
| Default branch | `production` (`br-frosty-moon-b2n9p446`), ready |
| Effective project permission | `ADMIN` |
| Creation source | Neon console |

Setup guidance: leave Neon Auth disabled. The archive owns invitations, email-code
verification and sessions using ASP.NET authentication; Neon supplies PostgreSQL.
Neon Auth is an optional application-user authentication service, not a requirement
for authenticated database connections. Its actual enabled/disabled state was not
checked during this read-only project inventory.

Reproduce the non-secret inventory with the locally authenticated CLI:

```bash
neon orgs list --output json
neon projects list --org-id org-round-tree-63490380 --output json
neon branches list --project-id bitter-base-66886756 --output json
```

Specify the organization explicitly: project listing otherwise prompts for an
organization, which is unsuitable for non-interactive runs.

The [current plan documentation](https://neon.com/docs/introduction/plans)
confirms Free includes 0.5 GB storage per project, 100 CU-hours and 5 GB public
transfer per project/month, with compute suspension after five idle minutes.
These are limits, not measured archive usage.

This establishes access and a project baseline, not completion of the trial.
Provider evaluation, disposable-resource creation/update/import/deletion evidence,
repeatable setup, an encrypted SQL connection test, and runtime/migration role
separation remain outstanding. Use a separate disposable project for lifecycle
testing; the existing `production` branch is not a trial deletion target. Check
the hosted PostgreSQL version against the local stack during the trial. Retrieve
credentials through local authentication/secret tooling, not committed output.

## Trial progress — 2026-09-14

- Documentation rechecked: [Free limits](https://neon.com/docs/introduction/plans)
  remain 0.5 GB/project, 100 CU-hours/project/month, 5 GB public transfer/month,
  five-minute suspension; currently 100 projects and 10 branches/project, with
  six-hour history capped at 1 GB of changes. [Frankfurt](https://neon.com/docs/introduction/regions)
  remains `aws-eu-central-1`; region cannot be changed in place.
- Candidate: community `kislerdm/neon` **0.18.0**, published in both Terraform
  and OpenTofu registries. Its [tagged project contract](https://github.com/kislerdm/terraform-provider-neon/blob/v0.18.0/docs/resources/project.md)
  supports explicit organization, region, PostgreSQL version and project import.
  Set history to **21600 seconds**, rather than the provider's one-day default;
  use `primary_compute` for actual compute settings.
- Local PostgreSQL
  is **17.6** while the existing hosted project is **18**. Trial new projects on
  major **17** to match the local application contract; leave the existing project
  intact and resolve its reuse explicitly in the ARC-011 handoff.
- Trial state, saved plans and credentials stay in a private temporary directory
  outside the checkout. Only allowlisted, non-secret evidence enters this plan.

## Decision and verified checkpoint — 2026-09-14

**Select documented Neon CLI/API provisioning; do not adopt OpenTofu for this
single-database scope.** The [setup runbook](../../../infrastructure/neon/README.md)
specifies the selected settings, credential handling, role bootstrap, explicit
migrations, grants and disposable cleanup. No provider/lock file or Azure remote
state is adopted. The conditional remote-state criterion is therefore not
applicable; trial resource disposal was verified separately.

OpenTofu's basic lifecycle works, but maintaining credential-bearing state and a
second infrastructure tool adds little value for one project. SQL role/grant
management remains necessary. The provider is community maintained and its
[upstream README](https://github.com/kislerdm/terraform-provider-neon/blob/v0.18.0/README.md)
announces transition to `neondatabase/neon`; this is another maintenance input,
not evidence that its successor has been proven. Future adoption needs a fresh
decision, pinned provider/lock file, and a maintainer-owned Azure Blob backend
with protected access, encryption, locking and no runtime access.

### Versions and lifecycle evidence

- Neon CLI **4.17.3**; OpenTofu **1.12.6**, Linux amd64 archive SHA-256 checked
  against upstream: `5dc43da4f750f33873dc25e94587128709e819e544b7be9016b255316153c3a8`.
- `registry.opentofu.org/kislerdm/neon` **0.18.0**, installed with provider
  signature key ID `D2D88CF3ABDC92DA`. Trial lock/state remained private and local.
- psql **17.6** from `postgres:17.6`, host CA bundle mounted read-only; actual Neon
  server **17.11**. This matches local PostgreSQL **17.6** at major-version level.
- Organization explicitly `org-round-tree-63490380`, Free; all trial projects
  `aws-eu-central-1`, PG17, six-hour history, one `production` branch, one `archive`
  database, bootstrap role `archive_admin`, one read-write endpoint. Neon Auth
  reported **not configured** on the inspected branches.

| Exercise | Observed result |
| --- | --- |
| Initial provider creation | `odd-credit-18877099` created, then HTTP **412**, `modifying the suspend interval is not permitted on this account`, when `primary_compute.suspend_timeout_seconds = 300` was applied. State retained a tainted resource. |
| Free-compatible retry | Setting timeout to **0** (plan default, five minutes) produced a tainted-resource delete/create plan. Applied only to the disposable target; replacement `nameless-darkness-65991725` succeeded. Subsequent plan exit **0**, no-op. |
| In-place update | Renamed to `arc003-tofu-updated-20260914` and raised primary min CU **0.25 → 0.5** (max 0.5). Plan `update`; same project/endpoint IDs; subsequent plan exit **0**. |
| Import | `tofu state rm neon_project.trial`, then `tofu import neon_project.trial nameless-darkness-65991725`, with matching name/min-CU inputs. Subsequent plan exit **0**, no-op. |
| Replacement inspection | Separate region, organization and PG-version changes each returned plan exit **2**, actions `delete, create`, replacement path respectively `region_id`, `org_id`, `pg_version`. These hypothetical changes were not applied. |
| Deletion safeguard | Destroy preview contained one delete. Adding `lifecycle { prevent_destroy = true }` made destroy planning fail with exit **1**. Removed only for disposal of the trial. |
| Provider deletion | `tofu destroy` succeeded; independent organization project listing confirmed the project absent. |
| Selected API path, first run | `twilight-leaf-21284678`: explicit API creation, SQL bootstrap, EF migration, runtime permission checks and migration repeat passed; deletion independently confirmed. |
| Selected API path, repeat | `polished-voice-89501841`: same setup and SQL checks passed on another fresh project; deletion independently confirmed. |
| Final SQL guard verification | `royal-lab-90916628`: final guarded SQL scripts, password-retrieval contract, permission checks and EF migration repeat passed; deletion independently confirmed. |

Provider trial configuration used `neon_project.trial` with explicit `org_id`,
`region_id`, `pg_version = 17`, `history_retention_seconds = 21600`, initial
`branch { name = "production", database_name = "archive", role_name = "archive_admin" }`
(attributes on separate HCL lines), and `primary_compute` min/max **0.25/0.5**,
timeout **0**. Commands were `tofu init`, saved `plan`/`apply`, update, state removal/
import, replacement previews and destroy. Only action summaries were published;
raw JSON state/plans contain passwords even without explicit outputs.

### SQL and credential evidence

- All successful SQL trials used **`verify-full`** with trusted CA roots.
  `psql \conninfo` reported **TLSv1.3 / TLS_AES_256_GCM_SHA384**. Npgsql's actual
  archive migration process connected with **`SSL Mode=VerifyFull`**.
- `dotnet run --project src/archive/backend --no-launch-profile -- --migrate`
  ran as `archive_migrator`, applied **`20260911201343_InitialArchive`**, and
  succeeded again without reapplying it. Credentials were injected privately via
  `ConnectionStrings__archive-migrations`; the process used Production mode.
- [Bootstrap SQL](../../../infrastructure/neon/bootstrap-roles.sql) creates SQL
  logins `archive_migrator` and `archive_runtime`, rather than API-created roles
  with `neon_superuser` membership. Both audited false for superuser, createdb,
  createrole, replication, bypass-RLS and `neon_superuser` membership.
- [Post-migration grants](../../../infrastructure/neon/runtime-grants.sql) run
  **as migrator**, establishing defaults for future tables/sequences and excluding
  EF history from runtime privileges. An attempted admin-owned default-privilege
  bootstrap failed; its transaction rolled back. The selected scripts avoid
  granting extra role membership to work around that restriction.
- A scratch identity-column table created **after** default grants allowed runtime
  INSERT/sequence use, UPDATE, SELECT and DELETE. Runtime CREATE/ALTER/DROP TABLE,
  CREATE ROLE, SET ROLE migrator, SELECT/DELETE EF history, and CREATE TEMP TABLE
  each failed with insufficient privilege. The final run also verified wrong-role
  script guards and denied runtime execution of a newly created function.
- Runtime receives only `ConnectionStrings__archive-db`; the separate migration
  process receives only its migration connection. Admin and provisioning credentials
  remain maintainer-only. The runbook specifies password-manager/Key Vault handoff,
  hidden password prompts and the existing bounded connection-pool contract.
- Trial organization API key **3336282** was revoked after provider destruction.
  All five created project IDs above were removed (including the failed creation's
  replaced ID); the existing PG18 project was never a mutation/deletion target.

### Completion — 2026-09-14

Final runbook review passed: the runbook matches the guarded SQL scripts and
the recorded evidence, including private credential capture, the reveal-password
contract, wrong-role script guards, `verify-full` TLS, explicit migrator-only
migrations, post-migration grants, and disposable-only cleanup. The
architecture's OpenTofu decision section now records the rejection, and ARC-011
consumes the runbook with a fresh PG17 project, separate migrator/runtime roles,
no OpenTofu state, and an explicit PG18 reuse decision gate. All acceptance
criteria and verification steps above are satisfied; no backup/recovery branches
were added and no secrets were committed.
