---
id: ARC-023
status: done
phase: core
kind: slice
depends_on: ["ARC-014", "ARC-015", "ARC-020"]
touches: ["search", "catalogue", "db-migrations"]
external_inputs: []
---

# ARC-023 — Filter to suitable arrangements with usable materials

**Depends on:** [ARC-014](ARC-014-arrangements-and-keys.md),
[ARC-015](ARC-015-private-score.md),
[ARC-020](ARC-020-catalogue-search.md).

## Outcome

A conductor filters songs by voice configuration, key, instrumentation or occasion
and sees which matching arrangement actually has the desired material.

## Acceptance criteria

- [x] Add editor fields and member filters for voice configuration, accompaniment/
  instrumentation, musical key, language, occasion/tags and available file types.
- [x] Keep unknown values explicit and optional; tags supplement structured
  relations. Duration/difficulty are not silently added.
- [x] Apply combined conditions to the matching arrangement/version where
  appropriate, not different siblings that collectively happen to satisfy them.
- [x] Material filters count authorized published/current score/audio/MIDI assets;
  reserve a recording-availability extension point for ARC-032 and store filter
  state in the URL. ARC-032 owns the actual performance-to-recording integration.
- [x] Give bounded result counts and clear/reset controls without exposing drafts.

## Verification

Use one song whose two arrangements satisfy different filters and verify it cannot
falsely match their combination. Test unknowns, hidden files, empty results, and
URL restoration on a phone.

## Frozen contract (2026-09-24)

Schema additions (new explicit migration `RepertoireFilters`, ARC-013+ migrations
untouched, Guid v7 IDs, snake_case tables):

- `Song` gains optional `Language` (≤ 200, null = unknown) and optional
  `Occasion` (≤ 200, null = unknown).
- New `SongTag`: `Id`, `SongId` (cascade), required `Value` (≤ 60), explicit
  `Position` (zero-based entry order, set by the replacement like
  `song_titles`), `CreatedAt`/`CreatedByAccountId`. Table `song_tags`, with a
  `SongId` index. Tags are a free-form supplement to the structured relations;
  they are not a controlled vocabulary.
- `Arrangement` gains optional `Accompaniment` (≤ 200, null = unknown) for
  accompaniment/instrumentation.
- Duration/difficulty are not added. Unknown values stay null; nothing is
  invented.

Editor contract (Editor/Administrator, CSRF, ProblemDetails German, `no-store`):

- `POST /api/songs` body gains `language?`, `occasion?`, `tags?: string[]`
  (trimmed, ≤ 60 each, 0–10 entries, empty entries rejected; `null` = none).
- `PATCH /api/songs/{id}` body gains `language?`, `occasion?`, `tags?:
  string[]` (full replacement like `alternateTitles`: re-add with fresh
  attribution and explicit position; empty array clears; `null` leaves the
  list unchanged; empty-string field values clear, mirroring the existing
  optional semantics). Song-level edits bump `RowVersion`.
- `POST /api/songs/{id}/arrangements` and `PATCH /api/arrangements/{id}`
  bodies gain `accompaniment?` (≤ 200, null = unknown / unchanged).
- Detail responses: song gains `language`, `occasion`, `tags: [string]`
  (position order); arrangement objects gain `accompaniment`. German
  validation: „Die Sprache ist zu lang.", „Der Anlass ist zu lang.",
  „Das Schlagwort ist zu lang.", „Ein Schlagwort darf nicht leer sein.",
  „Es sind höchstens 10 Schlagwörter möglich.", „Die Begleitung ist zu lang."

Filter contract (`GET /api/songs`, authorized like every catalogue read,
same route, `no-store`):

