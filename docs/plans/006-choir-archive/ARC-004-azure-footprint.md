---
id: ARC-004
status: planned
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

- [ ] Check Austria East quotas/admission for Consumption apps/jobs, GPv2 Hot/Cold
  storage and queues, Key Vault, and supported bounded monitoring settings.
- [ ] Record identifiers and permission boundaries for the archive resource group,
  runtime/job/release identities, GitHub OIDC, and managed certificate setup.
- [ ] Verify access to DNS for `archiv.liedertafel-mining.at` and an email sender;
  record required ownership and Europe email geography configuration.
- [ ] Produce a current itemized cost worksheet, distinguishing fixed costs,
  usage assumptions, free allowances, and unmeasured media inputs.
- [ ] Record either Austria East viability or the concrete fallback reason.
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

Access/ownership inputs are available. The itemized current cost worksheet,
resource names and scoped identity/OIDC contract, monitoring bounds, remaining
quota checks, and final regional feasibility decision are still outstanding.
No acceptance criterion is marked complete solely from this preflight.

References checked:

- [Container Apps quota scopes](https://learn.microsoft.com/en-us/azure/container-apps/quotas)
- [Custom email-domain verification](https://learn.microsoft.com/en-us/azure/communication-services/quickstarts/email/add-custom-verified-domains)
- [Communication Services data location and email handling](https://learn.microsoft.com/en-us/azure/communication-services/concepts/privacy)
