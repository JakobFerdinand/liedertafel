---
id: ARC-021
status: planned
phase: core
kind: decision
depends_on: []
touches: ["ai-evaluation", "planning"]
external_inputs: ["ai-evaluation-access"]
---

# ARC-021 — Validate a usage-billed provider for grounded German answers

**Depends on:** None; it is the prioritized next decision.

## Outcome

The maintainer selects an AI provider and a bounded first question type with
documented cost/data handling before adding conversational archive access.

## Acceptance criteria

- [ ] Compare current provider terms, relevant EU handling, model suitability,
  usage pricing and limits against a separately reviewed AI budget.
- [ ] Test synthetic German archive questions with citations and uncertain
  historical evidence; assess unsupported-answer and instruction-in-data behaviour.
- [ ] Record a selected provider/model, allowed data, credential ownership,
  request/token limits, cancellation and failure policy.
- [ ] Define the first supported question as grounded archive retrieval, such as
  when a song was sung; no editing tools or self-hosted model/search service.

## Verification

Record a small reproducible synthetic evaluation and per-answer cost estimate.
Do not use private choir data until its handling has been decided. Revalidate
provider facts when implementation starts.

## Handoff and parallel work

Supply the provider contract to ARC-022. No technical prerequisites means the
research can start immediately and is the next planned work item.
