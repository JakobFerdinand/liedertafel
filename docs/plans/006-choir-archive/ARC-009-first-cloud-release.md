---
id: ARC-009
status: in_progress
phase: core
kind: enabler
depends_on: ["ARC-001", "ARC-004"]
touches: ["azure-foundation", "release-workflow", "app-shell"]
external_inputs: ["azure-maintainer-access", "github-package-access", "dns-maintainer-access"]
---

# ARC-009 — Explicitly release the packaged shell to the archive domain

**Depends on:** [ARC-001](ARC-001-local-walking-skeleton.md),
[ARC-004](ARC-004-azure-footprint.md).

## Outcome

A maintainer selects a tested image and opens its build/version screen at
`archiv.liedertafel-mining.at` on scale-to-zero Container Apps.

## Acceptance criteria

- [ ] Provision the dedicated archive group, hosting and scoped OIDC/runtime
  identities with Bicep, following the chosen regional footprint.
- [ ] Build/check a versioned image in GitHub Actions, push to private GHCR, and
  deploy only through an explicit release action; configure pull secrets securely.
- [x] Package the production app independently of local Aspire orchestration;
  neither AppHost nor the dashboard becomes a required hosted service, and
  development OTLP endpoints/credentials are not baked into the image.
- [ ] Bind the domain and managed HTTPS certificate; verify SPA deep links,
  correct API error routing, and no development endpoints or seeded choir data.
- [ ] Configure Consumption zero-to-two replica bounds, process-based probes,
  and image ownership so an infrastructure update cannot revert a release.
- [x] Document pull-credential ownership/renewal and the second maintainer path.

## Verification evidence (2026-09-16)

Local packaging/shell checks on `docs/choir-archive-plan` (`91d8e99`):

- `az bicep build` passes for `subscription.bicep` and `main.bicep`.
- Production image builds
  (`VERSION=0.9.0-arc009`, `REVISION=<head>`) and boots with **no**
  database, storage, SMTP, OTLP endpoint or `Development` environment.
- Smoke against the image: `/alive` 200; `/api/build` reports
  `development=false` with the input version plus revision;
  `/api/dev/database` 404; unknown `/api/*` 404
  `application/problem+json` (`API-Endpunkt nicht gefunden.`);
  `/system/status/` 200 with archive content; runtime is non-root, no
  `node` in the final stage. Dev diagnostics are `IsDevelopment()`-gated
  (`DiagnosticEndpoints.cs`), `/alive` is dependency-free
  (`Extensions.cs`), `/api/build` carries the baked version plus revision.
- `dotnet build src/archive/Archive.slnx` green;
  `dotnet test tests/archive/backend` **73/73 green**;
  frontend `pnpm run check` green.
- PR what-if previews (`Deploy Archive Infrastructure`, `Deploy
  Infrastructure`) are green in CI.

Still live-gated (maintainer bootstrap, keeps this issue `in_progress`):

- `RG-Liedertafel-Archive` exists but is **empty**: no container app,
  environment or deployments. `archiv.liedertafel-mining.at` does not
  resolve yet (`bindCustomDomain=false` until the World4You CNAME/asuid
  TXT records exist).
- `release-archive.yml` is not on `main` yet, so no explicit release run
  has happened; the `archive-prod` environment (required reviewer) is
  configured. No live deploy was performed from this branch: releases must
  go through the explicit digest-pinned action after merge, not through a
  local `az` deploy that would bypass reviewers.
- Remaining live proof: one-off Entra/secret/DNS bootstrap per
  `infrastructure/archive/README.md`, then two distinguishable digests,
  selected-version check, idle scale-down to 0 plus cold request, and an
  infra reapply that preserves the deployed digest.

## Verification

Deploy two distinguishable builds by immutable identity, verify the selected
version, observe idle scale-down and a cold request, and reapply infrastructure
without changing the selected image. This release exposes only the empty shell;
real hosted member/data access is ARC-011.

## Handoff and parallel work

Publish deployed resource/identity outputs and release inputs. Email ARC-010 and
local product work proceed independently. Later cloud slices consume this release
path rather than invent another deployment mechanism.
