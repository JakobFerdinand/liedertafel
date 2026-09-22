---
id: ARC-020
status: done
phase: core
kind: slice
depends_on: ["ARC-013"]
touches: ["search", "catalogue", "app-shell", "db-migrations"]
external_inputs: []
---

# ARC-020 — Find a song by title, creator or remembered words

**Depends on:** [ARC-013](ARC-013-first-published-song.md).

## Frozen contract (2026-09-22)

Catalogue model extension (backend `Catalogue/`, Guid v7 IDs, snake_case tables):

- New `SongTitle`: `Id`, `SongId` (cascade), required `Value` (≤ 200),
  `CreatedAt`/`CreatedByAccountId`. Table `song_titles`. Alternate titles are
  stored in entry order (Guid v7 IDs keep insertion order stable).
- `Song.Lyrics`: optional entered lyrics/opening words, ≤ 5000 characters,
  table column `songs.lyrics`.
- Explicit migration `SongAlternateTitlesAndLyrics`.

Editing contract (Editor/Administrator, CSRF, ProblemDetails German):

- `PATCH /api/songs/{id}` body gains `lyrics?` (≤ 5000, optional) and
  `alternateTitles?: string[]` (full replacement, 0–10 entries, each trimmed
  and required ≤ 200; `null` leaves the list unchanged). Empty strings inside
  the list are rejected; clearing uses an empty array.
- `GET /api/songs/{id}` detail gains `lyrics` and `alternateTitles: [string]`.

Search contract (authorized like every catalogue read):

- `GET /api/songs?q=<text>&page=<n>` extends the existing list endpoint —
  no separate search route and no dedicated search service. `q` optional
  (trimmed, ≤ 200), `page` optional (default 1, values below 1 clamped),
  fixed `pageSize` 20.
- Response: `{ query, page, pageSize, total, songs: [...] }`. `total` counts
  the member-visible (see `CatalogueVisibility`) result set. Each song item
  keeps the existing summary shape and adds `alternateTitles: [string]`,
  `arrangements: [{ id, label, arranger }]` (all arrangements of the song,
  ordered by id), `matchedIn: [string]` — sorted keys among `title`,
  `alternateTitles`, `composer`, `lyricist`, `lyrics`, `arrangements`, empty
  without a query — and `lyricsSnippet` (`null` in this slice; PDF/lyrics
  excerpt matches arrive with ARC-033 through this same contract).
- German text behaviour: matching folds case and umlauts on both sides —
  lowercase, `ä→ae`, `ö→oe`, `ü→ue`, `ß→ss`, then `ae→a`, `oe→o`, `ue→u`, so
  „Müller", „Mueller" and „Muller" all match each other. The query is split on
  whitespace; every token must occur (substring) in at least one of title,
  alternate titles, composer, lyricist, lyrics, arrangement label or arranger.
- Determinism: no query orders by `Id` as before; with a query, rank first
  (0 = exact folded title, 1 = all tokens in title, 2 = match elsewhere),
  then `Id`. Paging is `Skip/Take` on this stable order; pages beyond the end
  return an empty `songs` list with the correct `total`.
- Draft protection: members query the same visible set as catalogue reads;
  drafts cannot appear in `songs`, `total`, `alternateTitles`, `arrangements`
  or any future snippet. Editors/administrators keep the existing draft view.
- Member catalogue text scale (a choir, hundreds of entries) lets matching run
  over one database-projected visible set in C# so InMemory tests and real
  PostgreSQL behave identically; no PostgreSQL-specific search feature is
  introduced in this slice.

Frontend contract:

- `/lieder/` gains search-first behaviour: a search field and deterministic
  pagination driven by URL parameters `suche` (query) and `seite` (page),
  preserved with `router` navigation like the existing `fassung`/`version`
  deep links; loading/empty/error states follow the established patterns.
- The home screen becomes search-first with a form that navigates to
  `/lieder/?suche=…`, so members search from the home without duplicating
  the catalogue UI.
- Catalogue editing gains lyrics (textarea) and alternate-title list editing
  in the existing `LiedFormular` pattern.

## Outcome

An editor enters alternate titles/creators/lyrics, and a member finds the correct
catalogue entry from the search-first home using those words.

## Acceptance criteria

- [x] Extend catalogue editing for alternate titles, composer/arranger/lyricist
  information and entered lyrics/opening words where not already present.
- [x] Implement bounded, database-backed search with deterministic pagination,
  useful German text behaviour, and matching arrangement context in results.
- [x] Preserve query state in navigation and provide usable loading/empty/error
  states on phone and desktop.
- [x] Share publication/deletion authorization with catalogue reads; draft text
  cannot leak through results, result counts or snippets.
- [x] Establish an authorized search-result contract that can later include PDF
  matches without replacing this initial searchable journey.

## Verification

Search title variants, creator names, German umlauts and entered opening words
against public/draft fixtures. Verify deterministic results, limits and query
restoration; no dedicated search service is introduced.

## Handoff and parallel work

ARC-021 adds filters and ARC-033 adds PDF text. Coordinate catalogue metadata and
home-screen edits with ARC-014/029; file-transfer implementation can proceed separately.

