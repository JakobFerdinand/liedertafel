# Dashboard Deep Insights Plan

## Implementation progress

- [x] Inspect the existing API, dashboard, storage schema, access rules, and privacy copy.
- [x] Implement bounded Vienna date ranges, comparison statistics, and storage cancellation/caps.
- [x] Implement masked session summaries, pagination, and chronological details.
- [x] Build the shared client, URL state, toolbar, and modular overview.
- [x] Build session filters, list, timeline, and overview drill-downs.
- [x] Update privacy disclosure and repository guidance; verify route protection.
- [ ] Run focused API checks, dashboard check/build, website build, and available smoke checks.

### Validation log

- Backend stages: `dotnet build src/dashboard-api --no-restore` passed with zero warnings/errors.

- Frontend stage: dashboard `pnpm run check` now runs both Astro and Svelte
  diagnostics (zero errors/warnings); `pnpm run build` emits overview and session
  routes successfully. Vite emits existing plugin deprecation notices.
- Route review: `/api/*` and `/*` both require `admin`/`collaborator`; removed
  the legacy catch-all `serve`/200 settings, allowing the new static route to
  resolve normally. Global no-referrer/no-store headers protect navigation.

### Implementation decisions

- Session links use a range-bound opaque handle; raw client IDs are never put
  in URLs or default payloads. A masked label alone is not a unique lookup key.
- Grouped sessions cannot be paginated correctly using only a storage row
  continuation token: one session can span many partitions and rows. The list
  uses a bounded, five-minute in-memory snapshot and opaque continuation tokens
  identifying the last summary's storage position. Later pages do not rescan.
  Expired/evicted snapshots (including a worker restart or another instance)
  return an explicit restart response. A shared indexed projection remains the
  scaling path; no storage writes or new dependencies are introduced.
- Session detail resolves range-bound HMAC handles during the capped partition
  scan. This intentionally replaces the proposed raw-ID OData filter, avoiding
  raw IDs in requests/logs and handling arbitrary legacy ID strings safely.
  Handles are keyed from the existing storage connection, stable across worker
  instances, and become invalid on credential rotation. No reveal control is
  needed: full identifiers are never returned. Both session endpoints only read
  storage; a SessionId-indexed projection is the next scaling step.
- Long overview windows remain available up to 400 days. A drill-down preserves
  its exact dates; if over 92 days, the session page asks for a narrower window
  instead of silently changing the investigation. Device/path/source filters
  match a common observed row; reload/minimum views match the whole session.
- The visual design extends the existing palette: background `#f5f3ef`, surface
  `#ffffff`, text `#2c2a28`, muted `#6b6862`, border `#e3dfd8`, brand `#823c41`.
  System typography, left-aligned controls, a prominent comparison trend, and
  compact linked breakdowns retain the dashboard identity. Timelines emphasize
  chronological order and observed gaps rather than suggesting engagement.


## Goal

Turn the dashboard from a fixed weekly overview into a real investigation
tool: custom time ranges (default **7 days / daily granularity**), a redesigned
overview that makes changes and anomalies easy to spot, and a session
inspector that lets a person go from "something changed" to "here is the
sequence of pages that one visitor loaded" — using only the data already
being collected. Every new metric or view must be labeled for what it
actually measures; this plan does not add new tracked fields.

## Current state (verified against code, not the older plans)

- `days` accepts only `28`, `90`, or `180`; anything else — including
  `7` — silently falls back to `28`
  (`src/dashboard-api/features/pageviews/GetPageViewStats.cs:16-41`,
  `src/dashboard/src/components/PageViewStats.svelte:23,54-68`).
- Aggregation is always **Monday-start weekly buckets**, computed in UTC,
  over one raw table scan (`Pv|yyyy-MM-dd` partition range) held entirely in
  memory (`GetPageViewStats.cs:104-114,251-276,358-409`).
- Stored fields per pageview row: `Path`, `ReferrerHost`, `ViewportWidth`
  (`screen.width`, not viewport), `SessionId`, `VisitorId`, `NavigationType`,
  plus table-maintained `Timestamp` (last-write time, not a browser event
  time) and a random GUID `RowKey` (not chronological)
  (`src/dashboard-api/shared/entities/PageViewEntity.cs`).
