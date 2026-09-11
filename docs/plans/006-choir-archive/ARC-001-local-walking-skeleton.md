---
id: ARC-001
status: planned
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

- [ ] Create the frontend/backend structure from the architecture and select
  supported Aspire/framework versions compatible with the repository's .NET SDK
  strategy; add AppHost and shared .NET Service Defaults projects.
- [ ] AppHost starts/connects the API, hot-reload Vite frontend, local PostgreSQL,
  Azurite Blob/queues, mail capture and dashboard with references/readiness order.
  Establish the registration contract for local workers added by later slices.
- [ ] Build Vite assets into the ASP.NET image; deep links return the frontend,
  API errors remain API errors, and no Node.js runtime is needed in the image.
  Keep development cookie/CSRF flow representative through the frontend proxy.
- [ ] Provide a build/version API response rendered by the UI. Verify local
  PostgreSQL connectivity through a development-only diagnostic, not liveness.
- [ ] Establish EF migration execution as an explicit command, local mail capture,
  isolated development keys, and local Blob/queue configuration conventions.
- [ ] Export API OpenTelemetry logs, traces and metrics to Aspire over OTLP using
  AppHost-injected configuration. Give services clear names and define worker
  tracing/flush defaults; console-only logging does not satisfy this requirement.
- [ ] Establish focused backend and browser verification commands; document them
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
