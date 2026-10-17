# Choir archive

ARC-001 establishes the local walking skeleton. The frontend uses Next.js App
Router with static export (replacing the plan's Vite choice at implementation
request). ASP.NET Core serves the exported assets and `/api/*` in production;
Next.js/Turbopack supplies hot reload and a same-origin API proxy in development.
Server-only Next.js features are outside this hosting contract.

The archive targets .NET 10 LTS. The root SDK uses `10.0.100` with
`latestFeature` roll-forward; existing Functions applications retain their
runtime targets and their CI also installs the pinned SDK.

## Start from a clean checkout

Prerequisites: .NET 10 SDK, Node.js 24 LTS, pnpm 11.3.0, and a running Docker
or Podman engine. AppHost restores frontend packages and starts the containers;
no Azure account or manually started database is needed.

From the repository root:

```bash
dotnet run --project src/archive/apphost
```

For Podman, prefix the command with `ASPIRE_CONTAINER_RUNTIME=podman`.
Open the dashboard login URL printed by AppHost, then the `archive-frontend`
HTTP endpoint. Open `/system/status/` directly and refresh it. Select **Lokale
Dienste prüfen** to query PostgreSQL and exercise Blob, queues and Mailpit.
Open `archive-mail`'s HTTP endpoint to see **Archiv: lokaler Funktionstest**.
Ctrl+C stops AppHost and its resources. Local PostgreSQL/Azurite volumes persist.

The launch profile uses **loopback HTTP**, including OTLP; dashboard token/API-key
authentication stays enabled. This avoids requiring trusted development TLS
certificates. Production cookies are Secure; local cookies use SameAsRequest.

## Resources and configuration contract

| Resource | Contract |
| --- | --- |
| `archive-postgres` / `archive-db` | PostgreSQL 17.6, database `archive`, Aspire-generated local password, named volume |
| `archive-storage` | Azurite 3.35.0, named volume, Blob and Queue endpoints |
| `archive-blobs` | Injects `ConnectionStrings__archive-blobs` |
| `archive-queues` | Injects `ConnectionStrings__archive-queues` |
| `archive-mail` | Mailpit 1.27.8, HTTP inbox and SMTP; injects `Mail__Host` / `Mail__Port` |
| `archive-storage-init` | Automatic finite local command; creates private `archive-assets` container and the configured extraction and reading queues (`archive-extraction`, `archive-reading`) |
| `archive-api` | HTTP `/api/*`, process-only `/alive`; waits for dependencies and successful storage setup |
| `archive-frontend` | Next.js/Turbopack; waits for API; receives `PORT` and server-only `ARCHIVE_API_URL` |
| `archive-migrate` | **Explicit Start**; only resource receiving `ConnectionStrings__archive-migrations` |
| `archive-worker-smoke` | **Explicit Start**; finite Blob/queue/mail verification with correlated producer/consumer tracing |
| `archive-mail-test` | **Explicit Start**; ARC-010 integration run with Aspire telemetry: sends the marked German test message through the real Azure sender. Requires `Mail:Provider=Azure` plus `Archive:MailTestRecipient` in AppHost configuration and refuses otherwise |
| `archive-extract` | **Explicit Start**; ARC-034 finite queue worker — dispatch sweep + drain of the extraction queue, then exit (production runs the same command as a queue-triggered job) |
| `archive-read` | **Explicit Start**; ARC-034-1 finite AI reading worker — wakes work parked for budget, drains the `archive-reading` queue with the offline scripted model locally, then exits |

`WithArchiveDependencies` owns reference injection and readiness for local C#
workers. Use `WithExplicitStart()` for operator-triggered work, and
`WaitForCompletion(archive-storage-init)` before touching local storage. The
smoke command is a contract example; real import/extraction work belongs to its
feature slice. `Archive:PersistLocalData=false` gives integration tests fresh,
ephemeral container data. Stop a running local AppHost before integration tests:
both use the same frontend working directory.

Mail transport selection (ARC-010): `Mail:Provider` defaults to `Smtp`, so
ordinary startup stays on Mailpit capture via the injected
`Mail__Host`/`Mail__Port`. Setting `Mail:Provider=Azure` (AppHost user secrets
or environment, never committed) routes the backend through Azure
Communication Services Email with the verified choir-domain sender; the local
authorized credential is `Mail:AzureConnectionString`, while production sends
keyless through the runtime managed identity. Production refuses to boot on
`Smtp` and refuses sender secrets outright. The same `--send-test-mail
<address>` operator command backs `archive-mail-test`; it requires the Azure
provider and Development.

The application uses `ConnectionStrings:archive-db` on demand, with a pool of
at most five connections, zero minimum connections, five-second connect timeout,
ten-second command timeout and no database keep-alive. `/alive`, `/health` and
`/api/build` never query dependencies. `/api/dev/database` is development-only
and reports connectivity plus pending migrations. There is no schema polling.

Local Data Protection keys are in ignored `src/archive/.local/keys`, with the
application discriminator `Liedertafel.Archive.Development`. These must never
be copied into production. Persistent production keys and actual member cookies
are owned by the later authentication/cloud slices.

The diagnostic uses fixed `.test` mail addresses. It creates a unique scratch
queue and a `diagnostics/<id>.txt` blob, round-trips them and removes them with
a bounded cleanup token. It never consumes messages from the configured
extraction queue (`archive-extraction`); the finite `archive-extract` worker is
the only local consumer, and `POST /api/dev/extraction-duplicate` re-sends one
extraction envelope for a Datei so its idempotent handling can be observed
against the real queue.
AppHost injects emulator connections; there are no production storage credentials
or `UseDevelopmentStorage=true` shortcuts in application configuration.

## Schema ownership and explicit commands

Start **archive-migrate** in the dashboard once after first startup. It must
finish with exit code 0. The same resource can be started after adding migrations.
The initial empty EF migration establishes migration history only; no catalogue,
membership or other product schema is pre-created. API startup never applies it.

If the Aspire CLI is installed, the dashboard actions also have CLI equivalents:

```bash
aspire resource archive-migrate start --apphost src/archive/apphost/Archive.AppHost.csproj
aspire resource archive-worker-smoke start --apphost src/archive/apphost/Archive.AppHost.csproj
aspire resource archive-extract start --apphost src/archive/apphost/Archive.AppHost.csproj
```

To author a migration, run from `src/archive`:

```bash
dotnet tool restore
# This design-time placeholder is enough to generate a migration; it does not connect.
env 'ConnectionStrings__archive-migrations=Host=localhost;Database=archive;Username=postgres' \
  dotnet ef migrations add MeaningfulFeatureName --project backend --output-dir Data/Migrations
```

Inspect the generated migration before applying it. EF Core alone owns schema
changes; each feature adds its entities/migrations in `backend/Data/Migrations`.
For a standalone migration process, supply the actual migration-role connection
through environment configuration and run:

```bash
dotnet run --project src/archive/backend --no-launch-profile -- --migrate
# The production image has the same entry point:
docker run --rm --env ConnectionStrings__archive-migrations liedertafel-archive:local --migrate
```

An inherited `DOTNET_ENVIRONMENT=Development` also requires OTLP configuration;
normally the explicit local AppHost resource supplies it. Local PostgreSQL uses
its isolated admin role for both paths. Production must supply distinct migration
and least-privilege runtime roles, as specified by the architecture. Migration
failure returns nonzero; no startup or release loop automatically retries it.

## Frontend and HTTP contract

Next.js 16.3.5, React 19.2.8, TypeScript, Tailwind 4 and Biome were generated with:

```bash
npx create-next-app@latest frontend --ts --tailwind --biome --app --no-src-dir --turbopack --import-alias "@/*" --use-pnpm --yes --disable-git
```

The .NET projects were created with `dotnet new web`, `aspire-apphost`,
`aspire-servicedefaults`, `xunit` and `aspire-xunit`, all targeting `net10.0`.
Aspire SDK/integrations/testing are pinned to 13.5.3, EF/EF tools to 10.0.12,
and the Npgsql EF provider to 10.0.3. Lockfiles record the frontend resolution.

- Browser calls use relative `/api/*` URLs. Only the development server reads
  `ARCHIVE_API_URL`; no `NEXT_PUBLIC_*` backend address is needed.
- The development rewrite forwards cookies, `Set-Cookie`, request bodies,
  `X-CSRF-TOKEN`, and API status/content types. There is no permissive CORS policy.
- GET `/api/antiforgery` supplies the request token and HTTP-only SameSite cookie.
  POST `/api/dev/exercise` validates both before side effects, and
  `POST /api/dev/extraction-duplicate?dateiId=<id>` re-sends one extraction
  envelope through the configured queue (502 when no queue backend exists).
  These diagnostics are absent in production. They do not implement member
  authentication.
- `pnpm run build` uses `output: "export"` and emits `out/`. ASP.NET serves each
  generated route's `index.html`, including `/system/status/`, its scripts and
  Next navigation payloads. Unknown API paths always return Problem Details;
  missing pages/assets remain 404s.
- New pages must support static export. Use client-side API calls for data and
  query parameters for runtime IDs unless a route can be generated at build time.
  Server Actions, SSR, API route handlers and unbounded dynamic App Router routes
  require an explicit hosting decision. Do not add a generic index fallback:
  it would hydrate the wrong App Router route.
- Production has no Node.js process. `next start` is deliberately not a script.
  Development output `.next-dev` is separate from production `.next` / `out`.

## Telemetry and finite workers

Every .NET host calls `AddServiceDefaults()`. Development startup fails clearly
if `OTEL_EXPORTER_OTLP_ENDPOINT` is missing. `UseOtlpExporter()` sends **logs,
traces and metrics**, consuming AppHost's endpoint, protocol and auth headers.
`OTEL_SERVICE_NAME` distinguishes API, storage setup, migrations and workers.
The API exports `archive.build.requests` plus HTTP/runtime metrics, and logs a
structured build-request event. Health probes are excluded from traces.

