# Choir archive

ARC-001 establishes the local walking skeleton. The frontend uses Next.js App
Router with static export (replacing the plan's Vite choice at implementation
request). ASP.NET Core serves the exported assets and `/api/*` in production;
Next.js/Turbopack supplies hot reload and a same-origin API proxy in development.
Server-only Next.js features are outside this hosting contract.

The archive targets .NET 10 LTS. The root SDK uses `10.0.100` with
`latestFeature` roll-forward; existing Functions applications retain their
runtime targets and their CI also installs the pinned SDK.

## Start from a clean checkout

Prerequisites: .NET 10 SDK, Node.js 24 LTS, pnpm 11.3.0, and a running Docker
or Podman engine. AppHost restores frontend packages and starts the containers;
no Azure account or manually started database is needed.

From the repository root:

```bash
dotnet run --project src/archive/apphost
```

For Podman, prefix the command with `ASPIRE_CONTAINER_RUNTIME=podman`.
Open the dashboard login URL printed by AppHost, then the `archive-frontend`
HTTP endpoint. Open `/system/status/` directly and refresh it. Select **Lokale
Dienste prüfen** to query PostgreSQL and exercise Blob, queues and Mailpit.
Open `archive-mail`'s HTTP endpoint to see **Archiv: lokaler Funktionstest**.
Ctrl+C stops AppHost and its resources. Local PostgreSQL/Azurite volumes persist.

The launch profile uses **loopback HTTP**, including OTLP; dashboard token/API-key
authentication stays enabled. This avoids requiring trusted development TLS
certificates. Production cookies are Secure; local cookies use SameAsRequest.

## Resources and configuration contract

| Resource | Contract |
| --- | --- |
| `archive-postgres` / `archive-db` | PostgreSQL 17.6, database `archive`, Aspire-generated local password, named volume |
| `archive-storage` | Azurite 3.35.0, named volume, Blob and Queue endpoints |
| `archive-blobs` | Injects `ConnectionStrings__archive-blobs` |
| `archive-queues` | Injects `ConnectionStrings__archive-queues` |
| `archive-mail` | Mailpit 1.27.8, HTTP inbox and SMTP; injects `Mail__Host` / `Mail__Port` |
| `archive-storage-init` | Automatic finite local command; creates private `archive-assets` container and `archive-work` queue |
| `archive-api` | HTTP `/api/*`, process-only `/alive`; waits for dependencies and successful storage setup |
| `archive-frontend` | Next.js/Turbopack; waits for API; receives `PORT` and server-only `ARCHIVE_API_URL` |
| `archive-migrate` | **Explicit Start**; only resource receiving `ConnectionStrings__archive-migrations` |
| `archive-worker-smoke` | **Explicit Start**; finite Blob/queue/mail verification with correlated producer/consumer tracing |
| `archive-mail-test` | **Explicit Start**; ARC-010 integration run with Aspire telemetry: sends the marked German test message through the real Azure sender. Requires `Mail:Provider=Azure` plus `Archive:MailTestRecipient` in AppHost configuration and refuses otherwise |

`WithArchiveDependencies` owns reference injection and readiness for local C#
workers. Use `WithExplicitStart()` for operator-triggered work, and
`WaitForCompletion(archive-storage-init)` before touching local storage. The
smoke command is a contract example; real import/extraction work belongs to its
feature slice. `Archive:PersistLocalData=false` gives integration tests fresh,
ephemeral container data. Stop a running local AppHost before integration tests:
both use the same frontend working directory.

Mail transport selection (ARC-010): `Mail:Provider` defaults to `Smtp`, so
ordinary startup stays on Mailpit capture via the injected
`Mail__Host`/`Mail__Port`. Setting `Mail:Provider=Azure` (AppHost user secrets
or environment, never committed) routes the backend through Azure
Communication Services Email with the verified choir-domain sender; the local
authorized credential is `Mail:AzureConnectionString`, while production sends
keyless through the runtime managed identity. Production refuses to boot on
`Smtp` and refuses sender secrets outright. The same `--send-test-mail
<address>` operator command backs `archive-mail-test`; it requires the Azure
provider and Development.

