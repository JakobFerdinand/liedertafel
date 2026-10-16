# AGENTS.md

This repository is an Astro site for Liedertafel Mining 1906.
Use this guide when editing or extending the codebase.

## Repository Layout
- src/website: self-contained Astro site with pages in `src/pages`, reusable sections in `src/components`, global shell/CSS in `src/layouts/Layout.astro`, assets in `src/assets`, content collections under `src/content`, and static files in `public/`.
- src/website-api: Azure Functions managed API (C# .NET isolated); serves the `/api/pageview` endpoint and writes page views to Azure Table Storage.
- src/dashboard: internal, auth-gated Astro dashboard (Svelte islands + Layerchart) for the `liedertafel-dashboard` Static Web App; overview at `/`, session inspector at `/sessions`. Shared API/date helpers in `src/lib`, UI grouped under `src/components/{filters,overview,breakdowns,sessions}`, styling in `src/styles/global.css`.
- src/dashboard-api: Azure Functions managed API (C# .NET isolated); serves read-only `/api/pageviews/stats`, `/api/pageviews/sessions`, and `/api/pageviews/sessions/{sessionRef}` endpoints. All require bounded Vienna date ranges; session references are opaque, range-bound handles.
- src/pageview-storage: shared C# library (net9.0) referenced by both APIs; owns the stored pageview shape (`PageViewEntity`), table name and `Pv|yyyy-MM-dd` UTC partition scheme (`PageViewTable`) and navigation vocabulary (`NavigationTypes`). Change stored-data rules here, never in an API. Its path is in both API workflows' path filters.
- src/archive: .NET 10 / Aspire choir archive. `apphost/` orchestrates local services, `service-defaults/` owns OTLP and finite-worker defaults, `backend/` serves the API/static export and explicit EF migrations, `frontend/` is Next.js App Router + TypeScript + Tailwind + Biome. Read `src/archive/README.md` for contracts and startup.
- tests/archive: xUnit backend/OTLP checks under `backend/` and clean-stack Aspire integration checks under `apphost/`. Browser checks live in `src/archive/frontend/tests`.
- infrastructure: Azure Bicep templates for the RG-Liedertafel estate.
- infrastructure/neon: archive Neon CLI/API setup runbook and SQL role/grant
  bootstrap. Follow its README for private credential handling; run
  `bootstrap-roles.sql` once as admin. `runtime-grants.sql` runs automatically
  inside the release's `--migrate` as `archive_migrator` (embedded copy at
  `src/archive/backend/Data/runtime-grants.sql`, kept identical by a test);
  run it by hand only as a fallback. OpenTofu was trialled but is not adopted.
- Plans and tickets: GitHub Issues (see `## Agent skills`); the choir archive is parent issue #96 with sub-issues ordered by milestone (Vereinfachung, Bestand, Betrieb). `src/archive/docs/architecture.md` is the archive architecture (§14 governs AI assistance).
- docs/agents: issue tracker, triage label and domain doc conventions for agent skills.
- liedertafel.slnx: .NET solution that opens all API projects together; global.json pins the .NET SDK.

## Commands (Build / Lint / Test)
- Website deps: `cd src/website && pnpm install`
- Dev server: `cd src/website && pnpm run dev`
- Build: `cd src/website && pnpm run build`
- Preview build: `cd src/website && pnpm run preview`
- Website API (local): `cd src/website-api && dotnet run`
- Dashboard deps: `cd src/dashboard && pnpm install`
- Dashboard dev server: `cd src/dashboard && pnpm run dev`
- Dashboard check (Astro and Svelte): `cd src/dashboard && pnpm run check`
- Dashboard build: `cd src/dashboard && pnpm run build`
- Dashboard API regression checks (dependency-free console harness): `dotnet run --project tests/dashboard-api`
- Add `-- --azurite` to also verify real Table Storage queries against local Azurite.
- API fixture export for browser smoke checks: append `-- --fixtures /tmp/liedertafel-insights-validation` (see `tests/dashboard-api/README.md`).
- Dashboard API (local): `cd src/dashboard-api && dotnet run` (with `StorageConnection` in `local.settings.json`)
- Archive local stack: `dotnet run --project src/archive/apphost` (Docker required; Podman: prefix `ASPIRE_CONTAINER_RUNTIME=podman`). Start `archive-migrate` explicitly in the dashboard for schema setup.
- Archive build: `dotnet build src/archive/Archive.slnx`
- Archive backend tests: `dotnet test tests/archive/backend`
- Archive focused xUnit test: `dotnet test tests/archive/backend --filter FullyQualifiedName~TelemetryTests`
- Archive live chat evaluation (keyless via az login; skips when unconfigured): `dotnet test tests/archive/backend --filter "Category=ChatEvaluationLive" --logger "console;verbosity=detailed"` with `ARCHIVE_CHAT_ENDPOINT`, `ARCHIVE_CHAT_DEPLOYMENT_NAME` and optional `ARCHIVE_CHAT_MODEL_VERSION` set; a deployment without a price under `Archive:Ai:Models` also needs `ARCHIVE_CHAT_INPUT_PRICE_EUR` and `ARCHIVE_CHAT_OUTPUT_PRICE_EUR` (EUR per million tokens), otherwise every call is refused
- Archive AI ledger on real PostgreSQL (the production SQL; the PostgreSQL-only cases skip when unconfigured): `dotnet test tests/archive/backend --filter "FullyQualifiedName~AiBudgetLedgerTests"` with `ARCHIVE_TEST_POSTGRES` set to a connection string for a throwaway server whose user may create databases (each test creates, migrates and drops its own)
- Archive scan reading (ARC-034-1) evaluation: `dotnet test tests/archive/backend --filter "Category=ScanReadingEvaluation"` runs generated scans through the whole reading job with the scripted client (no provider). `--filter "Category=ScanReadingEvaluationLive"` reads the maintainer's private scans (`ARCHIVE_SCAN_EVAL_DIR` with the PDFs and an `expected.json`, optional `ARCHIVE_SCAN_EVAL_REPORT`) with `ARCHIVE_CHAT_ENDPOINT` and `ARCHIVE_CHAT_DEPLOYMENT_NAME` set (az login) and skips when unconfigured; it costs a few cents per document.
- Archive scan reading on real PostgreSQL (migration over existing extraction rows, constraints, the budget sweep, real ledger SQL; skip when unconfigured): `dotnet test tests/archive/backend --filter "Category=PostgresReading"` with `ARCHIVE_TEST_POSTGRES` set as for the AI ledger
- Archive A2UI cards on real PostgreSQL (migration with existing rows, jsonb round trip, resolvers and load-time re-check as real SQL; skip when unconfigured): `dotnet test tests/archive/backend --filter "Category=PostgresA2ui"` with `ARCHIVE_TEST_POSTGRES` set as for the AI ledger
- Archive integration test: `dotnet test tests/archive/apphost` (fresh containers; stop a running archive AppHost first).
- Archive frontend deps/check/build: from `src/archive/frontend`, `pnpm install --frozen-lockfile`, `pnpm run check`, `pnpm run build`.
- Archive browsers: from `src/archive/frontend`, `pnpm exec playwright install chromium`, then `ARCHIVE_BASE_URL=http://localhost:<frontend-port> pnpm run test:browser`. Single test: `pnpm run test:browser -- --grep "German deep link"`.
- Archive image: `docker build -f src/archive/Dockerfile -t liedertafel-archive:local .` (Podman additionally needs `--ignorefile src/archive/Dockerfile.dockerignore`). Full smoke instructions in the archive README.

### Linting
- Website/dashboard have no lint script. Archive frontend has generated Biome `lint` / `format` scripts and `check` (Biome + Next route types + TypeScript).
- Do not invent lint commands; add one only if explicitly requested.

### Tests
- Website/dashboard have no JavaScript test runner; archive uses Playwright and xUnit as listed above.
- API endpoint/date/pagination checks live in `tests/dashboard-api`; run `dotnet run --project tests/dashboard-api`.
- Dashboard single-test command: not applicable; the focused console harness runs all API checks.
- If tests are added later, update this file with a single-test example.

## Astro Conventions

These Astro/style conventions apply to the website/dashboard. Archive frontend
uses its generated Next.js/Biome conventions and local `AGENTS.md`.
- Use `.astro` components for pages and UI sections.
- Keep page composition in `src/pages` and reuse UI in `src/components`.
- Use `Layout.astro` as the global shell and for global CSS.
- Global design tokens live in `:root` within `Layout.astro`.
- Prefer scoped component styles in `<style>` blocks.

## Formatting and Style
- Indentation uses tabs in `.astro` and CSS blocks.
- Use double quotes for JavaScript/TypeScript strings in frontmatter.
- Use single quotes inside HTML attributes only when necessary.
- Keep HTML and CSS aligned with existing component patterns.

## Git Commits
- When a commit is requested, always use Karma commit message format:
  `type(scope): subject` (e.g., `feat(team): add team images`).
- Valid Karma types: `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`, `chore`, `build`, `ci`.
- Keep the subject concise, imperative, and lowercase; scope is optional.

## CSS and Design System
- Global typography variables live in `Layout.astro`.
  - `--font-heading` defines the heading font.
- Heading styles are centralized in `Layout.astro`.
  - Do not reintroduce per-component heading styles unless required.
- Keep component styles small and focused on layout and component-specific rules.
- Prefer existing color palette and gradients; avoid introducing new colors.
- Avoid adding global CSS unless the rule is truly shared.

## Naming Conventions
- CSS classes are descriptive and lowercase with hyphenation.
- Component file names use PascalCase (e.g., `TeamIntro.astro`).
- Page routes map to file names under `src/pages`.

## Imports and Assets
- Use relative imports for local components and assets.
- Keep imports at the top of the frontmatter block.
- Use `src/assets` for images, PDFs, and SVGs referenced in code.

## Data and Content
- Inline data structures (e.g., team list) live in frontmatter.
- Content is German; keep copy consistent with existing tone.
- When adding content, keep line length reasonable for readability.

## Error Handling and Safety
- This site is mostly static; avoid unnecessary runtime logic.
- Validate external links and include `rel="noreferrer"` when using `target="_blank"`.
- Prefer explicit `alt` text for images.

## Accessibility
- Use semantic elements (`section`, `article`, `nav`, `footer`).
- Add `aria-label` where icons or decorative elements are used.
- Ensure text remains readable over gradients and overlays.

## Performance
- Favor SVGs and optimized images where possible.
- Avoid large inline assets in components.

## Build Artifacts
- `src/website/dist/` is a build output directory.
- Avoid editing `dist/` directly unless explicitly asked.
- Archive `frontend/out/`, `.next/`, `.next-dev/`, Playwright output, .NET `bin/obj`
  and `.local/` keys are generated/ignored. Never copy development keys into images.

## Archive Contracts

- [ADR-0001](docs/adr/0001-flat-lied-and-upload-first.md) simplifies the archive:
  flat Lied → Dateien, upload-first editing, AI suggestions only inline (no
  review queue or per-field origin), one editable Programm per Auftritt. Until the
  Vereinfachung issues land, parts of the code below still follow the old
  design; the ADR and `GLOSSARY.md` win for new work.
- Keep Next.js statically exportable; ASP.NET serves the only production origin.
  Browser APIs use relative `/api/*`; API errors must never fall back to HTML.
- Every archive C# host calls `AddServiceDefaults()`. Development requires
  AppHost-injected OTLP logs/traces/metrics; console logging alone is insufficient.
- Later workers join `WithArchiveDependencies`, use explicit start for finite
  operator work, carry W3C trace context, and use `RunArchiveJobAsync` to flush.
- EF migrations are explicit (`archive-migrate` / `--migrate`), never API startup.
  Schema lives under `backend/Data/Migrations`; each feature adds its own entities.
- Every model call goes through `Ai/AiGateway` (telemetry → budget → provider)
  and carries an `AiOperation`. Raw provider clients are registered only under
  the key `AiGateway.ProviderKey`; a host registers everything with
  `AddArchiveAi(configuration)`. The EUR 15 monthly cap, prices and limits are
  `Archive:Ai` settings. The ledger store is PostgreSQL SQL; tests on EF
  InMemory use `InMemoryAiLedgerStore`.
- Agent-made UI is A2UI with the archive catalog only (`frontend/lib/a2ui.ts`,
  `backend/Chat/A2uiSurfaces.cs`): the model passes record ids to
  `zeige_oberflaeche`, the server resolves them against what the audience may
  see and builds the messages, stores them with the chat message and rebuilds
  them on load. Never accept text or URLs from the model; never register the
  basic catalog. AG-UI events leave the server without null properties (the
  strict `@ag-ui/client` refuses explicit nulls); Playwright chat mocks use
  `tests/chat-mock.ts`.
- The AI reading job (ARC-034-1, `--read-queue` / `--dispatch-reading`, `backend/Reading`)
  reads page images (PDFium through the `PDFtoImage` package, native libraries
  ship in the package) and text, and writes catalogue fields only through
  `AutomatedFieldWriter`: only certain, quote-verified values are written, and
  only into fields that are still empty; everything else is dropped. A value an
  editor entered is never overwritten, and nothing records where a value came
  from. Keep input per call below
  `Archive:Ai:MaxInputTokensPerCall` (Luna's long-context tier). Every finite
  command registers `AddArchiveAi`; what differs is the identity the hosted job
  runs under. It stays switched off (`Archive:Reading:Enabled`, false in
  `appsettings.json`, true in Development) until its Azure job, identity and
  scheduled `--dispatch-reading` exist.
- Keep liveness dependency-free. Local diagnostics/mail/storage operations are
  Development-only; no production credentials are needed for ordinary startup.
- Root SDK now selects .NET 10 with latest-feature roll-forward. Existing Azure
  Functions still target .NET 9; CI installs both SDK lines.

## Infrastructure
- Azure estate lives in resource group `RG-Liedertafel` (public Static Web App `liedertafel`, internal dashboard Static Web App `liedertafel-dashboard`).
- Bicep templates under `infrastructure/`; deploy prompts run the workflow
  `.github/workflows/infra-deploy.yml` on changes to `infrastructure/**`.
- Validate before deploying infrastructure changes:
  - `az bicep build --file infrastructure/main.bicep --stdout`
  - `az deployment group what-if --resource-group RG-Liedertafel --template-file infrastructure/main.bicep --parameters infrastructure/main.bicepparam`
- Apply: `az deployment group create --resource-group RG-Liedertafel --template-file infrastructure/main.bicep --parameters infrastructure/main.bicepparam`
- Destructive changes (Delete/Replace) are rejected by the workflow guard; keep adoption changes `Modify`-only.
- Archive shell (ARC-009) lives in `infrastructure/archive/` targeting new group
  `RG-Liedertafel-Archive`: `subscription.bicep` creates the group,
  `main.bicep` owns topology (env/app 0–2 replicas, vault, workspace, runtime
  identity, optional managed cert). The running image is selected only via
  `archive.yml` (merged check + release: full check suite first, then a
  `archive-prod`-gated release job that reuses the checked image; push to
  `main` on image-affecting files or explicit `workflow_dispatch`,
  deploy by GHCR digest); `infra-deploy-archive.yml` passes the deployed image
  through so infra re-runs cannot revert a release. PR what-if uses a separate
  Contributor-only preview identity. Runbook:
  `infrastructure/archive/README.md`. Jobs `ja-archive-extract`,
  `ja-archive-read` (ARC-034-1, the only job identity with the OpenAI role) and
  the cron `ja-archive-read-dispatch` run the same image parameter; the release
  pauses, stops and pins all of them. `readingEnabled=false` only stops new
  hand-overs, endpoints and UI; to stop model spending set
  `Archive__MaintenanceMode=true` on the reading jobs or drop the reading
  identity's OpenAI role (this fails pending readings; prefer maintenance).
  - `az bicep build --file infrastructure/archive/main.bicep --stdout`
  - `az deployment sub what-if --location austriaeast --template-file infrastructure/archive/subscription.bicep --parameters archiveResourceGroupName=RG-Liedertafel-Archive --parameters resourceGroupLocation=austriaeast`
- The dashboard SWA is auth-gated (GitHub EasyAuth, roles `admin`/`collaborator`);
  identity-provider enablement and role mapping are portal-managed, not Bicep.
  Deployments use the GitHub Actions secret
  `DASHBOARD_AZURE_STATIC_WEB_APPS_API_TOKEN` (workflow
  `.github/workflows/build-and-deploy-dashboard.yml`).

## Cursor / Copilot Rules
- No Cursor rules found in `.cursor/rules/` or `.cursorrules`.
- No Copilot rules found in `.github/copilot-instructions.md`.

## Recommended Editing Workflow
1. Inspect related component/page styles before changing global rules.
2. Keep design consistent with existing sections.
3. Prefer small, focused edits to minimize layout regressions.
4. Run `cd src/website && pnpm run build` to verify when making larger changes.

## Notes for Agentic Tools
- Follow existing style and structure; do not introduce a new system.
- Centralize shared CSS in `Layout.astro` and use `:global()` where needed.
- Avoid adding dependencies unless requested.
- If you must add a new script or tool, update this file.

## Agent skills

### Issue tracker

Issues live in GitHub Issues on `JakobFerdinand/liedertafel`, managed with the `gh` CLI; archive tickets are sub-issues of #96, grouped by `archiv:*` labels and milestones. See `docs/agents/issue-tracker.md`.

### Triage labels

Default vocabulary: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
