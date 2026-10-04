---
id: ARC-022-3
status: planned
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

- [ ] Replace the hand-rolled loop in `ArchiveChatService` with an Agent
  Framework agent built on the existing `IChatClient`, pinned to stable
  packages. Keep the bounded tool calls, the citation filter, the no-token and
  overall timeouts, and the German error states.
- [ ] Thread history stays in the existing `ChatThread` / `ChatMessage` tables
  behind a history provider; client-supplied history is still ignored.
- [ ] Keep AG-UI as the wire contract through the stable `AGUI.Server` package
  in the existing endpoint, with its membership and thread-ownership checks.
  Do not adopt the prerelease Agent Framework hosting adapter.
- [ ] Add chat-client middleware below the agent that checks the ledger before
  each call and records usage after it. It is the only path to the provider
  for agents, jobs and embeddings.
- [ ] Generalise and rename `chat_usage_entries` in one explicit migration:
  feature and model columns, usage summed across tool-loop iterations (today
  the maximum is kept), month key in Europe/Vienna.
- [ ] Hard cap of EUR 15 per month replacing `MonthlyBudgetEur=5`; bound the
  maximum output per call. At the cap the chat returns a German
  "Monatsbudget erreicht" state and no non-AI feature changes behaviour. The
  kill switch keeps working.
- [ ] `ScriptedChatClient` still drives the agent in tests; agent telemetry
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
