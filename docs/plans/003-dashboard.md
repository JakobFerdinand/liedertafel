# Dashboard Plan

## Goal

Add an internal, password-less dashboard for the Liedertafel estate: a second
Azure Static Web App (`liedertafel-dashboard`) protected by GitHub
authentication (only the GitHub user `JakobFerdinand` may sign in), with an
Astro frontend that renders cookie-free page-view statistics of the public
website. The dashboard is reachable via the custom domain
`dashboard.liedertafel-mining.at`. The dashboard reads the same `pageviews`
table in Azure Table Storage that the website's beacon already writes — it
adds no new data, only a read-only view.

Version 1 is a single landing page (`/`) showing the full page-view
statistics: period toggle (4 Wochen / 3 Monate / 6 Monate), KPI tiles (Gesamt,
meistbesuchte Seite, einzigartige Seiten), a stacked weekly bar chart per page,
a top-pages table, device-category bars plus weekly device series, and
origin-domain table plus weekly series.

The entire dashboard — static pages **and** the API — is behind Static Web App
EasyAuth with the GitHub identity provider. Unauthenticated visitors are
redirected to the GitHub login; authenticated users without the `admin` or
`collaborator` role get a 403 page.

## What is needed (inventory)

| Component | New? | Notes |
| --------- | ---- | ----- |
| Static Web App `liedertafel-dashboard` | **New** | Free SKU, `westeurope`; managed functions host the dashboard API |
| Storage account / `pageviews` table | **No** | Reuses `stliedertafel` and the existing table written by the website API |
| Dashboard API (`src/dashboard-api/`) | **New** | C# .NET 9 isolated, HTTP trigger `GET /api/pageviews/stats`, read-only |
| Dashboard frontend (`src/dashboard/`) | **New** | Astro + Svelte islands + Layerchart |
| GitHub auth (provider + role mapping) | **New, manual** | Portal-managed one-time setup on the dashboard SWA (see §2) |
| Custom domain `dashboard.liedertafel-mining.at` | **New, partly manual** | Bicep-declared child resource + manual DNS CNAME record (see §1) |
| `staticwebapp.config.json` (dashboard) | **New** | Route auth rules, 401/403 overrides, `apiRuntime` |
| Build workflow (dashboard) | **New** | `.github/workflows/build-and-deploy-dashboard.yml` + new secret |
| Infra workflow (app settings) | **Edit** | Set `StorageConnection` on the dashboard SWA as well |
| `liedertafel.slnx`, `AGENTS.md` | **Edit** | Add the new project, update layout/commands |

No key vault, no additional storage — the dashboard stays on the Free tier;
it gets one custom domain (`dashboard.liedertafel-mining.at`) with a
free Azure-managed TLS certificate.

## Decisions

- **Separate SWA instead of a page inside the public site.** The dashboard has
  a different trust surface (auth-gated, internal tooling). Splitting into its
  own SWA keeps the public site's deployment, config, and auth rules untouched.
- **Managed functions on the dashboard SWA.** The API runs as managed
  functions (consumption, HTTP triggers only, no extra cost, no separate
  Function App resource) — same model as the website API.
- **Read-only analytics.** The dashboard API never writes; retention cleanup
  stays with the website's write path (36 months).
- **Same storage account, own app setting.** Both SWAs get their own
  `StorageConnection` app setting (same key), set by the infra workflow —
  never committed.
- **Auth via SWA EasyAuth (GitHub).** Role-based access on all routes
  (`admin`, `collaborator`); GitHub user `JakobFerdinand` is granted `admin`.
  The identity-provider enablement and role-to-user mapping are
  portal-managed and documented as a manual one-time step (§2, limitations).
- **.NET 9 everywhere.** `net9.0` target, `dotnet-isolated:9.0` runtime —
  matches the repo SDK (`global.json` 9.0.100) and the website API.
- **Stack, nothing more.** Astro + `@astrojs/svelte` + Svelte 5 + Layerchart
  (+ `d3-scale`/`d3-array` as chart companions). No icon library, no UI or CSS
  framework, no extra npm packages.
- **German UI copy**, consistent with the website.
- **Self-contained styling.** The dashboard gets its own small `global.css`
  with a minimal token set; no dependency on the website's `Layout.astro`
  tokens. Code style follows repo conventions (tabs in `.astro`/CSS,
  PascalCase component files).

## 1. Azure resources (Bicep)

### `infrastructure/modules/static-sites.bicep`

Add a second site next to the existing one:

- New params `dashboardSiteName` and `dashboardCustomDomains` (default `[]`).
- New resource `Microsoft.Web/staticSites@2023-12-01`, Free SKU, same
  location, `allowConfigFileUpdates: true` (so the config file can be
  updated from the repo).
