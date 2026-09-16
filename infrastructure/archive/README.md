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
   --query properties.customDomainVerificationId -o tsv`). Afterwards set
   `bindCustomDomain=true` in `main.bicepparam` and re-run the infra workflow
   to issue/bind the managed certificate.

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
  Blob-persisted Data Protection keys, ACS email. The shell boots
  dependency-free; DB-backed endpoints answer German 500 Problem Details
  until ARC-011 configures `ConnectionStrings__archive-db`.
