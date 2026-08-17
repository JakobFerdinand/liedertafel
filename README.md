# Liedertafel Mining 1906

The homepage of the choir "Liedertafel Mining 1906".

## Development

- `src/website`: Astro marketing site (`cd src/website && pnpm install && pnpm run dev`)
- `src/website-api`: Azure Functions managed API, hosted by the Static Web App
  (`cd src/website-api && dotnet run`); the `liedertafel.slnx` solution opens all
  API projects together.
- `src/dashboard`: internal, auth-gated Astro dashboard
  (`cd src/dashboard && pnpm install && pnpm run dev`), see `docs/plans/003-dashboard.md`
- `src/dashboard-api`: read-only stats API for the dashboard
  (`cd src/dashboard-api && dotnet run`)

## Infrastructure

The Azure estate (static web apps `liedertafel` and `liedertafel-dashboard` in
`RG-Liedertafel`) is managed with
Bicep under `infrastructure/`. Infrastructure changes are deployed automatically by
`.github/workflows/infra-deploy.yml`; see `docs/plans/001-infrastructure-as-code.md`.

Validate: `az deployment group what-if --resource-group RG-Liedertafel --template-file infrastructure/main.bicep --parameters infrastructure/main.bicepparam`
Apply: `az deployment group create --resource-group RG-Liedertafel --template-file infrastructure/main.bicep --parameters infrastructure/main.bicepparam`
