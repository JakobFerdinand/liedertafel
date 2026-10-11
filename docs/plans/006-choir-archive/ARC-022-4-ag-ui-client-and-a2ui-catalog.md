---
id: ARC-022-4
status: in_progress
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

- [x] Replace the hand-written stream reader in `lib/chat.ts` with
  `@ag-ui/client` talking directly to the existing .NET endpoint. No
  CopilotKit and no Node runtime; the frontend stays a static export.
- [x] Add `@a2ui/react`, pinned, with a catalog of archive components only:
  `LiedKarte`, `AuftrittKarte`, `ProgrammListe`, `VorschlagKarte`,
  `AenderungsVorschau`, `Quellen`. The basic catalog is not registered.
- [x] The agent emits A2UI through a `zeige_oberflaeche` tool. The server
  validates the arguments against the catalog, resolves record IDs to
  visibility-checked data, and drops anything invalid before streaming. The
  model never supplies a URL.
- [x] Citation chips stay the server-built `archive.citations` event.
- [x] Store the validated A2UI message with the chat message through an
  explicit migration, and re-check visibility on thread load so a card for a
  since-unpublished record is dropped for members.
- [x] Clicks inside rendered components call the REST endpoints directly.
- [x] Prove one card end to end in the member chat (`LiedKarte`), including the
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

## Implementation — 2026-10-11 (branch `main`)

**Client.** `lib/chat.ts` reads the run with `@ag-ui/client` 1.0.2 (`HttpAgent`
with the CSRF-aware fetch of `lib/auth.ts`, one fresh agent per question with
only that question; the server owns the history). Callbacks, error titles
(ProblemDetails title first, German fallbacks by status), the budget code and
the abort behaviour are those of ARC-022 and ARC-022-3, so the chat page
changed only where it shows surfaces. No CopilotKit, no Node runtime, static
export unchanged. `@a2ui/react` 0.13.0, `@a2ui/web_core` 0.13.0 and `zod`
3.25.76 are pinned exactly; the A2UI v0.9 protocol is used. The library loads on
demand (`next/dynamic`, no server rendering) only when an answer has surfaces.

**Wire fix found on the way.** The strict client refused the real server's
stream: the AGUI.Server library writes unused protocol fields as explicit nulls
(`usage[].model`, `parentRunId`, `subagentRunId`, `outcome.pendingToolCallIds`
…), which `@ag-ui/core` rejects (ZodError). `ChatEndpoints` now serializes
events without null properties (`WireJson`); `rawEvent` still carries the
library's own nulls, which the schema accepts. The existing Playwright mocks
had also violated the protocol (text without a start, runs without a
`runId`), which a hand-written reader never noticed; `tests/chat-mock.ts`
now builds events in the exact server shape and `chat.spec.ts` uses it.

**Catalog.** `urn:liedertafel:archiv:a2ui:katalog:v1`, exactly `LiedKarte`,
`AuftrittKarte`, `ProgrammListe`, `VorschlagKarte`, `AenderungsVorschau` and
`Quellen`; the basic catalog is not registered. Properties are strict Zod
schemas in `lib/a2ui.ts` (no field can hold an address); the React
implementations are in `components/a2ui-katalog.tsx`, styled in
`app/fragen/chat.css` as archive index cards. Every surface is one
`createSurface` plus one `updateComponents` with a single `root` component.

**Server.** `Chat/A2uiSurfaces.cs` (`A2uiCatalog`): the `zeige_oberflaeche`
tool takes `komponenten`, each a component name plus exactly the one id it is
built from. Each entry is validated against the catalog definition (known name,
only its own id; empty fields count as absent; at most 12 entries looked at, 4
surfaces per answer, a record once), resolved to published records only, and
built into the wire properties by the server (title, composer, lyricist, the
first two lyric lines; event title, kind, date, venue; up to 10 sources). The
model gets back only `angezeigt` and the count `verworfen`. The tool call is
removed from the AG-UI stream; the validated messages travel in one `CUSTOM`
event `archive.a2ui` before `archive.citations`. The member chat offers
`LiedKarte`, `AuftrittKarte` and `Quellen`; the other three are defined but
have no member resolver yet (always dropped, not offered).

