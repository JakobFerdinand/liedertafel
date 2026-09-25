---
id: ARC-026
status: planned
phase: core
kind: slice
depends_on: ["ARC-014", "ARC-024"]
touches: ["programmes", "events", "db-migrations"]
external_inputs: []
---

# ARC-026 — Publish an ordered programme for an upcoming appearance

**Depends on:** [ARC-014](ARC-014-arrangements-and-keys.md),
[ARC-024](ARC-024-historical-event.md).

## Outcome

An editor orders songs for an upcoming event, chooses their actual musical
versions, and explicitly publishes the programme members should follow.

## Acceptance criteria

- [ ] Add/remove/reorder entries using stable item IDs and arrangement/version
  selection, preserving repeated songs as distinct programme entries.
- [ ] Introduce separate working/published revision identities from the first
  publication; save a draft without making it member-visible.
- [ ] Publish atomically with a timestamp; members see ordered items, event details
  and notes with links to the selected musical version's current materials.
- [ ] Validate unavailable/private selections and stale edits on publication.
  Merely passing the event date does not mark its songs performed.
- [ ] Make upcoming published programmes accessible from member navigation.

## Verification

Build and publish a three-song programme, including a transposed version and a
repeated song. Verify member views, stable ordering/IDs, rejected stale changes,
and absence of draft exposure before the first publication.

## Handoff and parallel work

Publish revision/item and arrangement-picker contracts. ARC-027 extends revision
editing; ARC-029 later confirms actual performances. Historical entry ARC-028
is independent and must not share planned/confirmed state accidentally.

## Frozen contract (2026-09-25)

Builds on ARC-013/014 (Guid v7 IDs, snake_case tables, German ProblemDetails,
`no-store`, CSRF on mutations, Editor/Administrator writes via
`ArchiveAccessService` + role check) and ARC-024 (stable event IDs, event
publication independent of the programme).

Terms: the ordered song list of an appearance is the "Programm"; one entry is
a "Programmpunkt". The same song may appear twice as two distinct entries
(distinct stable item IDs). A programme belongs to exactly one event.

Schema (new explicit migration `EventProgrammes`, additive only, Guid v7 IDs,
snake_case tables, attribution/concurrency per the catalogue pattern where the
programme is the aggregate root — child edits bump the programme `RowVersion`,
the event row stays untouched, like ARC-025 asset attaches):

- `EventProgramme` (table `programmes`): `Id`, `EventId` (unique FK to
  `ChoirEvent`), `CreatedAt`/`CreatedByAccountId`, `UpdatedAt`/
  `UpdatedByAccountId`, application-bumped `RowVersion` (concurrency token for
  every programme/revision/items mutation). One programme per event, created
  implicitly by the first save.
- `ProgrammeRevision` (table `programme_revisions`): `Id`, `ProgrammeId` (FK),
  `Number` (int, unique per programme, starting at 1), `PublishedAt`
  (null = working draft revision), `PublishedByAccountId`, `CreatedAt`/
  `CreatedByAccountId`. From the first publication the programme carries
  separate working/published revision identities: at most one revision per
  programme has `PublishedAt = null` (the working draft); members see only the
  newest published revision (highest `Number` among published ones). Older
  published revisions remain frozen history for ARC-027; there is no unpublish
  endpoint in this slice — superseding means publishing a newer revision.
- `ProgrammeItem` (table `programme_items`): `Id`, `RevisionId` (FK), `Position`
  (1-based, contiguous within the revision), `SongId`, `ArrangementId`,
  `MusicalVersionId` (the musical version determines its arrangement parent),
  `Note` (optional ≤ 500, member-visible practical note for the entry). Unique
  index `(RevisionId, Position)`. Stable item IDs: within one working
  revision, unchanged entries keep their item ID across saves (full ordered
  replacement semantics update in place); repeated songs are distinct items.

API (ProblemDetails German, `no-store`, CSRF + antiforgery on mutations,
Editor-only writes via the shared decision, member 403/anonymous 401):

