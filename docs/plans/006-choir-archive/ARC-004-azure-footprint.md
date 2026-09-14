---
id: ARC-004
status: done
phase: core
kind: discovery
depends_on: []
touches: ["azure-foundation", "planning"]
external_inputs: ["azure-maintainer-access", "dns-maintainer-access"]
---

# ARC-004 — Confirm an affordable Austria East deployment footprint

**Depends on:** None.

## Outcome

The maintainer has an account-specific resource/permission plan that the first
release can actually provision, with a documented West Europe fallback.

## Acceptance criteria

- [x] Check Austria East quotas/admission for Consumption apps/jobs, GPv2 Hot/Cold
  storage and queues, Key Vault, and supported bounded monitoring settings.
- [x] Record identifiers and permission boundaries for the archive resource group,
  runtime/job/release identities, GitHub OIDC, and managed certificate setup.
- [x] Verify access to DNS for `archiv.liedertafel-mining.at` and an email sender;
  record required ownership and Europe email geography configuration.
- [x] Produce a current itemized cost worksheet, distinguishing fixed costs,
  usage assumptions, free allowances, and unmeasured media inputs.
- [x] Record either Austria East viability or the concrete fallback reason.
  Additional recurring commitments above the target require the agreed review.

## Verification

Use current provider/price data plus subscription quota checks; clearly distinguish
advertised availability from successful provisioning. ARC-009 supplies the latter
for hosting. Do not infer Archive-tier support from an early-deletion meter.

## Handoff and parallel work

Publish resource names, role scopes, domain/sender inputs, and the cost worksheet.
ARC-009 and ARC-010 can then implement hosting and email independently. Source
inventory results can refine costs without blocking this initial feasibility check.

## Access and regional preflight — 2026-09-14

Authenticated read-only checks with Azure CLI `2.90.0` succeeded against the
enabled `Pay as you go` subscription
`8c599ae4-ed4f-43ba-9754-0a380ea6f0e1`. The signed-in maintainer has a direct
subscription-level `Owner` assignment, covering resource creation and role
assignment bootstrap. Scoped archive release/runtime identities still need to
be designed and provisioned.

| Check | Observed result |
| --- | --- |
| Existing choir group | `RG-Liedertafel`, group metadata location `austriaeast`; its two Static Web Apps and storage account are in `westeurope` |
| Dedicated archive group | Not present in the subscription's resource-group listing |
| Container Apps provider | `Microsoft.App` registered; environments, apps and jobs list Austria East and West Europe |
| Regional environment quota | `ManagedEnvironmentCount`: 0 used / 50 allowed in both Austria East and West Europe |
| Storage provider | Registered; storage accounts and queue services list Austria East and West Europe |
| Austria East storage SKU | `StorageV2` / `Standard_LRS` listed with no subscription SKU restrictions |
| Key Vault provider | Registered; vaults list Austria East and West Europe |
| Log Analytics provider | Registered; workspaces list Austria East and West Europe |
| Communication provider | Registered; email and communication resources use `global` resource location |
| Archive email resources | None set up for the archive, as confirmed by the maintainer; the subscription contains an unrelated application's email resources |

The regional quota was read through
`Microsoft.App/locations/{region}/usages?api-version=2025-07-01`, and storage SKU
restrictions through `Microsoft.Storage/skus?api-version=2023-05-01` under the
subscription. Provider resource-type metadata supplied the availability checks.

Austria East remains the preferred candidate. These checks do not establish
successful provisioning, Hot/Cold tier behavior, or bounded monitoring costs.
Consumption-core quota is environment-scoped and must be inspected after the
archive environment exists using `az containerapp env list-usages`. ARC-009 must
record actual hosting admission and available cores. West Europe remains the
fallback, with no fallback-triggering failure observed in this preflight.

### Confirmed DNS and sender inputs

- The maintainer owns DNS and can create/configure records at **World4You**.
- Archive hostname: **`archiv.liedertafel-mining.at`**.
- Selected sender: **`archiv@liedertafel-mining.at`**.
- Public DNS resolves to `ns1.world4you.at` and `ns2.world4you.at`.
- The inspection found no archive CNAME, apex SPF record, MX record, or
  `_dmarc` TXT record. Recheck DNS before proposing changes; preserve existing
  unrelated TXT records.
