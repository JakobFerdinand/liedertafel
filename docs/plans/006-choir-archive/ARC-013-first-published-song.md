---
id: ARC-013
status: in_progress
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

- [ ] Implement creation/editing, persistence, draft/published visibility and a
  member catalogue/detail route through the real API and database.
- [ ] Give Song, Arrangement and MusicalVersion distinct stable IDs from the
  start; a single initial arrangement/version is enough for this slice.
- [ ] Capture title, optional creator information and clear arrangement/version
  labels; allow explicitly missing optional information.
- [ ] Record actor/time for edits, enforce Editor/Administrator writes, and deny
  draft reads through member API requests as well as navigation.
- [ ] Define publication/deletion/reference contracts so later files, history and
  trash share one visibility decision rather than reproducing it inconsistently.

## Verification

Create and publish with an Editor, browse as a Member, then verify direct draft
access and member writes fail. Reload from the database and test duplicate titles
without incorrectly treating a title as a unique song identifier.

## Handoff and parallel work

Publish stable catalogue IDs, visibility checks, detail-page extension points and
edit attribution. ARC-014/015/020 can branch from this slice; events ARC-022 can
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
