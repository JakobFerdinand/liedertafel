---
id: ARC-001
status: done
phase: core
kind: enabler
depends_on: []
touches: ["app-shell", "apphost", "service-defaults", "db-migrations", "build-tooling"]
external_inputs: []
---

# ARC-001 — Start the local archive and its dependencies with Aspire

**Depends on:** None.

## Outcome

A maintainer starts Aspire AppHost, opens the German frontend with a working API
and local dependencies, and sees the request's OpenTelemetry signals in Aspire.

## Acceptance criteria

- [x] Create the frontend/backend structure from the architecture and select
  supported Aspire/framework versions compatible with the repository's .NET SDK
  strategy; add AppHost and shared .NET Service Defaults projects.
- [x] AppHost starts/connects the API, hot-reload Next.js frontend, local PostgreSQL,
  Azurite Blob/queues, mail capture and dashboard with references/readiness order.
  Establish the registration contract for local workers added by later slices.
- [x] Build Next.js static assets into the ASP.NET image; deep links return the frontend,
  API errors remain API errors, and no Node.js runtime is needed in the image.
  Keep development cookie/CSRF flow representative through the frontend proxy.
- [x] Provide a build/version API response rendered by the UI. Verify local
  PostgreSQL connectivity through a development-only diagnostic, not liveness.
- [x] Establish EF migration execution as an explicit command, local mail capture,
  isolated development keys, and local Blob/queue configuration conventions.
- [x] Export API OpenTelemetry logs, traces and metrics to Aspire over OTLP using
  AppHost-injected configuration. Give services clear names and define worker
  tracing/flush defaults; console-only logging does not satisfy this requirement.
- [x] Establish focused backend and browser verification commands; document them
  in the repo guidance. Use a minimal shell, not the entire product schema.

## Verification

Start AppHost from a clean checkout without manually starting dependencies; open
a deep link, call the API, capture test mail and exercise the local data services.
Verify actual logs, traces and metrics in Aspire, then build/smoke the production
image without development services. Record startup/schema/verification commands.

## Handoff and parallel work

Publish AppHost references, Service Defaults/exporter configuration, frontend
proxy conventions, test commands and migration ownership. ARC-002/003/004 can
proceed independently. Later slices add
their own entities and UI rather than waiting for a separate whole-app foundation.

## Implementation and verification

Implemented 2026-09-11. The implementation request replaced Vite with Next.js;
static export preserves the ASP.NET-only runtime image. The concrete deep link
is `/system/status/`. See [the archive runbook](../../../src/archive/README.md)
for all startup, migration, resource, worker, proxy and verification commands.

Recorded local evidence (Linux, .NET SDK 10.0.400, Podman 5.5.2):

- `dotnet build liedertafel.slnx`: all eight projects, no warnings/errors.
  Existing dashboard regression harness: all 41 checks passed under the new SDK.
- `dotnet test tests/archive/backend`: 18 passed. Includes production route/API
  boundaries, dependency-free liveness, CSRF/cookie flags, and authenticated OTLP
  logs/traces/metrics with finite-job failure flushing and URL-query redaction.
- `ASPIRE_CONTAINER_RUNTIME=podman dotnet test tests/archive/apphost`: one passed.
  Fresh nonpersistent database/storage, no manual dependency startup; checks
  pending schema, explicit migration completion, proxy cookie/token flow,
  Blob/queue round trips, finite worker completion and two captured mails.
- Frontend `pnpm run check` and `pnpm run build`: passed.
- Browser suite against the live Aspire frontend: all six desktop/mobile checks
  passed, including deep-link refresh, client navigation and local diagnostics.
- Actual Aspire dashboard inspected: structured build/diagnostic API logs,
  API request traces, finite `worker-smoke` trace with Blob/queue/mail dependency
  spans, correlated producer/consumer parents, and metrics including
  `Liedertafel.Archive / archive.build.requests` and HTTP/runtime instruments.
  The CLI's `otel logs` / `otel traces` also retrieved the exported records.
- Multi-stage production image built and started with no dependency configuration
  and the development stack stopped. All six browser checks passed against it;
  `/alive` works, diagnostics are absent, and `command -v node` finds no runtime.
  Container user ID is 1654, not root.

Versions: .NET 10 LTS; Aspire 13.5.3; EF tools/runtime 10.0.12; Npgsql EF 10.0.3;
Next.js 16.3.5 / React 19.2.8. Projects and xUnit projects were scaffolded using
`dotnet new`; frontend used the requested `create-next-app` flags.