- `SessionId`/`VisitorId` are unauthenticated, unvalidated client-supplied
  strings with **no inactivity timeout** — a tab left open for days stays one
  session (`src/website/src/layouts/Layout.astro:45-64`).
- There is exactly one event type: a pageview. No click, engagement, scroll,
  exit, error, or duration events exist anywhere in the pipeline.
- The dashboard UI is a single 516-line Svelte component with no router, no
  shared data client, no filter/session components
  (`src/dashboard/src/components/PageViewStats.svelte`).
- Auth is SWA EasyAuth (`admin`/`collaborator` roles) in front of both static
  content and `/api/*`; the function itself only declares
  `AuthorizationLevel.Function` and does not re-check roles
  (`src/dashboard/staticwebapp.config.json`).
- Both the public write API and the dashboard read API share **the same
  storage account key** — the dashboard's "read-only" behavior is an
  application convention, not a credential boundary
  (`src/website-api/Program.cs`, `src/dashboard-api/Program.cs`,
  `.github/workflows/infra-deploy.yml:130-147`).
- Azure Table Storage has one clustered index on `PartitionKey`+`RowKey`;
  filtering by `SessionId` alone is a partition scan today, not a point/range
  query (Microsoft Learn: *Design Azure Table storage for queries*).
- Azure Static Web Apps managed-function API requests are capped at **45
  seconds** and support only redirects/role rules on routes, no custom CORS
  (Microsoft Learn: *Overview of API support in Azure Static Web Apps*).

## What this plan explicitly does not do

- **No distributed/OTel tracing.** There are no span IDs, parent/child spans,
  durations, or client-to-backend trace correlation, and `sendBeacon` cannot
  carry a `traceparent` header today. What we build instead is a
  **chronological pageview timeline per session** — closer to a browser
  history list than a trace waterfall — and it is labeled that way
  everywhere in the UI and API. Real distributed tracing (span IDs, explicit
  event timestamps, engagement/exit events) is called out as a separate,
  future instrumentation project in "Out of scope" below.
- **No new personal data collection.** No IP, user agent, geolocation, or
  cross-device identity is added.
- **No change to retention** (36 months, lazy cleanup on the write path).

## Design principles for the new dashboard

1. **Every number says what it measures.** "Sessions" already means
   "distinct non-blank session IDs observed in this window", not "visits" —
   keep saying that in labels/tooltips instead of implying more precision
   than the data has.
2. **The default view answers "did anything change this week?" in one
   glance.** 7 days, daily buckets, with the *previous* 7-day period drawn
   for comparison.
3. **Drill-down, not dead ends.** Every chart/table row that identifies a
   segment (a path, a source, a device class, a day) is a link that pivots
   into "sessions matching this", not just a bigger table.
4. **Investigation ends at a real, chronological artifact**: a session
   timeline of the pages that one browser session actually loaded, in order,
   with explicit gaps and explicit "this may be truncated" notices — never a
   fabricated duration or engagement claim.
5. **The existing visual identity is extended, not replaced** — warm
   background, burgundy brand, card language, system font
   (`src/dashboard/src/styles/global.css`). New components (date picker,
   session timeline, comparison sparkline) get their own scoped styles that
   reuse the existing custom-property palette.

## Architecture overview

```
┌─────────────────────────────────────────────────────────────┐
│ Toolbar: date range + granularity + compare toggle           │
├─────────────────────────────────────────────────────────────┤
│ Overview: KPI cards w/ delta vs. previous period              │
│           Traffic trend (line, current vs. previous, zoomable)│
├───────────────────────────────┬───────────────────────────────┤
│ Pages          Sources         │ Devices        Visitors        │
│ (table + bars, each row links  │ (bars)         (new/returning) │
│  to "sessions matching this")  │                                │
├───────────────────────────────┴───────────────────────────────┤
│ Sessions: filterable, paginated list                           │
│   → Session detail: ordered pageview timeline                  │
└─────────────────────────────────────────────────────────────┘
```

Frontend gets a proper structure instead of one monolith:

