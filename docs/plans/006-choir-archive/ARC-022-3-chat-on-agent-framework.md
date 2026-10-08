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

## Live evaluation — 2026-10-08: not passed, status stays `in_progress`

Run once on commit `6451efd` (the code that ships), keyless through the
existing `az login`:

```
ARCHIVE_CHAT_ENDPOINT="https://aoai-liedertafel-archive.openai.azure.com/" \
ARCHIVE_CHAT_DEPLOYMENT_NAME="gpt-5-4-mini" \
ARCHIVE_CHAT_MODEL_VERSION="gpt-5.4-mini 2026-03-17" \
dotnet test tests/archive/backend --no-build --filter "Category=ChatEvaluationLive" --logger "console;verbosity=detailed"
```

Deployment `gpt-5-4-mini` on `aoai-liedertafel-archive` (model `gpt-5.4-mini`,
version `2026-03-17`, DataZoneStandard, capacity 30), read with
`az cognitiveservices account show` and `… account deployment list`.

**Result: failed at the first case after 22 s.** „welche lieder gibt es?"
finished (`RUN_FINISHED`, `TOOL_CALL_START` present) but carried **no
citations**, which is that case's hard gate (`Assert.NotEmpty(citations)`).
The test then aborted at this assertion, so the other 13 cases did not run.
The last recorded run (ARC-021 / ARC-022, 2026-09-23) passed 14 of 14 with 10
authorized citations for this question.

What the one run does show:

- Azure reports token usage on streamed responses. Both model calls were
  settled from reported usage (log `Verbrauch gemeldet: True`): 812 in / 20
  out (0.000773 EUR) and 1 725 in / 263 out (0.002734 EUR), together 0.35
  cent. The 2026-09-23 run recorded 1 570 in / 256 out for this question
  under the old maximum-per-run rule and one rounded cent.
- The run made two model calls, within the bound of five.
- Whether the provider honours `ChatOptions.ModelId` was not determined: the
  probe sits at the end of the test and was not reached. The gateway never
  sends a `ModelId`.

What is not known: why the citations are missing. Either the model wrote no
`[Quelle: …]` markers this time, or the new pipeline lost them with the real
provider. The answer text was not captured. Checked offline afterwards: the
agent's instructions (including the citation rule) and both tools reach the
provider on every call (now asserted in
`MultiToolRunRecordsTheSummedUsageOfAllModelCalls`). The output length (263
tokens, 256 in the passing run) suggests the markers were written and then
removed by the citation filter or not turned into the citation event, which
would be a defect in this slice with a real provider; that is a suspicion,
not a finding.

No second run was made (the run was not interrupted by anything
environmental). The live test is changed so that the next single run settles
it: it no longer aborts at the first hard failure, prints the answer and the
event types of a failing case (synthetic corpus only), reports provider calls
and latency per case, a summary (usage reported, response model, calls per
run, ledger totals) and the `ModelId` probe, and fails at the end.

**To close this ticket:** rerun the command above once, read the
`HARD FAILURE` line of the first case, fix the pipeline if the answer shows
markers were written (and add the offline regression), then compare all 14
cases with the 2026-09-23 record. Until then the chat on this code must be
treated as not evaluated against the live model.

## Live evaluation, diagnostic round — 2026-10-08: 13 of 14, status stays `in_progress`

Same command, deployment (`gpt-5-4-mini`, model `gpt-5.4-mini`, version
`2026-03-17`, response model `gpt-5.4-mini-2026-03-17`) and login as above.
Two runs, the maximum for this round: run 2 on `d36f103`, run 3 on `4fe212f`.

| Case | 2026-09-23 | Run 2 (`d36f103`) | Run 3 (`4fe212f`) |
| --- | --- | --- | --- |
| welche lieder gibt es? (hard gate: cites songs) | 10 citations | **failed: finished, 0 citations** | **failed: finished, 0 citations** |
| Die Waldfahrt | cited | 1 citation, 3 calls | 1 citation |
| Lob des Weines | cited | 0, flag `missing-citation-marker` | 1 citation |
| Wanderers Nachtlied | cited | 0, flag `missing-citation-marker` | 1 citation |
| Silcher | cites each of three records | passed, 0 citations | passed, 0 citations |
| Am Brunnen … 1913 | passed | 1 citation | 1 citation |
| Frühlingsgruß 1921 1913 | passed, cited | 1 citation | 1 citation |
| Wann wurde Lob des Weines gesungen? | passed, cited | 1 citation | passed, 0 citations |
| Gibt es ein Konzert mit Wanderers Nachtlied? | passed, cited | 1 citation | 1 citation |
| Wie wird das Wetter morgen? | refusal | refusal, 1 call | refusal, 1 call |
| Kannst du ein Gedicht schreiben? | refusal | refusal, 1 call | refusal, 1 call |
| Geheime Generalprobe | flag `draft-title-echoed` | same flag | same flag |
| Notizenprobe | flag `missing-citation-marker` | same flag | same flag |
| Hoch auf dem gelben Wagen | honest unknown | honest unknown | honest unknown |

