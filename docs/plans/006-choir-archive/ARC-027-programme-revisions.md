---
id: ARC-027
status: done
phase: core
kind: slice
depends_on: ["ARC-026"]
touches: ["programmes", "db-migrations"]
external_inputs: []
---

# ARC-027 — Revise a programme without exposing unfinished edits

**Depends on:** [ARC-026](ARC-026-publish-programme.md).

## Outcome

An editor changes an already-published programme while members keep seeing the
last published version, then explicitly publishes the replacement.

## Acceptance criteria

- [x] Start a working revision from the current published programme and edit its
  order, entries and member notes independently of the published snapshot.
- [x] Show clear working/published states, publication timestamp and unsaved/stale
  edit feedback; publishing switches the complete member-visible view atomically.
- [x] Handle two editors without silently overwriting the other's accepted edit
  or publishing against an outdated base revision.
- [x] Preserve item identity where appropriate and retain the distinction from
  actual performance records. Linked scores remain current musical-version files.

## Verification

Use editor/member browsers simultaneously: partially edit, reload the member
view, publish, and observe only the complete new programme. Exercise competing
edits/publications and an event that already has historical evidence.

## Handoff and parallel work

Coordinate programme identity/locking with ARC-029; historical entry and media
playback can progress independently. ARC-040 later adds deletion/recovery to both
working and published programme state.

## Frozen contract (2026-09-25)

Builds on ARC-026 (working/published revision identities, stable item IDs,
German ProblemDetails, `no-store`, CSRF on mutations, the rowVersion conflict
409) and keeps its schema untouched — no migration, no new columns.

- **Frozen history embed (editors only).** `GET /api/events/{id}` embeds
  `programme.history` for editors: every frozen published revision EXCEPT the
  newest one, oldest first, each entry in the published-revision shape
  (`id`, `number`, `publishedAt`, `items` — the same item shape as the current
  embeds). The newest publication rides in `published`, the working draft in
  `working`, so history never duplicates either; the draft never appears in
  history. Members receive `history` null — their view stays the newest
  publication alone, and older revisions are never member-visible. With no
  programme saved at all the embed is null for both roles; before the first
  publication members read null (including `history`), editors read the
  working draft with `history` as an empty array — unchanged from ARC-026.
