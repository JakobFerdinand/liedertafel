# Archive cloud release (ARC-009)

Empty-shell release path for `archiv.liedertafel-mining.at` on scale-to-zero
Container Apps. Topology lives in Bicep; the running image is selected only
through an explicit release run, by immutable digest.

| File | Purpose |
| --- | --- |
| `subscription.bicep` | Subscription scope: creates `RG-Liedertafel-Archive` (idempotent) |
| `main.bicep` | Group scope: logs, environment, app (0–2 replicas), vault, runtime identity, optional managed certificate |
| `main.bicepparam` | Non-secret defaults; image + pull credential are injected per run |

`../../../.github/workflows/infra-deploy-archive.yml` owns topology (what-if
preview, destructive-change guard, image pass-through).
`../../../.github/workflows/archive.yml` owns the image
(build/check/push to private GHCR, deploy by digest, shell smoke checks).

## One-off bootstrap (maintainer, subscription Owner)

1. Create the Entra app `sp-liedertafel-archive-iac` with federated
   credentials `archive-gha-release`
   (`repo:JakobFerdinand/liedertafel:environment:archive-prod`) and optional
   `archive-gha-infra` (`ref:refs/heads/main`), audience
   `api://AzureADTokenExchange`. Grant it `Contributor` plus
   `User Access Administrator` (or group `Owner`) on the archive group after
   step 2 — plain `Contributor` cannot assign the data-plane roles.
   Create the separate `sp-liedertafel-archive-preview` app with only the
   `pull_request` federated credential and group-scoped `Contributor`; store
   its client ID as `AZURE_CLIENT_ID_ARCHIVE_PREVIEW`. PR what-if uses this
   identity and a fake GHCR password, so neither role-assignment permission
   nor the real pull credential enters a PR job. It previews with
   `deployRoleAssignments=false`; role diffs are code-reviewed and applied by
   the privileged deploy (`workflow_dispatch` previews with `true`).
2. Create the empty group once as subscription Owner (the release identity
   has no subscription-level rights by design):
   `az deployment sub create --location austriaeast --template-file infrastructure/archive/subscription.bicep --parameters archiveResourceGroupName=RG-Liedertafel-Archive --parameters resourceGroupLocation=austriaeast`
   (plain `az group create -n RG-Liedertafel-Archive -l austriaeast` works too).
   Grant the app group-scoped `Contributor` plus `User Access Administrator`
   (or group `Owner`). Then run `infra-deploy-archive.yml` once via
   `workflow_dispatch`    (placeholder image) — then immediately run
   `archive.yml` (workflow_dispatch).
3. Repo settings: environment `archive-prod` with required reviewers; secrets
   `AZURE_CLIENT_ID_ARCHIVE` / `AZURE_TENANT_ID_ARCHIVE` /
   `AZURE_SUBSCRIPTION_ID_ARCHIVE` plus `GHCR_PULL_PAT_ARCHIVE`.
4. DNS at World4You (recheck before changing; preserve unrelated TXT):
    `archiv` CNAME → environment default domain (workflow/Bicep output
    `environmentDefaultDomain`), then `asuid.archiv` TXT → env verification id
    (`az containerapp env show -g RG-Liedertafel-Archive -n cae-liedertafel-archive
    --query properties.customDomainConfiguration.customDomainVerificationId -o tsv`).
    Afterwards bind in two runs (certificate issuance requires the attached
    hostname, so one atomic run fails with `RequireCustomHostnameInEnvironment`):
    first push with `bindCustomDomain=true` / `bindManagedCertificate=false`
    in `main.bicepparam` to attach the hostname, then flip
    `bindManagedCertificate=true` and push again to issue/bind the managed
    certificate.

## Routine operation

- Release: merges to `main` touching `src/archive/**` or `global.json` run
  `archive.yml` automatically; or run it via `workflow_dispatch` (input
  `version`). The check runs first, then the release job — approve in
  `archive-prod` and keep the digest from the run summary.
- Infrastructure change: normal PR; what-if comment appears; merge deploys.
  The workflow preserves the deployed image — verify the digest in
  `/api/build` afterwards.
- Verification (per ARC-009): two distinguishable digests, selected version
  visible, idle scale-down to 0 plus a cold request (~60 s acceptable), infra
  reapply without image change.

## Pull-credential ownership and renewal

