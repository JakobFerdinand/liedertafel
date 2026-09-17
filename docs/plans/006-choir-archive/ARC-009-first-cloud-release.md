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

- [x] Provision the dedicated archive group, hosting and scoped OIDC/runtime
  identities with Bicep, following the chosen regional footprint.
- [x] Build/check a versioned image in GitHub Actions, push to private GHCR, and
  deploy only through an explicit release action; configure pull secrets securely.
- [x] Package the production app independently of local Aspire orchestration;
  neither AppHost nor the dashboard becomes a required hosted service, and
  development OTLP endpoints/credentials are not baked into the image.
- [ ] Bind the domain and managed HTTPS certificate; verify SPA deep links,
  correct API error routing, and no development endpoints or seeded choir data.
- [x] Configure Consumption zero-to-two replica bounds, process-based probes,
  and image ownership so an infrastructure update cannot revert a release.
- [x] Document pull-credential ownership/renewal and the second maintainer path.

## Verification evidence (2026-09-16, local)

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
  Infrastructure`) green in CI.

## Verification evidence (2026-09-17, live on `main`)

Provisioning: the merge push of PR 85 ran `Deploy Archive Infrastructure`
green and created all five resources in `RG-Liedertafel-Archive`
(`cae-liedertafel-archive`, `ca-liedertafel-archive`, `id-archive-app`,
`kv-liedertafel-archive`, `log-liedertafel-archive`) in `austriaeast`.

Two distinguishable releases, both green with `archive-prod` approval:

- Run `35184966107` (`0.1.0+51cf5c6`):
  `ghcr.io/jakobferdinand/liedertafel-archive@sha256:397baf27…`
- Run `35185807511` (`0.2.0+ebd8075`):
  `ghcr.io/jakobferdinand/liedertafel-archive@sha256:ab48b050…`
  (the first `0.2.0` attempt `35185567856` deployed `sha256:44d0bea1…`
  but its smoke check raced the revision switch; fixed in `ebd8075` by
  polling `/api/build` for the new version instead of `/alive`.)

Selected version live: `/api/build` on the Azure FQDN reports
`0.2.0+ebd8075…`, `development=false`; `az containerapp show` reports the
same digest. Both green release runs smoke-checked the live app:
`/alive` 200, `/system/status/` with archive content,
`/api/dev/database` 404, unknown `/api/*` 404
`application/problem+json` — no dev endpoints, no seeded choir data.

Scale: `minReplicas 0` / `maxReplicas 2` with the HTTP `concurrentRequests`
rule and `/alive` startup/liveness/readiness probes live. Observed idle
scale-down to 0 running replicas, then a cold `/alive` answering 200 in
**26.4 s** (within the ~60 s budget), serving the selected digest
afterwards.

Image ownership: `infra-deploy-archive.yml` re-run by
`workflow_dispatch` (`35186678112`, green, destructive-change guard clean)
preserved `sha256:ab48b050…`; `/api/build` still reports `0.2.0`.

Single remaining box: the World4You DNS records (`archiv` CNAME →
`ca-liedertafel-archive.gentleforest-881bec30.austriaeast.azurecontainerapps.io`,
`asuid.archiv` TXT → env verification id) plus setting
`bindCustomDomain=true` and re-running the infra workflow for the managed
certificate. Everything else in the acceptance list is proven above.

## Verification

Deploy two distinguishable builds by immutable identity, verify the selected
version, observe idle scale-down and a cold request, and reapply infrastructure
without changing the selected image. This release exposes only the empty shell;
real hosted member/data access is ARC-011.

## Handoff and parallel work

Publish deployed resource/identity outputs and release inputs. Email ARC-010 and
local product work proceed independently. Later cloud slices consume this release
path rather than invent another deployment mechanism.