```
src/dashboard/src/
  lib/
    api-client.ts       fetch wrapper, typed responses, AbortController, retry
    date-range.ts        range/granularity/timezone helpers, URL (de)serialization
    format.ts             existing de-AT number/date helpers, extracted
  components/
    DashboardNavbar.svelte        (existing, fix hydration bug)
    filters/
      DateRangePicker.svelte      presets + custom start/end + granularity
      CompareToggle.svelte
    overview/
      KpiCard.svelte               value + delta + sparkline
      TrendChart.svelte            current vs. previous period line/area
    breakdowns/
      BreakdownTable.svelte        generic ranked table w/ drill-down links
      DeviceBars.svelte
      VisitorSeriesChart.svelte
    sessions/
      SessionFilters.svelte        path/source/device/date/reload filters
      SessionList.svelte           paginated summaries
      SessionTimeline.svelte       ordered pageview detail w/ gap/reload badges
  pages/
    index.astro                    overview (unchanged route)
    sessions.astro                 new: session list + detail (query-param driven)
```

## 1. API contract redesign (`src/dashboard-api`)

### 1.1 `GET /api/pageviews/stats` — replace the days enum with real ranges

Request:

- `start`, `end` — ISO `yyyy-MM-dd`, inclusive, **required to be explicit
  after resolution**: the frontend always sends resolved dates, defaulting to
  `end = today`, `start = today - 6 days` (7 days total) in `Europe/Vienna`,
  converted to UTC partition bounds server-side.
- `granularity` — `day` | `week`, default `day`. Reject `week` spans over a
  server-defined maximum (e.g. 400 days) to bound the table scan; reject `day`
  spans over a smaller maximum (e.g. 92 days) so the response stays small
  enough to render as a daily chart.
- `compare` — `previous_period` | `none`, default `previous_period`. When
  set, the same-length immediately preceding period is computed and returned
  as a second series set.
- Validation: `end >= start`, both within the retention window, range within
  the granularity's maximum. Invalid input is a `400` with a specific German
  message — not a silent fallback to a default, so the UI never shows data
  for a range the user didn't ask for.

Response adds metadata so the UI never has to infer timezone/bucket
assumptions:

```jsonc
{
  "range": { "start": "2026-08-30", "end": "2026-09-05", "timezone": "Europe/Vienna" },
  "granularity": "day",
  "generatedAt": "2026-09-05T14:03:00Z",
  "current": { /* existing total/topPaths/series/devices/... shape,
                 with "week" renamed to "bucketStart" everywhere */ },
  "previous": { /* same shape, or omitted when compare=none */ } 
}
```

- Rename the `week` property to `bucketStart` across `series`, `pathSeries`,
  `deviceSeries`, `originSeries`, `visitorSeries` — the field already holds a
  bucket-start date; `week` is actively misleading once daily buckets exist.
