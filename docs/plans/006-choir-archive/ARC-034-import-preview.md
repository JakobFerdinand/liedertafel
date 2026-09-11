---
id: ARC-034
status: planned
phase: core
kind: slice
depends_on: ["ARC-002", "ARC-014"]
touches: ["import", "catalogue", "operator-commands", "db-migrations"]
external_inputs: ["drive-sample-access"]
---

# ARC-034 — Preview a Drive folder as proposed catalogue entries

**Depends on:** [ARC-002](ARC-002-source-inventory.md),
[ARC-014](ARC-014-arrangements-and-keys.md).

## Outcome

A maintainer scans selected Drive folders and an editor reviews proposed songs,
arrangements, keys and file labels before any source material is published.

## Acceptance criteria

- [ ] Implement a scoped, read-only Drive enumeration/mapping command and
  persisted import-run/candidate records, testable locally with the fixture set.
- [ ] Show source path/ID/version and proposed associations in an editor-only
  preview; identify uncertainty, unsupported conventions and possible duplicates.
- [ ] Let editors map to existing catalogue identities or correct proposals;
  do not infer arrangement identity solely from a matching title.
- [ ] Rerunning an unchanged scan preserves decisions and produces no duplicate
  candidates; source changes are surfaced rather than silently overwriting review.
- [ ] Record provenance and review attribution; private source details stay out
  of member APIs and unrestricted logs/artifacts.

## Verification

Scan the representative fixtures and an authorized source sample, review both
clear/ambiguous mappings, then rescan after a source change. Verify preview privacy,
stable source identity and retained editor decisions without copying media yet.

## Handoff and parallel work

ARC-035 consumes reviewed candidate IDs and the source/checkpoint contract.
This preview runs locally while cloud transfer/jobs are developed; it does not
depend on document extraction or full media playback.
