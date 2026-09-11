---
id: ARC-027
status: planned
phase: core
kind: slice
depends_on: ["ARC-024", "ARC-026"]
touches: ["programmes", "performances", "db-migrations"]
external_inputs: []
---

# ARC-027 — Confirm what was actually sung after an event

**Depends on:** [ARC-024](ARC-024-publish-programme.md),
[ARC-026](ARC-026-performance-evidence.md).

## Outcome

An editor confirms an unchanged concert programme in one action, or marks a
skipped song and adds an encore while preserving the original plan.

## Acceptance criteria

- [ ] Build an actual-performance review from a specific published revision,
  allowing bulk confirmation, omissions, additions and corrected selections.
- [ ] Persist actual occurrences using ARC-026's identity/evidence model and link
  them to their originating planned items where applicable.
- [ ] Repeated confirmation updates the same occurrences rather than doubling
  them; genuine repeated songs remain distinct performances.
- [ ] Reject or explicitly resolve stale published-revision changes and preserve
  the planned list. Show members the distinction between plan and actual history.
- [ ] Restrict confirmation to editors/admins and retain attribution.

## Verification

Confirm a programme unchanged, then exercise skip-plus-encore and repeated-song
cases. Retry the operation and submit against a stale revision. Assert planned
content remains intact and only confirmed actual occurrences affect totals.

## Handoff and parallel work

ARC-029/030 consume performance IDs regardless of whether entered historically
or confirmed here. Coordinate shared occurrence mapping with ARC-025, but do not
block unrelated recording-player work.