- Keep zero-filling per bucket; explicitly mark the last bucket as
  `"partial": true` when `end` is today (today's data is still accumulating).
- Pass the request `CancellationToken` into `QueryAsync`/enumeration, so
  aborted dashboard requests actually stop the storage read
  (currently ignored — `GetPageViewStats.cs:380-409`).
- Add a hard server-side row cap (e.g. 200,000 entities) with a `truncated`
  flag in the response, so a very large custom range fails safely instead of
  timing out against the SWA 45-second API limit.

### 1.2 `GET /api/pageviews/sessions` — new, paginated session list

Query params: `start`, `end` (bounded, same validation as above, max 92 days
to keep the partition scan bounded), optional `path`, `originHost`,
`device`, `hasReload`, `minViews`, `cursor`, `limit` (default 25, max 100).

Behavior:

- Server-side, this is a **partition-range table scan with in-process
  grouping by `SessionId`** — there is no secondary index on `SessionId`
  today (Table Storage has one clustered index on
  `PartitionKey`+`RowKey`), so this endpoint must keep the date range capped
  and explicitly document the scan cost. Do not expose an "all time" session
  search in v1.
- Group in-memory into session summaries: `sessionId` (masked in the
  default response — see §4), `visitorId` (masked), `firstSeen`, `lastSeen`,
  `viewCount`, `distinctPathCount`, `entryPath`, `lastPath`, `reloadCount`,
  `deviceCategory` (from the first observed viewport width),
  `originHosts` (distinct, excluding internal hosts as today).
- Sort by `lastSeen` descending; cursor is an opaque continuation token
  (encode last `PartitionKey`+`RowKey` position), not an offset — offset
  pagination over an unindexed grouped scan would recompute the whole range
  per page.
- Rows without a `SessionId` are excluded from this endpoint (they already
  can't be grouped) but are surfaced as a single "ohne Sitzungs-ID" count in
  the stats response so their exclusion is visible, not silent.

### 1.3 `GET /api/pageviews/sessions/{sessionId}` — new, single-session detail

Query params: `start`, `end` (same bounded window used to find the session;
required, so a session spanning outside the searched range is explicitly
flagged as possibly truncated rather than silently completed).

Response: ordered list of observed pageviews for that exact `SessionId`
within the window — `path`, `referrerHost`, `navigationType`,
`viewportWidth`, an approximate `observedAt` (Table Storage `Timestamp`,
explicitly documented as write-time, not click-time), and the gap in seconds
since the previous row. Include `possiblyTruncatedStart` /
`possiblyTruncatedEnd` booleans when the first/last row sits at the edge of
the requested window.

This endpoint still performs a partition-range scan filtered by
`SessionId eq '...'` (a partition scan per Table Storage's own terminology,
since there's no secondary index) — acceptable at current volume, called out
as a scaling risk in §6.

### 1.4 Backward-compatible rollout

- Deploy the new endpoints and the redesigned `/stats` contract together
  (breaking the `days` param is fine — this is an internal tool with one
  consumer, the dashboard itself, redeployed in lockstep).
- Update `src/dashboard-api/requests.http` with the new query shapes,
  replacing the now-incorrect `days=7` comment.

## 2. Frontend redesign (`src/dashboard`)

### 2.1 Toolbar and default range

- `DateRangePicker.svelte`: presets **7 Tage (default) / 14 Tage / 28 Tage /
  3 Monate / 6 Monate / Benutzerdefiniert**, each preset maps to explicit
  `start`/`end` computed in `Europe/Vienna`; "Benutzerdefiniert" reveals two
  native `<input type="date">` fields (no new date-picker dependency, per
  AGENTS.md's "avoid adding dependencies unless requested").
- Granularity control: `Tag` (default) / `Woche`, disabled/forced to `Woche`
  when the selected range exceeds the daily maximum (with an inline
  explanation, not a silent switch).
- Compare toggle: "Vorperiode vergleichen" checked by default; unchecking
  removes the previous-period overlay from KPI deltas and the trend chart.
- Selected range/granularity/compare state is serialized to the URL query
  string (`?start=...&end=...&granularity=day&compare=1`) so a view is
  shareable/reloadable — currently nothing survives a reload.
- Filters remain visible during loading/error/empty states (today the range
  toggle only renders after a successful non-empty response — fix this so a
  user is never stuck without a way to pick a different range).

### 2.2 Overview page (`index.astro` + `PageViewStats.svelte` split apart)

- Add an `<h1>` (currently missing) and move the skip link before nav links
  in DOM order (existing accessibility gaps).
- KPI cards gain a delta vs. the previous period (`+12 %` / `−4 %`) and a
  small inline sparkline using the already-available Layerchart primitives —
  no new charting dependency.
- Add one large **traffic trend** panel: daily (or weekly) line/area for the
  current period, with the previous period drawn as a lighter dashed
  reference line, and today's still-accumulating bucket visually marked as
  partial (matching the `partial` flag from §1.1).
- Turn `topPaths`, `origins`, and `devices` breakdown rows into links: each
  row navigates to `/sessions?path=...` (or `?originHost=...` /
  `?device=...`), carrying the current date range along — this is the core
  "investigate further" affordance the current dashboard lacks entirely.
- Fix real metric-definition issues while touching this code:
  - Device category: add an explicit "Unbekannt" bucket for `viewportWidth
    == 0` instead of silently counting it as `Mobil`
    (`GetPageViewStats.cs` device bucketing).
  - Reload share: show both "% aller Aufrufe" and "% aller klassifizierten
    Aufrufe" side by side, since legacy/missing `navigationType` rows
    currently stay in the denominator.
  - Rename "Neu"/"Wiederkehrend" copy to reflect that it's window-relative
    (e.g. "Neu in diesem Zeitraum" / "Bereits zuvor im Zeitraum gesehen"),
    matching what the aggregation actually computes.
- Loading/error states get `aria-live="polite"` / `role="alert"`; the
  previous successful result stays visible (dimmed) while a new range loads,
  instead of the whole panel being replaced by a spinner.

### 2.3 Session investigation (`sessions.astro`, new route)

- `SessionFilters.svelte`: date range (shared component with the overview),
  plus path/source/device/reload filters; filter state also lives in the URL
  so a drill-down link from the overview lands pre-filtered.
- `SessionList.svelte`: paginated table — "Erstes Ereignis", "Letztes
  Ereignis", "Aufrufe", "Eindeutige Seiten", "Einstiegsseite", "Letzte
  Seite", "Reloads", "Gerät" — each row links to the detail view. IDs shown
  are masked (see §4) with a "Details anzeigen" affordance rather than the
  full ID by default.
- `SessionTimeline.svelte`: the actual "trace-like" view —
  a vertical ordered list of observed pageviews, each entry showing path,
  navigation-type badge (`navigate`/`reload`/`back_forward`), referrer host,
  device category, and the time gap since the previous entry. Gaps are
  labeled "Zeit seit letztem Aufruf", never "Verweildauer" — the data cannot
  support a dwell-time claim. Explicit banners cover: session possibly starts
  before/continues after the searched window; rows without navigation type
  ("unbekannt" badge).
- Empty/zero-result states explain *why* (e.g. "Keine Sitzungen mit
  Sitzungs-ID in diesem Zeitraum gefunden" vs. a generic empty box).

### 2.4 Shared client and resilience

- Extract `api-client.ts`: single `fetch` wrapper with `AbortController`,
  runtime response shape checks (at least required keys/types, not just a
  TypeScript cast), and distinct error categories (`network`, `unauthorized`,
  `invalid-range`, `server`) so the UI can react specifically (e.g. redirect
  to `/.auth/login/github` on 401 instead of a generic error card).
- Fix the existing navbar hydration bug: `DashboardNavbar` needs a
  `client:*` directive in `DashboardLayout.astro`, or its `/.auth/me` fetch
  never runs (`DashboardLayout.astro:17`, `DashboardNavbar.svelte:4-30`).
- Component `onMount`/effect cleanup aborts any in-flight request.

## 3. Backend query and performance work (`src/dashboard-api`)

- Bound every new endpoint's date range server-side (see §1.2/§1.3 maxima)
  so no request can trigger an unbounded table scan within the 45-second SWA
  API limit.
- Propagate `CancellationToken` into all `QueryAsync` calls (`/stats` today
  ignores it entirely).
- Add an application-level row cap with a `truncated` response flag (§1.1)
  rather than letting a large custom range run unbounded.
- Session grouping (`/sessions`) is O(rows in range) in memory, same
  ballpark as today's `/stats` — acceptable at current volume; document in
  code comments (not user-facing) that a `SessionId`-indexed projection
  (Table Storage "index entities" pattern) is the next step if the raw table
  grows large enough to make partition scans slow, per Microsoft's own table
  design guidance.
- No schema change to `PageViewEntity` — all new endpoints read existing
  fields only.

## 4. Privacy and access hardening (do this alongside, not after)

Session-level views meaningfully increase what a signed-in dashboard user
can see about one browser, so ship these together with the feature, not as a
follow-up:

- **Mask IDs by default.** Session list/detail responses return session and
  visitor IDs pre-masked (e.g. first 8 chars + `…`) for the identifiers used
  in URLs/labels; do not display or log full UUIDs unless a user explicitly
  expands a "vollständige ID anzeigen" control, and never send full IDs to
  any external link/referrer.
- **`Cache-Control: no-store`** on `/sessions` and `/sessions/{id}` responses
  — these are the most sensitive payloads the API returns.
- **Do not widen storage credentials.** Keep the existing shared
  account-key connection string as-is for this plan (a least-privilege
  credential split is a separate infra change); explicitly note in code
  review that these new endpoints only ever call read operations.
- **Re-verify role protection** covers the new `sessions.astro` route and
  both new API functions in `staticwebapp.config.json` (`/*` already applies,
  but confirm after adding the new static route).
- **Update the privacy page** (`src/website/src/pages/datenschutz.astro`) to
  mention that internal, authenticated dashboard users can view individual
  session pageview sequences — this is a material change from "aggregates
  only" and belongs in the disclosure, consistent with AGENTS.md's
  instruction to keep privacy copy accurate.
- Do not add a session search by raw ID across "all time" in this iteration
  (§1.2) — unbounded ID lookups over an unindexed field are both a
  performance and a privacy-scope risk; date-bounded search only.

## 5. Out of scope (future, separate plan)

Explicitly deferred, because they need new instrumentation, a new privacy
review, or both — do not blend them into this plan:

- Real distributed tracing (span IDs, parent/child spans, explicit
  client-side event timestamps, `traceparent` propagation) — would need a
  transport change (`sendBeacon` can't set custom headers) and a new event
  schema.
- Engagement/dwell-time, scroll depth, exit tracking, click/download/outbound
  link events, PDF view events, conversion tracking.
- Session replay or DOM-level reconstruction.
- Cross-device/people-based identity resolution.
- A dedicated `SessionId` secondary index/projection table (only worth it if
  raw volume grows enough to make partition scans slow).
- A least-privilege, separate read-only storage credential for the dashboard
  API (currently shares the account key with the public write API).

## 6. Risks and mitigations

| Risk | Mitigation in this plan |
| --- | --- |
| Custom long ranges cause slow/unbounded table scans, risking the 45s SWA API limit | Server-side max span per granularity, row cap + `truncated` flag, cancellation propagated |
| `SessionId` has no secondary index; `/sessions` and `/sessions/{id}` are partition scans | Date range required and capped on every session endpoint; no unbounded/all-time ID search in v1 |
| Session/visitor IDs are long-lived, unauthenticated, unvalidated strings — a "session" can span days | Every session view labels this explicitly; gaps labeled as "time since last observed event", never as engagement/duration |
| Displaying raw session/visitor IDs increases behavioral linkability for dashboard users | IDs masked by default, `no-store` on session endpoints, privacy page updated |
| Legacy rows / missing navigation type or width bias metrics | Explicit "unbekannt" buckets and dual (all vs. classified) reload percentages instead of hiding the gap |
| Breaking the `/stats` contract affects only the dashboard's own consumer | Deploy dashboard API + UI together; update `requests.http` |
| New date/session UI could regress existing accessibility gaps | `aria-live` states, `<h1>`, skip-link order fix, and native date inputs (no new dependency) folded into this same plan |

## Rollout order

1. `src/dashboard-api`: new `/stats` contract (range/granularity/compare,
   `bucketStart` rename, partial-bucket flag, cancellation, row cap) —
   ship with `requests.http` updated.
2. `src/dashboard-api`: new `/sessions` and `/sessions/{id}` endpoints, ID
   masking, `no-store` headers.
3. `src/dashboard`: extract `api-client.ts`/`date-range.ts`, fix navbar
   hydration, split `PageViewStats.svelte` into the component tree in §2,
   wire the new `/stats` contract with the 7-day/daily default and URL state.
4. `src/dashboard`: `sessions.astro`, `SessionList`, `SessionTimeline`, and
   drill-down links from every overview breakdown.
5. `src/website/src/pages/datenschutz.astro`: disclose session-level
   dashboard access.
6. `AGENTS.md`: note the new dashboard routes/components if the layout
   section becomes stale.

## Verification

- `cd src/dashboard && pnpm run check && pnpm run build`
- `dotnet build src/dashboard-api` (or open `liedertafel.slnx`)
- Manual smoke test against local Azurite/dev storage:
  - Default load shows 7 days, daily buckets, matching `start`/`end` in the
    URL.
  - Switching to "Benutzerdefiniert" with an invalid range (`end < start`)
    surfaces the specific validation error, not a silent fallback.
  - A path/source/device row link lands on `/sessions` pre-filtered.
  - Opening a session detail shows pageviews in chronological order with
    correct gap calculations and a truncation banner when the window cuts
    off the session.
  - A session with no `navigationType`/`viewportWidth` shows the "unbekannt"
    badges instead of being miscategorized.
  - Reload the page mid-investigation (URL alone) and confirm state is
    restored.
- Manually confirm `/api/pageviews/sessions*` responses return masked IDs
  and `Cache-Control: no-store`.

Style constraints from AGENTS.md apply throughout (tabs, no comments, German
copy, existing design tokens, no new dependencies unless explicitly agreed).
