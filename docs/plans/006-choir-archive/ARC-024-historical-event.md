---
id: ARC-024
status: done
phase: core
kind: slice
depends_on: ["ARC-005"]
touches: ["events", "app-shell", "db-migrations"]
external_inputs: []
---

# ARC-024 — Publish an event even when its historical date is uncertain

**Depends on:** [ARC-005](ARC-005-email-code-sign-in.md).

## Outcome

An editor records a concert or other choir appearance, including one known only
as approximately 1950, and a member browses it in the event history.

## Acceptance criteria

- [x] Create/edit/publish events with kind, title, venue, optional time, member
  notes and source context; support concerts, services and other appearances.
- [x] Model exact/partial/approximate dates explicitly rather than inventing a
  January 1 date; define sorting/display for partial and unknown information.
- [x] Offer a browsable historical event list and detail page with year/period
  navigation, clear uncertainty, and useful empty sections.
- [x] Apply editor-only mutation, shared visibility/deletion contracts and
  contributor attribution. Event publication is distinct from future setlists.

## Verification

Publish exact-date and approximate-year fixtures, browse/sort them, and verify
member API access cannot reveal drafts. Check an event can be useful without
its programme, attachments or recordings entered yet.

## Handoff and parallel work

Publish stable event IDs, date representation, visibility and extension slots.
This slice runs beside catalogue work; it enables documents, historical evidence,
future programmes and recordings without requiring their complete schemas first.

## Frozen contract (2026-09-24)

Terms: the section is "Auftritte", one record is an "Auftritt". Events are the
choir's concerts, services, weddings, funerals, festivals and other appearances
(PRD §6). Programmes (ARC-026), documents (ARC-025), evidence (ARC-028) and
recordings (ARC-032) attach to these stable IDs later; publication of an event is
independent of a published programme.

Schema (new explicit migration `HistoricalEvents`, additive only, Guid v7 IDs,
snake_case table `events`, attribution/concurrency per the catalogue pattern):

- New entity `Archive.Backend.Events.ChoirEvent`: `Id`, `Kind` (closed set:
  `concert`, `service`, `wedding`, `funeral`, `festival`, `other`; ≤ 20 chars),
  `Title` (required ≤ 200), `Venue` (optional ≤ 200), `StartTime`
  (optional `HH:mm`, ≤ 10), `Notes` (optional ≤ 2000, member-visible practical
  notes), `SourceNote` (optional ≤ 500, source context), `DateYear`
  (int 1800–2100, nullable), `DateMonth` (int 1–12, nullable), `DateDay`
  (int, valid in month/year, nullable), `DateApproximate` (bool), attribution
  (`CreatedAt`/`CreatedByAccountId`, `UpdatedAt`/`UpdatedByAccountId`),
  `PublishedAt`/`PublishedByAccountId` (null = draft) and `RowVersion`
  (application-bumped concurrency token). Index on `DateYear`.
- Date model is explicit, no invented calendar dates: precision is derived from
  which parts are set — day present = exact, month present = month precision,
  year present = year precision, nothing present = unknown. Only the
  combinations (year), (year, month), (year, month, day) and (nothing) are
  valid; month without year or day without month is rejected, and the day must
  exist in that month. `DateApproximate` marks "um/ca." ("approximately 1950")
  independently of precision. Deletion stays with ARC-040 trash; this slice
  adds the shared visibility decision only.

API (ProblemDetails German, `no-store`, CSRF on mutations, fresh
`ArchiveAccessService` decision per request; members 401 when signed out,
403 on editor-only actions, drafts answer an indistinguishable 404 for members):

- `GET /api/events` (`?year=`, `?kind=`) returns `{ years: [{ year, count }],
  total, events: [item] }` — all member-visible events (unpaginated, archive
  scale), sorted newest first; within a year month/day entries come before
  undated ones; unknown-year events sort last under year `null`. `years` is
  the distinct-year summary of the visible set for navigation.
- `GET /api/events/{id}` returns `{ event: detail }` (adds `notes`,
  `sourceNote`, `createdAt`, `updatedAt`, `publishedAt`).
- `POST /api/events` creates a draft. `PATCH /api/events/{id}`: absent fields
  unchanged, empty strings clear; the nested `date` object is an explicit
  full replacement of the date block (`null` year = unknown date).
- `POST /api/events/{id}/publish` and `/unpublish` are idempotent, mirroring
  the catalogue: first publication stamp kept on republish, unpublish clears
  both stamps.
- Item/detail shape: `id`, `kind`, `title`, `venue`, `dateYear`,
  `dateMonth`, `dateDay`, `dateApproximate`, `datePrecision`
  (`day|month|year|unknown`), `dateDisplay` (server-rendered German: exact
  "12. Mai 1950", "Mai 1950", "1950", approximate "um 1950"/"um Mai 1950"/
  "ca. 12. Mai 1950", unknown "Datum unbekannt"), `startTime`, `published`.

Frontend (static export, client-side fetch, query-parameter detail route):

- `/auftritte/` list with year navigation (year rail with counts, "Ohne Jahr"
  group), kind labels, editor draft badges and Veröffentlichen/Zurückziehen,
  plus a "Neuer Auftritt" form for editors.
- `/auftritt/?id=` detail with uncertainty kept honest (approximate/unknown
  dates marked as such, no fake precision), venue/time/notes/source, and
  useful empty sections for the later programme, documents and recordings
  slices. `/auftritt/` stays outside the nav section like `/lied/`.
- Nav gains "Auftritte" pointing at `/auftritte/`.

