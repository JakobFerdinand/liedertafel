---
id: ARC-022
status: done
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

Implemented 2026-09-23 as the first bounded slice on the provider seam
(ARC-021 decision plus its hosting-adapter supersession). ARC-032 and ARC-035
were not implemented when this slice landed, and no events/programmes data
exists yet (ARC-024/026/028 unbuilt): the tool surface therefore degrades to
catalogue search and song details over member-visible catalogue text, and
performance questions receive the honest „keine Aufführungsdaten" answer
until those slices land.

## Outcome

A member asks a historical question and receives a concise German answer with
links to the same authorized records they can inspect through ordinary search.

## Acceptance criteria

- [x] Add a bounded chat input/API using approved retrieval queries and provider
  settings; keep model credentials and authorization on the server (built on
  the AG-UI 1.0 wire contract with the offline provider seam; the real Azure
  OpenAI credential setup is the provider step, `ai-runtime-credentials`).
- [x] Cite actual visible records, distinguish confirmed performance from
  programme evidence, and acknowledge incomplete history/unknown answers
  (catalogue citations today; performance questions answer honestly that no
  Aufführungsdaten exist until ARC-024/026/028/031 provide them).
- [x] Treat document text as data, not instructions; expose no editing tools or
  unrestricted database/model-generated query execution.
- [x] Enforce current membership, per-request/aggregate cost bounds, cancellation
  and useful failure states. Do not leak drafts through responses or citations.

## Verification

Exercise known/unknown songs, partial dates, conflicting evidence, hidden records,
revoked membership, malicious instructions embedded in text and exhausted budget.
Inspect every citation against the authorized source result set.

## Handoff and parallel work

This slice does not require member corrections or duplicate merging to be
finished. Coordinate changes to the stable search/history result contracts.

## Implementation — 2026-09-23 (branch `main`)

Backend, provider seam first (`4043360`):

- Packages pinned exactly: `Microsoft.Extensions.AI` 10.10.0,
  `AGUI.Abstractions` 1.0.0, `AGUI.Server` 1.0.0 (stable AG-UI 1.0 .NET SDK,
  superseding the preview MAF hosting adapter per ARC-021). No MAF, no
  Azure.AI.OpenAI, no frontend AI packages yet.
- `Chat/ChatOptions.cs` bound to `Archive:Chat`: `Enabled` (default false),
  `Disabled` (the maintainer's manual kill switch), `MonthlyBudgetEur` 5,
  `MaxToolCalls` 5, `NoTokenSeconds` 30, `OverallSeconds` 120,
  `MaxQuestionChars` 2000, `MaxAnswerChars` 8000, reference prices
  0.83/4.95 EUR per 1M tokens for the cost estimate.
- `Chat/ChatEntities.cs` + migration `20260922214454_ArchiveChat`:
  `chat_threads` (member-owned, plain `AccountId` column like the catalogue
  attribution columns), `chat_messages` (role user/assistant, content,
  cascade), `chat_usage_entries` (per-request `yyyy-MM` month, tokens, rounded
  EUR-cent estimate). Threads persist in the existing PostgreSQL/Neon
  database.
- `Chat/CatalogueTools.cs`: `catalogue_search` and `song_details` as
  `AIFunctionFactory` tools with German descriptions; authorization lives
  inside the tools (published-only via `CatalogueVisibility`), query ≤ 200
  chars folded with `CatalogueText.Fold`, token-AND matching like ARC-020,
  page ≤ 5, ≤ 10 results, lyrics excerpt ≤ 300 chars. The model can never
  reach drafts or run raw queries.
- `Chat/ScriptedChatClient.cs`: deterministic offline `IChatClient` behind
  the provider seam (Development default and tests): cited German answers
  from tool results only, honest unknown/refusal phrases, performance
  questions answered honestly without inventing dates, document text treated
  as data. The real Azure OpenAI implementation (managed identity, pinned
  GPT-5.4-mini, EU Data Zone) plugs in at this seam when
  `ai-runtime-credentials` exist; embeddings/pgvector move with it.
- `Chat/ArchiveChatService.cs`: the bounded loop — server-side history
  (last ≤ 20 persisted messages, client history ignored as tamper-proof
  grounding), thread ownership validated on every request (foreign thread →
  403, unknown id created owned by the asker), system prompt enforcing
  archive scope/citations/data-not-instructions, ≤ 5 tool iterations, 30 s
  no-token abort and 120 s overall cap, one pre-first-token retry structure,
  aborts emitted as AG-UI `RUN_ERROR` with German copy. Usage is recorded per
  run; the monthly sum is checked against `MonthlyBudgetEur` and, when
  exceeded, raises the maintainer log warning — never an automatic disable
  (EUR 5 semantics per ARC-021). Question/answer content is never logged.
- `Chat/ChatEndpoints.cs`: `POST /api/chat` (manual antiforgery 400, member
  401s, 503 „Der Archiv-Chat ist derzeit nicht verfügbar." when
  `Enabled=false` or `Disabled`, raw-body `RunAgentInput` deserialized with
  the AG-UI serializer options, streamed via
  `TypedResults.ServerSentEvents` over `AsAGUIEventStreamAsync`) and
  `GET /api/chat/thread/{id}` (owner-only history restore, foreign/missing
  indistinguishable 404). The client generates the thread id and learns the
  resolved one from `RUN_STARTED`.
- `tests/archive/backend/ChatApiTests.cs` (`e28024f`): 12 tests over the
  auth/CSRF/availability gates, the streamed event sequence, thread
  persistence and ownership, the 20-message bound, draft non-leakage,
  instruction-in-data, usage rows and the too-long question.

Frontend (`c4b3680`):

- `lib/chat.ts`: typed AG-UI SSE consumer (POST with the CSRF pair,
  incremental CRLF-tolerant `data:` parsing, callbacks for resolved
  threadId/assistant deltas/citations/errors, ProblemDetails titles surfaced
  in German, abort support); `ladeChatVerlauf` for thread restore;
  `arc-chat-thread` in localStorage.
- `components/chat-bereich.tsx` + `app/fragen/page.tsx` + nav entry: the
  member-gated „Fragen zum Archiv" chat — history restore, bounded textarea
  with character counter, Absenden/Abbrechen (client abort cancels the
  server run), citation chips linking to the song detail route
  (`/lied/?id=…`), German states for unavailable/re-login/retry, aria-live
  status. No new frontend dependency (AG-UI consumed directly).
- `tests/chat.spec.ts`: 5 mocked Playwright tests × desktop/mobile over the
  streamed answer with citation chips, history restore, 503 and RUN_ERROR
  states.

## Verification — 2026-09-23

- `dotnet build src/archive/Archive.slnx` 0 errors; `dotnet test
  tests/archive/backend` **246/246 green** (230 prior + 16 evaluation/chat
  tests), stable across repeated runs.
- `dotnet test tests/archive/apphost` **4/4 green** (fresh containers; the
  new `ArchiveChat` migration applied cleanly on real PostgreSQL and the
  walking-skeleton assertions still hold).
- Frontend `pnpm run check` clean; `pnpm run build` statically exports all
  12 routes including `/fragen`; mocked Playwright suite green except the
  real-backend `shell.spec.ts` smoke tests (no dev backend during authoring —
  same record as ARC-019/020).
- The synthetic evaluation (ARC-021 section above) doubles as this slice's
  rehearsal: citations verified against the authorized result set, zero
  unsupported claims, honest unknown behaviour, per-answer EUR estimate
  recorded. The live-model rerun against the pinned GPT-5.4-mini is the
  gate before production chat is enabled (provider step with
  `ai-runtime-credentials`).
