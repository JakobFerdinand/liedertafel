---
id: ARC-021
status: done
phase: core
kind: decision
depends_on: []
touches: ["ai-evaluation", "planning"]
external_inputs: ["ai-evaluation-access"]
---

# ARC-021 — Validate a usage-billed provider for grounded German answers

**Depends on:** None; it is the prioritized next decision.

## Outcome

The maintainer selects an AI provider and a bounded first question type with
documented cost/data handling before adding conversational archive access.

## Acceptance criteria

- [x] Compare current provider terms, relevant EU handling, model suitability,
  usage pricing and limits against a separately reviewed AI budget (documented
  comparison below; revalidate at implementation start).
- [x] Test synthetic German archive questions with citations and uncertain
  historical evidence; assess unsupported-answer and instruction-in-data
  behaviour (offline scripted-model pipeline run recorded 2026-09-23, see
  below; the same evaluation must be rerun against the pinned GPT-5.4-mini
  once live `ai-evaluation-access` exists, before first production chat use).
- [x] Record a selected provider/model, allowed data, credential ownership,
  request/token limits, cancellation and failure policy (recorded 2026-09-22).
- [x] Define the first supported question as grounded archive retrieval, such as
  when a song was sung; no editing tools or self-hosted model/search service
  (defined 2026-09-22 as archive-scope chat, with provider-hosted embeddings).

## Verification

Record a small reproducible synthetic evaluation and per-answer cost estimate.
Do not use private choir data until its handling has been decided. Revalidate
provider facts when implementation starts.

## Handoff and parallel work

Supply the provider contract to ARC-022. No technical prerequisites means the
research can start immediately and is the next planned work item.

## Decision record — 2026-09-22

Recorded from a maintainer interview (grilling session) plus provider research
checked 2026-09-22 against current provider documentation and published
pricing. Pricing figures are reference retail values in USD, not an
account-specific quote, and must be revalidated when implementation starts.

**Selected: Azure OpenAI (Microsoft Foundry), EU Data Zone deployment,
GPT-5.4-mini with pinned dated model versions; embeddings via
text-embedding-3-small stored in the existing Neon PostgreSQL (pgvector).**

### Provider choice and EU gate

- The EU gate is a hard requirement: EU data processing plus contractual
  no-training-on-API-inputs can veto a provider regardless of price.
- Azure OpenAI EU Data Zone keeps processing inside the EU Azure boundary with
  contractual no-training by default, at roughly 10% above global pricing.
- Alternatives rejected: Anthropic's first-party API has no EU-pinned
  inference region; Mistral is the cheapest EU-native option but needs a
  separate direct account and billing; OpenAI direct gates EU residency behind
  an application plus modified-retention amendments.

### Model and version policy

- Chat model: **GPT-5.4-mini** (EU Data Zone, reference ~$0.83/$4.95 per 1M
  input/output tokens). **GPT-5.4-nano** (reference ~$0.22/$1.38) runs through
  the same synthetic evaluation as a recorded fallback; GPT-5.4-class is an
  escalation option if citation behaviour is weak.
- Pin dated model versions for chat and embeddings; re-run the evaluation and
  re-check pricing before any upgrade.
- Embedding model: **text-embedding-3-small** (reference ~$0.02/1M tokens) on
  the same Azure OpenAI resource.

### Budget and spending controls

- Separate AI budget: **EUR 5 per normal month**, reviewed against the
  EUR 10 archive operating target. It is a reviewed amount, not an automatic
  spending cut-off.
- Enforcement: an app-side monthly token/EUR counter persisted in the database
  plus a maintainer alert. Exceeding the amount triggers review and **manual
  disable** by the maintainer; the chat is not auto-disabled. The Azure budget
  alert (ARC-043 path) remains as a backstop notification.
- Per-request bounds are app-enforced: bounded prompt/input size, 30-second
  timeout, client-disconnect cancellation, one bounded retry, and a clear
  German failure state.
