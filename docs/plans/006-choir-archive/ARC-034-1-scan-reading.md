---
id: ARC-034-1
status: in_progress
phase: core
kind: slice
depends_on: ["ARC-034", "ARC-021-1", "ARC-013-1"]
touches: ["extraction", "catalogue", "azure-jobs", "ai-evaluation"]
external_inputs: ["ai-runtime-credentials"]
---

# ARC-034-1 — Read scanned scores and prefill catalogue fields

**Depends on:** [ARC-034](ARC-034-pdf-extraction.md),
[ARC-021-1](ARC-021-1-luna-switch-and-budget-cap.md),
[ARC-013-1](ARC-013-1-field-provenance-and-proposals.md).

## Outcome

An editor uploads a scanned score that has no embedded text and finds title,
creators, voice, key and lyrics already filled in, marked "KI" and revertible.

## Acceptance criteria

- [ ] Add a second finite job, "AI reading", with its own identity (the only
  job identity with the OpenAI role), queue, size and timeout. The extraction
  job stays model-free and hands work over; `NoText` is no longer terminal.
  *Done in code (own command `--read-queue`, queue `archive-reading`, 32 MiB,
  8 minutes, hand-off from the extraction job, local AppHost resource); the
  Azure job and its identity are not created, see "For the maintainer".*
- [x] Step one, for `NoText` revisions: render pages in the job with a
  PDFium-based package that ships its native library, and have a named agent
  transcribe them. Up to 20 pages per document in requests of up to 10 images
  at a bounded resolution; longer documents are marked "teilweise gelesen" and
  an editor can request the rest. Store the text as the revision's text.
- [x] Step two, for any revision with text: `ScoreTextAnalyzer` runs first and
  its facts are written with source `regex` into empty, unlocked fields. A
  second named agent derives only the fields still empty.
- [x] Each derived field carries `sicher` or `unsicher` plus a verbatim quote.
  Code verifies the quote appears in the text. Only `sicher` with a verified
  quote is applied and badged; everything else becomes a proposal. A new song
  identity is never created automatically.
- [x] Writes go through the ARC-013-1 shared write service. The job has no
  other write access. Text in the scan is data, never instructions.
- [x] The job reads drafts (unpublished uploads); its output stays invisible to
  members until the record is published.
- [ ] At the budget cap the job row becomes `WaitingForBudget`, the queue
  message is acknowledged, and the dispatch sweep re-enqueues it when the month
  changes or the cap is raised. The editor sees that status; manual entry is
  never blocked. *Done in code and tested; the scheduled `--dispatch-reading`
  run that wakes parked rows is not deployed, see "For the maintainer".*
- [ ] A fixed regression set of real scans with expected fields, run on demand
  with pass rate and cost per document recorded. CI uses a scripted stand-in
  and never calls the provider. *The harness and the scripted CI run exist; the
  real scans and the recorded live result are the maintainer's, see below.*

## Verification

Upload a text PDF, a clean scan and a poor handwritten scan. Confirm the text
PDF makes no model call for fields the regex found, the scan is prefilled with
badges, uncertain fields land in "Vorschläge", and a hand-edited field survives
a retry. Run the evaluation set and record results.

## Handoff and parallel work

