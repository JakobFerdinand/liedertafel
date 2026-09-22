---
id: ARC-022
status: planned
phase: core
kind: slice
depends_on: ["ARC-007", "ARC-032", "ARC-035", "ARC-021"]
touches: ["chatbot", "search", "song-history"]
external_inputs: ["ai-runtime-credentials"]
---

# ARC-022 — Ask when a song was sung and receive a cited German answer

**Depends on:** [ARC-007](ARC-007-membership-revocation.md),
[ARC-032](ARC-032-recording-passages.md),
[ARC-035](ARC-035-search-score-text.md),
[ARC-021](ARC-021-chatbot-provider-decision.md).

## Outcome

A member asks a historical question and receives a concise German answer with
links to the same authorized records they can inspect through ordinary search.

## Acceptance criteria

- [ ] Add a bounded chat input/API using approved retrieval queries and provider
  settings; keep model credentials and authorization on the server.
- [ ] Cite actual visible records, distinguish confirmed performance from
  programme evidence, and acknowledge incomplete history/unknown answers.
- [ ] Treat document text as data, not instructions; expose no editing tools or
  unrestricted database/model-generated query execution.
- [ ] Enforce current membership, per-request/aggregate cost bounds, cancellation
  and useful failure states. Do not leak drafts through responses or citations.

## Verification

Exercise known/unknown songs, partial dates, conflicting evidence, hidden records,
revoked membership, malicious instructions embedded in text and exhausted budget.
Inspect every citation against the authorized source result set.

## Handoff and parallel work

This slice does not require member corrections or duplicate merging to be
finished. Coordinate changes to the stable search/history result contracts.