- ARC-022's "per-request/aggregate cost bounds" is agreed to mean this
  combination (per-request enforcement, monthly counter, alert, manual
  disable), not an automatic cut-off; amend ARC-022's wording when implementing.

### Data handling

- Member account/identity data (names, emails) never leaves the application.
- Historical and creator names in catalogue, event and programme content are
  content and may flow to the provider; redacting them would break the history
  answers they must support.
- Azure's default retention (prompts/completions up to 30 days for abuse
  monitoring, inside the EU boundary, no training) is accepted; zero-day
  retention/Modified Abuse Monitoring is recorded as an optional later step.
- Embedding scope: all member-visible text — titles, alternate titles, creator
  information, entered lyrics/opening words, extracted PDF text, and
  event/place/history notes. Re-embed on publication or change; drafts and
  unpublished records stay unindexed and invisible to the model.

### Architecture shape

- Embeddings are included: the pgvector extension in the existing Neon
  PostgreSQL stores vectors. No separate vector service and no self-hosted
  model/search service — the original boundary stands, and pgvector is an
  extension of the existing database, not a new service.
- Chat: free-form in-app chat, archive scope only, single-turn (no
  conversation memory), current members only. Non-archive questions receive a
  polite German refusal. No editing tools, no tool-calling, no web access, no
  general-knowledge answers.
- Answers are German, cite visible records, keep confirmed performances
  distinct from programme evidence, and may state "unknown".

### Credential ownership

- Billed to the existing archive Azure subscription.
- Managed identity (Entra ID) on the Container App with a scoped role
  assignment on the Azure OpenAI resource; Key Vault only where keyless
  authentication does not cover the integration.

### Evaluation bar and handoff

- Accepted bar: a fixed set of ~10–15 synthetic German questions including
  traps — unknown song, year-only date, conflicting evidence, instructions
  embedded in document text, and a question about an unpublished record. Pass
  requires every citation verifiable against the authorized result set, zero
  unsupported factual claims, honest "unknown" behaviour, and a recorded
  EUR-per-answer cost estimate. This doubles as the ARC-022 rehearsal.
- Handoff to ARC-022: Azure OpenAI EU Data Zone contract, pinned GPT-5.4-mini,
  managed identity, single-turn archive-scope chat, the embedding/pgvector
  index over member-visible text, and the agreed cap semantics above.

## Technical stack amendment — 2026-09-22

Recorded from a second maintainer interview (technical stack) plus research
checked 2026-09-22 against the Microsoft Agent Framework release notes, NuGet
metadata and the AG-UI protocol repository. Version facts must be revalidated
when implementation starts.

**Selected: Microsoft Agent Framework on the backend, AG-UI as the
frontend protocol with the CopilotKit runtime plus a custom German chat UI,
streamed multi-turn chat with bounded authorized tool-calling.**

### What this supersedes

- "No tool-calling" (single approved retrieval queries before the prompt) is
  superseded by **bounded tool-calling**: the agent calls authorized retrieval
  functions in a loop, with authorization enforced inside each tool.
- "Single-turn" is superseded by **multi-turn from the start**, with threads
  persisted in Neon.
- "Non-streaming 30 s single-shot request" is superseded by **streamed
  responses** over the AG-UI event protocol.

### Backend: Microsoft Agent Framework

- MAF reached 1.0 GA in April 2026 and is the successor to Semantic Kernel and
  AutoGen. Stable core packages checked 2026-09-22: `Microsoft.Agents.AI`,
  `Microsoft.Agents.AI.Abstractions` and `Microsoft.Agents.AI.OpenAI` at
  **1.22.0**; MIT licensed; targets net10.0 — fits the single container.
- Built on the Microsoft.Extensions.AI abstractions; agents can be created
  from an `IChatClient`. Azure OpenAI is used with `ManagedIdentityCredential`
  (production guidance from the framework), matching the keyless credential
  decision above.
- Built-in OpenTelemetry instrumentation must flow to the Aspire dashboard in
  development, per the repository's shared definition of done.
