---
id: ARC-022-3
status: in_progress
phase: core
kind: enabler
depends_on: ["ARC-022"]
touches: ["chatbot", "ai-evaluation", "db-migrations"]
external_inputs: []
---

# ARC-022-3 — Run the chat on an Agent Framework agent under a hard monthly cap

**Depends on:** [ARC-022](ARC-022-grounded-history-answer.md).

## Outcome

The member chat behaves as before but runs on a Microsoft Agent Framework
agent, and every model call in the archive passes one middleware that records
usage and stops at EUR 15 per month.

## Acceptance criteria

- [x] Replace the hand-rolled loop in `ArchiveChatService` with an Agent
  Framework agent built on the existing `IChatClient`, pinned to stable
  packages. Keep the bounded tool calls, the citation filter, the no-token and
  overall timeouts, and the German error states.
- [x] Thread history stays in the existing `ChatThread` / `ChatMessage` tables
  behind a history provider; client-supplied history is still ignored.
- [x] Keep AG-UI as the wire contract through the stable `AGUI.Server` package
  in the existing endpoint, with its membership and thread-ownership checks.
  Do not adopt the prerelease Agent Framework hosting adapter.
- [x] Add chat-client middleware below the agent that checks the ledger before
  each call and records usage after it. It is the only path to the provider
  for agents, jobs and embeddings.
- [x] Generalise and rename `chat_usage_entries` in one explicit migration:
  feature and model columns, usage summed across tool-loop iterations (today
  the maximum is kept), month key in Europe/Vienna.
- [x] Hard cap of EUR 15 per month replacing `MonthlyBudgetEur=5`; bound the
  maximum output per call. At the cap the chat returns a German
  "Monatsbudget erreicht" state and no non-AI feature changes behaviour. The
  kill switch keeps working.
- [x] `ScriptedChatClient` still drives the agent in tests; agent telemetry
  reaches the Aspire dashboard.

## Verification

Run `ChatEvaluationTests` and the chat API tests unchanged in intent against
the agent; run the live evaluation and compare with the last recorded run.
Drive the ledger to the cap with synthetic entries and confirm the chat state
and that a multi-tool run records the summed usage.

## Handoff and parallel work

