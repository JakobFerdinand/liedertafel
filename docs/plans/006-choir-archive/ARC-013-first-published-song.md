---
id: ARC-013
status: planned
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
