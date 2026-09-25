---
id: ARC-026
status: done
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
  display fields) and the new `rowVersion`. Amendment (review of 2026-09-25):
  the renumber commits in two explicit saves inside one transaction — the
  unique `(RevisionId, Position)` index makes an in-place renumber of swapped
  surviving entries a dependency cycle for the relational command batch, so
  phase 1 parks every entry above the current rows and phase 2 lands the
  final order; publish keeps its single atomic save.
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

## Implementation and verification (2026-09-25)

Backend (`0174a5e`):

- New entities `EventProgramme` (table `programmes`, unique `EventId`,
  attribution + application-bumped `RowVersion`), `ProgrammeRevision` (table
  `programme_revisions`, `Number` unique per programme, `PublishedAt`/
  `PublishedByAccountId` null = working draft) and `ProgrammeItem` (table
  `programme_items`, `Position` 1..n, `SongId`/`ArrangementId`/
  `MusicalVersionId`, note ≤ 500) with `EventProgrammeModelConfiguration`
  (cascade event→programme→revision→item, Restrict into the catalogue, unique
  indexes including the partial draft index on `PublishedAt IS NULL`) and the
  `ProgrammeVisibility` decision beside the entity; the DbSets are registered
  in `ArchiveDbContext`.
- `ProgrammeEndpoints.cs`: `PUT /api/events/{eventId}/programme/items` (full
  ordered replacement with stable item IDs, implicit programme + revision 1
  creation, post-publication fresh drafts seeded empty with fresh IDs,
  all-before-write validation, German 409/400 titles),
  `POST /api/events/{eventId}/programme/publish` (single atomic save: stale
  rowVersion 409, already-published 409, empty 400, member-visibility
  availability check 409 with a distinct title, stamp + `RowVersion` bump in
  one save; nothing else written — the event row and its stamps stay
  untouched, no performed markers), `GET /api/programmes` (Member+,
  precision-aware upcoming filter; soonest first, unknown last; Vienna
  calendar day) and the shared `LoadDetailEmbedAsync` embed wired into all
  four event detail responses after `documents` (members: newest published
  revision only, programme null before first publication; editors: the
  working draft too). `EventDate.Precision` deduplicated as the shared single
  source of truth.
- Tool-generated additive migration `20260925072429_EventProgrammes`; the
  walking-skeleton pending-migrations assertion lists `_EventProgrammes`.
- `tests/archive/backend/ProgrammeApiTests.cs`: 8 tests —
  `EditorSavesProgrammeItemsWithStableIdsAndReorder`,
  `DraftProgrammeStaysHiddenFromMembers`,
  `PublishStampsRevisionAndMembersSeeOrderedProgramme`,
  `SecondPublishCreatesFreshDraftAndSupersedesFirstRevision`,
  `StaleRowVersionConflictsReturn409OnSaveAndPublish`,
  `PublishRejectsUnavailableAndPrivateSelections`,
  `ValidationErrorsReturnGermanProblems`,
  `PublishingProgrammeLeavesEventRowAndSongsUnmarked`; one outdated ARC-025
  assertion in `EventAssetApiTests` honestly reworded ("no entity named
  Programme exists" → "nothing tracks performed or confirmed songs").

Frontend (`48cb8da`):

- `lib/events.ts`: `ProgrammPunkt`/`ProgrammRevision`/
  `ProgrammRevisionVeroeffentlicht`/`ProgrammEmbed`/`AuftrittDetailsMitProgramm`/
  `ProgrammListeZeile` types; `putProgrammItems` (optional rowVersion for
  implicit creation), `publishProgramm`, `fetchProgramme`,
  `programmPunktUrl` deep links (`/lied/?id=…&fassung=…&version=…`) and
  `publishedAtText`; `putAuth` added to `lib/auth.ts` on the `patchAuth`
  pattern.
- `components/auftritt-programm.tsx`: member Lesesaal (ordered
  Programmpunkte, deep-linked song titles, arrangement/version line with
  Tonart/Stimmkonfiguration only when non-null, per-entry note,
  "Veröffentlicht am …"), honest empty state; editor workbench (collapsed by
  default, per-entry `FassungsWahl`, catalogue song search, ↑/↓ reorder with
  aria-labels, remove, note ≤ 500, save with the programme rowVersion,
  publish), 409 → German copy + fresh state.
- `components/programm-liste.tsx` + `app/programm/page.tsx`: member
  directory of upcoming published programmes (server-sorted, `dateDisplay`
  with honest "Datum unsicher" marking, kind, venue/time, item count with
  plural, link to `/auftritt/?id=…`); the nav gains "Programme" →
  `/programm/`.
- `tests/programm.spec.ts`: initially 6 mocked tests × desktop + mobile.

Review findings and fixes (`349ed7c`, `d1fc955`):

- Backend review: Blocker — in-place renumbering of swapped surviving
  entries is a dependency cycle for the unique `(RevisionId, Position)` index
  on any relational provider (EF throws before SQL; InMemory tests cannot see
  it; the frontend's ↑/↓ swap produces exactly that shape) → fixed with a
  two-phase renumber in one explicit transaction (phase 1 parks every entry
  above the current rows, phase 2 lands 1..n; the InMemory test factory
  ignores the transaction warning so it works under tests). Should-fix: the
  working embed's `updatedAt` followed the frozen revision `CreatedAt` → now
  the programme aggregate's. Should-fix: new regression
  `PublishedProgrammeOnDraftEventStaysHiddenUntilEventPublish` (draft-event
  programme publication stays hidden until the event publishes); malformed
  entries (duplicated item ids, null entries) answer 400 "Ein Programmpunkt
  ist ungültig." before any write, `Assert.Single` silences xUnit2013.
- Frontend review: Blocker — the post-publication workbench started empty
  instead of seeding from the published list → now derives rows from the
  published items with `serverId` null (fresh IDs on save). Should-fix:
  sibling-triggered refetches wiped unsaved workbench rows → rows anchor to
  the programme rowVersion (resync only when it changes); failed song fetches
  are cached as a per-song "fehler" state with Erneut versuchen (no infinite
  refetch loop); Veröffentlichen refuses while unsaved edits exist; the
  post-publish class-name collision with `auftritt-dokumente.spec.ts` is
  fixed (`programm-verwaltung` only, styles in the shared recipe);
  `publishedAtText` formats in Europe/Vienna (timezoneId pinned in
  `playwright.config.ts`); the PUT/publish response embed is used directly;
  `LiedWahl` search aborts stale responses; `e13f17b` pins the Identity
  schema version in `MemberEmailChangeTests.RepairServices` so the repair
  tests no longer depend on EF's process-wide model-cache order (a
  pre-existing flake, not ARC-026 damage).

Verification evidence:

- `dotnet build src/archive/Archive.slnx` clean; `dotnet test
  tests/archive/backend` **332 passed** (322 prior + the new ProgrammeApiTests
  listed above).
- `dotnet test tests/archive/apphost` (fresh containers) **4/4 green** —
  the `EventProgrammes` migration is applied by `archive-migrate` and
  zero migrations stay pending afterwards.
- Frontend `pnpm run check` clean (Biome + route types + tsc) and
  `pnpm run build` exports `/programm`; the route-mocked Playwright spec
  passed **22/22** (11 tests × desktop + mobile), and the adjacent
  `auftritt-dokumente`/`auftritte` specs added another **28** green (50 in
  one run together).
