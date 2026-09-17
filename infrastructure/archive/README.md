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
`../../../.github/workflows/release-archive.yml` owns the image
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
   `workflow_dispatch` (placeholder image) — then immediately run
   `release-archive.yml`.
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

- Release: run `release-archive.yml` (`workflow_dispatch`, input `version`),
  approve in `archive-prod`, keep the digest from the run summary.
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
  from the previous run summary (or `gh run list --workflow release-archive.yml`),
  follow this file; no Azure portal rights beyond Reader are needed.

## Handoff outputs (for ARC-011/012)

- Bicep outputs: `environmentDefaultDomain`, `appFqdn`, `vaultUri`,
  `runtimeIdentityPrincipalId`, `customDomainBound`.
- Release inputs: image digest (`ghcr.io/jakobferdinand/liedertafel-archive@sha256:…`).
- Deliberately absent here (later slices): storage/queues, Neon wiring,
  Blob-persisted Data Protection keys, and the hosted mail wiring (ARC-011
  consumes the ARC-010 sender/identity outputs below). The shell boots
  dependency-free; DB-backed endpoints answer German 500 Problem Details
  until ARC-011 configures `ConnectionStrings__archive-db`.

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
