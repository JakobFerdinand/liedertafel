---
id: ARC-011
status: in_progress
phase: core
kind: slice
depends_on: ["ARC-003", "ARC-009", "ARC-010"]
touches: ["auth", "azure-foundation", "neon-provisioning", "release-workflow"]
external_inputs: ["azure-maintainer-access", "neon-maintainer-access", "pilot-mailboxes"]
---

# ARC-011 — Sign in to the hosted archive and survive a restart

**Depends on:** [ARC-003](ARC-003-neon-provisioning-trial.md),
[ARC-009](ARC-009-first-cloud-release.md),
[ARC-010](ARC-010-azure-login-email.md).

## Outcome

A maintainer signs into the live archive using a real email code and remains
signed in when Azure replaces the container or serves a request from another replica.

## Acceptance criteria

- [ ] Provision a fresh Neon Free Frankfurt PG17 project through the selected
  documented path in `infrastructure/neon/README.md`; apply existing
  auth migrations explicitly with the separate `archive_migrator` role, run
  `runtime-grants.sql` as migrator after each migration, and give the API only
  the `archive_runtime` connection. Do not reuse the existing PG18 project
  (`bitter-base-66886756`) without an explicit version-reconciliation decision.
- [ ] Persist the production key ring in private Blob Storage, protect it with
  Key Vault, and wire runtime credentials/sender permissions securely.
- [ ] Bootstrap only intended pilot accounts and exercise the hosted private
  shell against Neon; no local identity or development key leakage is allowed.
- [ ] Verify key retention/rotation handling for valid cookies, cross-instance
  challenge/session state, bounded cold-start connection handling, and revocation.
- [ ] Verify shared Service Defaults do not introduce database-dependent liveness,
  production exports to a developer dashboard or mandatory AppHost hosting.
- [ ] Create no OpenTofu state: ARC-003 rejected OpenTofu for this scope.
  Keep provisioning API credentials and the admin database credential
  inaccessible to the runtime; the API receives only
  `ConnectionStrings__archive-db`.

## Verification

Complete sign-in, restart/replace the app, and repeat across two replicas. Test
expired/reused codes, unauthorized API access and a failed database connection.
Record actual cold-start timings and ensure no database-dependent keep-alive probe.

## Handoff and parallel work

This is the first hosted identity/database slice; it consumes the ARC-003
decision and the `infrastructure/neon/` runbook for provisioning and
database roles. It does not wait for catalogue
features. It enables live jobs, maintenance releases, and storage integrations.
Coordinate shared configuration changes with ARC-009/010 owners.

## Implementation progress — 2026-09-17 (branch `feat/arc-011-hosted-persistent-sign-in`)

Backend (`be85fea`):

- New `Auth/DataProtectionConfiguration.cs`: Development keeps ignored
  filesystem keys (`Development:KeysPath`); outside Development the key ring
  persists in a private Blob object wrapped by a Key Vault RSA key
  (`Authentication:KeysBlobUri` + `Authentication:KeysKeyVaultKeyUri`, both
  required https absolut URIs, `Authentication:KeysPath` placeholder
  forbidden). Credential is `ManagedIdentityCredential(AZURE_CLIENT_ID)` on
  the host, `DefaultAzureCredential` fallback for local prod-like checks.
  Client construction is lazy, so `/alive` stays dependency-free. New
  `Authentication:AllowEphemeralKeysForTests` escape is test/CI-only; real
  deployments leave it unset.
- Packages: `Azure.Extensions.AspNetCore.DataProtection.Blobs 1.5.4`,
  `Azure.Extensions.AspNetCore.DataProtection.Keys 1.6.4`.
- `Program.cs` calls the helper and logs both URIs (never secrets) at
  startup. `check-archive.yml` and the README smoke use the ephemeral
  escape; production smoke stays dependency-free.

Infrastructure (`a6d3b42`, what-if preview green, Modify/Create only, no
Delete/Replace):

- `main.bicep` adds storage account `stliedertafelarchive` (name checked
  available, Entra-only, no anonymous blob), private container
  `dataprotection`, Key Vault RSA key `dataprotection-wrap` (2048,
  wrap/unwrap), `Storage Blob Data Contributor` (`ba92f5b4-…`) plus
  `Key Vault Crypto User` (`12338af0-…`) for `id-archive-app`, Key Vault
  references `archive-db-connection` / `archive-operator-token`, and env
  wiring `ConnectionStrings__archive-db` (secretRef),
  `Archive__OperatorToken` (secretRef), `AZURE_CLIENT_ID`,
  `Authentication__KeysBlobUri`, `Authentication__KeysKeyVaultKeyUri`
  (versionless key URI so rotation needs no Bicep change).

Verification (local, image `liedertafel-archive:arc011`, podman):

- `dotnet test tests/archive/backend` **106/106 green** (97 prior + 9 new
  `DataProtectionConfigurationTests`: missing/partial/non-https URIs throw,
  legacy `KeysPath` forbidden, ephemeral escape round-trips without I/O,
  dev filesystem keys intact). `dotnet build src/archive/Archive.slnx` and
  `dotnet build liedertafel.slnx` green; `az bicep build` green.
