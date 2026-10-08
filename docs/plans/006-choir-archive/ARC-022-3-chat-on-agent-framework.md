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

**Agent.** `Chat/ArchiveChatAgent.cs` builds the `ChatClientAgent` named
`archive-chat` once per process on the gateway and wraps it with the Agent
Framework's OpenTelemetry instrumentation (`Microsoft.Agents.AI` 1.23.0,
stable; it needs `Microsoft.Extensions.AI` 10.10.0, already pinned).
`ArchiveChatService` supplies each run through the run options: tools on the
request's database context, the thread's history provider, and the run
pipeline `FunctionInvokingChatClient` → `CitationFilteringChatClient` →
`ModelCallBoundsChatClient` on top of the gateway.

**Bounds.** A run makes at most `MaxToolCalls` (5) model calls and every one
offers the tools. When the last allowed call still asks for a tool, the tool
is not executed, the loop ends and the run stops with the German failure
state; there is no further call with the tools taken away. The 30 s no-token
window per call, one repetition of the run's first call before its first
update, the 120 s overall bound and the question and answer caps are as in
ARC-022. Tool results are still not sent to the browser.

**History.** `ChatThreadHistoryProvider` reads the last 20 persisted turns of
the member's thread and stores the question and the filtered final answer; a
failed, timed-out, budget-stopped or bound-stopped run stores the question
only. Only the last user message of the request is used. A failed save of the
turn is attempted once: the member sees the failure state and the ledger
keeps the run's real cost once.

**Wire.** `ChatEndpoints` is untouched: stable `AGUI.Server` 1.0.0, same
membership, CSRF, availability and ownership checks. No hosting adapter.

**Gateway and middleware.** `Ai/AiGateway` = OpenTelemetry →
`BudgetedChatClient` → provider. The raw provider is registered only under
the service key `AiGateway.ProviderKey`; `IChatClient` resolves to the
budgeted pipeline. `AddArchiveAi(configuration)` registers provider
selection, model keys (`AiModels`), store, budget and gateway in one call.
The middleware refuses a call without an `AiOperation`, a `ModelId` other
than its own model, non-text input without the feature's estimate, an
unpriced or zero-priced model and an unreachable ledger, all before the
provider. It bounds `MaxOutputTokens` (the feature may lower the configured
ceiling, never raise it), reserves the worst case, and settles in a
`finally`: the reported usage, or the full reservation when the call ended
without a usage report. `BudgetedEmbeddingGenerator` does the same for an
embedding provider registered under the same key.

**Ledger.** Migration `20261006111010_AiUsageLedger` renames
`chat_usage_entries` to `ai_usage_entries` in place and keeps its rows:
`Feature`, `Model`, `OperationId`, `Calls`, `ReservedMicroEur`, `UpdatedAt`
added; cost moved from rounded EUR cents to micro-EUR; token columns widened;
`AccountId` nullable for jobs; existing month keys recomputed in
Europe/Vienna. One row per operation and model holds the sum of all its
model calls (before: the maximum of one run). `ai_budget_months` holds one
lock row per month. `Ai/PostgresAiLedgerStore`: a reservation is one
transaction that inserts the month row if missing, locks it with `SELECT …
FOR UPDATE` (5 s lock timeout), sums the month and upserts the operation's
row; a settlement is one relative `UPDATE` of that row, retried three times,
then logged at error and counted (`archive.ai.budget.settlement_failures`).
The first version of this slice used an optimistic version column instead;
it lost a third of the calls and some settlements at 32 parallel workers and
was replaced before the migration was pushed (the column is gone from the
migration).

**Cap.** `Archive:Ai:MonthlyCapEur` = 15 replaces
`Archive:Chat:MonthlyBudgetEur` and the logged warning. Guarantee: settled
cost + open reservations + the worst case of a newly admitted call never
exceeds the cap. At the cap the run ends with `RUN_ERROR`, code
`monatsbudget_erreicht`, message „Monatsbudget erreicht. …"; the chat page
shows it without the retry button. The chat therefore stops when the
remainder no longer covers one worst-case call, slightly before EUR 15.00 is
settled. `Archive:Chat:Disabled` still answers 503.

