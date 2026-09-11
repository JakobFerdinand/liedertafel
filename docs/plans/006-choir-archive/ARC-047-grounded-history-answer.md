---
id: ARC-047
status: planned
phase: later
kind: slice
depends_on: ["ARC-007", "ARC-030", "ARC-033", "ARC-046"]
touches: ["chatbot", "search", "song-history"]
external_inputs: ["ai-runtime-credentials"]
---

# ARC-047 — Ask when a song was sung and receive a cited German answer

**Depends on:** [ARC-007](ARC-007-membership-revocation.md),
[ARC-030](ARC-030-recording-passages.md),
[ARC-033](ARC-033-search-score-text.md),
[ARC-046](ARC-046-chatbot-provider-decision.md).

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

This later slice does not require member corrections or duplicate merging to be
finished. Coordinate changes to the stable search/history result contracts.