- Production smoke (ephemeral escape): `/alive` Healthy, `/api/build`
  `development=false` with baked version, `/api/dev/database` 404, unknown
  `/api/*` 404 `application/problem+json`, `/system/status/` archive
  content, non-root, no node.
- Guard proof: production boot without key URIs and without the escape
  fails fast with `Produktion erfordert Authentication:KeysBlobUri und
  Authentication:KeysKeyVaultKeyUri als https-URIs.`

Remaining (live, needs maintainer-visible execution): fresh Neon PG17
provision → migrate/grants → Key Vault secrets → infra deploy → image
release → pilot bootstrap → restart/two-replica persistence, expired/reused
codes, unauthorized access, failed-DB, cold-start timings, no keep-alive
probe. No OpenTofu state is created by any step above.

## Live provisioning and pre-release verification — 2026-09-17

Neon (runbook `infrastructure/neon/README.md` §§1–4, `psql` via a local
`postgres:17.6` image carrying the host CA bundle because the stock image
fails `verify-full` here; SQL piped via stdin since host mounts are not
visible to the container runtime):

- Fresh project `liedertafel-archive-prod` (`muddy-leaf-09559586`): PG17,
  `aws-eu-central-1`, six-hour history, 0.25–0.5 CU, `0` suspend,
  `production` branch + active read-write endpoint, Neon Auth not
  configured. Existing PG18 `bitter-base-66886756` untouched (only listed).
- `bootstrap-roles.sql` as `archive_admin` committed; `\conninfo` reports
  TLSv1.3 / TLS_AES_256_GCM_SHA384 under `verify-full`.
- One process incident: the first migrator password was exposed in a local
  shell error echo (bash `export` rejects `-` in
  `ConnectionStrings__archive-migrations`). It was never used against the
  database (the migration failed before connecting) and was immediately
  rotated via admin; all later invocations use `env`. No secret entered the
  repo.
- All existing auth migrations applied explicitly as `archive_migrator`
  (`--migrate`, Production mode); `runtime-grants.sql` as migrator;
  scratch-table future-grant probe passed (runtime INSERT/UPDATE/SELECT/
  DELETE ok); all eight negative checks denied (DDL, role admin, `SET ROLE
  migrator`, EF-history read/write, temp tables); both roles show no
  superuser/createdb/createrole/replication/bypass-RLS and no extra
  memberships; scratch dropped; `--migrate` repeat clean.

Azure (deployment `arc011-keys-and-wiring`, `Succeeded`; what-if and the
workflow destructive-change guard clean — Modify/Create only):

- `archive-db-connection` (runtime Npgsql string) and
  `archive-operator-token` (`openssl rand -base64 32`) placed in
  `kv-liedertafel-archive` (versioned IDs recorded in the deployment
  session only). Maintainer `Secrets Officer` grant added for the operator
  identity (one-off, documented in `infrastructure/archive/README.md`).
- Reported outputs: key ring
  `https://stliedertafelarchive.blob.core.windows.net/dataprotection/keys.xml`,
  wrapping key
  `https://kv-liedertafel-archive.vault.azure.net/keys/dataprotection-wrap`
  (versionless). Live env audit: only the seven intended names (mail ×2,
  runtime DB, operator token, `AZURE_CLIENT_ID`, both key URIs) — no
  `OTEL_*`, no migrator/admin/Neon-API material, no `KeysPath`, no test
  escape. Probes `/alive`-only, scale 0–2, image digest preserved
  (`ab48b050…`, `0.2.0`).
- Pilot bootstrap 2026-09-17 through the **runtime** connection only:
  `Bootstrap administrator ensured for domain gmail.com`; DB row verified
  (confirmed Administrator, exactly one user). No local identity or dev key
  entered production.

Hosted findings on the still-live `0.2.0` image (pre-ARC-011 code):

- `/api/auth/me` without cookie → `authenticated:false`; revision restart
  is zero-downtime; `/alive` stays dependency-free by construction.
- **Latent hosted defect found and fixed on this branch (`021f17a`):**
  Container Apps terminates TLS at the front proxy, so Kestrel sees plain
  HTTP and antiforgery (`SecurePolicy.Always`) threw
  `InvalidOperationException` from `CheckSSLConfig` — every hosted code
  request answered 500 (Log Analytics type+frames evidence). Fix:
  `UseForwardedHeaders` (XForwardedFor/Proto, front proxy trusted) first
  in the pipeline. New test `HostedForwardedHttpsServesAntiforgeryWithout
  ServerFailure` fails without the fix (500) and passes with it (German
  400); full suite **107/107 green**.
- Failed-DB drill against the fixed image locally (unreachable host,
  ephemeral keys): `/alive` 200 during the outage; DB-backed POST answers
  the German 500 with traceId after ~6 s (5 s Npgsql timeout, no retry
  storm, no detail leak).

Still inbox- and release-gated (maintainer): merge this branch, run
`release-archive.yml` (builds the fixed image, deploys by digest, shell
smoke), then with the pilot mailbox complete a real code sign-in, restart
the revision, scale to two replicas, and exercise expired/reused codes plus
revocation. Expired/reused/attempt-limit paths themselves are covered by
the 107 green backend tests on the same engine.