Workers start a normal Generic Host, then use `RunArchiveJobAsync`. It creates
the job activity, records completion/handled failure, flushes traces, metrics and
logs (five-second budget per provider), stops the host and returns a process exit
code. Dispose the host afterwards. Shutdown has a 30-second budget. Later queue
envelopes carry W3C `traceparent` / `tracestate`; consumers extract the remote
parent, as demonstrated by `LocalServices`. Do not propagate arbitrary baggage.

URL queries are redacted before export, including Azure spans. AppHost's query
redaction opt-outs are overridden. Do not log codes, cookies, credentials, message
bodies or signed URLs. Shared HttpClient defaults add service discovery, **not
automatic retries**; each feature chooses safe operation-specific resilience.

To inspect actual export after a browser check:

1. Dashboard **Structured logs**, resource `archive-api`: find
   `Archive build information requested` and follow its trace.
2. **Traces**: inspect `GET /api/build`, then the diagnostic request with storage,
   queue producer/consumer and mail spans. Run `archive-worker-smoke` and inspect
   the finite `worker-smoke` trace and its completion log after the process exits.
3. **Metrics**, resource `archive-api`, meter `Liedertafel.Archive`: select
   `archive.build.requests`. Refresh the frontend and allow five seconds for
   another metric export. HTTP/runtime instruments are also present.

## ARC-015 private score assets

One private score per musical version flows through a three-step upload
protocol: `POST /api/musical-versions/{id}/assets` creates the logical asset,
`POST /api/assets/{id}/upload-session` issues a pending upload session with a
bounded write ticket, the browser PUTs the file directly to that ticket URL,
and `POST /api/upload-sessions/{id}/finalize` validates existence, size and the
`%PDF-` magic bytes before promoting the object server-side into an immutable
file revision (idempotent retry returns the same revision). Reads go through
`GET /api/assets/{id}/access`, which checks membership and song visibility and
returns blob-scoped 15-minute view/download ticket URLs; ticket URLs exist only
in JSON responses and are never logged.

The storage/ticket adapter (`IAssetStorageAdapter`,
`BlobAssetStorageAdapter`) lives in `backend/Assets/AssetStorage.cs` and is
configured under `Archive:Assets` (`ContainerName` defaults to
`archive-assets`, max upload size, upload-session and read-ticket lifetimes).
It uses the injected `ConnectionStrings__archive-blobs` connection string.
Locally the container and a permissive emulator CORS rule for direct browser
transfers are created by `archive-storage-init` (`Development/LocalServices.cs`,
`AssetStorageEmulatorBootstrap`); production CORS and the keyless identity
switch (user-delegation SAS via managed identity, ARC-051) are documented
configuration points and not yet wired.

The revision promotion is a server-side blob copy whose source is a signed
ticket URL. The emulator fetches that source from inside its own container, so
the AppHost pins the Azurite blob host port to the container port
(`WithBlobPort(10000)`); the ticket host then resolves in both directions.
Integration tests must pass `DcpPublisher:RandomizePorts=false` for the pin to
apply (testing mode randomizes proxied ports by default).

## ARC-017 large uploads with resume and cancellation

Upload sessions scale to representative large originals (~10 GiB per-file limit
`Archive:Assets:MaxUploadBytes`, recommended `UploadBlockBytes` 8 MiB block
size in the session response). The browser transfers in Azure Blob block
operations directly against the ticket URL — `?comp=block&blockid=…` per
chunk, `?comp=blocklist&blocklisttype=all` to discover retained progress,
`?comp=blocklist` to commit — so file bytes never traverse ASP.NET and memory
stays bounded to one block. Upload tickets now grant `Write|Create|Read` (Read
powers the committed-block listing used for resume).

Session contract on top of ARC-015/ARC-016:
- Initiation declares a file identity (`sizeBytes`, `fileName`) and is
  restricted to the asset's creator (ARC-025 generalized that owner check to
  version assets too — pending objects are never reassigned to someone
  else); per-file and per-owner collection limits (`MaxCollectionBytes`,
  40 GiB default; the owner is the musical version or, since ARC-025, the
  event) are enforced at initiation (declared) and finalization (actual),
  413. In the pending budget a declared session reserves its declared size,
  an undeclared session its per-file cap.
- A new session supersedes the editor's earlier pending sessions on the same
  asset (terminal `Cancelled` state, pending blobs deleted best-effort), so
  failed transfers cannot starve the version budget until the grace-window
  cleanup catches them; other users' and other assets' sessions are untouched.
- `POST /api/upload-sessions/{id}/renew` extends a pending session's lifetime
  and issues a fresh ticket — this is the recovery path after an interruption,
  even when the old ticket already expired. Finalized/abandoned/cancelled
  sessions answer 409.
- `DELETE /api/upload-sessions/{id}` cancels a pending session (terminal
  `Cancelled` state, pending blob deleted best-effort); finalize on cancelled
  sessions is refused with 409.
- Finalize validates the declared identity: a mismatch answers 409 `Die Datei
  passt nicht zur bestehenden Uploadsitzung.` before touching storage and
  keeps the session retryable, so a stale session can never commit a
  different file.
- Block-list commits carry no content type; finalize treats the storage
  default (`application/octet-stream`) as unset and falls back to the
  session's declared whitelisted type.

The editor UI shows per-row progress percentages, offers cancellation during
transfer and a resume path after reload: the browser persists
`arc-upload-<assetId>` (session id, name, size, lastModified), re-selection is
checked against that identity, and only missing blocks are re-transferred.
Abandoned sessions are cleaned by the bounded `--cleanup-uploads` job
(`UploadSessionCleaner`, Development-gated like the other finite jobs): pending
sessions expired for longer than `Archive:Assets:ExpiryGrace` (1 hour default)
are marked Abandoned and their pending blobs deleted.

The contract above is the handoff to ARC-037 (streaming finalization consumes
the same session/blocks) and the transfer-state ownership note for ARC-016's
batch UI, which rides this engine unchanged.

## ARC-016 labelled voice file batches

Batch uploads ride the unchanged ARC-015 three-step protocol, one independent
upload session per file. Asset types expand to `score`, `audio` and `midi`
with a per-type content-type whitelist
(`backend/Assets/AssetEndpoints.cs`): finalize validates type-conditionally —
score keeps the `%PDF-` magic-byte check, audio/MIDI check only the
whitelisted content types. Each asset additionally carries an optional
`Description` (≤ 500 chars, explicit migration `ArchiveAssetDescription`) and
the free-text `VoiceLabel`; editors edit these plus the asset type through
`PATCH /api/assets/{id}`, which requires the editor role and the creating
editor's ownership and locks the type once a current revision exists.
Song detail embeds `description` next to type/voice for every asset; pending
items expose no tickets.

The editor UI (`components/noten-bereich.tsx`) selects multiple files into an
editable batch list (type, voice suggestions from the arrangement's voice
configuration plus "Vollmix", description) and transfers with bounded
concurrency (2 parallel workers), per-file status and per-file retry that
reuses the created asset — a failed file never duplicates or rolls back
successful ones. Published materials are listed grouped by type and voice;
audio offers the ARC-018 player, MIDI authorized downloads until ARC-019
adds a MIDI listener.

## ARC-018 practice audio listening

Audio assets in published material are playable in the browser through
`components/audio-spieler.tsx`: the "Anhören" button loads
`GET /api/assets/{id}/access` once and the player renders play/pause, seek,
volume and a time display with per-voice labels; only one entry plays at a
time. The authorized download link stays beside the player.

The 15-minute read tickets are renewed transparently: the player schedules a
silent re-fetch of `/api/assets/{id}/access` 60 seconds before the response's
`expiresAt` (retried after 30 seconds on failure) and swaps the `<audio>`
`src` on the same element, restoring the captured position and play state in
`loadedmetadata` — seeking uses the blob's native `Range` support, so
playback continues across renewed URLs. Media errors first attempt one
silent renewal, then show an understandable state: unsupported/undecodable
originals report the non-playable format (the original is never treated as a
verified playable derivative; download stays available), transient failures
offer "Erneut versuchen", and a removed access (404) or expired member
session (401) shows specific copy. Renewal re-checks active membership and
member visibility on every call because the endpoint recomputes
authorization per request; revoked access stops ticket issuance while an
already-issued URL keeps its bounded lifetime. `restlaufzeitMs` in
`lib/assets.ts` plus `fetchAssetAccess` are the ticket-renewal primitives for
ARC-030's concert video.

## ARC-019 MIDI listening with practice tempo

Published MIDI assets are playable in the browser through
`components/midi-spieler.tsx` using the same "Anhören" flow as audio: one
access fetch, then the player beside the retained authorized "Herunterladen"
link. The approach is deliberately bounded and self-contained: a
dependency-free SMF parser (`lib/midi.ts`, formats 0/1, running status,
tempo map, SMPTE files rejected with the corrupt-file copy) turns the bytes —
fetched once through the ticketed `viewUrl` — into a seconds timeline, and a
small Web Audio synthesizer (triangle oscillator with per-note envelope into
a master gain) provides basic sound. No samples are downloaded, no remote
conversion service is involved, and no new dependency is added; soundfont
libraries were rejected because they fetch samples from third-party CDNs at
play time.

Controls mirror the audio player: play/pause, seek slider, tabular time
display, volume and a 50–150 % tempo slider — all labelled per voice
(`MIDI-Spieler · Sopran`, `Sopran (MIDI) abspielen`). Player error copy and
the tabular time formatter are shared with the audio player
(`SpielerFehler`/`ladeFehlerAusUrsache`/`zeitText` in `lib/assets.ts`), and a
dynamics compressor behind the master gain keeps dense chords below full
scale. The AudioContext is
created and resumed synchronously inside the play-button click (user-gesture
requirement on mobile browsers); tempo/seek changes re-anchor the running
scheduler and playback continues at the new rate from the current position
(already-sounding notes are cut at the switch); the position
lives on the file's own tempo timeline. Playback stops and the context is
closed on unmount, and the materials area's single-active rule covers both
player types. Corrupt or unsupported files report the non-playable format
(download stays available) without affecting other catalogue material;
transient load failures offer "Erneut versuchen", which re-fetches fresh
access first (404/401 map to specific copy). Because the bytes are fully
loaded before playback, no ticket renewal is needed mid-listen.

