---
id: ARC-026
status: planned
phase: core
kind: slice
depends_on: ["ARC-013", "ARC-022"]
touches: ["performances", "events", "db-migrations"]
external_inputs: []
---

# ARC-026 — Record what a historical source actually establishes

**Depends on:** [ARC-013](ARC-013-first-published-song.md),
[ARC-022](ARC-022-historical-event.md).

## Outcome

An editor links a song to an old event as either a confirmed performance or an
unconfirmed programme mention, even when its arrangement is unknown.

## Acceptance criteria

- [ ] Add event-side entry/editing with song, optional known arrangement/version,
  evidence status, source notes and stable occurrence identity.
- [ ] Preserve event date uncertainty and reject mismatched song/arrangement
  selections. Missing facts remain unknown rather than invented.
- [ ] Distinguish repeated occurrences of the same song at one event from repeated
  submissions of the same occurrence; idempotent retries cannot inflate totals.
- [ ] Record editor attribution and publish useful partial history. Date passage
  or a scanned programme alone never silently confirms a performance.
- [ ] Define the performance ID and evidence contract consumed by recordings and
  programme confirmation, independent of future-programme editing.

## Verification

Enter an approximate-year programme mention and a confirmed performance with
unknown arrangement. Edit evidence status, retry a submission and enter a genuine
repeat occurrence; verify identity, source context and permissions.

## Handoff and parallel work

ARC-024/025 may implement future programmes concurrently. Agree only the shared
event/song IDs and planned-versus-actual boundary; do not combine their persistence
into a single ambiguous 'performed' flag.
