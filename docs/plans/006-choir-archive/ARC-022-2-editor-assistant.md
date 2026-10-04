---
id: ARC-022-2
status: planned
phase: follow-up
kind: slice
depends_on: ["ARC-022-1", "ARC-022-4", "ARC-013-1"]
touches: ["chatbot", "catalogue", "events", "programmes", "membership-admin"]
external_inputs: ["ai-runtime-credentials"]
---

# ARC-022-2 — Let editors create and change drafts through an assistant

**Depends on:** [ARC-022-1](ARC-022-1-chat-history-tools.md),
[ARC-022-4](ARC-022-4-ag-ui-client-and-a2ui-catalog.md),
[ARC-013-1](ARC-013-1-field-provenance-and-proposals.md).

## Outcome

An editor tells the assistant "Lege das Frühjahrskonzert 1987 im Stadtsaal an
und setze diese fünf Lieder ins Programm" and finds a draft event with that
programme, ready to review and publish by hand.

## Acceptance criteria

- [ ] A side panel on every editor screen for the Editor and Administrator
  roles only, with its own endpoint and agent. It knows the open record
  through AG-UI shared state.
- [ ] A separate editor tool set, registered only on this surface, that reads
  drafts and creates and edits **drafts** of songs, arrangements, events,
  programme items and performances through the ARC-013-1 shared write service.
  The member chat's tools stay hard-coded to published content.
- [ ] Pending changes and proposals render as `AenderungsVorschau` and
  `VorschlagKarte`. Their buttons call the REST endpoints directly; the agent
  learns the outcome through shared state.
- [ ] A draft change based on text the assistant read from a document or report
  pauses as a tool approval (AG-UI interrupt) and runs only after the editor
  approves the shown change.
- [ ] Publish, unpublish, merge, delete and every member-administration action
  are not tools. The assistant can only file them as proposals in the
  "Vorschläge" queue, where a human confirms.
- [ ] Every assistant write is attributed to the editor, recorded with AI
  provenance, and bounded per turn by the existing tool-call limit.
- [ ] Member names and emails are not sent to the provider; the assistant has
  no member-directory tool.
- [ ] Evaluation cases cover refusal to publish, refusal to follow an
  instruction embedded in a document, and a correct multi-step draft.

## Verification

As an editor, have the assistant draft an event with a programme and correct a
song field; confirm the drafts, attribution and badges. Ask it to publish and
to delete and confirm it only files proposals. Confirm a member cannot reach
the surface.

## Handoff and parallel work

Post-launch. Uses the tools from ARC-022-1 for reading. Decisions:
[architecture §14](architecture.md#14-ai-assistance).
