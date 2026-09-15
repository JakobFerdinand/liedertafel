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
- [ ] Package the production app independently of local Aspire orchestration;
  neither AppHost nor the dashboard becomes a required hosted service, and
  development OTLP endpoints/credentials are not baked into the image.
- [ ] Bind the domain and managed HTTPS certificate; verify SPA deep links,
  correct API error routing, and no development endpoints or seeded choir data.
- [ ] Configure Consumption zero-to-two replica bounds, process-based probes,
  and image ownership so an infrastructure update cannot revert a release.
- [ ] Document pull-credential ownership/renewal and the second maintainer path.

## Verification

Deploy two distinguishable builds by immutable identity, verify the selected
version, observe idle scale-down and a cold request, and reapply infrastructure
without changing the selected image. This release exposes only the empty shell;
real hosted member/data access is ARC-011.

## Handoff and parallel work

Publish deployed resource/identity outputs and release inputs. Email ARC-010 and
local product work proceed independently. Later cloud slices consume this release
path rather than invent another deployment mechanism.
