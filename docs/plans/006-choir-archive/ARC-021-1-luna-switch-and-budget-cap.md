---
id: ARC-021-1
status: in_progress
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

The code side is written and committed in two stages (see "Two stages and what
a push does"). Nothing is deployed and no live call was made; every box that
needs a deployed Luna deployment or a live call stays open.

- [ ] Replace the `gpt-5.4-mini` deployment in
  `infrastructure/archive/main.bicep` with a pinned GPT-6 Luna deployment
  (EU Data Zone Standard) and a tokens-per-minute limit that acts as the
  backstop behind the application's cap. Written (stage B) and
  `az bicep build` clean; tick when the infrastructure workflow has deployed it.
- [ ] There is no switch gate. Run `ChatEvaluationLive` on Luna and record the
  result next to the last `gpt-5.4-mini` run; regressions become follow-up
  work, not a rollback condition. **Open:** needs the deployed deployment and
  `az login`; the result goes into "Live evaluation on Luna" below.
- [ ] Keep Chat Completions and set reasoning effort to `none`, because Luna
  rejects function calling with reasoning there. Record whether answers are
  worse; only then consider the Responses API. **Half done:** `none` is sent on
  every provider request and proven offline on the wire; whether the service
  accepts it on the api-version in use, and whether answers are worse, are open
  until the live runs.
- [x] Update the reference prices in the ARC-022-3 ledger to the Luna EU Data
  Zone rates. `Archive:Ai:Models:gpt-6-luna` is 0.1056 / 0.528 EUR per million
  tokens, read in EUR from the Azure Retail Prices API (see below).

## Verification

Deploy, run the live chat evaluation, ask a tool-using question in the hosted
chat and confirm the ledger records Luna with the new prices.

- [ ] Gate before stage A is pushed: `ChatEvaluationLive` (or one equivalent
  call) against the **existing** `gpt-5-4-mini` deployment accepted
  `reasoning_effort: none`.
- [ ] Stage A released, stage B deployed.
- [ ] Live evaluation on Luna recorded.
- [ ] Hosted tool-using question answered; `ai_usage_entries` shows model
  `gpt-6-luna` and a cost computed with 0.1056 / 0.528.

## Implementation (2026-10-11)

Facts and sources, all read on 2026-10-11:

| Fact | Value | Source |
| --- | --- | --- |
| Model name and version | `gpt-6-luna`, version `2026-09-22` | Microsoft Learn, "Foundry Models sold by Azure", GPT-6 table (page updated 2026-10-08); the region-availability page lists the same pair |
| Chat Completions, tools, image input | all supported; context 1,050,000 tokens (922,000 in, 128,000 out) | same page |
| `reasoning_effort` incl. `none` | supported for Luna | Microsoft Learn, "Azure OpenAI reasoning models", feature table |
| Function tools with reasoning on Chat Completions | the page documents the rejection for `gpt-5.6-*` and the `none` workaround; it does not name Luna in that passage. This ticket's premise (Luna rejects it too) is taken as given and `none` is safe either way | same page, "Tool calling with reasoning models" |
| EU Data Zone Standard rates, short context, **in EUR** | **0.1056 input, 0.528 output per million tokens** (meters `6-luna ShortCo Inp Std DZ` and `6-luna ShortCo Opt Std DZ`, `germanywestcentral`, effective 2026-09-01) | Azure Retail Prices API (`prices.azure.com/api/retail/prices?currencyCode='EUR'`, `productName` `Azure OpenAI GPT6`), queried 2026-10-11 |
| Same meters in USD | 0.12 / 0.60 | same API without `currencyCode` |
| Long context (input above about 272,000 tokens; the threshold is from a third-party mirror of the same API, not from Microsoft's own pages) | 0.2112 / 0.792 EUR (0.24 / 0.90 USD) | same API |
| Official price page | still shows `$-` for Luna ("prices are currently in processing for publishing") | azure.microsoft.com pricing page; the Retail Prices API is Microsoft's own machine-readable source |

Changes:

- `infrastructure/archive/main.bicep` (stage B): `aiChatDeploymentName` defaults
  to `gpt-6-luna`; the deployment declares model `gpt-6-luna`, version
  `2026-09-22`, `DataZoneStandard`, capacity 30 (30k tokens per minute),
  `OnceCurrentVersionExpired`. The app's `Archive__Chat__DeploymentName` takes
  `aoaiChatDeployment.name`, so the compiled template has the container app
  `dependsOn` the deployment resource and no revision points at a deployment
  that does not exist yet. The stale "EUR 5 alert" comment is replaced by the
  real cap. `az bicep build --file infrastructure/archive/main.bicep --stdout`
  succeeds (warning only: a newer Bicep release exists).
- **Tokens per minute: 30k, unchanged.** It is a burst limiter, not a budget:
  a saturated minute of all-output traffic costs about 0.016 EUR, a runaway
  loop at most about 1 EUR per hour, while the ledger refuses everything past
  EUR 15 per month. It is far above what five members need, and the quota
  admission for the old model in this region was verified at 200 (Luna's is
  not verified, see below).
- **Deployment rename, not an in-place model change.** The deployment is a new
  resource named `gpt-6-luna`. Incremental mode never deletes the old
  `gpt-5-4-mini` deployment, so the what-if is Create plus a Modify of the
  container app's env and passes the Delete/Replace guard; changing the model
  of an existing deployment in place could not be checked without a what-if.
  The old deployment stays as a rollback path until the maintainer deletes it.
- `src/archive/backend/appsettings.json` (stage A): `Archive:Ai:Models` prices
  `gpt-6-luna` at 0.1056 / 0.528. **The `gpt-5-4-mini` entry stays** so the
  image of stage A, which still runs against the old deployment, keeps working,
  and for a rollback to it. Remove it together with the old deployment.
- `src/archive/backend/Chat/AzureOpenAIChatClient.cs` (stage A): the provider
  client is wrapped with `ChatClientBuilder.ConfigureOptions`, which sets
  `ChatOptions.Reasoning.Effort = ReasoningEffort.None` unless the caller chose
  an effort (Microsoft.Extensions.AI 10.10; `Microsoft.Extensions.AI.OpenAI`
  maps it to `reasoning_effort` on Chat Completions). A new internal overload
  takes a credential and client options so a test can read the request. This
  applies to **every** deployment the app talks to, including `gpt-5-4-mini`
  until stage B is deployed; hence the gate below.
- **Currency.** The Retail Prices API returns EUR for the same meters
  (EUR/USD 0.88 in that response), so the ledger uses real EUR prices, not USD
  figures entered as EUR. The retained `gpt-5-4-mini` entry (0.83 / 4.95) is
  what ARC-021 recorded as reference retail values in USD and was entered as
  EUR without conversion; it is not re-derived here because the deployment is
  going away.
- **Long-context tier (follow-up guard).** Above about 272,000 input tokens
  Azure bills Luna at roughly twice the rates. The ledger holds one price pair,
  so a call beyond that size would be under-reserved. Chat stays far below it.
  The image and OCR slices (ARC-025-1, ARC-034-1, ARC-036) must either cap the
  input of a call under the threshold or add a long-context price to the
  ledger before they send large inputs.

Tests (written first, seen failing for the right reason):

- `AzureOpenAIChatClientTests` (6, stage A): streaming and non-streaming
  requests carry `"reasoning_effort":"none"` (failed with the property missing
  before the change); a feature's own effort is kept and the caller's options
  are not modified; the request uses `api-version=2024-10-21` on
  `/openai/deployments/gpt-6-luna/chat/completions` (a tripwire, see the gate
  below); the shipped `appsettings.json` prices `gpt-6-luna` at 0.1056 / 0.528
  and `gpt-6-sol` and an unknown name have no price.
- `AiBudgetLedgerTests.TheShippedConfigurationAdmitsLunaAndRefusesAnyOtherDeployment`
  (stage A): with the shipped options a reservation for `gpt-6-luna` is
  admitted at 633,600 micro-EUR for one million tokens each way, another
  deployment is refused (`unpriced_model`) before anything is written.
- `InfrastructureDeploymentTests` (1, stage B): the default of
  `aiChatDeploymentName` is a priced model key and the chat deployment
  resource is named by that parameter.

Results on 2026-10-11:

- `dotnet build src/archive/Archive.slnx`: 0 warnings, 0 errors, at stage A and
  at stage B.
- Stage A alone (`main.bicep` unchanged): the focused tests
  (`AzureOpenAIChatClient`, `AiBudget`, `BudgetedChatClient`, `ChatApi`,
  `ChatEvaluation`, `RuntimeGrants`) pass, 87 of 87.
- Stage B (the full tree), `dotnet test tests/archive/backend`: **605 passed,
  1 failed of 606** (598 before this slice; 8 new tests). The failure,
  `ExtractionWorkerTests.DeadlineExpiryNeverRewritesANewerLeaseOrReleasedRow`
  (a wall-clock assertion, expected 1:00, actual 0:30), is unrelated and
  passed on rerun together with the rest of its class (55 of 55 with
  `InfrastructureDeploymentTests`). The ledger tests ran on the in-memory
  store; the PostgreSQL cases were not run (`ARCHIVE_TEST_POSTGRES` unset).
- `az bicep build --file infrastructure/archive/main.bicep --stdout`: succeeds;
  the compiled container app `dependsOn` the chat deployment resource. No
  what-if or deployment was run.
- No EF model change, so no migration check; the frontend is untouched.

## Two stages and what a push does

`archive.yml` (image release) and `infra-deploy-archive.yml` both start from a
push to `main` that touches their paths, and infrastructure runs first. A
single push of both changes would point the **old image** (which has no price
for `gpt-6-luna`) at the new deployment and the ledger would refuse every chat
run. So the work is two commits, pushed one after the other:

- **Stage A** (`src/archive/**`, tests, docs; `main.bicep` unchanged, the
  default deployment stays `gpt-5-4-mini`). Pushing it releases the image. The
  app keeps talking to `gpt-5-4-mini` through the new code path, which now
  sends `reasoning_effort: none` and has both prices.
- **Stage B** (`infrastructure/archive/**`, the drift test). Pushing it starts
  the infrastructure workflow: what-if, guard, deploy. It creates the Luna
  deployment and switches `Archive__Chat__DeploymentName` to it, after the
  deployment exists.

Maintainer steps, in order:

1. **Gate, before pushing stage A.** The SDK in use (`Azure.AI.OpenAI` 2.1.0)
   sends `api-version=2024-10-21`, and Microsoft's API changelog lists
   `reasoning_effort` as added in `2024-12-01-preview`, so the service may
   answer HTTP 400 to the parameter stage A sends to *every* deployment. Run
   the live evaluation from the stage A checkout against the **existing**
   deployment:

   ```
   ARCHIVE_CHAT_ENDPOINT="https://aoai-liedertafel-archive.openai.azure.com/" \
   ARCHIVE_CHAT_DEPLOYMENT_NAME="gpt-5-4-mini" \
   ARCHIVE_CHAT_MODEL_VERSION="gpt-5.4-mini 2026-03-17" \
   dotnet test tests/archive/backend --filter "Category=ChatEvaluationLive" --logger "console;verbosity=detailed"
   ```

   Read the first case. If it fails with a 400 about `reasoning_effort`, do
   **not** push stage A: move the chat client to the `/openai/v1/` endpoint
   (`OpenAIClient` with a bearer token policy, the pattern in "Azure OpenAI v1
   API" on Microsoft Learn) or to a newer `Azure.AI.OpenAI`, then repeat. The
   unit test that pins `api-version=2024-10-21` fails when the SDK changes, as
   a reminder to re-read this note.
2. Push stage A. The image is released; chat still answers from
   `gpt-5-4-mini`.
3. Check quota for `OpenAI.DataZoneStandard.gpt-6-luna` in `germanywestcentral`
   (the GPT-6 family may need a quota request depending on the subscription
   tier, per the Microsoft Learn model page).
4. Push stage B. Read the what-if comment/log: Create of the `gpt-6-luna`
   deployment, Modify of the container app, no Delete/Replace.
5. Run the live evaluation on Luna (same command with
   `ARCHIVE_CHAT_DEPLOYMENT_NAME="gpt-6-luna"` and
   `ARCHIVE_CHAT_MODEL_VERSION="gpt-6-luna 2026-09-22"`; no price variables
   needed). Without the first two variables the test returns immediately, so it
   is safe in any run. Ask one tool-using question in the hosted chat and check
   the ledger row.
6. Record the result in "Live evaluation on Luna", tick the open boxes, set
   `status: done`. Only then delete the old deployment
   (`az cognitiveservices account deployment delete -g RG-Liedertafel-Archive
   -n aoai-liedertafel-archive --deployment-name gpt-5-4-mini`) and the
   `gpt-5-4-mini` price entry.

## Live evaluation on Luna

Not run. Nothing is recorded here until the maintainer runs it.

Last `gpt-5.4-mini` result for comparison (ARC-022-3, 2026-10-09,
deployment `gpt-5-4-mini`, model version `2026-03-17`): **14 of 14 cases**
passed on run 5 after the prompt fix for catalogue listings (run 4, the
control on the earlier code, had 13 of 14). Per-case tables:
[ARC-022-3](ARC-022-3-chat-on-agent-framework.md), "Live evaluation, control
and confirmation".

## Follow-up and handoff

- ARC-022-1, ARC-025-1, ARC-034-1, ARC-036, ARC-052 name this ticket as a
  dependency. The configuration side (model key, prices, `none`) is ready for
  them; image input is Luna-capable per the model page but unproven here.
- Reasoning `none` is a default. A slice that does not send tools through Chat
  Completions may ask for more through `ChatOptions.Reasoning`; one that does
  will fail loudly at the provider.
- Not verified: quota admission for Luna in `germanywestcentral`, whether the
  GPT-6 pages' statement on tools and reasoning covers Luna, and whether the
  service accepts `reasoning_effort` on `api-version=2024-10-21` (the gate).
- Long-context pricing needs a guard before large image/OCR inputs, see above.

## Handoff and parallel work

The hard cap and the generalised ledger moved to ARC-022-3. Slices that list
this issue as a dependency rely on both. Decisions:
[architecture §14](architecture.md#14-ai-assistance).
