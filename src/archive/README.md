# Choir archive

The Rust `archive-server` serves the unchanged Next.js static export and `/api/*`
under one production origin. Aspire still orchestrates local development, where
Next.js/Turbopack supplies hot reload and a same-origin API proxy. There is no
Node.js or .NET runtime in the production image.

## Start from a clean checkout

Prerequisites: the pinned Rust toolchain (`rustup`), .NET 10 SDK (Aspire only),
Node.js 24 and pnpm 11.3.0, and Docker or Podman. From the repository root:

```bash
(cd src/archive/backend-rs && cargo build --workspace --locked)
pnpm --dir src/archive/frontend install --frozen-lockfile
dotnet run --project src/archive/apphost
```

For Podman, set `ASPIRE_CONTAINER_RUNTIME=podman` before starting AppHost.
For PDF extraction or Lesungen, set `Archive__Pdfium__LibraryPath` to the
absolute path of the pinned native library described in
[image native notices](backend-rs/docs/image-native-notices.md); video work needs
`ffmpeg` and `ffprobe` on PATH. No cloud credentials are needed for normal startup.

Open the dashboard login URL printed by AppHost. Start **archive-migrate** and
wait for exit 0, then open `archive-frontend` and visit `/system/status/`.
**Lokale Dienste prüfen** exercises PostgreSQL, Blob, Queue and Mailpit. The
Mailpit HTTP endpoint shows the captured email. Ctrl+C stops the stack; local
PostgreSQL and Azurite volumes persist. `Archive__PersistLocalData=false` starts
fresh local containers instead. Dashboard/OTLP endpoints use authenticated
loopback HTTP; browser sessions use Secure cookies in production.

## Resources and configuration contract

| Resource | Contract |
| --- | --- |
| `archive-postgres` / `archive-db` | PostgreSQL 17.6; local Aspire-generated password |
| `archive-storage` | Azurite 3.35.0 with Blob and Queue endpoints; Blob port 10000 also supports emulator server-side copy |
| `archive-mail` | Mailpit 1.27.8; captured SMTP and HTTP inbox |
| `archive-storage-init` | Automatic finite setup of private `archive-assets` and configured extraction, reading and video queues |
| `archive-api` | Cargo-built executable; `Archive__ServerBinary` overrides `backend-rs/target/debug/archive-server` |
| `archive-frontend` | Turbopack proxy; receives `PORT` and server-only `ARCHIVE_API_URL` |
| `archive-migrate` | Explicit; only resource receiving `ConnectionStrings__archive-migrations` |
| `archive-worker-smoke`, `archive-extract`, `archive-read`, `archive-transcode`, `archive-papierkorb-cleanup` | Explicit finite runs; AppHost supplies local dependencies and OTLP |

AppHost passes `ARCHIVE_ENVIRONMENT=Development`, local `ConnectionStrings__archive-db`,
`ConnectionStrings__archive-blobs` and `ConnectionStrings__archive-queues`, SMTP
(`Mail__Host` / `Mail__Port`) and an offline scripted model. `/api/dev/*` diagnostics
are absent in production. Diagnostics use isolated scratch Blob and Queue objects;
they never consume feature queues. The separate, explicit `archive-mail-test`
resource requires `Mail:Provider=Azure` and `Archive:MailTestRecipient` in AppHost
configuration plus authorized Development mail credentials. Ordinary startup
stays on Mailpit. Production requires keyless Azure mail and the configured
passkey relying party and origins. It uses the existing user-assigned managed
identities and configured Blob/Queue service URIs, not emulator credentials.

`ConnectionStrings__archive-db` accepts the existing Npgsql-style environment
string (including `SSL Mode=VerifyFull`) and uses at most five connections,
zero minimum connections, five-second connect and ten-second command timeouts
and no periodic keep-alive. `/alive`, Development `/health` and `/api/build`
never query dependencies. Production sessions and CSRF state use PostgreSQL,
not the legacy ASP.NET Data Protection key ring. Existing cookies do not carry
over; compatible passkeys and account records are retained. The unused legacy
key-ring infrastructure is left in place for cutover and potential rollback.

## Schema ownership and explicit commands

API startup never migrates. Apply reviewed SQL under
`backend-rs/crates/archive-db/migrations/` via **archive-migrate** in Aspire,
or use the explicit command with a dedicated migration-role credential:

```bash
# Run from src/archive/backend-rs with ConnectionStrings__archive-migrations supplied.
cargo run --locked -p archive-server -- --migrate
# Same finite entry point in the packaged image:
docker run --rm -e ConnectionStrings__archive-migrations localhost/liedertafel-archive:local --migrate
```

