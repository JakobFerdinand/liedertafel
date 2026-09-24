---
id: ARC-024
status: planned
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

- [ ] Create/edit/publish events with kind, title, venue, optional time, member
  notes and source context; support concerts, services and other appearances.
- [ ] Model exact/partial/approximate dates explicitly rather than inventing a
  January 1 date; define sorting/display for partial and unknown information.
- [ ] Offer a browsable historical event list and detail page with year/period
  navigation, clear uncertainty, and useful empty sections.
- [ ] Apply editor-only mutation, shared visibility/deletion contracts and
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
