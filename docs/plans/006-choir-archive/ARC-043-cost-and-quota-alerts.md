---
id: ARC-043
status: planned
phase: core
kind: slice
depends_on: ["ARC-002", "ARC-011"]
touches: ["observability", "azure-alerts", "operator-commands"]
external_inputs: ["azure-maintainer-access", "neon-maintainer-access", "maintainer-alert-destinations"]
---

# ARC-043 — Notice cost, quota and credential problems before disruption

**Depends on:** [ARC-002](ARC-002-source-inventory.md),
[ARC-011](ARC-011-hosted-persistent-sign-in.md).

## Outcome

The maintainer can review projected archive cost, Neon Free headroom and required
credential renewal, and receives a useful notification near agreed thresholds.

## Acceptance criteria

- [ ] Configure an archive-scoped Azure budget notification and document its
  limitations as an alert rather than a hard spending cap (attempted
  2026-09-23 via Bicep; blocked by an RP-level 401 on programmatic budget
  creation — see the note below for the finding and the portal path).
- [ ] Observe Neon Free storage/compute/transfer through supported management
  metrics/API or a documented bounded check; do not poll the sleeping database.
- [ ] Record expiry/ownership and renewal reminders for the private GHCR pull
  credential and applicable external credentials, without logging their values.
- [ ] Use inventory sizes and explicit workload assumptions in the cost worksheet;
  separately identify unmeasured job, traffic, email and log usage.
- [ ] Document the monthly check and review-before-upgrade action, including the
  second maintainer. Thresholds are explicit configuration, not hidden defaults.

## Verification

Exercise threshold/reminder evaluation with synthetic usage and a harmless
near-expiry record; verify destination delivery and current actual Neon readings.
Confirm monitoring stays finite and alert wording does not promise a spending cap.

## Cost note — 2026-09-23 (updated same day)

The budget alert is NOT provisioned through Bicep. Attempts during the ARC-021
provider step (EUR 10/month group budget `budget-liedertafel-archive`,
notifications at Actual 80/100% and Forecast 100% to j.wegenschimmel@gmail.com
plus contactRoles Owner) were rejected by the Consumption budgets API with
`401 Unauthorized` in four CI runs — and an identical PUT as the subscription
Owner (same api-version, same shape) also returned 401 while reads on the same
RP succeeded, so this is an RP-level restriction on programmatic budget
creation for this identity class, not a permissions gap (the release identity
held `Cost Management Contributor` at group scope, propagated and verified).
Acceptance criterion 1 therefore stays open: the alert is to be created
portal-managed (Cost Management → Budgets — the portal flow is the documented
working path) or by a later ARC-043 slice; the Bicep budget resource was
removed 2026-09-23. The app-side ARC-021 counter (EUR 5, alert plus manual
disable) remains the active enforcement until then. The remaining criteria
above stay open.

## Handoff and parallel work

ARC-044 adds actual pilot measurements. This slice can proceed beside failure
alerts ARC-042; coordinate shared action groups rather than make one depend on
the other's unrelated signal implementation.
