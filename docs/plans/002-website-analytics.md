# Website Analytics Plan

## Goal

Track basic website usage (page views) for the Liedertafel site in a cookie-free,
first-party, privacy-friendly way: the site beacons a small JSON payload to an
HTTP endpoint on the Static Web App, which writes one row per page view into
Azure Table Storage. No cookies, no third-party SDKs, no IP addresses.

## What is needed (inventory)

| Component | New? | Notes |
| --------- | ---- | ----- |
| Storage account (Azure Table Storage) | **New** | Stores the `pageviews` table; only new Azure resource |
| Function app | **No new resource** | Hosted by the Static Web App itself as *managed functions* (consumption plan, HTTP-only triggers) — no separate Function App resource, no extra cost |
| Function code (`src/website-api/` in this repo) | **New** | C# .NET isolated, HTTP trigger `POST /api/pageview`, writes to Table Storage |
| Beacon script in `Layout.astro` | **New** | `navigator.sendBeacon` on `window` load |
| SWA app setting `StorageConnection` | **New** | Storage connection string; set via workflow, not committed |
| `staticwebapp.config.json` runtime | **Edit** | Declare `apiRuntime` for managed functions |
| Build workflow | **Edit** | Build and publish the API, pass `api_location` |
| Privacy page | **Edit** | Current text claims "no telemetry/analytics tools" and must be corrected |

No Key Vault is introduced (the estate has none; the storage key is fetched in
the deploy workflow). Application Insights is optional and not required.

## Decisions

- **Managed functions instead of a separate Function App.** The API runs inside
  the existing Static Web App (`liedertafel`, Free SKU, `westeurope`). The Free
  plan includes 1 million function executions/month, far above the expected page
  view volume. Consequence: only HTTP triggers are supported — retention
  cleanup must run lazily inside the write path, not on a timer.
- **Write-only tracking for now.** No read endpoint, no dashboard, no stats
  page. Data is inspected ad hoc via Azure portal or `az storage table query`.
  A protected read endpoint can be added later without changing the write path.
- **Cookie-free and first-party.** No cookies, no localStorage, no SDKs. The
  beacon goes to the same origin (`/api/pageview`), so no CORS is needed.
- **Privacy by design (DSGVO, Art. 6 lit. f).** Store no IP address, no user
  agent, no user ID, no full referrer URL. Only: path, referrer *host* (origin
  only), and viewport width. Disclose the tracking on the privacy page.
- **Anonymous write endpoint.** `AuthorizationLevel.Anonymous` — it must be
  callable by any visitor without credentials.
- **Table auto-created by the function** (`CreateIfNotExistsAsync`), not
  declared in Bicep.
- **Retention: 36 months**, enforced via a lazy purge in the write path (see
  below).

## 1. Azure resources (Bicep)

### New storage module

Add `infrastructure/modules/storage.bicep` and wire it into `main.bicep`:

- **Storage account** `stliedertafel` — `Standard_LRS`, `StorageV2`, Hot tier,
  `westeurope`, HTTPS-only, TLS 1.2 minimum. Global name must stay unique
  (3–24 chars, lowercase).
- No tables declared in Bicep; the function creates `pageviews` itself.
- **Cost:** negligible (few MB of rows, ~€0.02/GB/month; no transactions
  billed beyond LRS ops).

`main.bicep` gains a `storage` module; `main.bicepparam` gains the storage
account name/location params. The existing what-if guard in
`infra-deploy.yml` stays valid: a new storage account is a `Create`, not a
destructive change. The existing service principal
(`sp-liedertafel-iac`, Contributor on `RG-Liedertafel`) already covers the new
resource — no new identity needed.

### SWA app setting

After every infra deployment, the workflow fetches the account key and sets the
connection string on the SWA:

```bash
KEY=$(az storage account keys list --resource-group RG-Liedertafel \
  --account-name stliedertafel --query "[0].value" -o tsv)
az staticwebapp appsettings set --name liedertafel --resource-group RG-Liedertafel \
  --setting-names "StorageConnection=DefaultEndpointsProtocol=https;AccountName=stliedertafel;AccountKey=$KEY;EndpointSuffix=core.windows.net" \
  --output none
```

The function reads `StorageConnection` from its environment (SWA app settings
become env vars; the name does not collide with the reserved `AzureWeb*`,
`WEBSITE*`, … prefixes). The key never enters the repository.

**Why not Bicep-declared app settings?** The ARM schema
`Microsoft.Web/staticSites/config` (child `name: 'appsettings'`) does support
app settings, but Microsoft's official how-to documents portal/CLI only. The
workflow step is kept deliberately:

- The connection string would otherwise be materialized via `listKeys(...)`
  into what-if output and deployment history (readable by anyone with Reader on
  the resource group).
- Settings become drift-prone "side effects" when declared outside the step and
  have a history of config-resource quirks in the community.
- `--output none` keeps the key out of pipeline logs; `az staticwebapp
  appsettings set` would otherwise print all settings, including values.

## 2. Function code (`src/website-api/`)

New `src/website-api/` directory in the repo, C# .NET isolated:

