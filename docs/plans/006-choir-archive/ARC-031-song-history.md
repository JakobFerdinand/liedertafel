---
id: ARC-031
status: done
phase: core
kind: slice
depends_on: ["ARC-028"]
touches: ["song-history", "performances", "catalogue"]
external_inputs: []
---

# ARC-031 — See a song's history with honest performance totals

**Depends on:** [ARC-028](ARC-028-performance-evidence.md).

## Outcome

A member opens a song and sees where it is documented, with confirmed
performances separated from uncertain programme evidence.

## Acceptance criteria

- [x] Add song/arrangement history views with links to events, evidence/source
  context, known/unknown arrangements and exact/approximate dates.
- [x] Show separate confirmed and unconfirmed counts qualified as based on
  recorded history; do not imply 120 years of complete data.
- [x] Count stable performance occurrences rather than evidence documents or
  recordings. Preserve genuine repeated performances within one event.
- [x] Apply publication/deletion checks to rows and aggregate counts, with
  deterministic ordering and bounded pagination.
- [x] Provide an extension slot keyed by performance ID for upcoming recording
  links; this slice is useful without recording indexing.

## AI assistance

Counts and dates stay plain database queries. Post-launch increment, not
required for the launch gate:

- [ ] Generate a short written history per song from its recorded performances
  and evidence, with citations to the events, keeping confirmed and uncertain
  evidence distinct.
- [ ] The text is a draft in "Vorschläge" until an editor approves it; members
  see only the approved text. It is stored, never generated on page view, and
  regenerated as a new draft only when the underlying evidence changes.