- Owner: primary maintainer. Credential: fine-grained PAT (`packages:read`
  on `liedertafel-archive` only), stored as `GHCR_PULL_PAT_ARCHIVE`; expiry
  ≤ 1 year with a calendar reminder.
- Renewal: create a new PAT, update the secret, re-run the infra workflow
  (rotates the Container Apps registry secret; running revision unaffected
  until the next release, which pulls with the new credential).
- Second maintainer path: approve the `archive-prod` run, pick the digest
   from the previous run summary (or `gh run list --workflow archive.yml`),
  follow this file; no Azure portal rights beyond Reader are needed.

## Handoff outputs (for ARC-011/012)

- Bicep outputs: `environmentDefaultDomain`, `appFqdn`, `vaultUri`,
  `runtimeIdentityPrincipalId`, `runtimeIdentityClientId`, `keysBlobUri`,
  `keysKeyVaultKeyUri`, `customDomainBound`.
- Release inputs: image digest (`ghcr.io/jakobferdinand/liedertafel-archive@sha256:…`).
- Deliberately absent here (later slices): member-file storage/queues
  (ARC-049). Hosted sign-in (ARC-011) is wired: Blob key ring, Key Vault
  wrapping key, Neon runtime connection and operator token. The shell still
  boots dependency-free; DB-backed endpoints answer a German 500 Problem
  until the `archive-db-connection` secret is placed.

## Email sending (ARC-010, send-only)

`email.bicep` / `email.bicepparam` own the send-only mail path: Email Service
`liedertafel-archive` (Europe), custom domain `liedertafel-mining.at` with
sender username `archiv` (`archiv@liedertafel-mining.at`), linked
Communication Service `acs-liedertafel-archive` (Europe), and the
`Communication and Email Service Owner` grant for `id-archive-app` on the
Communication Service. Stage 1 (`linkDomain=false`) is deployed
(`email-arc010`, 2026-09-17); the domain link flips on after DNS verification.

Deploy/refresh stage 1 (no secrets; the principal ID is the identity object
ID, not a credential):

```bash
ARCHIVE_RUNTIME_PRINCIPAL_ID="5a43cf72-429e-4cd1-b055-2fdff4b07988" \
  az deployment group create --resource-group RG-Liedertafel-Archive \
  --template-file infrastructure/archive/email.bicep \
  --parameters infrastructure/archive/email.bicepparam --name email-arc010
```

### DNS at World4You (maintainer, recheck before changing)

Enter these Azure-generated records for the apex `liedertafel-mining.at`
(send-only: no MX record is issued or required):

| Host | Type | Value |
| --- | --- | --- |
| `liedertafel-mining.at` | TXT | `ms-domain-verification=71ecb910-8ba7-4cf6-a4ca-0f39942434a0` |
| `liedertafel-mining.at` | TXT (merge into the single apex SPF TXT) | `v=spf1 include:spf.protection.outlook.com -all` |
| `selector1-azurecomm-prod-net._domainkey` | CNAME | `selector1-azurecomm-prod-net._domainkey.azurecomm.net` |
| `selector2-azurecomm-prod-net._domainkey` | CNAME | `selector2-azurecomm-prod-net._domainkey.azurecomm.net` |

After 15–30 minutes propagation, trigger verification out of band and watch
all four reach `Verified`:

```bash
for t in Domain SPF DKIM DKIM2; do
  az communication email domain initiate-verification \
    --domain-name liedertafel-mining.at --email-service-name liedertafel-archive \
    --resource-group RG-Liedertafel-Archive --verification-type $t
done
az communication email domain show -g RG-Liedertafel-Archive \
  --email-service-name liedertafel-archive --domain-name liedertafel-mining.at \
  --query verificationStates
```

Then stage 2: set `linkDomain = true` in `email.bicepparam` and re-run the
deployment above. Linking an unverified domain fails with
`DomainValidationError` by design (observed 2026-09-17).

### Sending notes for ARC-011 and the pilot

- Runtime sends keyless via `EmailClient(endpoint,
  DefaultAzureCredential)` with endpoint
  `https://acs-liedertafel-archive.europe.communication.azure.com` (Europe
  geography regionalizes the host). With the user-assigned identity, the
  container needs `AZURE_CLIENT_ID=2f6f5bc6-b441-4ed4-abfa-c2e53f994547`;
  ARC-011 wires this with the remaining runtime configuration.
