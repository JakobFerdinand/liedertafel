---
id: ARC-014
status: planned
phase: core
kind: slice
depends_on: ["ARC-013"]
touches: ["catalogue", "db-migrations"]
external_inputs: []
---

# ARC-014 — Choose between arrangements and transposed versions

**Depends on:** [ARC-013](ARC-013-first-published-song.md).

## Outcome

A member opens one song and clearly chooses between two arrangements and two
keys of an arrangement instead of encountering duplicate-looking song folders.

## Acceptance criteria

- [ ] Editors add/edit arrangements and musical versions under the existing IDs,
  with labels, arranger information, voice configuration and optional key.
- [ ] The member page groups arrangements and versions distinctly and supports
  direct links to the intended selection.
- [ ] Validate parent relationships and avoid silently moving a referenced
  version or treating a corrected PDF as another musical arrangement.
- [ ] Preserve explicit unknown metadata and contributor attribution; distinguish
  identical titles/keys using identity rather than a filename convention.

## Verification

Build the two-arrangement/two-key example, reload and follow direct links. Test
attempts to attach a version to the wrong parent and role/visibility boundaries.
File association is verified by ARC-015 using these same stable IDs.

## Handoff and parallel work

Expose a reusable arrangement/version picker for programmes and import review.
Coordinate catalogue detail/picker changes with ARC-015/020/021, while keeping
file transfer work independent of this UI extension.
