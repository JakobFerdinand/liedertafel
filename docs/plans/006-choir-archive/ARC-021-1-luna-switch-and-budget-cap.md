---
id: ARC-021-1
status: planned
phase: core
kind: slice
depends_on: ["ARC-021", "ARC-022"]
touches: ["chatbot", "ai-evaluation", "azure-foundation", "db-migrations"]
external_inputs: ["ai-runtime-credentials", "azure-maintainer-access"]
---

# ARC-021-1 — Switch all AI work to GPT-6 Luna under a hard monthly cap

**Depends on:** [ARC-021](ARC-021-chatbot-provider-decision.md),
[ARC-022](ARC-022-grounded-history-answer.md).

## Outcome

The chat answers through GPT-6 Luna in the EU Data Zone at the same evaluated
quality as today, and the application itself stops AI spending at EUR 15 per
month while the rest of the archive keeps working.

## Acceptance criteria

- [ ] Add a pinned GPT-6 Luna deployment (EU Data Zone Standard) to
  `infrastructure/archive/main.bicep` beside the existing deployment; keep
  `gpt-5.4-mini` until the gate below has passed, then remove it.
- [ ] Gate: `ChatEvaluationLive` passes on Luna at the pass rate recorded for
  `gpt-5.4-mini`. If it fails, the chat stays on `gpt-5.4-mini` and this
  issue records the failing cases; other AI work may still use Luna.
- [ ] Add a vision evaluation with a small fixed set of real scanned scores and
  programme photos and expected fields; it must pass before ARC-034-1 starts.
- [ ] Decide and record how the tool loop runs on Luna: Chat Completions with
  reasoning effort `none`, or the Responses API. Luna rejects function calling
  with reasoning on Chat Completions.
- [ ] Replace `MonthlyBudgetEur=5` alert-plus-manual-disable with a shared
  EUR 15 hard cap checked before every AI call (chat, jobs, embeddings) from
  the usage ledger; update the reference prices to the Luna EU Data Zone rates.
- [ ] At the cap: chat and assistant return a German "Monatsbudget erreicht"
  state, background AI jobs stay queued and resume in the next month, and no
  non-AI feature changes behaviour. The existing kill switch keeps working.
- [ ] Generalise the usage ledger from chat-only to any AI caller, recording
  the feature that spent the tokens.

## Verification

Run the live chat evaluation against both deployments and record pass rates and
cost per run. Drive the ledger to the cap with synthetic entries and verify the
chat state, a queued job that resumes after the month rolls over, and that
upload, search and forms are unaffected.

## Handoff and parallel work

Every later AI slice calls the model through the capped path from this issue.
ARC-043 owns the maintainer-facing thresholds and notifications for this cap;
this issue owns the enforcement. Decisions:
[architecture §14](architecture.md#14-ai-assistance).