- **Stale-base publish guard.** A working draft whose `CreatedAt` predates the
  newest publication's stamp answers 409 with the existing
  "Der Programmentwurf wurde zwischenzeitlich geändert." — the guard, not the
  rowVersion, is the differentiator, and nothing is stamped. It is structurally
  unreachable through the API: a draft is always created after the last
  publication (implicit creation on the first post-publication PUT) and the PUT
  clamps the fresh draft's `CreatedAt` to `max(now, newest published stamp)` so
  a backwards clock step cannot freeze a stale base into the draft. Pure
  defence in depth for future draft-writing paths; the honest client reloads
  the fresh state and redoes its edit either way (the guard shares the
  rowVersion conflict's contract language). The structurally adjacent race —
  the second editor's publish after the first editor's publication consumed
  the draft — answers 409 "Das Programm wurde bereits veröffentlicht."
  instead; the 409 status and the reload-and-redo contract are identical.
- **Two-editor contract.** The working draft is shared, never owned: any
  editor edits the SAME draft revision in place (the embed's `working.id` is
  stable across editors). `rowVersion` on the programme aggregate guards both
  PUT and publish — a mismatch answers 409 "Der Programmentwurf wurde
  zwischenzeitlich geändert." before any validation or write, and the client
  reloads fresh state and redoes its change (surviving entries keep their
  stable item IDs across editors). There is deliberately NO takeover/reset
  endpoint: an explicit foreign-draft wipe would be a silent overwrite by
  another name. Last accepted edit wins; the loser loses the race, not their
  view — the 409 sends them to the fresh state.
- **Item identity.** Stable within ONE working revision across saves and
  editors: entries matched by item ID update in place (full ordered
  replacement), the two-phase renumber keeps positions 1..n contiguous.
  Published revisions freeze their item rows forever — the ARC-029 planned-
  item anchor (confirmations must reference exactly the frozen rows). Crossing
  a publication boundary creates a fresh draft whose entries carry fresh item
  IDs by contract (the client seeds the workbench from the published list with
  server ids dropped); repeated songs stay distinct entries throughout.
- **Display fields stay read-time.** Embedded `songTitle`, `arrangementLabel`,
  `voiceConfiguration`, `musicalVersionLabel`, `musicalKey` resolve on every
  read from the referenced chain — a later catalogue relabel (PATCH
  `/api/musical-versions/{id}`) reaches every already-published view, including
  the frozen history entries, while `songId`/`arrangementId`/
  `musicalVersionId` (the member deep links into the song page) stay the
  frozen columns. Published deep links therefore always resolve to the
  current musical-version files, never frozen copies, and the programme
  aggregate is untouched by such catalogue edits.
- **No database migration.** The ARC-026 revision model already carries
  everything (revisions with `CreatedAt`, publication stamps, frozen items,
  the untouched event row); `touches: db-migrations` is vacuous for this issue
  — the pending-migrations assertion is unchanged.

## Implementation and verification (2026-09-25)

Backend (`b9ba9e3`, slice 1; `ProgrammeApiTests.cs` journey tests in this
slice):

- `ProgrammeRevisions.cs` (new decision class): the shared guard decision —
  `EarliestAcceptedDraftMoment(programme)` returns the newest publication's
  `PublishedAt` (the aggregate's `CreatedAt` before the first publication), the
  earliest instant a working draft may publish.
- `ProgrammeEndpoints.cs`: the editor embed gains `history` (editors only,
  every frozen published revision except the newest, oldest first, members
  null); the publish handler refuses a working draft whose `CreatedAt`
  predates the guard with the existing German concurrency title; fresh
  post-publication drafts clamp `CreatedAt` against a backwards clock step;
  doc comments name all three behaviours.
- `tests/archive/backend/ProgrammeApiTests.cs`: slice-1 tests
  `RevisionHistorySurfacesFrozenRevisionsToEditorsOnly` (frozen revisions ride
  the editor embed oldest first, members read null) and
  `PublishingStaleBaseDraftIsRefused` (the guard, not the token, refuses the
  backdated draft; restoring the instant publishes with the same token); the
  slice-3 journey tests `TwoEditorsCannotSilentlyOverwriteEachOthersAcceptedEdit`
  (stale PUT anchor 409 with A's accepted draft intact in the DB, redo keeps
  two of A's item ids plus one fresh entry, positions 1..n),
  `SecondEditorPublishingStaleBaseIsRefused` (stale pre-publish anchor refused
  with the concurrency title, exactly one new published revision, revision 1
  frozen, no double stamp, then reload → copy from the published list without
  ids → revision 3, superseded rows byte-identical in the DB),
  `MemberViewStaysOnLastPublishedRevisionWhileDraftEvolves` (after each of two
  draft saves the member detail and the member `/api/programmes` list stay on
  revision 1 byte-identically; publishing revision 2 switches the view
  atomically with an advanced stamp and no performed fields; the event row's
  `RowVersion`/`PublishedAt`/`UpdatedAt` stay unchanged by both publishes) and
  `PublishedProgrammeLinksStayCurrentMusicalVersions` (a catalogue relabel of
  the published musical version keeps every deep-link id frozen while the
  label re-resolves read-time for the member view and the frozen history
  alike).

Frontend (`3bd4630`):

- `lib/events.ts`: `history?: ProgrammRevisionVeroeffentlicht[] | null` on
  `ProgrammEmbed` (editors receive the frozen revisions, members null; older
  answers without the field stay valid).
- `components/auftritt-programm.tsx`: the workbench reads its stand lines —
  `Entwurf: Revision ${number} · Letzte Änderung: ${date}` and
  `Mitgliedersicht: Revision ${number} · veröffentlicht am ${date}` (honest
  "Entwurf: noch keiner gespeichert." / "Mitgliedersicht: noch nichts
  veröffentlicht." before their state exists) — plus the `Frühere Fassungen`
  block listing the frozen revisions (`Revision ${number} · veröffentlicht am
  ${date}`) and the `Nicht gespeicherte Änderungen.` indicator driven by the
  unsaved-work check. A fresher outside state replacing unsaved workbench rows
  sets the existing 409 copy ("Der Programmentwurf wurde zwischenzeitlich
  geändert. Bitte prüfe den aktuellen Stand und wiederhole deine Änderung.")
  before the resync discards them.
- `app/globals.css`: the ARC-027 Werkbank-Stand block (stand lines, the
  dashed-rule `Frühere Fassungen` block, the unsaved indicator) with existing
  tokens only.
- `tests/programm.spec.ts`: +3 tests — "Redaktion sieht Revisionsstand und
  frühere Fassungen an der Werkbank", "Ungespeicherte Arbeit meldet sich und
  legt sich nach dem Speichern still" and "Frischer Stand ersetzt unerledigte
  Arbeit und erklärt es" — over the honest history fixture (published
  revision 2, empty draft revision 3, frozen revision 1 as the only earlier
  Fassung).

Verification evidence:

- `dotnet build src/archive/Archive.slnx` clean — 0 warnings, 0 errors.
- `dotnet test tests/archive/backend --filter
  FullyQualifiedName~ProgrammeApiTests` **16 passed** (12 prior + the 4 new
  journey tests).
- `dotnet test tests/archive/backend` **338 passed** (334 prior + the 4 new;
  the two previously documented order-dependent `MemberEmailChangeTests`
  repair flakes passed green in this full run).
- Frontend `pnpm run check` clean (Biome + route types + tsc) and the
  route-mocked `pnpm exec playwright test tests/programm.spec.ts` passed
  **28/28** (14 tests × desktop + mobile) against the freshly built static
  export (the pre-existing server on port 3000 still serves the older
  pre-ARC-027 export, so the run pinned `ARCHIVE_BASE_URL` to the fresh one).
- Apphost integration and the production-image smoke check run in CI.
