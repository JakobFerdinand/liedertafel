---
id: ARC-022-4
status: planned
phase: core
kind: enabler
depends_on: ["ARC-022-3"]
touches: ["chatbot", "app-shell", "db-migrations"]
external_inputs: []
---

# ARC-022-4 — Show agent answers as archive components with AG-UI and A2UI

**Depends on:** [ARC-022-3](ARC-022-3-chat-on-agent-framework.md).

## Outcome

A member asks the chat for a song and sees it as a rendered song card from the
archive's own design, which is still there after reloading the thread.

## Acceptance criteria

- [ ] Replace the hand-written stream reader in `lib/chat.ts` with
  `@ag-ui/client` talking directly to the existing .NET endpoint. No
  CopilotKit and no Node runtime; the frontend stays a static export.
- [ ] Add `@a2ui/react`, pinned, with a catalog of archive components only:
  `LiedKarte`, `AuftrittKarte`, `ProgrammListe`, `VorschlagKarte`,
  `AenderungsVorschau`, `Quellen`. The basic catalog is not registered.
- [ ] The agent emits A2UI through a `zeige_oberflaeche` tool. The server
  validates the arguments against the catalog, resolves record IDs to
  visibility-checked data, and drops anything invalid before streaming. The
  model never supplies a URL.
- [ ] Citation chips stay the server-built `archive.citations` event.
- [ ] Store the validated A2UI message with the chat message through an
  explicit migration, and re-check visibility on thread load so a card for a
  since-unpublished record is dropped for members.
- [ ] Clicks inside rendered components call the REST endpoints directly.
- [ ] Prove one card end to end in the member chat (`LiedKarte`), including the
  scripted client path and a browser test.

## Verification

Ask for a published song and confirm the card, its link and its survival
across reload; unpublish the song and confirm the card disappears for a
member. Send a malformed and an out-of-catalog A2UI message from a fake client
and confirm neither is rendered.

## Handoff and parallel work

ARC-022-1 uses the event and programme cards; ARC-022-2 uses the proposal and
change-preview cards, shared state and the approval interrupt. Decisions:
[architecture §14](architecture.md#14-ai-assistance).
