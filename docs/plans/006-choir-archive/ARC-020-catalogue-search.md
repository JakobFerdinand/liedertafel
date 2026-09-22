---
id: ARC-020
status: planned
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

- [ ] Extend catalogue editing for alternate titles, composer/arranger/lyricist
  information and entered lyrics/opening words where not already present.
- [ ] Implement bounded, database-backed search with deterministic pagination,
  useful German text behaviour, and matching arrangement context in results.
- [ ] Preserve query state in navigation and provide usable loading/empty/error
  states on phone and desktop.
- [ ] Share publication/deletion authorization with catalogue reads; draft text
  cannot leak through results, result counts or snippets.
- [ ] Establish an authorized search-result contract that can later include PDF
  matches without replacing this initial searchable journey.

## Verification

Search title variants, creator names, German umlauts and entered opening words
against public/draft fixtures. Verify deterministic results, limits and query
restoration; no dedicated search service is introduced.

## Handoff and parallel work

ARC-021 adds filters and ARC-033 adds PDF text. Coordinate catalogue metadata and
home-screen edits with ARC-014/029; file-transfer implementation can proceed separately.
