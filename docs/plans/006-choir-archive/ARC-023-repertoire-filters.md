---
id: ARC-023
status: planned
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

- [ ] Add editor fields and member filters for voice configuration, accompaniment/
  instrumentation, musical key, language, occasion/tags and available file types.
- [ ] Keep unknown values explicit and optional; tags supplement structured
  relations. Duration/difficulty are not silently added.
- [ ] Apply combined conditions to the matching arrangement/version where
  appropriate, not different siblings that collectively happen to satisfy them.
- [ ] Material filters count authorized published/current score/audio/MIDI assets;
  reserve a recording-availability extension point for ARC-032 and store filter
  state in the URL. ARC-032 owns the actual performance-to-recording integration.
- [ ] Give bounded result counts and clear/reset controls without exposing drafts.

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

## Handoff and parallel work

Recording slices register their material availability through this query contract
when integrated. Coordinate shared search SQL/UI with ARC-035; unrelated event
entry can progress in parallel.