- Quotas are subscription-wide and shared with the unrelated alpakasoelde
  sender: 30 sends/min, 100 sends/hour for custom domains. API acceptance
  (`Succeeded`) never promises inbox arrival.
- Local integration run (telemetry stays in Aspire): set AppHost user secrets
  `Mail:Provider=Azure`, `Mail:AzureConnectionString` (from
  `az communication list-key -g RG-Liedertafel-Archive -n
  acs-liedertafel-archive`, never committed) and `Archive:MailTestRecipient`,
  then start the explicit `archive-mail-test` resource. Ordinary startup stays
  on Mailpit capture.
- Pre-link rejection evidence (2026-09-17): data-plane send from the unlinked
  domain answers `404 DomainNotLinked`, nothing delivered.

## Hosted persistent sign-in (ARC-011)

`main.bicep` additionally owns: storage account `stliedertafelarchive`
(Entra-only, no anonymous blob) with private container `dataprotection`,
Key Vault RSA wrapping key `dataprotection-wrap` (2048, wrap/unwrap), and
`Storage Blob Data Contributor` plus `Key Vault Crypto User` for
`id-archive-app`. The container receives `ConnectionStrings__archive-db`
and `Archive__OperatorToken` as Key Vault references, `AZURE_CLIENT_ID`,
and the non-secret `Authentication__KeysBlobUri` /
`Authentication__KeysKeyVaultKeyUri` (versionless key URI: rotation needs
no Bicep change). Probes stay `/alive`-only; scale stays 0–2. The backend
fails fast in Production without both key URIs, refuses the pre-ARC-011
`Authentication:KeysPath` placeholder, and honors `X-Forwarded-Proto`
(Container Apps terminates TLS at the front proxy; without it every hosted
antiforgery POST dies with an SSL 500 — observed live 2026-09-17).

Live Neon project (provisioned 2026-09-17 per `infrastructure/neon/`):
`liedertafel-archive-prod` (`muddy-leaf-09559586`), PG17, Frankfurt,
`production` branch, 0.25–0.5 CU, six-hour history, Neon Auth disabled.
The existing PG18 project is untouched (no version-reconciliation
decision). Credential model: the admin password is never stored — reveal it
per maintenance window via the Neon API with maintainer CLI auth; the
migrator password is ephemeral per window (set via admin, migrate, grants,
discard); only the `archive_runtime` connection persists, as Key Vault
secret `archive-db-connection`. The API never receives migrator/admin
credentials or Neon API keys. No OpenTofu state exists for any of this.

### Maintainer one-off: vault secret management

The vault uses RBAC and grants the runtime only Secrets User. A human
maintainer needs Secrets Officer once (replace the object ID with the
second maintainer for shared ownership):

```bash
az role assignment create --assignee <maintainer-object-id> \
  --role "Key Vault Secrets Officer" \
  --scope "$(az keyvault show -g RG-Liedertafel-Archive -n kv-liedertafel-archive --query id -o tsv)"
```

Place (or rotate) the two runtime secrets — values never enter the repo;
rotation needs no Bicep change, then restart the revision:

```bash
az keyvault secret set --vault-name kv-liedertafel-archive \
  --name archive-db-connection --value "$NEON_RUNTIME_CONNECTION" --output none
az keyvault secret set --vault-name kv-liedertafel-archive \
  --name archive-operator-token --value "$OPERATOR_TOKEN" --output none
az containerapp revision restart -g RG-Liedertafel-Archive -n ca-liedertafel-archive
```

`$NEON_RUNTIME_CONNECTION` is the `archive_runtime` Npgsql string
(`SSL Mode=VerifyFull`, pool max 5 / min 0, timeout 5, command timeout 10,
keepalive 0). Generate `$OPERATOR_TOKEN` with `openssl rand -base64 32`
and hand it to the second maintainer out of band.

### Pilot bootstrap and hosted verification

Bootstrap uses the runtime connection only (least privilege proven
2026-09-17); never the migrator/admin credential:

```bash
env "ConnectionStrings__archive-db=$(az keyvault secret show \
    --vault-name kv-liedertafel-archive --name archive-db-connection \
    --query value -o tsv)" \
  "Archive__OperatorToken=$(az keyvault secret show \
    --vault-name kv-liedertafel-archive --name archive-operator-token \
    --query value -o tsv)" \
  dotnet run --project src/archive/backend --no-launch-profile -- \
  --bootstrap-admin --email <pilot-address> --name "<display>" \
  --operator "<name>" --operator-token "$(az keyvault secret show \
    --vault-name kv-liedertafel-archive --name archive-operator-token \
    --query value -o tsv)"
```

