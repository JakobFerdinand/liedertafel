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
  limitations as an alert rather than a hard spending cap.
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

## Handoff and parallel work

ARC-044 adds actual pilot measurements. This slice can proceed beside failure
alerts ARC-042; coordinate shared action groups rather than make one depend on
the other's unrelated signal implementation.