The application uses `ConnectionStrings:archive-db` on demand, with a pool of
at most five connections, zero minimum connections, five-second connect timeout,
ten-second command timeout and no database keep-alive. `/alive`, `/health` and
`/api/build` never query dependencies. `/api/dev/database` is development-only
and reports connectivity plus pending migrations. There is no schema polling.

Local Data Protection keys are in ignored `src/archive/.local/keys`, with the
application discriminator `Liedertafel.Archive.Development`. These must never
be copied into production. Persistent production keys and actual member cookies
are owned by the later authentication/cloud slices.

The diagnostic uses fixed `.test` mail addresses. It creates a unique scratch
queue and a `diagnostics/<id>.txt` blob, round-trips them and removes them with
a bounded cleanup token. It never consumes messages from `archive-work`.
AppHost injects emulator connections; there are no production storage credentials
or `UseDevelopmentStorage=true` shortcuts in application configuration.

## Schema ownership and explicit commands

Start **archive-migrate** in the dashboard once after first startup. It must
finish with exit code 0. The same resource can be started after adding migrations.
The initial empty EF migration establishes migration history only; no catalogue,
membership or other product schema is pre-created. API startup never applies it.

If the Aspire CLI is installed, the dashboard actions also have CLI equivalents:

```bash
aspire resource archive-migrate start --apphost src/archive/apphost/Archive.AppHost.csproj
aspire resource archive-worker-smoke start --apphost src/archive/apphost/Archive.AppHost.csproj
```

To author a migration, run from `src/archive`:

```bash
dotnet tool restore
# This design-time placeholder is enough to generate a migration; it does not connect.
env 'ConnectionStrings__archive-migrations=Host=localhost;Database=archive;Username=postgres' \
  dotnet ef migrations add MeaningfulFeatureName --project backend --output-dir Data/Migrations
```

Inspect the generated migration before applying it. EF Core alone owns schema
changes; each feature adds its entities/migrations in `backend/Data/Migrations`.
For a standalone migration process, supply the actual migration-role connection
through environment configuration and run:

```bash
dotnet run --project src/archive/backend --no-launch-profile -- --migrate
# The production image has the same entry point:
docker run --rm --env ConnectionStrings__archive-migrations liedertafel-archive:local --migrate
```

An inherited `DOTNET_ENVIRONMENT=Development` also requires OTLP configuration;
normally the explicit local AppHost resource supplies it. Local PostgreSQL uses
its isolated admin role for both paths. Production must supply distinct migration
and least-privilege runtime roles, as specified by the architecture. Migration
failure returns nonzero; no startup or release loop automatically retries it.

## Frontend and HTTP contract

Next.js 16.3.5, React 19.2.8, TypeScript, Tailwind 4 and Biome were generated with:

```bash
npx create-next-app@latest frontend --ts --tailwind --biome --app --no-src-dir --turbopack --import-alias "@/*" --use-pnpm --yes --disable-git
```

The .NET projects were created with `dotnet new web`, `aspire-apphost`,
`aspire-servicedefaults`, `xunit` and `aspire-xunit`, all targeting `net10.0`.
Aspire SDK/integrations/testing are pinned to 13.5.3, EF/EF tools to 10.0.12,
and the Npgsql EF provider to 10.0.3. Lockfiles record the frontend resolution.

- Browser calls use relative `/api/*` URLs. Only the development server reads
  `ARCHIVE_API_URL`; no `NEXT_PUBLIC_*` backend address is needed.
- The development rewrite forwards cookies, `Set-Cookie`, request bodies,
  `X-CSRF-TOKEN`, and API status/content types. There is no permissive CORS policy.
