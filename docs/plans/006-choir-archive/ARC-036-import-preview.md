---
id: ARC-036
status: planned
phase: core
kind: slice
depends_on: ["ARC-002", "ARC-014", "ARC-013-1", "ARC-021-1", "ARC-052"]
touches: ["import", "catalogue", "operator-commands", "db-migrations"]
external_inputs: ["drive-sample-access"]
---

# ARC-036 — Preview a Drive folder as proposed catalogue entries

**Depends on:** [ARC-002](ARC-002-source-inventory.md),
[ARC-014](ARC-014-arrangements-and-keys.md),
[ARC-013-1](ARC-013-1-field-provenance-and-proposals.md),
[ARC-021-1](ARC-021-1-luna-switch-and-budget-cap.md),
[ARC-052](ARC-052-chat-embeddings-pgvector.md).

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

## AI assistance

- [ ] The model proposes the mapping of folders and files to song, arrangement,
  musical version and voice label, with a confidence and a short reason for
  each proposal; deterministic naming conventions are applied first.
- [ ] Possible duplicates are suggested from the ARC-052 embeddings, across
  candidates and against the existing catalogue.
- [ ] Nothing in the import is applied automatically. Every mapping is a
  proposal, sorted into confidence bands, and the rule not to infer an
  arrangement identity from a matching title alone still binds the editor's
  decision.
- [ ] The mapping job reads unpublished source material and has no write access
  beyond its own candidate records. File names and contents are data, never
  instructions.

Decisions: [architecture §14](architecture.md#14-ai-assistance).

## Verification

Scan the representative fixtures and an authorized source sample, review both
clear/ambiguous mappings, then rescan after a source change. Verify preview privacy,
stable source identity and retained editor decisions without copying media yet.

## Handoff and parallel work

ARC-037 consumes reviewed candidate IDs and the source/checkpoint contract.
This preview runs locally while cloud transfer/jobs are developed; it does not
depend on document extraction or full media playback.
