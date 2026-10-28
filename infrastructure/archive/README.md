# Archive cloud release (ARC-009)

Empty-shell release path for `archiv.liedertafel-mining.at` on scale-to-zero
Container Apps. Topology lives in Bicep; the running image is selected only
through an explicit release run, by immutable digest.

| File | Purpose |
| --- | --- |
| `subscription.bicep` | Subscription scope: creates `RG-Liedertafel-Archive` (idempotent) |
| `main.bicep` | Group scope: logs, environment, app (0–2 replicas), queue-triggered extraction job (ARC-034), queue-triggered AI reading job and its scheduled dispatch sweep (ARC-034-1), vault, identities, optional managed certificate |
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
3. Repo settings: environment `archive-prod` (branch policy on main, no
   required reviewers); secrets
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
  `version`). The check runs first, then the release job runs without an
  approval gate — keep the digest from the run summary.
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
- Second maintainer path: pick the digest from the latest `archive-prod` run
   (or `gh run list --workflow archive.yml`),
   follow this file; no Azure portal rights beyond Reader are needed.

## Handoff outputs (for ARC-011/012)

- Bicep outputs: `environmentDefaultDomain`, `appFqdn`, `vaultUri`,
  `runtimeIdentityPrincipalId`, `runtimeIdentityClientId`, `keysBlobUri`,
  `keysKeyVaultKeyUri`, `assetsServiceUri`, `assetsQueueServiceUri`,
  `aiEndpoint`, `customDomainBound`.
- Release inputs: image digest (`ghcr.io/jakobferdinand/liedertafel-archive@sha256:…`).
- Deliberately absent here (later slices): the import job wiring (ARC-037
  reuses `assetsQueueServiceUri`) and the score-text search surface
  (ARC-035). Hosted sign-in (ARC-011) is wired: Blob key ring, Key Vault
  wrapping key, Neon runtime connection and operator token. Member files
  (ARC-049) and the extraction queue (ARC-034) are wired; the shell still
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

## Hosted private files (ARC-049)