- New child-resource loop `staticSites/customDomains` for the dashboard site
  (mirroring the website's pattern).
- New output `dashboardSiteId`.

### `infrastructure/main.bicep` / `main.bicepparam`

- Wire `dashboardSiteName` and `dashboardCustomDomains` through the
  `staticSites` module invocation.
- Param values: `dashboardSiteName: 'liedertafel-dashboard'` (global name
  must be unique, lowercase),
  `dashboardCustomDomains: ['dashboard.liedertafel-mining.at']`.

### Custom domain & DNS (partly manual)

Static Web Apps validate custom domains via a CNAME record and then issue a
free Azure-managed TLS certificate automatically (Free tier supports
single-subdomain custom domains; the certificate renews automatically).

1. **Manual (not Bicep-configurable):** at the DNS provider of
   `liedertafel-mining.at` (same place the existing `liedertafel-mining.at` /
   `www` records live), add one CNAME record:
   `dashboard` → `<default hostname of liedertafel-dashboard>.azurestaticapps.net`.
2. **Bicep:** the custom domain is declared as a child resource (see above),
   so the first infra deploy is a pure `Create` for the SWA and the domain.
3. **Validation is asynchronous:** after the record exists, the SWA picks it
   up within ~1 hour and the domain flips to "Ready" with the managed
   certificate. Easiest order: create the CNAME record first, then deploy —
   both orders eventually converge because validation retries. Check with
   `az staticwebapp show --name liedertafel-dashboard --resource-group
   RG-Liedertafel` (hostname state).
4. All auth and API behaviour is origin-agnostic: the 401 redirect, the
   `/.auth/me` calls, and the `/api/*` routes work unchanged on the custom
   domain. The dashboard domain is `Create`-only in `what-if`; the
   destructive-change guard stays untouched.

### `infrastructure` workflow (`infra-deploy.yml`)

- After deployment, also set the dashboard SWA app setting (same pattern as
  the website step):

```bash
KEY=$(az storage account keys list --resource-group RG-Liedertafel \
  --account-name stliedertafel --query "[0].value" -o tsv)
az staticwebapp appsettings set \
  --name liedertafel-dashboard --resource-group RG-Liedertafel \
  --setting-names "StorageConnection=DefaultEndpointsProtocol=https;AccountName=stliedertafel;AccountKey=$KEY;EndpointSuffix=core.windows.net" \
  --output none
```

- The existing what-if guard stays valid: a new SWA is a `Create`, not a
  destructive change. The existing service principal
  (`sp-liedertafel-iac`, Contributor on `RG-Liedertafel`) already covers the
  new resource.

## 2. Auth & access control

### Route rules (`src/dashboard/staticwebapp.config.json`)

```json
{
  "routes": [
    {
      "route": "/*",
      "serve": "/index.html",
      "statusCode": 200,
      "allowedRoles": ["admin", "collaborator"]
    }
  ],
  "responseOverrides": {
    "404": { "rewrite": "/404.html", "statusCode": 404 },
    "401": {
      "statusCode": 302,
      "redirect": "/.auth/login/github?post_login_redirect_uri=.referrer"
    },
    "403": { "rewrite": "/403.html", "statusCode": 403 }
  },
  "platform": { "apiRuntime": "dotnet-isolated:9.0" }
}
```

- `/*` (static pages **and** `/api/*` managed functions) requires
  `admin`/`collaborator`. Unauthenticated → 302 to GitHub login; after login
  the visitor lands back on the referring URL.
- The API additionally declares `AuthorizationLevel.Function` (SWA supplies
  the key transparently on same-origin requests).

### Manual one-time setup (portal, not Bicep-configurable)

1. In the Azure portal, open `liedertafel-dashboard` → *Authentication*:
   enable **GitHub** as identity provider.
2. Grant role **admin** to GitHub user **`JakobFerdinand`** (invitation/user
   mapping). The `collaborator` role stays available for future users without
   changing Bicep or config.

### 403 page

`src/dashboard/src/pages/403.astro` — German "Kein Zutritt" page with a link
back to the public website. Rendered by the 403 override above.

## 3. API (`src/dashboard-api/`)

New C# .NET 9 isolated functions project, mirroring the website API layout
(|vertical slice| style: function entry, records, handler, store):

- `src/dashboard-api/dashboard-api.csproj` — `net9.0`, isolated worker,
  packages: `Microsoft.Azure.Functions.Worker`,
  `Microsoft.Azure.Functions.Worker.Sdk`,
  `Microsoft.Azure.Functions.Worker.Extensions.Http`, `Azure.Data.Tables`.
- `src/dashboard-api/Program.cs` — requires `StorageConnection` (throw if
  unset, same message style as the website API), registers a singleton
  `TableServiceClient`, the stats handler, and the read store.
- `src/dashboard-api/shared/entities/PageViewEntity.cs` — same shape as the
  website entity (`Path`, `ReferrerHost`, `ViewportWidth`, `Timestamp`,
  `PartitionKey = "Pv|{yyyy-MM-dd}"`).
- `src/dashboard-api/features/pageviews/GetPageViewStats.cs` —
  - `[Function("get-pageview-stats")]`, `HttpTrigger(AuthorizationLevel.Function, "get", Route = "pageviews/stats")`.
  - Optional `days` query param: default 28, presets 28/90/180, clamped to
    180 (matches the 180-day partition-lookback).
  - Store: reads the `pageviews` table with the partition-range filter
    `PartitionKey ge 'Pv|{windowStart}' and le 'Pv|{today}'`; materialized in
    memory (small volume).
  - Aggregation (weekly buckets, ISO week start, zero-filled weeks):
    - `Total`, `UniquePaths`, `TopPaths` (top 10),
    - `Series` (total per week),
    - `PathSeries` (top 6 paths + `Übrige` bucket, stacked chart),
    - `Devices` + `DeviceSeries` (Mobil/Tablet/Laptop/Breitbild by
      `ViewportWidth`),
    - `Origins` + `OriginSeries` (external referrer hosts, top 6 + `Übrige`,
      lowercased, origin only),
    - internal referrers excluded: `liedertafel-mining.at`,
      `dashboard.liedertafel-mining.at`, and `*.azurestaticapps.net`
      (visitors coming from the dashboard onto the website must not count
      as an external origin).
  - Response shape matches the frontend contract described in §4.
- `src/dashboard-api/local.settings.json` + `requests.http` (sample call with
  `days` variations).
- Add the project to `liedertafel.slnx` (new `/src/` entry).

## 4. Frontend (`src/dashboard/`)

Astro project with a single Svelte island on the landing page:

- `package.json` — scripts `dev`/`build`/`check`/`preview`/`astro`;
  dependencies: `astro`, `@astrojs/svelte`, `svelte`, `layerchart`,
  `d3-scale`, `d3-array`, `sharp`; devDependencies: `@astrojs/check`,
  `@astrojs/ts-plugin`, `svelte-check`, `typescript`, `@types/d3-scale`,
  `@types/d3-array`. Nothing else.
- `pnpm-workspace.yaml` — `allowBuilds: esbuild: true` (same as the website,
  required for Astro/Vite build scripts under pnpm 10).
- `astro.config.mts` — `integrations: [svelte()]`; no `site` needed (no
  sitemap, internal tool).
- `src/styles/global.css` — minimal token set (club colors, small
  `--token` palette), `container`/`section`/`card`/KPI/table/loading/error
  primitives; tabs indentation.
- `src/layouts/DashboardLayout.astro` — HTML shell, skip link, navbar island,
  `<main>` slot; headings consistent with the repo's design language.
- `src/components/DashboardNavbar.svelte` — brand/title, link "Zur Website"
  (https://liedertafel.at, `rel="noreferrer"`), current user from
  `/.auth/me` (`clientPrincipal.userDetails`, GitHub avatar), logout link
  (`/.auth/logout`). No icons — text labels or inline, decorative-free.
- `src/components/PageViewStats.svelte` — Svelte 5 runes (`$state`,
  `$derived`, `$derived.by`):
  - Fetches `/api/pageviews/stats?days=…` on mount and on segment toggle
    (AbortController guards against races).
  - KPI tiles: Gesamt page views, meistbesuchte Seite (+ Aufrufe count),
    einzigartige Seiten.
  - Stacked weekly bar chart via Layerchart (`Chart`, `Layer`, `Axis`,
    `Bars`, `Highlight`, `Tooltip`, `groupStackData`, `scaleBand`) for
    paths, devices, and origins; chart colors from dashboard CSS variables.
  - Top-pages table + origin-domain table; accessible screen-reader tables
    for every chart.
  - Loading, empty, and error states; German copy; `Intl.NumberFormat` with
    `de-AT`.
- `src/pages/index.astro` — the landing page: renders `PageViewStats` as a
  Svelte island (`client:only="svelte"`) inside `DashboardLayout`.
- `src/pages/403.astro`, `public/favicon.svg` (club logo), `tsconfig.json`,
  `src/dashboard/.gitignore` (`.astro/`, `dist/`, `node_modules/`).
- `src/dashboard/README.md` — brief local-dev instructions.

## 5. Deployment workflow

New `.github/workflows/build-and-deploy-dashboard.yml`, same shape as the
website workflow:

- Triggers: push/PR on `main` with paths `src/dashboard/**`,
  `src/dashboard-api/**`, and the workflow file itself; `closed` PR job for
  cleanup.
- Steps: checkout → pnpm setup → node 22 (cache on dashboard lockfile) →
  `pnpm install` → `astro check` → `dotnet publish
  src/dashboard-api/dashboard-api.csproj -c Release -o api_output` →
  `pnpm run build` → `Azure/static-web-apps-deploy@v1`:
  - `app_location: "./src/dashboard/dist"`,
  - `api_location: "./api_output"`, `skip_api_build: true`,
  - `config_file_location: "/src/dashboard/"`, `skip_app_build: true`,
  - token: `${{ secrets.DASHBOARD_AZURE_STATIC_WEB_APPS_API_TOKEN }}`.
- One-time setup: grab the deployment token for `liedertafel-dashboard`
  (portal → Manage deployment token, or `az staticwebapp secrets list`) and
  store it as the GitHub Actions secret `DASHBOARD_AZURE_STATIC_WEB_APPS_API_TOKEN`.
- PR environments are covered automatically by the SWA action and sit behind
  the same auth rules.

## 6. Local development

- Frontend: `cd src/dashboard && pnpm install && pnpm run dev`.
- API: `cd src/dashboard-api && dotnet run` with `StorageConnection` in
  `local.settings.json`; exercise via `requests.http`.
- The stats endpoint can be pointed at dev by running the function app
  locally (SWA CLI or a plain `dotnet run`); the island only talks to
  `/api/pageviews/stats`, so local Astro dev keeps working with the API next
  to it.

## 7. Verification

1. `az bicep build --file infrastructure/main.bicep --stdout` passes;
   `az deployment group what-if ...` shows only `Create` for the new SWA.
2. `dotnet build src/website-api/website-api.csproj` and
   `dotnet build src/dashboard-api/dashboard-api.csproj` pass.
3. `cd src/dashboard && pnpm run build` passes (runs `astro check`).
4. Deployed behavior: unauthenticated visit to
   `https://dashboard.liedertafel-mining.at` → redirect to GitHub login;
   sign-in as `JakobFerdinand` → landing page renders KPIs, charts, and
   tables; another GitHub user → 403 page.
5. The custom domain serves the dashboard over HTTPS with a valid, managed
   certificate; the CNAME record points at the SWA default hostname.
6. `curl -i <dashboard-url>/api/pageviews/stats?days=28` without a session
   → 401/redirect; with a session (browser) → 200 JSON matching the §3 shape.
7. After infra deploy: `az staticwebapp appsettings list --name
   liedertafel-dashboard --resource-group RG-Liedertafel` contains
   `StorageConnection`.

## Milestones (tracked)

- [x] Add `liedertafel-dashboard` + `dashboard.liedertafel-mining.at` to
      `static-sites.bicep` + `main.bicep` + `main.bicepparam`; validate with
      `az bicep build` and `what-if` (expect only `Create`)
- [ ] Manual (DNS): add CNAME `dashboard` → SWA default hostname at the DNS
      provider; confirm the domain reaches "Ready" with the managed cert
- [x] Extend `infra-deploy.yml` with the dashboard `StorageConnection` step
- [ ] Manual (portal): enable GitHub auth on `liedertafel-dashboard`, grant
      `admin` to `JakobFerdinand`
- [ ] Add `DASHBOARD_AZURE_STATIC_WEB_APPS_API_TOKEN` secret
- [x] Scaffold `src/dashboard-api` (net9.0): `GetPageViewStats` slice,
      `PageViewEntity`, `Program.cs`, `local.settings.json`, `requests.http`;
      add to `liedertafel.slnx`
- [x] Scaffold `src/dashboard`: Astro project, `DashboardLayout`,
      `DashboardNavbar.svelte`, `PageViewStats.svelte` (+ wrapper), `index.astro`
      landing page, `403.astro`, `staticwebapp.config.json`, `global.css`
- [x] Add `build-and-deploy-dashboard.yml`
- [x] Update `AGENTS.md` (repo layout, dashboard commands) and README
- [ ] Deploy infra → deploy dashboard → verify auth, stats, 403 path

## Known limitations / notes

- GitHub identity-provider enablement and the `admin`/`collaborator`
  role-to-user mapping are **portal-managed** (a documented manual step);
  they cannot be expressed in Bicep.
- The DNS CNAME record for `dashboard.liedertafel-mining.at` cannot be
  created by Bicep and must be added at the DNS provider; custom-domain
  validation is asynchronous (~1 hour after the record is in place).
- The dashboard statistics read the same `pageviews` table the website
  writes; retention (36 months) is enforced by the website's write-path
  purge, so the dashboard sees data as long as the website keeps it.
- SWA Free managed functions support HTTP triggers only — fine for a
  read-only stats endpoint.
- Data visible in the dashboard is the cookie-free analytics data already
  collected (page path, referrer host, viewport width, timestamp) — no new
  personal data is gathered or exposed.
- v1 is observability-only: no management UI, no write operations.