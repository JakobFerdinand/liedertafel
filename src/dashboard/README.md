# Liedertafel Dashboard

Internal analytics dashboard for Liedertafel Mining 1906, built with Astro and Svelte.

The statistics API is served by the project in `src/dashboard-api` (Azure Functions). During
local development, run the API with `dotnet run` in `src/dashboard-api` and the dashboard with:

```
pnpm install
pnpm run dev
```