The frozen EF history is baseline metadata. The first Rust migration creates a
fresh baseline or adopts the exact independently verified frozen head; older or
drifted schemas refuse adoption rather than replaying historical transforms.
SQLx records the applied checksums; only the explicit migration process runs
these scripts and repairs runtime grants. In Development, AppHost injects the
mandatory OTLP endpoint for finite jobs as well as the API. Production failure
returns nonzero and leaves maintenance active. See the [baseline provenance and
schema gate](backend-rs/crates/archive-db/baseline/README.md) and
[release runbook](../../infrastructure/archive/README.md#controlled-database-releases-arc-012).

## Frontend and HTTP contract

- Browsers use relative `/api/*` URLs; only the Next dev server reads `ARCHIVE_API_URL`.
  The proxy preserves cookies, `X-CSRF-TOKEN`, bodies, statuses and content types.
- `GET /api/antiforgery` supplies a token and HTTP-only SameSite cookie. Local
  exercises and operator-only endpoints are not present in the packaged prod origin.
- `pnpm run build` emits `out/`; the Rust origin serves each generated route's
  `index.html`, hashed assets and Next navigation payloads. Unknown API routes
  return JSON Problem Details; missing pages and assets stay 404s.
- Keep new pages statically exportable. Use client-side API calls and query
  parameters for runtime IDs. No generic HTML fallback, SSR or `next start`.

## Telemetry and finite workers

`archive-telemetry` consumes AppHost's authenticated OTLP endpoint, protocol
and headers for **logs, traces and metrics**. `OTEL_SERVICE_NAME` identifies
resources. Finite workers report the exit outcome, drain admitted work and
flush telemetry on handled failure and success. Queue envelopes propagate W3C
`traceparent` / `tracestate`, not arbitrary baggage. Health probes are excluded
from traces. Never log codes, cookies, credentials, message bodies or signed URLs.

In the Aspire dashboard, find `Archive build information requested` for
`archive-api` and follow the trace. Run `archive-worker-smoke` to check
correlated Blob/Queue/SMTP work and the finite-job completion record. The
`archive.build.requests` meter is exported under `Liedertafel.Archive`.

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

**Kinds** are registered in `backend-rs/crates/archive-server/src/catalogue` (content types, header
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
commit), so file bytes never traverse the Rust origin. Tickets grant `Write|Create|Read`.

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
`AutomatedFieldWriter`; `/api/lieder?q=` (#142, extension of #169) matches every
token against the title, the creators, the Liedtext and the folded text of a
Lied's live Noten — embedded text and text transcribed by a reading job alike
(`extraction_jobs.Text`, also with `TextSource=Reading`); a replaced or trashed
Datei stops matching through `DateiQueries.Visible`. Each returned Lied carries
`matchIn`, the German labels of the fields that contributed ("im Titel",
"beim Komponisten", "beim Bearbeiter", "beim Texter", "im Liedtext",
"in den Noten"). Matching stays in C# over fold tokens in one search module
(#192; `backend-rs/crates/archive-server/src/catalogue`) that owns folding, matching, ranking and
paging for every adapter: the member list search and the chat tool
`catalogue_search` return the same matches in the same order (rank, then
title, then id) for the same query — only the chat's page size keeps ten — so
The translated tests exercise PostgreSQL; lexical SQL search and embeddings
remain cut (ADR-0001).

**Video playback copies** (#161). Every video Aufnahme — the concert Aufnahmen
of an Auftritt AND Lied-Aufnahmen — automatically gains a playable MP4 copy:
original resolution up to 1080p, H.264 CRF 20, AAC 192 kbit/s, `+faststart`. The work runs through the
finite-job pattern: `VideoHook` writes the
`video_jobs` row in the finalize save (`IDateiFinalizedHook`) and sends to the
`archive-videos` queue afterwards (quiet: a lost send is rediscovered by the
dispatch sweep), and the finite worker `--transcode-queue` sweeps and drains.
ffmpeg/ffprobe run from `Archive:Video:FfmpegPath`/`FfprobePath` (PATH by
default; the Dockerfile installs the package in its runtime stage — local
hosts without ffmpeg make the worker record the deterministic German
`MissingToolReason` on the Datei instead of a crash loop). The copy is a real
Datei (`… - Abspielkopie.mp4`, kind `aufnahme`, `video/mp4`): members play it
through the usual access tickets (view), while the download stays the original
— never re-encoded, always kept for editors. Already-playable originals up to
1080p end in `notNeeded` without a second object. Failures are terminal with a
German reason on the Datei; status and retry:
`GET /api/dateien/videos?ids=…`, `POST /api/dateien/{id}/video/retry`. The
Auftritt recording's empty playback slot receives the copy in the same save;
an editor's own playback slot entry is never overwritten, and a replaced
original takes its copy with it into the trash (`VideoTrash`).

*Scale/cost estimate for a 2-hour concert (one ~4K30 HEVC original, roughly
40–60 GiB): the 1080p H.264 CRF 20 copy lands around 5–10 GiB (10–20 % of the
original — matches the architecture's 20 % assumption); every clip of an
evening counts its own share. Job compute: 2 vCPU/3 Gi for 1–2 h of encode
≈ EUR 0.15–0.30 per concert clip; the daily sweep adds fractions of a cent.
Ongoing cost is dominated by hot-tier storage of originals plus copies and
blob egress for replay — see the capacity table under cost model. No model
spend, ever.*

**Storage adapter.** `IAssetStorageAdapter` / `BlobAssetStorageAdapter`
(`backend-rs/crates/archive-server/src/storage`, settings `Archive:Assets`, container
`archive-assets`) issue blob-scoped 15-minute read tickets and write tickets,
probe, read headers, promote (server-side copy from a signed source URL),
delete and list. The emulator fetches the copy source from inside its own
container, so the AppHost pins the Azurite blob host port to the container port
(`WithBlobPort(10000)`); integration tests pass `DcpPublisher:RandomizePorts=false`
for the pin to apply. Locally `archive-storage-init` creates the container and a
permissive emulator CORS rule for the direct browser transfers; production uses
user-delegation SAS through the runtime identity and CORS from infrastructure.

**Cold storage for originals (#148).** A preservation original moves to Cold
storage only once members get their playback from a separate, verified copy —
the automatic MP4 Abspielkopie for video material or an Auftritt recording's
validated Abspielfassung. `POST /api/dateien/{id}/storage-tier`
(`{ tier: "hot" | "cold", rowVersion? }`, editor-only through the archive
access module) moves the blob tier synchronously via
`IAssetStorageAdapter.SetAccessTierAsync` and records the editor state on the
Datei row (`StorageTier` `hot`/`cold` with check constraint, `TierChangedAt`,
migration `ColdStorage`); the lists carry `storageTier` in the Datei summary.
Refusals are explicit: without that separate playable copy the endpoint
answers 422 (`ColdNeedsPlaybackMessage`) and the object stays online — an
original that itself plays (the video `notNeeded` case included) is one object
for both roles and is never duplicated for tier separation. A failed provider
call answers 502 with the shared German storage-failure message and leaves the
row honestly `hot`; retrying repeats the idempotent provider call and heals a
save that failed after a successful move. The copy leaving is coordinated
automatically: when the live playback copy of a Cold original is trashed,
replaced, re-attached or removed — and when a Cold original is restored while
its copy is still gone — the original returns to Hot in the same save, both as
editor state (`TierChangedAt`, row version bump) and as the blob's real tier,
so playback never silently streams a Cold object. Only Cold → Hot is
automatic; a refused provider move aborts the editor action unchanged
(the removal is best-effort and logs a warning), so the state never drifts
into a claim the blob contradicts. Members keep playing the Hot copy,
downloads of a Cold original still work through the same read tickets (higher
retrieval charge) and uploads are untouched. In the Austria East reference
rates of `docs/architecture.md`'s illustrative capacity table Cold costs EUR
0.0039 per billing GB and month against Hot's 0.0168, but **every read of a
Cold object is charged** and
each object bills a **90-day minimum retention**: deleting or replacing a Cold
original earlier may incur a remaining-retention charge. That does not block
the seven-day Papierkorb cleanup — a Cold blob deletes like any other, and its
tier state leaves with the row. No new
job, queue or identity exists: the editor initiates every transition, the
runtime identity's account-scoped `Storage Blob Data Contributor` already
covers `SetAccessTier`, apart from the automatic Cold → Hot restores above
there are no automatic tier changes, no backup objects
and no automatic transcoding. Real-Azure verification (tier actually moves,
retrieval behaviour and charges) is a maintainer step; the offline Archive
tier is deliberately not offered.

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
(`backend-rs/crates/archive-server/src/ops/cleanup.rs`, runs outside Development, uses the same image
and the storage identity of the other jobs). It deletes everything under
`revisions/` and the staged `pending/` objects no upload session refers to any
more, and never touches `dateien/` or any other name; `--dry-run` only counts.
Run it once after the release that applied the contract migration, see
`infrastructure/archive/README.md`.

## ARC-022 grounded history chat

Members chat with the archive at `/fragen` („Fragen zum Archiv“). The Rust `POST /api/chat` endpoint streams AG-UI SSE to the unchanged browser client. The offline `Scripted` provider is deterministic; hosted chat uses `Archive__Chat__Provider=AzureOpenAI`, `Archive__Chat__Endpoint` and `Archive__Chat__DeploymentName` with managed identity.

- Threads persist in the existing PostgreSQL (`chat_threads`/`chat_messages`, migration `20260922214454_ArchiveChat`), are owned by the asking member, are bounded to the last 20 messages, and are validated for ownership on every request. The client generates the thread id (`localStorage` key `arc-chat-thread`); server-side history is authoritative — client-provided older messages are ignored as tamper-proof grounding. `GET /api/chat/thread/{id}` restores the owner's history; foreign/missing threads answer an indistinguishable 404.
- Tools are allow-listed and authorized inside themselves: `catalogue_search` (all Lieder, `CatalogueText.Fold` token-AND, ≤ 10 results, 300-char lyrics excerpt; since #142 it also matches the text of the live Noten and reports the match source via `trefferIn`) and `lied_details` (Lied fields, its live Dateien, where it was sung and where it is planned). Document text is data, never instructions; no editing tools, no raw queries, no general-knowledge answers.
- Per-request bounds are app-enforced: question ≤ 2000 chars, at most 5 model calls per run (`MaxToolCalls`; every call offers the tools, and a run whose fifth call still asks for a tool ends in the failure state without a sixth call), 30 s no-token abort per model call, 120 s overall cap, client-disconnect cancellation, one repetition of the run's first call when the provider fails before its first update (the only case with a sixth provider request), German `RUN_ERROR` failure state. Cost is bounded by the shared AI budget below.
- The chat answers 503 „Der Archiv-Chat ist derzeit nicht verfügbar." unless `Archive:Chat:Enabled=true` and `Disabled=false`, so deployments must select and price the provider. Questions about whole Auftritte answer honestly that the chat has no Auftritt tools (a single Lied's history comes from `lied_details`); the synthetic evaluation (`cargo test --locked -p archive-server --test chat_evaluation -- --ignored` in `backend-rs`) verifies the grounding bar — every citation verifiable against the authorized result set, zero unsupported claims, honest unknown behaviour, recorded EUR-per-answer cost.

### ARC-022-3 agent, AI gateway and the hard monthly cap

The Rust chat service owns its tool loop, citation filter, server-side thread history (`chat_threads` / `chat_messages`), and bounded model calls through the shared Gateway. It emits AG-UI SSE for the unchanged, locked browser client; authorization and thread ownership are enforced on the server.

Features use the Rust `ai::Gateway`; it validates the operation, bounds and prices, reserves budget in PostgreSQL, and settles after provider delivery. Scripted or Azure delivery is selected below that gateway. See `backend-rs/crates/archive-server/src/ai` and the [AI parity guide](backend-rs/docs/ai-foundation-parity.md).

### The maintainer side of the AI budget (ARC-043, #150)

The ledger already holds everything; nothing is added to it:

- **View:** `GET /api/maintenance/ai-budget` (editor-only, `no-store`, reads only, available even while maintenance mode is on, like the other diagnostics) reports the current month per feature and model (calls, tokens, EUR = settled cost plus open reservations), the EUR 15 cap and the configured thresholds with each level's notified state. The Verwaltung shows it at `/verwaltung/ki-budget/` (`components/ki-kosten.tsx`, pure read; nothing in the page pays money).
- **Notices:** the daily reading dispatch sweep (`--dispatch-reading`) runs the deterministic monthly review (`backend-rs/crates/archive-server/src/ai`): for each newly reached level — the configured thresholds plus the cap — one warning is logged, exactly once per level per month (the month's row in `ai_budget_months` remembers its level in `NotifiedMicroEur`, migration `20261023215101_AiCostNotices`; a raised cap re-enables). The lines land in the Log Analytics workspace (`log-liedertafel-archive`) for manual inspection; the alert rules that used to forward them were removed on 2026-11-08 (~EUR 0.10 per rule per 8 days, see "Failure monitoring (after ARC-042 removal)" in `infrastructure/archive/README.md`).
- **Thresholds:** `Archive:Ai:NoticeThresholdsEur` (comma separated) is explicit configuration and ships **empty — a disabled state, not a default**; relayed from the Bicep parameter `aiNoticeThresholdsEur` (main.bicepparam) to the web app and the sweep job. Values are maintainer-owned (suggestion pending decision: 5 and 12). A read-only summary with the monthly path lives in `infrastructure/archive/README.md`, "Cost overview (ARC-043)".
- **Checks:** Run `cargo test --locked -p archive-server --test ai_notices` in `backend-rs`; real PostgreSQL and browser prerequisites are in [Focused verification](#focused-verification).

- **Cap:** EUR 15 per calendar month in Europe/Vienna, for all AI features together. Before each model call the middleware reserves the call's worst case (estimated input plus the output bound at the model's price). A reservation that would pass the cap is refused and the provider is not called. Guarantee: settled cost + open reservations + the worst case of a newly admitted call never exceeds the cap. After the call the reservation is replaced by the usage the provider reported; a call that ends without a usage report (failure, timeout, client disconnect, abandoned stream) costs its full reservation.
- **Ledger:** `ai_usage_entries` (renamed from `chat_usage_entries` by migration `20261006111010_AiUsageLedger`): one row per operation and model with feature, model, summed tokens of all its calls, settled cost and open reservation in micro-EUR. The month's spend is the sum over its rows; nothing is cached in memory. `ai_budget_months` holds one row per month as a lock: a reservation runs in one transaction that takes `SELECT … FOR UPDATE` on it, so reservations of a month queue on every replica (5 s lock timeout, then refused). A settlement is one relative `UPDATE` of its own row, retried three times, then logged at error and counted. The ledger store in `backend-rs/crates/archive-server/src/ai` uses PostgreSQL SQL.
- **Fail closed:** no price or a price of 0 for the model, a `ModelId` other than the client's own model, non-text input without an estimate, a ledger that cannot be reached, or a missing `AiOperation` all stop the call before the provider.
- **Member state at the cap:** the run ends with AG-UI `RUN_ERROR`, `code: "monatsbudget_erreicht"` and a message starting „Monatsbudget erreicht."; the chat page shows it without a retry button. Nothing else changes behaviour. Because a call reserves its worst case, the chat stops when the remainder no longer covers one worst-case call, slightly before EUR 15.00 is settled. `Archive:Chat:Disabled=true` remains the manual kill switch (503).
- **Configuration:** `Archive__Ai__MonthlyCapEur` defaults to 15, `Archive__Ai__MaxOutputTokensPerCall` to 6000 and `Archive__Ai__MaxInputTokensPerCall` to 200000. The Rust defaults in `ai/options.rs` price `gpt-6-luna` at EUR 0.1056 input / 0.528 output per million tokens, below the long-context boundary; only Development prices `scripted`. For an overridden deployment set both `Archive__Ai__Models__<deployment>__InputPricePerMillionEur` and `Archive__Ai__Models__<deployment>__OutputPricePerMillionEur` to positive decimal EUR rates. Check rates against the selected deployment and region. A missing price refuses the request before provider I/O.
- **Telemetry:** The Rust gateway emits the existing `Liedertafel.Archive.Ai` spans and token, cost, cap, refusal and settlement metrics. Neither provider bodies nor questions, answers, or document content belong in telemetry.
- **Checks:** Run `cargo test --locked -p archive-server --test ai_ledger -- --ignored` in `backend-rs`; real PostgreSQL and browser prerequisites are in [Focused verification](#focused-verification).
- **Historical ledger migration:** the October 2026 EF rename is part of the frozen head, not rerun at Rust adoption. Current deployments must configure both price entries for an overridden model deployment or the Gateway refuses the call.
- **Live validation:** the three live evaluations are opt-in and require separate spending authorization; CI checks only the explicit skip when configuration is absent. For a future model change, review the new rates and configuration before switching the deployment.

### ARC-022-4 AG-UI client and A2UI cards

The chat page reads the run with `@ag-ui/client` 1.0.2 (`HttpAgent` in `frontend/lib/chat.ts`, posting straight to `/api/chat` through the same CSRF-aware fetch as every other mutation) and shows what the agent displays with `@a2ui/react` 0.13.0 / `@a2ui/web_core` 0.13.0 (protocol v0.9, `zod` 3.25.76; all pinned, no CopilotKit, no Node runtime, still a static export). The A2UI library is loaded on demand (`next/dynamic`, no server rendering) only when an answer carries surfaces. The basic catalog is not registered: the catalog `urn:liedertafel:archiv:a2ui:katalog:v1` has exactly `LiedKarte`, `AuftrittKarte`, `ProgrammListe` and `Quellen` (`frontend/lib/a2ui.ts` schemas, `components/a2ui-katalog.tsx` implementations, cards styled in `app/fragen/chat.css`).

- **Wire:** Rust omits unused/null AG-UI properties for the strict client, including `usage[].model` and `parentRunId`. `RUN_FINISHED`, `RUN_ERROR` with its `code`, `archive.citations`, and budget handling preserve the browser contract.
- **The tool:** the agent calls `zeige_oberflaeche(komponenten)`; each entry is a component name and exactly the one record id it is built from (`liedId`, `liedIds`, `auftrittId`). `backend-rs/crates/archive-server/src/chat` validates every entry against the catalog (known component, only its own id, valid GUIDs, at most 12 entries looked at, at most 4 surfaces per answer, one card per record), resolves the ids to what a member may see (Lieder and published Auftritte only) and builds the A2UI messages itself: `createSurface` plus one `updateComponents` with a single `root` component per surface. Anything else is dropped before streaming; the model only learns how many entries were not shown, never which or why. The model supplies no text and no URL, and the server sends none: the browser derives links (`/lied/?id=…`, `/auftritt/?id=…`) from the ids. The tool call itself is removed from the AG-UI stream; the validated messages travel in one `CUSTOM` event `archive.a2ui` (before the `archive.citations` chips, which stay the server-built event).
- **Member chat offers:** `LiedKarte` (title, composer, lyricist, first lines), `AuftrittKarte` (title, kind, date, venue), `ProgrammListe` (a saved Auftritt's Programm, Lieder in their order with notes; up to 60 entries) and `Quellen` (up to 10 Lieder). #124 added the `ProgrammListe` resolver: the model names only the `auftrittId`, the server builds the entries from that Auftritt's Programm.
- **Storage and reload:** the validated messages are stored with the assistant answer in `chat_messages."A2uiJson"` (`jsonb`, migration `20261011160524_ChatMessageA2ui`; a card-only answer stores empty content and is not fed back to the model as history). `GET /api/chat/thread/{id}` never returns the stored JSON as it is: the Rust resolver rebuilds each surface from the record ids it names, so a card for a since-unpublished Auftritt or a deleted Lied is gone, tampered or unreadable stored content yields nothing, and data is fresh. The message then has an `a2ui` array (or none).
- **Browser safety:** `leseA2uiOberflaechen` keeps only surfaces of exactly one `createSurface` (this catalog) and one `root` update with a catalog component and exactly its allowed properties (strict schemas: no `href`, `url`, data model or extra fields); the A2UI `MessageProcessor` then runs with `STRICT_VALIDATION` as a second check, and a surface it refuses is removed. Malformed or out-of-catalog messages show no card and no error.
- **Clicks:** cards link to archive pages that load their own data over REST; nothing goes back through the chat.
- **Checks:** Run `cargo test --locked -p archive-server --test chat_a2ui_api -- --ignored` in `backend-rs`; real PostgreSQL and browser prerequisites are in [Focused verification](#focused-verification).

## ARC-024 historical events

The choir's "Auftritte" record lives in the `events` table (`backend-rs/crates/archive-server/src/events/`,
additive migration `20260924100654_HistoricalEvents`). `ChoirEvent` keeps the
date honest as components: `DateYear` (1800–2100), `DateMonth` (1–12),
`DateDay` (must exist in its month) and an independent `DateApproximate`
flag; the derived `datePrecision` (`day|month|year|unknown`) follows from the
set parts and only (year), (year, month), (year, month, day) and none are
valid — nothing invents a calendar date. The event-date formatter renders the
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

An Auftritt has **one editable Programm** (ADR-0001, `backend-rs/crates/archive-server/src/events`): the
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
- `GET /api/lieder/{liedId}/historie[?page=]` is the
  Lied history ("wann gesungen"), derived on every read from the Programme plus
  the Lied-Aufnahmen naming an Auftritt. One row is one Auftritt (newest first by
  the honest date, `EventDate.CompareNewestFirst`, fixed pages of 20): `entries`
  (how often the Lied is in the Programm), `inProgramme`, `liedAufnahmen`,
  `recordings` (Zeitmarken of that Lied, with `timestampState`), the honest date
  and `dateUncertain` (a year-only or approximate date). `counts` has `events`,
  `inProgramme`, `uncertainDates`. An unknown Lied is 404.
- Chat: `lied_details` reads the same data; `gesungen` lists past Auftritte with
  the Lied in the Programm or an Aufnahme, `geplant` upcoming ones. #124 made the
  history answer full: every entry carries `auftrittId` (the model can show the
  `AuftrittKarte`, or a `ProgrammListe` when nothing is sung yet), the file names
  of Lied-Aufnahmen naming the Auftritt and the member-visible Zeitmarken
  (`aufnahme` label, `start`, `status` — a mark under review shows no start).
  Approximate dates arrive as the record keeps them ("um 1950") and the system
  prompt forbids inventing more precision; the scripted client states the date
  and names the Aufnahme with the Zeitmarke in its answer.
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
- **Papierkorb:** `GET /api/papierkorb` (editor) lists all three arts in one
  payload — `lieder` (title), `auftritte` (title, kind) and `dateien` with owner
  (Lied or Auftritt), who deleted them, when and whether they were replaced; the
  page is `/verwaltung/papierkorb/`, sectioned „Lieder“, „Auftritte“ and
  „Dateien“, with restore for the whole Redaktion and Administrators confirming
  the immediate removal of every art. The collection budget still counts trashed
  bytes. Restoring (#146) clears the trash columns, but only
  within the retention (`Archive:Papierkorb:RetentionDays`, default seven days):
  past it the restore endpoints answer 409 (the daily cleanup removes the entry
  for good), and a Datei whose owning Lied or Auftritt is still in the trash
  answers 409 until that owner is restored — individually trashed Dateien stay
  trashed when their owner is restored. Administrators' immediate removal is
  untouched by both guards.
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

**Recordings** (`backend-rs/crates/archive-server/src/recordings/`, table `recordings`, ARC-030). A recording
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
  the format sniffer); otherwise 422 and the unusable Datei is trashed while
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

- **Tables** (Rust catalogue; frozen baseline `AddLiederAndDateien`): `lieder`
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

## Auftritt from a programme scan (#131)

"Neuer Auftritt" opens the same kind of drop zone as "Neues Lied"; "ohne Datei anlegen"
stays as the small secondary link to the manual form. `POST /api/auftritt-lesungen` (editor,
antiforgery, multipart `entwurfId` GUID, `anfrageId` integer >= 1, `datei` a PDF or a
JPEG/PNG/WebP photo) answers `{ entwurfId, anfrageId, felder: [{ feld, wert, sicher }],
programm: [{ text, sicher, lied: { id, titel, sicher } | null, kandidaten: [{ id, titel }] }] }`.
`ProgrammReader` takes the embedded text of a PDF, or transcribes a scan (first four pages)
or a photo (turned upright, bounded like a rendered page), then asks the programme agent
(`archive-programm-fields`, features `programm-transcription` / `programm-fields`, both
through the Rust `ai::Gateway`) for title, `kind`, `jahr`/`monat`/`tag`, `unsicher` (the approximate-date
flag, "um 1950") and `venue` plus the ordered entries. The reading job's checks apply: a value
or entry needs a quote that stands in the text, a date must be plausible (a month needs a
year, a day needs a month and exists in it), `kind` must be a known Auftritt kind; `sicher:
false` marks an unsure value. Each entry is matched against the Lieder by `ProgrammMatcher`
(no model): only an exact normalised title (case, umlauts as ae/oe/ue or plain, punctuation
ignored) that names exactly one Lied is pre-selected as sure; a single clearly closest near
miss (similarity >= 0.85 and 0.1 ahead) is pre-selected but `sicher: false`; ties and weak
similarities (>= 0.6) are only `kandidaten`. Nothing is written: no Auftritt, Programm or Lied
exists until "Speichern". Errors as for the Lied draft reading (409 reading off, 429 budget,
422 not readable, 502 failure; the form stays manual). The form takes suggestions only into
untouched, empty fields (reducer `frontend/lib/auftritt-entwurf/reducer.ts`), every programme
entry needs a Lied before "Speichern" (pick a candidate, search, or "als neues Lied anlegen"
with the title only), and "Speichern" creates the Auftritt, saves the Programm in scan order,
uploads the scan as a document of the Auftritt and a dropped video or audio file as the
Auftritt's Aufnahme (`createAufnahme`, upload, attach as `original`; the playable copy and
Zeitmarken stay with #161/#163). The Rust `programm_reading_evaluation` suite
runs the scripted set in CI. The separate live evaluation reads
`ARCHIVE_PROGRAMM_EVAL_DIR` only with explicitly authorized live configuration.

Auftritt drafts in "Angefangen" follow the Lied drafts, in one table `auftritt_entwuerfe`
(migration `AuftrittEntwuerfe`): `GET /api/auftritt-entwuerfe`,
`GET|PUT|DELETE /api/auftritt-entwuerfe/{id}` (editor, antiforgery on changes, own drafts
only, at most 25 per editor). `PUT { entwurfId, titel, dateien, formular }` where `formular` is
a versioned JSON object (`version: 1`, at most 100 000 characters) the browser owns: values,
edited-field marks, suggestion marks, the programme rows with their Lied choices and the
Dateien without bytes, never where a value came from. The reading itself is stateless, so a
reading in flight when the tab closes is lost and is repeated ("Datei lesen") once the file
is selected again. Once the Auftritt is created the draft remembers its id, so a retry never
creates it twice, and it keeps only the Dateien that are not uploaded yet; it ends with the
last upload.

## Failure monitoring after ARC-042 (2026-11-08)

The Azure alert rules of ARC-042 (`ag-archive-maintainers`, six scheduled-query
rules over `ContainerAppConsoleLogs_CL`, names `al-archive-*`) were removed:
each rule billed ~EUR 0.10 per 8 days (~EUR 2.30/month together), an order of
magnitude above the rest of the group's bill. No deployed alert rule matches
the Rust job runner's or feature workers' failure logs. Release and migration
failures mail through GitHub's own
Actions notifications — keep both maintainers watching the repo. For manual
inspection the lines stay queryable in `log-liedertafel-archive`
(`ContainerAppConsoleLogs_CL`), and the operator paths of the former runbook
(jobs at `az containerapp job execution list`, Dateien in the Wartung tools,
stuck windows via `curl https://archiv.liedertafel-mining.at/api/maintenance`
and [Controlled database releases](../../infrastructure/archive/README.md#controlled-database-releases-arc-012))
still apply. The removal story and the re-enable path live in
`infrastructure/archive/README.md`, "Failure monitoring (after ARC-042 removal)".

## Runtime grants during `--migrate`

The explicit migration repairs runtime access when connected as
`archive_migrator`. The embedded `backend-rs/crates/archive-db/runtime-grants.sql`
matches the canonical `infrastructure/neon/runtime-grants.sql`; the
`migrate_process` test checks byte identity and the disposable PostgreSQL gate
checks real grants, including denial of both history tables. No extra credential
or `psql` is needed during releases. The manual SQL is only a recovery fallback
and must run as the migrator while the app is paused.

## Quellen-Inventur (#98)

`archive-server --inventur <verzeichnis>` reads only local files. The Rust
`inventur` module reports kinds, sizes, PDFium pages and text availability,
duplicate-name candidates and the analytical scan cost (no model calls). See
[Quellen-Inventur](docs/drive-inventur.md) for the maintainer workflow and
`tests/archive/fixtures/drive-inventur/` for the sanitized corpus.

## Bulk folder import (#143)

The editor uploads a whole folder (or a Drive export as unpacked files) through
the same upload-first path as single Lieder. The browser-side importer
(`frontend/components/sammel-import.tsx`, card "Ordner importieren" on the
Liederkatalog) groups the chosen folder's files with
`frontend/lib/sammel-import.ts` following the #98 mapping rules: one subfolder
becomes one suggested Lied, empty folders (and view fixtures like `.gitkeep`,
`.DS_Store`) import nothing, numbered material subfolders (`1. Noten`,
`2. Media`) classify instead of splitting, a shortcut (`Verknüpfung - X.url`,
`.webloc`) whose target folder lies in the import counts once, an unresolvable
shortcut is reported, same-named duplicates (`… B1 (1).pdf`) are both kept —
nothing deduplicates by filename, and `.xls`/`.xlsx`/`.capx` are listed as
"separat prüfen" and never auto-imported.

Suggestions use the existing seam untouched: every suggested Lied is a normal
Lied-Entwurf (#178) that the editor opens from the list, whose PDFs go through
the draft reading (#177/#159) and whose save creates the Lied and uploads its
Dateien with their own bytes — matches with existing Lieder are offered as
"hinzufügen" by the usual title search. Resume safety (acceptance: no
duplicate Lieder or Dateien) comes from content-addressed draft ids
(`gruppiereImport` SHA-256 → UUID): re-dropping the same folder re-derives the
same entwurf ids, the backend list decides "Fortsetzen" vs a fresh draft, and
after a save the id is gone with the finished draft. There is no Drive
connector: an export is files; nothing fetches or links to Drive.

Tests: the mapping rules stay truthful in
`frontend/lib/sammel-import.test.ts` and the flows in
`frontend/tests/sammel-import.spec.ts` (mocked API, folder picker fed by
Playwright's directory input).

## Focused verification

From `src/archive/backend-rs`, `cargo test --workspace --locked` runs the
ordinary suites. Use `cargo test --locked -p archive-server --test <name>` to
focus. Tests marked ignored need disposable PostgreSQL, private Azurite,
PDFium, media tools, or the local candidate image. The release check runs these
explicitly with prerequisites via `tests/image/check-translated.sh` (from the
repo root), plus browser, detector, dependency and image/AppHost gates. It
reports the three live AI evaluations as skipped, never as model passes. Live
runs require separate authorization, configuration and (for scans) private
input; see the individual parity guides. The frozen C# oracle is in git at
`8c7a077988d3ca6a0a72788fb4cc13da9e3ae4a6`, not a second build target.

From `src/archive/frontend`:

```bash
pnpm install --frozen-lockfile
pnpm run check
pnpm run test:unit
pnpm run build
pnpm exec playwright install chromium
ARCHIVE_BASE_URL=http://localhost:<frontend-port> pnpm run test:browser
```

Run `pnpm run test:browser -- --grep "German deep link"` to focus. Stop any
dev AppHost before running image AppHost tests; they share the frontend worktree.
The seven cross-stack cases all use the exact production Dockerfile image. See
[the image gate](backend-rs/docs/image-apphost-parity.md) for build and smoke
commands or a focused `dotnet test tests/archive/apphost-rust` invocation. For
Podman builds add `--ignorefile src/archive/Dockerfile.dockerignore`. The
container serves on port 8080 as UID 10001 with only private scratch writable.

`dotnet build src/archive/Archive.slnx` builds the retained Aspire orchestration
and integration tests. `dotnet build liedertafel.slnx` checks repository SDK
compatibility; analytics checks remain `dotnet run --project tests/dashboard-api`.