ARC-021-1 switches the deployment to Luna on top of this. Every later AI slice
adds its own named agent and reaches the provider only through this
middleware. ARC-043 owns maintainer notifications for the cap. Decisions:
[architecture §14](architecture.md#14-ai-assistance).

## Implementation — 2026-10-06 (branch `main`)

Status stays `in_progress` for one reason: the verification asks for a live
evaluation rerun compared with the last recorded run, and
`ai-runtime-credentials` were not available. Everything else is implemented
and verified offline. To close: run the live command from the root
`AGENTS.md` against the pinned deployment, record the result here next to
ARC-021's 2026-09-23 run (14/14 cases) and set `status: done`.

**Agent.** `ArchiveChatService` builds a `ChatClientAgent` named
`archive-chat` per request (`Microsoft.Agents.AI` 1.23.0, stable; it needs
`Microsoft.Extensions.AI` 10.10.0, which the repo already pins). Pipeline:
`FunctionInvokingChatClient` (`MaximumIterationsPerRequest = MaxToolCalls`)
→ `CitationFilteringChatClient` → `ModelCallBoundsChatClient` (30 s no-token
window per model call, one retry of the run's first call before its first
update) → `AiGateway`. The 120 s overall bound, the question and answer caps
and the German `RUN_ERROR` states are unchanged. Tool results are still not
sent to the browser.

**History.** `ChatThreadHistoryProvider` reads the last 20 persisted turns of
the member's thread and stores the question and the filtered final answer; a
failed, timed-out, budget-stopped or tool-bound run stores the question only.
Only the last user message of the request is used.

**Wire.** `ChatEndpoints` is untouched: stable `AGUI.Server` 1.0.0, same
membership, CSRF, availability and ownership checks. No hosting adapter.

**Gateway and middleware.** `Ai/AiGateway` = OpenTelemetry →
`BudgetedChatClient` → provider `IChatClient`. The middleware reserves the
worst case of each call in the ledger before the provider is called, bounds
`MaxOutputTokens` to `Archive:Ai:MaxOutputTokensPerCall`, and settles the
real usage afterwards in a `finally`, so failed, cancelled and abandoned
streams are charged too. A call without an `AiOperation`, a model without a
price and an unreachable ledger are refused before the provider. A test
(`OnlyTheGatewayTakesTheRawProviderClient`) fails when another type takes the
provider client in its constructor.

**Ledger.** Migration `20261006111010_AiUsageLedger` renames
`chat_usage_entries` to `ai_usage_entries` in place and keeps its rows:
`Feature`, `Model`, `OperationId`, `Calls`, `ReservedMicroEur`, `UpdatedAt`
added; cost moved from rounded EUR cents to micro-EUR; token columns widened;
`AccountId` nullable for jobs; existing month keys recomputed in
Europe/Vienna. One row per operation and model holds the sum of all its
model calls (before: the maximum of one run). New table `ai_budget_months`
holds a version per month; every ledger write increments it as an optimistic
concurrency token, which makes "sum the month, compare with the cap, reserve"
atomic across requests and replicas.

**Cap.** `Archive:Ai:MonthlyCapEur` = 15 replaces
`Archive:Chat:MonthlyBudgetEur` and the logged warning. At the cap the run
ends with `RUN_ERROR`, code `monatsbudget_erreicht`, message „Monatsbudget
erreicht. …"; the chat page shows it without the retry button. Because a call
reserves its worst case first, the chat stops when the remainder no longer
covers one worst-case call, slightly before EUR 15.00 is settled.
`Archive:Chat:Disabled` still answers 503.

**Telemetry.** Source and meter `Liedertafel.Archive.Ai` are registered in
the service defaults. An OTLP collector test receives `invoke_agent
archive-chat`, `execute_tool catalogue_search`, the GenAI token metric, the
budget instruments and the `KI-Budget` log line, and no lyrics or answer text.

### Decisions that differ from the ticket text or §14

- §14 says overshoot is "bounded by the calls in flight and a maximum output
  per call". The implementation is stricter: calls in flight hold a
  reservation, so they cannot overshoot together; the residual error is the
  input estimate of a call (three characters per token) being too low.
- The scripted model's price is only in `appsettings.Development.json`. A
  non-Development host without the Azure provider therefore refuses chat
  calls instead of running the offline stand-in.
- Costs are stored in micro-EUR. Rounding every call up to a whole cent, as
  the old per-run estimate did, would have charged about ten times the real
  price against a hard cap.

### Test changes to existing tests

- `ExhaustedBudgetStillRunsWritesLedgerRowAndWarnsMaintainer` asserted the old
  rule (never stop). It is replaced by
  `ReachedMonthlyCapAnswersMonatsbudgetErreichtWithoutCallingTheProvider`.
- `UsageLedgerRecordsMonthTokensAndCost` expects the Vienna month and the
  feature; `ChatUsageEntries` / `EstimatedCostEurCents` were renamed in the
  assertions of `ChatApiTests` and `ChatEvaluationTests`.
- `PausedChatSaveInterceptor` now pauses on the save that adds a chat message
  instead of the one that adds a usage row: the ledger writes through its own
  scope, so the save that must not outlive the request scope is the history
  provider's.
- The live evaluation passes the provider settings so the ledger records the
  deployment as the model.

### Known weaknesses

- A process that dies between reservation and settlement leaves the
  reservation counted for that month. Nothing releases it; the month is
  over-counted by at most one worst-case call per crash.
- A call that fails before any usage report is charged its input estimate,
  also when the provider billed nothing.
- Under heavy contention a writer gives up after 25 optimistic retries and the
  call is refused as "ledger unavailable" (generic failure state).
- The Bicep comment next to the `Archive__Chat__*` settings still mentions the
  EUR 5 alert; `infrastructure/**` was left untouched so this slice triggers
  no infrastructure deployment.

## Verification — 2026-10-06

- `dotnet build src/archive/Archive.slnx`: 0 errors, 0 warnings.
- `dotnet test tests/archive/backend`: **564 passed, 0 failed** (545 before;
  19 new), twice in a row. `Category=ChatEvaluationLive` and
  `Category=PostgresLedger` return early with their "skipped" line when
  unconfigured. In the first full run the new OTLP test failed once while it
  waited 20 s for the export under full parallel load; it passed alone, and in
  both full runs after the wait was raised to 90 s.
- Seen failing first: the seven ledger tests against a stub ledger. The
  middleware and HTTP tests were written before their implementation but first
  run after it; a mutation check then confirmed they can fail (cap check
  disabled and usage zeroed → `ReachedMonthlyCap…`, `MultiToolRun…`,
  `ACallInFlight…` and `CompletedCallSettles…` failed, 4 of 4).
- Cap at the HTTP seam: a synthetic entry of EUR 1 under a EUR 1 cap →
  `RUN_ERROR` with `monatsbudget_erreicht`, provider called 0 times, ledger
  unchanged, `/api/songs` still answers. Summed usage: a two-call run with
  100/10 and 200/20 tokens records one row with 2 calls, 300/30 tokens and
  398 micro-EUR.
- Concurrency on real PostgreSQL (`postgres:17.6` container, all migrations
  applied to an empty database, then `Category=PostgresLedger`): 24 parallel
  reservations of 900 micro-EUR against a 5 000 micro-EUR cap gave **5
  admitted, 19 refused at the cap, 0 refused after contention** in five of
  five runs on fresh databases; the ledger held 5 rows summing 4 500, the
  month version was 5, and a second service provider ("replica") was refused.
  EF InMemory cannot show this: it has no transactions, so a save that loses
  the version check still leaves its other rows.
- Migration on the same container: applied up to `RecordingPassages`, inserted
  one old row (1 cent, created 2026-09-30 22:30 UTC, month `2026-09`), applied
  `AiUsageLedger` → `2026-10`, `chat`, `gpt-5-4-mini`, 1 call, 10 000
  micro-EUR; migrated down → the original row; up again.
  `dotnet ef migrations has-pending-model-changes --project backend`: no
  changes.
- `dotnet test tests/archive/apphost --filter "FullyQualifiedName~WalkingSkeleton"`:
  **5 passed** (fresh containers; the new migration applied through
  `archive-migrate` and the member chat ran on the agent against PostgreSQL).
- Frontend: `corepack pnpm run check` clean, `corepack pnpm run build`
  exports statically, `playwright test tests/chat.spec.ts --workers=1`
  against the served export: **46 passed** (44 before; the budget state on
  desktop and mobile is new).
- `dotnet publish` of the backend in Release succeeds with the new package;
  the container image itself was not built on this machine. The Dockerfile
  needs no change (restore from the project file).
- Not run: the live evaluation (no `ai-runtime-credentials`).