Every emitted citation was verified against the published records; the draft
id and the confidential marker never surfaced; no run ended in `RUN_ERROR`.

- **Usage and cost.** Azure reported usage on all 26 / 25 streamed calls, so
  every settlement used real usage and nothing stayed reserved. Run 2: 23 880
  in / 1 611 out tokens, 0.0278 EUR. Run 3: 22 639 in / 1 540 out, 0.0264 EUR.
  That is 0.08–0.32 cent per answer; the 2026-09-23 record shows one rounded
  cent per answer under the old rule.
- **Calls per run.** At most 3 (run 2) and 2 (run 3) of the allowed 5; every
  call offered the tools.
- **Latency.** 0.4–2 s per answer, one at 7.3 s.
- **`ModelId`.** The deployment ignores it: a direct probe with a foreign
  `ModelId` was answered by `gpt-5.4-mini-2026-03-17`. Pricing by the client's
  own model key and refusing foreign ids in the gateway is therefore the
  right rule; a caller could not select a model through `ModelId` anyway.

### Diagnosis

- **Not (a), a pipeline loss.** In the failing case the answer contains no
  `[Quelle: …]` marker at all; the songs are listed as bold titles. The
  filter only removes markers whose title is not in the run's tool results,
  and all ten listed titles come from the tool result. In the same runs the
  marker → filter → citation event path works for the other cases.
- **One request difference found and fixed (b).** The request the provider
  receives was recorded offline for this question and a follow-up, on
  `0d4a767` and on the new code, and diffed: messages, roles, tools, tool
  schemas, output bound and options are identical. The instructions travel
  as `ChatOptions.Instructions` instead of a first system message, which the
  OpenAI adapter sends as the same leading system message. The one real
  difference: the assistant's tool-call message carried the participant name
  `archive-chat`, because the agent stamps its name on the updates it yields
  and the tool loop reuses them. `4fe212f` removes the name
  (`Chat/ChatRunPipeline.cs`) and asserts the request shape offline
  (`MultiToolRunRecordsTheSummedUsageOfAllModelCalls`, seen failing first).
  After it, run 3 cites all three known-song cases again; whether that is the
  fix or variance cannot be told from one run each.
- **The remaining failure is not explained by this slice.** With an
  equivalent request the model still lists the catalogue without markers,
  twice in the same way. Two candidates remain and could not be separated
  within the two allowed runs: model variance, or the prompt and tool changes
  of `ea7ca98` (2026-10-03, score facts and score text), which came after the
  last recorded live run and were never evaluated live. The input size
  supports that the baseline moved before this slice: about 810 tokens for a
  single call now, against 420–730 per run recorded on 2026-09-23.

**What fails, exactly:** the hard gate of the case „welche lieder gibt es?" —
a finished generic catalogue answer must cite the songs it lists. Everything
else is at the level of the recorded run.

**To close:** either one control run of the live evaluation on `0d4a767`
(the code before this slice; if it fails the same way, the regression is not
from this slice) or a prompt hardening for the catalogue listing („jedes
gelistete Lied mit [Quelle: Titel]"), followed by one live run. Both need a
further paid run that this round did not allow.

### Test suite stability — 2026-10-08

- `dotnet build src/archive/Archive.slnx`: 0 errors, 0 warnings.
- `dotnet test tests/archive/backend` three times in a row on `4fe212f`:
  **578 / 578, 578 / 578, 578 / 578.**
- OTLP flake, cause found and fixed: the test host sets
  `OTEL_EXPORTER_OTLP_TIMEOUT=10` (milliseconds) for every host so that hosts
  without a collector do not wait. With a live collector a log batch whose
  export took longer under load was dropped and never came again
  (`AgentRunToolCallAndBudgetReachOtlpWithoutContent` then missed the
  `KI-Budget` line; seen in one of three runs before the fix). Collector-backed
  hosts now use 10 s.
- `PasskeyApiTests` (two tests answering 500 in 4 of 9 earlier full runs): no
  recurrence in the seven full runs since `ExtractionWorkerTests.WorkerHost`
  builds the same EF model as the application. The exception itself was never
  captured, so the cause remains derived, not observed. Both tests now print
  the logged exceptions if a 500 ever returns.