- `src/website-api/website-api.csproj` — .NET 9 or 10 (isolated worker; use the newest
  version supported by SWA managed functions at implementation time), packages
  `Microsoft.Azure.Functions.Worker`, `.Extensions.Http`,
  `Azure.Data.Tables`.
- `src/website-api/Program.cs` — reads `StorageConnection` (throw if unset), registers a
  singleton `TableServiceClient` and the page-view handler.
- `src/website-api/features/pageviews/PageView.cs` —
  - `[Function("pageview")]`, `[HttpTrigger(AuthorizationLevel.Anonymous, "post")]`
    → route `/api/pageview`.
  - Payload: `{ path, referrerHost, viewportWidth }`.
  - Validation: `path` required, starts with `/`, max 200 chars; `referrerHost`
    max 200 chars; `viewportWidth` 0–10000. Invalid JSON → 400, validation
    failure → 400 with detail, success → `204 No Content`.
  - Writes a `PageViewEntity`: `PartitionKey = "Pv|{yyyy-MM-dd}"` (daily
    partitions), `RowKey = Guid` (no collisions), fields `Path`,
    `ReferrerHost`, `ViewportWidth`; storage sets `Timestamp`. No requester
    data is derived server-side.
  - **Retention:** on write, if the last cleanup marker (`Cleanup`/`last`
    entity) is older than one day, delete all partitions older than 36 months
    in 100-row transaction batches. Cleanup failures are logged and swallowed
    (fail-open — tracking must never break because of purge errors).

### Runtime declaration

`staticwebapp.config.json` gets:

```json
{
  "platform": { "apiRuntime": "dotnet-isolated:9.0" },
  "responseOverrides": { ... existing ... }
}
```

`apiRuntime` is set to the newest managed-functions runtime available at
implementation time (`dotnet-isolated:9.0` or `10.0`); verify the currently
supported version against the SWA docs (managed-functions language support)
when implementing.

## 3. Client beacon (`Layout.astro`)

Inline script in `Layout.astro` (present on every page):

- Fire once on `window` `load`, guarded by `'sendBeacon' in navigator`.
- `navigator.sendBeacon('/api/pageview', new Blob([JSON.stringify(payload)], { type: 'application/json' }))`
  — fire-and-forget, survives page unload.
- Payload: `path: location.pathname`, `referrerHost` = origin/host of
  `document.referrer` (never the full URL; empty string if none),
  `viewportWidth: screen.width`.

## 4. Deployment workflow

`build-and-deploy-website.yml` (single job builds and deploys today):

1. Keep the Astro build as-is (working directory `./src/website`).
2. New step: `dotnet publish src/website-api/website-api.csproj -c Release -o website-api_output`
   (API project output outside the static app output).
3. In `Azure/static-web-apps-deploy@v1`: `api_location: "./website-api_output"`,
   `skip_api_build: true` (already set). The existing
   `app_location: "./src/website/dist"` and token stay unchanged.

Local development: use the SWA CLI (`swa start dist --api-location api`) or run
the function with an empty `local.settings.json` `StorageConnection`; keep a
`requests.http` file for manual endpoint testing.

## 5. Privacy page

`src/pages/datenschutz.astro` currently states "keine Telemetrie oder
Analyse-Tools" — that claim must be updated before or together with this
feature. New section (pseudonymous visit statistics):

- Recorded fields: page path, referrer host (origin only), viewport width.
- No cookies, no IP addresses, no user IDs, no user agent.
- Storage in Azure Table Storage (EU, `westeurope`), automatic deletion after
  36 months.
- Legal basis: legitimate interest (DSGVO Art. 6 lit. f).

## Milestones (tracked)

Checkboxes are updated as work progresses.

- [x] Decide final storage account name (`stliedertafel`) and retention (36 months)
- [x] Add `storage.bicep` module + `main.bicep` wiring; validate with `az bicep build` + `az deployment group what-if` (expect only `Create`)
- [x] Extend `infra-deploy.yml` deploy job to set the `StorageConnection` app setting
- [ ] Deploy infrastructure; verify storage account exists
- [x] Scaffold `api/` (csproj, Program.cs, pageview feature, local.settings.json, requests.http)
- [x] Add beacon script to `Layout.astro`; update `staticwebapp.config.json` (`apiRuntime`)
- [x] Update `build-and-deploy-website.yml` (dotnet publish + `api_location`)
- [x] Update `datenschutz.astro` (pseudonymous statistics section)
- [ ] Deploy app; verify: beacon fires, 204 returned, rows appear in the
      `pageviews` table (`az storage table query`)
- [x] Update `AGENTS.md` if repo layout changed materially (new `api/` dir)

## Verification checklist

1. `curl -i -X POST https://<site>/api/pageview -d '{"path":"/","referrerHost":"google.at","viewportWidth":1920}'`
   → `204`.
2. Invalid payloads (`{}`, path without `/`) → `400`.
3. `az storage table query --account-name stliedertafel --table-name pageviews`
   shows one entity per request with today's `Pv|yyyy-MM-dd` partition.
4. Privacy page reflects the new section; no cookies set (check DevTools /
   `curl -v` response headers).
