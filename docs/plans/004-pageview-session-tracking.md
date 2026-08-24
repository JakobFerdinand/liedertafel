# Pageview Session Tracking Plan

## Goal

Improve the quality of the pageview data so that real session analysis is
possible and noise is removed. Today every full page load beacons one event,
with no session or visitor identity, no reload marker, and no gating against
preview deployments. Consecutive duplicate paths (`/120-jahre` →
`/120-jahre`) are reloads or clicks on the current page and cannot currently
be distinguished from genuine navigation.

Decisions made:

- **Full pipeline**: website snippet, website API, dashboard API, dashboard UI.
- **Reloads are counted but marked** via a navigation type field, so they can
  be filtered in analysis instead of being dropped at the source.
- **Session ID (sessionStorage) + anonymous visitor ID (localStorage)**.
  The localStorage identifier requires a correction of the privacy page.

## Current state

- `src/website/src/layouts/Layout.astro`: inline script fires one beacon per
  `load` event. Payload: `path`, `referrerHost`, `viewportWidth`.
- The site is a classic MPA (no `ClientRouter`), so one beacon per navigation
  is correct behavior by design.
- `src/website-api/features/pageviews/PageView.cs` validates the payload and
  appends an entity with a GUID row key; a lazy cleanup job prunes rows older
  than 36 months.
- `src/dashboard-api/features/pageviews/GetPageViewStats.cs` aggregates raw
  rows into weekly series (paths, devices, origins).
- Observed issues from a data review (Aug 17–23): dev traffic from
  `github.com` referrers and PR preview URLs
  (`*.azurestaticapps.net`) lands in production data; sessions can only be
  reconstructed with a 30-minute-gap heuristic.

## 1. Website tracking script (`src/website/src/layouts/Layout.astro`)

Rewrite the inline script (currently lines 26–47):

- **Production gating**: only beacon when `location.hostname` is
  `liedertafel-mining.at` or `www.liedertafel-mining.at`. Excludes PR preview
  SWA deployments and localhost.
- **`sessionId`**: random UUID via `crypto.randomUUID()`, stored in
  `sessionStorage` (key e.g. `lt-session`); one per tab session.
- **`visitorId`**: random UUID stored in `localStorage` (key `lt-visitor`);
  persisted across visits.
- **`navigationType`**: `performance.getEntriesByType('navigation')[0]?.type`
  fallback `'navigate'`; values `navigate` | `reload` | `back_forward`.
- **Path normalization**: strip trailing slash except for root `/`.
- Wrap both storage accesses in try/catch (private browsing modes may throw)
  and degrade gracefully by sending without IDs.

Keep `navigator.sendBeacon`, same-origin endpoint, and the existing payload
style. No new dependencies.

## 2. Website API (`src/website-api`)

- Extend `PageView.Payload` with optional `SessionId`, `VisitorId`,
  `NavigationType`.
- Validation: max length 64 for both IDs; `navigationType` checked against the
  whitelist above; all three optional so old clients keep working.
- Extend `PageViewEntity` with nullable `SessionId`, `VisitorId`,
  `NavigationType` properties.
- Azure Table Storage is schemaless — **no migration needed**; existing rows
  simply read back as null.
- RowKey stays a GUID (append-only); retention cleanup unchanged.

## 3. Privacy page (`src/website/src/pages/datenschutz.astro`)

Mandatory because Variant B contradicts the current text:

- Line 14 currently claims "no cookies and no local storage" — must be
  corrected.
- Section 6 must disclose: pseudonymous, purely random identifiers kept in the
  browser's local/session storage on the device; still no cookies, no IP
  addresses, no user agent; users can remove them by clearing browser data;
  legal basis remains Art. 6(1)(f) GDPR.

## 4. Dashboard API (`src/dashboard-api/features/pageviews/GetPageViewStats.cs`)

- Extend the local `PageViewEntity` copy with the new nullable fields.
- New aggregations:
  - **Sessions**: count of distinct non-null `SessionId` (legacy null rows are
    ignored for this KPI).
  - **Pages per session**: total views / sessions.
  - **New vs. returning visitors** per week: first occurrence of a
    `visitorId` within the window vs. earlier occurrence inside the window.
  - **Reload share**: share of events with `navigationType == "reload"`.
- Extend `StatsResponse` accordingly.

## 5. Dashboard UI (`src/dashboard/src/components/PageViewStats.svelte`)

- Add KPI cards: Sessions, pages per session, returning visitor share, reload
  share.
- Update types to match the extended response.

## Rollout order

All new fields are optional on both ends, so each step deploys safely
independently (the API ignores unknown JSON fields; old entities read as
nulls):

1. Website API
2. Website tracking script
3. Privacy page
4. Dashboard API
5. Dashboard UI

## Verification

- `cd src/website && pnpm run build`
- `cd src/dashboard && pnpm run check && pnpm run build`
- `dotnet build src/website-api` and `dotnet build src/dashboard-api`
  (or open `liedertafel.slnx`)
- Manual smoke test: run the website dev server, confirm in the browser
  network tab that the beacon contains session/visitor IDs and navigation
  type, and that no beacon is sent when the hostname is gated out.

Style constraints from AGENTS.md apply throughout (tabs, no comments, German
copy on user-facing pages, no new dependencies).