- ARC-009 supplies the actual hosting target and ownership-verification records.
- ARC-010 provisions Email Communication Services and its linked Communication
  Services resource with **Europe** data location, then supplies the generated
  domain-verification TXT, SPF and DKIM records for entry at World4You. The sender
  domain is `liedertafel-mining.at`; the website hostname is a separate DNS input.
- Configure sender username `archiv`. The maintainer explicitly selected a
  send-only address: receiving replies is not required. No mailbox, forwarding
  service, or MX record is needed for this outbound email setup.

Access/ownership inputs are available. The sections below complete the
remaining quota checks, resource/identity contract, monitoring bounds, cost
worksheet, and regional feasibility decision. No acceptance criterion is marked
complete solely from the preflight above; completion rests on the full evidence
in this file.

References checked:

- [Container Apps quota scopes](https://learn.microsoft.com/en-us/azure/container-apps/quotas)
- [Custom email-domain verification](https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/email/add-custom-verified-domains)
- [Communication Services data location and email handling](https://learn.microsoft.com/en-us/azure/communication-services/concepts/privacy)

## Quota and admission — 2026-09-14

Read-only follow-up with Azure CLI `2.90.0` against subscription
`8c599ae4-ed4f-43ba-9754-0a380ea6f0e1` (`Pay as you go`, Enabled). No writes
were performed. Main-agent spot checks re-ran `az account show`,
`az group list`, and the `Microsoft.App/locations/austriaeast/usages`
query and confirmed the subagent evidence below.

| Resource | Advertised Austria East support | Quota observed | Successful provisioning? |
| --- | --- | --- | --- |
| Consumption managed environment | Yes; `managedEnvironments` lists Austria East | `ManagedEnvironmentCount` 0/50 | Not proven; ARC-009 |
| Consumption apps (`minReplicas: 0`, `maxReplicas: 2`) | Yes; `containerApps` lists Austria East | Covered by environment quota only | Not proven; ARC-009 |
| Consumption jobs (finite C# extraction/import/migrate) | Yes; `jobs` lists Austria East | Same environment-scoped limitation | Not proven; ARC-009 |
| GPv2 account, Hot + Cold, queues, key-ring blob | Yes; `storageAccounts`, `blobServices`, `queueServices` list Austria East; `Standard_LRS` / `StorageV2` in Austria East with `restrictions: []` | No subscription storage-quota counter applies beyond SKU restrictions | Account/Hot/Cold behaviour not proven; ARC-009 |
| Key Vault | Yes; `vaults` lists Austria East | No vault-level quota counter queried | Not proven; ARC-009 |
| Bounded monitoring (Log Analytics + App Insights) | Yes; `workspaces` and `components` list Austria East; CLI advertises `PerGB2018` default, `--retention-time` (default 30 days), `--quota` daily cap | No workspace exists, so `list-usages` is not runnable | Not proven; ARC-009 |
| Email sender (ACS) | `CommunicationServices` / `EmailServices` / `Domains` are `global` location only, as expected; Europe is a data-location property at provisioning | N/A | ARC-010, not hosting admission |

Key limitation, stated explicitly per the verification rule: Consumption
core/vCPU quota is environment-scoped and is only visible after the archive
environment exists via `az containerapp env list-usages`. There is nothing to
query before ARC-009 provisions the environment. West Europe returns identical
`ManagedEnvironmentCount` 0/50 headroom; no quota reason prefers it.

Blob **Archive** tier: Austria East exposes no `Archive LRS Data Stored`
meter (only an `Archive LRS Early Delete` HNS meter). West Europe does expose
`Archive LRS Data Stored` at EUR 0.0015. Per the verification rule, the
early-deletion meter alone is not treated as tier support. The confirmed
design stays **Cold for preservation originals**; any Archive-tier use needs
an explicit tier-policy change.

Reproduce the quota reads:

```bash
az account show --query "{name:name, id:id, state:state}"
az group list --query "[].{name:name, location:location}"
az provider show --namespace Microsoft.App \
  --query "resourceTypes[?resourceType=='managedEnvironments' || resourceType=='containerApps' || resourceType=='jobs'].{type:resourceType, locations:locations}"
az rest --method get \
  --url "https://management.azure.com/subscriptions/8c599ae4-ed4f-43ba-9754-0a380ea6f0e1/providers/Microsoft.App/locations/austriaeast/usages?api-version=2025-07-01" \
  --query "value[].{name:name.value, current:currentValue, limit:limit}"
az rest --method get \
  --url "https://management.azure.com/subscriptions/8c599ae4-ed4f-43ba-9754-0a380ea6f0e1/providers/Microsoft.Storage/skus?api-version=2023-05-01" \
  --query "value[?name=='Standard_LRS' && kind=='StorageV2' && contains(locations,'austriaeast')].{name:name, kind:kind, restrictions:restrictions}"
```

`RG-Liedertafel-Archive` is absent (`az group exists` false). `RG-Liedertafel`
group metadata is `austriaeast`; its Static Web Apps and storage account remain
`westeurope`, matching `infrastructure/main.bicepparam`.

## Resource names and permission boundaries — 2026-09-14

### Identifiers

| Item | Value |
| --- | --- |
| Subscription | `8c599ae4-ed4f-43ba-9754-0a380ea6f0e1` (`Pay as you go`, Enabled) |
| Tenant | `39f97b50-0497-45db-884e-1cedd2074264` |
| Existing group | `RG-Liedertafel`, metadata `austriaeast`; resources in `westeurope` |
| Archive group (to be created) | `RG-Liedertafel-Archive`, metadata `austriaeast` |
| Maintainer | Direct subscription-level `Owner`; covers group creation and role-assignment bootstrap |
| Existing deployment principal | `sp-liedertafel-iac`, scoped `Contributor` on `RG-Liedertafel` only; cannot touch the archive group |
| Neon boundary | Org `org-round-tree-63490380` (Free), AWS Frankfurt, PG17, `archive_admin` / `archive_migrator` / `archive_runtime`; fully separate from Azure identities |

### Proposed resources

Location rule: everything `austriaeast` except email resources (`global` with
Europe data geography). Handoff to ARC-009/ARC-010 for provisioning.

| Resource | Proposed name | Location / SKU / notes |
| --- | --- | --- |
| Resource group | `RG-Liedertafel-Archive` | Metadata `austriaeast`; matches existing casing convention |
| Container Apps environment | `cae-liedertafel-archive` | `austriaeast`, Consumption-only; scale-to-zero |
| Container app | `ca-liedertafel-archive` | `austriaeast`, 0–2 replicas; single origin serves frontend + `/api/*` |
| Extraction job | `caj-archive-extract` | `austriaeast`, queue event-driven; own identity |
| Import job | `caj-archive-import` | `austriaeast`, manual trigger; own identity |
| Migration runner | `caj-archive-migrate` | `austriaeast`, manual one-off `--migrate` in maintenance window only |
| Storage account (GPv2 LRS) | `stliederarchiv` (fallback `stliedertafelarc`; both passed `check-name`) | `austriaeast`, `Standard_LRS`, Hot default; containers `media` (Hot), `originals` (Cold default tier), `dataprotection` (private key ring); queue `extraction` |
| Key Vault | `kv-liedertafel-archive` | `austriaeast`, RBAC authorization model; name free, no soft-deleted conflict |
| Log Analytics workspace | `log-liedertafel-archive` | `austriaeast`, `PerGB2018`, 30-day retention, explicit daily cap (exact cap is an ARC-009 decision) |
| Email Service | `liedertafel-archive` | `global`, data location **Europe**; custom domain `liedertafel-mining.at`, sender username `archiv` |
| Linked Communication Service | `acs-liedertafel-archive` | `global`, data location **Europe** |

Decide the HNS setting in ARC-009 and record it: hierarchical-namespace
accounts bill the HNS ops variants (Cold write EUR 0.2009/10k, read
EUR 0.1116/10k) instead of the flat-account meters used in the worksheet
default below.

### Permission boundaries

Bootstrap owner is the maintainer (subscription `Owner`): creates the archive
group and all role assignments. All other identities are scoped to the archive
group or narrower.

| Identity | Type | Scope | Roles (built-in) |
| --- | --- | --- | --- |
| Release identity `sp-liedertafel-archive-iac` (new app; do not reuse existing) | Entra app + federated credential | Archive group only | `Contributor` plus `User Access Administrator` (or group-scoped `Owner`); Bicep assigns data-plane roles, which plain `Contributor` cannot grant |
| Runtime `id-archive-app` | User-assigned managed identity | Storage / vault / queues | `Storage Blob Data Contributor`, `Key Vault Secrets User`, `Key Vault Crypto User`, `Storage Queue Data Contributor`, `Monitoring Metrics Publisher`; scope Blob role to containers (`media`, `dataprotection`) where possible |
| Job `id-archive-extract` | User-assigned managed identity | Media/originals + extraction queue + Neon runtime connection | Blob + Queue Data Contributor; database via Key Vault secret only |
| Job `id-archive-import` | User-assigned managed identity | Same plus Drive credential read | Same as extraction plus `Secrets User` for the import secret only |
| Migration `id-archive-migrate` | User-assigned managed identity | Vault secret for `archive_migrator` only | `Key Vault Secrets User` only; no blob/queue/data roles; enabled in the maintenance window only |
| GHCR pull | Key Vault secret (fine-grained PAT, `packages:read`) | Container app registry config | Not RBAC; avoids ACR Basic (~EUR 4.29/month). Maintainer owns lifecycle; second-maintainer replacement is an ARC-009 acceptance item |
| Neon SQL roles | `archive_admin` / `archive_migrator` / `archive_runtime` | Neon project only | Per `infrastructure/neon/` runbook; API receives only the runtime connection |

### GitHub OIDC contract

- New app `sp-liedertafel-archive-iac` for blast-radius separation.
- Federated credentials mirroring the existing pattern: `archive-gha-release`
  for `repo:JakobFerdinand/liedertafel:environment:archive-prod`, optionally
  `archive-gha-infra` for `ref:refs/heads/main` PR what-if. Issuer
  `https://token.actions.githubusercontent.com`, audience
  `api://AzureADTokenExchange`.
- New secrets (existing untouched): `AZURE_CLIENT_ID_ARCHIVE`,
  `AZURE_TENANT_ID_ARCHIVE`, `AZURE_SUBSCRIPTION_ID_ARCHIVE` via
  `azure/login@v3`, as in `infra-deploy.yml`.
- New workflows: `infra-deploy-archive.yml` (PR what-if + destructive-change
  guard + push/`workflow_dispatch` deploy) and `release-archive.yml` triggered
  only by `workflow_dispatch` behind the `archive-prod` environment with
  required reviewers.
- Image immutability: the release input is an image **digest**; Bicep takes an
  image parameter with no default and never sets it, so an infrastructure
  re-run cannot revert the running release. ARC-009 proves this with two
  distinguishable digests.
- The second maintainer operates from written instructions: approve the
  `archive-prod` run, select the digest, follow the PAT/Key Vault rotation
  runbook.

### Managed certificate setup

Container Apps managed certificate for `archiv.liedertafel-mining.at`
(subdomain, so no apex complications), SNI, platform auto-renewal. Flow: after
environment provisioning, create CNAME `archiv` toward the environment default
FQDN at World4You plus the `asuid.archiv` TXT verification value; ARC-009
owns creating/recording these records and binding the certificate, then
verifies HTTPS, SPA deep links, and JSON (never HTML/dev) API error routing.

## DNS and sender verification — 2026-09-14

No change to the preflight inputs; this section records them as the verified
ARC-004 handoff:

- DNS owned by the maintainer at **World4You** (`ns1/ns2.world4you.at`);
  maintainer can create/configure records. No archive CNAME, apex SPF, MX, or
  `_dmarc` TXT existed at inspection; recheck before proposing changes and
  preserve unrelated TXT records.
- Hostname `archiv.liedertafel-mining.at`; sender `archiv@liedertafel-mining.at`
  **send-only** (no mailbox, forwarding, or MX record).
- ARC-010 provisions Email + linked Communication resources, both **Europe**
  data location, verifies custom domain `liedertafel-mining.at` with sender
  username `archiv`, and returns the generated ownership-TXT/SPF/DKIM records
  for World4You entry. Runtime mail permission is a managed-identity grant, not
  a sender secret in the app. Local AppHost stays on mail capture; real sending
  is an explicit opt-in integration run.

## Cost worksheet — current, EUR, excluding tax — 2026-09-14

Checked 2026-09-14 via the Azure Retail Prices API (`prices.azure.com`,
`currencyCode=EUR`, `armRegionName=austriaeast`) plus current MS Learn/Neon
docs. Retail API figures exclude tax; VAT applies at invoicing. Blob bills
"GB" defined as GiB (2³⁰ bytes); Log Analytics measures GB as 10⁹ bytes. Do
not mix the two units in one formula.

### Data-quality flags (read first)

- Container Apps per-second compute rates are **unverified**: the Retail API
  currently lists `Standard vCPU/Memory Active/Idle Usage` at EUR 0.0 in both
  Austria East and West Europe under these meter names, which has never carried
  the real compute price. The billing model (per-second vCPU-s/GiB-s plus free
  grants) is confirmed by MS Learn. The worksheet uses rate variables
  `P_vcpu`/`P_mem`; re-verify via the pricing calculator or portal before
  ARC-009. Do not treat EUR 0.0 as free compute.
- New Container Apps `Environment` hourly meters effective 2026-09-01 in
  Austria East (Management, Planned Maintenance, Private Endpoint, each
  EUR 0.1116/hour, ~EUR 80/month at 24/7). Billing docs attach these to
  Dedicated profiles, private endpoints, and planned maintenance, not to a pure
  Consumption environment — but **ARC-009 must verify none applies**, since any
  one of them at 24/7 single-handedly breaches the EUR 10 target.
- Key Vault has **no per-secret monthly fee**; it is ops-only at
  EUR 0.0258/10k operations. Its fixed-cost row is effectively EUR 0.
- No Blob Archive capacity meter exists in Austria East, corroborating the
  Cold-only design (see quota section).

### Verified meter selection

| Service | Meter | Unit | EUR | Meter ID | Effective |
| --- | --- | --- | --- | --- | --- |
| Blob v2 | Hot LRS Data Stored | 1 GB/month | 0.0168 | `80aa9969-fd19-5d53-b441-83d4cc852415` | 2025-05-01 |
| Blob v2 | Cold LRS Data Stored | 1 GB/month | 0.0039 | `98cd70ea-daf8-5c6a-af63-41f53cef215f` | 2025-05-01 |
| Blob v2 | Hot Write / Read / Other ops | 10k | 0.0464 / 0.0037 / 0.0037 | `6cf4c9c4-…` / `8e399151-…` / `c6028bfb-…` | 2025-05-01 |
| Blob v2 | Cold Write / Read ops | 10k | 0.1546 / 0.0859 | `62fd47bc-…` / `3f22d239-…` | 2025-05-01 |
| Blob v2 | Cold Data Retrieval | 1 GB | 0.0258 | `c96e0694-…` | 2025-05-01 |
| Blob v2 | Cold Early Delete | 1 GB | 0.0039 | `69669dc5-…` | 2025-05-01 |
| Bandwidth | Out, first 100 GB free, then 0.0747+ | 1 GB | 0.0 → 0.0747 | `84257f2c-…` | 2025-05-01 |
| Bandwidth | In | 1 GB | 0.0 | `6e6e0417-…` | 2025-05-01 |
| Container Apps | Requests | 1M | 0.3435 | `f9bf424f-…` | 2026-01-01 |
| Key Vault | Operations | 10k | 0.0258 | `235177d0-…` | 2025-05-01 |
| Log Analytics | Ingestion (first 5 GB free, then 2.5674) | 1 GB | 0.0 → 2.5674 | `80788e69-…` | 2025-05-01 |
| Log Analytics | Retention beyond included 31 days | 1 GB/month | 0.1116 | `a0742393-…` | 2025-05-01 |
| Queues v1 | Class 1/2 ops | 10k | 0.0003 each | `2209eb0c-…` / `a9cdda9d-…` | 2025-05-01 |
| Queues v1 | Data Stored | 1 GB/month | 0.0386 | `1bedea09-…` | 2025-05-01 |
| Email (ACS) | Sent email | 1 email | 0.0002 | `91348e14-…` | 2023-04-01 |
| Email (ACS) | Data transferred | 1 MB | 0.0001 | `406fd373-…` | 2023-04-01 |
| Registry (avoided) | ACR Basic unit | 1 day | 0.1431 (~4.29/30d) | `a551f22b-…` | 2025-05-01 |

Jobs bill on the same active-rate vCPU/GiB-s meters from execution start to
completion; there is no separate execution-count meter, no request charge, and
no idle rate. Full meter IDs, HNS variants, and tier-break details are in the
working research notes; the IDs above are the worksheet inputs.

Austria East versus West Europe is cost-neutral for this footprint (Hot/Cold,
ops, Key Vault, Log Analytics, and email rates identical; requests slightly
favour Austria East at 0.3435 versus 0.4809 per million). No cost reason
prefers the fallback.

### Fixed-cost rows (configured per baseline)

| Row | EUR/month | Basis |
| --- | --- | --- |
| Key Vault (secrets + Data Protection key) | ~0.00–0.01 | No fixed fee; rare cached ops only |
| Log Analytics (PAYG, 31-day retention, daily cap, no commitment) | 0.00 | Under the shared 5 GB free ingestion allowance |
| Container Apps at 0 replicas idle | 0.00 | No replicas means no consumption charges; `minReplicas` stays 0 |
| Storage/queue/email accounts, pure-Consumption environment | 0.00 | No fixed-fee meters apply iff the Environment/Dedicated hourly meters stay unused; verify in ARC-009 |
| Neon Free / GHCR | 0.00 | Within allowances (see below) |

### Usage rows (formulas; inputs partly unmeasured)

| Row | Formula (EUR) | Illustrative value |
| --- | --- | --- |
| Capacity (verified rates only) | Cold GiB × 0.0039 + Hot GiB × 0.0168 | 100 GiB originals + 20 GiB Hot → **0.73**; 500 + 100 GiB → **3.63** (matches `architecture.md` §10) |
| App compute (≤5 users, max 2 replicas) | `max(0, V−180k)×P_vcpu + max(0, G−360k)×P_mem` | Small app (~0.5 vCPU/1 GiB, ~40 active h/month ≈ 72k vCPU-s, 144k GiB-s) fits inside the subscription free grants → EUR 0, once `P_vcpu`/`P_mem` resolve sanely |
| Requests | `max(0, R−2M)/1M × 0.3435` | Member browsing (thousands) → 0.00 |
| Jobs (import/extraction/cleanup) | Active vCPU-s/GiB-s per execution | Bounded by duration/concurrency; unmeasured, record per-execution in the pilot |
| Viewing transfer (<10 h/month) | `hours×3600×bitrate_Mbps/8/1024` GiB; `max(0, T−free_remaining)×0.0747` | 10 h at 8 Mbps ≈ 35 GiB → EUR 0 inside the 100 GB free tier; bitrate unmeasured |
| Blob operations | Hot/cold per-meter rates above | 100k Hot reads + 10k Hot writes + 10k Cold reads + 1k Cold writes ≈ 0.13 |
| Cold retrieval | GB × 0.0258 | First full re-read of 100 GiB ≈ 2.58 one-off, not monthly |
| Queues | Ops + stored per-meter rates | Sub-cent |
| Email (invites + codes) | `n × 0.0002 + MB × 0.0001` | 200 mails ≈ 0.04–0.05 |
| Log ingestion/retention | `max(0, GB−free_remaining) × 2.5674` + beyond-31d GB × 0.1116 | EUR 0 if bounded and under the free share |
| Early deletion (Cold 90-day minimum) | Pro-rata remainder per GB | EUR 0 in steady state; migration re-organisation is the exposure window |

The 20% Hot-derivative ratio used above is **unvalidated** (ARC-002 pending);
initial Drive samples give no measured ratio. The capacity subtotals are the
only EUR figures grounded in both verified rates and stated sizes; everything
else is formula plus assumption. **No validated full-month total exists yet.**

### Free allowances and sharing risk

- Container Apps Consumption: 180k vCPU-s + 360k GiB-s + 2M requests/month per
  **subscription**; still advertised. Only ACA meters consume it; the existing
  SWA/Functions estate does not, and no other ACA app exists, so the archive
  initially has the full grant. Future ACA apps are the risk, not current
  estate.
- Bandwidth: first 100 GB outbound/month free per **subscription**; inbound
  free. Shared with website/dashboard outbound; the archive's tens of GB fit
  only if the rest of the estate stays quiet.
- Log Analytics: first 5 GB ingested/month free per **billing account**;
  31-day retention included. Shared across workspaces; keep the archive at
  31 days plus a daily cap and watch other workspaces.
- Storage/Key Vault/email: no free allowance beyond trial for storage; Key
  Vault bills from the first op (no minimum); email bills from the first send
  (initial service limits 30/min, 100/hour per subscription).
- Neon Free (project scope): 0.5 GB storage, 100 CU-hours/month, 5 GB
  transfer/month, five-minute suspension, six-hour history capped at 1 GB of
  changes. Overage suspends rather than charges; Launch upgrade is USD usage
  billing and needs the agreed review. Cap temporary branches.
- GHCR: registry storage/bandwidth currently free; avoids ACR Basic. Verify
  the owner-plan quota at pilot scale.

### Unmeasured media inputs

Playable/original byte ratio, aggregate sizes, ~10 GB maximum-file claim,
codecs/bitrates, monthly request and vCPU/GiB-s counts, job duration and
concurrency, email volume, log GB, queue op counts, transfer geography,
subscription/billing-account free-grant consumption by other workloads, and
invoice tax. Cold's 90-day minimum applies per object: seven-day editor-trash
cleanup does **not** waive it, so use stable object identities. ARC-002
supplies sizes/ratios; ARC-042 measures the rest.

### Review trigger

Any recurring commitment above the EUR 10 normal-month target requires the
agreed review before acceptance. Do not adopt without review: ACR Basic,
Neon Launch, Log Analytics commitment tiers, always-on replicas or Dedicated
profiles, any Environment hourly meter at 24/7, Managed HSM, premium
certificate renewals (use free ACA-managed certificates), or Archive-tier
repatriation.

## Regional feasibility decision — 2026-09-14

**Austria East is viable as the preferred region, pending ARC-009
provisioning proof. No fallback-triggering failure was observed.**

Advertised availability, subscription quota (`ManagedEnvironmentCount` 0/50),
SKU restrictions (none), and cost (neutral versus West Europe) all support
Austria East for Consumption environments/apps/jobs, GPv2 Hot/Cold plus
queues, Key Vault, and bounded monitoring. DNS ownership (World4You) and the
send-only `archiv@liedertafel-mining.at` sender with Europe email geography
are confirmed inputs.

This is explicitly **not** successful provisioning. ARC-009 must still record:
environment creation plus `az containerapp env list-usages` Consumption cores,
app (0–2 replicas) and job admission, GPv2 Hot/Cold behaviour on real
objects, Key Vault plus bounded workspace creation (retention + daily cap),
domain/certificate binding for `archiv.liedertafel-mining.at`, scoped
identities/OIDC, GHCR pull lifecycle, and confirmation that no Environment or
Dedicated hourly meter bills a pure-Consumption environment.

Fall back to **West Europe** only on a concrete Austria East failure, for
example: environment/app/job creation rejected on quota, validation, or
capacity; GPv2/Hot/Cold or queue behaviour unavailable at provision time; or
vault/workspace creation with the bounded settings rejected or priced above
the agreed review threshold. Cost alone does not trigger the fallback.

## Completion — 2026-09-14

All acceptance criteria and the verification steps above are satisfied at the
discovery level: current provider/price data plus subscription quota checks
were used, advertised availability is distinguished from successful
provisioning (owned by ARC-009), and Archive-tier support was not inferred
from the early-deletion meter. Handoff to ARC-009 (hosting, identities,
certificate, `infrastructure/archive/` Bicep) and ARC-010 (Europe-geography
email resources, World4You TXT/SPF/DKIM entry, delivery evidence) is as
recorded above. ARC-002 inventory and ARC-042 pilot measurements refine the
worksheet without blocking this feasibility check.
