---
id: ARC-044
status: planned
phase: launch
kind: validation
depends_on: ["ARC-006", "ARC-007", "ARC-008", "ARC-019", "ARC-032", "ARC-035", "ARC-038", "ARC-040", "ARC-041", "ARC-042", "ARC-043"]
touches: ["pilot-evidence", "planning"]
external_inputs: ["azure-maintainer-access", "pilot-mailboxes", "pilot-devices", "drive-sample-access"]
---

# ARC-044 — Validate the complete archive with a restricted production pilot

**Depends on:** [ARC-006](ARC-006-member-invitations.md),
[ARC-007](ARC-007-membership-revocation.md), [ARC-008](ARC-008-account-repair.md),
[ARC-019](ARC-019-midi-listening.md), [ARC-032](ARC-032-recording-passages.md),
[ARC-035](ARC-035-search-score-text.md), [ARC-038](ARC-038-import-review-publication.md),
[ARC-040](ARC-040-event-trash.md), [ARC-041](ARC-041-live-media-tiers.md),
[ARC-042](ARC-042-failure-alerts.md), [ARC-043](ARC-043-cost-and-quota-alerts.md).

## Outcome

Maintainers can demonstrate the agreed member/editor journeys on actual Azure
and Neon infrastructure, using a bounded dataset before wider invitations.

## Acceptance criteria

- [ ] Execute the PRD acceptance scenarios with a small representative catalogue,
  two arrangements/keys, uncertain history, an upcoming programme and recordings.
- [ ] First verify the final developer workflow: one AppHost start brings up all
  implemented services; API/worker logs, traces and metrics reach Aspire, including
  queue context and finite-job completion. Production remains independently deployable.
- [ ] Verify real email, restart-safe sessions, revocation and maintainer repair;
  inspect cross-role and direct file-access boundaries.
- [ ] Exercise interrupted approximately 10 GB transfer, long playback across
  real ticket expiry, MIDI on target devices, extraction/import retries and trash.
- [ ] Rehearse the controlled release path, then measure cold starts and a realistic
  five-user workload without creating a standing staging environment.
- [ ] Record actual regional provisioning, resource/log/traffic usage, projected
  normal-month total and outstanding defects. Review deviations from EUR 10.
- [ ] Every failed acceptance check creates a concrete blocking fix; this gate
  does not hide unfinished feature implementation or claim guaranteed cold-start time.

## Verification

Attach a reproducible, redacted pilot report with commands, browsers, measurements,
expected/actual outcomes and links to fixes. Mark done only when blocking checks
pass. No operational backup/restore exercise belongs to this pilot.

## Handoff and parallel work

The dependencies are the terminal core slices; their ancestors cover all 42 core
issues: ARC-001–041 and ARC-051.
Ready implementation issues can run concurrently before this convergence gate.
ARC-045 is the separate real-content rollout, not another feature implementation.