`main.bicep` adds to the same storage account: private container
`archive-assets` (param `assetsContainerName`, matches the backend default)
and blob CORS for the app origins (custom domain and the environment default
FQDN; methods GET/HEAD/PUT) so the browser can transfer blocks directly. The
app receives the non-secret `Archive__Assets__ServiceUri` account endpoint.
`BlobAssetStorageAdapter` then signs user-delegation SAS tickets through the
runtime managed identity (`AZURE_CLIENT_ID`); no storage key or connection
string exists in the hosted app, and shared-key access stays disabled. The
Azurite connection-string path is unchanged for local runs. Ticket and copy
failures surface as German 502 ProblemDetails (`Speicherdienst nicht
erreichbar.`), never as raw provider errors. The runtime identity's
account-scoped `Storage Blob Data Contributor` role also covers the editor's
Cold-storage tier moves (#148, `SetAccessTier`): no separate tier job
identity, role or scheduled task exists — every transition is an editor
action against the single `archive-assets` container. Hosted verification
still open: one real upload/finalize/read through the custom domain after the
next release (ARC-049 acceptance), plus one real tier move with a Cold
retrieval read and an early-deletion charge check (maintainer step).

## Archive chat path (ARC-021, Azure OpenAI / Microsoft Foundry)

`main.bicep` additionally owns Azure OpenAI account `aoai-liedertafel-archive`
(kind `AIServices`, custom subdomain = account name, system-assigned identity,
`disableLocalAuth` so no API key exists) and chat deployment `gpt-6-luna`
(`gpt-6-luna`, version `2026-09-22`, `DataZoneStandard`, 30k TPM; ARC-021-1,
replacing `gpt-5-4-mini` / `gpt-5.4-mini` `2026-03-17`). The 30k TPM limit is
a burst backstop behind the application's EUR 15 cap, not a spending control:
saturated, it costs about 1 EUR per hour at the EU Data Zone rates
(0.1056 / 0.528 EUR per million input / output tokens). The
Foundry project `liedertafel-archive` (future tooling only; inference happens
at the account-level deployments) is created by the deploy workflow's
"Create Foundry project (CLI path)" step — the ARM child-resource path from
IaC is blocked by the RP ("you must enable a managed identity on your
resource", documented IaC limitation in microsoft-foundry discussion #313),
so the idempotent CLI step runs after the Bicep deploy. The account sits in
`germanywestcentral` (param `aiLocation`), not the group's `austriaeast`:
Austria East is not an EU Data Zone region for Azure OpenAI, and the default
chat model must stay inside the EU Data Zone.
The model version is pinned; changing it requires a re-run of the chat
evaluation and a pricing re-check first (`OnceCurrentVersionExpired` keeps
the pin until the provider expires the version). `id-archive-app` receives
`Cognitive Services OpenAI User` on the account (`deployRoleAssignments`
guarded), so the app talks to the endpoint keylessly via
`Archive__Chat__*` env entries — chat enabled from first deploy (maintainer
decision 2026-09-23; sole production user pre-release). Since ARC-022-3
the application enforces a hard EUR 15 monthly cap for all AI calls
(`Archive:Ai`, see `src/archive/README.md`); the earlier EUR 5 alert with
manual disable no longer exists, `Archive__Chat__Disabled=true` remains the
manual kill switch. Model prices are application settings: if
`aiChatDeploymentName` is ever overridden, both
`Archive__Ai__Models__<deployment>__InputPricePerMillionEur` and
`…__OutputPricePerMillionEur` must be set on the app, otherwise every chat
run is refused (`unpriced_model`). A test keeps the `aiChatDeploymentName`
default and the shipped `Archive:Ai:Models` keys in step.

**Switching to Luna (ARC-021-1).** The Luna deployment is a new resource;
incremental mode leaves the old `gpt-5-4-mini` deployment in place (a rollback
path, and its quota stays used). `Archive__Chat__DeploymentName` is the
deployment resource's own name, so the container app depends on the deployment
in the compiled template. A push of this template starts
`infra-deploy-archive.yml` and switches the app to `gpt-6-luna`, so the image
that prices `gpt-6-luna` (stage A, `src/archive/**`) must already be released:
never push both changes together, the infrastructure workflow would run first
and the old image would refuse every chat run (`unpriced_model`). Order: stage A
push (after one `ChatEvaluationLive` against the existing deployment, see the
ARC-021-1 ticket), check `OpenAI.DataZoneStandard.gpt-6-luna` quota in
`germanywestcentral`, stage B push (what-if: Create the deployment, Modify the
app; no Delete/Replace), live evaluation on Luna (exact commands in the ticket),
then delete the old deployment with
`az cognitiveservices account deployment delete -g RG-Liedertafel-Archive
-n aoai-liedertafel-archive --deployment-name gpt-5-4-mini` and its price
entry. A group budget backstop (EUR 10/month,
alert-only, ARC-004 review trigger) is intentionally NOT in Bicep: the
Consumption budgets API rejects programmatic creation on this subscription
with 401 even for the subscription Owner (finding recorded in ARC-043,
2026-09-23), so the alert is portal-managed via Cost Management budgets.

## Extraction queue path (ARC-034)

The extraction queue `archive-extraction` lives on the SAME storage account
as the key ring and member files (`stliedertafelarchive`): one account owns
blobs and queues. Bicep declares the queue (`extractionQueue` in
`main.bicep`): KEDA cannot start the job while the queue is missing and the
app's Message Sender role cannot create it, so a worker-provisioned queue
would never appear on a fresh account. `CreateIfNotExists` in
`--extract-queue` and `--dispatch-extraction` remains as a no-op safeguard,
and there is no maintainer queue-creation step.
The endpoint travels as `Archive__Extraction__QueueServiceUri` (Bicep output
`assetsQueueServiceUri`; ARC-037's import job reuses it) and authenticates
Entra-only — no storage key or connection string exists anywhere, and
shared-key access stays disabled account-wide.

Scale behavior (frozen contract): KEDA — the Container Apps scale rule —
polls ONLY the queue, never Neon. The rule passes
`queueLengthStrategy: 'visibleonly'` through its metadata: messages hidden
by visibility backoff or an active lease do not activate an idle job; a
message becoming visible again re-triggers processing on a later poll.
[KEDA's Azure Storage Queue scaler](https://keda.sh/docs/2.21/scalers/azure-storage-queue/)
uses Peek for this count (the supported value is `visibleonly`, not
`visible`). At 32 or more visible messages it falls back to counting all
messages, so this is an idle/backoff guarantee, not an exact-once execution
guarantee under a large backlog. Container Apps accepts these scaler keys
in [event job scale-rule metadata](https://learn.microsoft.com/azure/container-apps/jobs#event-driven-jobs).
`ja-archive-extract` runs the same backend
image as the app with `--extract-queue` as a finite run: when a receive
round comes back empty, the run exits, so an idle deployment creates no
executions and issues no database queries (min executions 0, max 10).
Retries are the worker's own bounded attempt ladder (visibility backoff,
stale-lease re-take), so the platform retry limit is 0; the 30-minute
replica timeout is generous headroom over the worker's 10-minute drain
budget, and the 1 Gi memory cap bounds a runaway parse end-to-end (OOM kill
resolves through the stale-lease ladder).

Identities (RBAC on the storage account, guarded by `deployRoleAssignments`
like every other role assignment):

| Identity | Role | Purpose |
| --- | --- | --- |
| `id-archive-app` (runtime) | `Storage Queue Data Message Sender` | finalize dispatches the extraction envelope inline (best-effort); nothing more |
| `id-archive-extract` (job) | `Storage Queue Data Contributor` | worker's `CreateIfNotExists` safeguard (Bicep declares the queue), the dispatch sweep sends, the drain receives/processes/deletes — and KEDA's queue-length poll needs the `queues/read` action, which the narrower Message Sender/Processor roles lack |
| `id-archive-extract` (job) | `Storage Blob Data Reader` | bounded PDF reads via `OpenReadAsync` |

The extraction identity deliberately holds no Key Vault, key-ring, mail or
chat roles. `ja-archive-extract` carries both identities, but
`identitySettings` marks the runtime identity `None` — unavailable inside
the container — and uses it only for the platform's Key Vault resolution of
`archive-db-connection` (the app's secret pattern, reused verbatim);
`AZURE_CLIENT_ID` pins `DefaultAzureCredential` to the extraction identity
for the queue and blob calls. Telemetry needs no extra role: the job's
console logs stream to the shared Log Analytics workspace through the
environment's `appLogsConfiguration`, and the app sets no `OTEL_*` env in
production either.

Release and image pass-through: `archive.yml` pins the app and the
extraction job to the SAME digest (`az containerapp update --image`, then
`az containerapp job update --image` — in-flight executions keep the
previous image and are resolved by the stale-lease ladder). Infrastructure
re-runs resolve the app's deployed image once and pass it to both (shared
`containerImage` param), preserving the deployed image across sequential
re-runs. Infra deploys share the release's `archive-prod` serialization
group, so image capture and deployment cannot interleave a release.

Bootstrap on a fresh account: deploying infrastructure creates the queue,
so finalize's inline send and the queue scale rule are self-sustaining from
the first upload. Rows that stayed un-enqueued (inline send failed, or they
predate the queue) are only picked up by a job run: the next queue-triggered
execution sweeps them, or start one by hand (`az containerapp job start -n
ja-archive-extract -g RG-Liedertafel-Archive`).
Maintenance windows share `Archive__MaintenanceMode` between the app and
job, declared by the same `maintenanceMode` topology parameter. Infra runs
preserve the app's live value and apply it to both resources. The release
workflow sets both flags before migrations, then stops running extraction
executions with `az containerapp job stop --name ... --resource-group ...`
and confirms they have quiesced (no confirmation flag is needed). In-flight
executions cannot see env updates; interrupted runs recover through the
stale-lease ladder. New executions read `Archive:MaintenanceMode` first and
exit 0 without receiving a message or querying the database, so KEDA
triggering during the window never launches new database work. After
migration and pre-open checks, both flags clear and visible messages
re-trigger processing; failures before reopening keep maintenance active.

## AI reading path (ARC-034-1)

Scanned scores are read by a second finite job. Everything is declared in
`main.bicep`; there is no maintainer creation step apart from the
verification at the end.

| Resource | Purpose |
| --- | --- |
| queue `archive-reading` | same storage account as `archive-extraction`; the extraction job hands each finished score over here, the editor's actions send here too |
| `id-archive-read` | reading job identity |
| `ja-archive-read` | event job, same image parameter, `--read-queue` |
| `id-archive-read-dispatch` | identity of the dispatch sweep |
| `ja-archive-read-dispatch` | cron job (daily 03:17 UTC, `readingDispatchCron`), same image parameter, `--dispatch-reading` |

`ja-archive-read` follows the extraction job: KEDA `azure-queue` rule on
`archive-reading` (`visibleonly`, queue length 1, min 0, at most 1
execution), authenticated as its own identity through the rule's `identity`
property; platform retries 0; the runtime identity is attached with lifecycle
`None` only to resolve the Key Vault `archive-db-connection` secret. It
differs in three ways: memory 2 Gi and 1 vCPU (the PDF, up to 32 MiB, is read
into memory and rendered by native PDFium/Skia); replica timeout 50 minutes
(the drain loop checks its 30 minute budget only before starting a message,
and a message gets min(8 minutes, the batch's 20 minute receive visibility),
not the drain deadline: worst case is about 40 minutes with cold start,
sweep, a last 8 minute message and the telemetry flush); and the model settings
`Archive__Chat__Provider=AzureOpenAI`, `Archive__Chat__Endpoint` and
`Archive__Chat__DeploymentName=gpt-6-luna` (the same values the app gets; the
deployment name is the key priced under `Archive:Ai:Models`). Further
settings: `Archive__Reading__QueueServiceUri` (the extraction queue endpoint,
same account), `Archive__Assets__ServiceUri` (PDF reads), the database
secret, `Archive__MaintenanceMode` and `AZURE_CLIENT_ID`. At most 1
execution at a time, because a ten-image call already uses most of the
deployment's 30k tokens-per-minute limit: parallel runs, or a run next to
member chat, would only cause 429s and spurious failed readings. One run
still handles many messages in a row.

| Identity | Role | Scope | Purpose |
| --- | --- | --- | --- |
| `id-archive-read` | `Storage Queue Data Contributor` | assets account | same reasons as the extraction job: receive/process/delete, the run's own sweep sends, KEDA's `queues/read` poll |
| `id-archive-read` | `Storage Blob Data Reader` | assets account | bounded PDF reads |
| `id-archive-read` | `Cognitive Services OpenAI User` | `aoai-liedertafel-archive` | the model calls (declared like the runtime identity's role); the ONLY job identity that holds it |
| `id-archive-read-dispatch` | `Storage Queue Data Contributor` | assets account | the sweep creates the queue when missing (needs the write action, which Message Sender lacks) and sends |

The account-scoped queue and blob roles mirror the extraction job's.

The extraction job already holds Queue Data Contributor on the account, so it
can send the hand-over message to `archive-reading` with no new role, and the
runtime identity's Message Sender covers the editor's actions. Both fall back
to `Archive__Extraction__QueueServiceUri` for the endpoint.

The dispatch sweep has its own minimal identity instead of reusing
`id-archive-read`: it needs the queue role only. It never reads a PDF or
calls the model, so sharing the reading identity would give a daily cron job
read access to every member file and the OpenAI role for nothing. It does
carry the same `Archive__Chat__*` settings as configuration (not rights):
the sweep prices the worst case of a reading request against the configured
deployment name to decide whether parked work fits the budget again, and
without a matching model key no price is found and no parked row would ever
wake. Parked rows (`WaitingForBudget`) have no queue message, so this daily
run (or the next `--read-queue` run, which sweeps first) is what resumes them
after the month changes or the cap is raised.

The switch: the bicep parameter `readingEnabled` (default `true`) sets
`Archive__Reading__Enabled` on the app, the extraction job and both reading
jobs. It is `false` in `appsettings.json`, so production only reads scans
because this template says so. Setting
`readingEnabled` to `false` (parameter file or `--parameters`) and running
the infrastructure workflow stops only NEW work: the extraction job hands
nothing over, the editor actions answer 409 and the pages hide the reading.
It does NOT stop spending: `ReadingWorker`, `--read-queue` and
`--dispatch-reading` never check `Archive:Reading:Enabled`, so messages that
are already queued, and both sweeps (the daily cron and the piggyback sweep at
the start of each `--read-queue` run), keep calling the model. To stop the
spending itself use one of these (both are reversible):

- Pause the jobs: set `Archive__MaintenanceMode=true` on both reading jobs.
  Executions that start afterwards exit 0 before touching the database or
  the queue. A running execution is NOT affected: it keeps the environment it
  started with, so stop it with `az containerapp job stop -n ja-archive-read
  -g RG-Liedertafel-Archive` (and the same for `ja-archive-read-dispatch`).
  `az containerapp job update -n ja-archive-read -g RG-Liedertafel-Archive
  --set-env-vars Archive__MaintenanceMode=true` and the same for
  `ja-archive-read-dispatch`. Undo with `...=false`. The pause is reset by
  the next infrastructure deploy (it applies the app's live maintenance flag)
  and by every migrating release (`archive.yml` "Open member access" sets
  `Archive__MaintenanceMode=false` on both jobs, and every push to
  `src/archive/**` migrates), so set it again afterwards. While paused, visible
  queue messages still make KEDA start a no-op execution every polling
  interval (30 s); messages stay queued.
  Queued work survives this route.
- Remove the model right: delete the role assignment of `id-archive-read` on
  `aoai-liedertafel-archive` (`az role assignment delete --assignee
  <principalId of id-archive-read> --role "Cognitive Services OpenAI User"
  --scope <account id>`). Every model call of the job is then refused by the
  account (403). This route spends nothing but fails pending work: the worker
  classifies a 403 as transient, so queued and woken readings are retried and
  end up `Failed` after `MaxAttempts` (4) with "Das Lesen ist nach mehreren
  Versuchen fehlgeschlagen" (editors can start them again). Prefer the
  maintenance route when the work should survive. Re-running the infrastructure
  workflow restores the role.

Check the live value with `az containerapp show -n ca-liedertafel-archive -g
RG-Liedertafel-Archive --query "properties.template.containers[0].env[?name=='Archive__Reading__Enabled']"`.

Release: `archive.yml` pins `ja-archive-read` and `ja-archive-read-dispatch`
to the same digest as the app and the extraction job, sets
`Archive__MaintenanceMode` on them around a migration, and stops their running
executions before it (a reading run may be minutes into a model call; the
stale-lease ladder recovers it). Jobs that do not exist yet are skipped with
a warning, so a release that runs before the first infrastructure deploy
cannot break halfway through a maintenance window; run the infrastructure
workflow first. Infrastructure re-runs pass the app's deployed image and
maintenance flag to the reading jobs through the same parameters.

Verification after the first deploy (read-only): `az containerapp job list
-g RG-Liedertafel-Archive -o table` shows the three jobs; `az role assignment
list --scope $(az cognitiveservices account show -n
aoai-liedertafel-archive -g RG-Liedertafel-Archive --query id -o tsv)` shows
`id-archive-app` and `id-archive-read` as the only OpenAI users. The hosted
journey check (upload a scan, watch the reading) is in
[ARC-034-1 (#141)](https://github.com/JakobFerdinand/liedertafel/issues/141).

## Failure alerts (ARC-042)
`main.bicep` declares the maintainer alert wiring (nothing else alerts
today). Every rule is a scheduled query over the environment console logs
that already flow into `log-liedertafel-archive` (`ContainerAppConsoleLogs_CL`)
and keys on the exact shipped failure lines; the line markers are pinned by
`tests/archive/backend/AlertQueryContractTests.cs` in the backend test run
(renaming a log line breaks the build before an alert can silently die).
Deterministic by design (architecture §14): no model involvement, and
budget-cap parks (`WaitingForBudget`) plus controlled transient retries are
normal states that no rule matches.

There are no metric alerts on application meters: the OTLP pipeline targets
Aspire in development only, and in production no OTLP endpoint is configured
(development keeps the Aspire path untouched; local runs never send to
production monitoring). Log volume/age stay bounded by the workspace's
31-day retention and the 1 GB/day ingestion cap.

### Destination and enablement

`param maintainerAlertEmails` (main.bicepparam, maintainer-managed, not a
secret): primary maintainer first, then the second maintainer. While the
list is empty every rule deploys with `enabled: false` — an infra re-run
cannot invent a receiver. Set the addresses, commit the params file, and
merge; the infra workflow deploys them (the addresses appear in the PR
what-if diff).

| Alert (name `al-archive-…`) | Severity | Marker | Means |
| --- | --- | --- | --- |
| `job-failed` | 1 | `Archive job … failed (…)` from a finite job run | A queue job or dispatch sweep execution ended in terminal failure |
| `item-failed` | 2 | `… exhausted its attempts` / `… failed deterministically`, dimension `DateiId_s` | A Datei is terminal-Failed and needs an editor retry or repair |
| `app-failed` | 2 | `Archive request failed (…)` on the app revision | Unhandled request 500s (DB outage, bad revision, bug) |
| `maintenance-open` | 1 | `… paused: maintenance mode is enabled` outside a release | A stuck maintenance window — a failed release keeps members locked out |

Release failures (`archive.yml`) most directly mail through GitHub's own
workflow-failure notifications — keep both maintainers watching the repo.
Azure-side, a failed release deterministically leaves maintenance open, which
the `maintenance-open` alert picks up from the next finite job run (the
daily dispatch sweep logs the pause line every day at 03:17 UTC, so a
stuck window re-alerts daily). Migration failures run inside the release
workflow's docker step and are covered by the GitHub notification as well,
never by the `job-failed` rule.

### Operator path (second maintainer, no memory required)

1. Open the alert mail; it links to this section and carries the query and
   the extracted `Job_s`/`DateiId_s` dimension. In the alert instance, jump
   to "View query" / Log Analytics for the full context (timestamps,
   `ContainerJobName_s`, `RevisionName_s`).
2. Identify the run:
   `az containerapp job execution list -n <job> -g RG-Liedertafel-Archive -o table`
   and `az containerapp job execution show` for the failed execution (the
   identity under `Web app > Azure`, or ask the primary maintainer for
   deploy rights; read access plus this runbook is enough).
3. One-time job failure or stuck window during a release: follow
   [Controlled database releases](#controlled-database-releases-arc-012)
   (maintenance stays active by design; fix forward, re-release). Check
   members are not locked out: `curl https://archiv.liedertafel-mining.at/api/maintenance`.
4. Recurring/undrained queue job failure: jobs restart automatically on the
   next queue message (or the daily sweep); if executions keep failing,
   stop churn with maintenance mode and follow the AI reading/extraction
   sections above (max attempts, stale leases, budget state). Escalate to
   the primary maintainer before any fixes beyond retry.
5. Item failure (`item-failed`): open the Datei in the archive UI
   (Wartung/"paper bin" tools show extraction/reading status); the editor's
   retry action restarts the work fresh. Only repair via maintenance commands
   (`--cleanup-uploads`, selection repair per its own section) with the
   primary maintainer.
6. App failure (`app-failed`): check the current revision health
   (`az containerapp show -g RG-Liedertafel-Archive -n ca-liedertafel-archive`)
   and the last release run; if Neon is unavailable the bounded monthly log
   cap and low traffic mean a few failed requests — verify the database
   rather than the app first.
7. Resolve the alert instance (auto-mitigates when the marker stops repeating;
   verify resolution in the portal alert rules view "Alerts").

Ownership: alert destinations and thresholds are owned by the primary
maintainer (Bicep params), actionable in this file; changes go through the
ordinary infra PR path with what-if review. Never add model-based
classification to an alert (stay deterministic, architecture §14).

### Verify receipt and inject safe failures (one-off after first enablement)

1. Receipt, without touching the app: in the Azure portal open
   `ag-archive-maintainers` → "Test notifications" (or
   `az monitor action-group show -g RG-Liedertafel-Archive -n ag-archive-maintainers`
   to confirm both receivers, then trigger a test mail from the portal
   "Test" pane). Both maintainers confirm receipt of the test and an alert
   mail with the common alert schema.
2. Safe job-failure injection — simulate a failed release window (also
   exercises `maintenance-open`). This is reversible within minutes and
   never touches member data:
   ```bash
   az containerapp job update -n ja-archive-read-dispatch -g RG-Liedertafel-Archive \
     --set-env-vars Archive__MaintenanceMode=true
   az containerapp job start -n ja-archive-read-dispatch -g RG-Liedertafel-Archive
   # job run receives no env override mistake; it logs its pause line and exits 0 —
   # correctness of the run is irrelevant, only the log line is.
   az monitor log-analytics query --workspace 1be7abbb-ed4d-4938-9cc2-08fd723637ac \
     --analytics-query 'ContainerAppConsoleLogs_CL | where TimeGenerated > ago(1h)
     and Log_s has "paused: maintenance mode is enabled"'
   # after the mail arrives (≤ ~15 min + eval):
   az containerapp job update -n ja-archive-read-dispatch -g RG-Liedertafel-Archive \
     --set-env-vars Archive__MaintenanceMode=false
   ```
   Then resolve the alert instance (or let auto-mitigate close it).
3. Verify alert→run trace: from the alert instance, use "See in Azure
   Monitor/Log query" and confirm `ContainerJobName_s` points at the
   execution listed in step 2. For a local end check, repeat any finite-job
   failure through AppHost (`src/archive`, see root AGENTS.md) and confirm
   the correlated trace/logs/metrics arrive in the Aspire dashboard after
   the job shuts down — development instrumentation is unchanged.

Never deploy alerts with a `what-if` receiver you can't justify; the deploy
guard rejects Delete/Replace changes, so all alert resources must remain
additions/modifications.

## Video playback copies (#161)

Every uploaded video (Auftritt Aufnahmen AND Lied-Aufnahmen) automatically
gains a playable MP4 playback copy; the transcode is a third finite job
(related to the reading path) with everything declared in `main.bicep`.

| Resource | Purpose |
| --- | --- |
| queue `archive-videos` | same storage account as `archive-extraction`; the finalize hook, editor sends and both sweeps go here |
| `id-archive-video` | transcode job identity |
| `ja-archive-video` | event job, same image parameter, `--transcode-queue` |
| `id-archive-video-dispatch` | identity of the dispatch sweep |
| `ja-archive-video-dispatch` | cron job (daily 04:23 UTC, `videoDispatchCron`), same image parameter, `--dispatch-videos` |

Same job contract as the extraction/reading jobs: KEDA `azure-queue` rule on
`archive-videos` (`visibleonly`, queue length 1, min 0, at most 1 execution)
authenticated as its own identity, platform retries 0, the runtime identity
attached with lifecycle `None` only to resolve the Key Vault
`archive-db-connection` secret. Differences: the video identity is the ONLY
job identity that writes blobs (it uploads the new MP4 copy under `dateien/`,
a Datei creation at finalize time, never a rewrite), so it holds `Storage
Blob Data Contributor` beside `Storage Queue Data Contributor`; CPU 2 and
memory 3 Gi bound hours-long concerts, with `Archive__Video__TimeBudget` and
`Archive__Video__DrainBudget` pinned to 2 h each inside replicaTimeout 4 h;
and `--transcode-queue` is entirely deterministic — ffmpeg/ffprobe from the
image's package, no model gateway registration, no spend cap to watch.
`archive.yml` pauses, stops and pins both video jobs with the
extraction/reading jobs through the shared resolve step; the `video_jobs`
schema of the same migration carries progress and the terminal German failure
on the Datei (`GET /api/dateien/videos`, `POST /api/dateien/{id}/video/retry`).
The dispatch sweep identity holds the queue role alone. The local ffmpeg
caveat: a worker host without ffmpeg records a clear German failure per Datei
and exits finitely — no crash loop; see the root `AGENTS.md` install note.

## One-off blob cleanup after the contract migration (#172)

The migration that dropped the asset/revision model cannot reach Blob storage.
After the release that applied it, release the old objects once with the finite
`--cleanup-dropped-blobs` command of the application image. It deletes
everything under `revisions/` and staged `pending/` objects no upload session
refers to any more, and never touches `dateien/` (the Dateien of the flat model)
or any other name; `--dry-run` only counts. It needs the storage identity the
extraction job already has (Blob data access to the assets account) and the
database connection of the runtime role.

```bash
# (Check `az containerapp job start --help`: the container arguments are
# passed with --args/--command depending on the CLI version.)
# Dry run first: count only, nothing is deleted. Reuse ja-archive-extract's
# identity and image by overriding the command for this one execution.
az containerapp job start -n ja-archive-extract -g RG-Liedertafel-Archive \
  --args "--cleanup-dropped-blobs" "--dry-run"
# Read the "Dropped-blob cleanup (dry run)" log line, then run it for real:
az containerapp job start -n ja-archive-extract -g RG-Liedertafel-Archive \
  --args "--cleanup-dropped-blobs"
```

Check the logs for `Dropped-blob cleanup finished`; a second run reports 0.
Do not run it before the migration: until then `revisions/` is still live data.

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
   `run_migration=true`). The workflow then:
   enters maintenance on the app, the extraction job AND the reading jobs
   (`Archive__MaintenanceMode=true`, members see the German banner/`/wartung/`,
   conflicting API work answers 503), stops in-flight extraction and reading executions
   and confirms quiescence (new executions exit 0 before database work), runs the
   selected image's `--migrate` exactly once with the migration role,
   deploys the digest to app and jobs, proves the window on the new revision,
   clears maintenance everywhere to reopen access and the jobs, and
   smoke-checks (`/alive`, `/api/build`,
   `/api/maintenance:false`, `/system/status/`, `/wartung/`,
   `/api/antiforgery:200`, dev-absence, unknown-API 404).
4. Discard the migrator password (rotate via the Neon API) and delete the
   `archive-migrations-connection` secret version.

Competing runs serialize on the `archive-prod` concurrency group: a second
release or infrastructure deploy waits instead of interleaving the first.
EF records applied versions in the migration history, so a queued run or a replay is a safe
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
- **Deliberate exception, ARC-022-3 (owner decision 2026-10-06):** the
  release carrying migration `20261006111010_AiUsageLedger` renames
  `chat_usage_entries` to `ai_usage_entries` in place, in one release,
  instead of splitting the rename into an additive and a cleanup release.
  A short chat downtime during that release is accepted. Consequences:
  a code-only rollback to the previous image **breaks the chat** (the old
  image writes `chat_usage_entries`, which no longer exists; every run ends
  in the failure state — everything else keeps working), so this release is
  fix-forward only for chat. After the release the operator must
  1. run `infrastructure/neon/runtime-grants.sql` as `archive_migrator`
     (new table `ai_budget_months`, renamed `ai_usage_entries`);
  2. check that the runtime role can use the lock table, for example
     `SELECT has_table_privilege('archive_runtime', 'ai_budget_months', 'SELECT, INSERT, UPDATE')`
     (a reservation inserts the month row and locks it with
     `SELECT … FOR UPDATE`, which needs `UPDATE`);
  3. smoke one chat run as a member and confirm a row in `ai_usage_entries`
     for the current month with `"ReservedMicroEur" = 0`.

### Job contract (handoff to ARC-032/035)

`GET /api/maintenance` (`{ maintenance, message }`, `no-store`) is the
machine-readable window state; `Archive:MaintenanceMode` carries the same
flag in configuration. The extraction job receives it in its template and
checks it before any database/queue work; the release stops existing
executions before migration because their configuration is immutable.
Future import jobs must receive and check the same flag before conflicting
work and quiesce existing executions while `maintenance` is true. Probes plus
`/api/build` and `/api/maintenance` always stay available for observers.
The reading jobs (ARC-034-1) follow the same rule.

## Cost overview (ARC-043, #150)

The monthly cost review (about 30 minutes, by the second maintainer, no
memory required) checks four places before the "review before a higher
recurring commitment" decision (architecture §2). Written so it runs without
the primary maintainer; escalate anything that is not just reading numbers.

### Azure group budget — portal-managed (documented finding)

The budget alert is **not** provisioned through Bicep. On 2026-09-23 the
Consumption budgets API answered `401 Unauthorized` to every programmatic
budget PUT on this subscription — four CI runs of the ARC-021 provider step
and then an identical PUT performed by the subscription Owner (same
api-version, same shape), while reads against the same resource provider
succeeded with the same identity. This is an RP-level restriction on
programmatic budget creation for this identity class, not a permissions gap;
the Bicep budget resource was removed the same day. The portal flow is the
documented working path.

One-off and after any amount change:

1. Azure portal → **Cost Management** → **Budgets** → **Add**.
2. Scope `RG-Liedertafel-Archive`; name `budget-liedertafel-archive`;
   monthly reset; the amount comes from the cost worksheet below (baseline
   EUR 10 for the group).
3. Alert conditions: **Actual 80 %**, **Actual 100 %** and **Forecast 100 %**.
4. Recipients: both alert addresses of `maintainerAlertEmails`
   (main.bicepparam) and the Owner contact role.
5. Confirm the first alert mail reaches both maintainers (test the action
   group from "Cost Management → Budgets → the budget → Alerts" or the
   action group's Test pane; see the ARC-042 verification steps above for
   how receipt is checked).

**The budget alert is an alert, never a cap.** Azure keeps billing past the
alert; the only hard cap in the whole estate is the application-level AI cap
(ARC-022-3). Wording everywhere reflects that: nothing here "spends", it
notifies.

### AI spend per feature (read from the usage ledger)

The usage ledger (`ai_usage_entries`/`ai_budget_months`, written by the ARC-022-3
budget) is the only source of AI spending truth; nothing of it is replicated.

- **View:** as an editor open `/verwaltung/ki-budget/` (the page calls
  `GET /api/maintenance/ai-budget`): the current month's spend per feature and
  model (calls, tokens, EUR including open reservations), the EUR 15 cap and
  the configured thresholds with each level's notified state. `no-store`; the
  endpoint only reads — verifying costs costs nothing.
- **Notices:** the daily reading dispatch sweep (`ja-archive-read-dispatch`,
  03:17 UTC) compares the month against the configured thresholds and logs a
  warning once per newly reached level, the cap included:
  - threshold: `al-archive-ai-threshold` (severity 2), line
    `KI-Budget: Schwellwert … EUR im Monat … erreicht (… von … EUR): <per-feature breakdown>`;
  - cap: `al-archive-ai-cap` (severity 1), line `KI-Budget: Monatsbudget erreicht …`,
    the same marker the refused chat/reading calls log. Both mail the
    maintainers through the same action group as every other archive alert.
  - Once per level per month (the month's row remembers the level); raising
    the cap mid-month re-enables the notices above the old level.
- **Thresholds are explicit configuration:** `aiNoticeThresholdsEur`
  (main.bicepparam, comma separated, e.g. `aiNoticeThresholdsEur = '5,12'`)
  relays to `Archive:Ai:NoticeThresholdsEur` on the web app and the reading
  sweep job. The list is **empty by default — that is a deliberate disabled
  state, not a default value**; maintainers choose the numbers when they edit
  main.bicepparam (suggestion pending maintainer decision: 5 and 12). The
  cap itself always notifies.
- AI stays out of the EUR 10 group target: AI has its own EUR 15 hard
  monthly cap; the group budget above only ever sees the rest of the estate.

### Neon Free headroom (bounded check — the sleeping database is never touched)

The check reads the **management plane** only:

1. Neon Console (console.neon.tech) → project `*archive*` → **Usage**: the
   month's branch storage, data transfer, and compute hours against the plan
   allowances. This is a dashboard read; no database connection is opened.
2. Alternatively read-only via the management API with the Neon API key
   (`curl -H "Authorization: Bearer $NEON_API_KEY"
   https://console.neon.tech/api/v2/projects/<project-id>/consumption`);
   the key is a credential: keep it in the vault/secret store, never in
   logs or shells history.
3. Never `psql` into the branch just to "check size": the archive's
   bounded connection design (zero keep-alive) exists so the sleeping
   database stays asleep; inode/storage growth shows up in the console
   storage graph.
4. Review **before** upgrading anything: when branch storage passes about
   80 % of the plan allowance, or transfer/consume hours trend at the
   allowance, decide with the primary maintainer whether the plan rises or
   a feature pauses (edition rate: no automatic upgrade is wired anywhere).

### Credentials and expiry reminders (values are never logged)

| Credential | Expiry | Owner | Renewal |
| --- | --- | --- | --- |
| GHCR pull PAT (`GHCR_PULL_PAT_ARCHIVE`, fine-grained, `packages:read` on `liedertafel-archive` only) | ≤ 1 year | primary maintainer | see "Pull-credential ownership and renewal" above; add the calendar reminder when creating a new PAT and confirm the date at every monthly check |
| Azure managed identities (mail, Blob keys, Key Vault, OpenAI, queue send) | none | — | nothing expires; all grants are role assignments visible via `az role assignment list` |
| Neon role passwords (`archive_migrator`, runtime, local admin; in Key Vault) | none | primary maintainer | rotation is manual (infrastructure/neon/README.md); no reminder needed |
| Neon API key(s) | user-set | primary maintainer | rotate from the Neon console Zapier/API page as needed; never log the value |
| Azure Communication Services sender (`archiv@liedertafel-mining.at`) | none | — | falls out of the verified domain; managed identity sends keyless |

Renewing a credential never logs its value: the runbooks only ever say
"update the secret"; GitHub Actions secrets and Key Vault references handle
the transfer.

### Cost worksheet (inventory sizes and explicit workload assumptions)

The EUR 10 group target is a target, not a cap (architecture §2); AI is
tracked separately under its own EUR 15 hard cap. Assumptions:

| Item | Inventory / workload assumption | Measured by |
| --- | --- | --- |
| Container Apps Consumption (web app) | Scales to 0; assumed < ~4 active hours/day (≈ 5 members); 2 replicas worst case | Azure Cost Management per service |
| Blob storage (member files) | Free plan until the imports: hot Dateien plus Cold originals; the collection budget tops a Lied/Auftritt at 40 GiB, the inventory target is 500 Lieder / 100–500 GB (long-term, review before importing at scale) | Azure Cost Management storage meter |
| Neon Free (metadata) | Catalogue rows only, well under the Free storage allowance; the database sleeps between requests | Neon Console usage (above) |
| Azure OpenAI (chat + scan reading) | Per-token; chat ≈ cents per session at the pinned Luna prices, scan reading a few cents per document | the ledger view (`/verwaltung/ki-budget/`), cap 15 EUR/month |
| Email (ACS send-only) | Sign-in codes + invitations; assumed well under the plan's monthly allowance | ACS metrics in the portal — currently unmeasured |
| Log Analytics ingestion | ≤ 1 GB/day cap (Bicep), 31-day retention; assumed a fraction of it | workspace ingestion view — currently unmeasured |
| Jobs (`--extract-queue`, `--read-queue`, sweeps, cleanup) | Consumption CPU-seconds, sweep runs are short (≤ 15 min execution budget) | Container Apps execution metrics — currently unmeasured |
| GHCR (private image) | Small registry, pulls per release | GitHub — unmeasured |

Unmeasured items are deliberate observations, not omissions: at the monthly
check, note in the review (repo docs or the ticket) whether any unmeasured
number has left "clearly small" territory. Items that never exist: egress
from Blob is member-driven (signed URLs) — spot-check the first heavy usage
month; DB backup/export is out of scope by decision (editor trash is the
recovery path).

### Monthly check — step by step (second maintainer)

1. **Azure:** Cost Management → group `RG-Liedertafel-Archive`, current
   month per service; compare against the worksheet assumptions; open the
   budget's monthly view if one exists.
2. **AI:** the `/verwaltung/ki-budget/` page (needs editor sign-in) or the
   Log Analytics query behind `al-archive-ai-threshold`/`al-archive-ai-cap`;
   note the month's total and the biggest feature.
3. **Neon:** Console → Usage; branch storage/transfer/compute hours vs the
   allowances.
4. **Credentials:** confirm the GHCR PAT expiry (GitHub → Settings →
   Developer settings → PAT list) and open the credentials table above.
5. **Decide:** any reading above ~80 % of its assumed envelope → record it
   in the review with the primary maintainer (upgrade review, feature
   pause, or expectation adjusted in this worksheet).