- New optional query parameters (trimmed; > 200 characters → 400
  „Der Filter ist zu lang."; empty string = absent): `voiceConfiguration`
  (arrangement-level), `accompaniment` (arrangement-level), `musicalKey`
  (version-level), `language`, `occasion`, `tag` (song-level), `material`
  (comma-separated material types, case-insensitive, deduplicated).
- Recognized material types today: `score`, `audio`, `midi`. Unknown values
  → 400 „Unbekannter Materialfilter." `recording` is a reserved ARC-032
  extension point, not accepted yet; the material predicate is one switch
  over asset types so ARC-032 registers there.
- Matching folds German text with `CatalogueText.Fold` on both sides and is
  substring-based; filters combine with `q` (AND) and with each other.
- Combination semantics (one matching arrangement/version, never siblings
  that collectively satisfy the conditions):
  - Song-level conditions (`language`, `occasion`, `tag`) hold on the song.
  - Arrangement-level conditions (`voiceConfiguration`, `accompaniment`)
    hold on the same arrangement.
  - Material availability counts only current revisions
    (`CurrentRevisionId != null`) of the arrangement's version assets;
    assets without a current revision never count.
  - With a `musicalKey` filter: the arrangement qualifies when one version
    carries the key AND all selected material types as current assets.
  - Without `musicalKey`: the arrangement qualifies when the arrangement
    conditions hold AND every selected material type is available as a
    current asset on some version of that same arrangement.
  - A song matches when the song conditions hold AND at least one
    arrangement qualifies; without any arrangement-level parameter the song
    matches on song conditions alone.
- Response: the existing `{ query, page, pageSize, total, songs }` shape
  gains a top-level `filters` echo `{ voiceConfiguration, accompaniment,
  musicalKey, language, occasion, tag, materials }` (absent → `null`,
  materials sorted unique) and per-song `matchedArrangements:
  [{ id, label, arranger }]` — the arrangements that satisfy the
  arrangement-level conditions (all arrangements when no arrangement-level
  parameter is present; never empty for a matched song when it is).
- Order/paging unchanged (rank then `Id` with a query, `Id` without; fixed
  pageSize 20, `Skip/Take`, beyond-end pages empty with correct `total`).
- Draft protection: members see published songs only through the shared
  `QueryVisible`/`CatalogueVisibility` decision; totals, items and matched
  hints never expose drafts (editors keep the existing draft view).

Frontend contract:

- `/lieder/` gains a collapsible filter panel next to the search composer
  with free-text fields (Stimmverteilung, Begleitung, Tonart, Sprache,
  Anlass, Schlagwort) and material checkboxes (Noten, Audio, MIDI; ARC-032
  adds recordings). Filter state lives in the German URL parameters
  `stimmbesetzung`, `begleitung`, `tonart`, `sprache`, `anlass`, `tag` and
  `material` (`noten,audio,midi`), preserved across pagination and reloads
  like `suche`/`seite` and applied with page reset through router
  navigation; „Filtern" applies, „Zurücksetzen" clears the filter
  parameters while keeping `suche`.
- Results show „Passende Fassung: …" hints (fundstelle recipe) from
  `matchedArrangements` when an arrangement-level filter is active, plus a
  bounded result-count line when searching or filtering; the empty state
  offers the reset; loading/error states follow the established patterns.
- Catalogue editing gains the new fields in the existing `LiedFormular`
  (Sprache, Anlass, Schlagwörter list) and `FassungsFormular` (Begleitung)
  patterns.

## Contract amendment (2026-09-24, committee)

List and search song items additionally carry `language`, `occasion` and
`tags` (same values as the detail response). The committee found that
catalogue-row editing otherwise replaces tag lists the editor cannot see
(the lyrics precedent only sends typed text, but a list of unseen rows
silently loses them). With the fields on the item the form prefills and
edits visibly, mirroring how composer/lyricist already behave. Existing
keys are unchanged; this is additive like the ARC-020 extensions.

## Implementation progress — 2026-09-24 (branch `main`)

Backend (`d32c6d3`, amendment `b5660aa`):

- `Song.Language`/`Song.Occasion` (≤ 200), `Arrangement.Accompaniment` (≤ 200)
  and the new `SongTag` entity (explicit `Position`, cascade, `song_tags`
  with a `SongId` index) with `CatalogueModelConfiguration` +
  `ArchiveDbContext` wiring; tool-generated additive migration
  `20260924050100_RepertoireFilters`; the apphost walking-skeleton
  pending-migrations assertion lists it.
- Editor API: POST/PATCH songs gain `language`/`occasion`/`tags` (full
  replacement like alternate titles, generalized `ValidateEntryList` keeps
  the ARC-020 messages byte-identical), arrangement create/patch gain
  `accompaniment`; detail responses carry all new fields; song-level
  `RowVersion` bumps, fresh attribution and the German validation messages
  follow the established pattern.
- Filter contract: `ParseRepertoireFilter` validates and dedupes the seven
  query parameters („Der Filter ist zu lang." / „Unbekannter Materialfilter."
  incl. the reserved `recording`), pure evaluation lives in
  `Catalogue/CatalogueFilters.cs` (`RepertoireFilter` + `RepertoireVersionRow`,
  `KnownMaterials` is the ARC-032 registration point); the endpoint projects
  one visible set and evaluates per level in C#. The plain-list and pure
  search branches keep their ARC-013/020 behaviour; the search loop moved
  verbatim into `SearchSongsAsync`. Responses carry the `filters` echo and
  per-song `matchedArrangements` in all three branches.
- `tests/archive/backend/FilterApiTests.cs`: 17 test methods (37 cases) over
  the headline sibling-arrangement case, same-version key+material
  semantics, current-revision-only materials, explicit unknowns, German
  fold equivalence, tag position round-trips, validation 400s, member/editor
  draft protection, combined query+filters, bounded pagination and the
  catalogue-item metadata amendment.

Frontend (`2adcf9e`):

- `lib/songs.ts`: `Lied` gains `matchedArrangements`, `language`, `occasion`,
  `tags`; `LiedFilter` maps the German URL parameters to the API contract
  (`noten→score` etc., trimmed, deduped; ARC-032 adds recordings there).
- `components/lieder-katalog.tsx`: collapsible „Filter" panel (same
  disclosure pattern as „Neues Lied"), open iff a filter parameter is in the
  URL; six ≤ 200 free-text fields, Material fieldset (Noten/Audio/MIDI,
  ARC-032 slot marked as a comment); „Filtern" applies with page reset,
  „Zurücksetzen" clears filters keeping `suche`; pagination/search/apply all
  build the URL through one `katalogPfad` helper; bounded count line
  („1 Lied gefunden." / „N Lieder gefunden."); „Passende Fassung: …" hints
  from `matchedArrangements` only when an arrangement-level filter is
  active; empty state offers „Filter zurücksetzen".
- `components/lied-formular.tsx` gains Sprache/Anlass (≤ 200) and the
  Schlagwörter list (0–10, ≤ 60) on the shared „Andere Titel" row pattern;
  known values are sent with, empty known values clear; create sends the
  new fields. `components/fassungs-formular.tsx` gains Begleitung (≤ 200)
  for arrangements; `lied-detail.tsx` passes the prefilled value through.
- `app/globals.css`: `.lieder-filter*` (Composer-karte recipe, existing
  tokens only, two-column grid collapsing under 540px, ≥ 2.75rem touch
  targets) and `.lieder-anzahl`/`.lieder-filter-leer` support styles.
- `tests/filter.spec.ts`: 12 mocked tests × desktop/mobile — parameter
  mapping, material translation, page reset, pagination preservation, URL
  restoration incl. reload, hints only with arrangement-level filters,
  bounded counts, empty-state reset, editor round-trips for the new fields.

## Verification — 2026-09-24

- `dotnet build src/archive/Archive.slnx` 0 errors; `dotnet test
  tests/archive/backend` **300/300 green** (263 prior + 37 new filter tests,
  including the catalogue-item metadata amendment); the ARC-020
  search suite stayed green throughout the extraction of `SearchSongsAsync`.
- Frontend `pnpm run check` clean (Biome + route types + tsc); `pnpm run
  build` statically exports all routes; the mocked Playwright suite passed
  **174/174** runnable cases (108 prior + 24 new filter cases + restyle
  suites) with no lieder/suche regressions (the six real-backend
  `shell.spec.ts` cases fail on a bare static origin as at every slice
  authoring — they pass in CI against the ASP.NET smoke).
- Real-PostgreSQL behaviour of the migration plus the walking skeleton:
  `dotnet test tests/archive/apphost` (fresh containers) **4/4 green** — the
  `RepertoireFilters` migration is asserted in the pending list and applies
  cleanly against real PostgreSQL.

## Review — 2026-09-24

Two-axis committee review (standards + spec) after each milestone; findings
fixed in place:

- Backend nits: every validation block asserts the status code before
  reading the ProblemDetails body; the mirrored match-hint list is no longer
  built for songs that already failed earlier conditions.
- Frontend major (fixed): catalogue-row tag editing silently replaced the
  unseen server tag list. List/search items now carry
  `language`/`occasion`/`tags` (contract amendment above) so the form
  prefills and edits visibly; the editor test asserts the full known-value
  round-trip and clearing.
- Frontend minors (fixed): unknown `material` URL tokens no longer count as
  active filters, the empty-state reset uses the quiet brand button, the
  Schlagwörter fieldset wears its own `.lied-listen` class, comment/copy
  polish („die Trefferzeile", „den Filterzustand", „Berührzielen").
- Accepted judgement calls: material echo is `[]` (not null) when no
  material filter is set; empty material list entries are rejected with the
  same German message; an emptied Begleitung field mirrors the pre-existing
  `arranger`/`voiceConfiguration` pattern (PATCH `null` = unchanged), so
  clearing happens with an empty string as before; the filter disclosure
  re-opens state-driven exactly like the existing editor disclosure.

## Handoff and parallel work

Recording slices register their material availability through this query contract
when integrated. Coordinate shared search SQL/UI with ARC-035; unrelated event
entry can progress in parallel.
