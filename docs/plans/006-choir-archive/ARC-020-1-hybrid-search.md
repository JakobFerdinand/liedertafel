---
id: ARC-020-1
status: planned
phase: follow-up
kind: slice
depends_on: ["ARC-020", "ARC-052"]
touches: ["search", "catalogue", "home"]
external_inputs: []
---

# ARC-020-1 — Find songs by meaning in the catalogue search box

**Depends on:** [ARC-020](ARC-020-catalogue-search.md),
[ARC-052](ARC-052-chat-embeddings-pgvector.md).

## Outcome

A member types "ruhige Adventlieder für Männerchor" into the existing search
box and gets fitting songs even when none of those words appear in the record.

## Acceptance criteria

- [ ] Combine the existing lexical match with the ARC-052 embedding search in
  one ranked result list behind the existing `/api/songs` search; exact title
  and creator matches stay on top.
- [ ] No chat-model call in the search path. The only AI cost is embedding the
  query; when the budget cap is reached, search falls back to lexical only.
- [ ] Members search published rows only; editors also reach drafts, using the
  visibility flag from ARC-052.
- [ ] Existing filters (ARC-023) keep working on the combined result.

## Verification

Compare lexical-only and hybrid results on a fixed list of German queries,
confirm a draft is unreachable for a member, and measure search latency after
a cold start with and without the embedding call.

## Handoff and parallel work

Post-launch. Questions that need an answer rather than a result list stay in
the chat. Decisions: [architecture §14](architecture.md#14-ai-assistance).
