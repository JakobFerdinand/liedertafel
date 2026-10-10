---
id: ARC-013-1
status: done
phase: core
kind: slice
depends_on: ["ARC-013", "ARC-014"]
touches: ["catalogue", "events", "membership-admin", "db-migrations"]
external_inputs: []
---

# ARC-013-1 — Mark AI-derived fields and review proposals in one queue

**Depends on:** [ARC-013](ARC-013-first-published-song.md),
[ARC-014](ARC-014-arrangements-and-keys.md).

## Outcome

An editor sees which catalogue fields were written by AI, reverts one with a
click, and works through everything that needs confirmation in a single
"Vorschläge" queue.

## Acceptance criteria

- [x] Persist per-field provenance in one generic `FieldProvenance` table for
  song, arrangement, musical-version, asset and event fields: entity type and
  id, field, source (human / regex / AI), confidence (`sicher` / `unsicher`),
  model, prompt version, previous value, time, actor. Add it through an
  explicit EF migration.
- [x] Move the song, arrangement, version and event mutations that AI features
  touch out of the endpoint lambdas into a shared write service used by
  endpoints, proposal handlers and jobs, so validation and audit fields cannot
  drift.
- [x] Song, arrangement and version PATCHes carry a row version and return 409
  on a stale edit; arrangement and musical version gain a row version.
- [x] A human edit sets the field's source to human and locks it: later AI or
  regex runs never overwrite it. Reverting restores the previous value and
  locks the field the same way.
- [x] Offer one write path for automated writers that applies a value only when
  the field is unlocked and the confidence is above a configured threshold;
  otherwise it creates a proposal.
- [x] Persist proposals in one `Proposal` table (kind, target record, JSON
  payload, reason, confidence, source document, row version of the target at
  proposal time) with a typed handler per kind, accept / reject and editor
  attribution. Accepting applies the change through the shared write service.
- [x] If the target changed since the proposal was made, accepting shows the
  current value beside the proposed one for a fresh decision instead of
  applying it.
- [x] Show a "KI" badge with revert on auto-applied fields in the existing
  forms, and a "Vorschläge" page in `/verwaltung` listing open proposals by
  kind. Both are ordinary React over stored state, not agent-rendered, so they
  work while AI is paused. Members never see the badge or proposals.
- [x] Changes to identity or visibility (new song, merge, publish, delete,
  member administration) can only ever be proposals, enforced server-side.

## Verification

Write a field through the automated path above and below the threshold, edit
it by hand, rerun the automated write and confirm the human value stays.
Accept and reject a proposal as an editor; confirm a member cannot reach the
queue or the provenance.

## Implementation and verification record (2026-10-10)

The journey: an automated reading run writes a catalogue field above the
confidence threshold, the field shows a "KI" (or "Auswertung") badge in the
editor form with a one-click revert; a below-threshold write (or an attempt
against a locked field) lands in the open "Vorschläge" queue in
`/verwaltung/vorschlaege/`, where the editor accepts (value applied through
the shared write service) or rejects it; a stale target answers 409 with the
current value beside the proposed one and an "Auf den aktuellen Stand
bringen" refresh. Members never see the badges, the queue or the provenance
field (member song detail omits it entirely; queue, accept, reject, refresh
and revert answer 403, anonymous 401). Automated runs have no path to
identity or visibility: unknown/identity fields are refused server-side
(they are not in `FieldCatalog`), so publication, deletion, merger and
member administration can only ever arrive as proposals.

Structure (backend `Provenance/`):

- `ProvenanceModel.cs`: `FieldProvenance` (latest row per (entity type, id,
  field), source human/regex/AI, confidence sicher/unsicher, model, prompt
  version, previous value, time, actor, locked) and `Proposal` (kind,
  target, payload, reason, confidence, source, model, prompt version,
  source description, target row version, status, attribution) plus
  `FieldCatalog` — the closed field vocabulary with German display names
  and the newline-joined list encoding.
- `WriteService.cs` (`CatalogueWriteService`): the one shared write path —
  song/arrangement/musical-version/event/asset-field patches (also moved
  song creation, arrangement and version creation, publish/unpublish of
  song and event), validation, attribution, row-version bumps, per-field
  provenance and the stale mapping in one save. The endpoint lambdas keep
  antiforgery, role checks and response building only.
- `AutomatedFieldWriter.cs`: THE automated write path (jobs, proposal
  handlers, diagnostics): unchanged value → no-op; locked field or value
  below `Archive:Provenance:AutoApplyConfidence` (`sicher` default,
  `unsicher`, `never`) → open proposal with the target's row version;
  otherwise applies through the write service and leaves KI badge state.