## Implementation and verification (2026-09-24)

Backend (`afe6306`):

- `ChoirEvent` with `EventModelConfiguration` (snake_case table `events`,
  field maximums per the schema, application-bumped `RowVersion` concurrency
  token, `DateYear` index), the `Events` DbSet and `Program.cs`
  `MapEventEndpoints()` wiring; the closed `EventKinds` set and the shared
  `EventVisibility` decision live beside the entity. `EventDate.cs` renders
  the culture-invariant German `dateDisplay` with hardcoded month names so
  server output is identical across hosts ("12. Mai 1950", "um Mai 1950",
  "um 1950", "ca. 12. Mai 1950", "Datum unbekannt").
- Endpoints: `GET /api/events` materializes one visible set and filters and
  sorts in C# (unpaginated, precision-aware newest-first — day precision
  before month-only before year-only within a year, unknown years last) so
  InMemory tests and PostgreSQL agree; the `years` navigation summary
  ignores the filters and ends with the `year: null` group.
  `GET /api/events/{id}`, editor-only `POST`/`PATCH` (present-empty strings
  clear, the nested `date` is an explicit full replacement, 409 on
  concurrent edits) and idempotent `publish`/`unpublish` follow the
  catalogue pattern; members get 403/401, drafts an indistinguishable 404.
- Tool-generated additive migration `20260924100654_HistoricalEvents`
  (earlier migrations untouched, snapshot extended); the apphost
  walking-skeleton pending-migrations assertion lists `_HistoricalEvents`.
- `tests/archive/backend/EventsApiTests.cs`: 8 tests —
  `MemberAndAnonymousCannotCreateEvents`,
  `DraftStaysHiddenFromMembersAndVisibleToEditors`,
  `PublishStampsAttributionAndUnpublishClearsIt`,
  `SecondEditorPatchAttributesAndKeepsPublicationStamps`,
  `ListSortsNewestFirstWithYearsSummaryAndFilters`,
  `ValidationErrorsReturnGermanProblems`,
  `PatchClearsOptionalsAndReplacesDateBlock`,
  `EventDateDisplayCoversAllPrecisions`.

Frontend (`058a4ad`):

- `app/auftritte/page.tsx` and `app/auftritt/page.tsx` (compact intros,
  Suspense shells, query-parameter detail route outside the nav like
  `/lied/`); `components/haupt-navigation.tsx` gains "Auftritte".
- `components/auftritte-bereich.tsx`: the year rail is the section's
  navigation (real URL selection via `?jahr=`, "Alle", year tiles with
  counts, "Ohne Jahr" picks the API's `year: null` group client-side),
  member list with kind labels and honest date column
  (`data-unbestimmt`), editor draft badges, inline Bearbeiten and
  Veröffentlichen/Zurückziehen; creating refetches the whole list so sort
  and the years summary stay true.
- `components/auftritt-detail.tsx`: uncertainty marked honestly (`Datum
  unsicher` badge whenever `dateApproximate` or the precision is not
  `day`), Hinweise/Quelle from the detail shape, and the always-present
  empty Programm/Dokumente/Aufnahmen sections reserved for ARC-025
  documents, ARC-026 programmes and ARC-032 recordings.
- `components/auftritt-formular.tsx`: the date precision emerges from the
  filled year/month/day fields (empty = unknown date, no invented calendar
  date; client validation mirrors the server: 1800–2100, day in month,
  HH:MM); the editor PATCH always carries the full `date` block and its
  month labels match the server's hardcoded month names.
- `lib/events.ts` types the item/detail/years shapes and provides
  `fetchEvents`/`fetchEvent` on the established German-error pattern.
- `app/globals.css`: the Auftritte block (year rail tiles in the
  `.fassungs-liste` recipe, tabular counts, stacked entries below 700 px)
  with existing tokens only.
- `tests/auftritte.spec.ts`: 8 mocked tests × desktop/mobile — year rail
  selection and "Ohne Jahr" honesty, deep-linked detail with indistinguishable
  drafts, uncertainty badges, editor publish/unpublish round-trip, create
  with partial and unknown dates, PATCH date-block replacement and the
  outage retry.

Verification evidence:

- `dotnet build src/archive/Archive.slnx` clean;
  `dotnet test tests/archive/backend` **308 passed** including the 8 new
  `EventsApiTests` listed above.
- `dotnet test tests/archive/apphost` (fresh containers) **4/4 green** —
  the `HistoricalEvents` migration is applied by `archive-migrate` and
  zero migrations stay pending afterwards.
- Frontend `pnpm run check` clean (Biome + route types + tsc); `pnpm run
  build` statically exports the new routes; the mocked Playwright suite
  against the dev server passed with the auftritte spec **16/16**
  (8 tests × desktop + mobile) and the full suite green apart from the
  environmental `shell.spec.ts` cases, which need the real backend as at
  every slice authoring.

Two-axis committee review after the slice; findings fixed in place:

- Major blocker (fixed): the editor form rejected an empty year outright, so
  an unknown date could never be saved and an existing "Datum unbekannt"
  entry could not even be edited without inventing a year. The form now
  accepts empty fields as the unknown date, always carries the full `date`
  replacement block (`null` year = unknown date) and the mocked test asserts
  the round-trip both ways.
- Minors (fixed): the month select labels follow the server's hardcoded
  culture-invariant month names, creating an entry refetches the list so
  the years summary stays true, and the detail-shape assertions cover the
  Hinweise/Quelle rendering from the detail response.