One production note: the bytes fetch is a CORS request against blob storage
(the audio element sidesteps CORS). Locally the permissive emulator rule
covers it; production blob CORS is the documented configuration point for
ARC-051.

## ARC-022 grounded history chat

Members chat with the archive at `/fragen` („Fragen zum Archiv"): `POST /api/chat` implements the AG-UI 1.0 wire contract (stable `AGUI.Server` 1.0.0 / `AGUI.Abstractions` 1.0.0 on `Microsoft.Extensions.AI` 10.10.0, superseding ARC-021's preview MAF hosting adapter). The bounded loop runs on an `IChatClient` provider seam — Development and tests use the deterministic `ScriptedChatClient` (cited answers from tool results only, honest unknown/refusal behaviour); the real Azure OpenAI implementation (managed identity, pinned GPT-6 Luna since ARC-021-1, formerly GPT-5.4-mini, EU Data Zone) plugs into the seam when credentials exist, and embeddings/pgvector move with it.

- Threads persist in the existing PostgreSQL (`chat_threads`/`chat_messages`, migration `20260922214454_ArchiveChat`), are owned by the asking member, are bounded to the last 20 messages, and are validated for ownership on every request. The client generates the thread id (`localStorage` key `arc-chat-thread`); server-side history is authoritative — client-provided older messages are ignored as tamper-proof grounding. `GET /api/chat/thread/{id}` restores the owner's history; foreign/missing threads answer an indistinguishable 404.
- Tools are allow-listed and authorized inside themselves: `catalogue_search` (published-only, `CatalogueText.Fold` token-AND like ARC-020, ≤ 10 results, 300-char lyrics excerpt) and `song_details`. Document text is data, never instructions; no editing tools, no raw queries, no general-knowledge answers.
- Per-request bounds are app-enforced: question ≤ 2000 chars, at most 5 model calls per run (`MaxToolCalls`; every call offers the tools, and a run whose fifth call still asks for a tool ends in the failure state without a sixth call), 30 s no-token abort per model call, 120 s overall cap, client-disconnect cancellation, one repetition of the run's first call when the provider fails before its first update (the only case with a sixth provider request), German `RUN_ERROR` failure state. Cost is bounded by the shared AI budget below.
- The chat answers 503 „Der Archiv-Chat ist derzeit nicht verfügbar." unless `Archive:Chat:Enabled=true` and `Disabled=false`, so production stays inert until the provider step. Performance/evidence questions answer honestly that no Aufführungsdaten exist yet (ARC-024/026/028/031 provide them); the synthetic evaluation (`tests/archive/backend/ChatEvaluationTests.cs`, `--filter FullyQualifiedName~ChatEvaluation`) verifies the grounding bar — every citation verifiable against the authorized result set, zero unsupported claims, honest unknown behaviour, recorded EUR-per-answer cost — and must be rerun against the pinned live model before first production chat use.

### ARC-022-3 agent, AI gateway and the hard monthly cap

The chat runs on a Microsoft Agent Framework agent (`Microsoft.Agents.AI` 1.23.0, stable) named `archive-chat` (`Chat/ArchiveChatAgent.cs`, built once per process on the gateway and wrapped with the framework's OpenTelemetry instrumentation). `ArchiveChatService` supplies what belongs to one run through the run options: the tools on the request's database context, the thread's `ChatThreadHistoryProvider`, and the run pipeline tool loop → citation filter → per-call bounds. Thread history stays in `chat_threads`/`chat_messages`; the endpoint, its membership and ownership checks and the AG-UI wire contract (`AGUI.Server` 1.0.0) are unchanged. The Agent Framework hosting adapter and workflows are not used.

`Ai/AiGateway` is the only path to a model provider: OpenTelemetry → `BudgetedChatClient` → provider. The raw provider client is registered under the service key `AiGateway.ProviderKey` and nothing else resolves it; injecting `IChatClient` yields the budgeted pipeline. `AddArchiveAi(configuration)` registers provider selection, model keys, ledger, budget and gateway in one call (API host today, job hosts later). A feature attaches an `AiOperation` (feature name, operation id, member or none, optional lower output bound, estimate for non-text input) to the options of its run; a call without one is refused. An embedding provider registered under the same key (ARC-052) is wrapped by `BudgetedEmbeddingGenerator` the same way.

- **Cap:** EUR 15 per calendar month in Europe/Vienna, for all AI features together. Before each model call the middleware reserves the call's worst case (estimated input plus the output bound at the model's price). A reservation that would pass the cap is refused and the provider is not called. Guarantee: settled cost + open reservations + the worst case of a newly admitted call never exceeds the cap. After the call the reservation is replaced by the usage the provider reported; a call that ends without a usage report (failure, timeout, client disconnect, abandoned stream) costs its full reservation.
- **Ledger:** `ai_usage_entries` (renamed from `chat_usage_entries` by migration `20261006111010_AiUsageLedger`): one row per operation and model with feature, model, summed tokens of all its calls, settled cost and open reservation in micro-EUR. The month's spend is the sum over its rows; nothing is cached in memory. `ai_budget_months` holds one row per month as a lock: a reservation runs in one transaction that takes `SELECT … FOR UPDATE` on it, so reservations of a month queue on every replica (5 s lock timeout, then refused). A settlement is one relative `UPDATE` of its own row, retried three times, then logged at error and counted. `Ai/PostgresAiLedgerStore` is SQL; tests on the EF in-memory provider use `tests/archive/backend/InMemoryAiLedgerStore.cs` behind `IAiLedgerStore`.
- **Fail closed:** no price or a price of 0 for the model, a `ModelId` other than the client's own model, non-text input without an estimate, a ledger that cannot be reached, or a missing `AiOperation` all stop the call before the provider.
- **Member state at the cap:** the run ends with AG-UI `RUN_ERROR`, `code: "monatsbudget_erreicht"` and a message starting „Monatsbudget erreicht."; the chat page shows it without a retry button. Nothing else changes behaviour. Because a call reserves its worst case, the chat stops when the remainder no longer covers one worst-case call, slightly before EUR 15.00 is settled. `Archive:Chat:Disabled=true` remains the manual kill switch (503).
- **Configuration (`Archive:Ai`):** `MonthlyCapEur` (15), `MaxOutputTokensPerCall` (6000, the ceiling a feature can only lower; the chat states 2000 itself), `MaxInputTokensPerCall` (200000: a call whose estimated input is larger is refused before the budget, below Luna's long-context tier), `Models:<model key>:InputPricePerMillionEur` / `OutputPricePerMillionEur` (both above 0). The chat's model key is `Archive:Chat:DeploymentName` with the Azure provider, otherwise `scripted`. `appsettings.json` prices `gpt-6-luna` (0.1056 / 0.528 EUR, the EU Data Zone Standard short-context meters from the Azure Retail Prices API in EUR, ARC-021-1) and, until its deployment is deleted, `gpt-5-4-mini` (0.83 / 4.95, the ARC-021 reference retail values, recorded there in USD); Luna's long-context tier (about twice the rates above roughly 272,000 input tokens) is not in the ledger: `MaxInputTokensPerCall` keeps every call below it (ARC-034-1); `appsettings.Development.json` prices `scripted`, so the offline client is refused outside Development. `Archive:Chat:MonthlyBudgetEur` and the two `Archive:Chat:*PricePerMillionEur` keys no longer exist. To switch the model, change the deployment name and add its price entry. The Azure client sends `reasoning_effort: none` on every request unless a feature sets `ChatOptions.Reasoning` itself (Luna rejects function tools with reasoning on Chat Completions).
- **Telemetry** (source and meter `Liedertafel.Archive.Ai`, registered in the service defaults): spans `invoke_agent archive-chat`, `chat <model>` and `execute_tool <tool>` with the GenAI metrics (`gen_ai.client.token.usage`, `gen_ai.client.operation.duration`); budget instruments `archive.ai.tokens`, `archive.ai.cost`, `archive.ai.budget.refusals` (reason `cap`, `unpriced_model`, `ledger_unavailable`), `archive.ai.budget.settlement_failures`, `archive.ai.budget.month_spend`, `archive.ai.budget.month_cap`. Logs: `KI-Budget: … verbucht` (information, per settled call), `KI-Budget: Monatsbudget erreicht …` (warning), ledger, price and settlement failures (error). Message content is not exported, and failed spans carry the exception type only (`AiExceptionRedactionProcessor`). Never set `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`: it would export questions, answers and document text.
- **Checks:** `dotnet test tests/archive/backend --filter "FullyQualifiedName~AiBudget|FullyQualifiedName~BudgetedChatClient|FullyQualifiedName~ChatApi"`. The ledger's SQL needs real PostgreSQL: start `docker run --rm -d -e POSTGRES_PASSWORD=pw -p 55432:5432 postgres:17.6 -c max_connections=300` and run `ARCHIVE_TEST_POSTGRES="Host=localhost;Port=55432;Database=postgres;Username=postgres;Password=pw" dotnet test tests/archive/backend --filter "FullyQualifiedName~AiBudgetLedgerTests"` — every test then creates, migrates and drops its own database and runs the production store (cap race, 32-worker contention, two replicas opening a month, settlement after the month rolled over). The Aspire walking skeleton asserts one chat run's ledger row on PostgreSQL through the Development-only `GET /api/dev/ai-budget`. The live evaluation command is unchanged (`Category=ChatEvaluationLive`, see the root `AGENTS.md`; for Luna: `ARCHIVE_CHAT_ENDPOINT="https://aoai-liedertafel-archive.openai.azure.com/" ARCHIVE_CHAT_DEPLOYMENT_NAME="gpt-6-luna" ARCHIVE_CHAT_MODEL_VERSION="gpt-6-luna 2026-09-22"`); it returns immediately when the first two are unset, and its calls are charged to the ledger of the test's in-memory database, not to production. Result and open steps: [ARC-021-1 (#122)](https://github.com/JakobFerdinand/liedertafel/issues/122).
- **Release of the ledger migration (owner decision 2026-10-06):** the rename ships in one release, against the split-rename compatibility rule. A code-only rollback to the previous image breaks the chat (it writes `chat_usage_entries`); this release is fix-forward only for chat. After the release: run `infrastructure/neon/runtime-grants.sql` as migrator, check the runtime role's privileges on `ai_budget_months`, smoke one chat run. Details and the exact checks: `infrastructure/archive/README.md`, "Rollback". If `aiChatDeploymentName` is overridden, set both `Archive__Ai__Models__<deployment>__…PricePerMillionEur` entries or every run is refused.
- **Switching to GPT-6 Luna (ARC-021-1):** two pushes, image first (this code, still on the `gpt-5-4-mini` deployment), then `infrastructure/archive/**` (creates the `gpt-6-luna` deployment and points the app at it). Before the first push run `ChatEvaluationLive` once against the existing deployment, because the SDK's `api-version=2024-10-21` may reject `reasoning_effort`. Confirm the Luna prices against the Azure Retail Prices API in EUR (`currencyCode='EUR'`, `Azure OpenAI GPT6`, meters `6-luna ShortCo Inp/Opt Std DZ`, `germanywestcentral`) before each release that touches them. Exact steps and the Luna evaluation command: [ARC-021-1 (#122)](https://github.com/JakobFerdinand/liedertafel/issues/122).

### ARC-022-4 AG-UI client and A2UI cards

The chat page reads the run with `@ag-ui/client` 1.0.2 (`HttpAgent` in `frontend/lib/chat.ts`, posting straight to `/api/chat` through the same CSRF-aware fetch as every other mutation) and shows what the agent displays with `@a2ui/react` 0.13.0 / `@a2ui/web_core` 0.13.0 (protocol v0.9, `zod` 3.25.76; all pinned, no CopilotKit, no Node runtime, still a static export). The A2UI library is loaded on demand (`next/dynamic`, no server rendering) only when an answer carries surfaces. The basic catalog is not registered: the catalog `urn:liedertafel:archiv:a2ui:katalog:v1` has exactly `LiedKarte`, `AuftrittKarte`, `ProgrammListe` and `Quellen` (`frontend/lib/a2ui.ts` schemas, `components/a2ui-katalog.tsx` implementations, cards styled in `app/fragen/chat.css`).

- **Wire:** the server library wrote protocol fields it does not use as explicit nulls (`usage[].model`, `parentRunId` …), which the strict client rejects as a schema violation. `ChatEndpoints` now serializes events without null properties. `RUN_FINISHED`, `RUN_ERROR` with its `code`, the `archive.citations` custom event and the budget state are unchanged for the page.
- **The tool:** the agent calls `zeige_oberflaeche(komponenten)`; each entry is a component name and exactly the one record id it is built from (`liedId`, `liedIds`, `auftrittId`). `Chat/A2uiSurfaces.cs` (`A2uiCatalog`) validates every entry against the catalog (known component, only its own id, valid GUIDs, at most 12 entries looked at, at most 4 surfaces per answer, one card per record), resolves the ids to what a member may see (published songs and events only) and builds the A2UI messages itself: `createSurface` plus one `updateComponents` with a single `root` component per surface. Anything else is dropped before streaming; the model only learns how many entries were not shown, never which or why. The model supplies no text and no URL, and the server sends none: the browser derives links (`/lied/?id=…`, `/auftritt/?id=…`) from the ids. The tool call itself is removed from the AG-UI stream; the validated messages travel in one `CUSTOM` event `archive.a2ui` (before the `archive.citations` chips, which stay the server-built event).
- **Member chat offers:** `LiedKarte` (title, composer, lyricist, first lines), `AuftrittKarte` (title, kind, date, venue) and `Quellen` (up to 10 songs). `ProgrammListe` is defined with its wire shape and the browser renders it, but the member chat has no resolver for it yet (always dropped, not offered to the model); ARC-022-1 and ARC-022-2 add its resolver to `A2uiCatalog.Components`.
- **Storage and reload:** the validated messages are stored with the assistant answer in `chat_messages."A2uiJson"` (`jsonb`, migration `20261011160524_ChatMessageA2ui`; a card-only answer stores empty content and is not fed back to the model as history). `GET /api/chat/thread/{id}` never returns the stored JSON as it is: `A2uiCatalog.RecheckAsync` rebuilds each surface from the record ids it names, so a card for a since-unpublished record is gone, tampered or unreadable stored content yields nothing, and data is fresh. The message then has an `a2ui` array (or none).
- **Browser safety:** `leseA2uiOberflaechen` keeps only surfaces of exactly one `createSurface` (this catalog) and one `root` update with a catalog component and exactly its allowed properties (strict schemas: no `href`, `url`, data model or extra fields); the A2UI `MessageProcessor` then runs with `STRICT_VALIDATION` as a second check, and a surface it refuses is removed. Malformed or out-of-catalog messages show no card and no error.
- **Clicks:** cards link to archive pages that load their own data over REST; nothing goes back through the chat.
- **Checks:** `dotnet test tests/archive/backend --filter "FullyQualifiedName~ChatApiTests"` (card stream, reload, unpublish, tampered storage, dropped requests); `ARCHIVE_TEST_POSTGRES=… dotnet test tests/archive/backend --filter "Category=PostgresA2ui"` (migration with existing rows, jsonb round trip, resolvers and re-check as real SQL); from `frontend`: `ARCHIVE_BASE_URL=http://localhost:<port> corepack pnpm exec playwright test tests/a2ui.spec.ts tests/a2ui-katalog.spec.ts tests/chat.spec.ts --workers=1`. The chat Playwright mocks live in `tests/chat-mock.ts` in the exact event shape the server sends; a hand-written stream that breaks the AG-UI protocol is now refused by the client, as it should be.

## ARC-024 historical events

The choir's "Auftritte" record lives in the `events` table (`backend/Events/`,
additive migration `20260924100654_HistoricalEvents`). `ChoirEvent` keeps the
date honest as components: `DateYear` (1800–2100), `DateMonth` (1–12),
`DateDay` (must exist in its month) and an independent `DateApproximate`
flag; the derived `datePrecision` (`day|month|year|unknown`) follows from the
set parts and only (year), (year, month), (year, month, day) and none are
valid — nothing invents a calendar date. `EventDate.cs` renders the
culture-invariant German `dateDisplay` ("12. Mai 1950", "um 1950", "ca.
12. Mai 1950", "Datum unbekannt"). Kinds are the closed set `concert`,
`service`, `wedding`, `funeral`, `festival`, `other`.

API (ProblemDetails German, `no-store`, CSRF on mutations, fresh access
decision per request; drafts answer an indistinguishable 404 for members):

- `GET /api/events` is unpaginated (archive scale) and filters/sorts in C#
  so InMemory tests and PostgreSQL agree: known years descending, within a
  year day-precision first (month/day descending), then month-only, then
  year-only, unknown years last; `?year=`/`?kind=` narrow the list while the
  `years` navigation summary ignores the filters and ends with the
  `year: null` "Ohne Jahr" group.
- `GET /api/events/{id}` adds `notes`, `sourceNote`, `createdAt`,
  `updatedAt`, `publishedAt`.
- Editor-only `POST /api/events` creates a draft; `PATCH /api/events/{id}`
  leaves absent fields unchanged, clears present-empty strings and treats
  the nested `date` as an explicit full replacement (`null` year = unknown
  date; 409 on concurrent edits). `POST .../publish` and `/unpublish` are
  idempotent; the first publication stamp survives republish.
- Visibility is the shared `EventVisibility` decision: member-visible
  exactly when published; deletion stays with ARC-040 trash.

The UI rides the redesigned system: `/auftritte/` is navigated through a
year rail ("Alle", year tiles with counts, "Ohne Jahr" last) beside the
newest-first list with kind labels, editor draft badges and
Veröffentlichen/Zurückziehen; `/auftritt/?id=` keeps uncertainty honest
(`Datum unsicher` badge on approximate/partial dates) and always shows the
empty Programm, Dokumente and Aufnahmen sections reserved for ARC-025
documents, ARC-026 programmes and ARC-032 recordings. The editor form
derives the date from the filled year/month/day fields — empty means an
unknown date, nothing invented.

Handoff: the stable event IDs are the anchor points the later slices attach
to; no programme/setlist tables exist yet, and publishing an event is
independent of ARC-026's programme publication.

## ARC-025 event documents and photos

Documents and photographs attach to events through the ARC-015 asset
contract, generalized to a shared owner registry: `ArchiveAsset` keeps
`MusicalVersionId` nullable beside the new `EventId`, and the check
constraint `CK_assets_owner` enforces in the database that exactly one owner
is set (application code keeps the same invariant). Event-owned assets
accept only the new types `document` (PDF) and `photo` (JPEG/PNG/WEBP) with
the ARC-016-style per-type content-type whitelists, and finalize validates
honest magic bytes: documents keep the `%PDF-` prefix gate, photographs must
present the honest signature of their effective image type (JPEG `FF D8 FF`,
the PNG signature, WEBP `RIFF`+`WEBP`) while audio/MIDI keep skipping the
gate.

`POST /api/events/{id}/assets` (editor-only, CSRF, German ProblemDetails)
creates the logical asset with only an optional description (≤ 500 chars,
no voice label); the event row itself is never touched, so attaching a
scanned programme creates no programme or performance record (ARC-026 does
that explicitly). Event detail responses embed the resulting `documents`
list after `sourceNote`, sorted by createdAt then id; members see only
finalized material, pending items stay editor-only and expose no tickets.
Upload sessions are now initiated creator-only for every asset — event and
version alike — because a pending object must not be reassigned to another
editor (403). Collection budgets scope per owner: version-owned assets
share their musical version's budget, event-owned the event's, enforced at
initiation (declared) and finalization (actual) with 413.

`GET /api/assets/{id}/access` keeps the unchanged ticket shape (blob-scoped
15-minute view/download ticket URLs, never logged) but gates on the shared
event visibility decision: members only see published events, so a draft
event's documents answer the same indistinguishable 404 as the event detail,
and material without a current revision reports the neutral pending message.
One photo-specific finalize rule closes the ARC-017 gap: a block-list commit
carries no blob content type, and the missing/default fallback to the
session's declared type would always say `image/jpeg` for photos, so on that
path the effective type is derived from the header magic bytes and rejected
when no whitelisted image signature matches (pinned fallback test).

The frontend (`components/auftritt-dokumente.tsx` on `/auftritt/?id=`)
serves both audiences from the embedded `documents` list: members get
photographs with the description as caption and alt text (per-asset ticket
fetch, silently renewed before expiry like the audio player) and documents
with Öffnen/Herunterladen beside the size/type line. The editor workbench
follows the noten-bereich recipe and reuses the ARC-017 upload engine
unchanged (create asset, upload session, block transfer to the ticket,
finalize, per-row progress, retry over the same asset, resume from the local
`arc-upload-<assetId>` state after reload); the file chooser pre-selects
document vs photo from the MIME type, description/type edits ride
`PATCH /api/assets/{id}` with the type locked after the first upload, and
photographs render as an auto-fill grid that stacks on phones with the large
view as a real dialog.

Handoff: event-asset ownership and the `documents` view slot in the event
detail are published for ARC-030 (concert recordings attach through the same
registry); the per-owner budget and the shared owner registry are the
coordination point with catalogue/import work.

## ARC-029 confirming what was actually sung

Plan and actual stay separate persistence. The planned revision rows
(ARC-026/027) are never written by a confirmation; the actual programme is a
`programme_confirmations` row (one per programme, bound to the published
revision it reviewed, attribution + application-bumped `RowVersion`, token 0 =
"nothing confirmed yet") plus ARC-028 `performances` rows that carry two new
nullable columns: `ProgrammeItemId` (the frozen planned entry they confirm,
unique) and `ConfirmationId` (the confirmation owning them; also set for
added encores). Migration `ProgrammeConfirmation`. Occurrences written here
are always evidence `confirmed`; historical ARC-028 rows (both columns null)
are never touched.

Editor-only API (`ProgrammeConfirmationEndpoints.cs`, German ProblemDetails,
`no-store`, antiforgery on the write, members 403, anonymous/revoked 401):

- `GET /api/events/{eventId}/programme/confirmation[?revisionId=]` — the
  review of a published revision (newest by default): every planned entry
  with `outcome` (`open`/`sung`/`skipped`/`unconfirmed`), its linked
  occurrence, a `suggestedPerformanceId` carry-over after a republication,
  the added songs, `upToDate` and the confirmation `rowVersion`.
- `PUT` the same path — one complete, idempotent statement:
  `{ revisionId, rowVersion, items: [{ programmeItemId, outcome: sung|skipped,
  musicalVersionId?, performanceId?, rowVersion? }], additions: [{ clientKey?,
  performanceId?, songId, musicalVersionId?, rowVersion? }] }`. Every planned
  entry must be stated exactly once. A statement identical to the stored
  state answers 200 without writing (lost-response retries, stale token
  included); otherwise a stale `rowVersion` or occurrence token, or a lost
  unique slot, is 409; a revision that is not the newest publication is 409
  "Das Programm wurde zwischenzeitlich neu veröffentlicht."; a certainly
  future event date is 409. Existing occurrences update in place (stable
  performance ids), omitted ones owned by the confirmation are removed,
  encores are keyed by `clientKey` (retry key `programme-addition:…`; planned
  entries use `programme-item:…`) so repeats never double and genuine repeats
  stay distinct rows. After a republication the editor adopts earlier
  occurrences explicitly by sending their `performanceId` (identity of
  performances survives for recordings).
- `GET /api/events/{id}` embeds `programme.confirmation` for everyone (members
  without token): `actual` (ordered: planned-linked in plan order, then
  encores; `added`, `differsFromPlan`, evidence status) and `skipped`. ARC-028
  editor occurrence embeds gain `programmeItemId`/`confirmationId`, and
  `POST /api/performances/{id}/delete` refuses confirmation-owned rows (409:
  change them in the confirmation).

Review hardening: the GET review reports `confirmable` (false for a
certainly-future event), suggests adoptable occurrences (earlier ones after a
republication, hand-entered unowned ones of the same song; adoption keeps id,
source note and requires the same song) and lists `unownedOccurrences` that
would be counted twice. The PUT may carry `knownOccurrences` (row tokens): an
occurrence it removes must be among them unchanged, otherwise 409. Duplicate
`clientKey` values are 400. Members never receive `skipped`/planned labels of a
superseded revision.

UI: `components/auftritt-bestaetigung.tsx` ("Tatsächlich gesungen") under the
Programm section — member read view with plan/actual separation and honest
"noch nicht bestätigt"; editor workbench with explicit per-entry decision,
corrected version, encore search, "Programm unverändert bestätigen" (first
confirmation only) and stale-state handling. Browser spec:
`tests/auftritt-bestaetigung.spec.ts`; API tests:
`tests/archive/backend/ProgrammeConfirmationApiTests.cs`.

## ARC-031 song history and honest occurrence counts

`GET /api/songs/{songId}/performances[?page=&evidence=confirmed|mention&arrangementId=<id>|unknown]`
(`backend/Events/SongHistoryEndpoints.cs`) is readable by every active member and
replaces ARC-028's editor-only groundwork on the same path. Rows are the ARC-028
performances (their `id` is the stable performance id for ARC-032 recordings),
ordered by the event's honest date (newest first, unknown year last, then event,
position, id) in fixed pages of 20. Counting is row counting: confirmed
occurrences and unconfirmed mentions are separate figures, never summed, and
documents, retries and recordings never add to them. A hand-entered confirmed row
beside a programme-confirmed row of the same song at one event is not merged but
flagged `possiblyDuplicate` and reported in `counts.confirmed.possiblyDuplicate`
(the total is an upper bound; every confirmed row of such an event carries
`possiblyDuplicateAtEvent`, so the UI never asserts a repeat there); a mention at
a confirmed event is flagged `alsoConfirmedAtEvent`. Read `origin` together with
`evidenceStatus` (a downgraded programme row is a mention, not a confirmation).
Ordering is `EventDate.CompareNewestFirst`, shared with the event list; a page
past the end is clamped to the last page and reported. Only published events are listed for members and counted
for everyone; editors also see unpublished-event rows (flagged, never counted,
`counts.draftEventOccurrences`) and source notes. Unpublished songs are a 404 for
members. The member filter lives in `EventVisibility.OfMemberVisibleEvents`
(ARC-040 extends it). No migration.

UI: "Aufführungsgeschichte" on `/lied/` (`components/lied-historie.tsx`,
`lib/historie.ts`) with qualified counts ("nur erfasste Überlieferung"), explicit
empty state, Nachweis/Fassung filters, paging, "Datum unsicher", "Fassung
unbekannt" and an optional `erweiterung(zeile)` render prop keyed by performance id
for ARC-032. Tests: `tests/archive/backend/SongHistoryApiTests.cs`,
`frontend/tests/lied-historie.spec.ts`. The AI history text of the ticket is a
post-launch follow-up (ARC-013-1/ARC-021-1) and not part of this slice.

## ARC-033 score corrections and retained revisions

A correction is a new immutable file revision under the **same** logical asset,
never a new arrangement, musical version or material entry. It uses the
unchanged ARC-015 upload protocol on the existing asset id.

Who may change the file an asset serves (one rule, `MayChangeCurrentFile` in
`backend/Assets/AssetEndpoints.cs`, shared by starting an upload session and
by restoring a revision): the creating editor always; every other editor only
for a **musical-version asset that already has a file**. That deliberately
covers every material type on a version — scores, voice files, audio and
MIDI — not only scores. An asset without a file, and every event-owned asset
(ARC-025), stay with the editor who created them. Members, song detail and old links read through
the asset's current-revision pointer and therefore follow a correction or a
restore immediately.

`RevisionChanges.MakeCurrentAsync` (`backend/Assets/RevisionChange.cs`) is the
revision-change contract and the only code that moves the pointer. Finalize
and restore both call it: pointer swap, optimistic token bump, an append-only
`asset_revision_changes` entry (who made which revision current, replacing
which) and — for a PDF revision without one — the ARC-034 extraction row are
committed in one save. Extraction stays keyed by revision, so a restored
revision reuses its stored text. Consumers that need "current text" join
through `assets.CurrentRevisionId`; there is no second signal to subscribe to.

Editor-only endpoints (members 403, anonymous 401, all `no-store`):

- `GET /api/assets/{id}/revisions` — retained revisions newest first with file
  name, size, upload time and uploader, plus the pointer-change log.
- `GET /api/assets/{id}/revisions/{revisionId}/access` — 15-minute view and
  download tickets for exactly that revision; a revision of another asset is
  the same 404 as an unknown one. Members only ever get the current revision
  through `GET /api/assets/{id}/access`.
- `POST /api/assets/{id}/current-revision` with `{ revisionId,
  expectedCurrentRevisionId }` — makes a retained revision current again. No
  revision is created; a missing `revisionId` is 400, another editor's
  event-owned asset 403, a stale `expectedCurrentRevisionId` or a lost race
  409; repeating the request for the already-current revision is a no-op 200.

Uploaders and restorers appear in the history by display name, falling back
to the account's email address when none is set (editor-only view).

Competing replacements never overwrite each other: every revision has its own
blob name, and a finalize that loses the race answers 409 with its session
still pending, so a retry adds the next revision. The browser repeats the
finalize once for exactly that 409 without transferring the file again, and
the history panel reloads whenever a 409 is shown. Revisions are retained until
explicitly removed; nothing expires them (the upload cleanup only touches
pending sessions). No removal action exists yet — ARC-039 owns deliberate
removal together with the retained-reference check.

The editor UI lives in `components/noten-verlauf.tsx` ("Dateistände" on a
score entry); the file revision is called "Dateistand" throughout so it is not
confused with the musical "Fassung".

## ARC-030 whole event recordings

A **recording** (`backend/Recordings/`, table `recordings`, migration
`20261005104333_ConcertRecordings`) is labelled audio or video material about
one event. An event may carry any number of them; creating or uploading one
never touches the event row, a programme or a performance occurrence.

Each recording has two event-owned asset slots that reuse the ARC-015
revision and ticket contracts unchanged:

- **Original** (`recording-original`): preserved whatever its format. Only an
  empty file is refused.
- **Playback copy** (`recording-playback`, optional): an externally converted
  file. Finalize accepts it only if its leading bytes are a container
  browsers play for the recording's kind — MP4/M4A, WebM, MP3 or WAV for
  audio; MP4 or WebM for video (`backend/Recordings/RecordingFormats.cs`).
  MP4 counts only with a known audio/video brand (HEIC/AVIF stills in the
  same container do not), MP3 only with an ID3 tag or a valid Layer III
  frame header.
  A refused candidate never becomes current; the earlier copy stays in place.

`RecordingFiles.Resolve` decides what members play: the playback copy when it
has a file, otherwise a playable original serving both roles as one object.
The recognised type is stored on the revision (block-list commits carry no
content type). This is a container check, not a decode: an MP4 with a codec a
browser lacks passes and is then reported by the player; the editor answers
that by adding a playback copy. There is no transcoding and no audio analysis.

Endpoints (all `no-store`; mutations need the antiforgery token):

- `GET /api/events/{id}/recordings` — members: published recordings of a
  published event, otherwise the event's 404. Editors: all, with an `editor`
  block (version, both slots with file name/type/size, `canChangeFiles`).
- `POST /api/events/{id}/recordings` `{ label, kind }` — editor; creates the
  recording and its empty original slot.
- `PATCH /api/recordings/{id}` `{ label?, isPublished?, downloadEnabled?,
  durationSeconds?, expectedVersion? }` — any editor. A stale
  `expectedVersion` is 409 "Die Aufnahme wurde zwischenzeitlich geändert."
  (reload); publishing without any file is a different 409 (not allowed in
  this state, reloading does not help).
- `POST /api/recordings/{id}/playback` — creating editor only; opens the
  playback-copy slot, idempotent.
- `GET /api/recordings/{id}/access` — the renewable ticket; renewal is the
  same call again and re-reads membership, both publications and the download
  switch. `viewUrl` is null while nothing is playable; `downloadUrl` is null
  and **no download ticket is issued** unless an editor enabled downloads.

Files are transferred with the existing upload protocol on the slot's asset
id (`POST /api/assets/{id}/upload-session` …). File changes stay with the
editor who created the recording (the ARC-025/ARC-033 `MayChangeCurrentFile`
rule); labels, publication and the download switch are open to every editor.
Members cannot reach recording files through `GET /api/assets/{id}/access`
(404), so publication and the download switch cannot be bypassed; editors can,
to fetch an original for conversion. The generic asset create/patch endpoints
refuse the two recording types.

Downloads default to off. Switching them off stops new download tickets, and
the player then also sets `controlsList="nodownload"` and suppresses the
context menu on the media element, so the browser's own "save"/"download"
entries are not offered. The streaming ticket is still a 15-minute read URL
for the same object — the switch withholds the download affordances, it is
not copy protection.

Storage limit (operator decision): recording files count against the
event's collection budget together with its documents and all retained
revisions — `Archive:Assets:MaxCollectionBytes`, 40 GiB by default. A few
multi-gigabyte originals plus playback copies of one event reach it, and
further uploads are then refused with 413. Decide the value for real
concert material during large-transfer validation. The 30-second promote
deadline for very large objects is inherited unchanged from ARC-017.

The table has two checks: the kind is `audio` or `video`, and the playback
slot never points at the original asset (`CK_recordings_slots`). The API
additionally ignores a playback link that is not a `recording-playback`
asset of the recording's own event.

`durationSeconds` is measured by the uploading editor's browser from the
local file and may be null ("unknown"). It is cleared in the same save
whenever the file members play changes (`RecordingFiles.OnCurrentFileChangedAsync`,
called from `RevisionChanges.MakeCurrentAsync`).

Frontend: `components/auftritt-aufnahmen.tsx` on `/auftritt/?id=…`, playing
through `components/medien-spieler.tsx` — the ARC-018 player generalised to
audio and video (`audio-spieler.tsx` is now a thin wrapper). Tickets are
renewed before expiry with position and play state preserved (read at the
moment the address is swapped). Twenty seconds before expiry the list itself
fetches a fresh ticket if the player has none and keeps trying while the old
one is valid; only when that fails — or the recording is no longer
accessible — is the ticket dropped before it expires, with the position
remembered for reopening. Download tickets are
fetched at click time and never kept. `/auftritt/?id=…&aufnahme=<id>&t=<seconds>`
opens one recording at a position without starting it.

For later slices:

- ARC-032 keys passages on `recordings.Id`; `playback.revisionId` in the list
  and access responses identifies the file a timestamp was taken against, and
  `MedienSpieler` takes a `sprung` prop (`{ sekunden, marke }`) to position
  the player.
- ARC-040: `recordings.EventId` and both asset links are `Restrict`; member
  visibility goes through `RecordingVisibility`, which builds on
  `EventVisibility`.
- ARC-041: a file is "original only" exactly when `RecordingFiles.Resolve`
  does not return it as `Playable`; with downloads enabled and nothing
  playable, the download ticket points at the original.
- After applying the migration in a hosted database, run
  `infrastructure/neon/runtime-grants.sql` as usual.

## ARC-032 passages in whole recordings

Migration `20261006010744_RecordingPassages`: table `recording_passages`
(`backend/Recordings/RecordingPassage.cs`) linking a recording to a
performance occurrence with `StartSeconds`/`EndSeconds` and the playback file
revision the two times were taken against. Database rules: `StartSeconds >= 0`,
`EndSeconds > StartSeconds`, one passage per (recording, performance), all
foreign keys `Restrict`, and event ownership as composite keys — the passage
carries `EventId` and points at `recordings (Id, EventId)` and
`performances (Id, EventId)` (both gained an alternate key), so a passage can
never join a recording and an occurrence of different events. A second
recording can mark the same occurrence; nothing here creates, changes or
counts a performance.

Endpoints (`backend/Recordings/RecordingPassageEndpoints.cs`, antiforgery on
every mutation, `no-store`, German ProblemDetails):

- `GET /api/recordings/{id}/passages` — members (published recording of a
  published event, only published songs; anything else is the recording's
  404) and editors. Editors additionally get `occurrences` (the event's
  performances in programme order with their passage id: the marker list),
  `playbackRevisionId`, `durationSeconds` and `hasPublishedProgramme`.
- `POST /api/recordings/{id}/passages` `{ performanceId, startSeconds, endSeconds, expectedPlaybackRevisionId }`,
  `PATCH …/passages/{passageId}` `{ startSeconds?, endSeconds?, expectedVersion, expectedPlaybackRevisionId }`,
  `POST …/passages/{passageId}/delete` `{ expectedVersion? }`,
  `POST …/passages/review` `{ expectedPlaybackRevisionId, passages: [{ id, expectedVersion }] }` — any editor.
  `expectedPlaybackRevisionId` is the `playbackRevisionId` of the editor GET
  the form was built from and is required on create, patch and review: when
  the file members play has changed since, the answer is the stale-state 409
  "Die Datei der Aufnahme wurde zwischenzeitlich ersetzt." and nothing is
  written. The review acts only on the listed passages (an empty or absent
  list is a 400; a foreign id 404; any stale row token refuses the whole
  review). A PATCH of a passage in need of review must state both bounds. Ids are checked
  against their stated parent (404 otherwise); times are validated in the
  API (ordering, a measured duration, two days at most); a recording without
  a playable file takes no passages (409). Two kinds of 409 stay distinct:
  "Die Zeitmarke wurde zwischenzeitlich geändert." (stale, reload) versus not
  allowed in this state (duplicate passage, no playable file; keep the input).
  A no-op PATCH does not move the version.
- `GET /api/songs/{id}/performances` rows gain `recordings`
  (`passageId`, `recordingId`, `recordingLabel`, `kind`, `isPublished`,
  `startSeconds`, `endSeconds`, `timestampState`); empty where nothing is
  marked, which is not a claim that no recording exists.
- `GET /api/songs?material=recording` completes the ARC-023 extension point:
  alone it is a song condition (a visible, trustworthy passage exists); next
  to other arrangement conditions the same arrangement (with a key filter the
  same version) must carry it. Editors count every passage, members only
  member-visible ones whose times are current.

Timestamp state: a passage is `current` only while its revision is the file
members play now (`RecordingFiles.Resolve`), otherwise `needsReview`. It is
computed on every read — not stored — so no hook is needed in
`RevisionChanges.MakeCurrentAsync`, and restoring the earlier file makes the
marks current again. Editors see values plus the state and re-anchor with an
edit or `…/review`; members get the song in the recording but no times, no
jump, and the filter does not count it.

Deleting or skipping a performance that has passages is refused, not cascaded:
`POST /api/performances/{id}/delete` and the programme-confirmation PUT answer
409 `Zeitmarken vorhanden: „Lied“ in „Aufnahme“ … Entferne zuerst diese
Zeitmarken in den Aufnahmen.` (names up to three; also when the foreign key
catches a race on PostgreSQL). The confirmation review carries `passageCount`
per occurrence so the UI warns before. The UI keeps its input on that 409.

Frontend: `components/aufnahme-zeitmarken.tsx` (member list with
"Zu „Lied“ springen"; editor marker list with "Position übernehmen" from the
open player, per-row save/remove, "Alle Zeitmarken für die aktuelle Datei
bestätigen"), `lib/zeitmarken.ts` (types, `m:ss`/`h:mm:ss` parsing and
formatting), `components/historie-aufnahmen.tsx` (history rows, through the
ARC-031 `erweiterung` slot), the catalogue checkbox "Aufnahme mit markierter
Stelle" (`?material=aufnahme`). `MedienSpieler` takes `abschnitt` and pauses
at the end of the segment ("Ende des Abschnitts … erreicht", repeat or play
on). `/auftritt/?id=…&aufnahme=<id>&stelle=<passageId>` opens at the passage;
an unknown or in-review passage opens the recording at the start and says so.
The ticket's "prefill the marker list from the programme" is deterministic:
the marker list is the event's occurrences in order, so the editor only sets
times; a published but unconfirmed programme has no occurrences yet and the
list points to the confirmation.

For later slices:

- ARC-022-1 (chat history tools): reuse `RecordingPassages.LinksByPerformanceAsync`
  (one visibility and state rule); never state a position for `needsReview`.
- ARC-035 (search): `RecordingPassages.RecordedChainsAsync` is the recorded
  song/arrangement/version set; link with `stellenPfad`.
- ARC-040 (event trash): passages and recordings are `Restrict`; trash must
  remove or keep passages explicitly and extend
  `RecordingVisibility.OfMemberVisible` / `EventVisibility`.
- ARC-046 (merge songs): a passage's song is its performance's song; moving a
  performance to another song moves its passages; the unique key is per
  (recording, performance), so merging two songs never collides.
- After applying the migration in a hosted database, run
  `infrastructure/neon/runtime-grants.sql` as usual.

## Catalogue write path and automated writes

Catalogue mutations go through one write service, `Catalogue/CatalogueWriteService.cs`:
song/arrangement/musical-version/event/asset field patches plus song creation,
arrangement/version creation and publish/unpublish of song and event, all with
validated German Problem titles, attribution and row-version bumps in one save;
endpoint lambdas keep antiforgery, role checks and responses, so endpoint
handlers and the automated writer cannot drift. Arrangements and musical
versions carry their own `RowVersion` concurrency column; song/arrangement/
version PATCHes carry it and a stale edit answers 409 "Die Fassung wurde
zwischenzeitlich geändert." without touching the row. Song detail responses
carry `rowVersion` on song, arrangement and version.

`Catalogue/AutomatedFieldWriter.cs` is the one automated path (scan reading):
it writes a certain, quote-verified value into a catalogued field only while
that field is still empty, and drops it otherwise (`Applied`, `Skipped` for a
field that holds a value, `Refused` for an unknown field, invalid value or lost
save race). It never overwrites a value an editor entered and keeps no record of
where a value came from. The vocabulary of writable fields is
`Catalogue/FieldCatalog.cs`; identity and visibility stay outside it, so new
songs, publication, deletion and member administration are never automated.
There is no suggestion queue: ADR-0001 keeps AI output to inline suggestions.
Migration `RemoveProvenanceAndProposals` dropped the former queue and
per-field origin tables and `reading_jobs."FieldsProposed"`.

## ARC-034-1 reading scanned scores

A score PDF whose extraction found no text ("Kein Text gefunden") is no longer
a dead end: the finite **AI reading** job renders its pages, has a named agent
transcribe them into the revision's text and fills the empty catalogue fields
of the score. Text PDFs go through the same second step with the regex analyzer
first, so a text PDF makes no model call for what the analyzer already found.

The journey: an editor uploads a score; the extraction job (model-free,
unchanged) finishes and, for every score, leaves a `reading_jobs` row in the
same save plus a message on the `archive-reading` queue. `archive-read` /
`--read-queue` (`Reading/ReadingWorker.cs`) then runs:

1. **Pages to text** (only for a `NoText` revision): PDFium renders at most 10
   pages per request (long side 1600 px, JPEG) and at most 20 pages per
   request of the editor (`Archive:Reading`). The agent `archive-scan-transcriber`
   answers with the page text; it becomes the revision's text
   (`extraction_jobs.Text`, `TextSource = Reading`, status `NoText` → `Completed`),
   so search, chat and the analyzer read it like embedded text. Progress is
   saved after every request: a retry or a resume after the cap never pays for
   a page twice. A longer scan ends "teilweise gelesen (20 von 35 Seiten)"; the
   editor's "Rest lesen" (`POST /api/revisions/{id}/reading/continue`) raises
   the page limit by another 20 and requeues the row.
2. **Text to fields** (`Reading/ScoreFieldPrefill.cs`): only fields that are
   empty, only while the revision is still the
   asset's current file. `ScoreTextAnalyzer` runs first; its facts are written
   as plain writes (composer, lyricist, arranger, key, voice). The agent
   `archive-score-fields` (text only, no tools, structured output) is asked only
   for what is still empty: the six fields `composer`, `lyricist`, `arranger`,
   `musical_key`, `voice_label` and `lyrics`. Each answer carries `sicher` or
   `unsicher` and a verbatim quote. Code verifies that the quote stands in the
   text (case, blanks and line breaks ignored) and that the quote ties the value
   to it: a name stands in its quote, **every line of the lyrics stands in the
   text** (a real first line cannot vouch for an invented stanza), the first
   word of a voice and the key (hyphens and blanks ignored) stand in their
   quote. Only `sicher` with a verified quote is written, and only while the field
   is still empty when the write happens (an editor's save during the model call
   wins); everything else is dropped and nothing is stored about it.
   Unknown or unrequested fields, repeated fields and over-long values in the
   answer are ignored. A refused write (a lost save race) drops its half-made
   change instead of poisoning the next save. No song identity is ever created,
   published or changed here; the job writes only through `AutomatedFieldWriter`.

State and budget: `ReadingStatus` is `Queued → Running → Completed | Failed`, plus
`WaitingForBudget`. When the monthly cap refuses a call the row becomes
`WaitingForBudget`, the attempt is not used up and the queue message is
acknowledged (deleted). Parked rows have no message, so something must wake
them: the dispatch sweep (`ReadingDispatcher`, run first by `--read-queue` and
alone by `--dispatch-reading`) asks `IAiBudget.WouldAdmitAsync` for a worst-case
transcription call and re-queues the parked rows only when it is admitted (new
month, raised cap). The editor sees "Das KI-Monatsbudget ist erreicht …"; manual
entry is never blocked. Other failures retry with backoff up to
`MaxAttempts` (4), then `Failed` with a German reason and an "Erneut lesen" button
(`POST /api/revisions/{id}/reading/retry`, which also creates the row for scans
extracted before this slice).

Bounds: own queue `archive-reading`, PDF up to 32 MiB, 8 minutes per message,
visibility 20 minutes, two messages per receive round. Every model call carries
an `AiOperation` with its own output bound and the image-token estimate
(`EstimatedTokensPerImage` per page); `Archive:Ai:MaxInputTokensPerCall`
(200,000) refuses any call whose estimated input would reach Luna's
long-context price tier (about 272,000 tokens), and `MaxOutputTokensPerCall` is
now 6,000 (the chat keeps its own 2,000). Text in a scan is data: both agents
are told so, and code, not the model, decides what is written.

UI: when reading is enabled, the extraction line of every score (`components/extraktion-status.tsx`)
shows the reading state ("KI liest die Seiten … (2 von 3 Seiten)", the count of
fields written, "Rest lesen", "Erneut lesen"); the list keeps polling only while
reading is queued or running. Editing the voice or description of a material by
hand goes through the shared write service.

Local run: `archive-read` is an explicit-start Aspire resource beside
`archive-extract`; without provider configuration the offline
`ScriptedChatClient` answers both requests (`Reading/ScriptedScanReader.cs`: it
cannot see images, it returns a fixed German sample page and picks fields out of
text by keyword), so the whole journey runs without credentials.
`dotnet run --project src/archive/apphost`, upload a scanned PDF, start
`archive-extract`, then `archive-read`.

**Switch.** `Archive:Reading:Enabled` is `false` in `appsettings.json` and `true`
in `appsettings.Development.json` (local AppHost, tests). While off, the
extraction job leaves no reading row and sends no message, the editor actions
answer 409 ("Das Lesen durch die KI ist noch nicht eingerichtet."), and every
extraction status item carries `readingEnabled: false` and `reading: null`, so
the page shows and offers nothing of the reading and does not poll for it. The
Bicep parameter `readingEnabled` (default `true`) sets `Archive__Reading__Enabled`
on the app and the three jobs in the same deployment that creates the Azure job,
its identity and the scheduled `--dispatch-reading` below; set it `false` and
redeploy to stop new hand-overs, the editor actions and the UI. The flag is not
checked by `ReadingWorker`, `--read-queue` or `--dispatch-reading`, so already
queued messages and the sweeps keep calling the model; to stop spending set
`Archive__MaintenanceMode=true` on both reading jobs (running executions must be
stopped separately; infra deploys and migrating releases reset it) or remove
`id-archive-read`'s OpenAI role, which fails pending readings
(`infrastructure/archive/README.md`). Scores extracted while it was off are read on
demand ("Seiten von der KI lesen").

Owner rule: the asset's own edit is creator-only (403 "Nur die anlegendende
Person kann das Material bearbeiten."). A PATCH that
changes nothing no longer bumps the asset's row version. "Rest lesen" is not
offered (and answers 409) once the revision's stored text has reached
`Archive:Extraction:MaxTextCharacters`, since more pages could add nothing. A
concurrent first "Erneut lesen" collides only on `PK_reading_jobs`; that case is
re-run once idempotently, every other save failure surfaces.

Production runs it through `infrastructure/archive/main.bicep` (written, applied by
the infrastructure workflow; see `infrastructure/archive/README.md`, "AI reading
path"): a Container Apps Job `ja-archive-read` running this image with
`--read-queue` (queue scale rule on `archive-reading`), its own identity
(Storage Queue Data Contributor and Blob Data Reader on the assets account plus
the Azure OpenAI user role; the only job identity with it) and the settings
`Archive__Chat__Provider`, `Archive__Chat__Endpoint`, `Archive__Chat__DeploymentName`,
`Archive__Reading__QueueServiceUri`, the database connection and the maintenance
flag; plus a scheduled run of `--dispatch-reading` (daily is enough) that wakes
work parked for budget (`ja-archive-read-dispatch`, own identity with the queue role
only). See the ticket for the full list.

Evaluation: `Category=ScanReadingEvaluation` runs generated scans through the
whole job with the scripted client (pass rate and cost per document, never a
provider call). `Category=ScanReadingEvaluationLive` reads the maintainer's
private scans from `ARCHIVE_SCAN_EVAL_DIR` (PDFs plus `expected.json`) and skips
unless it, `ARCHIVE_CHAT_ENDPOINT` and `ARCHIVE_CHAT_DEPLOYMENT_NAME` are set; the
format and the command are in the ticket.

Checks: `dotnet test tests/archive/backend --filter "FullyQualifiedName~Reading|FullyQualifiedName~PdfPageRenderer|FullyQualifiedName~ScanReadingEvaluation"`;
real PostgreSQL (migration over existing rows, constraints, the sweep, real ledger
SQL): `ARCHIVE_TEST_POSTGRES=… dotnet test tests/archive/backend --filter "Category=PostgresReading"`;
browser: `ARCHIVE_BASE_URL=… corepack pnpm exec playwright test tests/extraktion.spec.ts --workers=1`.
Migration `20261012052810_ScanReading` is additive: `extraction_jobs."TextSource"`
(default 0 = embedded) and table `reading_jobs`.

## Flat Lied and Dateien (#166, ADR-0001)

The new model is built beside the old catalogue (expand step of #158); the old
`songs`/`assets` tables and `/api/songs*` stay until #172 drops them.

- **Tables** (`backend/Lieder`, migration `AddLiederAndDateien`): `lieder`
  (Titel, Komponist, Bearbeiter, Texter, Besetzung, Tonart, Sprache, Liedtext,
  Anlass; no published state, `RowVersion` for 409) and `dateien`. A Datei has
  exactly one owner, a Lied or an Auftritt (`CK_dateien_owner`), a `Kind`
  (`noten`, `uebe`, `aufnahme`, `sonstiges`; `CK_dateien_kind`), `IsPartitur`
  (Noten only), `Stimme` (free text, "Alle" allowed), `Description`,
  `RecordedAtEventId` (Aufnahme of a Lied naming its Auftritt), blob and file
  facts, and the trash columns `DeletedAt`/`DeletedByAccountId`/
  `ReplacedByDateiId`. The whole shape is laid down now so #167, #168 and #171
  need no migration.
- **Visibility:** every active member reads every Lied. Always read Dateien
  through `DateiQueries.Visible(db)` (excludes the trash); #168 changes only
  that predicate.
- **Upload sessions** stay in `upload_sessions` (`PendingUpload`). The owner is
  exactly one of `AssetId` (old), `LiedId` or `EventId` (`CK_upload_sessions_owner`);
  Datei sessions also carry `DateiKind`, `IsPartitur`, `Stimme`, `Description`,
  `RecordedAtEventId`, `ReplacesDateiId` (#168) and `FinalizedDateiId`. So
  `POST /api/upload-sessions/{id}/renew|finalize` and `DELETE` serve both
  models and the browser engine in `lib/assets.ts` is shared. Open a session
  with `POST /api/lieder/{id}/dateien/upload-session`; finalize of a Datei
  session is `DateiUploads.FinalizeAsync`.
- **Kinds plug in** through `DateiKindRules` (content types, header gate,
  German rejection text); only `noten` (PDF) is registered, #167 adds the rest.
  Follow-up work runs through `IDateiFinalizedHook` registered in DI
  (`BeforeSaveAsync` in the finalize transaction, `AfterSaveAsync` best effort).
- **Blobs:** promoted Dateien live under `dateien/{dateiId}/{guid}`, apart from
  the old `revisions/` prefix, so #172 can clean up by prefix. Pending blobs
  are `pending/{guid}` as before.
- **Budgets:** `Archive:Assets:MaxCollectionBytes` applies per Lied (stored
  Dateien including the trash plus pending sessions). Several sessions of one
  Lied may be open at once (Partitur and Stimmen).
- **API:** `GET /api/lieder` (`q`, `besetzung`, `anlass`, repeated `dateiTyp`,
  `page`; also returns `options` for the filter lists), `GET /api/lieder/{id}`,
  `POST /api/lieder`, `PATCH /api/lieder/{id}`, `GET /api/dateien/{id}/access`.
- **Frontend:** `/lieder` and `/lied` use `lib/lieder.ts` and `lib/dateien.ts`.
  The Lied page is split by concern: `lied-kopf`, `datei-liste` (with
  `noten-ansicht`), `datei-hochladen`, `lied-formular`, composed in `lied-detail`.
  The resume key is `arc-upload-lied-<liedId>-<kind>-<partitur|stimme>`.

## Programm, Nachweise, Lied history and chat on Lieder (#170)

Programme items and performances reference a `Lied` (`LiedId`, Restrict FK) beside the old
song/arrangement/version chain, which stays nullable and readable until the final cleanup
ticket drops it. New rows carry only `LiedId`.

- `PUT /api/events/{id}/programme/items` takes `{id?, liedId, note}`; `POST .../performances`
  takes `{liedId, evidenceStatus, ...}`; confirmation additions take `liedId`. Embeds return
  `liedId`/`liedTitle` (plus `voiceConfiguration`/`musicalKey` of the Lied). A Lied has no
  versions, so a version correction on a Lied entry is refused.
- `GET /api/lieder/{id}/performances` (`Events/LiedHistoryEndpoints.cs`) is the Lied history:
  same honest counting as the old song history, without arrangements. Members only see
  occurrences at published Auftritte.
- Chat: `catalogue_search` and `lied_details` read `lieder` (every saved Lied is visible to a
  member). `lied_details` adds `gesungen` (confirmed or mentioned performances at published
  Auftritte) and `geplant` (published programme without confirmation). `LiedKarte` and
  `Quellen` resolve against `lieder`. The scripted client answers "Wann haben wir dieses Lied
  gesungen?" from that data and shows the Lied card.
- Frontend: `LiedWahl` (`components/lied-wahl.tsx`) replaces the Fassung picker.

## Runtime grants during `--migrate`

The release's `--migrate` step applies the runtime role's privileges itself:
after `MigrateAsync`, `Data/RuntimeGrants.cs` runs the embedded
`Data/runtime-grants.sql` (a byte-identical copy of
`infrastructure/neon/runtime-grants.sql`; `RuntimeGrantsTests` fails if they
drift, so change both together). It runs only when the connection is
`archive_migrator` and logs "Runtime grants skipped" for any other role, so
local Aspire runs are unaffected. It reuses the migration connection the job
already has: no extra secret, no `psql`. The script is idempotent and
transactional; a failure fails the step and leaves the maintenance window
active like a failed migration. The manual `psql` run remains a fallback and
the "run `runtime-grants.sql` as usual" notes above no longer apply to a
normal release.

Check: `dotnet test tests/archive/backend --filter "FullyQualifiedName~RuntimeGrantsTests"`;
with `ARCHIVE_TEST_POSTGRES` set to a throwaway superuser connection the live
case creates the fixed roles and an `archive` database, migrates as
`archive_migrator`, and asserts the privileges (runtime can use the new tables
and later tables, never `__EFMigrationsHistory`; an admin connection skips).

## Focused verification

From the repository root:

```bash
dotnet build src/archive/Archive.slnx
dotnet test tests/archive/backend
dotnet test tests/archive/apphost
```

The AppHost xUnit test needs Docker/Podman and pnpm. It enables the actual Aspire
dashboard/export endpoint, starts fresh containers, checks the deep link and
proxy, verifies migrations are initially pending and explicitly applies them,
tests Blob/queues and cookie/CSRF, runs the worker and asserts two captured mails.
The backend xUnit project is container-free, including an authenticated local OTLP
receiver test of all three signals and flush/redaction on success and failure.

From `src/archive/frontend`:

```bash
pnpm install --frozen-lockfile
pnpm run check
pnpm run build
pnpm exec playwright install chromium
ARCHIVE_BASE_URL=http://localhost:<frontend-port> pnpm run test:browser
```

Browser checks cover direct deep links, refresh and client navigation, actual API
version rendering, mobile layout, API error boundaries, retry after an outage,
and development diagnostics through the proxy. To run one test use
`pnpm run test:browser -- --grep "German deep link"`.
To focus xUnit use `dotnet test tests/archive/backend --filter FullyQualifiedName~TelemetryTests`.

Build/smoke the production image from the repository root:

```bash
docker build -f src/archive/Dockerfile --build-arg VERSION=0.1.0 \
  --build-arg REVISION="$(git rev-parse HEAD)" -t liedertafel-archive:local .
# ARC-010: Production refuses to boot on local SMTP capture. The endpoint is
# never contacted here (/alive, /api/build and static assets stay
# dependency-free; the mail transport builds lazily on first send).
# ARC-011: Production additionally requires Blob/Key Vault key persistence;
# the smoke stays dependency-free with the ephemeral test escape.
# ARC-011-1: Production additionally requires a passkey relying-party ID; the
# value is unused by the smoke (passkey ceremonies skip in production).
docker run --rm -d --name archive-smoke -p 127.0.0.1:18080:8080 \
  -e Mail__Provider=Azure \
  -e Mail__AzureEndpoint=https://acs-liedertafel-test.communication.azure.com \
  -e Authentication__AllowEphemeralKeysForTests=true \
  -e Authentication__PasskeyRelyingPartyId=archiv.liedertafel.test \
  liedertafel-archive:local
curl --fail http://localhost:18080/alive
docker exec archive-smoke sh -c '! command -v node && id -u'
```

Then run the same browser command with `ARCHIVE_BASE_URL=http://localhost:18080`
from the frontend directory, and `docker stop archive-smoke` when finished.
No database, storage, SMTP, OTLP endpoint or development environment is supplied
to this smoke container (only the Azure mail provider selection above, which is
never contacted by the smoke). For Podman builds, additionally pass
`--ignorefile src/archive/Dockerfile.dockerignore`; Docker automatically uses the
Dockerfile-specific ignore file. The final image runs as the .NET non-root user.

`.github/workflows/archive.yml` runs these checks and the production smoke
on archive changes (its release job then reuses the checked image to deploy). The full repository SDK compatibility check is
`dotnet build liedertafel.slnx`; existing analytics checks remain
`dotnet run --project tests/dashboard-api`.

See [ARC-001 verification evidence](https://github.com/JakobFerdinand/liedertafel/issues/97)
for the recorded local run.