ARC-035 searches the text this slice stores. ARC-037 imports feed scans into
this job under the budget cap. This reverses the PRD's earlier OCR exclusion;
optical music recognition stays out. Decisions:
[architecture §14](architecture.md#14-ai-assistance).

## Implementation and verification record (2026-10-12)

The journey, end to end (all of it runs without credentials, see "Local run"):
the editor uploads a score PDF; the extraction job finishes and, for every
score, leaves a `reading_jobs` row in the same save and a message on the
`archive-reading` queue; the AI reading job (`--read-queue`) transcribes a
scan's pages into the revision's text, then fills the empty, unlocked
catalogue fields (regex first, then the field agent); the editor's extraction
line shows "KI liest die Seiten … (2 von 3 Seiten)", the counts of applied
fields and proposals, "Vorschläge ansehen", "Rest lesen" or "Erneut lesen";
applied fields show "KI" with "Zurückholen" (the voice in the material list,
the rest in the song and Fassung forms). Details of the structure are in
`src/archive/README.md`, "ARC-034-1 reading scanned scores".

Decisions and deviations (read these first):

- **Title is not prefilled.** The ticket's outcome lists the title, but a song
  cannot exist without one and a song identity is never created here, so the
  field is never empty. The agent is not asked for it. Offering a differing
  title as an alternate title or proposal can be added with ARC-036/037, which
  create songs from files.
- **The text goes into the extraction row** (`extraction_jobs.Text`,
  new column `TextSource`: 0 embedded, 1 reading) instead of a second text
  table, so every reader of "the revision's text" (the analyzer, the chat
  tools, ARC-035's search) sees it unchanged. A `NoText` row becomes
  `Completed` as soon as the first request returned text; the reading row
  carries the pages (`PagesRead`/`PagesTotal`/`PageLimit`) and "teilweise gelesen".
- **Every score is handed over, not only `NoText`.** The extraction job inserts
  the reading row for any finished score (text or not), because step two is for
  any revision with text. Event documents are not read (a programme scan is
  ARC-025-1). Scans extracted before this slice have no row: the editor starts
  the reading per file ("Seiten von der KI lesen" / "Angaben von der KI
  ergänzen"). Nothing is backfilled automatically, to keep the cost a decision.
- **Only empty, unlocked fields, only for the current revision.** The ticket
  says "empty" for regex facts and "still empty" for the agent; the code applies
  that to both and also skips a field a human cleared (locked while empty). An
  older revision keeps its text but never writes fields. A retry never creates a
  second proposal for the same target and field.
- **`sicher` plus a verified quote or a proposal, regardless of the configured
  threshold.** `AutomatedFieldWriter` gained `FieldWriteRequest.ProposalOnly`;
  reading sets it for everything that is not certain and quoted. A name must
  also stand in its quote.
- **Output bound and the long-context guard.** `MaxOutputTokensPerCall` went from
  2,000 to 6,000 (a ten-page transcription needs more); the chat now states its
  own 2,000 on its operation, so its reservations are unchanged. New
  `Archive:Ai:MaxInputTokensPerCall` (200,000): a call whose estimated input
  exceeds it throws `AiInputTooLargeException` before the budget, so nothing can
  reach Luna's long-context tier (about 272,000) under-priced. Ten images are
  estimated at 25,000 tokens (`EstimatedTokensPerImage` 2,500); a request costs
  about 0.003 EUR.
- **The asset PATCH moved into the shared write service** (ARC-013-1's open
  note): a human edit of the voice label or description is now recorded as human
  provenance and locks the field, and the editor's song detail lists asset
  provenance. This also bumps the asset's row version on every PATCH.
- **The queue drain loop is shared.** `QueueDrain` (extraction and reading
  pumps) was extracted from `ExtractionPump`; its behaviour and tests are
  unchanged. The reading queue reuses the message type and the envelope
  (`type: "reading"`).
- **PDF rendering:** `PDFtoImage` 5.4.0 (MIT; PDFium through `bblanchon.PDFium.*`
  and Skia through `SkiaSharp.NativeAssets.Linux.NoDependencies`, both ship their
  native libraries, no apt packages or fonts needed). It is the one new package.

Tests were written before the code for the input guard, the renderer and the
worker/prefill/endpoint behaviour; mutation checks (quote check always true,
`ProposalOnly` ignored, current-revision guard removed, empty check removed, budget
exception treated as transient) applied together made 8 of the 22 worker tests
fail (the ones that cover each rule) and were reverted.

Scripted model: `Reading/ScriptedScanReader.cs` backs `ScriptedChatClient` for
reading requests, so Development, CI and the AppHost run the whole job offline.
It cannot see images; it returns a fixed German sample page.

### Verification (2026-10-12)

All on the maintainer's Raspberry Pi, local PostgreSQL 17.6 in Docker, no provider calls:

- `dotnet build src/archive/Archive.slnx`: 0 warnings, 0 errors.
- `ARCHIVE_TEST_POSTGRES="Host=localhost;Port=55432;Username=postgres;Password=pw;Database=postgres" dotnet test tests/archive/backend`: **684 passed, 0 failed, 0 skipped** (includes the reading worker/prefill/API tests, `PdfPageRendererTests`, `ScanReadingEvaluation` with the scripted client, the `PostgresReading` live cases and the existing AI-ledger live cases).
- `ConnectionStrings__archive-migrations=... dotnet ef migrations has-pending-model-changes --project src/archive/backend --no-build`: "No changes have been made to the model since the last migration."
- `dotnet test tests/archive/apphost` (fresh containers, includes the extraction round trip extended with the reading step on the scripted model): **6 passed, 0 failed** (15 min).
- `corepack pnpm run check` (Biome, `next typegen`, `tsc`) and `corepack pnpm run build` in `src/archive/frontend`: clean.
- `ARCHIVE_BASE_URL=http://localhost:3112 corepack pnpm exec playwright test tests/extraktion.spec.ts tests/vorschlaege.spec.ts tests/noten.spec.ts tests/lieder.spec.ts tests/materialien.spec.ts tests/auftritt-dokumente.spec.ts tests/noten-verlauf.spec.ts --workers=1` (static export served from `out/`): **150 passed** (desktop and mobile).

Not run: the Docker image build with the native PDFium/Skia libraries (the disk of the
build machine was full; only `dotnet publish` was attempted and interrupted), the
live evaluation on real scans, anything against Azure.

### For the maintainer

1. **Azure job and identity (not done: `infrastructure/**` was off limits).** Add
   a Container Apps Job `ja-archive-read` running the archive image with
   `--read-queue`, triggered by an `azure-queue` scale rule on `archive-reading`
   (visible-only, like `ja-archive-extract`), with its own user-assigned identity:
   Storage Queue Data Contributor and Storage Blob Data Reader on the assets
   account, plus the Azure OpenAI user role on `aoai-liedertafel-archive` — the
   only job identity that holds it. Settings: `Archive__Chat__Provider=AzureOpenAI`,
   `Archive__Chat__Endpoint`, `Archive__Chat__DeploymentName=gpt-6-luna`,
   `Archive__Reading__QueueServiceUri` (or the existing extraction one, it falls
   back), the database connection secret, `Archive__MaintenanceMode`,
   `AZURE_CLIENT_ID`; 8-minute message budget means a replica timeout of at least
   30 minutes, memory 2 Gi (the PDF is read into memory, 32 MiB cap). The release
   workflow must pause and pin this job like the extraction job. The runtime
   identity already holds Queue Data Message Sender for the editor's actions.
2. **Wake-up of parked work.** Rows in `WaitingForBudget` have no queue message.
   Schedule `--dispatch-reading` (a cron Container Apps Job, daily is enough) or
   rely on the next queue-triggered `--read-queue` run, which sweeps first. Without
   one of them a row parked at month end waits until the next reading message.
3. **Live evaluation on real scans.** Put 10 to 20 representative PDFs (a clean
   scan, a poor handwritten scan, text PDFs, a long scan) into a private folder
   with `expected.json`:
   `{"documents":[{"file":"a.pdf","expected":{"composer":"…","lyricist":"…","arranger":"…","musical_key":"G-Dur","voice_label":"Tenor 1","lyrics":"first sung line"}}]}`
   (leave out what the document does not say), then
   `ARCHIVE_SCAN_EVAL_DIR=… ARCHIVE_CHAT_ENDPOINT="https://aoai-liedertafel-archive.openai.azure.com/" ARCHIVE_CHAT_DEPLOYMENT_NAME="gpt-6-luna" ARCHIVE_SCAN_EVAL_REPORT=/tmp/scan-eval.json dotnet test tests/archive/backend --filter "Category=ScanReadingEvaluationLive" --logger "console;verbosity=detailed"`.
   It fails only if a wrong value was applied (the one hard rule); record pass
   rate and cost per document in this ticket. Cost is a few cents per document.
   Also unproven live, because no provider was called: (a) structured output
   (`response_format` json schema) on Luna with Chat Completions
   `api-version=2024-10-21`, (b) image input through `DataContent` on Luna, (c)
   quota (TPM) for ten-image requests against the 30k TPM deployment — if (c)
   throttles, lower `Archive:Reading:ImagesPerCall`.
4. **Hosted checks:** the hosted database applied `ScanReading` (use the release
   `--migrate`), then upload a scanned PDF as an editor, wait for the extraction
   line to show "Kein Text gefunden", confirm the reading starts, the text and
   "KI" badges appear, a hand-edited field survives "Erneut lesen" and an
   uncertain field lands in "Vorschläge". Then set this ticket to `done`.
5. Not verified here: the native PDFium/Skia libraries inside the real release
   image on linux-x64 (see the verification record for what was checked).

### Handoff

ARC-035 searches `extraction_jobs.Text` as before and may now find AI-read text
(`TextSource = 1`); ARC-037 imports can create reading rows with
`ReadingService.CreateForRevision` and send them with `ReadingService.TryEnqueueAsync`
under the same budget cap; ARC-025-1 reuses `ScanReadingModel`-style named agents,
`PdfiumPageRenderer`, `AiOperation.NonTextInputTokens` and the input guard.