- `Proposals.cs`: `IProposalHandler` + typed handlers
  (`FieldSuggestionHandler`, `SongCreationHandler`,
  `SongPublicationHandler`) and `ProposalDecisions` (accept with the
  freshness gate, reject, refresh); kinds without a handler in this slice
  (deletion, merge, member administration) are refused at acceptance.
- `ProvenanceEndpoints.cs`: editor-only queue list
  (`GET /api/proposals`), `POST /api/proposals/{id}/accept|reject|refresh`
  (accept staleness answers ProblemDetails extensions `currentValue`,
  `proposedValue`, `currentRowVersion`), revert at
  `POST /api/provenance/revert`, and the Development-only
  `POST /api/dev/provenance-write` diagnostic driving the real writer.
- Song detail responses carry `rowVersion` on song, arrangement and version
  and, for editors only, a flat `provenance` list; members get neither.

Conventions: proposal-applied values keep their automated provenance
(source regex/AI) with the confirming editor as actor — the badge stays
revertible. A revert (and every human edit) writes human provenance and
locks the field. No-op repeated acceptance answers 409 with the decision
recorded on the row.

Migration `20261010174016_FieldProvenanceAndProposals`: tables
`field_provenance` (unique per (entity type, id, field)) and `proposals`
(kind/status check constraints, status+created index) plus
`arrangements.RowVersion` / `musical_versions.RowVersion` (bigint con-
currency columns defaulting to 0). No pushed migration was touched.

Verification commands and results (all green):

- `dotnet build src/archive/Archive.slnx` — succeeded.
- `dotnet test tests/archive/backend` — 595 passed, 0 failed (re-run after the slice)
  (incl. 12 new `ProvenanceProposalTests`: apply above threshold with
  provenance, below-threshold proposal + accept flow, human lock survives
  reruns, revert restores and locks, stale song PATCH 409 without change,
  stale arrangement/version PATCH 409 with German message, song-creation
  proposal applied through the shared service, stale proposal accept shows
  current beside proposed then applies after refresh and "Auf den aktuellen
  Stand bringen", members 403/anonymous 401 plus no provenance in member
  detail, automated writer refuses the identity field `published` server
  side, threshold `never`, repeated accept 409 with stored decision)
  and 4 gated `ProvenancePostgresLiveTests` on throwaway PostgreSQL 17.6 —
  real migrations, real constraint violations mapped by constraint name
  (`IX_field_provenance_target`, `CK_proposals_kind`), automated
  apply/proposal/human-lock and proposal accept incl. stale gate and lost
  row-version race against real Npgsql SQL.
- `dotnet ef migrations has-pending-model-changes --project backend`
  (placeholder connection string) — no pending model changes.
- `corepack pnpm run check`, `corepack pnpm run build` (frontend) — 98
  files checked, static export includes `/verwaltung/vorschlaege/`.
- Playwright, static export, `--workers=1`: new
  `tests/vorschlaege.spec.ts` (badge + revert journey, member isolation,
  queue per kind, stale gate showing both values + refresh) — 4 tests
  green on desktop and mobile projects; touched specs `noten`, `lieder`,
  `materialien`, `mitglieder`, `vorschlaege` — 86 passed, 2 skipped
  (the pre-existing invitation test needs a development backend + Mailpit).
- `dotnet test tests/archive/apphost --filter
  "FullyQualifiedName~WalkingSkeleton"` — 5 passed against the real
  PostgreSQL/Azurite stack with the new migration applied.

Handoff and parallel work

ARC-034-1, ARC-036, ARC-025-1, ARC-046, ARC-047 and ARC-031 write through this
path. It needs no model access and can be built beside ARC-021-1. Decisions:
[architecture §14](architecture.md#14-ai-assistance). Additional notes for
those slices:

- Write all field touches through `AutomatedFieldWriter.WriteFieldAsync`
  (endpoints keep the write service for human edits). The confidence is the
  two-step `sicher`/`unsicher`; the auto-apply threshold lives in
  `Archive:Provenance:AutoApplyConfidence`.
- New songs, publication, deletion, merger and member administration must
  create `Proposal` rows (kinds already exist; deletion/merge/admin handlers
  are not registered yet — register them with your slice and accepting will
  pick them up).
- Provenance rows are per (entity type, id, field) with the latest write
  only; list fields (alternate titles, tags) store newline-joined strings.
- The KI badge vocabulary lives in `components/ki-abzeichen.tsx`; the queue
  labels in `components/vorschlaege-queue.tsx` (German field names map).
- The asset field support (description, voice label) rides the same
  service primitives (`PatchAssetAsync`); the asset endpoint is not yet
  moved into it — ARC-034-1 should change that while wiring its scan run.