- GET `/api/antiforgery` supplies the request token and HTTP-only SameSite cookie.
  POST `/api/dev/exercise` validates both before side effects. These diagnostics
  are absent in production. They do not implement member authentication.
- `pnpm run build` uses `output: "export"` and emits `out/`. ASP.NET serves each
  generated route's `index.html`, including `/system/status/`, its scripts and
  Next navigation payloads. Unknown API paths always return Problem Details;
  missing pages/assets remain 404s.
- New pages must support static export. Use client-side API calls for data and
  query parameters for runtime IDs unless a route can be generated at build time.
  Server Actions, SSR, API route handlers and unbounded dynamic App Router routes
  require an explicit hosting decision. Do not add a generic index fallback:
  it would hydrate the wrong App Router route.
- Production has no Node.js process. `next start` is deliberately not a script.
  Development output `.next-dev` is separate from production `.next` / `out`.

## Telemetry and finite workers

Every .NET host calls `AddServiceDefaults()`. Development startup fails clearly
if `OTEL_EXPORTER_OTLP_ENDPOINT` is missing. `UseOtlpExporter()` sends **logs,
traces and metrics**, consuming AppHost's endpoint, protocol and auth headers.
`OTEL_SERVICE_NAME` distinguishes API, storage setup, migrations and workers.
The API exports `archive.build.requests` plus HTTP/runtime metrics, and logs a
structured build-request event. Health probes are excluded from traces.

Workers start a normal Generic Host, then use `RunArchiveJobAsync`. It creates
the job activity, records completion/handled failure, flushes traces, metrics and
logs (five-second budget per provider), stops the host and returns a process exit
code. Dispose the host afterwards. Shutdown has a 30-second budget. Later queue
envelopes carry W3C `traceparent` / `tracestate`; consumers extract the remote
parent, as demonstrated by `LocalServices`. Do not propagate arbitrary baggage.

URL queries are redacted before export, including Azure spans. AppHost's query
redaction opt-outs are overridden. Do not log codes, cookies, credentials, message
bodies or signed URLs. Shared HttpClient defaults add service discovery, **not
automatic retries**; each feature chooses safe operation-specific resilience.

To inspect actual export after a browser check:

1. Dashboard **Structured logs**, resource `archive-api`: find
   `Archive build information requested` and follow its trace.
2. **Traces**: inspect `GET /api/build`, then the diagnostic request with storage,
   queue producer/consumer and mail spans. Run `archive-worker-smoke` and inspect
   the finite `worker-smoke` trace and its completion log after the process exits.
3. **Metrics**, resource `archive-api`, meter `Liedertafel.Archive`: select
   `archive.build.requests`. Refresh the frontend and allow five seconds for
   another metric export. HTTP/runtime instruments are also present.

## ARC-015 private score assets

One private score per musical version flows through a three-step upload
protocol: `POST /api/musical-versions/{id}/assets` creates the logical asset,
`POST /api/assets/{id}/upload-session` issues a pending upload session with a
bounded write ticket, the browser PUTs the file directly to that ticket URL,
and `POST /api/upload-sessions/{id}/finalize` validates existence, size and the
`%PDF-` magic bytes before promoting the object server-side into an immutable
file revision (idempotent retry returns the same revision). Reads go through
`GET /api/assets/{id}/access`, which checks membership and song visibility and
returns blob-scoped 15-minute view/download ticket URLs; ticket URLs exist only
in JSON responses and are never logged.

The storage/ticket adapter (`IAssetStorageAdapter`,
`BlobAssetStorageAdapter`) lives in `backend/Assets/AssetStorage.cs` and is
configured under `Archive:Assets` (`ContainerName` defaults to
`archive-assets`, max upload size, upload-session and read-ticket lifetimes).
It uses the injected `ConnectionStrings__archive-blobs` connection string.
Locally the container and a permissive emulator CORS rule for direct browser
transfers are created by `archive-storage-init` (`Development/LocalServices.cs`,
`AssetStorageEmulatorBootstrap`); production CORS and the keyless identity
switch (user-delegation SAS via managed identity, ARC-049) are documented
configuration points and not yet wired.