- Pin package versions; the 1.x cadence is roughly biweekly to monthly, so
  re-check versions and changelogs at each release.

### AG-UI protocol and hosting adapter

- AG-UI protocol is **1.0** (MIT, checked 2026-09-22): HTTP POST with
  `RunAgentInput`, streamed SSE events (`RUN_*`, `TEXT_MESSAGE_*`,
  `TOOL_CALL_*`, `STATE_*`, `CUSTOM`). It is the stable contract, independent
  of adapter maturity.
- Adopt the Microsoft hosting adapter
  `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` (`AddAGUIServer` /
  `MapAGUIServer`), **pinned to the exact preview version**
  (1.22.0-preview.260918.1 as of 2026-09-22) and re-verified at each release.
- Documented fallback if preview churn hurts: a hand-rolled SSE endpoint
  emitting AG-UI events from the stable `RunStreamingAsync` core API, keeping
  AG-UI as the wire contract.
- Known adapter gaps (for example rejection of AG-UI multimodal content
  arrays) do not affect this text-only chat; keep the endpoint text-only.
- Every request through the AG-UI endpoint must enforce current membership and
  thread-owner authorization before the agent runs.

### Frontend

- CopilotKit runtime with the AG-UI endpoint registered as an `HttpAgent`,
  plus `@ag-ui/client` / `@ag-ui/react` hooks, but a **custom chat component**
  styled to the archive's German design system. CopilotKit's prebuilt chat UI
  is not used.

### Threads and conversation data

- Multi-turn threads are persisted in Neon, **owned by the asking member**,
  bounded to approximately the last 20 messages per thread, with ownership
  validated on every request. Thread content stays within the member-visible
  data line above; drafts and unpublished records never enter thread content
  or tool results.

### First-slice tool allow-list

1. Authorized catalogue search (lexical plus pgvector).
2. Song performance/evidence history.
3. Event/programme details.

All three run server-side under member authorization; document text remains
data; no editing tools, no web access, no general-knowledge answers.

### Amended runtime bounds and caps

- Streamed German answer; abort on 30 seconds without tokens or a 2-minute
  overall cap; at most 5 authorized tool calls per request; max-output-token
  cap; client disconnect cancels; one bounded retry, then a German failure
  state. Citations are delivered as structured message payload events and
  rendered by the custom UI.
- The EUR 5 monthly alert-plus-manual-disable semantics stand unchanged; the
  per-request bound now additionally covers the tool loop and output tokens.

### Handoff update (ARC-022)

ARC-022 consumes the amended stack: Microsoft Agent Framework with the pinned
AG-UI hosting adapter, the three-tool allow-list, streaming multi-turn
conversation over Neon-persisted member-owned threads, the amended runtime
bounds, and the unchanged EUR 5 cap semantics. The earlier note about amending
ARC-022's wording now also covers tool-calling, streaming and multi-turn.

## Synthetic evaluation — 2026-09-23

Implemented with ARC-022's first slice as the reproducible evaluation harness
`tests/archive/backend/ChatEvaluationTests.cs` (`dotnet test
tests/archive/backend --filter FullyQualifiedName~ChatEvaluation`). Because
`ai-evaluation-access` does not exist yet, the recorded run executes the real
pipeline (AG-UI endpoint, bounded loop, authorized catalogue tools, visibility
filtering, thread persistence, usage ledger) against the deterministic
`ScriptedChatClient` instead of the live model — it validates the grounding
contract, not model behaviour.

- Corpus: 12 published songs plus a draft, including traps per the accepted
  bar — unknown song, year-only date, conflicting evidence (published 1913,
  lyric line mentioning a 1921 event), instruction embedded in document text
  (`Anweisung:` lyrics carrying a confidential marker), and a question about
  the unpublished record.
- 16 cases pass: every citation is verifiable against the authorized result
  set (the test recomputes the same visibility-filtered search the tool
  runs), zero unsupported factual claims, honest unknown/refusal behaviour,
  drafts and revoked members and overlong questions blocked.