**Storage.** Migration `20261011160524_ChatMessageA2ui` adds
`chat_messages."A2uiJson"` (nullable `jsonb`). `GET /api/chat/thread/{id}`
never returns it as stored: `RecheckAsync` rebuilds every surface from the
record ids it names against today's visibility, so a card for a since-unpublished
record is gone and tampered or unreadable content yields nothing. A card-only
answer stores empty content, is not fed to the model as history and is left out
of the thread once its card is gone. The system prompt got one rule for
`zeige_oberflaeche`; `MaxToolCalls` stays 5 (search, optional details, show,
answer fit).

**Browser safety.** `leseA2uiOberflaechen` keeps a surface only if it has
exactly one `createSurface` (this catalog) and one later `root` update with a
catalog component and exactly its allowed properties; then the A2UI
`MessageProcessor` runs with `STRICT_VALIDATION` and a surface it refuses is
removed. Malformed, out-of-catalog, foreign-catalog, over-limit and
data-model-carrying messages render nothing and raise no error. The chat
log follows the answer when the library loads late and makes the log taller.

**Clicks.** Cards link to archive pages (`/lied/?id=`, `/auftritt/?id=`, built
from the ids). `VorschlagKarte` calls `POST /api/proposals/{id}/accept|reject`
with the CSRF token and shows the server's answer (409 text included); no click
goes back through the chat endpoint.

**Scripted client.** `ScriptedChatClient` shows a `LiedKarte` for questions
containing "als Karte" (search, `zeige_oberflaeche` with the first hit, answer
from the search result); the synthetic evaluation has a new case "Zeige mir die
Waldfahrt als Karte" (hard: a card event and a citation; live: `no-card` is a
soft flag, because the model may choose not to show one).

### Decisions the maintainer should know

- Status stays `in_progress`: everything below is covered by automated tests,
  but the live model was not asked. Whether Luna calls `zeige_oberflaeche` on
  a "show me the song" question rests on the prompt rule and is unproven;
  `ChatEvaluationLive` (see `AGENTS.md`) now has the case and reports
  `no-card` if it does not. Closing the ticket takes one live run (about
  EUR 0.005) and the hosted check after the release applied the migration.
- A card costs one extra model round trip and about 150 more input tokens per
  call (the tool schema). The bound of five model calls is unchanged.
- The member chat reads published records only, so the re-check uses the
  published predicate for everyone who can open the chat, editors included.
- `Quellen` is a card of songs and is not the citation chip list; the chips
  remain the server-built `archive.citations` event.
- The re-check issues one to four small queries per stored card on each thread
  load (at most 20 messages); fine for the 20-message bound, batch it if the
  bound grows.

## Handoff

- **ARC-022-1 (event and performance questions).** Add the event tools like
  `CatalogueTools` (authorization inside) and give `ProgrammListe` its
  resolver in `A2uiCatalog.Components`: today its entry has `Resolve: null`,
  which makes it unoffered and always dropped. The wire shape is fixed in
  `lib/a2ui.ts` (`auftrittId`, `titel`, `punkte[{position, liedId, titel,
  notiz?}]`); use the member visibility of `/api/events/{id}` programmes, not a
  looser one. `AuftrittKarte` already resolves published events.
- **ARC-022-2 (editor assistant).** `VorschlagKarte` and `AenderungsVorschau`
  are defined and rendered (`vorschlagId`, `titel`, `grund`, `vertrauen?` /
  `titel`, `aenderungen[{feld, bisher?, neu}]`) and `VorschlagKarte`'s buttons
  already call the proposal REST endpoints. Their server resolvers belong to
  the editor assistant's own run: give that run its own tool set and catalog
  view (offer the components with an editor resolver; the member chat must keep
  not offering them) and keep the same rules: ids in, server data out, no URL.
  The re-check on load currently uses the member predicate; an editor thread
  needs its own audience parameter before editor-only cards can be stored.
  Shared state and the approval interrupt are untouched by this slice. The
  AG-UI client is the stable 1.0.2, which already has `RUN_FINISHED` with
  `outcome: interrupt` and `resume` in `RunAgentParameters`; the `HttpAgent` is
  created per question in `streamChatAntwort`, so a panel with state or resume
  wants its own agent instance next to it. Events must keep the null-free wire
  form.