Hosted checklist after the ARC-011 image release: request/enter a real code
at `https://archiv.liedertafel-mining.at`, restart the revision and confirm
the session survives, scale to two replicas and repeat, then exercise
expired/reused codes, revocation (logout/member admin kills tickets),
unauthorized `/api/auth/me` (`authenticated:false`), and the bounded German
500 on DB failure. Pre-release evidence 2026-09-17: unauthenticated `/me`
correct, CSRF-less POST rejected, `/alive` 200 during a total DB outage
with the DB-backed POST failing bounded (~6 s, German 500 with traceId),
probes `/alive`-only, no `OTEL_*` env (no prod export to Aspire), image
carries no AppHost. Inbox-dependent steps (real code, restart/replica
persistence, revocation) run with the pilot mailbox after release.

## Controlled database releases (ARC-012)

Releases carrying a schema change run inside a maintenance window owned by
`archive.yml`. Code-only releases (`workflow_dispatch` with
`run_migration=false`, for already-applied or schema-free changes) skip the
window; push-triggered runs always migrate.

### Operator sequence

1. Review the migration (`src/archive/backend/Data/Migrations/`) and keep
   the change additive where possible (new tables, new nullable columns,
   new indexes). Destructive changes (renames/drops, new `NOT NULL`
   without default, type changes) break the previous image — split them
   across releases (additive first, cleanup later).
2. Place the per-window migration credential (maintainer, never committed):
   reveal the Neon admin password via the Neon API with maintainer CLI
   auth, set an ephemeral `archive_migrator` password, and store its Npgsql
   string as Key Vault secret `archive-migrations-connection`
   (`az keyvault secret set --vault-name kv-liedertafel-archive --name
   archive-migrations-connection --value "$MIGRATOR_CONNECTION" --output
   none`). Only the `archive_runtime` connection persists; this secret is
   discarded after the window.
3. Run `archive.yml` (`workflow_dispatch`, inputs `version` plus
   `run_migration=true`) and approve in `archive-prod`. The workflow then:
   enters maintenance (`Archive__MaintenanceMode=true`, members see the
   German banner/`/wartung/`, conflicting API work answers 503), runs the
   selected image's `--migrate` exactly once with the migration role,
   deploys the digest, proves the window on the new revision, reopens
   access, and smoke-checks (`/alive`, `/api/build`,
   `/api/maintenance:false`, `/system/status/`, `/wartung/`,
   `/api/antiforgery:200`, dev-absence, unknown-API 404).
4. Discard the migrator password (rotate via the Neon API) and delete the
   `archive-migrations-connection` secret version.

Competing runs serialize on the `archive-prod` concurrency group: a second
release waits instead of migrating alongside the first. EF records applied
versions in the migration history, so a queued run or a replay is a safe
no-op — an applied migration never runs twice.

### Credentials

- The release identity (`sp-liedertafel-archive-iac`) needs one additional
  grant to read the per-window credential (maintainer, one-off):
  `Key Vault Secrets User` on `kv-liedertafel-archive`. It never receives
  the admin credential or Neon API keys.
- Ordinary container startup never migrates: the app image has no
  migration credential and `Program.cs` applies nothing on boot.
  `--migrate` inside the release step is the only production migration
  path.

### Failure handling and rollback

- Migration failure: the run fails with the `[migrate]` diagnostics and
  **maintenance stays active**. Inspect, fix forward in code (new
  migration/image), and retry the release. Never retry blindly and never
  restore the database.
- Failed pre-open checks keep maintenance active the same way. A failed
  final smoke after reopening means fix-forward with a new release.
- Rollback is code-only: redeploy the previous digest (a new release run
  or `az containerapp update --image`), valid only while that image stays
  schema-compatible (additive changes). There is deliberately no database
  restore/export and no backup job; editor trash and revision history
  remain the ordinary recovery path.

### Job contract (handoff to ARC-032/035)

`GET /api/maintenance` (`{ maintenance, message }`, `no-store`) is the
machine-readable window state; `Archive:MaintenanceMode` carries the same
flag in configuration. Future extraction/import jobs must read it before
conflicting work and pause while `maintenance` is true. Probes plus
`/api/build` and `/api/maintenance` always stay available for observers.
