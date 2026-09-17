---
id: ARC-010
status: in_progress
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
  (Stage 1 deployed 2026-09-17, Europe confirmed; domain verification and
  the link await the World4You entry below.)
- [x] Implement the shared mail-sender adapter, German invitation/code templates,
  bounded send handling, and useful delivery-failure diagnostics.
- [x] Define runtime managed-identity permissions and local authorized credentials;
  no sender secrets or code contents enter frontend assets or logs.
- [x] Keep ordinary AppHost startup on local mail capture; real Azure sending is
  an explicit integration-test option, still exporting development telemetry to Aspire.
- [ ] Test representative recipient providers and initial sending quotas, recording
  actual delivery evidence rather than equating API acceptance with delivery.
  (Quotas recorded, sender rejection simulated; inbox delivery awaits pilot
  mailboxes and the verified link.)

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

## Implementation progress — 2026-09-17 (branch `feat/arc-010-azure-login-email`)

Backend (`6ea3654` Bicep design, `1815f8c` sender, `2135dc8` tests,
`9ee73e0` live stage 1):

- `ArchiveMailTemplates` is the single source of the German sign-in,
  invitation, address-change, and test copy; both the SMTP capture sender and
  `AzureCommunicationMailSender` render from it (97/97 backend tests green,
  including exact-copy locks).
- The Azure sender uses `EmailClient` with `WaitUntil.Started` plus bounded
  polling under `Mail:SendTimeout` (45 s default): sender rejection (4xx) and
  throttling (429) fail fast with no app-level retry loop, and only the
  operation id, status, and error code are logged — never addresses, codes,
  subjects, or bodies (content logging stays off).
- `Mail:Provider` selects the transport (`Smtp` default, so ordinary AppHost
  startup is unchanged). Production refuses to boot on `Smtp` and refuses
  `Mail:AzureConnectionString`; the connection string is a Development-only
  local credential for the explicit run. Invitation mails stay retryable on
  transport failure; a failed code send now expires the undelivered challenge
  so the next request (inside the old cooldown) mints a fresh code instead of
  falsely reporting a suppressed resend (covered by
  `SenderRejectionKeepsResendSafe`).
- `archive-mail-test` (explicit Start) plus `--send-test-mail <address>`
  backs the integration run inside Aspire with full telemetry; both refuse to
  run unless the Azure provider is explicitly selected.

Azure (subscription `8c599ae4-…`, deployment `email-arc010`):

- `liedertafel-archive` Email Service, `liedertafel-mining.at` custom domain
  (all verifications `NotStarted`), sender username `archiv`
  (`archiv@liedertafel-mining.at`), and `acs-liedertafel-archive`
  Communication Service — all Europe geography. `id-archive-app` holds
  `Communication and Email Service Owner` on the Communication Service
  (direct assignment; group nesting is unsupported).
- Two learnings are baked into `email.bicep`: the RP accepts `2023-04-01`
  (not `2025-05-01`), linking an unverified domain fails with
  `DomainValidationError` (so `linkDomain` stages it like the ARC-009
  certificate), and Europe geography regionalizes the endpoint to
  `https://acs-liedertafel-archive.europe.communication.azure.com`.
- Rejection simulation: a data-plane send from the unlinked domain answers
  `404 DomainNotLinked` — nothing delivered, structured error for the
  `MailFailed`/safe-resend paths. Quotas are subscription-wide and shared
  with the unrelated alpakasoelde sender: 30 sends/min, 100 sends/hour.

## Remaining maintainer steps (external inputs)

1. Enter the four DNS records in `infrastructure/archive/README.md` at
   World4You, wait 15–30 minutes, run `initiate-verification` per type until
   all four read `Verified`.
2. Flip `linkDomain = true` in `email.bicepparam` and re-run the stage-1
   deployment command from the README.
3. Provide pilot recipient mailboxes across representative providers; then run
   `archive-mail-test` (AppHost user secrets, never committed) and complete a
   real code request/verify plus an invitation acceptance to record inbox
   delivery evidence.

## Handoff to ARC-011

Sender `archiv@liedertafel-mining.at`, endpoint
`https://acs-liedertafel-archive.europe.communication.azure.com`, runtime
identity `id-archive-app` (clientId `2f6f5bc6-b441-4ed4-abfa-c2e53f994547`
for `AZURE_CLIENT_ID`, objectId already granted). ARC-011 still wires
`AZURE_CLIENT_ID`, the Neon connection, and persistent keys; until then the
hosted shell keeps answering DB-backed endpoints with German 500s.