- Recorded EUR-per-answer estimate with the scripted client and the reference
  prices: **1 EUR-cent per answered request** (rounded up; each run writes a
  `chat_usage_entries` row and an `EVAL` output line). A live rerun must
  record the same lines against the pinned model before production chat is
  enabled.

## Hosting-adapter supersession — 2026-09-23

Implemented with ARC-022's first slice, revalidated against NuGet on
2026-09-23: the AG-UI .NET SDK now ships as stable 1.0.0 packages
(`AGUI.Abstractions`, `AGUI.Server`, MIT, targeting net10.0), so the
**preview** MAF hosting adapter
`Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`
(1.22.0-preview.260918.1) is superseded. The bounded agent loop is
hand-rolled on `Microsoft.Extensions.AI` `IChatClient` **10.10.0** (the
documented fallback shape), hosted through `AGUI.Server` 1.0.0's
`ToChatRequestContext`/`AsAGUIEventStreamAsync` — same AG-UI 1.0 wire
contract, no preview churn, no MAF dependency in the first slice. Microsoft
Agent Framework remains the planned agent layer for later slices; the
package facts (1.22.0, net10.0) were revalidated 2026-09-22/23. The
frontend consumes the AG-UI SSE stream directly with a custom German UI;
the CopilotKit runtime step moves with the provider step.

## Provider step — 2026-09-23

Implemented on branch `feat/archive-ai-provider` (commits `a51bc90`
infrastructure, `b564f8a` backend seam). With the resource provisioned, both
`ai-evaluation-access` and `ai-runtime-credentials` are now satisfied: the
maintainer owns the account, the keyless runtime credential path is the role
assignment below, and the evaluation can run against the pinned deployment.

### Infrastructure (commit `a51bc90`, `infrastructure/archive/main.bicep`)

- Foundry account `aoai-liedertafel-archive` (`Microsoft.CognitiveServices/
  accounts@2026-07-01`, kind AIServices, S0, `customSubDomainName` = account
  name, `disableLocalAuth: true`, `allowProjectManagement: true`) in
  `germanywestcentral` via a new `aiLocation` parameter: austriaeast is not an
  EU Data Zone region for Azure OpenAI (EU DZ region list excludes it), while
  germanywestcentral serves gpt-5.4-mini under DataZoneStandard. Everything
  else in the group stays austriaeast.
- Foundry project child `liedertafel-archive` for future tooling only;
  inference is account-level.
- Chat deployment `gpt-5-4-mini`: model `gpt-5.4-mini`, pinned dated version
  `2026-03-17`, SKU `DataZoneStandard` capacity 30 (30k TPM; quota admission
  verified in germanywestcentral: `OpenAI.DataZoneStandard.gpt-5.4-mini`
  limit 200, 0 used). `versionUpgradeOption: 'OnceCurrentVersionExpired'`
  holds the pin until the provider expires that version — no silent upgrade
  while pinned, but the expiry transition itself is automatic, so the
  evaluation rerun and pricing re-check remain manual duties on expiry
  (tracked with the ARC-043 maintenance checks).
- Role assignment `Cognitive Services OpenAI User`
  (5e0bd9bd-7b93-4f28-af87-19fc36ad61bd) on the account for the runtime
  identity `id-archive-app` — the keyless Entra path; no API key exists
  (`disableLocalAuth`).
- Group budget `budget-liedertafel-archive` (`Microsoft.Consumption/budgets@
  2019-10-01`): EUR 10/month over the whole resource group, notifications at
  Actual 80/100% and Forecast 100% to j.wegenschimmel@gmail.com plus
  contactRoles Owner. Alert-only backstop per ARC-004's EUR 10 operating
  target and this ticket's alert-plus-manual-disable semantics (never an
  automatic cap); it covers the budget-notification part of ARC-043 (see the
  dated note there).
- Embeddings/text-embedding-3-small deliberately not deployed: this step is
  chat-only; embeddings/pgvector are the follow-up slice
  [ARC-052](ARC-052-chat-embeddings-pgvector.md).