Depends on ARC-013-1 and ARC-021-1 for this increment. Decisions: [architecture §14](architecture.md#14-ai-assistance).

## Verification

Use mixed evidence, uncertain dates, duplicate supporting sources, repeats and
hidden events. Verify totals, filters and links across song/arrangement views.
Changing one occurrence's evidence status changes the right total once.

## Handoff and parallel work

ARC-032 supplies passage links through the performance slot. This history view
can proceed while concert playback is built; coordinate the shared song page
with catalogue/search contributors.

## Implementation and verification (2026-10-05)

Scope: the non-AI core. The two "AI assistance" checkboxes (generated written
history, approval in "Vorschläge") stay open: they are post-launch and depend on
ARC-013-1 and ARC-021-1, which are not done.

Contract (decisions made in this slice):

- `GET /api/songs/{songId}/performances[?page=&evidence=&arrangementId=]`
  (`backend/Events/SongHistoryEndpoints.cs`) replaces ARC-028's editor-only
  groundwork route on the same path and is readable by every active member
  (401 anonymous/revoked, `no-store`, German ProblemDetails). An unpublished
  or unknown song is an indistinguishable 404 for members (editors read draft
  songs). `evidence` is `confirmed|mention`, `arrangementId` a song arrangement
  or `unknown` (400 / 404 with German titles otherwise). Fixed page size 20
  (`page`/`pageSize`/`total`, the catalogue convention); ordering is a total
  order: known years newest first, month/day descending, unknown year last,
  ties grouped by event then captured position then id.
- Rows are ARC-028 performance rows, so `id` is the stable performance id the
  recording slot (ARC-032) hangs on. Per row: event link data, German
  `dateDisplay`, `datePrecision`, `dateUncertain` (only an exact, non-approximate
  day is certain, the event page rule), `evidenceStatus`, `origin`
  (`programme` for confirmation-owned rows, `record` for hand-entered ones),
  `arrangement`/`musicalVersion` as `{id,label}` or explicit `null` ("Fassung
  unbekannt"), `occurrence {index, of}` among the confirmed rows of this song at
  the event (genuine repeats stay separate rows), `possiblyDuplicate`,
  `alsoConfirmedAtEvent`. Members get no source note, no audit fields, no plan,
  revision or confirmation ids; editors additionally get `sourceNote`.
- Counting is plain row counting, never documents, retries or recordings:
  `counts.confirmed {occurrences, events, uncertainDates, possiblyDuplicate}`
  and `counts.unconfirmed {occurrences, events, onlyEvents, uncertainDates}` are
  separate and never summed. Honest handling of the ARC-029 ambiguity: a
  hand-entered confirmed row next to a confirmation-owned confirmed row of the
  same song at one event is neither merged nor hidden; it is flagged
  `possiblyDuplicate` and counted in `confirmed.possiblyDuplicate`, so the
  confirmed total reads as an upper bound. A mention at an event where the song
  is confirmed is flagged `alsoConfirmedAtEvent`; `unconfirmed.onlyEvents` counts
  events whose only evidence is a mention. `arrangements[]` and
  `unknownArrangement` give per-arrangement counts (independent of the filters).
- Visibility: only member-visible (published) events are listed for members and
  counted for everyone. Editors also see rows of unpublished events, flagged
  `eventPublished: false`, excluded from every aggregate and reported as
  `counts.draftEventOccurrences` (null for members). The predicate lives in
  `EventVisibility.OfMemberVisibleEvents`; ARC-040 trash adds its condition
  there. Access is decided fresh per request (downgraded editors lose drafts and
  notes immediately, revoked users get 401).
- No migration: the model is unchanged
  (`dotnet ef migrations has-pending-model-changes --project backend`: no changes).

UI: `components/lied-historie.tsx` ("Aufführungsgeschichte", on the song page
below Material), `lib/historie.ts`, CSS block in `app/globals.css`. Qualified
counts ("Die Zahlen beruhen nur auf dem, was das Archiv bisher dokumentiert
hat ..."), separate Bestätigt/Unbestätigt figures, explicit empty state ("Das
heißt nicht, dass es nie gesungen wurde."), Nachweis and Fassung filters,
Seite x von y, "Datum unsicher", "Fassung unbekannt", repeat and duplicate
hints. Extension slot: the component takes an optional `erweiterung(zeile)`
render prop keyed by the row's performance id (each item carries
`data-performance-id`); it renders nothing when absent, so no "keine Aufnahmen"
claim is invented before ARC-032.

Verification (actual commands/results):

- `dotnet test tests/archive/backend --filter FullyQualifiedName~SongHistoryApiTests`:
  first run red (403 from the old editor-only route), then green; final 6
  passed (member counts/ordering/chain/repeat, owned vs hand-entered flags and
  downgrade counting once, skipped songs create no history, hidden events and
  draft songs, anonymous/downgraded/revoked callers, paging/filters/arrangement
  overview). A temporary mutation (member visibility filter off, drafts counted)
  failed 2 of them as intended.
- `dotnet build src/archive/Archive.slnx`: 0 warnings, 0 errors.
- `dotnet test tests/archive/backend`: **505 passed**, 0 failed (499 + 6; one
  assertion in `PerformanceApiTests` moved from 403 to 200 for members because
  the song history is now member-readable).
- `corepack pnpm run check`: clean; `corepack pnpm run build`: static export ok.
- Playwright against the static export (`python3 -m http.server 3111 --directory out`,
  `ARCHIVE_BASE_URL=http://localhost:3111`, `--workers=1`): `lied-historie.spec.ts`
  10/10 (5 tests x desktop + mobile, mocks in the real response shape);
  regression on every spec that opens `/lied/`: `lieder`, `materialien`, `noten`,
  `noten-verlauf`, `audio-wiedergabe`, `midi-wiedergabe`, `extraktion`, `filter`,
  `auftritt-belege`, `auftritt-bestaetigung`, `programm`, `chat` all green
  (`materialien.spec.ts` needed a mock for the new history request: its
  page-wide "Erneut versuchen" locator collided with the unmocked error state).
- Not run: `dotnet test tests/archive/apphost` and `shell.spec.ts` (need Docker
  stack / real backend). The history query is only exercised on InMemory; it uses
  plain projections (`p.Event.PublishedAt != null`, `p.ConfirmationId != null`,
  `isEditor ? p.SourceNote : null`) and sorting/paging run in C#.

Known limits: the whole set of a song's rows is materialized per request
(bounded by one song's history, projections only); no event-side "all songs
sung" aggregate; members do not see per-row source notes (ARC-028 contract), so
mention rows carry only their evidence label; counts are not cached.

Handoff: ARC-032 renders passage/recording links through `erweiterung` keyed by
the row `id` and may add a `recordings` field to the row projection; ARC-022-1
chat tools should reuse this endpoint's visibility rules and counts wording
(never sum confirmed and unconfirmed); ARC-040 must extend
`EventVisibility.OfMemberVisibleEvents` so trashed events leave rows and counts.

## Review follow-up (2026-10-05)

An independent review of `1582a4e` found no blockers (and confirmed the history
query translates and runs server-side on PostgreSQL). Fixed in the follow-up commit:

- An unconfirmed row never reads as confirmed: the UI shows "Im Programm
  bestätigt" only for confirmed programme rows; a programme-origin row
  downgraded to a mention reads "Programmangabe · Aus dem Programm". The API
  keeps `origin: programme` (it records how the row arose) and its doc says to
  read it together with `evidenceStatus`.
- A possible duplicate is never asserted as a repeat: new per-row flag
  `possiblyDuplicateAtEvent` (set on every confirmed row of an event where any
  confirmed row is `possiblyDuplicate`). Affected rows read "N bestätigte
  Einträge an diesem Auftritt – möglicherweise doppelt erfasst."; the
  "Mehrmals ... Aufführung i von n" wording remains only for genuine repeats.
- One shared date order: `EventDate.CompareNewestFirst` (day precision before
  month-only before year-only within a year) is used by the event list
  (`CompareEvents`) and the song history (`CompareRows`); a backend test orders
  same-year mixed precision identically in both.
- Editors: when drafts exist, a line above the list says N entries belong to
  unpublished events and are in no number and no arrangement figure (from
  `draftEventOccurrences`, also under a filter). Counts stay published-only.
- A page past the end is clamped server-side to the last page (the response
  reports the clamped `page`); the UI adopts it, so "Seite 3 von 2" cannot occur.
- Page/filter changes show `aria-busy` on the list region plus a visible
  "Wird aktualisiert …" hint instead of silently keeping old rows.
- Not done: an automated check of the `erweiterung(zeile)` slot. It has no
  consumer until ARC-032, and a throw-away consumer would test only itself.

Recorded limits: members see the evidence label and origin but no per-row
source note (ARC-028 contract); there is no separate arrangement history page,
only the Fassung filter plus per-arrangement counts on the song page; the whole
song's rows are materialized per request (projections only, then sorted and
paged in C#).

Verification (actual commands/results):

- Backend tests first (red against the pre-change code: 4 of 7 failed — chain
  flags/paging/ordering/downgrade pair), then green.
- `dotnet build src/archive/Archive.slnx`: 0 warnings, 0 errors.
- `dotnet test tests/archive/backend`: **506 passed**, 0 failed (505 + the
  same-year precision test; further assertions added to existing tests).
- `corepack pnpm run check`: clean; `corepack pnpm run build`: static export ok.
- Playwright (static export on port 3111, `--workers=1`): `lied-historie.spec.ts`
  16/16 (8 tests x desktop + mobile; new: downgraded programme row and genuine
  repeat, clamped page, loading state, editor draft hint under a filter);
  `lieder`, `materialien`, `auftritte`, `chat` 102/102; `programm` 28/28. The
  new Playwright cases were written alongside the UI change, so no separate red
  run was recorded for them.
