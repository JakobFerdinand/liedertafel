---
id: ARC-021-1
status: planned
phase: core
kind: slice
depends_on: ["ARC-021", "ARC-022-3"]
touches: ["chatbot", "ai-evaluation", "azure-foundation"]
external_inputs: ["ai-runtime-credentials", "azure-maintainer-access"]
---

# ARC-021-1 — Switch all AI work to GPT-6 Luna

**Depends on:** [ARC-021](ARC-021-chatbot-provider-decision.md),
[ARC-022-3](ARC-022-3-chat-on-agent-framework.md).

## Outcome

The chat answers through GPT-6 Luna in the EU Data Zone, and Luna is the one
model every later AI slice uses, including image input.

## Acceptance criteria

- [ ] Replace the `gpt-5.4-mini` deployment in
  `infrastructure/archive/main.bicep` with a pinned GPT-6 Luna deployment
  (EU Data Zone Standard) and a tokens-per-minute limit that acts as the
  backstop behind the application's cap.
- [ ] There is no switch gate. Run `ChatEvaluationLive` on Luna and record the
  result next to the last `gpt-5.4-mini` run; regressions become follow-up
  work, not a rollback condition.
- [ ] Keep Chat Completions and set reasoning effort to `none`, because Luna
  rejects function calling with reasoning there. Record whether answers are
  worse; only then consider the Responses API.
- [ ] Update the reference prices in the ARC-022-3 ledger to the Luna EU Data
  Zone rates.

## Verification

Deploy, run the live chat evaluation, ask a tool-using question in the hosted
chat and confirm the ledger records Luna with the new prices.

## Handoff and parallel work

The hard cap and the generalised ledger moved to ARC-022-3. Slices that list
this issue as a dependency rely on both. Decisions:
[architecture §14](architecture.md#14-ai-assistance).
