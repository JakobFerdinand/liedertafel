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

## Dateien: upload protocol, kinds and players

Every file in the archive is a `Datei` (ADR-0001, `GLOSSARY.md`): a Lied's
Noten, Übe-Dateien, Aufnahmen and sonstige Dateien, or an Auftritt's documents,
photos and concert Aufnahmen. There are no assets, no file revisions and no
revision history; a correction is a replacement (see "Replace, delete and the
Papierkorb"). Storage names: promoted Dateien live under
`dateien/{dateiId}/{guid}`, staged uploads under `pending/{guid}`.

**Three-step upload.** `POST /api/lieder/{id}/dateien/upload-session` (or
`POST /api/events/{id}/dateien/upload-session`) creates a pending session
(`upload_sessions`, `PendingUpload`) with a bounded write ticket; the browser
PUTs the file straight to that ticket URL; `POST /api/upload-sessions/{id}/finalize`
validates existence, size, the declared content type and the file's leading
bytes, promotes the object server-side and creates the Datei in one save
(`DateiUploads.FinalizeAsync`; an idempotent retry returns the same Datei).
Request body: `kind`, `isPartitur`, `stimme`, `description`, `fileName`,
`sizeBytes`, `contentType`, `replacesDateiId`, `recordedAtEventId`. Ticket URLs
exist only in JSON responses and are never logged. Only editors upload, and only
the creating editor renews, cancels or finalizes a session.

**Kinds** are registered in `Lieder/DateiKindRules.cs` (content types, header
gate, German rejection text). `noten`: PDF. `uebe`: audio (MP3, MP4/M4A, WAV,
OGG) and MIDI. `aufnahme`: the audio types plus WebM audio and MP4/WebM video.
`sonstiges`: PDF, JPEG/PNG/WEBP, plain text and docx/xlsx/pptx. A Stimme is
required for Noten that are not a Partitur and for Übe-Dateien (free text,
"Alle" allowed) and ignored for the other kinds. An Aufnahme of a Lied may name
the Auftritt it was recorded at (`recordedAtEventId`). Follow-up work plugs in
through `IDateiFinalizedHook` (registered in DI): `BeforeSaveAsync` runs inside
the finalize save (outbox rows, the soft delete of a replaced Datei),
`AfterSaveAsync` is best effort (queue sends).

**Large files and resume** (ARC-017). Per-file limit
`Archive:Assets:MaxUploadBytes` (~10 GiB), block size `UploadBlockBytes`
(8 MiB) reported in the session response. The browser transfers in Azure Blob
block operations directly against the ticket (`?comp=block&blockid=…`,
`?comp=blocklist&blocklisttype=all` to discover progress, `?comp=blocklist` to
commit), so file bytes never traverse ASP.NET. Tickets grant `Write|Create|Read`.

- Initiation declares the file identity (`sizeBytes`, `fileName`). The
  collection budget `Archive:Assets:MaxCollectionBytes` (40 GiB) is per owner,
  a Lied or an Auftritt, and counts stored Dateien (the trash included) plus
  pending sessions; it is enforced at initiation (declared) and finalization
  (actual), 413. A declared session reserves its declared size, an undeclared
  one its per-file cap. Several sessions of one owner may be open at once.
- `POST /api/upload-sessions/{id}/renew` extends a pending session and issues a
  fresh ticket, also after the old one expired; `DELETE /api/upload-sessions/{id}`
  cancels (terminal `Cancelled`, pending blob deleted best-effort). Finalized,
  abandoned and cancelled sessions answer 409.
- Finalize compares the declared identity: a mismatch is 409 `Die Datei passt
  nicht zur bestehenden Uploadsitzung.` before storage is touched and keeps the
  session retryable.
- Block-list commits carry no content type; finalize treats the storage default
  (`application/octet-stream`) as unset and falls back to the session's
  declared, whitelisted type.
- The browser persists `arc-upload-<key>` (session id, name, size, lastModified;
  Lied: `arc-upload-lied-<liedId>-<kind>-<partitur|stimme>`, Auftritt:
  `arc-upload-auftritt-…`), checks a re-selected file against it and
  re-transfers only missing blocks (`lib/uebertragung.ts`, shared by all
  uploads).
- `--cleanup-uploads` (`UploadSessionCleaner`, Development-gated like the other
  local jobs) marks pending sessions expired for longer than
  `Archive:Assets:ExpiryGrace` (1 hour) abandoned and deletes their pending
  blobs.

**Text extraction** (ARC-034, #169). Extraction and reading rows are keyed by
`DateiId` (`extraction_jobs`, `reading_jobs`, FK to `dateien`, Restrict). A PDF
Datei of kind `noten` or `sonstiges` is extractable; `DateiExtractionHook`
writes the outbox row in the finalize save and sends the queue message
afterwards; workers and endpoints read through `DateiQueries.Visible`, so the
trash means no work. Status and retry: `GET /api/dateien/extraction?ids=…`,
`POST /api/dateien/{id}/extraction/retry`. Only empty Lied fields are filled
(composer, lyricist, arranger, musical key, lyrics) through
`AutomatedFieldWriter`; `/api/lieder?q=` also matches the extracted text of a
Lied's live Noten.

**Storage adapter.** `IAssetStorageAdapter` / `BlobAssetStorageAdapter`
(`backend/Assets/AssetStorage.cs`, settings `Archive:Assets`, container
`archive-assets`) issue blob-scoped 15-minute read tickets and write tickets,
probe, read headers, promote (server-side copy from a signed source URL),
delete and list. The emulator fetches the copy source from inside its own
container, so the AppHost pins the Azurite blob host port to the container port
(`WithBlobPort(10000)`); integration tests pass `DcpPublisher:RandomizePorts=false`
for the pin to apply. Locally `archive-storage-init` creates the container and a
permissive emulator CORS rule for the direct browser transfers; production uses
user-delegation SAS through the runtime identity and CORS from infrastructure.

**Reading a Datei.** `GET /api/dateien/{id}/access` returns blob-scoped
15-minute view/download tickets. Every active member reads every Datei of a
Lied. Dateien of an Auftritt are visible to every member (a saved Auftritt has no
draft state) and members never get an
`aufnahme` this way, they use the recording's own access endpoint. Trashed
Dateien are not served (`DateiQueries.Visible`).

**Players** (frontend). `components/datei-spieler.tsx` plays Übe-Dateien and
Aufnahmen of a Lied: `audio-spieler.tsx` and `medien-spieler.tsx` for audio and
video (one player at a time; play/pause, seek, volume, time), `midi-spieler.tsx`
for MIDI. All take a `holeZugriff` that fetches a fresh ticket. Tickets are
renewed silently 60 seconds before `expiresAt` and the media element keeps
position and play state (seeking uses the blob's `Range` support); a media error
first tries one silent renewal, then shows an understandable state (undecodable
format with the download still offered, "Erneut versuchen" for transient
failures, specific copy for 404 and 401). The MIDI player is dependency-free
(`lib/midi.ts` parses SMF formats 0/1 into a seconds timeline, a small Web Audio
synthesizer plays it, 50–150 % tempo, no samples and no remote conversion); its
bytes fetch is a CORS request against blob storage.

**One-off cleanup of the old model.** Blobs of the dropped asset/revision model
are released by the finite operator command `--cleanup-dropped-blobs`
(`Assets/DroppedBlobCleaner.cs`, runs outside Development, uses the same image
and the storage identity of the other jobs). It deletes everything under
`revisions/` and the staged `pending/` objects no upload session refers to any
more, and never touches `dateien/` or any other name; `--dry-run` only counts.
Run it once after the release that applied the contract migration, see
`infrastructure/archive/README.md`.

## ARC-022 grounded history chat

Members chat with the archive at `/fragen` („Fragen zum Archiv"): `POST /api/chat` implements the AG-UI 1.0 wire contract (stable `AGUI.Server` 1.0.0 / `AGUI.Abstractions` 1.0.0 on `Microsoft.Extensions.AI` 10.10.0, superseding ARC-021's preview MAF hosting adapter). The bounded loop runs on an `IChatClient` provider seam — Development and tests use the deterministic `ScriptedChatClient` (cited answers from tool results only, honest unknown/refusal behaviour); the real Azure OpenAI implementation (managed identity, pinned GPT-6 Luna since ARC-021-1, formerly GPT-5.4-mini, EU Data Zone) plugs into the seam when credentials exist, and embeddings/pgvector move with it.

- Threads persist in the existing PostgreSQL (`chat_threads`/`chat_messages`, migration `20260922214454_ArchiveChat`), are owned by the asking member, are bounded to the last 20 messages, and are validated for ownership on every request. The client generates the thread id (`localStorage` key `arc-chat-thread`); server-side history is authoritative — client-provided older messages are ignored as tamper-proof grounding. `GET /api/chat/thread/{id}` restores the owner's history; foreign/missing threads answer an indistinguishable 404.
- Tools are allow-listed and authorized inside themselves: `catalogue_search` (all Lieder, `CatalogueText.Fold` token-AND, ≤ 10 results, 300-char lyrics excerpt) and `lied_details` (Lied fields, its live Dateien, where it was sung and where it is planned). Document text is data, never instructions; no editing tools, no raw queries, no general-knowledge answers.
- Per-request bounds are app-enforced: question ≤ 2000 chars, at most 5 model calls per run (`MaxToolCalls`; every call offers the tools, and a run whose fifth call still asks for a tool ends in the failure state without a sixth call), 30 s no-token abort per model call, 120 s overall cap, client-disconnect cancellation, one repetition of the run's first call when the provider fails before its first update (the only case with a sixth provider request), German `RUN_ERROR` failure state. Cost is bounded by the shared AI budget below.
- The chat answers 503 „Der Archiv-Chat ist derzeit nicht verfügbar." unless `Archive:Chat:Enabled=true` and `Disabled=false`, so production stays inert until the provider step. Questions about whole Auftritte answer honestly that the chat has no Auftritt tools (a single Lied's history comes from `lied_details`); the synthetic evaluation (`tests/archive/backend/ChatEvaluationTests.cs`, `--filter FullyQualifiedName~ChatEvaluation`) verifies the grounding bar — every citation verifiable against the authorized result set, zero unsupported claims, honest unknown behaviour, recorded EUR-per-answer cost — and must be rerun against the pinned live model before first production chat use.

### ARC-022-3 agent, AI gateway and the hard monthly cap

The chat runs on a Microsoft Agent Framework agent (`Microsoft.Agents.AI` 1.23.0, stable) named `archive-chat` (`Chat/ArchiveChatAgent.cs`, built once per process on the gateway and wrapped with the framework's OpenTelemetry instrumentation). `ArchiveChatService` supplies what belongs to one run through the run options: the tools on the request's database context, the thread's `ChatThreadHistoryProvider`, and the run pipeline tool loop → citation filter → per-call bounds. Thread history stays in `chat_threads`/`chat_messages`; the endpoint, its membership and ownership checks and the AG-UI wire contract (`AGUI.Server` 1.0.0) are unchanged. The Agent Framework hosting adapter and workflows are not used.

`Ai/AiGateway` is the only path to a model provider: OpenTelemetry → `BudgetedChatClient` → provider. The raw provider client is registered under the service key `AiGateway.ProviderKey` and nothing else resolves it; injecting `IChatClient` yields the budgeted pipeline. `AddArchiveAi(configuration)` registers provider selection, model keys, ledger, budget and gateway in one call (API host today, job hosts later). A feature attaches an `AiOperation` (feature name, operation id, member or none, optional lower output bound, estimate for non-text input) to the options of its run; a call without one is refused. An embedding provider registered under the same key (ARC-052) is wrapped by `BudgetedEmbeddingGenerator` the same way.

- **Cap:** EUR 15 per calendar month in Europe/Vienna, for all AI features together. Before each model call the middleware reserves the call's worst case (estimated input plus the output bound at the model's price). A reservation that would pass the cap is refused and the provider is not called. Guarantee: settled cost + open reservations + the worst case of a newly admitted call never exceeds the cap. After the call the reservation is replaced by the usage the provider reported; a call that ends without a usage report (failure, timeout, client disconnect, abandoned stream) costs its full reservation.
- **Ledger:** `ai_usage_entries` (renamed from `chat_usage_entries` by migration `20261006111010_AiUsageLedger`): one row per operation and model with feature, model, summed tokens of all its calls, settled cost and open reservation in micro-EUR. The month's spend is the sum over its rows; nothing is cached in memory. `ai_budget_months` holds one row per month as a lock: a reservation runs in one transaction that takes `SELECT … FOR UPDATE` on it, so reservations of a month queue on every replica (5 s lock timeout, then refused). A settlement is one relative `UPDATE` of its own row, retried three times, then logged at error and counted. `Ai/PostgresAiLedgerStore` is SQL; tests on the EF in-memory provider use `tests/archive/backend/InMemoryAiLedgerStore.cs` behind `IAiLedgerStore`.
- **Fail closed:** no price or a price of 0 for the model, a `ModelId` other than the client's own model, non-text input without an estimate, a ledger that cannot be reached, or a missing `AiOperation` all stop the call before the provider.
- **Member state at the cap:** the run ends with AG-UI `RUN_ERROR`, `code: "monatsbudget_erreicht"` and a message starting „Monatsbudget erreicht."; the chat page shows it without a retry button. Nothing else changes behaviour. Because a call reserves its worst case, the chat stops when the remainder no longer covers one worst-case call, slightly before EUR 15.00 is settled. `Archive:Chat:Disabled=true` remains the manual kill switch (503).
- **Configuration (`Archive:Ai`):** `MonthlyCapEur` (15), `MaxOutputTokensPerCall` (6000, the ceiling a feature can only lower; the chat states 2000 itself), `MaxInputTokensPerCall` (200000: a call whose estimated input is larger is refused before the budget, below Luna's long-context tier), `Models:<model key>:InputPricePerMillionEur` / `OutputPricePerMillionEur` (both above 0). The chat's model key is `Archive:Chat:DeploymentName` with the Azure provider, otherwise `scripted`. `appsettings.json` prices `gpt-6-luna` (0.1056 / 0.528 EUR, the EU Data Zone Standard short-context meters from the Azure Retail Prices API in EUR, ARC-021-1); Luna's long-context tier (about twice the rates above roughly 272,000 input tokens) is not in the ledger: `MaxInputTokensPerCall` keeps every call below it (ARC-034-1); `appsettings.Development.json` prices `scripted`, so the offline client is refused outside Development. `Archive:Chat:MonthlyBudgetEur` and the two `Archive:Chat:*PricePerMillionEur` keys no longer exist. To switch the model, change the deployment name and add its price entry. The Azure client sends `reasoning_effort: none` on every request unless a feature sets `ChatOptions.Reasoning` itself (Luna rejects function tools with reasoning on Chat Completions).
- **Telemetry** (source and meter `Liedertafel.Archive.Ai`, registered in the service defaults): spans `invoke_agent archive-chat`, `chat <model>` and `execute_tool <tool>` with the GenAI metrics (`gen_ai.client.token.usage`, `gen_ai.client.operation.duration`); budget instruments `archive.ai.tokens`, `archive.ai.cost`, `archive.ai.budget.refusals` (reason `cap`, `unpriced_model`, `ledger_unavailable`), `archive.ai.budget.settlement_failures`, `archive.ai.budget.month_spend`, `archive.ai.budget.month_cap`. Logs: `KI-Budget: … verbucht` (information, per settled call), `KI-Budget: Monatsbudget erreicht …` (warning), ledger, price and settlement failures (error). Message content is not exported, and failed spans carry the exception type only (`AiExceptionRedactionProcessor`). Never set `OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT`: it would export questions, answers and document text.
- **Checks:** `dotnet test tests/archive/backend --filter "FullyQualifiedName~AiBudget|FullyQualifiedName~BudgetedChatClient|FullyQualifiedName~ChatApi"`. The ledger's SQL needs real PostgreSQL: start `docker run --rm -d -e POSTGRES_PASSWORD=pw -p 55432:5432 postgres:17.6 -c max_connections=300` and run `ARCHIVE_TEST_POSTGRES="Host=localhost;Port=55432;Database=postgres;Username=postgres;Password=pw" dotnet test tests/archive/backend --filter "FullyQualifiedName~AiBudgetLedgerTests"` — every test then creates, migrates and drops its own database and runs the production store (cap race, 32-worker contention, two replicas opening a month, settlement after the month rolled over). The Aspire walking skeleton asserts one chat run's ledger row on PostgreSQL through the Development-only `GET /api/dev/ai-budget`. The live evaluation command is unchanged (`Category=ChatEvaluationLive`, see the root `AGENTS.md`; for Luna: `ARCHIVE_CHAT_ENDPOINT="https://aoai-liedertafel-archive.openai.azure.com/" ARCHIVE_CHAT_DEPLOYMENT_NAME="gpt-6-luna" ARCHIVE_CHAT_MODEL_VERSION="gpt-6-luna 2026-09-22"`); it returns immediately when the first two are unset, and its calls are charged to the ledger of the test's in-memory database, not to production. Result and open steps: [ARC-021-1 (#122)](https://github.com/JakobFerdinand/liedertafel/issues/122).
- **Release of the ledger migration (owner decision 2026-10-06):** the rename ships in one release, against the split-rename compatibility rule. A code-only rollback to the previous image breaks the chat (it writes `chat_usage_entries`); this release is fix-forward only for chat. After the release: run `infrastructure/neon/runtime-grants.sql` as migrator, check the runtime role's privileges on `ai_budget_months`, smoke one chat run. Details and the exact checks: `infrastructure/archive/README.md`, "Rollback". If `aiChatDeploymentName` is overridden, set both `Archive__Ai__Models__<deployment>__…PricePerMillionEur` entries or every run is refused.
- **Switching to GPT-6 Luna (ARC-021-1):** two pushes, image first (this code, still on the `gpt-5-4-mini` deployment), then `infrastructure/archive/**` (creates the `gpt-6-luna` deployment and points the app at it). Before the first push run `ChatEvaluationLive` once against the existing deployment, because the SDK's `api-version=2024-10-21` may reject `reasoning_effort`. Confirm the Luna prices against the Azure Retail Prices API in EUR (`currencyCode='EUR'`, `Azure OpenAI GPT6`, meters `6-luna ShortCo Inp/Opt Std DZ`, `germanywestcentral`) before each release that touches them. Exact steps and the Luna evaluation command: [ARC-021-1 (#122)](https://github.com/JakobFerdinand/liedertafel/issues/122).

### ARC-022-4 AG-UI client and A2UI cards

The chat page reads the run with `@ag-ui/client` 1.0.2 (`HttpAgent` in `frontend/lib/chat.ts`, posting straight to `/api/chat` through the same CSRF-aware fetch as every other mutation) and shows what the agent displays with `@a2ui/react` 0.13.0 / `@a2ui/web_core` 0.13.0 (protocol v0.9, `zod` 3.25.76; all pinned, no CopilotKit, no Node runtime, still a static export). The A2UI library is loaded on demand (`next/dynamic`, no server rendering) only when an answer carries surfaces. The basic catalog is not registered: the catalog `urn:liedertafel:archiv:a2ui:katalog:v1` has exactly `LiedKarte`, `AuftrittKarte`, `ProgrammListe` and `Quellen` (`frontend/lib/a2ui.ts` schemas, `components/a2ui-katalog.tsx` implementations, cards styled in `app/fragen/chat.css`).

- **Wire:** the server library wrote protocol fields it does not use as explicit nulls (`usage[].model`, `parentRunId` …), which the strict client rejects as a schema violation. `ChatEndpoints` now serializes events without null properties. `RUN_FINISHED`, `RUN_ERROR` with its `code`, the `archive.citations` custom event and the budget state are unchanged for the page.
- **The tool:** the agent calls `zeige_oberflaeche(komponenten)`; each entry is a component name and exactly the one record id it is built from (`liedId`, `liedIds`, `auftrittId`). `Chat/A2uiSurfaces.cs` (`A2uiCatalog`) validates every entry against the catalog (known component, only its own id, valid GUIDs, at most 12 entries looked at, at most 4 surfaces per answer, one card per record), resolves the ids to what a member may see (Lieder and published Auftritte only) and builds the A2UI messages itself: `createSurface` plus one `updateComponents` with a single `root` component per surface. Anything else is dropped before streaming; the model only learns how many entries were not shown, never which or why. The model supplies no text and no URL, and the server sends none: the browser derives links (`/lied/?id=…`, `/auftritt/?id=…`) from the ids. The tool call itself is removed from the AG-UI stream; the validated messages travel in one `CUSTOM` event `archive.a2ui` (before the `archive.citations` chips, which stay the server-built event).
- **Member chat offers:** `LiedKarte` (title, composer, lyricist, first lines), `AuftrittKarte` (title, kind, date, venue) and `Quellen` (up to 10 Lieder). `ProgrammListe` is defined with its wire shape and the browser renders it, but the member chat has no resolver for it yet (always dropped, not offered to the model); ARC-022-1 and ARC-022-2 add its resolver to `A2uiCatalog.Components`.
- **Storage and reload:** the validated messages are stored with the assistant answer in `chat_messages."A2uiJson"` (`jsonb`, migration `20261011160524_ChatMessageA2ui`; a card-only answer stores empty content and is not fed back to the model as history). `GET /api/chat/thread/{id}` never returns the stored JSON as it is: `A2uiCatalog.RecheckAsync` rebuilds each surface from the record ids it names, so a card for a since-unpublished Auftritt or a deleted Lied is gone, tampered or unreadable stored content yields nothing, and data is fresh. The message then has an `a2ui` array (or none).
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
decision per request; every saved Auftritt is visible to every member, there is
no draft or publish state, ADR-0001):

- `GET /api/events` is unpaginated (archive scale) and filters/sorts in C#
  so InMemory tests and PostgreSQL agree: known years descending, within a
  year day-precision first (month/day descending), then month-only, then
  year-only, unknown years last; `?year=`/`?kind=` narrow the list while the
  `years` navigation summary ignores the filters and ends with the
  `year: null` "Ohne Jahr" group.
- `GET /api/events/{id}` adds `notes`, `sourceNote`, `createdAt`,
  `updatedAt`.
- Editor-only `POST /api/events` creates the Auftritt; `PATCH /api/events/{id}`
  leaves absent fields unchanged, clears present-empty strings and treats
  the nested `date` as an explicit full replacement (`null` year = unknown
  date; 409 on concurrent edits). The former `POST .../publish` and
  `/unpublish` endpoints and the `PublishedAt` columns are gone (migration
  `RemoveEventPublication`; events that were still unpublished became visible).
- Deletion stays with ARC-040 trash.

The UI rides the redesigned system: `/auftritte/` is navigated through a
year rail ("Alle", year tiles with counts, "Ohne Jahr" last) beside the
newest-first list with kind labels and the editor's Bearbeiten; `/auftritt/?id=` keeps uncertainty honest
(`Datum unsicher` badge on approximate/partial dates) and always shows the
empty Programm, Dokumente and Aufnahmen sections reserved for ARC-025
documents, ARC-026 programmes and ARC-032 recordings. The editor form
derives the date from the filled year/month/day fields — empty means an
unknown date, nothing invented.

Handoff: the stable event IDs are the anchor points the later slices attach
to; see "Programm and Lied history (#160)" for the Programm.

## Programm and Lied history (#160)

An Auftritt has **one editable Programm** (ADR-0001, `backend/Events`): the
ordered list of Lieder it was sung with, which is also the record of what was
sung. There are no programme revisions or drafts, no separate confirmation step
and no `confirmed`/`mention` evidence states; tables `programme_revisions`,
`programme_confirmations` and `performances` are gone (migration
`AuftrittProgramm`, which keeps the items of each programme's newest revision as
its Programm and maps Zeitmarken from a performance to the Programm entry it
confirmed; marks on evidence rows without a surviving entry are dropped, test
data).

- `programmes` (one row per event, `RowVersion` for 409) and `programme_items`
  (`ProgrammeId`, `Position` 1..n unique per programme, `LiedId` Restrict,
  `Note` ≤ 500). A Lied may stand twice; every entry has a stable id.
- `PUT /api/events/{eventId}/programme/items` `{ items: [{ id?, liedId, note? }],
  rowVersion? }` (editor) is the whole ordered replacement and the whole
  publication step: saving makes the Programm what members read at once. Known ids update in place (reordering keeps them,
  and with them the Zeitmarken), missing ids are removed, the first save creates
  the programme (`rowVersion` is required afterwards, else 409). Removing an
  entry that carries Zeitmarken is refused with 409 `Zeitmarken vorhanden: …`
  (`RecordingPassages.BlockedMessageAsync`). There is no publish endpoint.
- `GET /api/events/{id}` embeds `programme` (`id`, `rowVersion`, `updatedAt`,
  `items` with `liedId`, `liedTitle`, `voiceConfiguration`, `musicalKey`,
  `note`; `null` without a Programm) and `liedAufnahmen`: the live
  Lied-Aufnahmen (`dateien.RecordedAtEventId`) that name this Auftritt.
  `GET /api/programmes` lists the upcoming Programme (`updatedAt`, `itemCount`).
- `GET /api/lieder/{liedId}/historie[?page=]` (`LiedHistoryEndpoints.cs`) is the
  Lied history ("wann gesungen"), derived on every read from the Programme plus
  the Lied-Aufnahmen naming an Auftritt. One row is one Auftritt (newest first by
  the honest date, `EventDate.CompareNewestFirst`, fixed pages of 20): `entries`
  (how often the Lied is in the Programm), `inProgramme`, `liedAufnahmen`,
  `recordings` (Zeitmarken of that Lied, with `timestampState`), the honest date
  and `dateUncertain` (a year-only or approximate date). `counts` has `events`,
  `inProgramme`, `uncertainDates`. An unknown Lied is 404.
- Chat: `lied_details` reads the same data; `gesungen` lists past Auftritte with
  the Lied in the Programm or an Aufnahme, `geplant` upcoming ones.
- Frontend: `components/auftritt-programm.tsx` (reading list and the editor's
  "Programm bearbeiten"), `auftritt-lied-aufnahmen.tsx`, `lied-historie.tsx`
  ("Wann gesungen"), `lied-wahl.tsx`. Tests: `AuftrittProgrammTests`,
  `LiedHistorieTests`, `AuftrittProgrammMigrationPostgresTests` (needs
  `ARCHIVE_TEST_POSTGRES`), `frontend/tests/programm.spec.ts`,
  `lied-historie.spec.ts`.

## Replace, delete and the Papierkorb (#168)

There is no revision history. A Datei is replaced by uploading a new one and
deleted into the Papierkorb; both are soft deletes of the old Datei (`DeletedAt`,
`DeletedByAccountId`, `ReplacedByDateiId`) and the blob is kept.

- **Replace:** open the upload session with `replacesDateiId`. The target must
  be a live Datei of the same Lied (a Datei of an Auftritt: of the same Auftritt,
  kind `sonstiges`); the new Datei inherits kind, Stimme, Partitur, description
  and Aufnahme-Auftritt from it. `DateiReplacementHook` soft-deletes the old
  Datei and sets `ReplacedByDateiId` in the finalize save. If the target left in
  the meantime, finalize is 409 `ReplacedMeanwhileMessage` and the session is
  abandoned.
- **Delete:** `POST /api/dateien/{id}/delete` `{ rowVersion }` → 204; stale 409,
  gone 404, member 403. Works for Dateien of Lieder and of Auftritte.
- **Papierkorb:** `GET /api/papierkorb` (editor) lists trashed Dateien with
  owner (Lied or Auftritt), who deleted them, when and whether they were
  replaced; the page is `/verwaltung/papierkorb/`. The collection budget still
  counts trashed bytes. Restoring (#146) clears the trash columns.
- `DateiQueries.Visible(db)` is the only "live Dateien" predicate; read Dateien
  through it. `DateiQueries.Trashed(db)` is the trash.
- A Datei is immutable: a changed file is a new Datei. Zeitmarken, extraction
  and the recording slots all key on the Datei id, so replacing a file makes
  them follow the new Datei (or read as in need of review), and restoring the
  old Datei makes them trustworthy again without any bookkeeping.

## Auftritt Dateien and Aufnahmen (ARC-025, ARC-030, #171)

Documents, photos and concert recordings of an Auftritt are Dateien owned by the
Auftritt (`dateien."EventId"`; exactly one of Lied or Auftritt owns a Datei,
`CK_dateien_owner`).

**Documents and photos.** `POST /api/events/{id}/dateien/upload-session`
(editor) with kind `sonstiges` (PDF, JPEG/PNG/WEBP, text, docx/xlsx/pptx; photos
must show the honest signature of their type); `noten` and `uebe` are refused
(422). The event detail embeds the live `sonstiges` Dateien as `documents`
(`id`, `art` document|photo, `description`, `fileName`, `contentType`, `sizeBytes`,
`createdAt`, `rowVersion`); `PATCH /api/dateien/{id}` `{ description, rowVersion? }`
edits the description. Replace and delete follow the Papierkorb rules. PDFs get
the same extraction status as Noten. Frontend: `components/auftritt-dokumente.tsx` (photos as a grid with a
dialog for the large view, documents with Öffnen/Herunterladen).

**Recordings** (`backend/Recordings/`, table `recordings`, ARC-030). A recording
is labelled audio or video material about one Auftritt with a stable id; an
Auftritt may carry several, and creating or attaching one never touches the
Auftritt or its Programm. A recording keeps its own row (label,
`Kind`, publication, download switch, measured duration) and points at two
`aufnahme` Dateien of the same Auftritt:

- `OriginalDateiId`: the preserved original, whatever its format (any audio or
  video container; the type is recognised from the bytes at finalize, only an
  empty file is refused).
- `PlaybackDateiId` (optional): an externally converted playback copy. It is
  accepted only if its leading bytes are a container browsers play for the
  recording's kind (MP4/M4A, WebM, MP3 or WAV for audio, MP4 or WebM for video,
  `RecordingFormats.cs`); otherwise 422 and the unusable Datei is trashed while
  the old copy stays.

`RecordingFiles.Resolve` decides what members play: the playback copy when it
has a file, otherwise a playable original serving both roles as one object. A
trashed Datei counts as an empty slot (`RecordingFiles.Live`). This is a
container check, not a decode, and there is no transcoding. Check constraints:
`CK_recordings_kind` and `CK_recordings_dateien` (the playback slot never holds
the original).

Endpoints (all `no-store`, antiforgery on mutations):

- `GET /api/events/{id}/recordings`: members get published recordings of
  the Auftritt; editors get all, with an
  `editor` block (version, both slots with file facts).
- `POST /api/events/{id}/recordings` `{ label, kind }` (editor) creates the
  recording with empty slots.
- `PUT /api/recordings/{id}/dateien/{original|playback}` `{ dateiId }` (creating
  editor) attaches an uploaded Aufnahme Datei of the same Auftritt. The Datei
  previously in the slot goes to the trash (`ReplacedByDateiId`); the measured
  duration is cleared when the playable Datei changes.
- `PATCH /api/recordings/{id}` `{ label?, isPublished?, downloadEnabled?,
  durationSeconds?, expectedVersion? }` (any editor); a stale version is 409,
  publishing without any file is another 409.
- `GET /api/recordings/{id}/access`: the renewable ticket (same call again);
  `viewUrl` is null while nothing is playable and `downloadUrl` is null with no
  download ticket issued unless an editor enabled downloads (default off; the
  switch withholds the affordances and `controlsList="nodownload"`, it is not
  copy protection).

Files change only through the creating editor's session; labels, publication
and the download switch are open to every editor. Storage counts against the
Auftritt's collection budget. `durationSeconds` is measured by the uploading
editor's browser.

Frontend: `components/auftritt-aufnahmen.tsx` on `/auftritt/?id=…` plays through
`components/medien-spieler.tsx`; `/auftritt/?id=…&aufnahme=<id>&t=<seconds>` opens
one recording at a position.

**Zeitmarken** (table `recording_passages`, `RecordingPassage`). A Zeitmarke is
set by hand and links a recording to an entry of the Auftritt's Programm
(`ProgrammeItemId`) with `StartSeconds`/`EndSeconds` and `PlaybackDateiId`, the
playback Datei the two times were taken against. Database rules:
`StartSeconds >= 0`, `EndSeconds > StartSeconds`, one passage per (recording,
Programm entry), all foreign keys `Restrict`, and the recording as a composite
key (the passage carries `EventId` and points at `recordings (Id, EventId)`);
that the entry belongs to the same event is checked by the endpoint. A second
recording can mark the same entry; nothing here changes the Programm.

- `GET /api/recordings/{id}/passages`: members (published recording) and
  editors; editors also get `entries` (the Programm in
  order with each entry's Zeitmarke id: the Zeitmarkenliste), `playbackDateiId` and
  `durationSeconds`. A Zeitmarke (wire name `passage`) carries `programmeItemId`, `liedId` and
  `liedTitle`.
- `POST /api/recordings/{id}/passages` `{ programmeItemId, startSeconds, endSeconds,
  expectedPlaybackDateiId }`, `PATCH …/passages/{passageId}`,
  `POST …/passages/{passageId}/delete`, `POST …/passages/review` (any editor).
  `expectedPlaybackDateiId` is the `playbackDateiId` of the GET the form was
  built from: when the Datei members play has changed since, the answer is the
  stale-state 409 `Die Datei der Aufnahme wurde zwischenzeitlich ersetzt.` and
  nothing is written. A recording without a playable file takes no passages
  (409). Two kinds of 409 stay distinct: stale (reload) versus not allowed in
  this state (duplicate, no playable file; keep the input).
- `GET /api/lieder/{id}/historie` rows carry `recordings` (`passageId`,
  `recordingId`, `recordingLabel`, `kind`, `isPublished`, `startSeconds`,
  `endSeconds`, `timestampState`); empty where nothing is marked, which is not
  a claim that no recording exists.

Timestamp state: a passage is `current` only while its `PlaybackDateiId` is the
Datei members play now, otherwise `needsReview`. It is computed on every read,
not stored; legacy rows without a Datei read as `needsReview`. Editors see the
values plus the state and re-anchor with an edit or `…/review`; members get the
Lied in the recording but no times and no jump.

Removing a Programm entry that has passages is refused, not cascaded: the
programme save answers 409 `Zeitmarken vorhanden: „Lied“ in „Aufnahme“ …
Entferne zuerst diese Zeitmarken in den Aufnahmen.`

Frontend: `components/aufnahme-zeitmarken.tsx` (member list with "Zu „Lied“
springen"; editor marker list with "Position übernehmen" from the open player,
per-row save/remove, "Alle Zeitmarken für die aktuelle Datei bestätigen"),
`lib/zeitmarken.ts` (types, `m:ss`/`h:mm:ss`), `components/historie-aufnahmen.tsx`
(history rows). `MedienSpieler` takes `abschnitt` and pauses at the end of the
segment. `/auftritt/?id=…&aufnahme=<id>&stelle=<passageId>` opens at the passage.

## Flat Lied and Dateien (ADR-0001)

The old catalogue (songs, arrangements, musical versions, assets, file
revisions) is gone: migration `DropOldCatalogue` (#172) dropped its tables and
the legacy columns, and with it every row that could only be read through them
(programme items and performances without a Lied, recordings without an
original Datei, old upload sessions; test data, ADR-0001). Accounts, passkeys
and everything of the flat model are untouched.

- **Tables** (`backend/Lieder`, migration `AddLiederAndDateien`): `lieder`
  (Titel, Komponist, Bearbeiter, Texter, Besetzung, Tonart, Sprache, Liedtext,
  Anlass; no published state, `RowVersion` for 409) and `dateien`. A Datei has
  exactly one owner, a Lied or an Auftritt (`CK_dateien_owner`), a `Kind`
  (`noten`, `uebe`, `aufnahme`, `sonstiges`; `CK_dateien_kind`), `IsPartitur`
  (Noten only), `Stimme` (free text, "Alle" allowed), `Description`,
  `RecordedAtEventId` (Aufnahme of a Lied naming its Auftritt), blob and file
  facts, and the trash columns `DeletedAt`/`DeletedByAccountId`/
  `ReplacedByDateiId`.
  - **Visibility:** every active member reads every Lied. Always read Dateien
  through `DateiQueries.Visible(db)` (excludes the trash).
- **Upload sessions** live in `upload_sessions` (`PendingUpload`). The owner is
  exactly one of `LiedId` or `EventId` (`CK_upload_sessions_owner`); sessions
  also carry `DateiKind`, `IsPartitur`, `Stimme`, `Description`,
  `RecordedAtEventId`, `ReplacesDateiId` and `FinalizedDateiId`. Open one with
  `POST /api/lieder/{id}/dateien/upload-session`; finalize is
  `DateiUploads.FinalizeAsync`.
- **Kinds plug in** through `DateiKindRules` (content types, header gate,
  German rejection text). Follow-up work runs through `IDateiFinalizedHook`
  registered in DI (`BeforeSaveAsync` in the finalize transaction,
  `AfterSaveAsync` best effort).
- **Blobs:** promoted Dateien live under `dateien/{dateiId}/{guid}`, pending
  blobs are `pending/{guid}`.
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

## Failure alerts (ARC-042, #149)

The Azure side lives in `infrastructure/archive/main.bicep` (action group,
four scheduled-query rules over `ContainerAppConsoleLogs_CL` in the
Log Analytics workspace, rule names `al-archive-*`) and its operator path /
runbook in `infrastructure/archive/README.md`, section "Failure alerts
(ARC-042)". Alerts key on the shipped console-log failure lines only —
deterministic (architecture §14), no model involvement, no metric alerts on
application meters (production has no OTLP endpoint; development keeps the
Aspire path untouched). The queries' marker strings are pinned by
`tests/archive/backend/AlertQueryContractTests.cs`, which runs in the normal
backend suite: renaming one of the failure line templates in
`service-defaults/Extensions.cs`, the workers or `Program.cs` breaks the
build exactly when the deployed queries would stop matching.

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

## Quellen-Inventur (#98)

The deterministic Drive/source inventory lives in `backend/Inventur/`: the
console command `--inventur <verzeichnis>` walks one folder export without any
database, queue or model (architecture §14) and reports file counts, aggregate
and largest sizes, leading-byte container sniffs (reusing `RecordingFormats`
and the MIDI gate), MIDI presence, per-PDF PDFium page counts plus PdfPig
extractable-text classification, duplicate-name candidates (never resolved by
the walk) and the analytical scan-reading cost estimate priced from
`Archive:Ai:Models` (`--model <schlüssel>`), bounded by the reading job's own
page/image assumptions. Its report documents the maintainer
runbook (`src/archive/docs/drive-inventur.md`), including the real source-link
handling in git-ignored `src/archive/.local/source-inventory.md`. The sanitized fixture
corpus for the bulk import (#143) lives in
`tests/archive/fixtures/drive-inventur/` with the machine-readable
`mapping.json`; `InventurTests` keeps corpus, mapping and report agreed.

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
