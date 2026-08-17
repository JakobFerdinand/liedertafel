# Liedertafel Mining 1906

The homepage of the choir "Liedertafel Mining 1906".

## Development

- Install: `pnpm install`
- Dev server: `pnpm run dev`
- Build: `pnpm run build`

## Infrastructure

The Azure estate (static web app `liedertafel` in `RG-Liedertafel`) is managed with
Bicep under `infrastructure/`. Infrastructure changes are deployed automatically by
`.github/workflows/infra-deploy.yml`; see `docs/plans/001-infrastructure-as-code.md`.

Validate: `az deployment group what-if --resource-group RG-Liedertafel --template-file infrastructure/main.bicep --parameters infrastructure/main.bicepparam`
Apply: `az deployment group create --resource-group RG-Liedertafel --template-file infrastructure/main.bicep --parameters infrastructure/main.bicepparam`
