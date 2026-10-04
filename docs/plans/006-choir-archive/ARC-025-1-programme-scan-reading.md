---
id: ARC-025-1
status: planned
phase: follow-up
kind: slice
depends_on: ["ARC-025", "ARC-026", "ARC-028", "ARC-034-1", "ARC-052"]
touches: ["events", "event-materials", "programmes", "performances", "ai-evaluation"]
external_inputs: ["ai-runtime-credentials"]
---

# ARC-025-1 — Draft an event and its programme from a programme scan

**Depends on:** [ARC-025](ARC-025-event-documents.md),
[ARC-026](ARC-026-publish-programme.md),
[ARC-028](ARC-028-performance-evidence.md),
[ARC-034-1](ARC-034-1-scan-reading.md),
[ARC-052](ARC-052-chat-embeddings-pgvector.md).

## Outcome

An editor uploads a photo of an old concert programme and gets a drafted event
with date, venue and the ordered pieces, each matched to a catalogue song
where the match is confident.

## Acceptance criteria

- [ ] Read programme scans and photos with Luna and produce a draft: event
  kind, title, venue, date with the recorded uncertainty fields, and ordered
  items.
- [ ] Match each item to an existing song using lexical and embedding search.
  Items without a confident match are flagged "nicht zugeordnet"; no song is
  ever created automatically.
- [ ] Everything arrives as a draft or proposal in "Vorschläge" with the scan as
  its source. Publishing the event, programme or performance evidence stays a
  human action.
- [ ] Proposed performances use evidence status `mention` with the scan as
  source note, never `confirmed`.
- [ ] The reading job has no write access beyond its own draft; text on the
  scan is data, never instructions.
- [ ] A fixed evaluation set of real programme scans with expected event fields
  and item lists; record pass rate and cost per document.

## Verification

Upload a printed programme, a handwritten one and a photo with a partly
legible date. Confirm the draft, the approximate-date handling, flagged
unmatched items and that nothing is visible to members before publication.

## Handoff and parallel work

Post-launch. ARC-029 confirmation of what was actually sung stays a human
step. Decisions: [architecture §14](architecture.md#14-ai-assistance).
