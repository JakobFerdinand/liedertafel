---
id: ARC-013-2
status: planned
phase: follow-up
kind: slice
depends_on: ["ARC-013-1", "ARC-021-1"]
touches: ["catalogue", "db-migrations"]
external_inputs: ["ai-runtime-credentials"]
---

# ARC-013-2 — Show a German translation of non-German lyrics

**Depends on:** [ARC-013-1](ARC-013-1-field-provenance-and-proposals.md),
[ARC-021-1](ARC-021-1-luna-switch-and-budget-cap.md).

## Outcome

A member opens a song with Latin or English lyrics and reads a German
translation beside them, clearly marked as a translation.

## Acceptance criteria

- [ ] For songs whose language is not German and that have lyrics, generate a
  German translation once and store it; regenerate only when the lyrics change.
- [ ] The translation is a draft until an editor approves it; members see only
  approved translations, labelled as a translation and not as original text.
- [ ] Nothing is generated on page view. At the budget cap the work stays
  queued.
- [ ] An editor can edit or remove the translation; a human edit locks it.

## Verification

Add a Latin and an English song, approve one translation and edit the other,
change the lyrics and confirm only the unlocked one is regenerated as a draft.

## Handoff and parallel work

Post-launch, independent of the other AI slices. Decisions:
[architecture §14](architecture.md#14-ai-assistance).
