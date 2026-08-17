# Liedertafel Mining 1906

The homepage of the choir "Liedertafel Mining 1906".

## Development

- `src/website`: Astro marketing site (`cd src/website && pnpm install && pnpm run dev`)
- `src/website-api`: Azure Functions managed API, hosted by the Static Web App
  (`cd src/website-api && dotnet run`); the `liedertafel.slnx` solution opens all
  API projects together.

## Infrastructure

The Azure estate (static web app `liedertafel` in `RG-Liedertafel`) is managed with
Bicep under `infrastructure/`. Infrastructure changes are deployed automatically by
`.github/workflows/infra-deploy.yml`; see `docs/plans/001-infrastructure-as-code.md`.

Validate: `az deployment group what-if --resource-group RG-Liedertafel --template-file infrastructure/main.bicep --parameters infrastructure/main.bicepparam`
Apply: `az deployment group create --resource-group RG-Liedertafel --template-file infrastructure/main.bicep --parameters infrastructure/main.bicepparam`
