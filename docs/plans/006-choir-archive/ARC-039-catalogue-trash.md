---
id: ARC-039
status: planned
phase: core
kind: slice
depends_on: ["ARC-033"]
touches: ["catalogue-trash", "assets", "catalogue", "db-migrations"]
external_inputs: []
---

# ARC-039 — Recover an accidentally deleted song or score

**Depends on:** [ARC-033](ARC-033-score-revisions.md).

## Outcome

An editor deletes catalogue content, members lose access to it, and the editor
can recover it from a seven-day trash while retained score revisions remain intact.

## Acceptance criteria

- [ ] Implement trash/list/recover for songs, arrangements, musical versions and
  logical assets using explicit deletion groups and contributor attribution.
- [ ] Hide deleted content from authorized member reads/search/ticket renewal;
  existing signed tickets retain their already-agreed expiry behaviour.
- [ ] Recovery restores the intended group without reviving independently deleted
  descendants or publishing content that was a draft before deletion.
- [ ] Expose Administrator-only permanent deletion and bounded expired-trash
  cleanup, checking retained references before removing objects/identities.
- [ ] Keep current/prior score revisions until deliberately removed; publish the
  retained-reference contract for event/history consumers and cleanup jobs.

## Verification

Trash and recover an arrangement with a revised score, test draft visibility and
independently deleted children, advance the clock past seven days, and verify
reference-protected items are not silently purged. This is editor recovery, not backups.

## Handoff and parallel work

ARC-040 extends this same contract to events. Coordinate asset identity/current
revision changes with ARC-033/033; feature authors adding references must register
them before their entities can participate in permanent cleanup.
