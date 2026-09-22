---
id: ARC-013
status: done
phase: core
kind: slice
depends_on: ["ARC-005"]
touches: ["catalogue", "app-shell", "db-migrations"]
external_inputs: []
---

# ARC-013 — Publish the first song with its musical identity

**Depends on:** [ARC-005](ARC-005-email-code-sign-in.md).

## Outcome

An editor creates a song with one arrangement and one labelled musical version,
publishes it, and a member finds it in the catalogue and opens its page.

## Acceptance criteria

- [x] Implement creation/editing, persistence, draft/published visibility and a
  member catalogue/detail route through the real API and database.
- [x] Give Song, Arrangement and MusicalVersion distinct stable IDs from the
  start; a single initial arrangement/version is enough for this slice.
- [x] Capture title, optional creator information and clear arrangement/version
  labels; allow explicitly missing optional information.
- [x] Record actor/time for edits, enforce Editor/Administrator writes, and deny
  draft reads through member API requests as well as navigation.
- [x] Define publication/deletion/reference contracts so later files, history and
  trash share one visibility decision rather than reproducing it inconsistently.

## Verification

Create and publish with an Editor, browse as a Member, then verify direct draft
access and member writes fail. Reload from the database and test duplicate titles
without incorrectly treating a title as a unique song identifier.

## Handoff and parallel work

Publish stable catalogue IDs, visibility checks, detail-page extension points and
edit attribution. ARC-014/015/020 can branch from this slice; events ARC-024 can
already run beside it. Cloud deployment is not a prerequisite for this local flow.

## Frozen contract (2026-09-19)

Entities (backend `Catalogue/`, Guid v7 IDs, snake_case tables):

- `Song`: `Id`, required `Title` (≤ 200), optional `Composer`/`Lyricist`
  (≤ 200 each), `PublishedAt`/`PublishedByAccountId` nullable (draft = null),
  `CreatedAt`/`CreatedByAccountId`, `UpdatedAt`/`UpdatedByAccountId`,
  `RowVersion` concurrency token. Table `songs`.
- `Arrangement`: `Id`, `SongId` (cascade), required `Label` (≤ 200),
  optional `Arranger` (≤ 200), `CreatedAt`/`CreatedByAccountId`. Table
  `arrangements`.
- `MusicalVersion`: `Id`, `ArrangementId` (cascade), required `Label`
  (≤ 200), optional `Creator` (≤ 200), `CreatedAt`/`CreatedByAccountId`.
  Table `musical_versions`.

Visibility contract (single decision, shared with later file/history/trash
slices): `CatalogueVisibility.IsMemberVisible(Song)` — a song is member-visible
iff `PublishedAt` is set. Deletion is not implemented in this slice; contract:
future deletion marks songs deleted (trash) and member reads exclude deleted
songs through this same decision point. Member API reads never expose drafts;
draft detail requests answer 404 for members, full data for Editor/Administrator.

API (ProblemDetails German, `no-store`, CSRF on mutations, camelCase):

- `GET /api/songs` → `{ songs: [...] }`. Members/Admin-less: published only
  (`{ id, title, composer, lyricist, published, publishedAt }`). Editors and
  administrators additionally receive `isDraft` view of unpublished songs.
- `GET /api/songs/{id}` → `{ song: { ..., arrangements: [{ id, label, arranger,
  musicalVersions: [{ id, label, creator }] }] } }`; member 404 on drafts.
- `POST /api/songs` (Editor/Administrator) `{ title, composer?, lyricist?,
  arrangementLabel?, versionLabel? }` → 201 `{ song }`; creates the song with
  exactly one arrangement and one labelled musical version (defaults
  „Standardfassung"/„Standardfassung"). Duplicate titles are allowed.
- `PATCH /api/songs/{id}` (Editor/Administrator) `{ title?, composer?,
  lyricist? }` → updates core data, records `UpdatedAt`/`UpdatedByAccountId`.
- `POST /api/songs/{id}/publish` (Editor/Administrator) → sets
  `PublishedAt`/`PublishedByAccountId`; re-publishing is a no-op success.
- `POST /api/songs/{id}/unpublish` (Editor/Administrator) → clears
  `PublishedAt`/`PublishedByAccountId`; unpublishing drafts is a no-op success.

Editor/Administrator enforcement mirrors the member-admin guard:
`ArchiveAccessService.GetDecisionAsync` + role check (Editor or Administrator),
401 „Anmeldung erforderlich." / 403 German message. Attribution uses the
authenticated account id and injected `TimeProvider`.

## Implementation progress — 2026-09-19 (branch `feat/arc-013-first-published-song`)

Backend (`f1e211c`, `e4e48c7`, `16b1951`):

- `Catalogue/Song.cs`, `Arrangement.cs`, `MusicalVersion.cs` with distinct
  Guid v7 IDs; `CatalogueModelConfiguration.cs` (snake_case tables, cascade
  FKs, max lengths, `RowVersion` concurrency like the auth slices, title
  index). `CatalogueVisibility.IsMemberVisible` is the single shared
  publication/deletion/reference decision point.
- `Catalogue/CatalogueEndpoints.cs` registered in `Program.cs`: member reads
  (`GET /api/songs`, `GET /api/songs/{id}`, 401 unauthenticated, 404 for
  drafts), editor writes (`POST`, `PATCH`, `publish`/`unpublish`) with
  Editor/Administrator enforcement, German ProblemDetails, manual CSRF, and
  `TimeProvider`/account-id attribution on create, edit and publication.
- Explicit migration `20260919125632_CatalogueSongs`; `WalkingSkeletonTests`
  now asserts it in the pending-migrations list.
- `tests/archive/backend/CatalogueApiTests.cs`: editor create (201 with one
  arrangement + one labelled version, default labels), member 403 on writes,
  draft hidden from member list and 404 on member detail, publish/unpublish
  with persisted actor/time and idempotent replay, PATCH attribution,
  unauthenticated 401, duplicate titles allowed, German validation 400s,
  unknown id 404.

Frontend (`1d10601`, `5397427`, `5461867`):

- `/lieder/` member catalogue (loading/sign-in/gate/content states; editors
  additionally see drafts marked „Entwurf" plus create form, inline core-data
  edit and publish/unpublish controls); `/lied/?id=…` detail page with
  arrangements, labelled musical versions, German 404 and editor badge.
  `/archiv` placeholder replaced by a CTA into the catalogue. Shared
  `LiedFormular`, `lib/songs.ts`, `patchAuth` in `lib/auth.ts`, new
  `lieder-*` CSS classes without new colors.

## Verification — 2026-09-19 (branch `feat/arc-013-first-published-song`)

- `dotnet build src/archive/Archive.slnx` 0 errors; `dotnet test
  tests/archive/backend` **137/137 green** (125 prior + 12 new catalogue
  tests).
- `dotnet test tests/archive/apphost` (Podman, fresh containers) **green**:
  the walking skeleton still holds and the `CatalogueSongs` migration is
  listed as pending before and absent after `archive-migrate` on real
  PostgreSQL.
- Frontend `pnpm run check` clean (Biome + route types + tsc); `pnpm run
  build` statically exports `/lied` and `/lieder`; mocked Playwright suite
  `tests/lieder.spec.ts` 12/12 green (6 tests × desktop/mobile): member list
  and detail, member draft 404, editor create→publish, ProblemDetails
  400/409 surfacing, editor detail controls, unauthenticated gate — every
  test asserts no page errors. Real-backend browser flows were not run (no
  dev backend during authoring); the apphost integration test covers the
  real API/database path.
