---
id: ARC-031
status: planned
phase: core
kind: slice
depends_on: ["ARC-015"]
touches: ["assets", "catalogue-materials", "db-migrations"]
external_inputs: []
---

# ARC-031 — Correct a score while retaining its previous revision

**Depends on:** [ARC-015](ARC-015-private-score.md).

## Outcome

An editor replaces a score, members receive the current PDF, and an editor can
inspect or make an earlier retained revision current again.

## Acceptance criteria

- [ ] Upload a new immutable file revision under the same logical asset and
  atomically update its current pointer after successful validation.
- [ ] Preserve revision metadata/attribution and editor-only history; retries and
  competing replacements cannot overwrite another file or lose a revision.
- [ ] Members and historical musical-version links resolve current materials;
  a previous revision is not a new arrangement or transposition.
- [ ] Selecting an earlier revision as current preserves history and triggers
  the same revision-change contract used by search/extraction consumers.
- [ ] Retain score revisions until explicitly removed, independently of the
  seven-day trash policy; enforce role checks on old-revision file tickets.

## Verification

Replace a PDF, follow an existing member link, inspect history as editor, restore
the prior current pointer and test concurrent replacements/unauthorized revision
reads. Recheck identity and attribution after reload.

## Handoff and parallel work

ARC-033 integrates current-revision text visibility. Coordinate upload/revision
contracts with ARC-017/032; player implementation need not wait for this history UI.
