---
id: ARC-010
status: planned
phase: core
kind: slice
depends_on: ["ARC-004", "ARC-005"]
touches: ["email", "azure-email", "auth"]
external_inputs: ["azure-maintainer-access", "dns-maintainer-access", "pilot-mailboxes"]
---

# ARC-010 — Receive a real German sign-in email from the choir domain

**Depends on:** [ARC-004](ARC-004-azure-footprint.md),
[ARC-005](ARC-005-email-code-sign-in.md).

## Outcome

A maintainer requests a code through the local application, receives it in a
real inbox through Azure Email, and completes the existing sign-in journey.

## Acceptance criteria

- [ ] Provision Azure Communication Services Email and linked resources with
  Europe geography and a verified, recognizable choir-domain sender.
- [ ] Implement the shared mail-sender adapter, German invitation/code templates,
  bounded send handling, and useful delivery-failure diagnostics.
- [ ] Define runtime managed-identity permissions and local authorized credentials;
  no sender secrets or code contents enter frontend assets or logs.
- [ ] Keep ordinary AppHost startup on local mail capture; real Azure sending is
  an explicit integration-test option, still exporting development telemetry to Aspire.
- [ ] Test representative recipient providers and initial sending quotas, recording
  actual delivery evidence rather than equating API acceptance with delivery.

## Verification

Use real test inboxes to request/enter codes and accept an invitation. Simulate
sender rejection and delayed delivery; verify safe resend semantics and that
local mail capture cannot accidentally be selected for production.

## Handoff and parallel work

Supply sender/identity outputs to ARC-011. This slice can run alongside ARC-009;
it needs Azure email access but not a deployed web application.

## Confirmed setup inputs — 2026-09-14

The maintainer selected `archiv@liedertafel-mining.at` as the sender and manages
the domain's DNS at World4You. Azure CLI authentication is available; archive
Email Communication Services resources have not yet been set up. Use Europe
data location for both the email resource and its linked Communication Services
resource, with sender username `archiv` on the verified custom domain
`liedertafel-mining.at`.

Supply the generated ownership TXT, SPF and DKIM records to the maintainer for
DNS entry, then verify them through Azure. The maintainer explicitly requires
outbound email only: no receiving mailbox, forwarding service, or MX record is
needed for this sender. See the [ARC-004 preflight](ARC-004-azure-footprint.md#access-and-regional-preflight--2026-09-14)
for the access and DNS observations. Pilot recipient mailboxes remain an external
input.