- `PUT /api/events/{eventId}/programme/items` (Editor): full ordered
  replacement of the working draft's items — body
  `{ items: [{ id?, songId, musicalVersionId, note? }], rowVersion }`. The
  array order defines `Position` 1..n; entries with an existing item ID update
  in place (stable IDs), entries without are created, missing IDs are removed.
  When no programme exists yet, programme + first draft revision (Number 1)
  are created implicitly. When no working revision exists (after a
  publication), a fresh draft revision is created and every requested item is
  a new entry (fresh IDs); the client edits from the fetched published list.
  `rowVersion` must match the programme's — mismatch answers 409
  "Der Programmentwurf wurde zwischenzeitlich geändert." (stale edits).
  Validation per item: referenced song/arrangement/version must exist and
  belong together (400 "Ein Programmpunkt verweist auf eine nicht vorhandene
  Liedfassung."), note ≤ 500 (400). Draft selections are allowed while
  drafting; member-visibility is enforced at publish. Response: the programme
  embed with the fresh working revision (items carry IDs, positions, computed
  display fields) and the new `rowVersion`.
- `POST /api/events/{eventId}/programme/publish` (Editor): body
  `{ rowVersion }`. Publishes atomically in one save: stale `rowVersion` → 409
  (as above); no working revision → 409 "Das Programm wurde bereits
  veröffentlicht."; zero items → 400 "Das Programm enthält keine Lieder.";
  every item's musical version (with its arrangement and song) must still
  exist and be member-visible (song published) → 409 "Das Programm enthält
  nicht verfügbare Liedfassungen." Otherwise the working revision is stamped
  `PublishedAt`/`PublishedByAccountId` (kept forever) and the programme
  `RowVersion` bumps — the stamp and the bump commit together. Merely passing
  the event date does NOT mark songs performed: no performance records are
  written, the event row (and its own publish stamps) stays untouched.
- Reads: `GET /api/events/{id}` detail embeds `programme` after `documents`.
  Members receive only the newest published revision (or nothing — an absent
  programme before first publication is indistinguishable from no programme);
  editors additionally receive the working draft and the newest published
  revision. Embed shape: `{ id (programme id), working: { id (revision id),
  number, updatedAt, items: [item] } | null, published: { id (revision id),
  number, publishedAt, items: [item] } | null, rowVersion }` (working/published
  nulled per role). Item shape: `id`, `position`, `songId`, `songTitle`,
  `arrangementLabel`, `voiceConfiguration` (null preserved),
  `musicalVersionLabel`, `musicalKey` (null preserved), `note` (null
  preserved). Members link the item themselves to the selected musical
  version's current materials: `/lied/?id={songId}&fassung={arrangementId}&version={musicalVersionId}` — the ids ride the embed
  (the programme item therefore also carries `arrangementId` and
  `musicalVersionId`).
- `GET /api/programmes` (Member+): upcoming published programmes —
  `{ programmes: [{ eventId, eventTitle, kind, dateDisplay, datePrecision,
  dateApproximate, venue, startTime, publishedAt, itemCount }] }`. "Upcoming"
  is an honest precision-aware decision: exact dates from today on, month
  precision from the current month on, year precision from the current year
  on, and unknown dates count as upcoming (they cannot honestly be called
  past). Sorted soonest first; unknown dates last.

Frontend (static export, client-side fetch, existing tokens only):

- `components/auftritt-programm.tsx` takes over the ARC-024 empty Programm
  section in `auftritt-detail.tsx`. Member view: ordered Programmpunkte with
  position numbers, the song title linking to the deep-linked song page
  (`/lied/?id=…&fassung=…&version=…`), the arrangement/version line (labels,
  Tonart, Stimmkonfiguration) and the per-item note; honest empty section
  before the first publication. Editor view: a compact workbench — per-entry
  arrangement/version choice via the reusable `FassungsWahl`, add/remove/
  reorder (↑/↓) with stable item IDs, per-item note, save (PUT) with the
  programme `rowVersion`, 409 handling that reloads the fresh state and asks
  the editor to redo the change, and Veröffentlichen with the 409/400 copy.
  The event row's own publish/unpublish stays untouched by all of this.
- `app/programm/page.tsx` + `components/programm-liste.tsx`: member list of
  upcoming published programmes (date display, venue/time, title, item count,
  link into `/auftritt/?id=…`), honest about unknown/approximate dates.
  `haupt-navigation.tsx` gains "Programme" pointing at `/programm/`.
- `lib/events.ts` types the programme embed, the item shapes and
  `ProgrammListe`, and provides `putProgrammItems`, `publishProgramm` and
  `fetchProgramme` on the established German-error pattern.
- `app/globals.css` adds the Programm block (ordered entries in the
  `.fassungs-liste` recipe family, editor workbench rows, stacked entries on
  phones) with existing tokens only.

Verification: three-song programme including a transposed version and a
repeated song; member views, stable ordering/IDs across saves, rejected stale
changes (PUT and publish), absence of draft exposure before the first
publication, and no performed markers from passing event dates.
