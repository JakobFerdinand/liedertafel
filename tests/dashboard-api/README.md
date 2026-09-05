# Dashboard API regression checks

Run the focused console harness from the repository root:

```bash
dotnet run --project tests/dashboard-api
```

No additional test framework or package is required. The harness invokes the
actual function endpoints with in-memory HTTP requests and storage fixtures,
and exercises the real reader against a synthetic SDK page stream for the
200,000-row cap. A failed assertion exits with a nonzero status.

Coverage includes Vienna daylight-saving boundaries, inclusive calendar ranges,
Monday buckets, comparison validation, zero filling, unknown classifications,
masked IDs, range-bound handles, filter semantics, cached pagination, expired
snapshots, chronological gaps, no-store headers, and cancellation.

With Azurite Table Storage running on the standard loopback port 10002:

```bash
dotnet run --project tests/dashboard-api -- --azurite
```

This additional check uses only `UseDevelopmentStorage=true`, creates a uniquely
named fixture row, reads it through the actual Azure SDK, and deletes that row
in a `finally` block. It never connects to production storage.

To export synthetic endpoint responses for browser smoke checks:

```bash
dotnet run --project tests/dashboard-api -- --fixtures /tmp/liedertafel-insights-validation
```

The exported files contain no real visitor data. Browser verification for plan
005 used these responses with temporary Playwright tooling, covering the date
controls, URL restoration, drill-down links, pagination, timeline, responsive
layout, empty/error states, and sign-in handling. No browser dependency is added
to the application.