The revision promotion is a server-side blob copy whose source is a signed
ticket URL. The emulator fetches that source from inside its own container, so
the AppHost pins the Azurite blob host port to the container port
(`WithBlobPort(10000)`); the ticket host then resolves in both directions.
Integration tests must pass `DcpPublisher:RandomizePorts=false` for the pin to
apply (testing mode randomizes proxied ports by default).

## Focused verification

From the repository root:

```bash
dotnet build src/archive/Archive.slnx
dotnet test tests/archive/backend
dotnet test tests/archive/apphost
```

The AppHost xUnit test needs Docker/Podman and pnpm. It enables the actual Aspire
dashboard/export endpoint, starts fresh containers, checks the deep link and
proxy, verifies migrations are initially pending and explicitly applies them,
tests Blob/queues and cookie/CSRF, runs the worker and asserts two captured mails.
The backend xUnit project is container-free, including an authenticated local OTLP
receiver test of all three signals and flush/redaction on success and failure.

From `src/archive/frontend`:

```bash
pnpm install --frozen-lockfile
pnpm run check
pnpm run build
pnpm exec playwright install chromium
ARCHIVE_BASE_URL=http://localhost:<frontend-port> pnpm run test:browser
```

Browser checks cover direct deep links, refresh and client navigation, actual API
version rendering, mobile layout, API error boundaries, retry after an outage,
and development diagnostics through the proxy. To run one test use
`pnpm run test:browser -- --grep "German deep link"`.
To focus xUnit use `dotnet test tests/archive/backend --filter FullyQualifiedName~TelemetryTests`.

Build/smoke the production image from the repository root:

```bash
docker build -f src/archive/Dockerfile --build-arg VERSION=0.1.0 \
  --build-arg REVISION="$(git rev-parse HEAD)" -t liedertafel-archive:local .
# ARC-010: Production refuses to boot on local SMTP capture. The endpoint is
# never contacted here (/alive, /api/build and static assets stay
# dependency-free; the mail transport builds lazily on first send).
# ARC-011: Production additionally requires Blob/Key Vault key persistence;
# the smoke stays dependency-free with the ephemeral test escape.
# ARC-011-1: Production additionally requires a passkey relying-party ID; the
# value is unused by the smoke (passkey ceremonies skip in production).
docker run --rm -d --name archive-smoke -p 127.0.0.1:18080:8080 \
  -e Mail__Provider=Azure \
  -e Mail__AzureEndpoint=https://acs-liedertafel-test.communication.azure.com \
  -e Authentication__AllowEphemeralKeysForTests=true \
  -e Authentication__PasskeyRelyingPartyId=archiv.liedertafel.test \
  liedertafel-archive:local
curl --fail http://localhost:18080/alive
docker exec archive-smoke sh -c '! command -v node && id -u'
```

Then run the same browser command with `ARCHIVE_BASE_URL=http://localhost:18080`
from the frontend directory, and `docker stop archive-smoke` when finished.
No database, storage, SMTP, OTLP endpoint or development environment is supplied
to this smoke container (only the Azure mail provider selection above, which is
never contacted by the smoke). For Podman builds, additionally pass
`--ignorefile src/archive/Dockerfile.dockerignore`; Docker automatically uses the
Dockerfile-specific ignore file. The final image runs as the .NET non-root user.

`.github/workflows/archive.yml` runs these checks and the production smoke
on archive changes (its release job then reuses the checked image to deploy). The full repository SDK compatibility check is
`dotnet build liedertafel.slnx`; existing analytics checks remain
`dotnet run --project tests/dashboard-api`.

See [ARC-001 verification evidence](../../docs/plans/006-choir-archive/ARC-001-local-walking-skeleton.md#implementation-and-verification)
for the recorded local run.
