# AGENTS.md

This repository is an Astro site for Liedertafel Mining 1906.
Use this guide when editing or extending the codebase.

## Repository Layout
- src/website: self-contained Astro site with pages in `src/pages`, reusable sections in `src/components`, global shell/CSS in `src/layouts/Layout.astro`, assets in `src/assets`, content collections under `src/content`, and static files in `public/`.
- src/website-api: Azure Functions managed API (C# .NET isolated); serves the `/api/pageview` endpoint and writes page views to Azure Table Storage.
- src/dashboard: internal, auth-gated Astro dashboard (Svelte islands + Layerchart) for the `liedertafel-dashboard` Static Web App; overview at `/`, session inspector at `/sessions`. Shared API/date helpers in `src/lib`, UI grouped under `src/components/{filters,overview,breakdowns,sessions}`, styling in `src/styles/global.css`.
- src/dashboard-api: Azure Functions managed API (C# .NET isolated); serves read-only `/api/pageviews/stats`, `/api/pageviews/sessions`, and `/api/pageviews/sessions/{sessionRef}` endpoints. All require bounded Vienna date ranges; session references are opaque, range-bound handles.
- src/pageview-storage: shared C# library (net10.0) referenced by both APIs; owns the stored pageview shape (`PageViewEntity`), table name and `Pv|yyyy-MM-dd` UTC partition scheme (`PageViewTable`) and navigation vocabulary (`NavigationTypes`). Change stored-data rules here, never in an API. Its path is in both API workflows' path filters.
- src/archive: Rust choir archive. `backend-rs/` owns the API, static export, finite workers, migrations and OTLP; its nested `AGENTS.md` lists feature-specific checks. `apphost/` orchestrates local development, `apphost-rust/` is the disposable image-test topology, and `frontend/` is the unchanged Next.js static client. Follow `src/archive/README.md` for startup and verification.
- tests/archive: cross-stack Aspire tests in `apphost-rust/`, shared synthetic fixtures in `apphost/TestAssets/` and `fixtures/`; browser checks live in `src/archive/frontend/tests`.
- infrastructure: Azure Bicep templates for the RG-Liedertafel estate.
- infrastructure/neon: archive Neon CLI/API setup runbook and SQL role/grant
  bootstrap. Follow its README for private credential handling; run
  `bootstrap-roles.sql` once as admin. `runtime-grants.sql` runs automatically
  inside the release's `--migrate` as `archive_migrator` (embedded copy at
  `src/archive/backend-rs/crates/archive-db/runtime-grants.sql`, kept identical by a test);
  run it by hand only as a fallback. OpenTofu was trialled but is not adopted.
- Plans and tickets: GitHub Issues (see `## Agent skills`); the choir archive is parent issue #96 with sub-issues ordered by milestone (Vereinfachung, Bestand, Betrieb). `src/archive/docs/architecture.md` is the archive architecture (§14 governs AI assistance). The deterministic
source-folder inventory (Drive, #98) runbook lives at
`src/archive/docs/drive-inventur.md`; its sanitized fixture corpus and
`mapping.json` for the bulk import (#143) at
`tests/archive/fixtures/drive-inventur/`.
- docs/agents: issue tracker, triage label and domain doc conventions for agent skills.
- liedertafel.slnx: .NET solution that opens all API projects together; global.json pins the .NET SDK.

## Commands (Build / Lint / Test)
- Website deps: `cd src/website && pnpm install`
- Dev server: `cd src/website && pnpm run dev`
- Build: `cd src/website && pnpm run build`
- Preview build: `cd src/website && pnpm run preview`
- Website unit tests (Node's built-in runner with type stripping): `cd src/website && pnpm run test:unit`; single file: `node --disable-warning=MODULE_TYPELESS_PACKAGE_JSON --test src/lib/pageview-beacon.test.ts`
- Website API (local): `cd src/website-api && dotnet run`
- Dashboard deps: `cd src/dashboard && pnpm install`
- Dashboard dev server: `cd src/dashboard && pnpm run dev`
- Dashboard check (Astro and Svelte): `cd src/dashboard && pnpm run check`
- Dashboard build: `cd src/dashboard && pnpm run build`
- Dashboard frontend loader tests (pure, no browser; Node's built-in runner with type stripping, Node 24): from `src/dashboard`, `pnpm run test:unit`. Single test: `node --disable-warning=MODULE_TYPELESS_PACKAGE_JSON --test src/lib/loader.test.ts`.
- Dashboard API regression checks (dependency-free console harness): `dotnet run --project tests/dashboard-api`
- Add `-- --azurite` to also verify real Table Storage queries against local Azurite.
- API fixture export for browser smoke checks: append `-- --fixtures /tmp/liedertafel-insights-validation` (see `tests/dashboard-api/README.md`).
- Dashboard API (local): `cd src/dashboard-api && dotnet run` (with `StorageConnection` in `local.settings.json`)
- Archive local stack: build with `cargo build --workspace --locked` from `src/archive/backend-rs`, then from root `dotnet run --project src/archive/apphost` (Podman: `ASPIRE_CONTAINER_RUNTIME=podman`). Start `archive-migrate` explicitly in Aspire. Set `Archive__Pdfium__LibraryPath` for PDF operations and have `ffmpeg`/`ffprobe` for video jobs.
- Archive Aspire projects: `dotnet build src/archive/Archive.slnx`. Image integration: `dotnet test tests/archive/apphost-rust --filter FullyQualifiedName~ExtractionRoundtripThroughRealAzuriteQueueAndFiniteWorker` (candidate and other prerequisites in `src/archive/backend-rs/docs/image-apphost-parity.md`).
- Archive backend: from `src/archive/backend-rs`, `cargo test --workspace --locked`; single example `cargo test --locked -p archive-server --test ops_http`. Real SQL/Queue/Blob/PDFium/image cases are ignored by default, not passed. From the repo root `bash src/archive/backend-rs/tests/image/check-translated.sh` runs both tiers and native tests with synthetic dependencies; see `src/archive/README.md#focused-verification`.
- Archive release checks: `.github/workflows/archive.yml` adds dependency policy, detector, exact-image AppHost and frontend browser gates. The three live AI evaluations explicitly skip without configuration; separate authorization is required to supply it.
- Archive infra guard: `bash src/archive/backend-rs/tests/image/check-infra-preview-policy.sh` tests the pre-apply what-if with a stub CLI, without Azure.
- Archive deterministic inventory: `archive-server --inventur <verzeichnis> [--model <schluessel> --out bericht.json]`; see `src/archive/docs/drive-inventur.md`.
- Archive frontend deps/check/build: from `src/archive/frontend`, `pnpm install --frozen-lockfile`, `pnpm run check`, `pnpm run build`.
- Archive frontend reducer tests (pure, no browser; Node's built-in runner with type stripping, Node 24): from `src/archive/frontend`, `pnpm run test:unit`. Single file: `node --disable-warning=MODULE_TYPELESS_PACKAGE_JSON --test lib/lied-entwurf/reducer.test.ts`. Tests sit beside the module as `*.test.ts` under `lib/`; modules under test import siblings with `.ts` extensions and must not use the `@/` alias.
- Archive browsers: from `src/archive/frontend`, `pnpm exec playwright install chromium`, then `ARCHIVE_BASE_URL=http://localhost:<frontend-port> pnpm run test:browser`. Single test: `pnpm run test:browser -- --grep "German deep link"`.
- Archive image: `docker build -f src/archive/Dockerfile -t localhost/liedertafel-archive:local .` (Podman additionally needs `--ignorefile src/archive/Dockerfile.dockerignore`). See `src/archive/backend-rs/docs/image-apphost-parity.md` for exact-image checks.

### Linting
- Website/dashboard have no lint script. Archive frontend has generated Biome `lint` / `format` scripts and `check` (Biome + Next route types + TypeScript).
- Do not invent lint commands; add one only if explicitly requested.

### Tests
- Website/dashboard unit tests run with Node's built-in runner (type stripping, Node 24): from `src/website` or `src/dashboard`, `pnpm run test:unit`. Single files: `node --disable-warning=MODULE_TYPELESS_PACKAGE_JSON --test src/lib/pageview-beacon.test.ts` (website), `node --disable-warning=MODULE_TYPELESS_PACKAGE_JSON --test src/lib/loader.test.ts` (dashboard); archive uses Cargo, Playwright and Aspire xUnit as listed above.
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
  and `.local/` data is generated/ignored. Never copy local data or credentials into images.

## Archive Contracts

- [ADR-0001](docs/adr/0001-flat-lied-and-upload-first.md) and `GLOSSARY.md` govern the flat Lied → Dateien model, upload-first editing, immutable Datei replacement/Papierkorb and one editable Programm per Auftritt.
- Rust serves the only production origin. Keep Next.js statically exportable and API URLs relative; API errors must never fall back to HTML. Liveness stays dependency-free.
- `archive-telemetry` owns OTLP logs/traces/metrics, required in Development. Finite workers carry W3C context, drain admitted work, and flush before exit. Run migrations explicitly (`archive-migrate` / `--migrate`), never at API startup. The SQLx migrations adopt the verified frozen EF head; older/drifted schemas refuse adoption.
- AI uses the shared Gateway (telemetry → budget → provider), with the EUR 15 cap and environment-only model prices. A2UI accepts record IDs, not text or URLs; the server resolves visibility and builds/rechecks cards (strict AG-UI client: omit nulls). Stored reading only fills empty fields with certain, quote-verified values. Lied/Auftritt readings return inline suggestions; only exact unique Lied matches are pre-selected. See the nested Rust guide for feature contracts.
- Local diagnostics/mail/storage setup are Development-only; normal startup needs no production credentials. The .NET 10 SDK remains for Aspire and the unrelated Functions.

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
  `main` on image-affecting files or explicit `workflow_dispatch` on `main`,
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
  The web app (`id-archive-app`) also calls the model for chat and the draft
  reading (`POST /api/lied-lesungen`), so its identity needs the OpenAI role
  too (`aoaiOpenAIUser`). To stop spending on that path: disable reading
  (`Archive:Reading:Enabled=false` answers 409), set
  `Archive__MaintenanceMode=true` on the web app, or drop its OpenAI role
  (this also breaks chat; prefer the first two).
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
- Nested `AGENTS.md` files (e.g. `src/archive/backend-rs/AGENTS.md`) are additive in OpenCode V2: they load once per session when the agent first touches a file in their subtree. Keep nested files self-consistent with this root file and list a subdirectory's unique rules there, not here.
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