- **Every new AG-UI event or custom event** must pass the strict client: a
  Playwright mock built with `tests/chat-mock.ts` is the check.

## Verification — 2026-10-11

| Command | Result |
| --- | --- |
| `dotnet build src/archive/Archive.slnx` | 0 errors, 0 warnings |
| `dotnet test tests/archive/backend` (final code) | **631 passed, 0 failed** (the handoff recorded 605 before slice 8; new here: 22 cases in `ChatApiTests.A2ui.cs`, 1 evaluation case, 2 PostgreSQL cases that return early without `ARCHIVE_TEST_POSTGRES`; the known `ExtractionWorkerTests` flake did not occur) |
| `ARCHIVE_TEST_POSTGRES=… dotnet test tests/archive/backend --filter "Category=PostgresA2ui"` on a throwaway `postgres:17.6` container | 2 passed: migration up with an existing assistant message (kept, no surfaces), column is `jsonb`, jsonb round trip and `@>` query, down to `20261010174016_FieldProvenanceAndProposals` and up again; `ShowAsync` and `RecheckAsync` as real SQL (published-only song/event filters, `Contains` over ids, unpublish then re-check) |
| `dotnet ef migrations has-pending-model-changes --project backend` (from `src/archive`, placeholder connection string) | no changes since the last migration |
| `corepack pnpm run check` (frontend) | clean (Biome 104 files, route types, `tsc`) |
| `corepack pnpm run build` (frontend) | static export; the A2UI chunk (about 210 kB, lazy) is only requested when an answer has surfaces |
| `ARCHIVE_BASE_URL=http://localhost:3132 corepack pnpm exec playwright test tests/chat.spec.ts tests/a2ui.spec.ts tests/a2ui-katalog.spec.ts --workers=1` (static export via `python3 -m http.server`) | **130 passed** (desktop and mobile): 46 chat (mocks now in the real event shape), 84 new |
| `dotnet test tests/archive/apphost --filter "FullyQualifiedName~WalkingSkeleton"` | 5 passed against the real PostgreSQL/Azurite stack with the new migration |

Seen failing first (the new tests against the code without the change):

- All new `ChatApiTests` card cases failed before the tool, the event, the
  column and the endpoint change existed (10 of 13 failed in the first run; the
  three that passed vacuously — nothing streamed, unreadable stored JSON,
  foreign thread — were checked by mutation below).
- Mutation run on the finished code, each restored afterwards: no re-check on
  load → `ReloadDrops…`, `Tampered…`, `AnAnswerThatWasOnlyACard…` and
  `UnreadableStoredA2ui…` failed; no published filter on the song card →
  `OnlyValidVisibleRequests…` and `RequestsWithoutAnyValid…` failed; the tool
  call left on the wire → `CardQuestionStreams…` failed; nulls left in the
  events → `EventsLeaveOutNullFields…` failed.
- Browser: the strict client first refused the real server stream (ZodError on
  `usage[].model`), and five existing chat tests failed on their own mocks
  (text without a start, runs without `runId`) until the mocks were made
  protocol-conformant. With the reader made permissive (no catalog check, no
  strict processor) 24 of 42 desktop tests of `a2ui-katalog.spec.ts` and
  `a2ui.spec.ts` fail; the browser cases for kaputte messages alone still
  passed because the library's own check caught them, which shows the two
  layers each hold. The scroll-follow assertion failed on desktop before
  `folgeAntwort` measured the position before the log grew.

What the Verification section of this ticket asked for, covered by these tests:
a published song as a card with its link (`CardQuestionStreams…`,
`a2ui.spec.ts` first test), its survival across reload (`ShownCardSurvives…`,
browser reload), unpublishing it (`ReloadDropsTheCard…`, Postgres re-check),
and a malformed and an out-of-catalog message from a fake client neither
rendered (`a2ui.spec.ts` seven cases, `a2ui-katalog.spec.ts` 22 cases).

Not done: the live model was not asked (no credentials were used), so
whether Luna calls `zeige_oberflaeche` is not shown; `shell.spec.ts` was not
run (needs a real backend); the hosted check after the release applied the
migration is the maintainer's.
