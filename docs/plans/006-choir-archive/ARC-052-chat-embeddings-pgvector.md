---
id: ARC-052
status: planned
phase: core
kind: slice
depends_on: ["ARC-021", "ARC-022"]
touches: ["chatbot", "search"]
external_inputs: ["ai-runtime-credentials"]
---

# ARC-052 — Search the catalogue semantically for the chat with pgvector

**Depends on:** [ARC-021](ARC-021-chatbot-provider-decision.md),
[ARC-022](ARC-022-grounded-history-answer.md).

## Outcome

The chat's `catalogue_search` tool also finds member-visible catalogue text by
meaning instead of only through word-identical tokens, so a freer or
paraphrased German question still reaches the records the answer cites.

## Acceptance criteria

- [ ] Enable the pgvector extension in the existing Neon PostgreSQL and add a
  `text-embedding-3-small` deployment to `infrastructure/archive/main.bicep`
  (pinned dated version per ARC-021's version policy; evaluation and pricing
  re-check before any upgrade).
- [ ] Add an explicit EF migration persisting embeddings for member-visible
  catalogue text under the same `CatalogueVisibility` boundary; drafts and
  unpublished records stay unindexed and invisible to the model.
- [ ] Backfill embeddings over member-visible catalogue text (titles, alternate
  titles, creator information, entered lyrics/opening words, extracted PDF
  text, event/place/history notes) and re-embed on publication or change.
- [ ] Extend the `catalogue_search` chat tool with the pgvector path while it
  keeps the same visibility filtering and result bounds as the lexical path;
  authorization stays inside the tool and the model never reaches drafts or
  raw queries.
- [ ] Record the embedding token cost of backfill/re-embed against the ARC-021
  pricing; the EUR 5 monthly alert-plus-manual-disable semantics stay
  unchanged.

## Verification

Re-embed the chat evaluation corpus and rerun the chat evaluation with
semantic search enabled: every citation still comes only from the authorized
result set, unknown/trap questions keep their honest behaviour, and a draft
stays unreachable. Compare lexical and semantic result quality on a freer
German question and record the per-run embedding cost.

## Handoff and parallel work

The chat currently runs lexical-only (ARC-022): embeddings improve retrieval
for the chat tool, not the chat pipe itself — no endpoint, streaming, thread
or UI change is planned. ARC-034/035 feed extracted score text into the
embedding scope once those slices land. `ai-runtime-credentials` is available
through the ARC-021 provider step; no further external input is required.
