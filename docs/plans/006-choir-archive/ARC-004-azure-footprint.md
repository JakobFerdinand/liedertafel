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