**Telemetry.** Source and meter `Liedertafel.Archive.Ai` are registered in
the service defaults; `AiExceptionRedactionProcessor` reduces failed spans of
that source to the exception type, and the model-call instrumentation gets no
logger (it would log provider exception messages). OTLP collector tests
receive `invoke_agent archive-chat`, `execute_tool catalogue_search`, the
GenAI token metric, the budget instruments and the `KI-Budget` log line, and
neither lyrics, answer text nor a provider exception message.

### Owner decision: one-release rename (2026-10-06)

The in-place rename ships as one release. This deliberately deviates from
the split-rename compatibility rule in `infrastructure/archive/README.md`
("Rollback is code-only … valid only while that image stays
schema-compatible"). A short chat downtime during the release is accepted. A
code-only rollback to the previous image breaks the chat, because that image
writes `chat_usage_entries`; the release is fix-forward only for chat. The
exception and the operator steps are recorded next to the rule and in
`src/archive/README.md`.

### Deployment checklist for the release of this slice

1. Before: confirm 0.83 / 4.95 EUR per million input / output tokens against
   the current Azure price of the `gpt-5-4-mini` EU Data Zone deployment;
   correct `Archive:Ai:Models` if it changed.
2. If `aiChatDeploymentName` is overridden anywhere, set both
   `Archive__Ai__Models__<deployment>__InputPricePerMillionEur` and
   `…__OutputPricePerMillionEur`; otherwise every run is refused
   (`unpriced_model`).
3. Never set `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`.
4. Release (migration `AiUsageLedger` runs through `archive-migrate`). Expect
   chat failures between migration and the new revision taking traffic.
5. After: run `infrastructure/neon/runtime-grants.sql` as `archive_migrator`.
6. Check `SELECT has_table_privilege('archive_runtime', 'ai_budget_months', 'SELECT, INSERT, UPDATE')`
   and the same for `ai_usage_entries`.
7. Smoke one chat run as a member; expect a row in `ai_usage_entries` for the
   current month with `"ReservedMicroEur" = 0` and one row in
   `ai_budget_months`.
8. Do not roll back to the previous image for a chat problem; set
   `Archive__Chat__Disabled=true` and fix forward.

### Decisions that differ from the ticket text or §14

- §14 says overshoot is "bounded by the calls in flight and a maximum output
  per call". The implementation is stricter: calls in flight hold a
  reservation, so they cannot overshoot together; the residual error is the
  text-input estimate of a call (three characters per token) being too low
  on a call that does report usage.
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
- `ToolCapExceededEndsWithRunErrorAndNoAssistantMessage` now counts provider
  calls for bounds 1, 2 and 5, and `LoopingChatClient` no longer returns a
  tool call when no tools are offered.
- `PersistenceFailureStaysVisibleAsRunErrorWithoutLedgerRow` is split into
  `UnwritableLedgerEndsAsRunErrorBeforeTheProviderIsCalled` and
  `HistorySaveFailureAfterAPaidAnswerStaysVisibleAndIsChargedOnce`.
- `UsageLedgerRecordsMonthTokensAndCost` expects the Vienna month and the
  feature; `ChatUsageEntries` / `EstimatedCostEurCents` were renamed in the
  assertions of `ChatApiTests` and `ChatEvaluationTests`.
- `PausedChatSaveInterceptor` pauses on the save that adds a chat message
  instead of the one that adds a usage row: the ledger writes through its own
  scope, so the save that must not outlive the request scope is the history
  provider's.
- The test host swaps the provider under the gateway's service key and uses
  `InMemoryAiLedgerStore`, because the production store is SQL.

### Handoff

- ARC-021-1: change `Archive:Chat:DeploymentName`, add the deployment's
  price under `Archive:Ai:Models`, rerun the live evaluation.
- ARC-022-1 (tools): add the tool to the `Tools` list in
  `ArchiveChatService.RunAgentAsync`, authorization inside the tool.
- ARC-022-4: `RunAgentAsync` consumes `agent.RunStreamingAsync` and feeds
  `AsAGUIEventStreamAsync`; tool results are filtered out there and the
  citation event is added there.
- ARC-034-1 and other non-text input: state `AiOperation.NonTextInputTokens`
  and, where useful, a lower `MaxOutputTokens`.
- Jobs: call `AddArchiveAi(configuration)` in the job host; ask
  `IAiBudget.WouldAdmitAsync` before re-enqueueing `WaitingForBudget` work.
- **ARC-052 must decide deliberately how search behaves at the cap.** §14
  says search keeps working because stored embeddings cost nothing to query,
  but the query embedding is computed in the request and passes this budget:
  at the cap `BudgetedEmbeddingGenerator` refuses it. ARC-052 has to choose
  (lexical-only fallback at the cap is the obvious candidate) and test it.
  It registers its provider under `AiGateway.ProviderKey`, sets
  `AiModels.Embedding` and a price, and uses `AiGateway.EmbeddingGenerator`.

### Known weaknesses and follow-ups

- A process that dies between reservation and settlement strands one
  reservation (about 1–5 cents for a chat call) until the month ends. There
  is no expiry.
- A call without a usage report costs its full reservation, also when the
  provider billed nothing (for example a connection failure).
- A reservation waits at most 5 s for the month lock, then the call is
  refused as "ledger unavailable" (generic failure state).
- The Bicep comment next to the `Archive__Chat__*` settings in
  `infrastructure/archive/main.bicep` still mentions the EUR 5 alert. No
  `.bicep` file was touched; correct the comment with the next
  infrastructure change.

## Verification — 2026-10-06

- `dotnet build src/archive/Archive.slnx`: 0 errors, 0 warnings.
- `dotnet test tests/archive/backend`: **578 passed, 0 failed** on the
  committed code (545 before this slice). The PostgreSQL-only and live cases
  return early with their "skipped" line when unconfigured.
- A flake surfaced while verifying and is fixed: in several full runs two
  `PasskeyApiTests` answered 500. `ExtractionWorkerTests.WorkerHost` built an
  in-memory context without the Identity schema version; EF shares one cached
  model between in-memory contexts with equal options, so when that context
  built the model first the passkey tables were missing in other test hosts.
  The worker host now supplies the same Identity options. The cause was
  derived from the code (both failing tests are the ones that read passkeys),
  not captured from a failing run; after the fix the full suite passed.
- Seen failing first in the follow-up: on `postgres:17.6` against the first
  implementation the contention test gave **114 of 320 refused, 18
  settlements lost, 16 200 micro-EUR stranded**; the zero-price,
  unreported-usage and broken-store ledger tests; the tool bound for 1, 2 and
  5 calls; the foreign `ModelId`; the container resolution test; the
  exception-message export (logs and, with the processor removed, traces).
  The two history-failure tests (N6) passed against the existing code.
- Ledger on real PostgreSQL (`postgres:17.6`, every test on its own migrated
  database, production store), 12 of 12 passed in four consecutive runs:
  contention 32 workers × 10 rounds of reserve → 0–30 ms → settle:
  **0 refused, 320 rows, 0 settlements lost, reserved 0, cost 28 800
  micro-EUR (exact)** in about 7 s; cap race 24 callers on a 5 000 micro-EUR
  cap: **5 admitted, 19 refused at the cap**, second replica refused; two
  replicas opening a month: 8 of 8 admitted, one month row; settlement after
  the month rolled over charged October, November stayed empty.
- Migration (amended, unpushed) on the same server with two pre-existing
  `chat_usage_entries` rows: up (months `2026-10` / `2026-09` in Vienna time,
  10 000 / 20 000 micro-EUR, `ai_budget_months` with one column), down (the
  original rows), up again. `dotnet ef migrations has-pending-model-changes`:
  no changes.
- `dotnet test tests/archive/apphost --filter "FullyQualifiedName~WalkingSkeleton"`:
  **5 passed**. The member chat run there now also asserts the ledger on
  PostgreSQL after all migrations through `GET /api/dev/ai-budget`: one new
  row, two calls, cost above zero, nothing reserved, spend equal to cost, one
  month row, cap 15 000 000 micro-EUR.
- Frontend: unchanged in the follow-up. From the first commit:
  `corepack pnpm run check` clean, `corepack pnpm run build` exports
  statically, `chat.spec.ts` with `--workers=1` 46 passed.
- The container image was not built on this machine; a Release
  `dotnet publish` of the backend succeeded with the first commit.