## Implementation progress — 2026-09-22 (branch `main`)

Backend (`be870b6`):

- `Catalogue/SongTitle.cs` (Guid v7, cascade, `Value` ≤ 200, attribution) and
  `Song.Lyrics` ≤ 5000; `CatalogueModelConfiguration` configures `song_titles`
  with a `SongId` index; `ArchiveDbContext` gains `DbSet<SongTitle>`.
- Tool-generated migration `20260922110543_SongAlternateTitlesAndLyrics`
  (`songs.lyrics varchar(5000)`, `song_titles` table); the apphost
  walking-skeleton pending-migrations assertion now lists it.
- `Catalogue/CatalogueText.cs` implements the frozen German fold (umlaut
  expansion then digraph collapse) as a pure helper; matching in
  `CatalogueEndpoints` runs over one database-projected visible set (token AND
  across fields, rank 0 exact folded title / 1 all tokens in title / 2
  elsewhere, `Skip/Take` paging, fixed pageSize 20, page clamp, `total`,
  `matchedIn`, `lyricsSnippet: null`), with `Cache-Control: no-store` and the
  shared `QueryVisible`/`CatalogueVisibility` decision point.
- Detail gains `lyrics`/`alternateTitles`; `PATCH /api/songs/{id}` gains
  `lyrics?` and `alternateTitles?` (full replacement, 0–10 entries, fresh
  attribution, RowVersion bump, unchanged 409 message). `q` > 200 answers 400
  „Die Suche ist zu lang."; punctuation-only queries match nothing.
- `tests/archive/backend/SearchApiTests.cs`: 16 tests over umlaut equivalence
  („Müller"/„Mueller"/„Muller"), alternate titles, creators, lyric words,
  member-vs-editor draft protection for results and totals, deterministic
  pagination (21 songs → 20+1, beyond-end empty, clamped pages), cross-field
  token AND, PATCH round-trips and German validation titles.

Frontend (`607a444`):

- `lib/songs.ts` extends `Lied`/`LiedDetails` with the new fields and adds
  `fetchSongSearch` (same-origin, `no-store`, throws `Response`).
- `components/lieder-katalog.tsx`: search-first catalogue driven by the
  `suche`/`seite` URL parameters (navigation like the existing
  `fassung`/`version` deep links), fixed pageSize 20 with „Zurück"/„Weiter"
  pagination showing „Seite X von Y", German loading/empty/error states with
  the retry pattern, „Auch bekannt als …" lines and matched-in hints
  („Getroffen: Fassung …", „Getroffen im Liedtext."), editor draft badge and
  controls unchanged on the new shape.
- `components/suche-formular.tsx` + home `app/page.tsx`: the home screen opens
  with a search form that navigates to `/lieder/?suche=…&seite=1`.
- `components/lied-formular.tsx`: edit form gains the optional „Liedtext"
  textarea (≤ 5000) and an „Andere Titel" dynamic list (0–10 entries); the
  create request body is unchanged. `lieder-suche*`/`lieder-seiten*` CSS
  classes extend the existing tokens with a small-screen media query.
- `tests/suche.spec.ts`: 8 mocked Playwright tests × desktop/mobile over home
  navigation, request mapping (`?q=…`/`page=…`), query restoration on reload,
  match hints, empty/error/retry states, pagination, editor drafts and the
  lyrics/alternate-title edit flow.

## Verification — 2026-09-22

- `dotnet build src/archive/Archive.slnx` 0 errors; `dotnet test
  tests/archive/backend` **214/214 green** (198 prior + 16 new search tests).
- Frontend `pnpm run check` clean (Biome + route types + tsc); `pnpm run
  build` statically exports all routes; mocked Playwright suite **108/114
  green** with the remaining 6 failures being the real-backend `shell.spec.ts`
  smoke tests (no dev backend during authoring — same record as ARC-019).
- Real-PostgreSQL behaviour of the migration and apphost integration checks
  were not executed locally (Podman integration run left for CI/release);
  the in-memory tests cover the fold/rank/pagination logic provider-independently.

## Review — 2026-09-22

Two-axis review (standards + spec) against the ARC-013 baseline fixed three
follow-ups (commit `f603952`):

- Lyrics can now be cleared through the UI: the detail view (which knows the
  previous text) sends an empty string on an emptied field, while catalogue
  editing without a known previous value only sends typed text — the earlier
  form always sent `null` (= „unverändert"), making lyrics settable but
  never removable.
- The lyrics textarea gained `maxLength={5000}` to match the other inputs.
- The duplicated per-song response shape in the two `GET /api/songs` branches
  is now one shared `SongItem` helper with a typed `ArrangementSummary`, and
  the search branch reuses `LoadAlternateTitlesAsync` instead of an inline
  duplicate query. JSON keys are unchanged; backend tests stayed green.

Accepted judgement calls: the `matchedIn` field keys stay as contract-documented
strings (no separate service introduced), the arrangement match hint names the
first arrangement (the contract carries no per-arrangement hit information),
and stale beyond-end page URLs simply show the empty state.