- Container App environment: `Archive__Chat__Provider='AzureOpenAI'`,
  `Archive__Chat__Endpoint` (custom-subdomain endpoint),
  `Archive__Chat__DeploymentName='gpt-5-4-mini'`,
  `Archive__Chat__Enabled='true'` — chat enabled from the first deploy per
  the enablement decision below.
- Validated with `az bicep build` (clean) and `az deployment group what-if`
  (5 to create: account, deployment, project, account role assignment,
  budget; 10 modifies, all representation noise; no Delete/Replace — passes
  the destructive guard).

### Enablement decision — 2026-09-23

The maintainer decided on 2026-09-23 to deploy the chat enabled
(`Archive__Chat__Enabled='true'`) from the first release: pre-release the
maintainer is the sole production user, so the live-eval-before-enable gate
recorded in the acceptance criteria above is consciously replaced by
enable-immediately, with the live evaluation rerun still executed and
recorded afterwards. The live rerun therefore remains the gate before the
first **member-facing** chat use, not before chat exists at all.

### Backend seam (commit `b564f8a`)

- Packages pinned exactly: `Azure.AI.OpenAI` 2.1.0 and
  `Microsoft.Extensions.AI.OpenAI` 10.10.0 (matching the existing
  `Microsoft.Extensions.AI` 10.10.0 pin; transitive OpenAI 2.13.0 and
  System.ClientModel 1.14.0 resolved cleanly, build 0 warnings).
- New `Chat/AzureOpenAIChatClient.cs`: config-selected provider at the
  existing seam — `Provider=AzureOpenAI` plus the new `Endpoint` and
  `DeploymentName` chat options produce `AzureOpenAIClient` +
  `GetChatClient(deployment).AsIChatClient()`, with the credential built like
  the existing `DataProtectionConfiguration.CreateCredential()` precedent
  (`AZURE_CLIENT_ID` → user-assigned managed identity in production; az
  login locally). Otherwise `ScriptedChatClient` stays; tests and local
  development are unchanged.
- No frontend changes: the AG-UI direct SSE consumer stands, and the
  CopilotKit runtime step from the stack amendment above is superseded by
  that direct consumer (same reasoning as the hosting-adapter supersession;
  no frontend AI dependency).
- `tests/archive/backend/ChatEvaluationTests.cs` gained the live rerun
  `LiveProviderRerunRecordsTheSameEvidence` behind trait
  `Category=ChatEvaluationLive`: the same 13-case evaluation set through the
  same seam/factory, keyless via az login, recording the same `EVAL` evidence
  lines plus one `chat_usage_entries` ledger row per run (EUR-per-answer),
  enforcing the hard grounding invariants (citations only inside the
  authorized visibility-filtered set; draft title and confidential marker
  never surface) and reporting soft model behaviour (missing `[Quelle: …]`
  marker, no-finish) as `live-flag:` lines without failing. Environment
  variables `ARCHIVE_CHAT_ENDPOINT`, `ARCHIVE_CHAT_DEPLOYMENT_NAME`
  (optional `ARCHIVE_CHAT_MODEL_VERSION`); the test returns immediately with
  an EVAL skip line when unconfigured (xunit 2.9.3 has no runtime skip).
  Invocation: `dotnet test tests/archive/backend --filter
  "Category=ChatEvaluationLive" --logger "console;verbosity=detailed"`.

### Verification and pending live rerun

- `dotnet build` 0 errors/0 warnings; `dotnet test tests/archive/backend`
  252/252 (251 + the gated live test returning early); the `az what-if`
  above.
- The live rerun has **not** executed yet: the resource does not exist until
  the archive infra workflow deploys this Bicep. It must run against the
  pinned deployment and its results be recorded in this ticket before first
  member-facing chat use — enablement already happened per the decision
  above.

**Live evaluation results: recorded here once run** (placeholder — no
results exist yet; they must not be filled in from memory).

