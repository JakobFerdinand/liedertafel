---
id: ARC-022-1
status: planned
phase: follow-up
kind: slice
depends_on: ["ARC-022", "ARC-028", "ARC-021-1"]
touches: ["chatbot", "events", "performances", "ai-evaluation"]
external_inputs: ["ai-runtime-credentials"]
---

# ARC-022-1 — Answer "Wann haben wir dieses Lied gesungen?" in the chat

**Depends on:** [ARC-022](ARC-022-grounded-history-answer.md),
[ARC-028](ARC-028-performance-evidence.md),
[ARC-021-1](ARC-021-1-luna-switch-and-budget-cap.md).

## Outcome

A member asks the chat when a song was performed and gets dated events with
citations, with confirmed performances kept apart from uncertain evidence.

## Acceptance criteria

- [ ] Add the read-only tools on the ARC-021 allow-list that are still missing:
  event search, event and programme details, and performance history for a
  song. Each enforces member visibility inside the tool.
- [ ] Answers keep `confirmed` and `mention` evidence distinct and state date
  uncertainty as recorded; counts come from the tool, never from the model.
- [ ] Events and programmes in answers render as `AuftrittKarte` and
  `ProgrammListe` where ARC-022-4 is available.
- [ ] Citations cover events and programmes as well as songs, through the
  existing citation filter.
- [ ] Extend the chat evaluation with history questions, including a song with
  no recorded performance and an event with an approximate date.

## Verification

Run the extended evaluation on Luna. Ask about a song with confirmed
performances, one with only a programme mention and one with none; confirm the
answers and citations match the records and an unpublished event stays hidden.

## Handoff and parallel work

The member chat stays read-only. ARC-022-2 adds the editor assistant with
write tools as a separate surface. Decisions:
[architecture §14](architecture.md#14-ai-assistance).
